namespace ErikTerm.Core;

/// <summary>
/// XParseColor's common forms: <c>rgb:r/g/b</c> (1–4 hex digits per channel), <c>#rgb</c>, <c>#rrggbb</c>,
/// <c>#rrrgggbbb</c>, <c>#rrrrggggbbbb</c>. Color names aren't supported.
/// </summary>
internal static class XColorSpec
{
    public static bool TryParse(ReadOnlySpan<byte> spec, out uint rgb)
    {
        rgb = 0;
        if (spec.StartsWith("rgb:"u8)) return TryParseRgbColor(spec[4..], out rgb);
        if (spec.Length > 1 && spec[0] == '#' && (spec.Length - 1) % 3 == 0 && spec.Length - 1 <= 12)
            return TryParseHashColor(spec, out rgb);
        return false;
    }

    /// <summary><c>r/g/b</c> after the <c>rgb:</c> prefix: each channel is 1–4 hex digits, scaled to a byte.</summary>
    private static bool TryParseRgbColor(ReadOnlySpan<byte> rest, out uint rgb)
    {
        rgb = 0;
        uint result = 0;
        for (int ch = 0; ch < 3; ch++)
        {
            int slash = rest.IndexOf((byte)'/');
            var part = rest;
            if (ch < 2) part = slash < 0 ? default : rest[..slash];
            if (part.IsEmpty || part.Length > 4 || (ch < 2 && slash < 0)) return false;
            if (!TryHex(part, out int v)) return false;
            int max = (1 << (4 * part.Length)) - 1;
            result = result << 8 | (uint)((v * 255 + max / 2) / max);
            rest = ch < 2 ? rest[(slash + 1)..] : default;
        }
        rgb = result;
        return true;
    }

    /// <summary><c>#rgb</c> through <c>#rrrrggggbbbb</c>, already checked for its length.</summary>
    private static bool TryParseHashColor(ReadOnlySpan<byte> spec, out uint rgb)
    {
        rgb = 0;
        int digits = (spec.Length - 1) / 3;
        uint result = 0;
        for (int ch = 0; ch < 3; ch++)
        {
            if (!TryHex(spec.Slice(1 + ch * digits, digits), out int v)) return false;
            // #rgb is r0g0b0 (xterm); longer forms keep their high byte.
            int byteValue = digits == 1 ? v << 4 : v >> (4 * (digits - 2));
            result = result << 8 | (uint)byteValue;
        }
        rgb = result;
        return true;
    }

    private static bool TryHex(ReadOnlySpan<byte> s, out int value)
    {
        value = 0;
        foreach (byte b in s)
        {
            int d = HexValue(b);
            if (d < 0) return false;
            value = value << 4 | d;
        }
        return true;
    }

    /// <summary>A hex digit's value, or -1.</summary>
    public static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
