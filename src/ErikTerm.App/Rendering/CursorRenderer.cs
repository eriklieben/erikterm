using ErikTerm.App.Panes;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.Rendering;

/// <summary>Per-pane cursor animation state: the glide between cells and the blink clock.</summary>
internal sealed class CursorState
{
    public Tween X = new(0), Y = new(0); // in cells; Y is the screen row (independent of scrollback)
    public int LastX = -1, LastY = -1;
    public long MovedAt;
    /// <summary>Last keypress or cursor move: blinking pauses while you type.</summary>
    public long ActiveAt;
    /// <summary>Until when the bar is drawn thicker after a typed character.</summary>
    public long SettleUntil;
    /// <summary>When the program hid the cursor (0: it's showing), and whether that has outlasted the grace period.</summary>
    public long HiddenAt;
    public bool HideApplied;
    /// <summary>The cell under the cursor last frame, to tell typing from moving over existing text.</summary>
    public Cell? Resting;

    public void Touch(long now) => ActiveAt = now;

    public bool IsGliding(long now) => X.IsAnimating(now) || Y.IsAnimating(now);
}

/// <summary>
/// Draws the text cursor after the glyphs: a block that inverts the text under it, a bar or an
/// underline. Blinking cursors fade (eased) rather than flick, pause while typing, and the cursor
/// glides on short moves when cursor_animation is on; typing moves it at once, and a bar stays thicker for a moment.
/// </summary>
internal static class CursorRenderer
{
    public const float GlideMs = 60;

    /// <summary>A glide starts this far in, so the frame after a key press has already moved.</summary>
    private const long GlideLeadMs = 16;

    /// <summary>Whether this pane's cursor blinks right now (focused, blinking style, visible).</summary>
    /// <summary>Bar width / underline height in device pixels for the configured thickness (fractions allowed).</summary>
    public static float Thickness(float lineThickness, float points, float cells, float scale, int cellSize)
    {
        float t;
        if (points > 0) t = points * scale;
        else if (cells > 0) t = cells * cellSize;
        else t = MathF.Max(2, lineThickness * 2);
        return Math.Clamp(t, 0.5f, cellSize);
    }

    public static bool Blinks(Pane pane, bool focused, bool windowFocused, int rateMs) =>
        focused && windowFocused && rateMs > 0 && pane.Term.CursorBlink && pane.Term.CursorVisible;

    /// <summary>Frame interval while a blink fades: a soft fade reads fine at ~30 fps and an idle window stays cheap.</summary>
    public const int BlinkFadeFrameMs = 33;

    /// <summary>Milliseconds until this pane's cursor next needs a frame (0 = animating now: gliding).</summary>
    public static int MsUntilNextFrame(Pane pane, bool focused, bool windowFocused, int rateMs, long now, bool phase = false)
    {
        if (focused && pane.Cursor.IsGliding(now)) return 0;
        if (focused && windowFocused && !pane.Term.CursorVisible && !pane.Cursor.HideApplied) return 0; // one more frame takes it away
        // One timed frame takes the settle away. Frames drawn in between would make the next key wait for a screen refresh.
        int settle = focused ? CursorMotion.MsUntilSettled(now, pane.Cursor.SettleUntil) : int.MaxValue;
        if (!Blinks(pane, focused, windowFocused, rateMs)) return settle;
        int ms = CursorMotion.MsUntilChange(now - pane.Cursor.ActiveAt, rateMs, phase);
        return Math.Min(ms == 0 ? BlinkFadeFrameMs : ms, settle);
    }

