using ErikTerm.App;
using ErikTerm.App.SettingsPage;
using ErikTerm.Config;
using ErikTerm.Ui.Compare;

namespace ErikTerm.Tests;

/// <summary>Changing one setting in the user's own file: everything else stays exactly as it was.</summary>
public class TomlEditorTests
{
    [Test]
    public async Task ChangingAValueKeepsItsCommentInItsColumn()
    {
        // Arrange
        const string text = "font_size = 12                       # points\nline_height = 1.0\n";

        // Act
        string result = TomlEditor.Set(text, "font_size", "13.5");

        // Assert
        await Assert.That(result).IsEqualTo("font_size = 13.5                     # points\nline_height = 1.0\n");
    }

    [Test]
    public async Task ACommentedOutExampleIsSwitchedOnInPlace()
    {
        // Arrange
        const string text = "font = \"x\"\n# cursor_blink_style = \"phase\"      # fade or phase\n\n[colors]\nforeground = \"#fff\"\n";

        // Act
        string result = TomlEditor.Set(text, "cursor_blink_style", "\"fade\"");

        // Assert
        await Assert.That(result).IsEqualTo("font = \"x\"\ncursor_blink_style = \"fade\"       # fade or phase\n\n[colors]\nforeground = \"#fff\"\n");
    }

    [Test]
    public async Task ANewKeyStaysAboveTheFirstTable()
    {
        // Arrange
        const string text = "font = \"x\"\n\n[keys]\n\"CTRL+h\" = \"None\"\n";

        // Act
        string result = TomlEditor.Set(text, "font_size", "13");

        // Assert
        await Assert.That(Toml.Parse(result).TryGet("font_size", out var value, out _)).IsTrue();
        await Assert.That(value).IsEqualTo(13L);
        await Assert.That(result).IsEqualTo("font = \"x\"\nfont_size = 13\n\n[keys]\n\"CTRL+h\" = \"None\"\n");
    }

    [Test]
    public async Task AKeyOfTheSameNameInATableIsLeftAlone()
    {
        // Arrange
        const string text = "[worktree_agents.claude]\nargs = [\"--model\", \"sonnet\"]\n";

        // Act
        string result = TomlEditor.Set(text, "args", "[]");

        // Assert: the top-level key goes before the table, the table's own key is untouched
        await Assert.That(result).IsEqualTo("args = []\n[worktree_agents.claude]\nargs = [\"--model\", \"sonnet\"]\n");
    }

    [Test]
    public async Task AValueOverSeveralLinesIsReplacedWhole()
    {
        // Arrange
        const string text = "restore_programs = [\n  \"nvim\", # editor\n  \"less\",\n]\nfont = \"x\"\n";

        // Act
        string result = TomlEditor.Set(text, "restore_programs", "[\"btop\"]");

        // Assert
        await Assert.That(result).IsEqualTo("restore_programs = [\"btop\"]\nfont = \"x\"\n");
    }

    [Test]
    public async Task AHashInsideAStringIsNotAComment()
    {
        // Arrange
        const string text = "color = \"#ff0000\"  # red\n";

        // Act
        string result = TomlEditor.Set(text, "color", "\"#00ff00\"");

        // Assert
        await Assert.That(result).IsEqualTo("color = \"#00ff00\"  # red\n");
    }

    [Test]
    public async Task UnsetCommentsTheLineOut()
    {
        // Arrange
        const string text = "font_size = 13  # points\r\nfont = \"x\"\r\n";

        // Act
        string result = TomlEditor.Unset(text, "font_size");

        // Assert: Windows line endings are kept too
        await Assert.That(result).IsEqualTo("# font_size = 13  # points\r\nfont = \"x\"\r\n");
    }

    [Test]
    public async Task SetThenUnsetThenSetComesBackToTheSameLine()
    {
        // Arrange
        const string text = "font_size = 12 # points\n";

        // Act
        string result = TomlEditor.Set(TomlEditor.Unset(text, "font_size"), "font_size", "12");

        // Assert
        await Assert.That(result).IsEqualTo(text);
    }

    [Test]
    public async Task StringsAreEscaped()
    {
        // Act
        string literal = TomlEditor.Literal("a \"b\" \\ c\n");

        // Assert
        await Assert.That(Toml.Parse($"x = {literal}").TryGet("x", out var value, out _)).IsTrue();
        await Assert.That(value).IsEqualTo("a \"b\" \\ c\n");
    }

