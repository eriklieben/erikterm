using ErikTerm.Config;

namespace ErikTerm.Tests;

/// <summary>What a config with mistakes does: a live reload with a syntax error, and "did you mean" for typos.</summary>
public class ConfigDiagnosticsTests
{
    private const string Good = """
        font = "Iosevka"
        font_size = 15
        """;

    // Saved halfway through an edit: the inline table on line 3 never closes.
    private const string HalfSaved = """
        font = "Iosevka"
        font_size = 15
        keys = { "CTRL+h" = "ActivatePaneDirection(Left)"
        """;

    [Test]
    public async Task SyntaxErrorIsFlagged()
    {
        // Act
        var result = SettingsLoader.Parse(HalfSaved);

        // Assert
        await Assert.That(result.SyntaxError).IsTrue();
        await Assert.That(result.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task ReloadWithASyntaxErrorKeepsTheCurrentSettings()
    {
        // Arrange
        var current = SettingsLoader.Parse(Good).Settings;
        var broken = SettingsLoader.Parse(HalfSaved);

        // Act
        var (settings, kept) = SettingsLoader.Reloaded(current, broken);

        // Assert
        await Assert.That(kept).IsTrue();
        await Assert.That(settings.Font).IsEqualTo("Iosevka");
        await Assert.That(settings.FontSize).IsEqualTo(15);
    }

    [Test]
    public async Task ReloadOfAValidFileAppliesIt()
    {
        // Arrange
        var current = SettingsLoader.Parse(Good).Settings;
        var edited = SettingsLoader.Parse("font = \"Fira Code\"");

        // Act
        var (settings, kept) = SettingsLoader.Reloaded(current, edited);

        // Assert
        await Assert.That(kept).IsFalse();
        await Assert.That(settings.Font).IsEqualTo("Fira Code");
    }

    [Test]
    [Arguments("colr_scheme", "color_scheme")]
    [Arguments("font-size", "font_size")]
    [Arguments("scrolback_lines", "scrollback_lines")]
    [Arguments("tmux_emualtion", "tmux_emulation")]
    public async Task UnknownSettingSuggestsTheClosestOne(string typo, string meant)
    {
        // Act
        var result = SettingsLoader.Parse($"{typo} = 1");

        // Assert
        await Assert.That(result.Diagnostics).Contains(d => d.Contains($"did you mean \"{meant}\"?", StringComparison.Ordinal));
    }

    [Test]
    public async Task UnrelatedUnknownSettingGetsNoSuggestion()
    {
        // Act
        var result = SettingsLoader.Parse("completely_made_up = 1");

        // Assert
        await Assert.That(result.Diagnostics).Count().IsEqualTo(1);
        await Assert.That(result.Diagnostics[0]).DoesNotContain("did you mean");
    }

    [Test]
    public async Task UnknownColorSuggestsTheClosestOne()
    {
        // Act
        var result = SettingsLoader.Parse("[colors]\nforground = \"#ffffff\"");

        // Assert
        await Assert.That(result.Diagnostics).Contains(d => d.Contains("did you mean \"foreground\"?", StringComparison.Ordinal));
    }

    [Test]
    public async Task EveryKnownKeyIsASetting()
    {
        // Act: a wrong value type is fine here; only "unknown setting" would mean the list and the parser disagree.
        var unknown = SettingsLoaderKnownKeys().Where(key =>
            SettingsLoader.Parse($"{key} = 0").Diagnostics.Any(d => d.Contains("unknown setting", StringComparison.Ordinal))).ToList();

        // Assert
        await Assert.That(unknown).IsEmpty();
    }

    private static IEnumerable<string> SettingsLoaderKnownKeys() => SettingsLoader.KnownKeys;
}