    /// <summary>Follows the terminal's cursor. Runs before the glyphs are drawn, which need to know about a reveal.</summary>
    public static void Track(Pane pane, bool animate, long now)
    {
        var t = pane.Term;
        var s = pane.Cursor;

        // While hidden the position isn't followed: mid-redraw the program's cursor is all over the screen.
        if (!t.CursorVisible)
        {
            if (s.HiddenAt == 0) s.HiddenAt = now;
            s.HideApplied = CursorMotion.HideTakesEffect(now - s.HiddenAt);
            return;
        }
        s.HiddenAt = 0;
        s.HideApplied = false;

        // Track movement in screen rows so output scrolling under a resting cursor doesn't animate.
        int cx = t.CursorX, cy = t.CursorY;
        if (cx != s.LastX || cy != s.LastY)
        {
            int dx = cx - s.LastX, dy = cy - s.LastY;
            bool glide = animate && s.LastX >= 0 && CursorMotion.ShouldGlide(dx, dy, now - s.MovedAt);
            bool typed = glide && CursorMotion.IsTyping(dx, dy, CellChanged(s.Resting, CellAt(pane, s.LastX, s.LastY)));
            if (glide && !typed) { s.X.Set(cx, now - GlideLeadMs, GlideMs); s.Y.Set(cy, now - GlideLeadMs, GlideMs); }
            else { s.X.Snap(cx); s.Y.Snap(cy); } // typing: there the same frame as the letter, never on top of it
            if (typed)
            {
                s.SettleUntil = now + CursorMotion.TypingSettleMs;
            }
            s.LastX = cx;
            s.LastY = cy;
            s.MovedAt = now;
            s.ActiveAt = now;
        }
        s.Resting = CellAt(pane, cx, cy);
    }

    private static Cell? CellAt(Pane pane, int col, int screenRow)
    {
        var t = pane.Term;
        int viewRow = screenRow + t.ViewOffset;
        if (viewRow < 0 || viewRow >= t.Rows) return null;
        var row = t.GetVisibleRow(viewRow);
        return row is not null && col >= 0 && col < row.Cells.Length ? row.Cells[col] : null;
    }

    private static bool CellChanged(Cell? before, Cell? after) =>
        before is { } b && after is { } a && (a.Rune != b.Rune || a.Fg != b.Fg || a.Bg != b.Bg || a.Flags != b.Flags);

    /// <summary>
    /// The cursor settings: <see cref="BlinkRateMs"/> (0: steady), the shaping <see cref="Features"/> of the text under a
    /// block, cursor_border (<see cref="Border"/>), the blink <see cref="Phase"/>, cursor_thickness (window points × Scale
    /// or a fraction of the cell; both 0: automatic) and cursor_height (<see cref="BarHeightEm"/>, 0 fills the cell).
    /// </summary>
    public readonly record struct Look(int BlinkRateMs, IReadOnlyList<string> Features, uint? Border = null, bool Phase = false,
        float ThicknessPoints = 0, float ThicknessCells = 0, float Scale = 1, float BarHeightEm = 0);

    public static void Draw(in PaneFrame frame, bool focused, bool windowFocused, in Look look)
    {
        var dl = frame.Dl;
        var pane = frame.Pane;
        float ox = frame.Ox, oy = frame.Oy, appear = frame.Appear;
        long now = frame.Now;
        int blinkRateMs = look.BlinkRateMs;
        bool phase = look.Phase;
        float thicknessPoints = look.ThicknessPoints, thicknessCells = look.ThicknessCells, scale = look.Scale;
        var t = pane.Term;
        var s = pane.Cursor;
        var fonts = dl.Fonts;
        int cw = fonts.CellWidth, ch = fonts.CellHeight;
        int cx = s.LastX, cy = s.LastY; // Track's: where it was last seen, if it's hidden just now
        if (IsOff(t, s, focused, windowFocused)) return;

        float col = s.X.Value(now), row = s.Y.Value(now) + t.ViewOffset;
        if (row <= -1 || row >= t.Rows) return;
        float px = MathF.Round(ox + col * cw), py = MathF.Round(oy + row * ch);

        var pal = t.Palette;
        bool active = focused && windowFocused;
        float blink = Blinks(pane, focused, windowFocused, blinkRateMs) ? CursorMotion.BlinkOpacity(now - s.ActiveAt, blinkRateMs, phase) : 1;
        float a = appear * blink;

        var cellRow = RowUnderCursor(t, cy);
        float w = cw * (IsWideLead(cellRow, cx) ? 2 : 1);

        // A bar hugs the text instead of filling the cell (line_height makes cells tall).
        var (barTop, barBottom) = t.CursorShape == CursorShape.Bar
            ? CursorMotion.BarSpan(ch, fonts.Baseline, fonts.PixelSize, look.BarHeightEm)
            : (0f, ch);
        float top = py + barTop, bottom = py + barBottom;

        if (!active)
        {
            DrawOutline(dl, frame.Dim.Apply(look.Border ?? pal.Cursor), px, top, bottom, w, appear);
            return;
        }
        if (a <= 0.002f) return;

        switch (t.CursorShape)
        {
            case CursorShape.Bar:
                DrawBar(dl, pal.Cursor, a, px, top, bottom,
                    Thickness(fonts.LineThickness, thicknessPoints, thicknessCells, scale, cw) + (now < s.SettleUntil ? MathF.Max(1, MathF.Round(scale)) : 0));
                break;
            case CursorShape.Underline:
                DrawUnderline(dl, pal.Cursor, a, new RectF(px, py, w, ch), Thickness(fonts.LineThickness, thicknessPoints, thicknessCells, scale, ch));
                break;
            default:
                var box = new RectF(px, py, w, ch);
                dl.Rect(box, pal.Cursor, a);
                if (cellRow is not null) DrawCellsUnderBlock(in frame, box, cellRow, look.Features, col, cx, a);
                break;
        }
    }

