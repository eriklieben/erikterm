using ErikTerm.Config;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class TomlTests
{
    /// <summary>The value at <paramref name="path"/> through nested tables, or null when any key along it is missing.</summary>
    private static object? Lookup(TomlTable table, params string[] path)
    {
        object? current = table;
        foreach (string key in path)
        {
            if (current is not TomlTable t || !t.TryGet(key, out var value, out _)) return null;
            current = value;
        }
        return current;
    }

    private const string Scalars = """
        # comment
        name = "erik"   # trailing comment
        lit = 'C:\path'
        int = 1_000
        hex = 0xff
        neg = -3
        float = 1.25
        yes = true
        """;

    [Test]
    [Arguments("name", "erik")]
    [Arguments("lit", @"C:\path")]
    [Arguments("int", 1000L)]
    [Arguments("hex", 255L)]
    [Arguments("neg", -3L)]
    [Arguments("float", 1.25)]
    [Arguments("yes", true)]
    public async Task ScalarsAndComments(string key, object expected)
    {
        // Act
        var table = Toml.Parse(Scalars);
        bool found = table.TryGet(key, out var value, out _);

        // Assert
        await Assert.That(found).IsTrue().Because($"missing {key}");
        await Assert.That(value).IsEqualTo(expected);
    }

    [Test]
    public async Task StringEscapes()
    {
        // Act
        var value = Lookup(Toml.Parse("""s = "a\tb\"c\u00e9\U0001F980" """), "s");

        // Assert
        await Assert.That(value).IsEqualTo("a\tb\"c\u00e9\U0001F980");
    }

    [Test]
    [Arguments("a", "line1\nline2")]
    [Arguments("b", "raw\\n")]
    public async Task MultilineStrings(string key, string expected)
    {
        // Act
        var value = Lookup(Toml.Parse("a = \"\"\"\nline1\nline2\"\"\"\nb = '''\nraw\\n'''"), key);

        // Assert
        await Assert.That(value).IsEqualTo(expected);
    }

    [Test]
    public async Task ArraysSpanLinesWithTrailingComma()
    {
        // Arrange
        const string toml = """
            a = [
              "x",  # first
              "y",
            ]
            """;

        // Act
        var value = Lookup(Toml.Parse(toml), "a") as IEnumerable<object>;

        // Assert
        await Assert.That(value).IsEquivalentTo(new object[] { "x", "y" }, CollectionOrdering.Matching);
    }

    private const string Tables = """
        top.inner = 1
        pad = { left = 2, top = 3 }
        [colors]
        foreground = "#fff"
        [a.b]
        c = 4
        """;

    [Test]
    [Arguments("top.inner", 1L)]
    [Arguments("pad.top", 3L)]
    [Arguments("colors.foreground", "#fff")]
    [Arguments("a.b.c", 4L)]
    public async Task TablesDottedKeysAndInlineTables(string path, object expected)
    {
        // Act
        var value = Lookup(Toml.Parse(Tables), path.Split('.'));

        // Assert
        await Assert.That(value).IsEqualTo(expected);
    }

    [Test]
    [Arguments("a = ", 1)]
    [Arguments("a = 1\na = 2", 2)]
    [Arguments("x = 1\n\ns = \"open", 3)]
    [Arguments("a = 1 b = 2", 1)]
    public async Task ErrorsCarryLineNumbers(string toml, int line)
    {
        // Act
        var act = () => Toml.Parse(toml);

        // Assert
        var ex = await Assert.That(act).ThrowsExactly<TomlException>();
        await Assert.That(ex!.Line).IsEqualTo(line);
    }
}

