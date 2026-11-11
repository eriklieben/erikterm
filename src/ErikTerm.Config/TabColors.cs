namespace ErikTerm.Config;

/// <summary>
/// Tab colors (SetTabColor, `erikterm cli set-tab-color`, the palette's Tab Color entries, session.json): a color name
/// from the scheme's ANSI palette — "red", "green", "yellow", "blue", "magenta" ("purple"), "cyan", "white", "black",
/// each also as "bright-red" etc. — which follows the theme, or a fixed "#rrggbb". Stored in its canonical spelling.
/// </summary>
public static class TabColors
{
    /// <summary>The names the palette and menus offer, in palette order (the ANSI colors that read as a tab color).</summary>
    public static readonly string[] Offered = ["red", "yellow", "green", "cyan", "blue", "magenta"];

    private static readonly string[] AnsiNames = ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white"];

    /// <summary>"none", "default", "reset" or "": no color (the tab's own, or a program's).</summary>
    public static bool IsReset(string? text) =>
        text is null || text.Trim().ToLowerInvariant() is "" or "none" or "default" or "reset" or "off";

    /// <summary>
    /// The canonical form of a color ("red", "bright-blue", "#ff8800"), null for a reset, or false when it isn't a color.
    /// Names are case-insensitive; "brightred" / "bright_red" / "purple" are accepted.
    /// </summary>
    public static bool TryNormalize(string? text, out string? canonical)
    {
        canonical = null;
        if (IsReset(text)) return true;
        string t = text!.Trim().ToLowerInvariant();
        if (t.StartsWith('#') || t.StartsWith("rgb:", StringComparison.Ordinal))
        {
            if (!SettingsLoader.TryParseColor(t, out uint rgb)) return false;
            canonical = $"#{rgb:x6}";
            return true;
        }
        bool bright = false;
        foreach (string prefix in (ReadOnlySpan<string>)["bright-", "bright_", "bright"])
        {
            if (!t.StartsWith(prefix, StringComparison.Ordinal)) continue;
            bright = true;
            t = t[prefix.Length..];
            break;
        }
        if (t is "purple" or "violet") t = "magenta";
        if (t is "grey" or "gray") { t = "black"; bright = true; } // the scheme's gray is bright black
        if (Array.IndexOf(AnsiNames, t) < 0) return false;
        canonical = bright ? "bright-" + t : t;
        return true;
    }

    /// <summary>The RGB of a canonical color under <paramref name="scheme"/> (names follow the theme), or null.</summary>
    public static uint? Resolve(string? canonical, ColorScheme scheme)
    {
        if (string.IsNullOrEmpty(canonical)) return null;
        if (canonical[0] == '#') return SettingsLoader.TryParseColor(canonical, out uint rgb) ? rgb : null;
        bool bright = canonical.StartsWith("bright-", StringComparison.Ordinal);
        int i = Array.IndexOf(AnsiNames, bright ? canonical["bright-".Length..] : canonical);
        if (i < 0) return null;
        return bright ? scheme.Brights[i] : scheme.Ansi[i];
    }

    /// <summary>"Red", "Bright Blue", "#ff8800": for menus and toasts.</summary>
    public static string DisplayName(string canonical)
    {
        if (canonical.StartsWith('#')) return canonical;
        bool bright = canonical.StartsWith("bright-", StringComparison.Ordinal);
        string name = bright ? canonical["bright-".Length..] : canonical;
        name = char.ToUpperInvariant(name[0]) + name[1..];
        return bright ? "Bright " + name : name;
    }
}
