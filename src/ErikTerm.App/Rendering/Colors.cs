namespace ErikTerm.App.Rendering;

internal static class Colors
{
    public static uint Mix(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        int r = (int)MathF.Round(R(a) + (R(b) - R(a)) * t);
        int g = (int)MathF.Round(G(a) + (G(b) - G(a)) * t);
        int bl = (int)MathF.Round(B(a) + (B(b) - B(a)) * t);
        return (uint)(r << 16 | g << 8 | bl);
    }

    public static int R(uint c) => (int)(c >> 16) & 0xFF;
    public static int G(uint c) => (int)(c >> 8) & 0xFF;
    public static int B(uint c) => (int)c & 0xFF;

    public static float Luminance(uint c) => (0.2126f * R(c) + 0.7152f * G(c) + 0.0722f * B(c)) / 255f;

    /// <summary>wezterm-style HSB adjustment: multiplies hue, saturation and brightness.</summary>
    public static uint AdjustHsb(uint rgb, float hue, float saturation, float brightness)
    {
        float r = R(rgb) / 255f, g = G(rgb) / 255f, b = B(rgb) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float v = max, d = max - min;
        float s = max <= 0 ? 0 : d / max;
        float h = 0;
        if (d > 0)
        {
            if (r >= g && r >= b) h = (g - b) / d % 6;
            else if (g >= b) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }

        h = h * hue % 360;
        s = Math.Clamp(s * saturation, 0, 1);
        v = Math.Clamp(v * brightness, 0, 1);

        float c = v * s, x = c * (1 - MathF.Abs(h / 60 % 2 - 1)), m = v - c;
        (float r1, float g1, float b1) = (h / 60) switch
        {
            < 1 => (c, x, 0f),
            < 2 => (x, c, 0f),
            < 3 => (0f, c, x),
            < 4 => (0f, x, c),
            < 5 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return (uint)((int)MathF.Round((r1 + m) * 255) << 16 | (int)MathF.Round((g1 + m) * 255) << 8 | (int)MathF.Round((b1 + m) * 255));
    }
}

/// <summary>Small direct-mapped cache for per-color transforms (inactive pane dimming) — cells share few colors.</summary>
internal sealed class ColorTransformCache
{
    private readonly uint[] _keys = new uint[512];
    private readonly uint[] _values = new uint[512];
    private readonly bool[] _used = new bool[512];
    private float _h = 1, _s = 1, _b = 1;

    public void Configure(float hue, float saturation, float brightness)
    {
        #pragma warning disable S1244 // change detection against the stored copies: any other value must rebuild the cache
        if (hue == _h && saturation == _s && brightness == _b) return;
        #pragma warning restore S1244
        (_h, _s, _b) = (hue, saturation, brightness);
        Array.Clear(_used);
    }

    #pragma warning disable S1244 // the configured factors are exactly 1 when dimming is off; anything else must still transform
    public bool IsIdentity => _h == 1 && _s == 1 && _b == 1;
    #pragma warning restore S1244

    public uint Apply(uint rgb)
    {
        if (IsIdentity) return rgb;
        int slot = (int)((rgb * 2654435761u) >> 23) & 511;
        if (_used[slot] && _keys[slot] == rgb) return _values[slot];
        uint v = Colors.AdjustHsb(rgb, _h, _s, _b);
        _keys[slot] = rgb;
        _values[slot] = v;
        _used[slot] = true;
        return v;
    }
}
