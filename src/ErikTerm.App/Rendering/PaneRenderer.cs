using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Core;
using ErikTerm.Core.Images;
using ErikTerm.Ui;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Draws one pane's grid (backgrounds, selection, glyphs, decorations, cursor, scrollbar, bell flash).
/// Each row's backgrounds and glyphs are built once and replayed from the pane's <see cref="RowCache"/> until the
/// row changes; selection, search highlights, the cursor and link decorations are overlays drawn on top.
/// </summary>
internal sealed class PaneRenderer
{
    private readonly ColorTransformCache _dim = new();
    private readonly ContrastCache _contrast = new(); // minimum_contrast (Contrast.cs)
    private readonly GridText _text = new();
    private readonly ImageRenderer _images = new();

    /// <summary>Search highlights, drawn between the cell backgrounds and the glyphs.</summary>
    public SearchBar? Search;
    public bool CursorAnimation = true;
    public int CursorBlinkRate = 800;
    public bool CursorBlinkPhase;
    /// <summary>cursor_thickness: window points (× Scale) or a fraction of the cell; both 0 = automatic.</summary>
    public float CursorThicknessPoints, CursorThicknessCells, Scale = 1;
    /// <summary>cursor_height: a bar's height in multiples of the font size; 0 fills the cell.</summary>
    public float CursorHeightEm = CursorMotion.DefaultBarHeightEm;
    public bool SmoothScrolling = true;

    /// <summary>When a kitty animation drawn in the last frame changes frame next (long.MaxValue: none).</summary>
    public long NextAnimationAt => _images.NextAnimationAt;

    /// <summary>Frame statistics: rows built vs. replayed since the last reset.</summary>
    public long RowsBuilt, RowsReused;

    // Per-draw scratch: which displayed rows were rebuilt in pass 1 (pass 2 rebuilds the same ones).
    private bool[] _fresh = new bool[128];

    public void Draw(DrawList dl, Pane pane, RectF rect, Theme theme, bool focused, bool windowFocused, long now)
    {
        var t = pane.Term;
        var fonts = dl.Fonts;
        var pal = t.Palette;
        int cw = fonts.CellWidth, ch = fonts.CellHeight;

        // Inactive panes fade toward inactive_pane_hsb as focus animates away.
        float inactive = 1 - pane.Focus.Value(now);
        var hsb = theme.InactiveHsb;
        float dimH = 1 + (hsb.H - 1) * inactive, dimS = 1 + (hsb.S - 1) * inactive, dimB = 1 + (hsb.B - 1) * inactive;
        _dim.Configure(dimH, dimS, dimB);
        _contrast.Configure(theme.MinimumContrast); // part of the row cache's key through the theme
        float appear = pane.Appear.Value(now);

        dl.PushClip(rect);
        // OSC 11: the program changed this pane's background, which the window clear (the scheme's) doesn't show.
        // erikterm's own pages (the settings page) ask for it as a card with rounded corners, as see-through as the window.
        if (pal.HasBackgroundOverride)
        {
            if (IsCard(t)) dl.RoundRect(rect, CardRadius(cw, ch), _dim.Apply(pal.Background), theme.WindowOpacity * appear);
            else dl.Rect(rect, _dim.Apply(pal.Background), theme.WindowOpacity * appear);
        }
        float ox = MathF.Round(rect.X);
        int rows = t.Rows, cols = t.Columns;

        // Smooth scrolling: a visual offset in rows; while it's non-zero one more row shows above or below.
        float scroll = pane.Scroll.Update(SmoothScrolling, now);
        float oy = MathF.Round(rect.Y + scroll * ch);
        int yFirst = scroll > 0 ? -(int)MathF.Ceiling(scroll) : 0;
        int yEnd = scroll < 0 ? rows + (int)MathF.Ceiling(-scroll) : rows;
        long viewTop = t.ViewTopLine;
        // Row the cursor is drawn on (it stays visible while scrolled back): ligatures split there.
        int cursorRow = t.CursorVisible ? t.CursorY + t.ViewOffset : -1;
        CursorRenderer.Track(pane, CursorAnimation, now);

        var cache = pane.RowCache;
        cache.Begin(new RowCache.Config(fonts, fonts.GridPx, cw, ch, dl.Atlas, dl.Atlas.Generation, pal, pal.Version, theme,
            dimH, dimS, dimB, cols));
        if (_fresh.Length < yEnd - yFirst) _fresh = new bool[(yEnd - yFirst) * 2];
        var grid = new GridPass(t, cache, pal, ox, oy, yFirst, yEnd, viewTop, cursorRow, cols, cw, ch, appear);

        DrawBackgroundPass(dl, in grid, theme);

        DrawSelection(dl, pane, in grid, theme);
        Search?.DrawHighlights(dl, pane, ox, oy, theme, appear);
        var frame = new PaneFrame(dl, pane, ox, oy, _dim, _text, appear, now);
        _images.Draw(in frame, below: true); // kitty z < 0 (ImageRenderer.cs)

        uint emojiTint = _dim.Apply(0xFFFFFF); // color glyphs aren't tinted, but inactive panes still dim them
        DrawForegroundPass(dl, in grid, theme, emojiTint);
        cache.End();
        DrawSelectedText(dl, pane, in grid, theme, emojiTint);

        _images.Draw(in frame, below: false); // images over the text (ImageRenderer.cs)

        // In vim mode the program's cursor steps back (drawn as when unfocused) for vim's own (VimCursorRenderer.cs).
        CursorRenderer.Draw(in frame, focused && pane.Vim is null, windowFocused, new CursorRenderer.Look(CursorBlinkRate, theme.HarfbuzzFeatures,
            theme.CursorBorder, CursorBlinkPhase, CursorThicknessPoints, CursorThicknessCells, Scale, CursorHeightEm));
        if (pane.Vim is not null) VimCursorRenderer.Draw(in frame, theme, focused && windowFocused);

        ScrollbarRenderer.Draw(dl, pane, rect, theme, now, Search);

        float bell = pane.Bell.Value(now);
        if (bell > 0) dl.Rect(rect, theme.Foreground, 0.10f * bell);

        dl.PopClip();
        ShellDecorations.Draw(dl, pane, rect, theme, now, oy - MathF.Round(rect.Y)); // link underline, prompt marks (ShellDecorations.cs)
    }

