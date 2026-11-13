namespace ErikTerm.Config;

public sealed record ColorScheme(
    string Name,
    uint Foreground,
    uint Background,
    uint CursorBg,
    uint CursorFg,
    uint SelectionBg,
    uint[] Ansi,
    uint[] Brights,
    uint Split,
    uint Accent)
{
    /// <summary>Selected text color from a scheme file (null: keep the text's own color).</summary>
    public uint? SelectionFg { get; init; }
    /// <summary>Cursor outline color from a scheme file.</summary>
    public uint? CursorBorder { get; init; }
    /// <summary>The scheme file it came from; null for a built-in scheme.</summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// The minimum_contrast this scheme is drawn with when the config doesn't set one (1 = off). The built-in schemes
    /// carry <see cref="ColorSchemes.BuiltInMinimumContrast"/>; a scheme file has none.
    /// </summary>
    public double MinimumContrast { get; init; } = 1;

    /// <summary>
    /// With <see cref="MinimumContrast"/> from the scheme: the contrast text keeps in an inactive pane, against what it is
    /// drawn on after inactive_pane_hsb dims it (1 = none). The built-in schemes carry
    /// <see cref="ColorSchemes.BuiltInInactiveMinimumContrast"/>.
    /// </summary>
    public double InactiveMinimumContrast { get; init; } = 1;

    /// <summary>Where a built-in scheme's colours come from; null for erikterm's own schemes and for scheme files.</summary>
    public SchemeCredit? Credit { get; init; }
}

/// <summary>
/// The project a built-in scheme's colours come from, for THIRD-PARTY-NOTICES.md and the About screen's Licenses tab.
/// <paramref name="License"/> is one of <see cref="SchemeLicenses"/>.
/// </summary>
public sealed record SchemeCredit(string Project, string Copyright, string License, string Url)
{
    /// <summary>What erikterm took or changed beyond the published palette (null: the colours as published).</summary>
    public string? Notes { get; init; }
}

/// <summary>The licenses the built-in schemes' colours are used under.</summary>
public static class SchemeLicenses
{
    public const string Mit = "MIT License";
    public const string Apache2 = "Apache License 2.0";
    public const string Bsd3 = "BSD 3-Clause License";
}

/// <summary>
/// Built-in schemes, selectable with <c>color_scheme = "..."</c> (wezterm-style names). Each one is a file under
/// Schemes/ and a line in the registry below; the list is "erik" (the default) first, then the rest by name.
/// </summary>
public static partial class ColorSchemes
{
    /// <summary>
    /// The minimum_contrast every built-in scheme brings (WCAG AA): text on any ANSI background reads at 4.5:1 in the
    /// focused pane (BuiltInSchemeContrastTests).
    /// </summary>
    public const double BuiltInMinimumContrast = 4.5;

    /// <summary>
    /// What the built-in schemes keep in an inactive pane (WCAG's level for large text and non-text marks): dimming darkens
    /// text but not the window's background, so text on the plain background needs this second floor.
    /// </summary>
    public const double BuiltInInactiveMinimumContrast = 3;

    public static readonly ColorScheme[] All;

    static ColorSchemes()
    {
        // The registry: one line per built-in scheme (Schemes/*.cs). Field initializers of every part run first.
        ColorScheme[] builtIn =
        [
            Erik,
            CatppuccinMacchiato,
            CatppuccinMocha,
            TokyoNight,
            AbyssAndBrass,
            AyuDark,
            AyuLight,
            Blossom,
            Carbonfox,
            CatppuccinFrappe,
            CatppuccinLatte,
            CherryBlossom,
            CoralNavy,
            CreamAndRose,
            Cyberpunk,
            Dracula,
            EverforestDark,
            EverforestLight,
            FairyFloss,
            GitHubDark,
            GitHubLight,
            GraphiteAndBlush,
            GruvboxDark,
            GruvboxLight,
            KanagawaLotus,
            KanagawaWave,
            LaserWave,
            LuminaOxbloodLight,
            NeonGrid,
            NightBlossom,
            NightOwl,
            Nightfox,
            Nord,
            OneHalfDark,
            OneHalfLight,
            OnyxAndRose,
        ];
        All = [builtIn[0], .. builtIn.Skip(1).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A built-in scheme: the built-in contrast default and, for one from another project, its credit.</summary>
    private static ColorScheme BuiltIn(ColorScheme scheme, SchemeCredit? credit = null) =>
        scheme with { MinimumContrast = BuiltInMinimumContrast, InactiveMinimumContrast = BuiltInInactiveMinimumContrast, Credit = credit };

    /// <summary>Case-insensitive; also accepts wezterm's "(Gogh)" / "(base16)" suffixed variants.</summary>
    public static ColorScheme? Find(string name)
    {
        string n = name.Trim();
        int paren = n.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) n = n[..paren];
        return All.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Schemes from files (<see cref="ColorSchemeFiles"/>) first — exact name, case-insensitive — then the built-ins.</summary>
    public static ColorScheme? Find(string name, IReadOnlyList<ColorScheme> custom) =>
        custom.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Find(name);

    /// <summary>Custom schemes followed by the built-ins they don't shadow.</summary>
    public static List<ColorScheme> WithCustom(IReadOnlyList<ColorScheme> custom) =>
        [.. custom, .. All.Where(b => !custom.Any(c => c.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)))];
}
