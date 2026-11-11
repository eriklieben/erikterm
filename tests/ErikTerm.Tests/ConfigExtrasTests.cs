using ErikTerm.Config;

namespace ErikTerm.Tests;

public class ConfigOverrideTests
{
    private static ConfigOverride O(string arg)
    {
        if (!ConfigOverrides.TryParse(arg, out var o, out string error)) Assert.Fail(error);
        return o!;
    }

    private static LoadResult Parse(string toml, params string[] overrides) =>
        SettingsLoader.Parse(toml, "cfg.toml", new LoadOptions { Overrides = overrides.Select(O).ToList() });

    [Test]
    public async Task TomlValues()
    {
        // Act
        var s = Parse("", "font_size=13", "enable_tab_bar=false", "default_prog=[\"fish\", \"-l\"]", "color_scheme=\"Tokyo Night\"").Settings;

        // Assert
        await Assert.That(s.FontSize).IsEqualTo(13);
        await Assert.That(s.EnableTabBar).IsFalse();
        await Assert.That(s.DefaultProg).IsEquivalentTo(new[] { "fish", "-l" }, CollectionOrdering.Matching);
        await Assert.That(s.Colors.Name).IsEqualTo("Tokyo Night");
    }

    [Test]
    [Arguments("color_scheme=Tokyo Night", "Tokyo Night")]
    [Arguments("color_scheme = Tokyo Night ", "Tokyo Night")]
    [Arguments("font=JetBrains Mono", "JetBrains Mono")]
    [Arguments("font=true story", "true story")]
    [Arguments("font=#ff0000", "#ff0000")]
    [Arguments("font=", "")]
    public async Task BareWordsAreStrings(string arg, string expected)
    {
        // Arrange
        string key = arg[..arg.IndexOf('=')].Trim();

        // Act
        var o = O(arg);
        bool found = o.Table.TryGet(key, out var v, out _);

        // Assert
        await Assert.That(found).IsTrue();
        await Assert.That(v).IsEqualTo(expected);
    }

    [Test]
    public async Task OverridesWinOverTheFile()
    {
        // Act
        var r = Parse("font_size = 11\nline_height = 1.2\n", "font_size=14");

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.FontSize).IsEqualTo(14);
        await Assert.That(r.Settings.LineHeight).IsEqualTo(1.2); // the rest of the file still applies
    }

    [Test]
    public async Task LaterOverridesWin()
    {
        // Act
        double fontSize = Parse("", "font_size=13", "font_size=15").Settings.FontSize;

        // Assert
        await Assert.That(fontSize).IsEqualTo(15);
    }

    [Test]
    public async Task DottedKeysMergeIntoTables()
    {
        // Act
        var s = Parse("""
            color_scheme = "Tokyo Night"
            [colors]
            background = "#000000"
            """, "colors.split=#ff0000", "colors.foreground=\"#00ff00\"").Settings;

        // Assert
        await Assert.That(s.Colors.Background).IsEqualTo(0x000000u); // kept from the file
        await Assert.That(s.Colors.Split).IsEqualTo(0xFF0000u);
        await Assert.That(s.Colors.Foreground).IsEqualTo(0x00FF00u);
    }

    [Test]
    public async Task InlineTablesReplace()
    {
        // Act
        var s = Parse("window_padding = { left = 9, right = 9, top = 9, bottom = 9 }", "window_padding={ left = 2 }").Settings;

        // Assert
        await Assert.That(s.WindowPadding).IsEqualTo(new Padding(2, 0, 0, 0));
    }

    [Test]
    public async Task KeyBindingOverride()
    {
        // Act
        var s = Parse("", "keys.\"CTRL+h\"=ActivatePaneDirection(Left)").Settings;

        // Assert
        var b = await Assert.That(s.Keys).HasSingleItem();
        await Assert.That(b!.Action.Name).IsEqualTo("ActivatePaneDirection");
    }

    [Test]
    public async Task BadOverrideValuesAreReportedAsSet()
    {
        // Act
        var r = Parse("font_size = 11", "font_size=1000", "nonsense=1");

        // Assert
        await Assert.That(r.Settings.FontSize).IsEqualTo(new Settings().FontSize); // the override replaced the file's value, then was rejected
        await Assert.That(r.Diagnostics).Contains("--set: font_size: must be between 4 and 200");
        await Assert.That(r.Diagnostics).Contains("--set: nonsense: unknown setting (ignored)");
    }

    [Test]
    public async Task OverridesSurviveASyntaxErrorInTheFile()
    {
        // Act
        var r = Parse("font_size = ", "font_size=13");

        // Assert
        await Assert.That(r.Settings.FontSize).IsEqualTo(13);
        await Assert.That(r.Diagnostics).HasSingleItem();
    }

    [Test]
    public async Task AppliesWithoutAConfigFile()
    {
        // Arrange
        string missing = Path.Combine(Path.GetTempPath(), "et-none-" + Guid.NewGuid().ToString("N"), "erikterm.toml");
        var options = new LoadOptions { Overrides = [O("font_size=13")] };

        // Act
        var r = SettingsLoader.Load(missing, options);

        // Assert
        await Assert.That(r.Path).IsNull();
        await Assert.That(r.Settings.FontSize).IsEqualTo(13);
    }

    [Test]
    [Arguments("font_size")]
    [Arguments("=13")]
    [Arguments("bad key=1")]
    [Arguments("a..b=1")]
    public async Task RejectsMalformed(string arg)
    {
        // Act
        bool parsed = ConfigOverrides.TryParse(arg, out _, out string error);

        // Assert
        await Assert.That(parsed).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }
}

