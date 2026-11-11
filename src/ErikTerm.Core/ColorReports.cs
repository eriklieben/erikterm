using System.Globalization;

namespace ErikTerm.Core;

/// <summary>
/// Colors a program sets and asks for: OSC 4 / 10 / 11 / 12 and their resets (104, 110, 111, 112), as overrides on
/// top of the color scheme in <see cref="Palette"/>, and the light/dark notifications of DECSET 2031
/// (<c>CSI ? 997 ; 1|2 n</c>, asked for with <c>CSI ? 996 n</c>). The OSC handlers return whether the colors changed;
/// redrawing is the terminal's business.
/// </summary>
internal sealed class ColorReports(Palette palette, TerminalReplies replies)
{
    private bool _reportedDark;

    /// <summary>DECSET 2031: the program wants a report whenever the background turns dark or light.</summary>
    public bool ColorSchemeUpdates { get; private set; }

    /// <summary>The palette changed: with DECSET 2031 on, a background that flipped between dark and light is reported.</summary>
    public void PaletteChanged()
    {
        bool dark = palette.IsDark;
        if (dark == _reportedDark) return;
        _reportedDark = dark;
        if (ColorSchemeUpdates) ReportColorScheme();
    }

    public void ReportColorScheme() => replies.Send(palette.IsDark ? "\e[?997;1n"u8 : "\e[?997;2n"u8);

    public void SetColorSchemeUpdates(bool on)
    {
        ColorSchemeUpdates = on;
        _reportedDark = palette.IsDark;
    }

    /// <summary>RIS / a new program: the scheme's own colors again, and no light/dark reports.</summary>
    public void Reset()
    {
        palette.ResetAllOverrides(); // OSC 4 / 10 / 11 / 12 colors
        ColorSchemeUpdates = false;
        _reportedDark = palette.IsDark;
    }

    /// <summary>Sends an OSC reply ended like the query was (BEL or ST).</summary>
    private void RespondOsc(string body, bool bel) => replies.Send(bel ? $"\e]{body}\a" : $"\e]{body}\e\\");

    private static string RgbSpec(uint rgb)
    {
        int r = (int)(rgb >> 16) & 0xFF, g = (int)(rgb >> 8) & 0xFF, b = (int)rgb & 0xFF;
        return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
    }

    /// <summary>OSC 4 ; index ; spec [; index ; spec …]: "?" asks, a color sets (an override on top of the scheme).</summary>
    public bool Osc4(ReadOnlySpan<byte> arg, bool bel)
    {
        bool changed = false;
        while (!arg.IsEmpty)
        {
            int semi = arg.IndexOf((byte)';');
            if (semi < 0) break;
            var indexText = arg[..semi];
            arg = arg[(semi + 1)..];
            int next = arg.IndexOf((byte)';');
            var spec = next < 0 ? arg : arg[..next];
            arg = next < 0 ? default : arg[(next + 1)..];
            if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index > 255) continue;
            if (spec.SequenceEqual("?"u8)) RespondOsc($"4;{index};{RgbSpec(palette[index])}", bel);
            else if (XColorSpec.TryParse(spec, out uint rgb)) { palette.SetOverride(index, rgb); changed = true; }
        }
        return changed;
    }

    /// <summary>OSC 104 [; index …]: back to the scheme's colors (all of them without indexes).</summary>
    public void Osc104(ReadOnlySpan<byte> arg)
    {
        if (arg.IsEmpty) { palette.ResetOverrides(); return; }
        foreach (var range in arg.Split((byte)';'))
        {
            if (int.TryParse(arg[range], NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index <= 255)
                palette.ResetOverride(index);
        }
    }

    /// <summary>
    /// OSC 10 / 11 / 12 ; spec [; spec …]: each spec is "?" (query) or a color, and like xterm further specs go to the
    /// next code (OSC 10 ; fg ; bg sets both).
    /// </summary>
    public bool OscDynamicColor(int code, ReadOnlySpan<byte> arg, bool bel)
    {
        bool changed = false;
        foreach (var range in arg.Split((byte)';'))
        {
            if (code > 12) break;
            var which = (DynamicColor)(code - 10);
            var spec = arg[range];
            if (spec.SequenceEqual("?"u8)) RespondOsc($"{code};{RgbSpec(palette.GetDynamic(which))}", bel);
            else if (XColorSpec.TryParse(spec, out uint rgb))
            {
                palette.SetDynamic(which, rgb);
                if (which == DynamicColor.Background) PaletteChanged();
                changed = true;
            }
            code++;
        }
        return changed;
    }

    /// <summary>OSC 110 / 111 / 112.</summary>
    public void OscResetDynamic(DynamicColor which)
    {
        palette.ResetDynamic(which);
        if (which == DynamicColor.Background) PaletteChanged();
    }
}