    [Test]
    public async Task TheExampleConfigSurvivesEverySettingBeingChanged()
    {
        // Arrange
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "erikterm.example.toml"));

        // Act — each setting the page can change, to its default
        foreach (var info in SettingsCatalog.All.Where(s => s.Default is { Length: > 0 } && s.Kind is SettingKind.Toggle or SettingKind.Number))
            text = TomlEditor.Set(text, info.Key, info.Default!);

        // Assert: still valid, nothing reported, and the tables are where they were
        var result = SettingsLoader.Parse(text);
        await Assert.That(result.SyntaxError).IsFalse();
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(text).Contains("# [colors]");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "erikterm.example.toml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

/// <summary>The page's list of settings matches what the loader reads.</summary>
public class SettingsCatalogTests
{
    [Test]
    public async Task EverySettingTheLoaderReadsIsOnThePage()
    {
        // Act
        var missing = SettingsLoader.KnownKeys.Except(SettingsCatalog.All.Select(s => s.Key)).Except(SettingsCatalog.Hidden).ToList();

        // Assert
        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task EverySettingOnThePageIsOneTheLoaderReads()
    {
        // Act
        var unknown = SettingsCatalog.All.Select(s => s.Key).Except(SettingsLoader.KnownKeys).ToList();

        // Assert
        await Assert.That(unknown).IsEmpty();
    }

    [Test]
    public async Task EveryDefaultIsAValueTheLoaderAccepts()
    {
        // Arrange
        var problems = new List<string>();

        // Act
        foreach (var info in SettingsCatalog.All.Where(s => s.Default is not null && s.Kind is not SettingKind.File))
        {
            string literal = info.Kind switch
            {
                SettingKind.Toggle or SettingKind.Number => info.Default!,
                SettingKind.List => TomlEditor.Literal(info.Default!.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0)),
                _ => TomlEditor.Literal(info.Default!),
            };
            problems.AddRange(SettingsLoader.Parse($"{info.Key} = {literal}").Diagnostics);
        }

        // Assert
        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task EveryChoiceIsAValueTheLoaderAccepts()
    {
        // Act
        var problems = SettingsCatalog.All.SelectMany(s => s.Choices.Select(c => (s.Key, c)))
            .SelectMany(p => SettingsLoader.Parse($"{p.Key} = {TomlEditor.Literal(p.c)}").Diagnostics).ToList();

        // Assert
        await Assert.That(problems).IsEmpty();
    }

    [Test]
    public async Task EveryGroupIsListed()
    {
        // Act
        var groups = SettingsCatalog.All.Select(s => s.Group).Distinct().Except(SettingsCatalog.Groups).ToList();

        // Assert
        await Assert.That(groups).IsEmpty();
    }

    [Test]
    public async Task TheSettingsCommandIsItsOwnEntry()
    {
        // Act
        var entry = EntryCommand.Classify(["settings"], invokedAsTmux: false, "--erikterm-pty-exec");

        // Assert
        await Assert.That(entry).IsEqualTo(EntryKind.Settings);
    }
}

/// <summary>The settings page driven like a user would, against a file in memory.</summary>
public class SettingsScreenTests
{
    private sealed class MemoryHost(string text) : ISettingsHost
    {
        public string Text { get; private set; } = text;
        public int Writes { get; private set; }
        public int? EditedAtLine { get; private set; }
        public string DisplayPath => "~/.config/erikterm/erikterm.toml";
        public string Read() => Text;

        public string? Write(string text)
        {
            Text = text;
            Writes++;
            return null;
        }

        public IReadOnlyList<string> Problems(string text) => SettingsLoader.Parse(text).Diagnostics;
        public IReadOnlyList<string> SchemeNames() => [.. ColorSchemes.All.Select(s => s.Name)];
        public void EditFile(int line) => EditedAtLine = line;
    }

    private static SettingsScreen At(MemoryHost host, string key)
    {
        var screen = new SettingsScreen(host);
        while (screen.Selected.Key != key) screen.OnKey(new KeyEvent(TuiKey.Down));
        return screen;
    }

    [Test]
    public async Task RightStepsANumberAndSavesIt()
    {
        // Arrange
        var host = new MemoryHost("font_size = 12 # points\n");
        var screen = At(host, "font_size");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Right));

