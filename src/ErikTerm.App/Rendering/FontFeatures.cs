namespace ErikTerm.App.Rendering;

/// <summary>One OpenType feature setting, e.g. <c>calt=0</c>: a 4-byte tag and a value (0 disables).</summary>
internal readonly record struct FontFeature(uint Tag, uint Value)
{
    public static uint MakeTag(ReadOnlySpan<char> name)
    {
        uint tag = 0;
        for (int i = 0; i < 4; i++) tag = tag << 8 | (i < name.Length ? (byte)name[i] : (byte)' ');
        return tag;
    }

    public override string ToString()
    {
        Span<char> name = [(char)(Tag >> 24), (char)(Tag >> 16 & 0xFF), (char)(Tag >> 8 & 0xFF), (char)(Tag & 0xFF)];
        return $"{name.TrimEnd(' ')}={Value}";
    }
}

/// <summary>
/// Parses wezterm's <c>harfbuzz_features</c> strings (HarfBuzz's feature syntax without ranges):
/// <c>"calt"</c>, <c>"+calt"</c>, <c>"-calt"</c>, <c>"calt=0"</c>, <c>"ss01=1"</c>, <c>"liga=off"</c>.
/// No dependencies, so it is unit-tested directly.
/// </summary>
internal static class FontFeatures
{
    public static readonly IReadOnlyList<string> Default = ["calt=1", "liga=1", "clig=1"];

    /// <summary>Parses every entry; invalid ones are skipped and described in <paramref name="errors"/>.</summary>
    public static FontFeature[] Parse(IReadOnlyList<string> settings, List<string>? errors = null)
    {
        var result = new List<FontFeature>(settings.Count);
        foreach (var s in settings)
        {
            if (TryParse(s, out var f)) result.Add(f);
            else errors?.Add($"harfbuzz_features: cannot parse \"{s}\" (expected e.g. \"calt=0\" or \"+ss01\")");
        }
        return [.. result];
    }

    public static bool TryParse(string text, out FontFeature feature)
    {
        feature = default;
        var s = text.AsSpan().Trim();
        uint value = 1;
        if (s.Length > 0 && (s[0] == '+' || s[0] == '-'))
        {
            value = s[0] == '+' ? 1u : 0u;
            s = s[1..].TrimStart();
        }

        int eq = s.IndexOf('=');
        var name = (eq >= 0 ? s[..eq] : s).Trim();
        if (!IsFeatureName(name)) return false;

        if (eq >= 0)
        {
            if (text.AsSpan().TrimStart().StartsWith("-") || text.AsSpan().TrimStart().StartsWith("+")) return false; // "+calt=0" is ambiguous
            if (!TryParseValue(s[(eq + 1)..].Trim(), ref value)) return false;
        }

        feature = new FontFeature(FontFeature.MakeTag(name), value);
        return true;
    }

    private static bool IsFeatureName(ReadOnlySpan<char> name)
    {
        if (name.Length is 0 or > 4) return false;
        foreach (char c in name)
            if (c is <= ' ' or > '~' or '[' or ']' or '"' or '\'') return false;
        return true;
    }

    private static bool TryParseValue(ReadOnlySpan<char> v, ref uint value)
    {
        if (uint.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint n)) value = n;
        else if (v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase)) value = 1;
        else if (v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)) value = 0;
        else return false;
        return true;
    }
}