public class SettingsTests
{
    [Test]
    public async Task EmptyFileGivesDefaults()
    {
        // Act
        var r = SettingsLoader.Parse("");

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.DefaultProg).IsNull();
        await Assert.That(r.Settings.WindowBackgroundOpacity).IsEqualTo(1.0);
        await Assert.That(r.Settings.Colors).IsSameReferenceAs(ColorSchemes.Erik);
    }

    [Test]
    public async Task WeztermStyleConfig()
    {
        // Arrange
        const string toml = """
            color_scheme = "Catppuccin Macchiato (Gogh)"
            window_background_opacity = 0.75
            line_height = 1.25
            font_size = 11
            font = "JetBrainsMono Nerd Font"
            default_cursor_style = "SteadyBar"
            window_decorations = "NONE"
            window_padding = { left = 8, right = 8, top = 4, bottom = 4 }
            default_prog = ["zsh", "-l"]
            set_environment_variables = { EDITOR = "nvim" }
            """;

        // Act
        var r = SettingsLoader.Parse(toml);
        var s = r.Settings;

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(s.Colors.Name).IsEqualTo("Catppuccin Macchiato");
        await Assert.That(s.WindowBackgroundOpacity).IsEqualTo(0.75);
        await Assert.That(s.LineHeight).IsEqualTo(1.25);
        await Assert.That(s.FontSize).IsEqualTo(11);
        await Assert.That(s.Font).IsEqualTo("JetBrainsMono Nerd Font");
        await Assert.That(s.CursorShape).IsEqualTo(CursorShape.Bar);
        await Assert.That(s.WindowDecorations).IsFalse();
        await Assert.That(s.WindowPadding).IsEqualTo(new Padding(8, 4, 8, 4));
        await Assert.That(s.DefaultProg).IsEquivalentTo(["zsh", "-l"], CollectionOrdering.Matching);
        await Assert.That(s.SetEnvironmentVariables["EDITOR"]).IsEqualTo("nvim");
    }

    private const string TokyoNightWithColors = """
        color_scheme = "Tokyo Night"
        [colors]
        background = "#000000"
        ansi = ["#000", "#f00", "#0f0", "#ff0", "#00f", "#f0f", "#0ff", "#fff"]
        """;

    [Test]
    public async Task ColorsOverrideTheScheme()
    {
        // Act
        var s = SettingsLoader.Parse(TokyoNightWithColors).Settings;

        // Assert
        await Assert.That(s.Colors.Background).IsEqualTo(0x000000u);
        await Assert.That(s.Colors.Foreground).IsEqualTo(ColorSchemes.TokyoNight.Foreground);
        await Assert.That(s.Colors.Ansi[1]).IsEqualTo(0xFF0000u);
    }

    [Test]
    public async Task OverriddenColorsReachThePalette()
    {
        // Arrange
        var s = SettingsLoader.Parse(TokyoNightWithColors).Settings;
        var palette = new Palette();

        // Act
        s.ApplyTo(palette);

        // Assert
        await Assert.That(palette[1]).IsEqualTo(0xFF0000u);
        await Assert.That(palette[9]).IsEqualTo(ColorSchemes.TokyoNight.Brights[1]);
    }

    private const string BadValues = """
        font_size = "big"
        window_background_opacity = 3
        color_scheme = "Nope"
        fnot = 1
        line_height = 1.5
        """;

    [Test]
    public async Task BadValuesAreSkipped()
    {
        // Act
        var r = SettingsLoader.Parse(BadValues, "cfg.toml");

        // Assert
        await Assert.That(r.Settings.LineHeight).IsEqualTo(1.5); // the good value still applies
        await Assert.That(r.Settings.FontSize).IsEqualTo(12);
    }

    [Test]
    public async Task BadValuesAreReported()
    {
        // Act
        var diagnostics = SettingsLoader.Parse(BadValues, "cfg.toml").Diagnostics;

        // Assert
        await Assert.That(diagnostics).Count().IsEqualTo(4);
        await Assert.That(diagnostics[0]).IsEqualTo("cfg.toml:1: font_size: expected a number");
        await Assert.That(diagnostics[1]).IsEqualTo("cfg.toml:2: window_background_opacity: must be between 0 and 1");
        await Assert.That(diagnostics[2]).StartsWith("cfg.toml:3: color_scheme: unknown color scheme \"Nope\"");
        await Assert.That(diagnostics[3]).IsEqualTo("cfg.toml:4: fnot: unknown setting (ignored) — did you mean \"font\"?");
    }

    [Test]
    public async Task SyntaxErrorFallsBackToDefaults()
    {
        // Act
        var r = SettingsLoader.Parse("font_size = 11\nline_height = ", "cfg.toml");

        // Assert
        await Assert.That(r.Settings.FontSize).IsEqualTo(new Settings().FontSize);
        await Assert.That(r.Diagnostics).IsEquivalentTo(["cfg.toml: line 2: expected a value, found end of file"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("#24273a", 0x24273Au)]
    [Arguments("#abc", 0xAABBCCu)]
    [Arguments("rgb:ff/80/00", 0xFF8000u)]
    [Arguments("rgb:ffff/8080/0000", 0xFF8000u)]
    public async Task ParsesColors(string text, uint expected)
    {
        // Act
        bool ok = SettingsLoader.TryParseColor(text, out uint rgb);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(rgb).IsEqualTo(expected);
    }

    private static Terminal BarByDefault() => new(10, 2) { DefaultCursorShape = CursorShape.Bar };

    [Test]
    public async Task DefaultCursorShapeAppliesFromTheStart()
    {
        // Act
        var t = BarByDefault();

        // Assert
        await Assert.That(t.CursorShape).IsEqualTo(CursorShape.Bar);
    }

    [Test]
    public async Task DecscusrChangesTheCursorShape()
    {
        // Arrange
        var t = BarByDefault();

        // Act
        t.Feed("\e[2 q"u8);

        // Assert
        await Assert.That(t.CursorShape).IsEqualTo(CursorShape.Block);
    }

    [Test]
    public async Task DefaultCursorShapeIsRestoredByDecscusrZero()
    {
        // Arrange
        var t = BarByDefault();
        t.Feed("\e[2 q"u8);

        // Act
        t.Feed("\e[0 q"u8);

        // Assert
        await Assert.That(t.CursorShape).IsEqualTo(CursorShape.Bar);
    }
}

/// <summary>A config kept in a dotfiles repo and symlinked into ~/.config: live reload watches where the link points.</summary>
public class ConfigLinkTargetTests
{
    [Test]
    public async Task PlainFileHasNoLinkTarget()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("erikterm-link-");
        string config = Path.Combine(dir.FullName, "erikterm.toml");
        File.WriteAllText(config, "font_size = 11\n");

        // Act
        string? target = ErikTerm.Config.SettingsLoader.ConfigLinkTarget(config);

        // Assert
        await Assert.That(target).IsNull();
        dir.Delete(recursive: true);
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task SymlinkedConfigResolvesToTheRepoFile()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("erikterm-link-");
        string repoFile = Path.Combine(dir.FullName, "repo", "erikterm.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(repoFile)!);
        File.WriteAllText(repoFile, "font_size = 11\n");
        string config = Path.Combine(dir.FullName, "erikterm.toml");
        File.CreateSymbolicLink(config, repoFile);

        // Act
        string? target = ErikTerm.Config.SettingsLoader.ConfigLinkTarget(config);

        // Assert
        await Assert.That(target).IsEqualTo(repoFile);
        dir.Delete(recursive: true);
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task BrokenSymlinkHasNoTarget()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("erikterm-link-");
        string config = Path.Combine(dir.FullName, "erikterm.toml");
        File.CreateSymbolicLink(config, Path.Combine(dir.FullName, "missing.toml"));

        // Act
        string? target = ErikTerm.Config.SettingsLoader.ConfigLinkTarget(config);

        // Assert
        await Assert.That(target).IsNull();
        dir.Delete(recursive: true);
    }

    [Test]
    [Arguments("auto", WindowControlsMode.Auto)]
    [Arguments("on", WindowControlsMode.On)]
    [Arguments("OFF", WindowControlsMode.Off)]
    public async Task WindowControlsParses(string value, WindowControlsMode expected)
    {
        // Act
        var r = SettingsLoader.Parse($"window_controls = \"{value}\"");

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.WindowControls).IsEqualTo(expected);
    }

    [Test]
    [Arguments("grow", ChromeRevealMode.Grow)]
    [Arguments("push", ChromeRevealMode.Push)]
    [Arguments("PUSH", ChromeRevealMode.Push)]
    public async Task ChromeRevealParses(string value, ChromeRevealMode expected)
    {
        // Act
        var r = SettingsLoader.Parse($"chrome_reveal = \"{value}\"");

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.ChromeReveal).IsEqualTo(expected);
    }

    [Test]
    public async Task ChromeRevealDefaultsToGrowAndWarnsOnNonsense()
    {
        // Act
        var r = SettingsLoader.Parse("chrome_reveal = \"slide\"");

        // Assert
        await Assert.That(r.Settings.ChromeReveal).IsEqualTo(ChromeRevealMode.Grow);
        await Assert.That(r.Diagnostics.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WindowControlsDefaultsToAutoAndWarnsOnNonsense()
    {
        // Act
        var r = SettingsLoader.Parse("window_controls = \"sometimes\"");

        // Assert
        await Assert.That(r.Settings.WindowControls).IsEqualTo(WindowControlsMode.Auto);
        await Assert.That(r.Diagnostics).IsNotEmpty();
    }

    [Test]
    [Arguments("auto", WindowBlurMode.Auto)]
    [Arguments("none", WindowBlurMode.None)]
    [Arguments("Acrylic", WindowBlurMode.Acrylic)]
    [Arguments("MICA", WindowBlurMode.Mica)]
    public async Task WindowBlurParses(string value, WindowBlurMode expected)
    {
        // Act
        var r = SettingsLoader.Parse($"window_blur = \"{value}\"");

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.WindowBlur).IsEqualTo(expected);
    }

    [Test]
    public async Task WindowBlurDefaultsToAutoAndWarnsOnNonsense()
    {
        // Act
        var r = SettingsLoader.Parse("window_blur = \"frosted\"");

        // Assert
        await Assert.That(r.Settings.WindowBlur).IsEqualTo(WindowBlurMode.Auto);
        await Assert.That(r.Diagnostics).IsNotEmpty();
    }
}