        // Assert
        await Assert.That(host.Text).IsEqualTo("font_size = 12.5 # points\n");
        await Assert.That(screen.Status).Contains("font_size = 12.5");
    }

    [Test]
    public async Task EnterFlipsATogglePresentOrNot()
    {
        // Arrange
        var host = new MemoryHost("");
        var screen = At(host, "cursor_animation");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.Text).IsEqualTo("cursor_animation = false\n");
    }

    [Test]
    public async Task ChoicesGoRound()
    {
        // Arrange
        var host = new MemoryHost("ctrl_tab_order = \"position\"\n");
        var screen = At(host, "ctrl_tab_order");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Right));

        // Assert
        await Assert.That(host.Text).IsEqualTo("ctrl_tab_order = \"recent\"\n");
    }

    [Test]
    public async Task ColourSchemesComeFromTheHost()
    {
        // Arrange
        var host = new MemoryHost("color_scheme = \"erik\"\n");
        var screen = At(host, "color_scheme");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Right));

        // Assert
        await Assert.That(host.Text).IsEqualTo($"color_scheme = \"{ColorSchemes.All[1].Name}\"\n");
    }

    [Test]
    public async Task TypedTextIsSavedAsAString()
    {
        // Arrange
        var host = new MemoryHost("");
        var screen = At(host, "font");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));
        screen.OnKey(KeyEvent.CtrlOf('u'));
        foreach (char c in "Iosevka Term") screen.OnKey(KeyEvent.OfChar(c));
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.Text).IsEqualTo("font = \"Iosevka Term\"\n");
    }

    [Test]
    public async Task AValueOutOfRangeIsNotWritten()
    {
        // Arrange
        var host = new MemoryHost("font_size = 12\n");
        var screen = At(host, "font_size");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));
        screen.OnKey(KeyEvent.CtrlOf('u'));
        foreach (char c in "900") screen.OnKey(KeyEvent.OfChar(c));
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.Writes).IsEqualTo(0);
        await Assert.That(screen.Status).Contains("goes from 4 to 200");
    }

    [Test]
    public async Task ResetAndUndo()
    {
        // Arrange
        var host = new MemoryHost("font_size = 14\n");
        var screen = At(host, "font_size");

        // Act
        screen.OnKey(KeyEvent.OfChar('r'));
        string afterReset = host.Text;
        screen.OnKey(KeyEvent.OfChar('u'));

        // Assert
        await Assert.That(afterReset).IsEqualTo("# font_size = 14\n");
        await Assert.That(host.Text).IsEqualTo("font_size = 14\n");
    }

    [Test]
    public async Task ASyntaxErrorStopsChanges()
    {
        // Arrange
        var host = new MemoryHost("font_size = = 12\n");
        var screen = At(host, "font_size");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Right));

        // Assert
        await Assert.That(host.Writes).IsEqualTo(0);
        await Assert.That(screen.Status).Contains("syntax error");
    }

    [Test]
    public async Task SearchNarrowsTheList()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));

        // Act
        screen.OnKey(KeyEvent.OfChar('/'));
        foreach (char c in "blur") screen.OnKey(KeyEvent.OfChar(c));

        // Assert
        await Assert.That(screen.Visible.Select(s => s.Key)).Contains("window_blur");
        await Assert.That(screen.Visible.Count).IsLessThan(5);
    }

    [Test]
    public async Task ATableSettingOpensTheEditorOnItsLine()
    {
        // Arrange
        var host = new MemoryHost("font = \"x\"\n\n[colors]\nforeground = \"#ffffff\"\n");
        var screen = At(host, "colors");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.EditedAtLine).IsEqualTo(3);
    }

    [Test]
    public async Task ItDrawsLabelsKeysAndExplanations()
    {
        // Arrange
        var screen = At(new MemoryHost("font_size = 13\n"), "font_size");
        var frame = new Frame(120, 40);

        // Act
        screen.Render(frame);
        string text = frame.AllText();

        // Assert
        await Assert.That(text).Contains("Font size  font_size");
        await Assert.That(text).Contains("‹ 13 pt ›");
        await Assert.That(text).Contains("Ctrl+= and Ctrl+- zoom a single window");
        await Assert.That(text).Contains("1 setting changed");
    }
}