    private static bool IsOff(Terminal t, CursorState s, bool focused, bool windowFocused) =>
        s.LastX < 0 || !t.CursorVisible && (s.HideApplied || !focused || !windowFocused);

    private static Row? RowUnderCursor(Terminal t, int cy)
    {
        int viewRow = cy + t.ViewOffset;
        return viewRow >= 0 && viewRow < t.Rows ? t.GetVisibleRow(viewRow) : null;
    }

    private static bool IsWideLead(Row? row, int cx) =>
        row is not null && cx < row.Cells.Length && (row.Cells[cx].Flags & CellAttributes.WideLead) != 0;

    /// <summary>Unfocused: a hollow outline in the current shape's footprint, never blinking; cursor_border when the scheme has one.</summary>
    private static void DrawOutline(DrawList dl, uint color, float px, float top, float bottom, float w, float appear)
    {
        dl.Rect(px, top, px + w, top + 1, color, appear);
        dl.Rect(px, bottom - 1, px + w, bottom, color, appear);
        dl.Rect(px, top, px + 1, bottom, color, appear);
        dl.Rect(px + w - 1, top, px + w, bottom, color, appear);
    }

    /// <summary>
    /// Whole device pixels at full strength, a fractional remainder as a fainter column: a 1.5 px bar reads as thinner
    /// than 2 px without blurring the edge it starts on.
    /// </summary>
    private static void DrawBar(DrawList dl, uint color, float a, float px, float top, float bottom, float bar)
    {
        float whole = MathF.Floor(bar), frac = bar - whole;
        if (whole > 0) dl.Rect(px, top, px + whole, bottom, color, a);
        if (frac > 0.02f) dl.Rect(px + whole, top, px + whole + 1, bottom, color, a * frac);
    }

    /// <summary>An underline <paramref name="bar"/> pixels high along the bottom of <paramref name="cell"/>.</summary>
    private static void DrawUnderline(DrawList dl, uint color, float a, RectF cell, float bar)
    {
        float whole = MathF.Floor(bar), frac = bar - whole;
        float px = cell.X, right = cell.Right, bottom = cell.Bottom;
        if (whole > 0) dl.Rect(px, bottom - whole, right, bottom, color, a);
        if (frac > 0.02f) dl.Rect(px, bottom - whole - 1, right, bottom - whole, color, a * frac);
    }

    /// <summary>
    /// Re-draws the glyphs the block <paramref name="box"/> covers in the cursor text color, clipped to the block, so the
    /// text stays readable mid-glide (at column <paramref name="col"/>, on its way to <paramref name="cx"/>) and
    /// cross-fades with the blink.
    /// </summary>
    private static void DrawCellsUnderBlock(in PaneFrame frame, RectF box, Row cellRow, IReadOnlyList<string> features, float col, int cx, float a)
    {
        var dl = frame.Dl;
        var t = frame.Pane.Term;
        dl.PushClip(box);
        int first = Math.Max(0, (int)MathF.Floor(col) - 1), last = Math.Min(cellRow.Cells.Length - 1, (int)MathF.Ceiling(col) + 1);
        // Shaped like the row itself (the cursor cell is its own run), so clusters and emoji come out right.
        int n = Math.Min(cellRow.Cells.Length, t.Columns);
        frame.Text.DrawCells(dl, new GridText.RowShape(features, cellRow, n, cx), first, last, frame.Ox, box.Y,
            new GridText.CellInk(t.Palette.CursorText, a));
        dl.PopClip();
    }
}
