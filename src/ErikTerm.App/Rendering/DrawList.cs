using System.Text;
using ErikTerm.App.Native;
using ErikTerm.Ui;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Immediate-mode 2D painter. Everything — cell backgrounds, glyphs, rounded panels, shadows — is a
/// textured quad from the one atlas, appended in painter's order. A new batch starts only when the
/// clip rectangle changes, so a frame is a handful of SDL_RenderGeometry calls. Images
/// (<see cref="Image"/>) have textures of their own: each starts a batch bound to its texture.
/// </summary>
#pragma warning disable S6640 // vertex and index arrays are pinned for SDL_RenderGeometryRaw every frame
internal sealed unsafe class DrawList
#pragma warning restore S6640
{
    private struct Batch
    {
        public int StartQuad;
        public bool HasClip;
        public Sdl.Rect Clip;
        public nint Texture; // 0 = the atlas
        public int ScaleMode;
    }

    private readonly nint _renderer;
    private Sdl.Vertex[] _vertices = new Sdl.Vertex[32 * 1024];
    private int _quads;
    private readonly List<Batch> _batches = [];
    private readonly Stack<RectF?> _clipStack = new();
    private RectF? _clip;
    private int[] _indices = [];

    public GlyphAtlas Atlas { get; set; }
    public FontSet Fonts { get; set; }

    /// <summary>Debug: when set, the next presented frame is also saved to this .bmp path.</summary>
    public string? CapturePath { get; set; }

    // Debug recorder for the README demos (scripts/demos): ERIKTERM_RECORD_DIR saves presented frames as <ms>.bmp, at
    // most one per ERIKTERM_RECORD_MIN_MS, between the debug script's record:on and record:off.
    private static readonly string? RecordDir = Environment.GetEnvironmentVariable("ERIKTERM_RECORD_DIR") is { Length: > 0 } rd ? rd : null;
    private static readonly int RecordMinMs = int.TryParse(Environment.GetEnvironmentVariable("ERIKTERM_RECORD_MIN_MS"), out int rm) ? rm : 25;
    private static readonly System.Diagnostics.Stopwatch RecordClock = System.Diagnostics.Stopwatch.StartNew();
    private long _recordLast = -100000;
    private static bool _recordOn;

    /// <summary>Debug script record:on / record:off; leaves a &lt;ms&gt;.on / .off marker next to the frames.</summary>
    public static void Record(bool on)
    {
        if (RecordDir is null) return;
        _recordOn = on;
        File.WriteAllText(Path.Combine(RecordDir, RecordClock.ElapsedMilliseconds.ToString("D8") + (on ? ".on" : ".off")), "");
    }
    /// <summary>A frame was presented but not recorded (too soon after the last): draw again so the final state lands.</summary>
    public bool RecordSkipped { get; private set; }

    /// <summary>The SDL renderer, for creating textures (images).</summary>
    public nint Renderer => _renderer;

    public DrawList(nint renderer, GlyphAtlas atlas, FontSet fonts)
    {
        _renderer = renderer;
        Atlas = atlas;
        Fonts = fonts;
    }

    public void Begin()
    {
        _quads = 0;
        _batches.Clear();
        _clipStack.Clear();
        _clip = null;
        StartBatch();
    }

    private void StartBatch(nint texture = 0, int scaleMode = 0)
    {
        var b = new Batch { StartQuad = _quads, Texture = texture, ScaleMode = scaleMode };
        if (_clip is { } c)
        {
            b.HasClip = true;
            int x0 = (int)MathF.Floor(c.X), y0 = (int)MathF.Floor(c.Y);
            b.Clip = new Sdl.Rect { X = x0, Y = y0, W = Math.Max(0, (int)MathF.Ceiling(c.Right) - x0), H = Math.Max(0, (int)MathF.Ceiling(c.Bottom) - y0) };
        }
        if (_batches.Count > 0 && _batches[^1].StartQuad == _quads) _batches[^1] = b;
        else _batches.Add(b);
    }

    public void PushClip(RectF r)
    {
        _clipStack.Push(_clip);
        if (_clip is { } c)
        {
            float x0 = MathF.Max(c.X, r.X), y0 = MathF.Max(c.Y, r.Y);
            float x1 = MathF.Min(c.Right, r.Right), y1 = MathF.Min(c.Bottom, r.Bottom);
            r = new RectF(x0, y0, MathF.Max(0, x1 - x0), MathF.Max(0, y1 - y0));
        }
        _clip = r;
        StartBatch();
    }

    public void PopClip()
    {
        _clip = _clipStack.Count > 0 ? _clipStack.Pop() : null;
        StartBatch();
    }

    // ---- Primitives ---------------------------------------------------------------------------

    /// <summary>A quad from (x0, y0) to (x1, y1) showing the texture between <paramref name="uv"/>'s corners, tinted <paramref name="rgb"/>.</summary>
    public void Quad(float x0, float y0, float x1, float y1, in QuadUv uv, uint rgb, float a)
    {
        if (a <= 0.002f) return;
        int i = _quads * 4;
        if (i + 4 > _vertices.Length) Array.Resize(ref _vertices, _vertices.Length * 2);
        float u0 = uv.U0, v0 = uv.V0, u1 = uv.U1, v1 = uv.V1;
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, b = (rgb & 0xFF) / 255f;
        ref var v = ref _vertices[i];
        v = new Sdl.Vertex { X = x0, Y = y0, R = r, G = g, B = b, A = a, U = u0, V = v0 };
        _vertices[i + 1] = new Sdl.Vertex { X = x1, Y = y0, R = r, G = g, B = b, A = a, U = u1, V = v0 };
        _vertices[i + 2] = new Sdl.Vertex { X = x1, Y = y1, R = r, G = g, B = b, A = a, U = u1, V = v1 };
        _vertices[i + 3] = new Sdl.Vertex { X = x0, Y = y1, R = r, G = g, B = b, A = a, U = u0, V = v1 };
        _quads++;
    }

    // ---- Recording (PaneRenderer caches each row's quads and replays them while the row is unchanged) ----

    /// <summary>Quads emitted so far this frame.</summary>
    public int QuadCount => _quads;

    /// <summary>
    /// Copies quads [<paramref name="startQuad"/>, <paramref name="endQuad"/>) into <paramref name="dst"/> as they are
    /// (replay them with <see cref="AppendQuads"/>, moved by the difference to where they were built). Returns the count.
    /// </summary>
    public int CopyQuads(int startQuad, int endQuad, ref Sdl.Vertex[] dst)
    {
        int n = (endQuad - startQuad) * 4;
        if (dst.Length < n) dst = new Sdl.Vertex[Math.Max(n, dst.Length * 2)];
        _vertices.AsSpan(startQuad * 4, n).CopyTo(dst);
        return endQuad - startQuad;
    }

    /// <summary>Multiplies the alpha of quads [<paramref name="startQuad"/>, <paramref name="endQuad"/>) by <paramref name="a"/>.</summary>
    public void ScaleAlpha(int startQuad, int endQuad, float a)
    {
        #pragma warning disable S1244 // exact opacity 1 is the no-op; any other value must scale
        if (a == 1) return;
        #pragma warning restore S1244
        var span = _vertices.AsSpan(startQuad * 4, (endQuad - startQuad) * 4);
        for (int i = 0; i < span.Length; i++) span[i].A *= a;
    }

    /// <summary>Appends recorded quads (see <see cref="CopyQuads"/>) moved by (dx, dy), alpha multiplied by <paramref name="a"/>.</summary>
    public void AppendQuads(ReadOnlySpan<Sdl.Vertex> quads, float dx, float dy, float a)
    {
        if (quads.IsEmpty || a <= 0.002f) return;
        int i = _quads * 4;
        if (i + quads.Length > _vertices.Length) Array.Resize(ref _vertices, Math.Max(_vertices.Length * 2, i + quads.Length));
        var dst = _vertices.AsSpan(i, quads.Length);
        quads.CopyTo(dst);
        #pragma warning disable S1244 // exact opacity 1 and zero offsets are the no-op; any other value must transform
        if (dx != 0 || dy != 0 || a != 1)
        #pragma warning restore S1244
        {
            for (int k = 0; k < dst.Length; k++)
            {
                ref var v = ref dst[k];
                v.X += dx;
                v.Y += dy;
                v.A *= a;
            }
        }
        _quads += quads.Length / 4;
    }

    public void Rect(RectF r, uint rgb, float a = 1) =>
        Quad(r.X, r.Y, r.Right, r.Bottom, in GlyphAtlas.SolidUv, rgb, a);

    public void Rect(float x0, float y0, float x1, float y1, uint rgb, float a = 1) =>
        Quad(x0, y0, x1, y1, in GlyphAtlas.SolidUv, rgb, a);

    /// <summary>Anti-aliased rounded rectangle.</summary>
    public void RoundRect(RectF r, float radius, uint rgb, float a = 1) => NineSlice(r, radius, 0, rgb, a);

    /// <summary>Soft shadow of a rounded rectangle, fading out over ±blur pixels.</summary>
    public void Shadow(RectF r, float radius, float blur, float a, uint rgb = 0) => NineSlice(r, radius, blur, rgb, a);

    /// <summary>
    /// Anti-aliased line with round caps from (x0, y0) to (x1, y1): a pill-shaped rounded rect rotated onto the
    /// segment. <paramref name="width"/> is rounded to an even number of pixels (the masks have whole-pixel radii).
    /// </summary>
    public void Capsule(float x0, float y0, float x1, float y1, float width, uint rgb, float a = 1)
    {
        float radius = MathF.Max(1, MathF.Round(width / 2));
        float dx = x1 - x0, dy = y1 - y0, len = MathF.Sqrt(dx * dx + dy * dy);
        float cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        int first = _quads;
        RoundRect(new RectF(cx - len / 2 - radius, cy - radius, len + 2 * radius, 2 * radius), radius, rgb, a);
        if (len < 0.01f) return;
        float cos = dx / len, sin = dy / len;
        foreach (ref var v in _vertices.AsSpan(first * 4, (_quads - first) * 4))
        {
            float px = v.X - cx, py = v.Y - cy;
            v.X = cx + px * cos - py * sin;
            v.Y = cy + px * sin + py * cos;
        }
    }

    /// <summary>Rounded panel with a 1px border: border color underneath, fill inset on top.</summary>
    public void Panel(RectF r, float radius, uint fill, float fillAlpha, uint border, float borderAlpha, float borderWidth = 1)
    {
        if (borderAlpha > 0) RoundRect(r, radius, border, borderAlpha);
        RoundRect(r.Inset(borderWidth), MathF.Max(0, radius - borderWidth), fill, fillAlpha);
    }

    /// <summary>
    /// A rounded outline with nothing inside it, the line just inside <paramref name="r"/>. The corners are drawn pixel by
    /// pixel: the atlas shapes are all filled, and what's behind a translucent pane has no single color to paint over them.
    /// </summary>
    public void RoundedOutline(RectF r, float radius, float width, uint rgb, float a = 1)
    {
        if (a <= 0.002f) return;
        float left = MathF.Round(r.X), top = MathF.Round(r.Y), right = MathF.Round(r.Right), bottom = MathF.Round(r.Bottom);
        int rad = (int)MathF.Ceiling(MathF.Max(width, MathF.Min(radius, MathF.Min(right - left, bottom - top) / 2)));
        Rect(left + rad, top, right - rad, top + width, rgb, a);
        Rect(left + rad, bottom - width, right - rad, bottom, rgb, a);
        Rect(left, top + rad, left + width, bottom - rad, rgb, a);
        Rect(right - width, top + rad, right, bottom - rad, rgb, a);
        for (int y = 0; y < rad; y++)
            for (int x = 0; x < rad; x++)
            {
                float coverage = global::ErikTerm.Ui.RoundedOutline.Coverage(rad - x - 0.5f, rad - y - 0.5f, rad, width);
                if (coverage <= 0.02f) continue;
                float near = left + x, far = right - x - 1, high = top + y, low = bottom - y - 1, alpha = a * coverage;
                Rect(near, high, near + 1, high + 1, rgb, alpha);
                Rect(far, high, far + 1, high + 1, rgb, alpha);
                Rect(near, low, near + 1, low + 1, rgb, alpha);
                Rect(far, low, far + 1, low + 1, rgb, alpha);
            }
    }

    private void NineSlice(RectF r, float radius, float blur, uint rgb, float a)
    {
        if (r.W <= 0 || r.H <= 0 || a <= 0.002f) return;
        int rad = (int)MathF.Round(MathF.Min(radius, MathF.Min(r.W, r.H) / 2));
        var m = Atlas.GetMask(rad, (int)MathF.Round(blur));
        float e = m.Blur + 1; // mask margin outside the shape
        float c = m.Corner;
        float x0 = r.X - e, x3 = r.Right + e, y0 = r.Y - e, y3 = r.Bottom + e;
        float x1 = x0 + c, x2 = x3 - c, y1 = y0 + c, y2 = y3 - c;
        float t = 1f / GlyphAtlas.Size;
        float u0 = m.U0, u1 = m.U0 + c * t, u2 = m.U0 + (c + 2) * t, u3 = m.U0 + m.Pixels * t;
        float v0 = m.V0, v1 = m.V0 + c * t, v2 = m.V0 + (c + 2) * t, v3 = m.V0 + m.Pixels * t;

        ReadOnlySpan<float> xs = [x0, x1, x2, x3], ys = [y0, y1, y2, y3];
        ReadOnlySpan<float> us = [u0, u1, u2, u3], vs = [v0, v1, v2, v3];
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                if (xs[col + 1] > xs[col] && ys[row + 1] > ys[row])
                    Quad(xs[col], ys[row], xs[col + 1], ys[row + 1], new QuadUv(us[col], vs[row], us[col + 1], vs[row + 1]), rgb, a);
    }

    /// <summary>
    /// Draws a quad of another texture (an image) at <paramref name="at"/> with UVs <paramref name="uv"/>, tinted by
    /// <paramref name="tint"/>, in painter's order with everything else and inside the current clip. Consecutive
    /// quads of the same texture share a batch.
    /// </summary>
    public void Image(nint texture, in QuadCorners at, in QuadUv uv, uint tint, float a, int scaleMode)
    {
        float x0 = at.X0, y0 = at.Y0, x1 = at.X1, y1 = at.Y1;
        if (a <= 0.002f || texture == 0 || x1 <= x0 || y1 <= y0) return;
        // Continue the previous image batch when nothing else was drawn since (the empty atlas batch after it).
        if (_batches.Count >= 2 && _batches[^1].StartQuad == _quads && _batches[^1].Texture == 0
            && _batches[^2].Texture == texture && _batches[^2].ScaleMode == scaleMode && SameClip(_batches[^1], _batches[^2]))
            _batches.RemoveAt(_batches.Count - 1);
        else StartBatch(texture, scaleMode);
        Quad(x0, y0, x1, y1, in uv, tint, a);
        StartBatch();
    }

    private static bool SameClip(in Batch a, in Batch b) =>
        a.HasClip == b.HasClip && (!a.HasClip || (a.Clip.X == b.Clip.X && a.Clip.Y == b.Clip.Y && a.Clip.W == b.Clip.W && a.Clip.H == b.Clip.H));

    // ---- Text ---------------------------------------------------------------------------------

    /// <summary>Draws one codepoint with its pen at (x, baseline); returns the advance.</summary>
    public float Glyph(int codepoint, float x, float baseline, float px, uint rgb, float a, FontStyle style = FontStyle.Regular)
    {
        var g = Atlas.Get(codepoint, style, px);
        if (!g.Empty)
        {
            float gx = MathF.Round(x) + g.Left, gy = MathF.Round(baseline) - g.Top;
            Quad(gx, gy, gx + g.Width, gy + g.Height, in g.Uv, g.Color ? 0xFFFFFFu : rgb, a); // color emoji: untinted
        }
        return g.Advance;
    }

    /// <summary>Draws text with its pen at (x, baseline). Returns the width.</summary>
    public float Text(string text, float x, float baseline, float px, uint rgb, float a, FontStyle style = FontStyle.Regular) =>
        Text(text, x, baseline, px, rgb, a, new TextHighlight(style, null, 0));

    /// <summary>
    /// Draws text in <see cref="TextHighlight.Style"/> with its pen at (x, baseline). Characters whose index is in
    /// <see cref="TextHighlight.Positions"/> are drawn in <see cref="TextHighlight.Rgb"/> and bold. Returns the width.
    /// </summary>
    public float Text(string text, float x, float baseline, float px, uint rgb, float a, in TextHighlight highlighted)
    {
        var style = highlighted.Style;
        var highlight = highlighted.Positions;
        uint highlightRgb = highlighted.Rgb;
        float pen = x;
        int index = 0, h = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            bool hi = false;
            if (highlight is not null)
            {
                while (h < highlight.Count && highlight[h] < index) h++;
                hi = h < highlight.Count && highlight[h] == index;
            }
            pen += Glyph(rune.Value, pen, baseline, px, hi ? highlightRgb : rgb, a, hi ? FontStyle.Bold : style);
            index += rune.Utf16SequenceLength;
        }
        return pen - x;
    }

    public float Measure(string text, float px, FontStyle style = FontStyle.Regular)
    {
        float w = 0;
        foreach (var rune in text.EnumerateRunes()) w += Atlas.Get(rune.Value, style, px).Advance;
        return w;
    }

    /// <summary>Truncates with an ellipsis so the text fits in maxWidth.</summary>
    public string Ellipsize(string text, float px, float maxWidth, FontStyle style = FontStyle.Regular)
    {
        if (Measure(text, px, style) <= maxWidth) return text;
        float ell = Measure("…", px, style);
        var sb = new StringBuilder();
        float w = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            float adv = Atlas.Get(rune.Value, style, px).Advance;
            if (w + adv + ell > maxWidth) break;
            sb.Append(rune.ToString());
            w += adv;
        }
        return sb.Append('…').ToString();
    }

    /// <summary>Vertical metrics for UI text at a pixel size.</summary>
    public FontSet.Metrics MetricsFor(float px) => Fonts.MetricsFor(px);

    // ---- Submit -------------------------------------------------------------------------------

    private void SubmitBatch(int i, Sdl.Vertex* verts, int* idx)
    {
        var b = _batches[i];
        int end = i + 1 < _batches.Count ? _batches[i + 1].StartQuad : _quads;
        int count = end - b.StartQuad;
        if (count <= 0) return;
        if (b.HasClip)
        {
            var clip = b.Clip;
            if (clip.W <= 0 || clip.H <= 0) return;
            Sdl.SetRenderClipRect(_renderer, &clip);
        }
        else Sdl.SetRenderClipRect(_renderer, null);
        nint texture = Atlas.Texture;
        if (b.Texture != 0)
        {
            texture = b.Texture;
            Sdl.SetTextureScaleMode(texture, b.ScaleMode);
        }
        Sdl.RenderGeometry(_renderer, texture, verts + b.StartQuad * 4, count * 4, idx, count * 6);
    }

    public void Present(uint clearRgb, float clearAlpha)
    {
        EnsureIndices(_quads);
        float a = Math.Clamp(clearAlpha, 0, 1);
        // Wayland compositors expect premultiplied alpha.
        Sdl.SetRenderDrawColor(_renderer,
            (byte)(Colors.R(clearRgb) * a), (byte)(Colors.G(clearRgb) * a), (byte)(Colors.B(clearRgb) * a), (byte)MathF.Round(a * 255));
        Sdl.SetRenderClipRect(_renderer, null);
        Sdl.RenderClear(_renderer);

        fixed (Sdl.Vertex* verts = _vertices)
        fixed (int* idx = _indices)
        {
            for (int i = 0; i < _batches.Count; i++) SubmitBatch(i, verts, idx);
        }
        Sdl.SetRenderClipRect(_renderer, null);

        if (CapturePath is { } path)
        {
            CapturePath = null;
            nint surface = Sdl.RenderReadPixels(_renderer, null);
            if (surface != 0)
            {
                Sdl.SaveBMP(surface, path);
                Sdl.DestroySurface(surface);
            }
        }
        RecordSkipped = false;
        if (RecordDir is not null && _recordOn)
        {
            long ms = RecordClock.ElapsedMilliseconds;
            if (ms - _recordLast >= RecordMinMs)
            {
                _recordLast = ms;
                nint surface = Sdl.RenderReadPixels(_renderer, null);
                if (surface != 0)
                {
                    Sdl.SaveBMP(surface, Path.Combine(RecordDir, ms.ToString("D8") + ".bmp"));
                    Sdl.DestroySurface(surface);
                }
            }
            else RecordSkipped = true;
        }
        Sdl.RenderPresent(_renderer);
    }

    private void EnsureIndices(int quads)
    {
        if (_indices.Length >= quads * 6) return;
        int capacity = Math.Max(quads, Math.Max(4096, _indices.Length / 6 * 2));
        _indices = new int[capacity * 6];
        for (int q = 0; q < capacity; q++)
        {
            int v = q * 4, i = q * 6;
            _indices[i] = v; _indices[i + 1] = v + 1; _indices[i + 2] = v + 2;
            _indices[i + 3] = v + 2; _indices[i + 4] = v + 3; _indices[i + 5] = v;
        }
    }
}

/// <summary>Texture coordinates of a quad: (U0, V0) at its top-left corner, (U1, V1) at its bottom-right.</summary>
internal readonly record struct QuadUv(float U0, float V0, float U1, float V1);

/// <summary>A quad's screen corners: (X0, Y0) top-left, (X1, Y1) bottom-right, in pixels.</summary>
internal readonly record struct QuadCorners(float X0, float Y0, float X1, float Y1);

/// <summary>
/// How <see cref="DrawList.Text(string, float, float, float, uint, float, in TextHighlight)"/> draws a string: in
/// <see cref="Style"/>, with the characters at <see cref="Positions"/> (UTF-16 indices, ascending) bold in <see cref="Rgb"/>.
/// </summary>
internal readonly record struct TextHighlight(FontStyle Style, List<int>? Positions, uint Rgb);
