using System.Globalization;

namespace ErikTerm.Core;

/// <summary>
/// The tab color a program asked for with iTerm2's <c>OSC 6 ; 1 ; bg ; red|green|blue ; brightness ; N</c>, one
/// component at a time.
/// </summary>
internal sealed class ProgramTabColor
{
    private int _red = -1, _green = -1, _blue = -1;

    /// <summary>0xRRGGBB (components not sent yet are 0), or null: never set, or reset.</summary>
    public uint? Value => _red < 0 && _green < 0 && _blue < 0
        ? null
        : (uint)(Math.Max(0, _red) << 16 | Math.Max(0, _green) << 8 | Math.Max(0, _blue));

    /// <summary>OSC 6 in iTerm2's tab-color form; false for anything else (xterm's OSC 6 is "color mode", not handled).</summary>
    public bool TryApply(ReadOnlySpan<byte> arg)
    {
        if (!arg.StartsWith("1;bg;"u8)) return false;
        var rest = arg["1;bg;".Length..];
        int semi = rest.IndexOf((byte)';');
        if (semi <= 0) return false;
        var component = rest[..semi];
        var value = rest[(semi + 1)..];
        if (value.SequenceEqual("default"u8))
        {
            Reset(); // iTerm2 resets the whole color, whichever component is named
            return true;
        }
        if (!value.StartsWith("brightness;"u8) || !int.TryParse(value["brightness;".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return false;
        n = Math.Clamp(n, 0, 255);
        if (component.SequenceEqual("red"u8)) _red = n;
        else if (component.SequenceEqual("green"u8)) _green = n;
        else if (component.SequenceEqual("blue"u8)) _blue = n;
        else return false;
        return true;
    }

    public void Reset() => _red = _green = _blue = -1;
}
