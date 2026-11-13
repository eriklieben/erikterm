using System.Collections.Concurrent;
using System.Text;
using ErikTerm.App;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Session;
using ErikTerm.App.Ui;
using ErikTerm.App.WindowFeatures;
using ErikTerm.App.Worktrees;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>Snippets from the palette and SendSnippet: what is typed into the focused pane.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class WindowSnippetsTests
{
    private static (FakeFeatureWindow Window, WindowSnippets Snippets) New(params Snippet[] snippets)
    {
        var window = new FakeFeatureWindow(new Settings { Snippets = snippets });
        var gate = new Lock();
        var recent = new WindowRecent(window, window, window, gate);
        return (window, new WindowSnippets(window, window, recent, gate));
    }

    [Test]
    public async Task EachSnippetIsAPaletteEntryWithItsDescriptionOrFirstLine()
    {
        // Arrange
        var (window, snippets) = New(new Snippet("deploy", "make deploy\nmake verify"), new Snippet("hi", "echo hi", "Greets"));
        using var _ = window;
        var list = new List<Command>();

        // Act
        snippets.AddCommands(list);

        // Assert
        await Assert.That(list.Select(c => c.Id)).IsEquivalentTo(["snippet:deploy", "snippet:hi"], CollectionOrdering.Matching);
        await Assert.That(list[0].Detail).IsEqualTo("make deploy …");
        await Assert.That(list[1].Detail).IsEqualTo("Greets");
    }

    [Test]
    public async Task RunningAnEntryPastesItIntoTheFocusedPane()
    {
        // Arrange
        var (window, snippets) = New(new Snippet("hi", "echo hi"));
        using var _ = window;
        var list = new List<Command>();
        snippets.AddCommands(list);

        // Act
        list[0].RunWith!(RunModifiers.None);

        // Assert
        await Assert.That(window.Pastes.Single().Text).IsEqualTo("echo hi");
        await Assert.That(window.Pastes.Single().Pane).IsSameReferenceAs(window.Focused);
        await Assert.That(window.Pastes.Single().Broadcast).IsTrue();
        await Assert.That(window.Inputs).IsEmpty();
    }

    [Test]
    public async Task ShiftEnterAlsoPressesEnter()
    {
        // Arrange
        var (window, snippets) = New(new Snippet("hi", "echo hi"));
        using var _ = window;
        var list = new List<Command>();
        snippets.AddCommands(list);

        // Act
        list[0].RunWith!(RunModifiers.Shift);

        // Assert
        await Assert.That(window.Inputs.Single().Text).IsEqualTo("\r");
        await Assert.That(window.Broadcasts).IsEquivalentTo(["\r"]);
    }

    [Test]
    public async Task SendSnippetFindsItByNameIgnoringCaseAndSpaces()
    {
        // Arrange
        var (window, snippets) = New(new Snippet("Deploy", "make deploy"));
        using var _ = window;

        // Act
        snippets.SendNamed("  deploy ");

        // Assert
        await Assert.That(window.Pastes.Single().Text).IsEqualTo("make deploy");
    }

    [Test]
    public async Task AnUnknownSnippetIsAnError()
    {
        // Arrange
        var (window, snippets) = New(new Snippet("deploy", "make deploy"));
        using var _ = window;

        // Act
        snippets.SendNamed("nope");

        // Assert
        await Assert.That(window.Toasts.Single()).IsEqualTo((ToastKind.Error, "No such snippet", (string?)"nope"));
        await Assert.That(window.Pastes).IsEmpty();
    }

    [Test]
    public async Task TheSelectionIsFilledIn()
    {
        // Arrange
        var (window, snippets) = New(new Snippet("grep", "grep {{selection}}"));
        using var _ = window;
        var pane = window.Focused;
        pane.Term.Feed("needle\r\n"u8);
        pane.Selection = new Selection { AnchorLine = pane.Term.ScreenTopLine, AnchorCol = 0, HeadLine = pane.Term.ScreenTopLine, HeadCol = 6 };

        // Act
        snippets.SendNamed("grep");

        // Assert
        await Assert.That(window.Pastes.Single().Text).IsEqualTo("grep needle");
    }
}

