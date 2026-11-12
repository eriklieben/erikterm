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

    // ---- Moving around -----------------------------------------------------------------------------

    [Test]
    public async Task JAndKMoveLikeTheArrows()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));
        string first = screen.Selected.Key;

        // Act
        screen.OnKey(KeyEvent.OfChar('j'));
        screen.OnKey(KeyEvent.OfChar('j'));
        screen.OnKey(KeyEvent.OfChar('k'));

        // Assert
        await Assert.That(screen.Selected).IsEqualTo(screen.Visible[1]);
        await Assert.That(screen.Visible[0].Key).IsEqualTo(first);
    }

    [Test]
    public async Task PageKeysMoveFiveAndHomeEndGoToTheEnds()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));

        // Act
        screen.OnKey(new KeyEvent(TuiKey.PageDown));
        var five = screen.Selected;
        screen.OnKey(new KeyEvent(TuiKey.End));
        var last = screen.Selected;
        screen.OnKey(new KeyEvent(TuiKey.PageUp));
        var lastMinusFive = screen.Selected;
        screen.OnKey(new KeyEvent(TuiKey.Home));

        // Assert
        await Assert.That(five).IsEqualTo(screen.Visible[5]);
        await Assert.That(last).IsEqualTo(screen.Visible[^1]);
        await Assert.That(lastMinusFive).IsEqualTo(screen.Visible[^6]);
        await Assert.That(screen.Selected).IsEqualTo(screen.Visible[0]);
    }

    [Test]
    public async Task TabGoesToTheNextGroupAndBackTabToTheStartOfThisOne()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));
        string firstGroup = screen.Selected.Group;
        screen.OnKey(new KeyEvent(TuiKey.Tab));
        string secondGroup = screen.Selected.Group;
        screen.OnKey(new KeyEvent(TuiKey.Down));

        // Act
        screen.OnKey(new KeyEvent(TuiKey.BackTab));
        var startOfSecond = screen.Selected;
        screen.OnKey(new KeyEvent(TuiKey.BackTab));

        // Assert
        await Assert.That(secondGroup).IsNotEqualTo(firstGroup);
        await Assert.That(startOfSecond.Group).IsEqualTo(secondGroup);
        await Assert.That(screen.Visible.First(s => s.Group == secondGroup)).IsEqualTo(startOfSecond);
        await Assert.That(screen.Selected).IsEqualTo(screen.Visible[0]);
    }

    [Test]
    public async Task TabOnTheLastGroupStays()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));
        screen.OnKey(new KeyEvent(TuiKey.End));
        var last = screen.Selected;

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Tab));

        // Assert
        await Assert.That(screen.Selected).IsEqualTo(last);
    }

    [Test]
    [Arguments('q')]
    public async Task QQuits(char key)
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));

        // Act
        screen.OnKey(KeyEvent.OfChar(key));

        // Assert
        await Assert.That(screen.Done).IsTrue();
        await Assert.That(screen.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task CtrlCQuits()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));

        // Act
        screen.OnKey(KeyEvent.CtrlOf('c'));

        // Assert
        await Assert.That(screen.Done).IsTrue();
    }

    [Test]
    public async Task EscWithASearchClearsItFirst()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));
        int all = screen.Visible.Count;
        screen.OnKey(KeyEvent.OfChar('/'));
        foreach (char c in "blur") screen.OnKey(KeyEvent.OfChar(c));
        screen.OnKey(new KeyEvent(TuiKey.Enter)); // leaves the search field, the filter stays

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Esc));
        bool doneAfterFirst = screen.Done;
        screen.OnKey(new KeyEvent(TuiKey.Esc));

        // Assert
        await Assert.That(doneAfterFirst).IsFalse();
        await Assert.That(screen.Visible.Count).IsEqualTo(all);
        await Assert.That(screen.Done).IsTrue();
    }

    [Test]
    public async Task EscInTheSearchFieldDropsTheSearch()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));
        int all = screen.Visible.Count;
        screen.OnKey(KeyEvent.OfChar('/'));
        foreach (char c in "blur") screen.OnKey(KeyEvent.OfChar(c));

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Esc));

        // Assert
        await Assert.That(screen.Visible.Count).IsEqualTo(all);
        await Assert.That(screen.Done).IsFalse();
    }

    [Test]
    public async Task BackspaceInTheSearchWidensIt()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));
        screen.OnKey(KeyEvent.OfChar('/'));
        foreach (char c in "blurx") screen.OnKey(KeyEvent.OfChar(c));
        int none = screen.Visible.Count;

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Backspace));

        // Assert
        await Assert.That(none).IsEqualTo(0);
        await Assert.That(screen.Visible.Select(s => s.Key)).Contains("window_blur");
    }

    [Test]
    public async Task PastingIntoTheSearchFilters()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));
        screen.OnKey(KeyEvent.OfChar('/'));

        // Act
        screen.OnPaste("window_blur");

        // Assert
        await Assert.That(screen.WantsPaste).IsTrue();
        await Assert.That(screen.Visible.Single().Key).IsEqualTo("window_blur");
    }

    [Test]
    public async Task WithNothingListedTheValueKeysDoNothing()
    {
        // Arrange
        var host = new MemoryHost("");
        var screen = new SettingsScreen(host);
        screen.OnKey(KeyEvent.OfChar('/'));
        foreach (char c in "zzqqxx") screen.OnKey(KeyEvent.OfChar(c));
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Act
        foreach (var key in new[] { new KeyEvent(TuiKey.Right), new KeyEvent(TuiKey.Left), new KeyEvent(TuiKey.Enter), KeyEvent.OfChar(' '), KeyEvent.OfChar('r'), new KeyEvent(TuiKey.Tab) })
            screen.OnKey(key);

        // Assert
        await Assert.That(host.Writes).IsEqualTo(0);
    }

    // ---- Changing values ---------------------------------------------------------------------------

    [Test]
    public async Task HAndLStepANumber()
    {
        // Arrange
        var host = new MemoryHost("font_size = 12\n");
        var screen = At(host, "font_size");

        // Act
        screen.OnKey(KeyEvent.OfChar('l'));
        screen.OnKey(KeyEvent.OfChar('l'));
        screen.OnKey(KeyEvent.OfChar('h'));

        // Assert
        await Assert.That(host.Text).IsEqualTo("font_size = 12.5\n");
    }

    [Test]
    public async Task LeftFromAnUnknownChoiceTakesTheLast()
    {
        // Arrange
        var host = new MemoryHost("ctrl_tab_order = \"sideways\"\n");
        var screen = At(host, "ctrl_tab_order");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Left));

        // Assert: the loader warns about the old value, but the new one is fine.
        await Assert.That(host.Writes).IsEqualTo(1);
        await Assert.That(host.Text).Contains("ctrl_tab_order = ");
        await Assert.That(host.Text).DoesNotContain("sideways");
    }

    [Test]
    public async Task SpaceFlipsAToggle()
    {
        // Arrange
        var host = new MemoryHost("");
        var screen = At(host, "cursor_animation");

        // Act
        screen.OnKey(KeyEvent.OfChar(' '));

        // Assert
        await Assert.That(host.Text).IsEqualTo("cursor_animation = false\n");
    }

    [Test]
    public async Task ATypedListIsSplitAtCommas()
    {
        // Arrange
        var host = new MemoryHost("");
        var screen = At(host, "font_fallback");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));
        screen.OnPaste("Noto Sans,  Symbola , ");
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.Text).IsEqualTo("font_fallback = [\"Noto Sans\", \"Symbola\"]\n");
    }

    [Test]
    public async Task ANumberThatIsNotOneIsRefused()
    {
        // Arrange
        var host = new MemoryHost("font_size = 12\n");
        var screen = At(host, "font_size");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));
        screen.OnKey(KeyEvent.CtrlOf('u'));
        foreach (char c in "big") screen.OnKey(KeyEvent.OfChar(c));
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.Writes).IsEqualTo(0);
        await Assert.That(screen.Status).IsEqualTo("\"big\" isn't a number");
    }

    [Test]
    public async Task ACommaIsADecimalPointToo()
    {
        // Arrange
        var host = new MemoryHost("font_size = 12\n");
        var screen = At(host, "font_size");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));
        screen.OnKey(KeyEvent.CtrlOf('u'));
        foreach (char c in "13,5") screen.OnKey(KeyEvent.OfChar(c));
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.Text).IsEqualTo("font_size = 13.5\n");
    }

    [Test]
    public async Task EscCancelsTypingAndBackspaceEdits()
    {
        // Arrange
        var host = new MemoryHost("");
        var screen = At(host, "font");
        screen.OnKey(new KeyEvent(TuiKey.Enter));
        screen.OnKey(KeyEvent.CtrlOf('u'));
        foreach (char c in "abc") screen.OnKey(KeyEvent.OfChar(c));
        screen.OnKey(new KeyEvent(TuiKey.Backspace));
        bool editing = screen.Editing;

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Esc));

        // Assert
        await Assert.That(editing).IsTrue();
        await Assert.That(screen.Editing).IsFalse();
        await Assert.That(host.Writes).IsEqualTo(0);
        await Assert.That(screen.Done).IsFalse();
    }

    [Test]
    public async Task AnEmptyValueWithoutADefaultResetsIt()
    {
        // Arrange
        var host = new MemoryHost("default_prog = [\"fish\"]\n");
        var screen = At(host, "default_prog");

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));
        screen.OnKey(KeyEvent.CtrlOf('u'));
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(host.Text).IsEqualTo("# default_prog = [\"fish\"]\n");
    }

    [Test]
    public async Task ResettingADefaultSaysSo()
    {
        // Arrange
        var host = new MemoryHost("");
        var screen = At(host, "font_size");

        // Act
        screen.OnKey(KeyEvent.OfChar('r'));

        // Assert
        await Assert.That(screen.Status).IsEqualTo("Font size is already the default");
        await Assert.That(host.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task UndoWithNothingToUndoSaysSo()
    {
        // Arrange
        var screen = new SettingsScreen(new MemoryHost(""));

        // Act
        screen.OnKey(KeyEvent.OfChar('u'));

        // Assert
        await Assert.That(screen.Status).IsEqualTo("Nothing to undo");
    }

    [Test]
    public async Task EOpensTheEditorAtTheSelectedSetting()
    {
        // Arrange
        var host = new MemoryHost("font = \"x\"\nfont_size = 12\n");
        var screen = At(host, "font_size");

        // Act
        screen.OnKey(KeyEvent.OfChar('e'));

        // Assert
        await Assert.That(host.EditedAtLine).IsEqualTo(2);
        await Assert.That(screen.Status).IsEqualTo("Back from the editor");
    }

    [Test]
    public async Task FocusReloadsAFileEditedByHand()
    {
        // Arrange
        var host = new MemoryHost("font_size = 12\n");
        var screen = At(host, "font_size");
        host.Write("font_size = 20\n");

        // Act
        screen.OnFocus(true);
        screen.OnKey(new KeyEvent(TuiKey.Right));

        // Assert: stepped from the new value.
        await Assert.That(host.Text).IsEqualTo("font_size = 20.5\n");
    }

    // ---- The mouse and the theme -------------------------------------------------------------------

    private static (SettingsScreen Screen, Frame Frame) Rendered(MemoryHost host)
    {
        var screen = new SettingsScreen(host);
        var frame = new Frame(120, 400); // tall: every setting is on screen
        screen.Render(frame);
        return (screen, frame);
    }

    [Test]
    public async Task AClickOnAValueRegionActivatesThatSetting()
    {
        // Arrange: a frame with only the value's region, as the screen gets it from a click there.
        var host = new MemoryHost("");
        var screen = new SettingsScreen(host);
        int entry = screen.Visible.ToList().FindIndex(s => s.Key == "cursor_animation");
        var frame = new Frame(120, 40);
        frame.AddHit(80, 5, 20, 1, SettingsView.ActionValue, entry);

        // Act
        screen.OnMouse(new MouseEvent(MouseKind.Press, 85, 5), frame);

        // Assert
        await Assert.That(screen.Selected.Key).IsEqualTo("cursor_animation");
        await Assert.That(host.Text).IsEqualTo("cursor_animation = false\n");
    }

    [Test]
    public async Task ClickingARowSelectsIt()
    {
        // Arrange
        var host = new MemoryHost("");
        var (screen, frame) = Rendered(host);
        var hit = frame.Hits.First(h => h.Action == SettingsView.ActionSelect && h.Arg > 0);

        // Act
        screen.OnMouse(new MouseEvent(MouseKind.Press, hit.X, hit.Y), frame);

        // Assert
        await Assert.That(screen.Selected).IsEqualTo(screen.Visible[hit.Arg]);
        await Assert.That(host.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task ClickingAGroupGoesToItsFirstSetting()
    {
        // Arrange
        var (screen, frame) = Rendered(new MemoryHost(""));
        var hit = frame.Hits.First(h => h.Action == SettingsView.ActionGroup && h.Arg > 0);

        // Act
        screen.OnMouse(new MouseEvent(MouseKind.Press, hit.X, hit.Y), frame);

        // Assert
        await Assert.That(screen.Selected.Group).IsEqualTo(SettingsCatalog.Groups[hit.Arg]);
    }

    [Test]
    public async Task TheWheelAndOtherButtonsSelectNothing()
    {
        // Arrange
        var (screen, frame) = Rendered(new MemoryHost(""));
        var first = screen.Selected;

        // Act
        screen.OnMouse(new MouseEvent(MouseKind.WheelDown, 1, 1), frame);
        screen.OnMouse(new MouseEvent(MouseKind.WheelUp, 1, 1), frame);
        screen.OnMouse(new MouseEvent(MouseKind.Press, frame.Hits[^1].X, frame.Hits[^1].Y, Button: 2), frame);

        // Assert
        await Assert.That(screen.Selected).IsEqualTo(first);
    }

    [Test]
    public async Task OnADarkTerminalThePageIsOneDarkerShade()
    {
        // Act
        var theme = SettingsScreen.PageTheme(0x202020);

        // Assert
        await Assert.That(theme.Base).IsEqualTo(0x131313u);
        await Assert.That(theme.Crust).IsEqualTo(theme.Base);
    }

    [Test]
    public async Task OnABlackTerminalThePageIsStillNotSeeThrough()
    {
        // Act
        var theme = SettingsScreen.PageTheme(0x000000);

        // Assert
        await Assert.That(theme.Base).IsEqualTo(0x0B0B0Eu);
    }

    [Test]
    public async Task OnALightTerminalTheLightThemeIsKept()
    {
        // Act
        var theme = SettingsScreen.PageTheme(0xFFFFFF);

        // Assert
        await Assert.That(theme).IsEqualTo(TuiTheme.Light);
    }

    [Test]
    public async Task TheCardColorFollowsTheTerminal()
    {
        // Arrange
        uint? card = null;
        var screen = new SettingsScreen(new MemoryHost("")) { CardColor = c => card = c };

        // Act
        screen.OnThemeColor(0x202020);

        // Assert
        await Assert.That(card).IsEqualTo(0x131313u);
    }
}