    /// <summary>The user variable a program sets (to 1) to have its OSC 11 background drawn as a rounded card.</summary>
    public const string CardVariable = "erikterm_card";

    private static bool IsCard(Terminal t) => t.UserVars.TryGetValue(CardVariable, out var v) && v == "1";

    /// <summary>Rounded about as much as the palette and menus, for any font size.</summary>
    private static float CardRadius(int cw, int ch) => MathF.Round(Math.Min(cw, ch) * 0.8f);

    /// <summary>What both grid passes of a frame draw with: YFirst and YEnd bound the displayed rows.</summary>
    private readonly record struct GridPass(Terminal Term, RowCache Cache, Palette Pal, float Ox, float Oy, int YFirst, int YEnd, long ViewTop, int CursorRow,
        int Cols, int Cw, int Ch, float Appear);

    /// <summary>Pass 1: cell backgrounds, built for changed rows and replayed from the cache for the rest.</summary>
    private void DrawBackgroundPass(DrawList dl, in GridPass grid, Theme theme)
    {
        var t = grid.Term;
        for (int y = grid.YFirst; y < grid.YEnd; y++)
        {
            var row = t.GetLine(grid.ViewTop + y);
            if (row is null) continue;
            float py = grid.Oy + y * grid.Ch;
            var e = grid.Cache.Get(row, y == grid.CursorRow ? t.CursorX : -1, grid.Cols, out bool fresh);
            _fresh[y - grid.YFirst] = fresh;
            if (!fresh)
            {
                dl.AppendQuads(e.Bg.AsSpan(0, e.BgQuads * 4), grid.Ox - e.BuiltX, py - e.BuiltY, grid.Appear);
                RowsReused++;
                continue;
            }
            RowsBuilt++;
            int start = dl.QuadCount;
            BuildBackground(dl, in grid, row, py, theme);
            e.BgQuads = dl.CopyQuads(start, dl.QuadCount, ref e.Bg);
            e.BuiltX = grid.Ox;
            e.BuiltY = py;
            dl.ScaleAlpha(start, dl.QuadCount, grid.Appear);
        }
    }

    /// <summary>Pass 2: glyphs and decorations of the rows pass 1 marked fresh; the others replay from the cache.</summary>
    private void DrawForegroundPass(DrawList dl, in GridPass grid, Theme theme, uint emojiTint)
    {
        var t = grid.Term;
        for (int y = grid.YFirst; y < grid.YEnd; y++)
        {
            var row = t.GetLine(grid.ViewTop + y);
            if (row is null) continue;
            float py = grid.Oy + y * grid.Ch;
            var e = grid.Cache.Get(row, y == grid.CursorRow ? t.CursorX : -1, grid.Cols, out _);
            if (!_fresh[y - grid.YFirst])
            {
                dl.AppendQuads(e.Fg.AsSpan(0, e.FgQuads * 4), grid.Ox - e.BuiltX, py - e.BuiltY, grid.Appear);
                continue;
            }
            int start = dl.QuadCount;
            BuildForeground(dl, in grid, row, y == grid.CursorRow ? t.CursorX : -1, py, theme, emojiTint);
            e.FgQuads = dl.CopyQuads(start, dl.QuadCount, ref e.Fg);
            dl.ScaleAlpha(start, dl.QuadCount, grid.Appear);
        }
    }

