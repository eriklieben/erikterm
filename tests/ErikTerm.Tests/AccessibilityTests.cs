using ErikTerm.App.Rendering;
using ErikTerm.App.SystemPreferences;
using ErikTerm.App.Ui;
using ErikTerm.Config;

namespace ErikTerm.Tests;

/// <summary>reduce_motion, high_contrast and text_scale as the config file has them.</summary>
public class AccessibilitySettingsTests
{
    [Test]
    public async Task AllThreeFollowTheSystemByDefault()
    {
        // Act
        var s = SettingsLoader.Parse("").Settings;

        // Assert
        await Assert.That(s.ReduceMotion).IsEqualTo(AccessibilityMode.Auto);
        await Assert.That(s.HighContrast).IsEqualTo(AccessibilityMode.Auto);
        await Assert.That(s.TextScale).IsEqualTo(TextScaleMode.Auto);
    }

    [Test]
    [Arguments("\"on\"", AccessibilityMode.On)]
    [Arguments("\"OFF\"", AccessibilityMode.Off)]
    [Arguments("\" auto \"", AccessibilityMode.Auto)]
    [Arguments("true", AccessibilityMode.On)]
    [Arguments("false", AccessibilityMode.Off)]
    public async Task ReduceMotionAndHighContrastRead(string literal, AccessibilityMode expected)
    {
        // Act
        var result = SettingsLoader.Parse($"reduce_motion = {literal}\nhigh_contrast = {literal}");

        // Assert
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Settings.ReduceMotion).IsEqualTo(expected);
        await Assert.That(result.Settings.HighContrast).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\"off\"", TextScaleMode.Off)]
    [Arguments("\"auto\"", TextScaleMode.Auto)]
    [Arguments("false", TextScaleMode.Off)]
    [Arguments("true", TextScaleMode.Auto)]
    public async Task TextScaleReads(string literal, TextScaleMode expected)
    {
        // Act
        var result = SettingsLoader.Parse($"text_scale = {literal}");

        // Assert
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Settings.TextScale).IsEqualTo(expected);
    }

    [Test]
    [Arguments("reduce_motion = \"sometimes\"")]
    [Arguments("high_contrast = 1")]
    [Arguments("text_scale = \"on\"")]
    public async Task ABadValueIsReportedAndLeavesAuto(string line)
    {
        // Act
        var result = SettingsLoader.Parse(line);

        // Assert
        await Assert.That(result.Diagnostics).Count().IsEqualTo(1);
        await Assert.That(result.Settings.ReduceMotion).IsEqualTo(AccessibilityMode.Auto);
        await Assert.That(result.Settings.HighContrast).IsEqualTo(AccessibilityMode.Auto);
        await Assert.That(result.Settings.TextScale).IsEqualTo(TextScaleMode.Auto);
    }
}

/// <summary>AccessibilityPolicy: the config's modes against the system's preferences.</summary>
public class AccessibilityPolicyTests
{
    private static readonly SystemColors Aquatic = new(0xFFFFFF, 0x202020, 0x8EE3F0, 0x263B50, 0x8EE3F0);

    [Test]
    [Arguments(AccessibilityMode.Auto, null, false)]
    [Arguments(AccessibilityMode.Auto, false, false)]
    [Arguments(AccessibilityMode.Auto, true, true)]
    [Arguments(AccessibilityMode.On, null, true)]
    [Arguments(AccessibilityMode.On, false, true)]
    [Arguments(AccessibilityMode.On, true, true)]
    [Arguments(AccessibilityMode.Off, null, false)]
    [Arguments(AccessibilityMode.Off, false, false)]
    [Arguments(AccessibilityMode.Off, true, false)]
    public async Task ReduceMotionFollowsTheSystemOnlyOnAuto(AccessibilityMode mode, bool? system, bool expected)
    {
        // Arrange
        var configured = new Settings { ReduceMotion = mode };

        // Act
        var decided = AccessibilityPolicy.Decide(configured, new SystemAccessibility { ReduceMotion = system });

        // Assert
        await Assert.That(decided.ReduceMotion).IsEqualTo(expected);
        await Assert.That(decided.HighContrast).IsFalse();
    }