/// <summary>Recent commands and directories: the palette's entries, running again, going to a directory, and collecting them.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class WindowRecentTests
{
    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    private static (FakeFeatureWindow Window, WindowRecent Recent) New(RecentHistory? history, Settings? settings = null)
    {
        var window = new FakeFeatureWindow(settings) { Recent = history };
        return (window, new WindowRecent(window, window, window, new Lock()));
    }

    private static RecentHistory History(string[] commands, string[] directories)
    {
        var h = new RecentHistory();
        foreach (string c in commands) h.AddCommand(c, null, 1, isPrivate: false, 1);
        foreach (string d in directories) h.AddDirectory(d, 1, 1);
        return h;
    }

    [Test]
    public async Task WithoutAHistoryThereAreNoEntries()
    {
        // Arrange
        var (window, recent) = New(null);
        using var _ = window;
        var list = new List<Command>();

        // Act
        recent.AddCommands(list);

        // Assert
        await Assert.That(list).IsEmpty();
    }

    [Test]
    public async Task AnEmptyHistoryHasNoEntries()
    {
        // Arrange
        var (window, recent) = New(new RecentHistory());
        using var _ = window;
        var list = new List<Command>();

        // Act
        recent.AddCommands(list);

        // Assert
        await Assert.That(list).IsEmpty();
    }

    [Test]
    public async Task TheHistoryGivesPickersInlineEntriesAndClear()
    {
        // Arrange
        string dir = Directory.GetCurrentDirectory();
        var (window, recent) = New(History(["make", "ls"], [dir]));
        using var _ = window;
        var list = new List<Command>();

        // Act
        recent.AddCommands(list);

        // Assert: most recent first.
        await Assert.That(list.Select(c => c.Id)).IsEquivalentTo(
            ["recent-commands", "recent-directories", "recent-command:ls", "recent-command:make", "recent-directory:" + dir, "recent-clear"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheCommandsPickerListsEveryCommand()
    {
        // Arrange
        var (window, recent) = New(History(["make", "ls"], []));
        using var _ = window;
        var list = new List<Command>();
        recent.AddCommands(list);

        // Act
        list.Single(c => c.Id == "recent-commands").Run!();

        // Assert
        await Assert.That(window.Palette!.Value.Commands.Select(c => c.Title)).IsEquivalentTo(["ls", "make"], CollectionOrdering.Matching);
        await Assert.That(window.Palette!.Value.Options.Noun).IsEqualTo("commands");
    }

    [Test]
    public async Task TheDirectoriesPickerListsEveryDirectory()
    {
        // Arrange
        string dir = Directory.GetCurrentDirectory();
        var (window, recent) = New(History([], [dir]));
        using var _ = window;
        var list = new List<Command>();
        recent.AddCommands(list);

        // Act
        list.Single(c => c.Id == "recent-directories").Run!();

        // Assert
        await Assert.That(window.Palette!.Value.Commands.Single().Id).IsEqualTo("recent-directory:" + dir);
        await Assert.That(window.Palette!.Value.Options.Noun).IsEqualTo("directories");
    }

    [Test]
    public async Task RunAgainTypesTheCommandWithoutEnter()
    {
        // Arrange
        var (window, recent) = New(History(["make"], []));
        using var _ = window;
        var list = new List<Command>();
        recent.AddCommands(list);

        // Act
        list.Single(c => c.Id == "recent-command:make").RunWith!(RunModifiers.None);

        // Assert
        await Assert.That(window.Pastes.Single().Text).IsEqualTo("make");
        await Assert.That(window.Inputs).IsEmpty();
    }

    [Test]
    public async Task ShiftRunsItToo()
    {
        // Arrange
        var (window, recent) = New(History(["make"], []));
        using var _ = window;
        var list = new List<Command>();
        recent.AddCommands(list);

        // Act
        list.Single(c => c.Id == "recent-command:make").RunWith!(RunModifiers.Shift);

        // Assert
        await Assert.That(window.Inputs.Single().Text).IsEqualTo("\r");
    }

    [Test]
    [Arguments(true, "Recent commands and directories cleared")]
    [Arguments(false, "Couldn't delete the saved history")]
    public async Task ClearSaysWhetherTheFileWentAway(bool deleted, string toast)
    {
        // Arrange
        var (window, recent) = New(History(["make"], []));
        using var _ = window;
        window.ForgetResult = deleted;
        var list = new List<Command>();
        recent.AddCommands(list);

        // Act
        list.Single(c => c.Id == "recent-clear").Run!();

        // Assert
        await Assert.That(window.Calls).Contains("ForgetRecent");
        await Assert.That(window.Toasts.Single().Title).IsEqualTo(toast);
    }

    [Test]
    public async Task GoingToAMissingDirectoryIsAnError()
    {
        // Arrange
        string gone = Path.Combine(Path.GetTempPath(), "erikterm-gone-" + Guid.NewGuid().ToString("N"));
        var h = new RecentHistory();
        Directory.CreateDirectory(gone);
        h.AddDirectory(gone, 1, 1);
        Directory.Delete(gone);
        var (window, recent) = New(h);
        using var _ = window;
        var list = new List<Command>();
        recent.AddCommands(list);

        // Act
        list.Single(c => c.Id == "recent-directory:" + gone).RunWith!(RunModifiers.None);

        // Assert
        await Assert.That(window.Toasts.Single().Title).IsEqualTo("That directory is gone");
        await Assert.That(window.TabsSpawnedIn).IsEmpty();
    }

    [Test]
    public async Task AltEnterOpensANewTabThere()
    {
        // Arrange
        string dir = Directory.GetCurrentDirectory();
        var (window, recent) = New(History([], [dir]));
        using var _ = window;
        var list = new List<Command>();
        recent.AddCommands(list);

        // Act
        list.Single(c => c.Id == "recent-directory:" + dir).RunWith!(RunModifiers.Alt);

        // Assert
        await Assert.That(window.TabsSpawnedIn).IsEquivalentTo([dir]);
        await Assert.That(window.Pastes).IsEmpty();
    }

    [Test]
    public async Task WhenAProgramRunsTheDirectoryOpensInANewTab()
    {
        // Arrange: the pane runs sleep, not a shell at its prompt.
        string dir = Directory.GetCurrentDirectory();
        var (window, recent) = New(History([], [dir]));
        using var _ = window;
        await Eventually.True(() => window.Focused.Pty.ForegroundProcessName == "sleep");
        var list = new List<Command>();
        recent.AddCommands(list);

        // Act
        list.Single(c => c.Id == "recent-directory:" + dir).RunWith!(RunModifiers.None);

        // Assert
        await Assert.That(window.TabsSpawnedIn).IsEquivalentTo([dir]);
    }

    [Test]
    public async Task WhileACommandRunsTheDirectoryOpensInANewTab()
    {
        // Arrange: shell integration says a command is running.
        string dir = Directory.GetCurrentDirectory();
        var (window, recent) = New(History([], [dir]));
        using var _ = window;
        Feed(window.Focused.Term, "\e]133;A\e\\$ \e]133;B\e\\make\r\n\e]133;C\e\\");
        var list = new List<Command>();
        recent.AddCommands(list);

        // Act
        list.Single(c => c.Id == "recent-directory:" + dir).RunWith!(RunModifiers.None);

        // Assert
        await Assert.That(window.TabsSpawnedIn).IsEquivalentTo([dir]);
    }

    [Test]
    public async Task PressEnterGoesToTheFocusedPaneAndTheBroadcastGroup()
    {
        // Arrange
        var (window, recent) = New(null);
        using var _ = window;

        // Act
        recent.PressEnter();

        // Assert
        await Assert.That(window.Inputs.Single().Pane).IsSameReferenceAs(window.Focused);
        await Assert.That(window.Broadcasts).IsEquivalentTo(["\r"]);
    }

    [Test]
    public async Task FinishedCommandsGoIntoTheHistory()
    {
        // Arrange
        var history = new RecentHistory();
        var (window, recent) = New(history);
        using var _ = window;
        Feed(window.Focused.Term, "\e]133;A\e\\$ \e]133;B\e\\make all\r\n\e]133;C\e\\done\r\n\e]133;D;0\e\\");

        // Act
        recent.CollectFinishedCommands([], 1000);

        // Assert
        await Assert.That(history.Commands.Select(c => c.Text)).IsEquivalentTo(["make all"]);
    }

    [Test]
    public async Task ACommandIsCollectedOnce()
    {
        // Arrange
        var history = new RecentHistory();
        var (window, recent) = New(history);
        using var _ = window;
        Feed(window.Focused.Term, "\e]133;A\e\\$ \e]133;B\e\\make\r\n\e]133;C\e\\\e]133;D;0\e\\");
        recent.CollectFinishedCommands([], 1000);
        history.Clear(Path.Combine(Path.GetTempPath(), "erikterm-none-" + Guid.NewGuid().ToString("N")));

        // Act
        recent.CollectFinishedCommands([], 2000);

        // Assert
        await Assert.That(history.Commands).IsEmpty();
    }

    [Test]
    public async Task ALongCommandIsNoticed()
    {
        // Arrange: notify_command_duration is 10 s; this one ran 12.
        var (window, recent) = New(null);
        using var _ = window;
        long now = 1000;
        var term = window.Focused.Term;
        term.CommandClock = () => now;
        Feed(term, "\e]133;A\e\\$ \e]133;B\e\\make\r\n\e]133;C\e\\");
        now += 12_000;
        Feed(term, "\e]133;D;0\e\\");
        var notices = new List<FinishedCommandNotice>();

        // Act
        recent.CollectFinishedCommands(notices, 1000);

        // Assert
        await Assert.That(notices.Single().PaneId).IsEqualTo(window.Focused.Id);
        await Assert.That(notices.Single().Text).IsEqualTo("make");
    }

    [Test]
    public async Task AShortCommandIsNotNoticed()
    {
        // Arrange
        var (window, recent) = New(null);
        using var _ = window;
        Feed(window.Focused.Term, "\e]133;A\e\\$ \e]133;B\e\\ls\r\n\e]133;C\e\\\e]133;D;0\e\\");
        var notices = new List<FinishedCommandNotice>();

        // Act
        recent.CollectFinishedCommands(notices, 1000);

        // Assert
        await Assert.That(notices).IsEmpty();
    }

    [Test]
    public async Task ReportedDirectoriesGoIntoTheHistory()
    {
        // Arrange
        var history = new RecentHistory();
        var (window, recent) = New(history);
        using var _ = window;
        string dir = Directory.GetCurrentDirectory();
        Feed(window.Focused.Term, $"\e]7;file://{Environment.MachineName}{dir}\e\\");

        // Act
        recent.CollectFinishedCommands([], 1000);

        // Assert
        await Assert.That(history.Directories.Select(d => d.Text)).IsEquivalentTo([dir]);
    }

    [Test]
    public async Task ShortPathUsesATildeForHome()
    {
        // Arrange
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Act
        string shown = WindowRecent.ShortPath(Path.Combine(home, "src"));

        // Assert
        await Assert.That(shown).IsEqualTo("~" + Path.DirectorySeparatorChar + "src");
    }
}

/// <summary>Tool tabs, launch tabs and splits: what is spawned, where it goes, and what happens when it can't start.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class WindowLaunchesTests
{
    private static (FakeFeatureWindow Window, WindowLaunches Launches) New()
    {
        var window = new FakeFeatureWindow();
        // WindowIpc is only needed by OpenLaunchSplitNextTo, which these tests don't reach.
        return (window, new WindowLaunches(window, window, null!));
    }

    [Test]
    public async Task SplitRunStartsTheCommandInItsDirectory()
    {
        // Arrange
        var (window, launches) = New();
        using var _ = window;
        string dir = Directory.GetCurrentDirectory();

        // Act
        launches.SplitRun(dir, ["git", "show"]);

        // Assert: env -C on Linux, so no shell is involved.
        var command = window.Splits.Single().Command!;
        string[] expected = OperatingSystem.IsLinux() ? ["env", "-C", dir, "git", "show"] : ["git", "show"];
        await Assert.That(command).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task SplitRunWithoutADirectoryRunsItAsIs()
    {
        // Arrange
        var (window, launches) = New();
        using var _ = window;

        // Act
        launches.SplitRun(null, ["htop"]);

        // Assert
        await Assert.That(window.Splits.Single().Command!).IsEquivalentTo(["htop"]);
    }

    [Test]
    public async Task AToolTabClosesWithItsToolAndIsSwitchedTo()
    {
        // Arrange
        var (window, launches) = New();
        using var _ = window;
        var from = window.ActiveTab;

        // Act
        var tab = launches.SpawnToolTab(["erikterm", "review"], "/src", "Couldn't open the review");

        // Assert
        await Assert.That(tab).IsNotNull();
        await Assert.That(window.Tabs.Count).IsEqualTo(2);
        await Assert.That(tab!.Focused.CloseOnExit).IsTrue();
        await Assert.That(window.Switches.Single()).IsEqualTo((from, tab, 1));
        await Assert.That(window.Spawned.Single().Cwd).IsEqualTo("/src");
    }

    [Test]
    public async Task AToolThatCannotStartLeavesNoTab()
    {
        // Arrange
        var (window, launches) = New();
        using var _ = window;
        var from = window.ActiveTab;
        window.SpawnFails = new InvalidOperationException("no such program");

        // Act
        var tab = launches.SpawnToolTab(["nope"], "/src", "Couldn't open the review");

        // Assert
        await Assert.That(tab).IsNull();
        await Assert.That(window.Tabs.Count).IsEqualTo(1);
        await Assert.That(window.Tabs.Active).IsSameReferenceAs(from);
        await Assert.That(window.Toasts.Single()).IsEqualTo((ToastKind.Error, "Couldn't open the review", (string?)"no such program"));
    }

    private static WorktreeTabLaunch Launch(bool background, string[]? argv = null) =>
        new("feature-x", argv ?? ["claude"], "/w/feature-x", null, background);

    [Test]
    public async Task ALaunchTabIsNamedColoredAndSwitchedTo()
    {
        // Arrange
        var (window, launches) = New();
        using var _ = window;
        Pane? prepared = null;

        // Act
        string? error = launches.OpenLaunchTab(Launch(background: false), p => prepared = p);

        // Assert
        await Assert.That(error).IsNull();
        var tab = window.Tabs.Active!;
        await Assert.That(tab.CustomTitle).IsEqualTo("feature-x");
        await Assert.That(tab.Color).IsNotNull();
        await Assert.That(prepared).IsSameReferenceAs(tab.Focused);
        await Assert.That(window.Switches.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ABackgroundLaunchKeepsTheTabYouWereIn()
    {
        // Arrange
        var (window, launches) = New();
        using var _ = window;
        var from = window.ActiveTab;

        // Act
        launches.OpenLaunchTab(Launch(background: true), _ => { });

        // Assert: Ctrl+Tab still goes back to the tab used before, not the new one.
        await Assert.That(window.Tabs.Active).IsSameReferenceAs(from);
        await Assert.That(window.Tabs.Count).IsEqualTo(2);
        await Assert.That(window.Tabs.Recent[^1]).IsNotSameReferenceAs(from);
        await Assert.That(window.Switches).IsEmpty();
        await Assert.That(window.Calls).Contains("ApplyLayout(False)");
    }

    [Test]
    public async Task TheDefaultShellIsSpawnedAsTheShell()
    {
        // Arrange
        var (window, launches) = New();
        using var _ = window;

        // Act
        launches.OpenLaunchTab(Launch(background: false, argv: ["/bin/sh"]), _ => { });

        // Assert: null means the default shell, which gets shell integration.
        await Assert.That(window.Spawned.Single().Command).IsNull();
    }

    [Test]
    public async Task ALaunchThatCannotStartSaysWhy()
    {
        // Arrange
        var (window, launches) = New();
        using var _ = window;
        window.SpawnFails = new IOException("disk full");

        // Act
        string? error = launches.OpenLaunchTab(Launch(background: false), _ => { });

        // Assert
        await Assert.That(error).IsEqualTo("disk full");
        await Assert.That(window.Tabs.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ALaunchSplitGoesToTheRight()
    {
        // Arrange
        var (window, launches) = New();
        using var _ = window;

        // Act
        var pane = launches.OpenLaunchSplit(["npm", "test"], "/w", focus: false, null);

        // Assert
        await Assert.That(pane).IsNotNull();
        await Assert.That(window.Splits.Single()).IsEqualTo((SplitAxis.Horizontal, (IReadOnlyList<string>?)window.Splits[0].Command, (string?)"/w", false));
        await Assert.That(window.Splits[0].Command!).IsEquivalentTo(["npm", "test"]);
    }

    [Test]
    public async Task AShellTabIsNamedAfterItsBranch()
    {
        // Arrange
        var (window, launches) = New();
        using var _ = window;

        // Act
        launches.OpenShellTab("/w/feature-x", "feature-x");

        // Assert: the fake's SpawnTabIn opens no tab, so the active one gets the name.
        await Assert.That(window.TabsSpawnedIn).IsEquivalentTo(["/w/feature-x"]);
        await Assert.That(window.ActiveTab.CustomTitle).IsEqualTo("feature-x");
    }
}

/// <summary>Prompt marks: jumping between prompts, and what the keys do without shell integration.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class WindowPromptsTests
{
    private const string A = "\e]133;A\e\\", B = "\e]133;B\e\\", C = "\e]133;C\e\\";
    private static string D(int code) => $"\e]133;D;{code}\e\\";

    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    private static (FakeFeatureWindow Window, WindowPrompts Prompts) New()
    {
        var window = new FakeFeatureWindow();
        return (window, new WindowPrompts(window, window, new Lock()));
    }

    [Test]
    public async Task WithoutMarksTheKeyGoesToTheProgram()
    {
        // Arrange
        var (window, prompts) = New();
        using var _ = window;

        // Act
        prompts.ScrollToPrompt(-1, 'k', 3, 0);

        // Assert
        await Assert.That(window.PassedThrough.Single()).IsEqualTo(((uint)'k', (ushort)3));
        await Assert.That(window.Toasts).IsEmpty();
    }

    [Test]
    public async Task WithoutMarksThePaletteCommandExplains()
    {
        // Arrange
        var (window, prompts) = New();
        using var _ = window;

        // Act
        prompts.ScrollToPrompt(-1, 0, 0, 0);

        // Assert
        await Assert.That(window.Toasts.Single().Title).IsEqualTo("No prompts marked yet");
    }

    [Test]
    public async Task ScrollingUpGoesToTheEarlierPromptAndFlashesIt()
    {
        // Arrange: two commands with long output, the view at the bottom of a six-row screen.
        var (window, prompts) = New();
        using var _ = window;
        var pane = window.Focused;
        string output = string.Concat(Enumerable.Range(0, 10).Select(i => $"out {i}\r\n"));
        Feed(pane.Term, $"{A}$ {B}one\r\n{C}{output}{D(0)}{A}$ {B}two\r\n{C}{output}{D(0)}{A}$ {B}");
        long top = pane.Term.ViewTopLine;

        // Act
        prompts.ScrollToPrompt(-1, 0, 0, 500);

        // Assert
        await Assert.That(pane.Term.ViewTopLine).IsLessThan(top);
        await Assert.That(pane.Links.FlashLine).IsEqualTo(pane.Term.ViewTopLine);
        await Assert.That(pane.ScrollActivityAt).IsEqualTo(500);
    }

    [Test]
    public async Task ScrollingDownPastTheLastPromptReturnsToTheLiveView()
    {
        // Arrange
        var (window, prompts) = New();
        using var _ = window;
        var pane = window.Focused;
        string output = string.Concat(Enumerable.Range(0, 10).Select(i => $"out {i}\r\n"));
        Feed(pane.Term, $"{A}$ {B}one\r\n{C}{output}{D(0)}{A}$ {B}");
        prompts.ScrollToPrompt(-1, 0, 0, 0);

        // Act
        prompts.ScrollToPrompt(1, 0, 0, 0);
        prompts.ScrollToPrompt(1, 0, 0, 0);

        // Assert
        await Assert.That(pane.Term.ViewOffset).IsEqualTo(0);
    }

    [Test]
    public async Task WhileACommandRunsItsKeysAreItsOwn()
    {
        // Arrange
        var (window, prompts) = New();
        using var _ = window;
        Feed(window.Focused.Term, $"{A}$ {B}fzf\r\n{C}");

        // Act
        prompts.ScrollToPrompt(-1, 'k', 0, 0);

        // Assert
        await Assert.That(window.PassedThrough.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SelectingWithoutMarksExplains()
    {
        // Arrange
        var (window, prompts) = New();
        using var _ = window;

        // Act
        prompts.SelectLastCommand(copy: false);

        // Assert
        await Assert.That(window.Toasts.Single().Title).IsEqualTo("No prompts marked yet");
        await Assert.That(window.Focused.Selection).IsNull();
    }

    [Test]
    public async Task TheBlockOfALineIsTheCommandItBelongsTo()
    {
        // Arrange
        var t = new Terminal(40, 10);
        Feed(t, $"{A}$ {B}make\r\n{C}built\r\n{D(2)}{A}$ {B}");

        // Act
        var block = WindowPrompts.BlockContaining(t, t.ScreenTopLine + 1);

        // Assert
        await Assert.That(block).IsNotNull();
        await Assert.That(WindowPrompts.DescribeBlock(block!.Value)).IsEqualTo("make · 1 line · exit 2");
    }

    [Test]
    public async Task ABlockWithoutACommandIsCalledCommand()
    {
        // Arrange
        var t = new Terminal(40, 10);
        Feed(t, $"{A}$ {B}\r\n{C}a\r\nb\r\n{D(0)}{A}$ {B}");

        // Act
        var block = WindowPrompts.BlockContaining(t, t.ScreenTopLine + 1);

        // Assert
        await Assert.That(WindowPrompts.DescribeBlock(block!.Value)).IsEqualTo("Command · 2 lines");
    }

    [Test]
    public async Task ThePaletteGetsFiveCommands()
    {
        // Arrange
        var added = new List<string>();

        // Act
        WindowPrompts.AddCommands((_, title, _, action, _) => added.Add($"{title}={action}"));

        // Assert
        await Assert.That(added).Contains("Scroll to Previous Prompt=ScrollToPrompt(-1)");
        await Assert.That(added.Count).IsEqualTo(5);
    }
}

/// <summary>OSC 52 clipboard writes: the policy on each terminal, asking in the info bar, answering and expiring.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class WindowOsc52Tests
{
    private static (FakeFeatureWindow Window, WindowOsc52 Osc52, InfoBar Bar, ConcurrentQueue<(Terminal, ClipboardTarget, string)> Queue) New(Osc52Policy policy)
    {
        var window = new FakeFeatureWindow(new Settings { Osc52 = policy });
        var bar = new InfoBar();
        var queue = new ConcurrentQueue<(Terminal, ClipboardTarget, string)>();
        return (window, new WindowOsc52(window, bar, queue), bar, queue);
    }

    [Test]
    [Arguments(Osc52Policy.Allow, true)]
    [Arguments(Osc52Policy.Ask, true)]
    [Arguments(Osc52Policy.Deny, false)]
    public async Task AttachSetsWhetherTheTerminalMayWrite(Osc52Policy policy, bool allowed)
    {
        // Arrange
        var (window, osc52, _, _) = New(policy);
        using var _w = window;
        var term = new Terminal(10, 2);

        // Act
        osc52.Attach(term);

        // Assert
        await Assert.That(term.AllowClipboardWrite).IsEqualTo(allowed);
    }

    [Test]
    public async Task ARequestFromTheTerminalIsQueuedAndWakesTheLoop()
    {
        // Arrange
        var (window, osc52, _, queue) = New(Osc52Policy.Ask);
        using var _w = window;
        var term = new Terminal(10, 2);
        osc52.Attach(term);

        // Act: OSC 52 with "hi" in base64.
        term.Feed("\e]52;c;aGk=\a"u8);

        // Assert
        await Assert.That(queue.Count).IsEqualTo(1);
        await Assert.That(window.Wakes).IsEqualTo(1);
    }

    [Test]
    public async Task AskingShowsTheQuestionInTheInfoBar()
    {
        // Arrange
        var (window, osc52, bar, _) = New(Osc52Policy.Ask);
        using var _w = window;

        // Act
        osc52.Apply(window.Focused.Term, ClipboardTarget.Clipboard, "secret");

        // Assert
        await Assert.That(bar.Contains($"osc52:{window.Focused.Id}")).IsTrue();
        await Assert.That(bar.Current!.Buttons.Length).IsEqualTo(3);
        await Assert.That(window.Redraws).IsEqualTo(1);
    }

    [Test]
    public async Task DenyingRemovesTheQuestionWithoutWriting()
    {
        // Arrange
        var (window, osc52, bar, _) = New(Osc52Policy.Ask);
        using var _w = window;
        osc52.Apply(window.Focused.Term, ClipboardTarget.Clipboard, "secret");

        // Act: the ✕.
        bar.Current!.Dismissed!();

        // Assert
        await Assert.That(bar.Contains($"osc52:{window.Focused.Id}")).IsFalse();
        await Assert.That(window.Toasts).IsEmpty();
    }

    [Test]
    public async Task DenyAppliesNothing()
    {
        // Arrange
        var (window, osc52, bar, _) = New(Osc52Policy.Deny);
        using var _w = window;

        // Act
        osc52.Apply(window.Focused.Term, ClipboardTarget.Clipboard, "secret");

        // Assert
        await Assert.That(bar.IsShown).IsFalse();
        await Assert.That(window.Toasts).IsEmpty();
    }

    [Test]
    public async Task TheSameWriteTwiceAsksOnce()
    {
        // Arrange: programs that think they're in tmux send it raw and in passthrough.
        var (window, osc52, bar, _) = New(Osc52Policy.Ask);
        using var _w = window;
        osc52.Apply(window.Focused.Term, ClipboardTarget.Clipboard, "secret");
        bar.Current!.Dismissed!();

        // Act
        osc52.Apply(window.Focused.Term, ClipboardTarget.Clipboard, "secret");

        // Assert
        await Assert.That(bar.IsShown).IsFalse();
    }

    [Test]
    public async Task TheQuestionWakesTheLoopForItsExpiry()
    {
        // Arrange
        var (window, osc52, _, _) = New(Osc52Policy.Ask);
        using var _w = window;
        int idle = osc52.WakeMs(Environment.TickCount64);
        osc52.Apply(window.Focused.Term, ClipboardTarget.Clipboard, "secret");

        // Act
        int asking = osc52.WakeMs(Environment.TickCount64);

        // Assert
        await Assert.That(idle).IsEqualTo(int.MaxValue);
        await Assert.That(asking).IsGreaterThan(0).And.IsLessThanOrEqualTo(10_000);
    }

    [Test]
    public async Task TheQuestionGoesAwayWhenItExpires()
    {
        // Arrange
        var (window, osc52, bar, _) = New(Osc52Policy.Ask);
        using var _w = window;
        osc52.Apply(window.Focused.Term, ClipboardTarget.Clipboard, "secret");

        // Act
        osc52.Tick(Environment.TickCount64 + 60_000, Osc52Policy.Ask, window.Layout.LeafList);

        // Assert
        await Assert.That(bar.Contains($"osc52:{window.Focused.Id}")).IsFalse();
        await Assert.That(osc52.WakeMs(Environment.TickCount64)).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task TurningAskOffCancelsTheQuestion()
    {
        // Arrange
        var (window, osc52, bar, _) = New(Osc52Policy.Ask);
        using var _w = window;
        osc52.Apply(window.Focused.Term, ClipboardTarget.Clipboard, "secret");

        // Act
        osc52.Tick(Environment.TickCount64, Osc52Policy.Deny, window.Layout.LeafList);

        // Assert
        await Assert.That(bar.Contains($"osc52:{window.Focused.Id}")).IsFalse();
        await Assert.That(window.Focused.Term.AllowClipboardWrite).IsFalse();
    }

    [Test]
    public async Task ATickKeepsAFreshQuestion()
    {
        // Arrange
        var (window, osc52, bar, _) = New(Osc52Policy.Ask);
        using var _w = window;
        osc52.Apply(window.Focused.Term, ClipboardTarget.Clipboard, "secret");

        // Act
        osc52.Tick(Environment.TickCount64, Osc52Policy.Ask, window.Layout.LeafList);

        // Assert
        await Assert.That(bar.Contains($"osc52:{window.Focused.Id}")).IsTrue();
        await Assert.That(window.Focused.Term.AllowClipboardWrite).IsTrue();
    }

    [Test]
    public async Task ATerminalOfNoPaneHereIsNeverAskedAbout()
    {
        // Arrange
        var (window, osc52, bar, _) = New(Osc52Policy.Ask);
        using var _w = window;

        // Act
        osc52.Apply(new Terminal(10, 2), ClipboardTarget.Clipboard, "secret");

        // Assert
        await Assert.That(bar.IsShown).IsFalse();
    }
}

/// <summary>File links (path:line printed by a compiler): which directory a relative path is resolved against.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class WindowEditorTests
{
    private static WindowEditor New(FakeFeatureWindow window)
    {
        var jobs = new ErikTerm.App.Integrations.BackgroundJobs(() => { });
        var tracks = new PaneTracks();
        var gate = new Lock();
        var launches = new WindowLaunches(window, window, null!);
        return new WindowEditor(window, jobs, tracks, new WindowGit(window, jobs, tracks, launches), new WindowGitHub(window, jobs, tracks, launches, gate), launches, gate);
    }

    /// <summary>A new directory with src/parser.c in it.</summary>
    private static string ProjectDirectory()
    {
        string dir = Directory.CreateTempSubdirectory("erikterm-links-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "parser.c"), "int main(void) { return 0; }\n");
        return dir;
    }

    [Test]
    public async Task ARelativePathResolvesInTheShellsDirectoryWithoutOsc7()
    {
        // Arrange: no OSC 7 and no git status (so no tracked directory): only the shell's own directory says where it is.
        string dir = ProjectDirectory();
        using var window = new FakeFeatureWindow(new Settings { GitStatus = false });
        using var pane = TestPanes.Spawn(workingDirectory: dir);
        var editor = New(window);
        const string text = "src/parser.c:3:5: error: expected one";
        // The directory is read from the running program: wait until the pane's own program runs (on macOS the spawn
        // helper is in the foreground for a moment, still in the test's directory).
        for (int i = 0; i < 100 && pane.Pty.ForegroundProcessName != "sleep"; i++) await Task.Delay(50);

        try
        {
            // Act
            var span = editor.FindSmartLinkAt(pane, text, 2, out var link);

            // Assert: the file in the pane's folder (compared as a file: on macOS /var is a link to /private/var).
            await Assert.That(span).IsNotNull();
            await Assert.That(link.Value).EndsWith(Path.Combine("src", "parser.c"));
            await Assert.That(File.Exists(link.Value)).IsTrue();
            await Assert.That(SameFile(link.Value, Path.Combine(dir, "src", "parser.c"))).IsTrue();
            await Assert.That(link.Line).IsEqualTo(3);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Whether two paths name the same file, whatever links lie on the way to it.</summary>
    private static bool SameFile(string a, string b) =>
        string.Equals(RealPath(a), RealPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static string RealPath(string path)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var parts = new Stack<string>();
        for (var d = dir; d is not null; d = d.Parent)
            parts.Push(d.LinkTarget is { } target ? Path.GetFullPath(target, d.Parent?.FullName ?? "/") : d.Name);
        return Path.Combine([.. parts, Path.GetFileName(path)]);
    }

    [Test]
    public async Task ARelativePathToAFileThatIsntThereIsNoLink()
    {
        // Arrange
        string dir = ProjectDirectory();
        using var window = new FakeFeatureWindow(new Settings { GitStatus = false });
        using var pane = TestPanes.Spawn(workingDirectory: dir);
        var editor = New(window);
        const string text = "src/lexer.c:3:5: error: expected one";

        // Act
        var span = editor.FindSmartLinkAt(pane, text, 2, out _);

        // Assert
        Directory.Delete(dir, recursive: true);
        await Assert.That(span).IsNull();
    }
}
