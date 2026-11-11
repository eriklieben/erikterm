namespace ErikTerm.Config;

/// <summary>reduce_motion and high_contrast: follow the operating system (Auto), or always / never.</summary>
public enum AccessibilityMode { Auto, On, Off }

/// <summary>text_scale: multiply the font sizes by the operating system's text size (Auto), or leave them as set (Off).</summary>
public enum TextScaleMode { Auto, Off }

/// <summary>
/// The colours a high-contrast theme of the operating system asks applications to use (Windows: GetSysColor while a
/// contrast theme is on), as 0xRRGGBB.
/// </summary>
public sealed record SystemColors(uint Foreground, uint Background, uint SelectionBg, uint SelectionFg, uint Accent);

/// <summary>
/// What the operating system says about the user's accessibility preferences. A null means it couldn't be read or the
/// platform has no such setting; that counts as "no preference".
/// </summary>
public sealed record SystemAccessibility
{
    /// <summary>Animations turned off (Windows "Animation effects" off, macOS "Reduce motion", GNOME animations off).</summary>
    public bool? ReduceMotion { get; init; }
    /// <summary>A high-contrast theme or "Increase contrast" is on.</summary>
    public bool? HighContrast { get; init; }
    /// <summary>The system's text size as a factor (1.25 = 125 %), apart from the display's scaling.</summary>
    public double? TextScale { get; init; }
    /// <summary>The high-contrast theme's own colours, where the system has them (Windows).</summary>
    public SystemColors? HighContrastColors { get; init; }

    public static readonly SystemAccessibility Unknown = new();
}

/// <summary>What erikterm does about accessibility right now: the config's modes resolved against the system.</summary>
public sealed record EffectiveAccessibility(bool ReduceMotion, bool HighContrast, double TextScale, bool UsesSystemColors)
{
    public static readonly EffectiveAccessibility None = new(false, false, 1, false);
}

/// <summary>
/// Turns the configured settings and the system's preferences into the settings the windows use. The file's values are
/// never rewritten; this is an override on top of them. Explicit "on" / "off" in the config win over the system.
/// </summary>
public static class AccessibilityPolicy
{
    /// <summary>WCAG AAA for text: the least minimum_contrast high contrast gives.</summary>
    public const double HighContrastMinimum = 7;

    /// <summary>Bounds for a system text scale; anything outside is taken as a misread and ignored.</summary>
    public const double MinTextScale = 0.5, MaxTextScale = 4;

    public static EffectiveAccessibility Decide(Settings configured, SystemAccessibility system)
    {
        bool reduceMotion = Resolve(configured.ReduceMotion, system.ReduceMotion);
        bool highContrast = Resolve(configured.HighContrast, system.HighContrast);
        double scale = configured.TextScale == TextScaleMode.Auto ? UsableScale(system.TextScale) : 1;
        bool systemColors = highContrast && system.HighContrast == true && system.HighContrastColors is not null;
        return new EffectiveAccessibility(reduceMotion, highContrast, scale, systemColors);
    }

    /// <summary>The settings to run with, and what was decided.</summary>
    public static (Settings Settings, EffectiveAccessibility Accessibility) Apply(Settings configured, SystemAccessibility system)
    {
        var decided = Decide(configured, system);
        var s = configured;
        if (decided.ReduceMotion)
            s = s with { AnimationDurationMs = 0, CursorAnimation = false, SmoothScrolling = false, CursorBlinkRate = 0 };
        if (decided.HighContrast)
            s = s with { MinimumContrast = Math.Max(s.MinimumContrast, HighContrastMinimum) };
        if (decided.UsesSystemColors)
            s = s with { Colors = WithSystemColors(s.Colors, system.HighContrastColors!) };
#pragma warning disable S1244 // 1 is exactly what UsableScale returns for "no scaling"; any other value scales.
        if (decided.TextScale != 1)
#pragma warning restore S1244
        {
            s = s with
            {
                FontSize = Math.Clamp(s.FontSize * decided.TextScale, 4, 200),
                CommandPaletteFontSize = Math.Clamp(s.CommandPaletteFontSize * decided.TextScale, 6, 72),
                PaneSelectFontSize = Math.Clamp(s.PaneSelectFontSize * decided.TextScale, 8, 200),
            };
        }
        return (s, decided);
    }

    private static bool Resolve(AccessibilityMode mode, bool? system) => mode switch
    {
        AccessibilityMode.On => true,
        AccessibilityMode.Off => false,
        _ => system == true,
    };

    private static double UsableScale(double? scale) =>
        scale is { } v && double.IsFinite(v) && v >= MinTextScale && v <= MaxTextScale ? v : 1;

    /// <summary>
    /// The scheme with the system's text, background, selection and accent colours. The 16 ANSI colours stay the
    /// scheme's: programs pick them by number, and minimum_contrast keeps them readable on the new background.
    /// </summary>
    public static ColorScheme WithSystemColors(ColorScheme scheme, SystemColors colors) => scheme with
    {
        Name = scheme.Name + " (system high contrast)",
        Foreground = colors.Foreground,
        Background = colors.Background,
        CursorBg = colors.Foreground,
        CursorFg = colors.Background,
        SelectionBg = colors.SelectionBg,
        SelectionFg = colors.SelectionFg,
        CursorBorder = colors.Foreground,
        Split = colors.Foreground,
        Accent = colors.Accent,
    };
}