    [Test]
    [Arguments(AccessibilityMode.Auto, null, false)]
    [Arguments(AccessibilityMode.Auto, false, false)]
    [Arguments(AccessibilityMode.Auto, true, true)]
    [Arguments(AccessibilityMode.On, null, true)]
    [Arguments(AccessibilityMode.On, false, true)]
    [Arguments(AccessibilityMode.On, true, true)]
    [Arguments(AccessibilityMode.Off, null, false)]
    [Arguments(AccessibilityMode.Off, false, false)]
    [Arguments(AccessibilityMode.Off, true, false)]
    public async Task HighContrastFollowsTheSystemOnlyOnAuto(AccessibilityMode mode, bool? system, bool expected)
    {
        // Arrange
        var configured = new Settings { HighContrast = mode };

        // Act
        var decided = AccessibilityPolicy.Decide(configured, new SystemAccessibility { HighContrast = system });

        // Assert
        await Assert.That(decided.HighContrast).IsEqualTo(expected);
        await Assert.That(decided.ReduceMotion).IsFalse();
    }

    [Test]
    [Arguments(TextScaleMode.Auto, null, 1.0)]
    [Arguments(TextScaleMode.Auto, 1.0, 1.0)]
    [Arguments(TextScaleMode.Auto, 1.5, 1.5)]
    [Arguments(TextScaleMode.Auto, 0.8, 0.8)]
    [Arguments(TextScaleMode.Auto, 0.1, 1.0)]   // a misread
    [Arguments(TextScaleMode.Auto, 40.0, 1.0)]  // a misread
    [Arguments(TextScaleMode.Auto, double.NaN, 1.0)]
    [Arguments(TextScaleMode.Off, 1.5, 1.0)]
    [Arguments(TextScaleMode.Off, null, 1.0)]
    public async Task TextScaleFollowsTheSystemOnlyOnAuto(TextScaleMode mode, double? system, double expected)
    {
        // Arrange
        var configured = new Settings { TextScale = mode };

        // Act
        var decided = AccessibilityPolicy.Decide(configured, new SystemAccessibility { TextScale = system });

        // Assert
        await Assert.That(decided.TextScale).IsEqualTo(expected);
    }

    [Test]
    public async Task NothingAskedForChangesNothing()
    {
        // Arrange
        var configured = new Settings { FontSize = 13, MinimumContrast = 2, AnimationDurationMs = 300 };

        // Act
        var (effective, decided) = AccessibilityPolicy.Apply(configured, SystemAccessibility.Unknown);

        // Assert
        await Assert.That(effective).IsEqualTo(configured);
        await Assert.That(decided).IsEqualTo(EffectiveAccessibility.None);
    }

    [Test]
    public async Task ReduceMotionStopsEveryAnimationWithoutTouchingTheConfiguredSettings()
    {
        // Arrange
        var configured = new Settings { AnimationDurationMs = 300, CursorAnimation = true, SmoothScrolling = true, CursorBlinkRate = 600 };

        // Act
        var (effective, _) = AccessibilityPolicy.Apply(configured, new SystemAccessibility { ReduceMotion = true });

        // Assert
        await Assert.That(effective.AnimationDurationMs).IsEqualTo(0);
        await Assert.That(effective.CursorAnimation).IsFalse();
        await Assert.That(effective.SmoothScrolling).IsFalse();
        await Assert.That(effective.CursorBlinkRate).IsEqualTo(0);
        await Assert.That(configured.AnimationDurationMs).IsEqualTo(300);
        await Assert.That(configured.CursorBlinkRate).IsEqualTo(600);
    }

    [Test]
    [Arguments(1.0, 7.0)]
    [Arguments(4.5, 7.0)]
    [Arguments(7.0, 7.0)]
    [Arguments(12.0, 12.0)] // a higher minimum of the user's own stays
    public async Task HighContrastRaisesMinimumContrastToAtLeastSeven(double configuredMinimum, double expected)
    {
        // Arrange
        var configured = new Settings { HighContrast = AccessibilityMode.On, MinimumContrast = configuredMinimum };

        // Act
        var (effective, _) = AccessibilityPolicy.Apply(configured, SystemAccessibility.Unknown);

        // Assert
        await Assert.That(effective.MinimumContrast).IsEqualTo(expected);
    }