    private static uint BackgroundOf(in Cell c, Palette pal) =>
        (c.Flags & CellAttributes.Inverse) != 0 ? pal.ResolveForeground(c.Fg, (c.Flags & CellAttributes.Bold) != 0) : pal.ResolveBackground(c.Bg);

    private void BuildBackground(DrawList dl, in GridPass grid, Row row, float py, Theme theme)
    {
        int cols = grid.Cols, cw = grid.Cw, ch = grid.Ch;
        float ox = grid.Ox;
        var pal = grid.Pal;
        var cells = row.Cells;
        int n = Math.Min(cells.Length, cols);
        uint defaultBg = pal.Background;
        for (int x = 0; x < n; x++)
        {
            ref readonly var c = ref cells[x];
            if ((c.Flags & CellAttributes.WideTrail) != 0) continue;
            uint bg = BackgroundOf(in c, pal);
            if (bg == defaultBg) continue;
            int end = BackgroundRunEnd(cells, x, n, bg, pal);
            float px = ox + x * cw;
            dl.Rect(px, py, ox + end * cw, py + ch, _dim.Apply(bg), theme.TextBackgroundOpacity);
            #pragma warning disable S127 // the run just drawn is skipped: moving the counter is how this per-cell, per-frame loop merges cells into one quad
            x = end - 1;
            #pragma warning restore S127
        }
    }

    /// <summary>Merges a run of cells with the same background into one quad: the cell after the run starting at <paramref name="x"/>.</summary>
    private static int BackgroundRunEnd(Cell[] cells, int x, int n, uint bg, Palette pal)
    {
        int end = x + ((cells[x].Flags & CellAttributes.WideLead) != 0 ? 2 : 1);
        while (end < n)
        {
            ref readonly var d = ref cells[end];
            var f = d.Flags;
            if ((f & CellAttributes.WideTrail) != 0) { end++; continue; }
            if (BackgroundOf(in d, pal) != bg) break;
            end += (f & CellAttributes.WideLead) != 0 ? 2 : 1;
        }
        return end;
    }

    private void BuildForeground(DrawList dl, in GridPass grid, Row row, int cursorCol, float py, Theme theme, uint emojiTint)
    {
        var pal = grid.Pal;
        float ox = grid.Ox;
        var cells = row.Cells;
        int n = Math.Min(cells.Length, grid.Cols);
        _text.BeginRow(dl, theme.HarfbuzzFeatures, row, n, cursorCol);
        for (int x = 0; x < n; x++)
        {
            ref readonly var c = ref cells[x];
            var flags = c.Flags;
            if ((flags & CellAttributes.WideTrail) != 0) continue;
            bool hasGlyph = c.Rune > 0x20 && c.Rune != KittyPlaceholder.Codepoint; // placeholders are drawn as images
            bool decorated = (flags & DecorationFlags) != 0;
            if (!hasGlyph && !decorated) continue;
            if ((flags & CellAttributes.Hidden) != 0) continue;

            uint fg = ForegroundOf(in c, pal);
            if (hasGlyph) _text.SetColor(x, fg); // shaped and drawn per row by EndRow
            if (decorated) DrawCellDecorations(dl, in grid, row, x, flags, py, fg);
        }
        _text.EndRow(dl, ox, py, 1, emojiTint);
    }

    private const CellAttributes DecorationFlags = CellAttributes.Underline | CellAttributes.Strike | CellAttributes.Overline;

    private uint ForegroundOf(in Cell c, Palette pal)
    {
        var flags = c.Flags;
        uint fg = pal.ResolveForeground(c.Fg, (flags & CellAttributes.Bold) != 0);
        uint bg = pal.ResolveBackground(c.Bg);
        if ((flags & CellAttributes.Inverse) != 0) (fg, bg) = (bg, fg);
        if ((flags & CellAttributes.Dim) != 0) fg = Colors.Mix(fg, bg, 0.5f);
        // minimum_contrast against the cell's own background (faint text too), before inactive-pane dimming;
        // graphics characters keep their colors (Contrast.cs).
        if (_contrast.Enabled && !Contrast.IsGraphic(c.Rune)) fg = _contrast.Apply(fg, bg);
        return _dim.Apply(fg);
    }