public class ColorSchemeFileTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "et-schemes-" + Guid.NewGuid().ToString("N"));

    [After(Test)]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private const string Wezterm = """
        [colors]
        ansi = ["#000000", "#cc0000", "#00cc00", "#cccc00", "#0000cc", "#cc00cc", "#00cccc", "#cccccc"]
        brights = ["#555555", "#ff0000", "#00ff00", "#ffff00", "#0000ff", "#ff00ff", "#00ffff", "#ffffff"]
        background = "#101010"
        foreground = "#e0e0e0"
        cursor_bg = "#ff8800"
        cursor_border = "#ff8800"
        cursor_fg = "#101010"
        selection_bg = "#333333"
        selection_fg = "#ffffff"

        [colors.indexed]
        16 = "#123456"

        [metadata]
        aliases = []
        author = "someone"
        name = "My Scheme"
        origin_url = "https://example.com"
        """;

    /// <summary>The <see cref="Wezterm"/> scheme as a selectable file scheme.</summary>
    private static LoadOptions WithWeztermScheme() =>
        new() { Schemes = [ColorSchemeFiles.Parse(Wezterm, "my.toml", [])!] };

    /// <summary>A config selecting "my scheme" from a colors directory that also holds a broken scheme file.</summary>
    private LoadResult LoadWithSchemeDirectory()
    {
        string dir = Dir("colors");
        File.WriteAllText(Path.Combine(dir, "mine.toml"), Wezterm);
        File.WriteAllText(Path.Combine(dir, "bad.toml"), "[colors]\nforeground = 3");
        string config = Path.Combine(_root, "erikterm.toml");
        File.WriteAllText(config, "color_scheme = \"my scheme\"\n[colors]\nsplit = \"#abcdef\"\n");
        return SettingsLoader.Load(config, new LoadOptions { SchemeDirectories = [dir] });
    }

    [Test]
    public async Task ParsesWeztermFormat()
    {
        // Arrange
        var diags = new List<string>();

        // Act
        var s = ColorSchemeFiles.Parse(Wezterm, "my.toml", diags)!;

        // Assert
        await Assert.That(diags).IsEmpty();
        await Assert.That(s.Name).IsEqualTo("My Scheme");
        await Assert.That((s.Foreground, s.Background, s.CursorBg, s.CursorFg, s.SelectionBg)).IsEqualTo((0xE0E0E0u, 0x101010u, 0xFF8800u, 0x101010u, 0x333333u));
        await Assert.That(s.Ansi[1]).IsEqualTo(0xCC0000u);
        await Assert.That(s.Brights[7]).IsEqualTo(0xFFFFFFu);
        await Assert.That(s.SelectionFg).IsEqualTo(0xFFFFFFu);
        await Assert.That(s.CursorBorder).IsEqualTo(0xFF8800u);
        await Assert.That(s.Accent).IsEqualTo(0x0000CCu); // derived: blue
    }

    [Test]
    public async Task NameFallsBackToFileName()
    {
        // Act
        var s = ColorSchemeFiles.Parse("[colors]\nforeground = \"#fff\"\nbackground = \"#000\"", "/x/Night Owl.toml", [])!;

        // Assert
        await Assert.That(s.Name).IsEqualTo("Night Owl");
        await Assert.That(s.Ansi).IsEquivalentTo(ColorSchemes.Erik.Ansi, CollectionOrdering.Matching); // missing palettes fall back
    }

    [Test]
    [Arguments("[colors\n", "line 1")]
    [Arguments("name = 1", "expected a [colors] table")]
    [Arguments("[colors]\nforeground = \"#fff\"", "foreground and background are required")]
    [Arguments("[colors]\nforeground = \"red\"\nbackground = \"#000\"", "bad.toml:2: colors.foreground: expected a color")]
    [Arguments("[colors]\nforeground = \"#fff\"\nbackground = \"#000\"\nansi = [\"#000\"]", "bad.toml:4: colors.ansi: expected an array of 8 colors")]
    public async Task BadFilesAreDiagnosed(string toml, string expected)
    {
        // Arrange
        var diags = new List<string>();

        // Act
        var scheme = ColorSchemeFiles.Parse(toml, "bad.toml", diags);

        // Assert
        await Assert.That(scheme).IsNull();
        await Assert.That(diags).Contains(d => d.Contains(expected));
    }

    [Test]
    public async Task LoadsDirectoriesFirstOneWins()
    {
        // Arrange
        string mine = Dir("erikterm-colors"), wez = Dir("wezterm-colors");
        File.WriteAllText(Path.Combine(mine, "a.toml"), Wezterm);
        File.WriteAllText(Path.Combine(wez, "a.toml"), Wezterm.Replace("#101010", "#202020"));
        File.WriteAllText(Path.Combine(wez, "b.toml"), "[colors]\nforeground = \"#fff\"\nbackground = \"#111\"\n[metadata]\nname = \"Other\"");
        File.WriteAllText(Path.Combine(wez, "broken.toml"), "[colors");
        File.WriteAllText(Path.Combine(wez, "ignored.txt"), "not a scheme");
        var diags = new List<string>();

        // Act
        var schemes = ColorSchemeFiles.LoadDirectories([mine, wez, Path.Combine(_root, "missing")], diags);

        // Assert
        await Assert.That(schemes.Select(s => s.Name)).IsEquivalentTo(new[] { "My Scheme", "Other" }, CollectionOrdering.Matching);
        await Assert.That(schemes[0].Background).IsEqualTo(0x101010u);
        await Assert.That(diags).HasSingleItem(d => d.Contains("broken.toml"));
    }

    [Test]
    public async Task ColorSchemeResolvesAgainstFiles()
    {
        // Act
        var r = LoadWithSchemeDirectory();

        // Assert
        await Assert.That(r.Settings.Colors.Name).IsEqualTo("My Scheme");
        await Assert.That(r.Settings.Colors.Split).IsEqualTo(0xABCDEFu); // [colors] still applies on top
        await Assert.That(r.CustomSchemes.Select(s => s.Name)).IsEquivalentTo(new[] { "My Scheme" }, CollectionOrdering.Matching);
        await Assert.That(r.Diagnostics).HasSingleItem(d => d.Contains("bad.toml") && d.Contains("colors.foreground"));
    }

    [Test]
    public async Task FileSchemesListFirstAndBuiltInsStillResolve()
    {
        // Arrange
        var r = LoadWithSchemeDirectory();

        // Act
        // Built-ins still work, and the list puts file schemes first without duplicates.
        var all = ColorSchemes.WithCustom(r.CustomSchemes);
        var tokyoNight = ColorSchemes.Find("Tokyo Night (Gogh)", r.CustomSchemes);

        // Assert
        await Assert.That(all[0].Name).IsEqualTo("My Scheme");
        await Assert.That(all.Count).IsEqualTo(ColorSchemes.All.Length + 1);
        await Assert.That(tokyoNight).IsSameReferenceAs(ColorSchemes.TokyoNight);
    }

    [Test]
    public async Task FileSchemeShadowsBuiltInOfTheSameName()
    {
        // Arrange
        var custom = ColorSchemeFiles.Parse(Wezterm.Replace("My Scheme", "Tokyo Night"), "t.toml", [])!;

        // Act
        var found = ColorSchemes.Find("tokyo night", [custom]);
        var all = ColorSchemes.WithCustom([custom]);

        // Assert
        await Assert.That(found).IsSameReferenceAs(custom);
        await Assert.That(all.Count).IsEqualTo(ColorSchemes.All.Length);
        await Assert.That(all).HasSingleItem(s => s.Name == "Tokyo Night");
    }

    [Test]
    public async Task UnknownSchemeMentionsFileSchemes()
    {
        // Arrange
        var custom = ColorSchemeFiles.Parse(Wezterm, "my.toml", [])!;

        // Act
        var r = SettingsLoader.Parse("color_scheme = \"Nope\"", "cfg.toml", new LoadOptions { Schemes = [custom] });

        // Assert
        await Assert.That(r.Diagnostics).Contains(d => d.Contains("from files: My Scheme"));
    }

    [Test]
    public async Task SelectionFgAndCursorBorderComeFromTheSchemeFile()
    {
        // Arrange
        var opts = WithWeztermScheme();

        // Act
        var r = SettingsLoader.Parse("color_scheme = \"My Scheme\"", "cfg.toml", opts);

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.Colors.SelectionFg).IsEqualTo(0xFFFFFFu);
        await Assert.That(r.Settings.Colors.CursorBorder).IsEqualTo(0xFF8800u);
    }

    [Test]
    public async Task SelectionFgAndCursorBorderFromTheConfigWinOverTheScheme()
    {
        // Arrange
        var opts = WithWeztermScheme();

        // Act
        // [colors] in the main config overrides it.
        var r = SettingsLoader.Parse("color_scheme = \"My Scheme\"\n[colors]\nselection_fg = \"#010203\"\ncursor_border = \"#0a0b0c\"", "cfg.toml", opts);

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.Colors.SelectionFg).IsEqualTo(0x010203u);
        await Assert.That(r.Settings.Colors.CursorBorder).IsEqualTo(0x0A0B0Cu);
    }

    [Test]
    public async Task BuiltInSchemesHaveNoSelectionFgOrCursorBorder()
    {
        // Arrange
        var opts = WithWeztermScheme();

        // Act
        // Built-in schemes have neither, so selected text keeps its colors.
        var r = SettingsLoader.Parse("color_scheme = \"Tokyo Night\"", "cfg.toml", opts);

        // Assert
        await Assert.That(r.Settings.Colors.SelectionFg).IsNull();
        await Assert.That(r.Settings.Colors.CursorBorder).IsNull();
    }

    [Test]
    public async Task ConfigColorsCanAddSelectionFgToABuiltInScheme()
    {
        // Arrange
        var opts = WithWeztermScheme();

        // Act
        // [colors] can still add them.
        var r = SettingsLoader.Parse("[colors]\nselection_fg = \"#fff\"", "cfg.toml", opts);

        // Assert
        await Assert.That(r.Settings.Colors.SelectionFg).IsEqualTo(0xFFFFFFu);
        await Assert.That(r.Settings.Colors.CursorBorder).IsNull();
    }

    [Test]
    public async Task BadCursorBorderIsReportedAndIgnored()
    {
        // Arrange
        var opts = WithWeztermScheme();

        // Act
        // A bad value is reported and ignored.
        var r = SettingsLoader.Parse("[colors]\ncursor_border = \"nope\"", "cfg.toml", opts);

        // Assert
        await Assert.That(r.Diagnostics).Contains(d => d.Contains("colors.cursor_border"));
        await Assert.That(r.Settings.Colors.CursorBorder).IsNull();
    }

    [Test]
    public async Task SelectionFgCanBeSetWithAnOverride()
    {
        // Act
        bool parsed = ConfigOverrides.TryParse("colors.selection_fg=#123456", out var o, out _);
        var r = SettingsLoader.Parse("", "cfg.toml", new LoadOptions { Overrides = [o!] });

        // Assert
        await Assert.That(parsed).IsTrue();
        await Assert.That(r.Settings.Colors.SelectionFg).IsEqualTo(0x123456u);
    }

    [Test]
    public async Task DefaultDirectoriesIncludeErikAndWezterm()
    {
        // Act
        var dirs = ColorSchemeFiles.DefaultDirectories(Path.Combine(_root, "erikterm.toml"));

        // Assert
        await Assert.That(dirs[0]).IsEqualTo(Path.Combine(_root, "colors"));
        await Assert.That(dirs).Contains(d => d.EndsWith(Path.Combine("wezterm", "colors"), StringComparison.Ordinal));
        await Assert.That(dirs.Distinct().Count()).IsEqualTo(dirs.Count);
    }

    [Test]
    public async Task NearestExistingAncestor()
    {
        // Arrange
        Directory.CreateDirectory(_root);
        string target = Path.Combine(_root, "wezterm", "colors");

        // Act
        var beforeParentExists = ErikTerm.App.SchemeDirectoryWatch.NearestExistingAncestor(target);
        Directory.CreateDirectory(Path.Combine(_root, "wezterm"));
        var afterParentExists = ErikTerm.App.SchemeDirectoryWatch.NearestExistingAncestor(target);

        // Assert
        await Assert.That(beforeParentExists).IsEqualTo((_root, "wezterm"));
        await Assert.That(afterParentExists).IsEqualTo((Path.Combine(_root, "wezterm"), "colors"));
    }

    [Test]
    public async Task ADirectoryCreatedWhileArmingIsStillWatched()
    {
        // Arrange: the directory appears after the watch looked and before its watcher runs, so no event will report it.
        Directory.CreateDirectory(_root);
        string colors = Path.Combine(_root, "colors");
        int changes = 0;

        // Act
        using var watch = new ErikTerm.App.SchemeDirectoryWatch(colors, () => Interlocked.Increment(ref changes),
            afterLooking: () => Directory.CreateDirectory(colors));
        bool watchingFiles = watch.WatchingFiles;
        int reported = Volatile.Read(ref changes);

        // Assert
        await Assert.That(watchingFiles).IsTrue();
        await Assert.That(reported).IsEqualTo(1);
    }

    [Test]
    [NotInParallel(Isolation.FileWatchers)]
    public async Task WatchFollowsDirectoriesCreatedLater()
    {
        // One walk-through: every step waits for the watcher to react to the previous one (it re-arms on a
        // thread-pool thread) before the next directory or file change is made.

        // Arrange
        Directory.CreateDirectory(_root);
        string parent = Path.Combine(_root, "wezterm"), colors = Path.Combine(parent, "colors");
        int changes = 0;

        // Act 1: watch a directory two levels below what exists
        using var watch = new ErikTerm.App.SchemeDirectoryWatch(colors, () => Interlocked.Increment(ref changes));
        bool watchingFilesAtStart = watch.WatchingFiles;
        string? watchingForAtStart = watch.WatchingFor;

        // Assert 1
        await Assert.That(watchingFilesAtStart).IsFalse();
        await Assert.That(watchingForAtStart).IsEqualTo(parent);

        // Act 2: one level down
        Directory.CreateDirectory(parent);
        bool followedParent = await Eventually.True(() => watch.WatchingFor == colors);

        // Assert 2
        await Assert.That(followedParent).IsTrue().Because("timed out waiting for the parent directory");

        // Act 3: the colors directory itself: armed + a reload
        Directory.CreateDirectory(colors);
        bool armed = await Eventually.True(() => watch.WatchingFiles);
        bool reloaded = await Eventually.True(() => Volatile.Read(ref changes) > 0);

        // Assert 3
        await Assert.That(armed).IsTrue().Because("timed out waiting for the colors directory");
        await Assert.That(reloaded).IsTrue().Because("timed out waiting for a change for the new directory");

        // Act 4: a scheme file appears
        int before = Volatile.Read(ref changes);
        File.WriteAllText(Path.Combine(colors, "new.toml"), "[colors]\nforeground = \"#fff\"\nbackground = \"#000\"");
        bool fileChanged = await Eventually.True(() => Volatile.Read(ref changes) > before);

        // Assert 4
        await Assert.That(fileChanged).IsTrue().Because("timed out waiting for a scheme file change");

        // Act 5: deleted ...
        Directory.Delete(colors, recursive: true);
        bool disarmed = await Eventually.True(() => !watch.WatchingFiles);

        // Assert 5
        await Assert.That(disarmed).IsTrue().Because("timed out waiting for the deletion");

        // Act 6: ... and re-created: re-armed
        Directory.CreateDirectory(colors);
        bool rearmed = await Eventually.True(() => watch.WatchingFiles);

        // Assert 6
        await Assert.That(rearmed).IsTrue().Because("timed out waiting for the re-created directory");

        // Act 7: a file in the re-created directory
        before = Volatile.Read(ref changes);
        File.WriteAllText(Path.Combine(colors, "again.toml"), "x");
        bool changedAgain = await Eventually.True(() => Volatile.Read(ref changes) > before);

        // Assert 7
        await Assert.That(changedAgain).IsTrue().Because("timed out waiting for a change in the re-created directory");
    }
}
