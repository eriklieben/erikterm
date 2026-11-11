using ErikTerm.App.Native;
using ErikTerm.Core;
using ErikTerm.Core.Images;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Draws a pane's inline images (see Images/IImageGrid.cs): kitty placements, iTerm2 / sixel cell tiles and kitty
/// Unicode placeholders. Images aren't in the glyph atlas: each is uploaded once to a texture of its own, cached by
/// <see cref="TerminalImage.Serial"/>, and freed when the terminal evicts the image or it hasn't been drawn for a
/// while. Pixel art scaled up (≥ 1.5×) is drawn with SDL's pixel-art filter (nearest on older SDL), everything else
/// linearly. Drawn in two passes from <see cref="PaneRenderer"/>: kitty z &lt; 0 after the cell backgrounds, the
/// rest after the glyphs (tile and placeholder cells have no glyph of their own).
/// </summary>
#pragma warning disable S6640 // image pixels are pinned and handed to SDL textures through pointers
internal sealed unsafe class ImageRenderer
#pragma warning restore S6640
{
    private sealed class Texture
    {
        public nint Handle;
        public long LastUsed;
        public long Version; // TerminalImage.DisplayVersion uploaded (animation frame)
        public required TerminalImage Image;
    }

    private const long IdleMs = 30_000;

    private readonly Dictionary<long, Texture> _textures = [];
    private readonly List<long> _dead = [];
    private readonly List<KittyPlacement> _sorted = [];
    // Screen cell where each virtual placement's placeholders start (this pane, this frame): placements relative to
    // a virtual one are drawn from there.
    private readonly Dictionary<KittyPlacement, (float X, float Y)> _virtualOrigins = new(ReferenceEqualityComparer.Instance);
    private bool _hasVirtualChildren;

    /// <summary>
    /// When an animation drawn in the current frame next changes frame (long.MaxValue: none). Only images that were
    /// drawn count, so animations in hidden panes, background tabs or scrolled out of view wake nothing.
    /// </summary>
    public long NextAnimationAt { get; private set; } = long.MaxValue;
    private long _frame = long.MinValue;
    private int _upscaleMode = -1; // pixel art if this SDL supports it, else nearest

    /// <summary>Draws the images below the text (<paramref name="below"/>: kitty z &lt; 0) or above it.</summary>
    public void Draw(DrawList dl, Terminal t, float ox, float oy, ColorTransformCache dim, float alpha, long now, bool below)
    {
        if (now != _frame) { _frame = now; Sweep(now); NextAnimationAt = long.MaxValue; }
        var images = t.Images;
        int cw = dl.Fonts.CellWidth, ch = dl.Fonts.CellHeight;
        uint tint = dim.Apply(0xFFFFFF);

        var placements = images.Placements(t.IsAlternateScreen);
        if (!below) _hasVirtualChildren = false;
        if (placements.Count > 0) DrawPlacements(dl, t, placements, ox, oy, cw, ch, tint, alpha, now, below);
        if (below) return;
        _virtualOrigins.Clear();
        if (images.HasInlineImages || images.HasVirtualPlacements) DrawCells(dl, t, ox, oy, cw, ch, tint, alpha, now);
        if (_hasVirtualChildren) DrawVirtualChildren(dl, t, placements, cw, ch, tint, alpha, now);
    }

    /// <summary>Placements relative to a virtual placement, positioned from where its placeholders were drawn.</summary>
    private void DrawVirtualChildren(DrawList dl, Terminal t, List<KittyPlacement> placements, int cw, int ch, uint tint, float alpha, long now)
    {
        bool alt = t.IsAlternateScreen;
        foreach (var p in placements)
        {
            if (!p.RelativeToVirtual) continue;
            var root = p;
            for (int i = 0; i < 16 && root is { Virtual: false }; i++) root = t.Images.ParentOf(root, alt);
            if (root is null || !_virtualOrigins.TryGetValue(root, out var origin)) continue;
            DrawPlacement(dl, t, p, origin.X + p.Column * cw, origin.Y + p.Line * ch, cw, ch, tint, alpha, now);
        }
    }

    private void DrawPlacement(DrawList dl, Terminal t, KittyPlacement p, float x0, float y0, int cw, int ch, uint tint, float alpha, long now)
    {
        var img = p.Image;
        var (w, h) = p.DisplaySize(cw, ch);
        x0 += p.OffsetX;
        y0 += p.OffsetY;
        nint tex = TextureFor(dl, img, now);
        if (tex == 0) return;
        t.Images.Touch(img);
        dl.Image(tex, x0, y0, x0 + w, y0 + h,
            p.SourceX / (float)img.Width, p.SourceY / (float)img.Height,
            (p.SourceX + p.SourceWidth) / (float)img.Width, (p.SourceY + p.SourceHeight) / (float)img.Height,
            tint, alpha, ScaleMode(w / p.SourceWidth));
    }

    private void DrawPlacements(DrawList dl, Terminal t, List<KittyPlacement> placements, float ox, float oy, int cw, int ch,
        uint tint, float alpha, long now, bool below)
    {
        _sorted.Clear();
        foreach (var p in placements)
        {
            if (p.RelativeToVirtual) { _hasVirtualChildren = true; continue; }
            if (!p.Virtual && (p.Z < 0) == below) _sorted.Add(p);
        }
        // Stable insertion sort by z (lower first).
        for (int i = 1; i < _sorted.Count; i++)
        {
            var p = _sorted[i];
            int j = i - 1;
            while (j >= 0 && _sorted[j].Z > p.Z) { _sorted[j + 1] = _sorted[j]; j--; }
            _sorted[j + 1] = p;
        }

        long viewTop = t.ViewTopLine;
        float paneBottom = oy + t.Rows * ch;
        foreach (var p in _sorted)
        {
            var (_, h) = p.DisplaySize(cw, ch);
            float x0 = ox + p.Column * cw, y0 = oy + (p.Line - viewTop) * ch;
            if (y0 + p.OffsetY >= paneBottom || y0 + p.OffsetY + h <= oy) continue;
            DrawPlacement(dl, t, p, x0, y0, cw, ch, tint, alpha, now);
        }
    }

    /// <summary>What every run of a <see cref="DrawCells"/> call draws with.</summary>
    private readonly record struct CellPass(DrawList Dl, ImageStore Images, float Ox, int Cw, int Ch, uint Tint, float Alpha, long Now,
        bool Alt, bool Tiles, bool Placeholders);

    /// <summary>A placeholder cell's image, row, column and high id byte (-1: none) after inference.</summary>
    private struct PlaceholderCell
    {
        public uint Id;
        public int Row, Col, High;
    }

    /// <summary>Cell tiles (iTerm2, sixel) and kitty placeholders, merged into one quad per run of adjacent cells.</summary>
    private void DrawCells(DrawList dl, Terminal t, float ox, float oy, int cw, int ch, uint tint, float alpha, long now)
    {
        var images = t.Images;
        var pass = new CellPass(dl, images, ox, cw, ch, tint, alpha, now, t.IsAlternateScreen, images.HasInlineImages, images.HasVirtualPlacements);
        int rows = t.Rows, cols = t.Columns;
        for (int y = 0; y < rows; y++)
        {
            var row = t.GetVisibleRow(y);
            DrawRowCells(in pass, row, Math.Min(row.Cells.Length, cols), oy + y * ch);
        }
    }

    private void DrawRowCells(in CellPass pass, Row row, int n, float py)
    {
        var cells = row.Cells;
        // Placeholder state of the previous cell, for inferring omitted diacritics.
        var prev = new PlaceholderCell { Row = -1, Col = -1, High = -1 };

        for (int x = 0; x < n;)
        {
            ref readonly var c = ref cells[x];
            if (pass.Tiles && (c.Flags & CellAttributes.Image) != 0)
            {
                x = DrawTileRun(in pass, cells, x, n, py);
                prev.Id = 0;
            }
            else if (pass.Placeholders && c.Rune == KittyPlaceholder.Codepoint)
            {
                x = DrawPlaceholderRun(in pass, row, x, n, py, ref prev);
            }
            else
            {
                prev.Id = 0;
                x++;
            }
        }
    }

    /// <summary>Draws the run of tiles of one image starting at cell <paramref name="x"/>; returns the cell after it.</summary>
    private int DrawTileRun(in CellPass pass, Cell[] cells, int x, int n, float py)
    {
        var (slot, r, col) = ImageStore.UnpackTile(cells[x].Fg);
        int end = x + 1, nextCol = col + 1;
        while (end < n && (cells[end].Flags & CellAttributes.Image) != 0 && cells[end].Fg == ImageStore.PackTile(slot, r, nextCol))
        {
            end++;
            nextCol++;
        }
        if (pass.Images.GetSlot(slot) is { } inline)
            DrawRun(pass.Dl, inline.Image, 0, 0, inline.Image.Width, inline.Image.Height,
                inline.ContentX, inline.ContentY, inline.ContentW, inline.ContentH,
                r, col, nextCol, pass.Ox + x * pass.Cw, py, pass.Cw, pass.Ch, pass.Tint, pass.Alpha, pass.Now, pass.Images);
        return end;
    }

    /// <summary>Draws one placeholder run (same image, same row, consecutive columns) from cell <paramref name="x"/>; returns the cell after it.</summary>
    private int DrawPlaceholderRun(in CellPass pass, Row row, int x, int n, float py, ref PlaceholderCell prev)
    {
        var cells = row.Cells;
        int start = x;
        uint runId = 0;
        int runRow = 0, runCol = 0, count = 0;
        while (x < n && cells[x].Rune == KittyPlaceholder.Codepoint)
        {
            var cell = ResolvePlaceholder(row, x, cells[x].Fg, prev);
            if (count > 0 && (cell.Id != runId || cell.Row != runRow || cell.Col != runCol + count)) break;
            if (count == 0) { runId = cell.Id; runRow = cell.Row; runCol = cell.Col; }
            count++;
            prev = cell;
            x++;
        }
        if (runId != 0 && pass.Images.FindVirtual(runId, 0, pass.Alt) is { } vp)
            DrawPlaceholders(in pass, vp, start, runRow, runCol, count, py);
        return x;
    }

    /// <summary>A placeholder cell's values, the omitted diacritics inferred from the previous cell.</summary>
    private static PlaceholderCell ResolvePlaceholder(Row row, int x, uint fg, in PlaceholderCell prev)
    {
        Terminal.TryGetCluster(row, x, out var text);
        var (dr, dc, dh) = KittyPlaceholder.Decode(text);
        uint id = KittyPlaceholder.ImageIdFromColor(fg);
        bool continues = prev.Id != 0 && (id & 0xFF_FFFF) == (prev.Id & 0xFF_FFFF);
        int prow = dr;
        if (prow < 0) prow = continues ? prev.Row : 0;
        int pcol = dc;
        if (pcol < 0) pcol = continues && prow == prev.Row ? prev.Col + 1 : 0;
        int high = dh;
        if (high < 0) high = continues && prow == prev.Row && pcol == prev.Col + 1 ? prev.High : -1;
        if (high > 0) id |= (uint)high << 24;
        return new PlaceholderCell { Id = id, Row = prow, Col = pcol, High = high };
    }

    private void DrawPlaceholders(in CellPass pass, KittyPlacement vp, int start, int runRow, int runCol, int count, float py)
    {
        int cw = pass.Cw, ch = pass.Ch;
        _virtualOrigins.TryAdd(vp, (pass.Ox + (start - runCol) * cw, py - runRow * ch));
        // Fit the source rectangle into the placement's box, preserving the aspect ratio, centred.
        float boxW = vp.Columns * cw, boxH = vp.Rows * ch;
        float scale = MathF.Min(boxW / vp.SourceWidth, boxH / vp.SourceHeight);
        float cwCells = vp.SourceWidth * scale / cw, chCells = vp.SourceHeight * scale / ch;
        DrawRun(pass.Dl, vp.Image, vp.SourceX, vp.SourceY, vp.SourceWidth, vp.SourceHeight,
            (vp.Columns - cwCells) / 2, (vp.Rows - chCells) / 2, cwCells, chCells,
            runRow, runCol, runCol + count, pass.Ox + start * cw, py, cw, ch, pass.Tint, pass.Alpha, pass.Now, pass.Images);
    }

    /// <summary>
    /// Draws image row <paramref name="r"/>, columns [<paramref name="c0"/>, <paramref name="c1"/>) of a box whose
    /// picture fills the content rectangle (cell units) — the cells start at screen pixel (sx, sy).
    /// </summary>
    private void DrawRun(DrawList dl, TerminalImage img, int srcX, int srcY, int srcW, int srcH,
        float cx, float cy, float cwCells, float chCells, int r, int c0, int c1, float sx, float sy, int cw, int ch,
        uint tint, float alpha, long now, ImageStore store)
    {
        if (cwCells <= 0 || chCells <= 0) return;
        float ix0 = MathF.Max(c0, cx), ix1 = MathF.Min(c1, cx + cwCells);
        float iy0 = MathF.Max(r, cy), iy1 = MathF.Min(r + 1, cy + chCells);
        if (ix1 <= ix0 || iy1 <= iy0) return;
        nint tex = TextureFor(dl, img, now);
        if (tex == 0) return;
        store.Touch(img);
        float u0 = (srcX + (ix0 - cx) / cwCells * srcW) / img.Width, u1 = (srcX + (ix1 - cx) / cwCells * srcW) / img.Width;
        float v0 = (srcY + (iy0 - cy) / chCells * srcH) / img.Height, v1 = (srcY + (iy1 - cy) / chCells * srcH) / img.Height;
        float x0 = sx + (ix0 - c0) * cw, x1 = sx + (ix1 - c0) * cw;
        float y0 = sy + (iy0 - r) * ch, y1 = sy + (iy1 - r) * ch;
        dl.Image(tex, x0, y0, x1, y1, u0, v0, u1, v1, tint, alpha, ScaleMode(cwCells * cw / srcW));
    }

    private int ScaleMode(float scale)
    {
        if (scale < 1.5f) return Sdl.SCALEMODE_LINEAR;
        return _upscaleMode < 0 ? Sdl.SCALEMODE_NEAREST : _upscaleMode;
    }

    private nint TextureFor(DrawList dl, TerminalImage img, long now)
    {
        if (img.Animation is { } anim)
        {
            anim.Update(now);
            int ms = anim.MsUntilNextFrame(now);
            if (ms != int.MaxValue) NextAnimationAt = Math.Min(NextAnimationAt, now + Math.Max(1, ms));
        }
        if (_textures.TryGetValue(img.Serial, out var tex))
        {
            tex.LastUsed = now;
            if (tex.Handle != 0 && tex.Version != img.DisplayVersion && img.DisplayRgba is { } frame)
            {
                // Another animation frame: same size, re-uploaded into the same texture.
                fixed (byte* p = frame) Sdl.UpdateTexture(tex.Handle, null, p, img.Width * 4);
                tex.Version = img.DisplayVersion;
            }
            return tex.Handle;
        }
        var rgba = img.DisplayRgba;
        if (rgba is null) return 0;
        nint handle = Sdl.CreateTexture(dl.Renderer, Sdl.PIXELFORMAT_RGBA32, Sdl.TEXTUREACCESS_STATIC, img.Width, img.Height);
        if (handle != 0)
        {
            Sdl.SetTextureBlendMode(handle, Sdl.BLENDMODE_BLEND);
            if (_upscaleMode < 0)
                _upscaleMode = Sdl.SetTextureScaleMode(handle, Sdl.SCALEMODE_PIXELART) ? Sdl.SCALEMODE_PIXELART : Sdl.SCALEMODE_NEAREST;
            fixed (byte* p = rgba) Sdl.UpdateTexture(handle, null, p, img.Width * 4);
        }
        // A failed texture (too large for the GPU) is remembered as 0 so it isn't retried every frame.
        _textures[img.Serial] = new Texture { Handle = handle, LastUsed = now, Image = img, Version = img.DisplayVersion };
        return handle;
    }

    /// <summary>Frees textures of evicted images and ones not drawn for a while (they're re-uploaded on demand).</summary>
    private void Sweep(long now)
    {
        _dead.Clear();
        foreach (var (serial, tex) in _textures)
            if (tex.Image.IsEvicted || now - tex.LastUsed > IdleMs) _dead.Add(serial);
        foreach (long serial in _dead)
        {
            if (_textures.Remove(serial, out var tex) && tex.Handle != 0) Sdl.DestroyTexture(tex.Handle);
        }
    }
}
