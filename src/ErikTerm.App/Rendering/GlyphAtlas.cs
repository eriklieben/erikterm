using ErikTerm.App.Native;
using ErikTerm.Core;

namespace ErikTerm.App.Rendering;

/// <summary>
/// One RGBA texture holding everything the renderer samples. Monochrome content is white pixels whose
/// alpha is coverage, tinted by vertex color; color glyphs (emoji) are stored as real color and must be
/// drawn with a white vertex color (<see cref="Glyph.Color"/>). Contents:
///  - a 2x2 opaque block at the origin for solid quads;
///  - glyphs by codepoint at any pixel size (UI text, big pane-select labels);
///  - glyphs by (face, glyph index) at the grid size, for shaped grid text; color glyphs are scaled to
///    fit their cells;
///  - procedural box-drawing / block / braille / powerline glyphs at the exact cell size;
///  - curly / dotted / dashed underline strips at the exact cell size (<see cref="Underlines"/>);
///  - generated nine-slice masks for anti-aliased rounded rectangles and soft drop shadows.
/// Shelf packing; when full, everything is dropped and rebuilt lazily.
/// </summary>
#pragma warning disable S6640 // glyph bitmaps are copied out of FreeType's memory into SDL textures
internal sealed unsafe class GlyphAtlas : IDisposable
#pragma warning restore S6640
{
    public const int Size = 2048;

    public struct Glyph
    {
        public bool Empty;
        /// <summary>RGBA color glyph: draw with white vertex color so it isn't tinted.</summary>
        public bool Color;
        public int Left, Top, Width, Height;
        public float Advance;
        public float U0, V0, U1, V1;
    }

    /// <summary>A rounded-rect mask: <see cref="Corner"/> px corner cells around a 2px stretchable middle.</summary>
    public struct Mask
    {
        public int Corner;   // radius + blur
        public int Blur;
        public int Pixels;   // full mask size = 2 * Corner + 2
        public float U0, V0; // top-left of the mask in UV space
    }

    private readonly FontSet _fonts;
    private readonly Dictionary<long, Glyph> _glyphs = [];
    private readonly Dictionary<int, Mask> _masks = [];
    private byte[] _scratch = new byte[64 * 64 * 4];
    private byte[] _coverage = new byte[64 * 64];
    private int _penX, _penY, _rowHeight;

    public nint Texture { get; }
    public static float SolidU => 1f / Size;
    public static float SolidV => 1f / Size;

    /// <summary>Cached glyphs and masks (for the About screen).</summary>
    public int Entries => _glyphs.Count + _masks.Count;

    /// <summary>Fraction of the texture's rows in use (packing is shelf-by-shelf).</summary>
    public float Fill => Math.Min(1f, (_penY + _rowHeight) / (float)Size);

    /// <summary>Set when the cache was flushed; glyphs fetched earlier in the same frame are stale.</summary>
    public bool Flushed { get; set; }

    /// <summary>Bumped every time the atlas is cleared: UVs recorded before (cached row quads) are invalid.</summary>
    public int Generation { get; private set; }

    public GlyphAtlas(nint renderer, FontSet fonts)
    {
        _fonts = fonts;
        Texture = Sdl.CreateTexture(renderer, Sdl.PIXELFORMAT_RGBA32, Sdl.TEXTUREACCESS_STATIC, Size, Size);
        if (Texture == 0) throw new InvalidOperationException("SDL_CreateTexture: " + Sdl.GetError());
        Sdl.SetTextureBlendMode(Texture, Sdl.BLENDMODE_BLEND);
        Sdl.SetTextureScaleMode(Texture, Sdl.SCALEMODE_LINEAR); // linear: masks stretch smoothly; glyphs are drawn pixel-aligned
        Clear();
    }

    public void Clear()
    {
        _glyphs.Clear();
        _masks.Clear();
        uint* white = stackalloc uint[4] { 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF };
        var rect = new Sdl.Rect { X = 0, Y = 0, W = 2, H = 2 };
        Sdl.UpdateTexture(Texture, &rect, white, 8);
        _penX = 4;
        _penY = 0;
        _rowHeight = 2;
        Flushed = true;
        Generation++;
    }

    // ---- Keys: the top 4 bits say which kind of entry --------------------------------------------

    private static long CharKey(int codepoint, FontStyle style, int px) => (long)codepoint << 12 | (long)px << 2 | (long)style;

    private static long GlyphKey(int face, uint glyph, int px, int boxW, int boxH) =>
        1L << 60 | (long)(face & 0x3FF) << 46 | (long)(glyph & 0xFFFF) << 30 | (long)(boxH & 0x3FF) << 20 | (long)(boxW & 0x3FF) << 10 | (long)(px & 0x3FF);

    private static long BoxKey(int codepoint, int w, int h, int thickness) =>
        2L << 60 | (long)(codepoint & 0x1FFFFF) << 30 | (long)(h & 0x3FF) << 20 | (long)(w & 0x3FF) << 10 | (long)(thickness & 0x3FF);

    // ---- Lookups ------------------------------------------------------------------------------

    public Glyph Get(int codepoint, FontStyle style) => Get(codepoint, style, _fonts.PixelSize);

    /// <summary>A glyph by codepoint (UI text). Color emoji are scaled to the text size, two advances wide.</summary>
    public Glyph Get(int codepoint, FontStyle style, float pixelSize)
    {
        int px = (int)Math.Clamp(MathF.Round(pixelSize), 4, 400);
        long key = CharKey(codepoint, style, px);
        if (_glyphs.TryGetValue(key, out var g)) return g;
        if (!_fonts.Rasterize(codepoint, style, px, out var bmp)) g = new Glyph { Empty = true };
        else if (bmp.PixelMode == FreeType.PIXEL_MODE_BGRA)
        {
            var m = _fonts.MetricsFor(px);
            g = FromColorBitmap(bmp, (int)MathF.Round(2 * m.Advance), (int)MathF.Ceiling(m.Height), (int)MathF.Round(m.Ascender));
        }
        else g = FromBitmap(bmp);
        _glyphs[key] = g;
        return g;
    }

    /// <summary>
    /// A glyph by face and glyph index at the grid size (shaped grid text). Color glyphs are scaled to
    /// fit <paramref name="cells"/> cells and centred in them; their Top is relative to the baseline.
    /// </summary>
    public Glyph GetGlyph(int face, uint glyph, int cells = 1)
    {
        int px = (int)_fonts.GridPx;
        bool color = _fonts.IsColorFace(face);
        int boxW = color ? _fonts.CellWidth * Math.Max(1, cells) : 0, boxH = color ? _fonts.CellHeight : 0;
        long key = GlyphKey(face, glyph, px, boxW, boxH);
        if (_glyphs.TryGetValue(key, out var g)) return g;
        if (!_fonts.RasterizeGlyph(face, glyph, px, out var bmp)) g = new Glyph { Empty = true };
        else if (bmp.PixelMode == FreeType.PIXEL_MODE_BGRA) g = FromColorBitmap(bmp, boxW, boxH, _fonts.Baseline);
        else g = FromBitmap(bmp);
        _glyphs[key] = g;
        return g;
    }

    /// <summary>
    /// A procedurally drawn cell glyph (see <see cref="BoxDrawing"/>) at the current cell size. It covers
    /// the whole cell: draw it at the cell's top-left corner, Width x Height.
    /// </summary>
    public Glyph GetBox(int codepoint)
    {
        int w = _fonts.CellWidth, h = _fonts.CellHeight, t = _fonts.LineThickness;
        long key = BoxKey(codepoint, w, h, t);
        if (_glyphs.TryGetValue(key, out var g)) return g;
        if (_coverage.Length < w * h) _coverage = new byte[w * h];
        if (!BoxDrawing.Render(codepoint, w, h, t, _coverage)) g = new Glyph { Empty = true };
        else
        {
            fixed (byte* src = _coverage) g = Upload(src, w, w, h, mono: false);
            g.Advance = w;
        }
        _glyphs[key] = g;
        return g;
    }

    /// <summary>
    /// A curly, dotted or dashed underline strip (see <see cref="Underlines"/>) for the current cell size: one cell wide,
    /// drawn at the cell's left edge, <see cref="Glyph.Top"/> rows below its top. Cached per style, cell size and
    /// thickness, so frames with styled underlines stay allocation-free.
    /// </summary>
    public Glyph GetUnderline(UnderlineStyle style)
    {
        int w = _fonts.CellWidth, h = _fonts.CellHeight, t = _fonts.LineThickness;
        int underlineY = _fonts.UnderlinePosition;
        // Box keys above Unicode: style and underline position folded into the "codepoint".
        long key = BoxKey(0x110000 + ((int)style << 12) + (underlineY & 0xFFF), w, h, t);
        if (_glyphs.TryGetValue(key, out var g)) return g;
        var (top, height) = Underlines.Strip(style, h, underlineY, t, _fonts.Baseline);
        if (_coverage.Length < w * height) _coverage = new byte[w * height];
        if (!Underlines.Render(style, w, height, t, _coverage)) g = new Glyph { Empty = true };
        else
        {
            fixed (byte* src = _coverage) g = Upload(src, w, w, height, mono: false);
            g.Top = top;
            g.Advance = w;
        }
        _glyphs[key] = g;
        return g;
    }

    // ---- Uploads ------------------------------------------------------------------------------

    private Glyph FromBitmap(in FreeType.GlyphBitmap bmp)
    {
        float advance = (float)bmp.AdvanceX;
        if (bmp.Width == 0 || bmp.Rows == 0 || bmp.Buffer == null) return new Glyph { Empty = true, Advance = advance };
        var g = Upload(bmp.Buffer, bmp.Pitch, bmp.Width, bmp.Rows, bmp.PixelMode == FreeType.PIXEL_MODE_MONO);
        g.Left = bmp.Left;
        g.Top = bmp.Top;
        g.Advance = advance;
        return g;
    }

    /// <summary>Copies a coverage bitmap (8-bit gray or 1-bit mono) as white + alpha.</summary>
    private Glyph Upload(byte* src, int pitch, int w, int h, bool mono)
    {
        Allocate(w, h, out int x, out int y); // a flush here only drops cached entries; the source bitmap is still valid
        int needed = w * h * 4;
        if (_scratch.Length < needed) _scratch = new byte[needed];
        fixed (byte* dst = _scratch)
        {
            for (int row = 0; row < h; row++)
            {
                byte* srcRow = src + row * pitch;
                uint* dstRow = (uint*)(dst + row * w * 4);
                for (int col = 0; col < w; col++)
                {
                    byte a;
                    if (mono) a = (byte)((srcRow[col >> 3] & (0x80 >> (col & 7))) != 0 ? 255 : 0);
                    else a = srcRow[col];
                    dstRow[col] = 0x00FFFFFFu | (uint)a << 24; // RGBA32 is R,G,B,A in memory (little endian)
                }
            }
            var rect = new Sdl.Rect { X = x, Y = y, W = w, H = h };
            Sdl.UpdateTexture(Texture, &rect, dst, w * 4);
        }
        return Region(x, y, w, h);
    }

    /// <summary>
    /// Scales a premultiplied BGRA bitmap to fit boxW x boxH (area averaging), converts it to straight
    /// RGBA and centres it in the box. <paramref name="baseline"/> is the box's baseline from its top.
    /// </summary>
    private Glyph FromColorBitmap(in FreeType.GlyphBitmap bmp, int boxW, int boxH, int baseline)
    {
        int sw = bmp.Width, sh = bmp.Rows;
        if (sw == 0 || sh == 0 || bmp.Buffer == null || boxW <= 0 || boxH <= 0)
            return new Glyph { Empty = true, Color = true, Advance = boxW };
        double scale = Math.Min((double)boxW / sw, (double)boxH / sh);
        int w = Math.Clamp((int)Math.Round(sw * scale), 1, boxW), h = Math.Clamp((int)Math.Round(sh * scale), 1, boxH);
        Allocate(w, h, out int x, out int y);

        int needed = w * h * 4;
        if (_scratch.Length < needed) _scratch = new byte[needed];
        double fx = (double)sw / w, fy = (double)sh / h; // source pixels per destination pixel
        fixed (byte* dst = _scratch)
        {
            for (int dy = 0; dy < h; dy++)
                for (int dx = 0; dx < w; dx++)
                    ScaleColorPixel(in bmp, dx * fx, dy * fy, fx, fy, dst + (dy * w + dx) * 4);
            var rect = new Sdl.Rect { X = x, Y = y, W = w, H = h };
            Sdl.UpdateTexture(Texture, &rect, dst, w * 4);
        }

        var glyph = Region(x, y, w, h);
        glyph.Color = true;
        glyph.Left = (boxW - w) / 2;
        glyph.Top = baseline - (boxH - h) / 2;
        glyph.Advance = boxW;
        return glyph;
    }

    /// <summary>Averages the source pixels under one destination pixel into straight RGBA at <paramref name="o"/>.</summary>
    private static void ScaleColorPixel(in FreeType.GlyphBitmap bmp, double sx0, double sy0, double fx, double fy, byte* o)
    {
        int sw = bmp.Width, sh = bmp.Rows;
        double sx1 = sx0 + fx, sy1 = sy0 + fy;
        double b = 0, g = 0, r = 0, a = 0, total = 0;
        for (int sy = (int)sy0; sy < Math.Min(sh, (int)Math.Ceiling(sy1)); sy++)
        {
            double wy = Math.Min(sy + 1, sy1) - Math.Max(sy, sy0);
            byte* row = bmp.Buffer + sy * bmp.Pitch;
            for (int sx = (int)sx0; sx < Math.Min(sw, (int)Math.Ceiling(sx1)); sx++)
            {
                double wgt = wy * (Math.Min(sx + 1, sx1) - Math.Max(sx, sx0));
                byte* p = row + sx * 4;
                b += p[0] * wgt; g += p[1] * wgt; r += p[2] * wgt; a += p[3] * wgt;
                total += wgt;
            }
        }
        if (total <= 0 || a <= 0) { *(uint*)o = 0; return; }
        // Premultiplied average -> straight alpha (the texture blends non-premultiplied).
        o[0] = (byte)Math.Min(255, Math.Round(r * 255 / a));
        o[1] = (byte)Math.Min(255, Math.Round(g * 255 / a));
        o[2] = (byte)Math.Min(255, Math.Round(b * 255 / a));
        o[3] = (byte)Math.Min(255, Math.Round(a / total));
    }

    private static Glyph Region(int x, int y, int w, int h) => new()
    {
        Width = w,
        Height = h,
        U0 = (float)x / Size,
        V0 = (float)y / Size,
        U1 = (float)(x + w) / Size,
        V1 = (float)(y + h) / Size,
    };

    /// <summary>
    /// Nine-slice mask for a rounded rectangle with the given corner radius; with blur &gt; 0 the edge
    /// fades out over ±blur pixels (a soft shadow). Crisp masks get a 1px analytic anti-aliased edge.
    /// </summary>
    public Mask GetMask(int radius, int blur)
    {
        radius = Math.Clamp(radius, 0, 96);
        blur = Math.Clamp(blur, 0, 96);
        int key = radius << 8 | blur;
        if (_masks.TryGetValue(key, out var mask)) return mask;

        int corner = radius + blur + 1;
        int n = 2 * corner + 2;
        Allocate(n, n, out int x, out int y);

        int needed = n * n * 4;
        if (_scratch.Length < needed) _scratch = new byte[needed];
        // The shape is a (2r+2)-px rounded square centred in the mask, leaving blur+1 px around it.
        float center = n / 2f, half = radius + 1;
        fixed (byte* dst = _scratch)
        {
            for (int py = 0; py < n; py++)
            {
                for (int px = 0; px < n; px++)
                {
                    // Signed distance from the pixel center to a rounded box (negative inside).
                    float qx = MathF.Abs(px + 0.5f - center) - (half - radius);
                    float qy = MathF.Abs(py + 0.5f - center) - (half - radius);
                    float outside = MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0));
                    float d = outside + MathF.Min(MathF.Max(qx, qy), 0) - radius;

                    float a = blur > 0
                        ? 1 - SmoothStep(-blur, blur, d)
                        : Math.Clamp(0.5f - d, 0, 1);
                    byte* p = dst + (py * n + px) * 4;
                    p[0] = p[1] = p[2] = 255;
                    p[3] = (byte)MathF.Round(a * 255);
                }
            }
            var rect = new Sdl.Rect { X = x, Y = y, W = n, H = n };
            Sdl.UpdateTexture(Texture, &rect, dst, n * 4);
        }

        mask = new Mask { Corner = corner, Blur = blur, Pixels = n, U0 = (float)x / Size, V0 = (float)y / Size };
        _masks[key] = mask;
        return mask;
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Reserves a w×h region (1px gutter), flushing the atlas first when it is full.</summary>
    private void Allocate(int w, int h, out int x, out int y)
    {
        if (_penX + w + 1 > Size)
        {
            _penY += _rowHeight + 1;
            _penX = 0;
            _rowHeight = 0;
        }
        if (_penY + h + 1 > Size) Clear();
        x = _penX;
        y = _penY;
        _penX += w + 1;
        _rowHeight = Math.Max(_rowHeight, h);
    }

    public void Dispose() => Sdl.DestroyTexture(Texture);
}
