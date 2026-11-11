using System.Globalization;
using System.Text.RegularExpressions;
using ErikTerm.Config;

namespace ErikTerm.App.SystemPreferences;

/// <summary>
/// The text and numbers the platforms hand back, turned into <see cref="SystemAccessibility"/>: gsettings and gdbus output
/// on Linux, COLORREFs and percentages on Windows. No I/O, so the tests run it on any platform.
/// </summary>
internal static partial class PreferenceParsing
{
    /// <summary>gsettings get … for a boolean key: "true" or "false" (GVariant text); anything else is unreadable.</summary>
    public static bool? GsettingsBool(string output) => output.Trim() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    /// <summary>gsettings get … for a double key: "1.25", "1.0" (GVariant text is locale-independent).</summary>
    public static double? GsettingsDouble(string output)
    {
        string text = output.Trim();
        if (text.StartsWith("double ", StringComparison.Ordinal)) text = text[7..];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
    }

    /// <summary>
    /// One uint32 key of the portal's Settings.ReadAll(['org.freedesktop.appearance']) as gdbus prints it:
    /// <c>({'org.freedesktop.appearance': {'contrast': &lt;uint32 1&gt;, 'color-scheme': &lt;uint32 1&gt;}},)</c>.
    /// Null when the key isn't there (an older portal, another backend).
    /// </summary>
    public static uint? PortalUInt(string output, string key)
    {
        foreach (var groups in PortalEntry().Matches(output).Select(m => m.Groups))
            if (groups[1].Value == key && uint.TryParse(groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint v))
                return v;
        return null;
    }

    [GeneratedRegex(@"'([a-z-]+)': <(?:uint32 )?(\d+)>")]
    private static partial Regex PortalEntry();

    /// <summary>
    /// The Linux sources together. The portal's org.freedesktop.appearance keys (contrast, reduced-motion: 1 = asked for)
    /// work on any desktop whose portal backend has them; GNOME's own keys cover the GNOME versions before that. Either
    /// one saying "on" is enough.
    /// </summary>
    public static SystemAccessibility Linux(uint? portalContrast, uint? portalReducedMotion, bool? gnomeAnimations, bool? gnomeHighContrast, double? gnomeTextScale) => new()
    {
        HighContrast = AnyOn(portalContrast is { } c ? c == 1 : null, gnomeHighContrast),
        ReduceMotion = AnyOn(portalReducedMotion is { } r ? r == 1 : null, gnomeAnimations is { } a ? !a : null),
        TextScale = gnomeTextScale,
    };

    private static bool? AnyOn(bool? a, bool? b) => a == true || b == true ? true : a ?? b;

    /// <summary>A Win32 COLORREF (0x00BBGGRR) as 0xRRGGBB.</summary>
    public static uint ColorRefToRgb(uint colorRef) =>
        (colorRef & 0xFF) << 16 | (colorRef & 0xFF00) | (colorRef >> 16) & 0xFF;

    /// <summary>Windows' TextScaleFactor (a percentage, 100–225) as a factor; null when missing or out of range.</summary>
    public static double? WindowsTextScale(object? value) =>
        value is int percent && percent is >= 100 and <= 500 ? percent / 100.0 : null;
}