    /// <summary>The underline, strikethrough and overline of cell <paramref name="x"/> of a row whose top is at <paramref name="py"/>.</summary>
    private void DrawCellDecorations(DrawList dl, in GridPass grid, Row row, int x, CellAttributes flags, float py, uint fg)
    {
        float px = grid.Ox + x * grid.Cw;
        int span = (flags & CellAttributes.WideLead) != 0 ? 2 : 1;
        if ((flags & CellAttributes.Underline) != 0)
        {
            // SGR 58: the underline's own color, dimmed like the text in an inactive pane.
            uint ul = row.HasUnderlineColors ? row.UnderlineColorAt(x) : 0;
            uint uc = ul == 0 ? fg : _dim.Apply(grid.Pal.ResolveForeground(ul, bold: false));
            DrawUnderline(dl, CellStyle.Underline(flags), px, py, span, uc, 1);
        }
        DrawStrikeAndOverline(dl, flags, px, py, span, fg, 1);
    }

    /// <summary>Strikethrough and overline of <paramref name="span"/> cells at (px, py), in the draw list's grid font.</summary>
    private static void DrawStrikeAndOverline(DrawList dl, CellAttributes flags, float px, float py, int span, uint fg, float a)
    {
        var fonts = dl.Fonts;
        int cw = fonts.CellWidth, ch = fonts.CellHeight, thickness = fonts.LineThickness;
        if ((flags & CellAttributes.Strike) != 0) dl.Rect(px, py + ch / 2, px + cw * span, py + ch / 2 + thickness, fg, a);
        if ((flags & CellAttributes.Overline) != 0) dl.Rect(px, py, px + cw * span, py + thickness, fg, a);
    }

    /// <summary>The selection, over the backgrounds (it used to replace them; it's opaque, so the result is the same).</summary>
    private void DrawSelection(DrawList dl, Pane pane, in GridPass grid, Theme theme)
    {
        var t = pane.Term;
        if (pane.Selection?.Range(t) is not var (sl, sc, el, ec)) return;
        var range = new SelectedRange(sl, sc, el, ec, pane.Selection.Mode == SelectionMode.Block);
        float ox = grid.Ox, oy = grid.Oy, appear = grid.Appear;
        int cw = grid.Cw, ch = grid.Ch;
        long viewTop = t.ViewTopLine;
        uint color = _dim.Apply(theme.Selection);
        for (int y = grid.YFirst; y < grid.YEnd; y++)
        {
            if (!SelectedSpan(t, viewTop + y, in range, out int s, out int e)) continue;
            float py = oy + y * ch;
            dl.Rect(ox + s * cw, py, ox + (e + 1) * cw, py + ch, color, appear);
        }
    }

    /// <summary>
    /// selection_fg: after the glyphs, covers the selected cells with the selection color again and redraws their text
    /// in selection_fg (clipped to the selection, shaped like the row). Cached rows stay untouched, so selecting
    /// doesn't rebuild anything; emoji keep their colors.
    /// </summary>
    private void DrawSelectedText(DrawList dl, Pane pane, in GridPass grid, Theme theme, uint emojiTint)
    {
        if (theme.SelectionFg is not { } selFg) return;
        var t = pane.Term;
        if (pane.Selection?.Range(t) is not var (sl, sc, el, ec)) return;
        var range = new SelectedRange(sl, sc, el, ec, pane.Selection.Mode == SelectionMode.Block);
        float ox = grid.Ox, oy = grid.Oy, appear = grid.Appear;
        var fonts = dl.Fonts;
        int cw = fonts.CellWidth, ch = fonts.CellHeight, cols = t.Columns;
        long viewTop = t.ViewTopLine;
        uint bg = _dim.Apply(theme.Selection), fg = _dim.Apply(selFg);
        for (int y = grid.YFirst; y < grid.YEnd; y++)
        {
            if (!SelectedSpan(t, viewTop + y, in range, out int s, out int e) || t.GetLine(viewTop + y) is not { } row) continue;
            float py = oy + y * ch;
            var box = new RectF(ox + s * cw, py, (e + 1 - s) * cw, ch);
            dl.Rect(box, bg, appear);
            int n = Math.Min(row.Cells.Length, cols);
            dl.PushClip(box);
            // One cell of context each side: a wide glyph or a mark may reach into the selection.
            _text.DrawCells(dl, new GridText.RowShape(theme.HarfbuzzFeatures, row, n, y == grid.CursorRow ? t.CursorX : -1), s - 1, e + 1, ox, py,
                new GridText.CellInk(fg, appear, emojiTint));
            dl.PopClip();
            DrawSelectedDecorations(dl, row.Cells, s, Math.Min(e, n - 1), new RowPen(ox, py, fg, appear));
        }
    }

