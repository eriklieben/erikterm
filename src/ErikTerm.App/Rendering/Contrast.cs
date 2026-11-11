namespace ErikTerm.App.Rendering;

/// <summary>
/// minimum_contrast: WCAG contrast ratios and the foreground adjustment. A color with too little contrast against its
/// background moves toward white or black — the side it is already on, unless only the other side can reach the
/// ratio — just far enough (iTerm2-style, so it keeps its hue; ghostty jumps straight to white or black). Box drawing,
/// block elements, Symbols for Legacy Computing and powerline glyphs are left alone, as ghostty does: TUIs draw borders
/// and backgrounds with them, often in colors close to (or equal to) the background on purpose.
/// </summary>
internal static class Contrast
{
    private static readonly float[] Linear = BuildLinear();

    private static float[] BuildLinear()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            t[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return t;
    }

    /// <summary>WCAG relative luminance of 0xRRGGBB (0 black … 1 white).</summary>
    public static float Luminance(uint rgb) =>
        0.2126f * Linear[(rgb >> 16) & 0xFF] + 0.7152f * Linear[(rgb >> 8) & 0xFF] + 0.0722f * Linear[rgb & 0xFF];

    /// <summary>WCAG contrast ratio, 1 (same) … 21 (black on white).</summary>
    public static float Ratio(uint a, uint b) => Ratio(Luminance(a), Luminance(b));

    private static float Ratio(float la, float lb) => la >= lb ? (la + 0.05f) / (lb + 0.05f) : (lb + 0.05f) / (la + 0.05f);

    /// <summary>
    /// <paramref name="fg"/>, or the nearest color toward white or black that has <paramref name="minimum"/> contrast with
    /// <paramref name="bg"/> (or as much as either can: white or black itself).
    /// </summary>
    public static uint Adjust(uint fg, uint bg, float minimum)
    {
        float lf = Luminance(fg), lb = Luminance(bg);
        if (Ratio(lf, lb) >= minimum) return fg;
        bool lighterReaches = (lb + 0.05f) * minimum - 0.05f <= 1;
        bool darkerReaches = (lb + 0.05f) / minimum - 0.05f >= 0;
        bool lighter = lf >= lb
            ? lighterReaches || !darkerReaches && Ratio(1f, lb) >= Ratio(0f, lb)
            : !darkerReaches && (lighterReaches || Ratio(1f, lb) >= Ratio(0f, lb));
        uint target = lighter ? 0xFFFFFFu : 0x000000u;
        if (Ratio(lighter ? 1f : 0f, lb) < minimum) return target; // as far as it goes
        // The smallest step toward the target that reaches the ratio (luminance grows monotonically along the way).
        float lo = 0, hi = 1;
        for (int i = 0; i < 12; i++)
        {
            float mid = (lo + hi) / 2;
            if (Ratio(Luminance(Colors.Mix(fg, target, mid)), lb) >= minimum) hi = mid;
            else lo = mid;
        }
        return Colors.Mix(fg, target, hi);
    }

    /// <summary>Glyphs minimum_contrast leaves alone: box drawing, block elements, legacy computing, powerline.</summary>
    public static bool IsGraphic(int rune) =>
        rune is >= 0x2500 and <= 0x259F       // box drawing, block elements
            or >= 0x1FB00 and <= 0x1FBFF      // symbols for legacy computing
            or >= 0xE0A0 and <= 0xE0D7;       // powerline (and its extra symbols)
}

/// <summary>
/// The adjusted foreground per (foreground, background) pair for one minimum_contrast — direct-mapped like
/// <see cref="ColorTransformCache"/>, so the row builder doesn't redo the search for every cell. Allocation-free.
/// </summary>
internal sealed class ContrastCache
{
    private const int Size = 1024;
    private readonly ulong[] _keys = new ulong[Size];
    private readonly uint[] _values = new uint[Size];
    private readonly bool[] _used = new bool[Size];
    private float _minimum = 1;

    /// <summary>Whether there is anything to do (minimum_contrast above 1).</summary>
    public bool Enabled => _minimum > 1;

    public void Configure(float minimum)
    {
        #pragma warning disable S1244 // change detection against the stored copy: any other value must clear the cache
        if (minimum == _minimum) return;
        #pragma warning restore S1244
        _minimum = minimum;
        Array.Clear(_used);
    }

    public uint Apply(uint fg, uint bg)
    {
        if (_minimum <= 1) return fg;
        ulong key = (ulong)fg << 32 | bg;
        int slot = (int)((key * 0x9E3779B97F4A7C15UL) >> 54) & (Size - 1);
        if (_used[slot] && _keys[slot] == key) return _values[slot];
        uint v = Contrast.Adjust(fg, bg, _minimum);
        _keys[slot] = key;
        _values[slot] = v;
        _used[slot] = true;
        return v;
    }
}