    [Test]
    public async Task ASystemContrastThemeGivesThePanesItsColours()
    {
        // Arrange
        var configured = new Settings();
        var system = new SystemAccessibility { HighContrast = true, HighContrastColors = Aquatic };

        // Act
        var (effective, decided) = AccessibilityPolicy.Apply(configured, system);

        // Assert
        await Assert.That(decided.UsesSystemColors).IsTrue();
        await Assert.That(effective.Colors.Foreground).IsEqualTo(0xFFFFFFu);
        await Assert.That(effective.Colors.Background).IsEqualTo(0x202020u);
        await Assert.That(effective.Colors.SelectionBg).IsEqualTo(0x8EE3F0u);
        await Assert.That(effective.Colors.SelectionFg).IsEqualTo(0x263B50u);
        await Assert.That(effective.Colors.Ansi).IsSameReferenceAs(configured.Colors.Ansi); // programs' colours stay
    }

    [Test]
    [Arguments(AccessibilityMode.Off, true)]   // the user turned it off
    [Arguments(AccessibilityMode.On, false)]   // on in the config, but the system has no contrast theme on
    [Arguments(AccessibilityMode.Auto, false)]
    public async Task SystemColoursOnlyWithAContrastThemeAndHighContrastOn(AccessibilityMode mode, bool systemHighContrast)
    {
        // Arrange
        var configured = new Settings { HighContrast = mode };
        var system = new SystemAccessibility { HighContrast = systemHighContrast, HighContrastColors = Aquatic };

        // Act
        var (effective, decided) = AccessibilityPolicy.Apply(configured, system);

        // Assert
        await Assert.That(decided.UsesSystemColors).IsFalse();
        await Assert.That(effective.Colors).IsEqualTo(configured.Colors);
    }

    [Test]
    public async Task TextScaleMultipliesEveryFontSize()
    {
        // Arrange
        var configured = new Settings { FontSize = 12, CommandPaletteFontSize = 12, PaneSelectFontSize = 36 };

        // Act
        var (effective, _) = AccessibilityPolicy.Apply(configured, new SystemAccessibility { TextScale = 1.5 });

        // Assert
        await Assert.That(effective.FontSize).IsEqualTo(18.0);
        await Assert.That(effective.CommandPaletteFontSize).IsEqualTo(18.0);
        await Assert.That(effective.PaneSelectFontSize).IsEqualTo(54.0);
        await Assert.That(configured.FontSize).IsEqualTo(12.0);
    }

    [Test]
    public async Task ScaledSizesStayInTheirRanges()
    {
        // Arrange
        var configured = new Settings { FontSize = 150, CommandPaletteFontSize = 60, PaneSelectFontSize = 180 };

        // Act
        var (effective, _) = AccessibilityPolicy.Apply(configured, new SystemAccessibility { TextScale = 2 });

        // Assert
        await Assert.That(effective.FontSize).IsEqualTo(200.0);
        await Assert.That(effective.CommandPaletteFontSize).IsEqualTo(72.0);
        await Assert.That(effective.PaneSelectFontSize).IsEqualTo(200.0);
    }
}

/// <summary>The UI chrome with high_contrast on (Ui/Theme.cs).</summary>
public class HighContrastThemeTests
{
    [Test]
    [Arguments("erik")]
    [Arguments("Catppuccin Macchiato")]
    [Arguments("Tokyo Night")]
    public async Task ChromeReachesWcagLevels(string schemeName)
    {
        // Arrange
        var scheme = ColorSchemes.Find(schemeName, [])!;
        var settings = new Settings { Colors = scheme };

        // Act
        var t = Theme.From(settings, scheme, 1, highContrast: true);

        // Assert
        await Assert.That(t.HighContrast).IsTrue();
        await Assert.That(Contrast.Ratio(t.PanelFg, t.PanelBg)).IsGreaterThanOrEqualTo(6.99f);
        await Assert.That(Contrast.Ratio(t.Muted, t.PanelBg)).IsGreaterThanOrEqualTo(4.49f);
        await Assert.That(Contrast.Ratio(t.Border, t.PanelBg)).IsGreaterThanOrEqualTo(2.99f);
        await Assert.That(Contrast.Ratio(t.Split, t.Background)).IsGreaterThanOrEqualTo(2.99f);
        await Assert.That(Contrast.Ratio(t.Accent, t.Background)).IsGreaterThanOrEqualTo(2.99f);
        await Assert.That(Contrast.Ratio(t.Danger, t.PanelBg)).IsGreaterThanOrEqualTo(4.49f);
    }