    /// <summary>Where and how a row's overlay decorations are drawn: the row's top-left (Ox, Py), in Fg at opacity A.</summary>
    private readonly record struct RowPen(float Ox, float Py, uint Fg, float A);

    /// <summary>The decorations of the selected cells [<paramref name="s"/>, <paramref name="e"/>], in the selection's colors.</summary>
    private static void DrawSelectedDecorations(DrawList dl, Cell[] cells, int s, int e, in RowPen pen)
    {
        float ox = pen.Ox, py = pen.Py, appear = pen.A;
        uint fg = pen.Fg;
        int cw = dl.Fonts.CellWidth;
        for (int x = s; x <= e; x++)
        {
            var f = cells[x].Flags;
            if ((f & CellAttributes.Hidden) != 0 || (f & DecorationFlags) == 0) continue;
            float px = ox + x * cw;
            int span = (f & CellAttributes.WideLead) != 0 ? 2 : 1;
            if ((f & CellAttributes.Underline) != 0) DrawUnderline(dl, CellStyle.Underline(f), px, py, span, fg, appear);
            DrawStrikeAndOverline(dl, f, px, py, span, fg, appear);
        }
    }

    /// <summary>An underline of any SGR 4:n style under <paramref name="span"/> cells at (px, py), in the draw list's grid font.</summary>
    private static void DrawUnderline(DrawList dl, UnderlineStyle style, float px, float py, int span, uint rgb, float a)
    {
        var fonts = dl.Fonts;
        int cw = fonts.CellWidth, ch = fonts.CellHeight, t = fonts.LineThickness, y = fonts.UnderlinePosition;
        switch (style)
        {
            case UnderlineStyle.Double:
                var (first, second) = Underlines.DoubleLines(ch, y, t);
                dl.Rect(px, py + first, px + cw * span, py + first + t, rgb, a);
                dl.Rect(px, py + second, px + cw * span, py + second + t, rgb, a);
                break;
            case UnderlineStyle.Curly or UnderlineStyle.Dotted or UnderlineStyle.Dashed:
                var g = dl.Atlas.GetUnderline(style);
                if (g.Empty) break;
                for (int i = 0; i < span; i++)
                {
                    float x0 = px + i * cw;
                    dl.Quad(x0, py + g.Top, x0 + g.Width, py + g.Top + g.Height, in g.Uv, rgb, a);
                }
                break;
            default:
                dl.Rect(px, py + y, px + cw * span, py + y + t, rgb, a);
                break;
        }
    }

    /// <summary>A selection: from column Sc of line Sl to column Ec of line El, or that block of columns on each line.</summary>
    private readonly record struct SelectedRange(long Sl, int Sc, long El, int Ec, bool Block);

    /// <summary>The selected columns [s, e] of a line (widened to whole wide characters), or false.</summary>
    private static bool SelectedSpan(Terminal t, long line, in SelectedRange range, out int s, out int e)
    {
        long sl = range.Sl, el = range.El;
        int sc = range.Sc, ec = range.Ec;
        bool block = range.Block;
        s = e = 0;
        if (line < sl || line > el) return false;
        int cols = t.Columns;
        s = line == sl || block ? sc : 0;
        e = Math.Min(line == el || block ? ec : cols - 1, cols - 1);
        if (t.GetLine(line) is { } row)
        {
            var cells = row.Cells;
            if (s > 0 && s < cells.Length && (cells[s].Flags & CellAttributes.WideTrail) != 0) s--;
            if (e >= 0 && e < cells.Length && (cells[e].Flags & CellAttributes.WideLead) != 0) e++;
        }
        return e >= s;
    }

    /// <summary>Vim marks fit in here: a–z and A–Z (<see cref="ScrollbarRenderer"/>).</summary>
    internal const int ScrollbarMarkCapacity = ScrollbarRenderer.MarkCapacity;

    /// <summary>What the pane's scrollbar ticks are built from right now (the open search's matches, vim's marks). Marks go in <paramref name="marks"/>.</summary>
    internal (IReadOnlyList<SearchMatch> Matches, long Signature, int Marks) TickSources(Pane pane, Span<long> marks) =>
        ScrollbarRenderer.TickSources(pane, marks, Search);
}
