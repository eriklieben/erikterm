using ErikTerm.Core;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Geometry and coverage bitmaps of the SGR 4:n underline styles, at the exact cell size (like <see cref="BoxDrawing"/>,
/// in device pixels, so HiDPI is just a bigger cell). Single and double are pixel-snapped rectangles; curly, dotted and
/// dashed are one cell wide strips the atlas caches per (style, cell size, thickness), drawn once per cell so the
/// pattern continues across cells. No dependencies: unit-tested directly.
/// </summary>
internal static class Underlines
{
    /// <summary>The strip a patterned style occupies: rows [Top, Top + Height) of the cell.</summary>
    public static (int Top, int Height) Strip(UnderlineStyle style, int cellHeight, int underlineY, int thickness, int baseline)
    {
        int t = Math.Max(1, thickness);
        if (style == UnderlineStyle.Curly)
        {
            // From just under the baseline to the bottom of the cell (the descent), at least three strokes tall.
            int height = Math.Clamp(cellHeight - baseline, 3 * t, Math.Max(3 * t, cellHeight / 3));
            height = Math.Min(height, cellHeight);
            int top = Math.Clamp(Math.Min(underlineY - t / 2, cellHeight - height), 0, Math.Max(0, cellHeight - height));
            return (top, height);
        }
        int y = Math.Clamp(underlineY, 0, Math.Max(0, cellHeight - t));
        return (y, Math.Min(t, cellHeight));
    }

    /// <summary>The two lines of a double underline: (first top, second top); each is <paramref name="thickness"/> tall.</summary>
    public static (int First, int Second) DoubleLines(int cellHeight, int underlineY, int thickness)
    {
        int t = Math.Max(1, thickness);
        int first = underlineY, second = underlineY + 2 * t;
        if (second + t > cellHeight)
        {
            second = Math.Max(0, cellHeight - t);
            first = Math.Max(0, second - 2 * t);
        }
        return (first, second);
    }

    /// <summary>
    /// Renders a curly, dotted or dashed underline strip (<paramref name="width"/> x <paramref name="height"/> coverage
    /// bytes, row-major) into <paramref name="alpha"/>. False for the other styles.
    /// </summary>
    public static bool Render(UnderlineStyle style, int width, int height, int thickness, Span<byte> alpha)
    {
        if (width <= 0 || height <= 0) return false;
        alpha[..(width * height)].Clear();
        float t = Math.Max(1, thickness);
        switch (style)
        {
            case UnderlineStyle.Curly: RenderCurly(width, height, t, alpha); return true;
            case UnderlineStyle.Dotted: RenderDotted(width, height, t, alpha); return true;
            case UnderlineStyle.Dashed: RenderDashed(width, height, alpha); return true;
        }
        return false;
    }

    /// <summary>
    /// One period of a sine per cell; anti-aliased by the distance to the curve (vertical distance over the slope's
    /// length), so the stroke is about t wide everywhere.
    /// </summary>
    private static void RenderCurly(int width, int height, float t, Span<byte> alpha)
    {
        float amplitude = MathF.Max(0, (height - t) / 2f - 0.5f);
        float center = height / 2f;
        float k = 2 * MathF.PI / width;
        for (int x = 0; x < width; x++)
        {
            // Sample the pixel's center column; the curve starts at its crest so neighbouring cells join.
            float fx = x + 0.5f;
            float y0 = center - amplitude * MathF.Cos(k * fx);
            float slope = amplitude * k * MathF.Sin(k * fx);
            float norm = MathF.Sqrt(1 + slope * slope);
            for (int y = 0; y < height; y++)
            {
                float d = MathF.Abs(y + 0.5f - y0) / norm;
                float a = Math.Clamp(t / 2 + 0.5f - d, 0, 1);
                if (a > 0) alpha[y * width + x] = (byte)MathF.Round(a * 255);
            }
        }
    }

    /// <summary>Square-ish dots one stroke wide with gaps of the same size, evenly spread over the cell.</summary>
    private static void RenderDotted(int width, int height, float t, Span<byte> alpha)
    {
        int dot = Math.Max(1, (int)t);
        int n = Math.Max(1, (int)MathF.Round(width / (2f * dot)));
        float step = width / (float)n;
        for (int i = 0; i < n; i++)
        {
            int x0 = (int)MathF.Round(i * step + (step - dot) / 2);
            for (int x = Math.Max(0, x0); x < Math.Min(width, x0 + dot); x++)
                for (int y = 0; y < height; y++) alpha[y * width + x] = 255;
        }
    }

    /// <summary>One dash per cell, two thirds of it, centred: the gaps meet between cells.</summary>
    private static void RenderDashed(int width, int height, Span<byte> alpha)
    {
        int x0 = (int)MathF.Round(width / 6f), x1 = Math.Max(x0 + 1, (int)MathF.Round(width * 5 / 6f));
        for (int y = 0; y < height; y++)
            for (int x = x0; x < Math.Min(width, x1); x++) alpha[y * width + x] = 255;
    }
}