    [Test]
    public async Task WithoutHighContrastTheSchemesColoursAreKept()
    {
        // Arrange
        var scheme = ColorSchemes.Erik;

        // Act
        var t = Theme.From(new Settings(), scheme, 1);

        // Assert
        await Assert.That(t.HighContrast).IsFalse();
        await Assert.That(t.Split).IsEqualTo(scheme.Split);
        await Assert.That(t.Accent).IsEqualTo(scheme.Accent);
    }
}

/// <summary>What gsettings, gdbus and Windows hand back (SystemPreferences/PreferenceParsing.cs).</summary>
public class PreferenceParsingTests
{
    // gdbus call … org.freedesktop.portal.Settings.ReadAll "['org.freedesktop.appearance']", as GNOME's portal prints it.
    private const string PortalOutput = "({'org.freedesktop.appearance': {'contrast': <uint32 1>, 'color-scheme': <uint32 1>, 'reduced-motion': <uint32 0>}},)\n";

    [Test]
    [Arguments("true\n", true)]
    [Arguments("false", false)]
    [Arguments("", null)]
    [Arguments("No such schema “org.gnome.desktop.a11y.interface”", null)]
    public async Task GsettingsBooleans(string output, bool? expected)
    {
        // Act
        var value = PreferenceParsing.GsettingsBool(output);

        // Assert
        await Assert.That(value).IsEqualTo(expected);
    }

    [Test]
    [Arguments("1.25\n", 1.25)]
    [Arguments("1.0", 1.0)]
    [Arguments("double 1.5", 1.5)]
    [Arguments("", null)]
    [Arguments("nan", null)]
    public async Task GsettingsDoubles(string output, double? expected)
    {
        // Act
        var value = PreferenceParsing.GsettingsDouble(output);

        // Assert
        await Assert.That(value).IsEqualTo(expected);
    }

    [Test]
    [Arguments("contrast", 1u)]
    [Arguments("reduced-motion", 0u)]
    [Arguments("color-scheme", 1u)]
    [Arguments("accent-color", null)]
    public async Task PortalKeys(string key, uint? expected)
    {
        // Act
        var value = PreferenceParsing.PortalUInt(PortalOutput, key);

        // Assert
        await Assert.That(value).IsEqualTo(expected);
    }

    [Test]
    public async Task APortalWithoutTheKeysGivesNothing()
    {
        // Act
        var value = PreferenceParsing.PortalUInt("", "contrast");

        // Assert
        await Assert.That(value).IsNull();
    }

    [Test]
    [Arguments(null, null, null, null, null, null)]
    [Arguments(1u, null, null, null, true, null)]      // the portal asks for contrast
    [Arguments(0u, null, null, true, true, null)]      // GNOME's own switch is enough
    [Arguments(0u, null, null, false, false, null)]
    [Arguments(null, 1u, true, null, null, true)]      // the portal asks for reduced motion
    [Arguments(null, 0u, false, null, null, true)]     // GNOME's animations off
    [Arguments(null, 0u, true, null, null, false)]
    [Arguments(null, null, true, null, null, false)]
    public async Task LinuxSourcesTogether(uint? contrast, uint? reducedMotion, bool? animations, bool? gnomeHighContrast, bool? expectedContrast, bool? expectedReduce)
    {
        // Act
        var read = PreferenceParsing.Linux(contrast, reducedMotion, animations, gnomeHighContrast, 1.25);

        // Assert
        await Assert.That(read.HighContrast).IsEqualTo(expectedContrast);
        await Assert.That(read.ReduceMotion).IsEqualTo(expectedReduce);
        await Assert.That(read.TextScale).IsEqualTo(1.25);
    }

    [Test]
    public async Task AColorRefIsBgr()
    {
        // Act
        uint rgb = PreferenceParsing.ColorRefToRgb(0x00F0E38E); // COLORREF of #8EE3F0

        // Assert
        await Assert.That(rgb).IsEqualTo(0x8EE3F0u);
    }

    [Test]
    [Arguments(125, 1.25)]
    [Arguments(100, 1.0)]
    [Arguments(225, 2.25)]
    [Arguments(50, null)]
    [Arguments(null, null)]
    public async Task WindowsTextScaleFactorIsAPercentage(int? percent, double? expected)
    {
        // Act
        var scale = PreferenceParsing.WindowsTextScale(percent);

        // Assert
        await Assert.That(scale).IsEqualTo(expected);
    }
}
