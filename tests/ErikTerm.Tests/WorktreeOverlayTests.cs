using ErikTerm.App.Native;
using ErikTerm.App.Ui;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

/// <summary>The New Agent Worktree form's keys: editing its fields, moving between them, the agent, and the launches.</summary>
public class AgentWorktreeOverlayTests
{
    private const ushort None = 0, Ctrl = Sdl.KMOD_CTRL, Shift = Sdl.KMOD_SHIFT, Alt = Sdl.KMOD_LALT;

    private sealed class Harness
    {
        public readonly AgentWorktreeOverlay Overlay = new();
        public readonly WorktreeForm Form;
        public readonly List<(WorktreeRequest Request, bool Background, WorktreePlacement Placement)> Launches = [];

        public Harness(WorktreePlacement placement = WorktreePlacement.Tab)
        {
            Form = new WorktreeForm(new WorktreeFormOptions
            {
                Cwd = "/src/app",
                RepoName = "app",
                MainRoot = "/src/app",
                Home = "/home/u",
                DirTemplate = WorktreeLauncher.DefaultDirTemplate,
                DefaultBase = "main",
                Agents = WorktreeLauncher.BuiltIn,
                Branches = [],
                PathExists = _ => false,
                NameSeed = 7,
                Placement = placement,
            });
            Overlay.Launch = (_, request, background, where) => Launches.Add((request, background, where));
            Overlay.Open(Form, new Theme(), 0);
        }

        public void Key(uint key, ushort mod = None) => Overlay.HandleKey(key, mod, 0);
        public void Type(string text) => Overlay.HandleText(text, 0);
        public TextInput Focused => Form.Field(Form.Focus);
    }

    [Test]
    public async Task TypingGoesIntoThePromptAndNamesTheBranch()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Type("Fix login");

        // Assert
        await Assert.That(h.Form.Prompt.Text).IsEqualTo("Fix login");
        await Assert.That(h.Form.Branch.Text).IsEqualTo("fix-login");
    }

    [Test]
    public async Task EnterLaunchesInTheForegroundWhereTheSettingSays()
    {
        // Arrange
        var h = new Harness();
        h.Type("Fix login");

        // Act
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Launches.Count).IsEqualTo(1);
        await Assert.That(h.Launches[0].Request.Branch).IsEqualTo("fix-login");
        await Assert.That(h.Launches[0].Background).IsFalse();
        await Assert.That(h.Launches[0].Placement).IsEqualTo(WorktreePlacement.Tab);
    }

    [Test]
    public async Task CtrlEnterLaunchesInTheBackground()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key(Sdl.K_KP_ENTER, Ctrl);

        // Assert
        await Assert.That(h.Launches[0].Background).IsTrue();
    }

    [Test]
    public async Task AltEnterOpensItTheOtherWay()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key(Sdl.K_RETURN, Alt);

        // Assert
        await Assert.That(h.Launches[0].Placement).IsEqualTo(WorktreePlacement.Split);
    }

    [Test]
    public async Task EnterWithAProblemLaunchesNothing()
    {
        // Arrange: a branch name git refuses.
        var h = new Harness();
        h.Key(Sdl.K_DOWN);
        h.Key(Sdl.K_DOWN);
        h.Key('u', Ctrl);
        h.Type("bad..name");

        // Act
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Form.Error).IsNotNull();
        await Assert.That(h.Launches).IsEmpty();
    }

    [Test]
    public async Task TabCyclesTheAgentAndShiftTabGoesBack()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key(Sdl.K_TAB);
        string next = h.Form.Agent!.Key;
        h.Key(Sdl.K_TAB, Shift);

        // Assert
        await Assert.That(next).IsEqualTo("copilot");
        await Assert.That(h.Form.Agent!.Key).IsEqualTo("claude");
    }

    [Test]
    public async Task DownAndUpMoveBetweenTheFields()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key(Sdl.K_DOWN);
        var afterDown = h.Form.Focus;
        h.Key(Sdl.K_UP);
        h.Key(Sdl.K_UP);

        // Assert: Up from the prompt wraps to the last field.
        await Assert.That(afterDown).IsEqualTo(WorktreeField.Arguments);
        await Assert.That(h.Form.Focus).IsEqualTo(WorktreeField.Base);
    }

    [Test]
    public async Task ArrowsAndHomeEndMoveTheCaret()
    {
        // Arrange
        var h = new Harness();
        h.Type("abc");

        // Act
        h.Key(Sdl.K_LEFT);
        int afterLeft = h.Focused.Caret;
        h.Key(Sdl.K_HOME);
        int afterHome = h.Focused.Caret;
        h.Key(Sdl.K_RIGHT);
        int afterRight = h.Focused.Caret;
        h.Key(Sdl.K_END);

        // Assert
        await Assert.That(afterLeft).IsEqualTo(2);
        await Assert.That(afterHome).IsEqualTo(0);
        await Assert.That(afterRight).IsEqualTo(1);
        await Assert.That(h.Focused.Caret).IsEqualTo(3);
    }

    [Test]
    public async Task CtrlAAndCtrlEAreHomeAndEnd()
    {
        // Arrange
        var h = new Harness();
        h.Type("abc");

        // Act
        h.Key('a', Ctrl);
        int start = h.Focused.Caret;
        h.Key('e', Ctrl);

        // Assert
        await Assert.That(start).IsEqualTo(0);
        await Assert.That(h.Focused.Caret).IsEqualTo(3);
    }

    [Test]
    public async Task BackspaceAndDeleteEditTheField()
    {
        // Arrange
        var h = new Harness();
        h.Type("abcd");
        h.Key(Sdl.K_LEFT);
        h.Key(Sdl.K_LEFT);

        // Act
        h.Key(Sdl.K_BACKSPACE);
        h.Key(Sdl.K_DELETE);

        // Assert
        await Assert.That(h.Form.Prompt.Text).IsEqualTo("ad");
    }

    [Test]
    [Arguments(Sdl.K_BACKSPACE)]
    [Arguments('w')]
    public async Task CtrlBackspaceAndCtrlWDeleteAWord(uint key)
    {
        // Arrange
        var h = new Harness();
        h.Type("fix the login");

        // Act
        h.Key(key, Ctrl);

        // Assert
        await Assert.That(h.Form.Prompt.Text).IsEqualTo("fix the ");
    }

    [Test]
    public async Task CtrlUClearsTheFieldAndTheBranchFollows()
    {
        // Arrange
        var h = new Harness();
        h.Type("Fix login");

        // Act
        h.Key('u', Ctrl);

        // Assert: an empty prompt gets a generated name again.
        await Assert.That(h.Form.Prompt.Text).IsEqualTo("");
        await Assert.That(h.Form.Branch.Text).IsNotEqualTo("fix-login");
    }

    [Test]
    public async Task PlainLettersAreLeftToTheTextEvent()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key('u');

        // Assert
        await Assert.That(h.Form.Prompt.Text).IsEqualTo("");
        await Assert.That(h.Overlay.IsOpen).IsTrue();
    }

    [Test]
    public async Task EscapeClosesWithoutLaunching()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key(Sdl.K_ESCAPE);

        // Assert
        await Assert.That(h.Overlay.IsOpen).IsFalse();
        await Assert.That(h.Overlay.IsShowing(h.Form)).IsFalse();
        await Assert.That(h.Launches).IsEmpty();
    }

    [Test]
    public async Task AClosedFormIgnoresKeysAndText()
    {
        // Arrange
        var h = new Harness();
        h.Overlay.Close(0);

        // Act
        h.Type("x");
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Form.Prompt.Text).IsEqualTo("");
        await Assert.That(h.Launches).IsEmpty();
    }

    [Test]
    public async Task AClickOutsideThePanelCloses()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Overlay.HandleMouseDown(3, 3, 0);

        // Assert
        await Assert.That(h.Overlay.IsOpen).IsFalse();
    }

    [Test]
    public async Task ItShowsTheFormItWasOpenedWith()
    {
        // Arrange
        var h = new Harness();
        var other = new Harness().Form;

        // Act
        bool mine = h.Overlay.IsShowing(h.Form);
        bool theirs = h.Overlay.IsShowing(other);

        // Assert
        await Assert.That(mine).IsTrue();
        await Assert.That(theirs).IsFalse();
    }
}

/// <summary>The Finish Worktree dialog's keys: the options, the message, running it, and the plain confirmation.</summary>
public class FinishWorktreeOverlayTests
{
    private const ushort None = 0, Ctrl = Sdl.KMOD_CTRL;
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static FinishFacts Facts(bool agent = false) => new()
    {
        RepoName = "r", Worktree = "/w", MainRoot = "/r", Branch = "feat", Target = "main", HeadOid = Sha, TargetOid = Sha,
        MainBranch = "main", MainChanges = 0, WorktreeChanges = 0, Subjects = ["do a thing"], Commits = 1, Files = ["a.txt"],
        Prediction = new ConflictPrediction(true, true, "t", [], null), HasDiff = true, AgentRunning = agent, HasGh = true, HasOrigin = true,
    };

    private sealed class Harness
    {
        public readonly FinishWorktreeOverlay Overlay = new();
        public readonly FinishForm Form;
        public int Runs;

        public Harness(FinishOption option = FinishOption.Squash, bool agent = false)
        {
            Form = new FinishForm(Facts(agent), option, ["do a thing"]);
            Overlay.Run = _ => Runs++;
            Overlay.Open(Form, new Theme(), 0);
        }

        public void Key(uint key, ushort mod = None) => Overlay.HandleKey(key, mod, 0);
    }

    private static WorktreeConfirm Confirm(Action confirmed, bool busy = false) => new()
    {
        Title = "Remove", Detail = "2 worktrees", ConfirmLabel = "remove", Lines = ["a"], Commands = ["git worktree remove a"],
        Confirmed = confirmed, Busy = busy,
    };

    [Test]
    public async Task EnterRunsTheFinish()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Runs).IsEqualTo(1);
    }

    [Test]
    public async Task AProblemStopsTheRun()
    {
        // Arrange: an agent still runs in the worktree.
        var h = new Harness(agent: true);

        // Act
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Runs).IsEqualTo(0);
    }

    [Test]
    public async Task ABusyFormDoesNotRunTwice()
    {
        // Arrange
        var h = new Harness();
        h.Form.Busy = true;

        // Act
        h.Key(Sdl.K_KP_ENTER);

        // Assert
        await Assert.That(h.Runs).IsEqualTo(0);
    }

    [Test]
    public async Task TabFocusesTheMessageWhenTheOptionHasOne()
    {
        // Arrange
        var h = new Harness(FinishOption.Squash);

        // Act
        h.Key(Sdl.K_TAB);

        // Assert
        await Assert.That(h.Overlay.EditingMessage).IsTrue();
    }

    [Test]
    public async Task TabDoesNothingForAMerge()
    {
        // Arrange
        var h = new Harness(FinishOption.Merge);

        // Act
        h.Key(Sdl.K_TAB);

        // Assert
        await Assert.That(h.Overlay.EditingMessage).IsFalse();
    }

    [Test]
    public async Task EnterInTheMessageAddsALine()
    {
        // Arrange
        var h = new Harness();
        h.Key(Sdl.K_TAB);
        h.Form.Message.End();

        // Act
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Form.Message.Text).EndsWith("\n");
        await Assert.That(h.Runs).IsEqualTo(0);
    }

    [Test]
    public async Task CtrlEnterInTheMessageRuns()
    {
        // Arrange
        var h = new Harness();
        h.Key(Sdl.K_TAB);

        // Act
        h.Key(Sdl.K_RETURN, Ctrl);

        // Assert
        await Assert.That(h.Runs).IsEqualTo(1);
    }

    [Test]
    public async Task TextGoesIntoTheMessageOnlyWhileItIsFocused()
    {
        // Arrange
        var h = new Harness();
        string before = h.Form.Message.Text;
        h.Overlay.HandleText("ignored", 0);
        h.Key(Sdl.K_TAB);
        h.Form.Message.End();

        // Act
        h.Overlay.HandleText("!", 0);

        // Assert
        await Assert.That(h.Form.Message.Text).IsEqualTo(before + "!");
    }

    [Test]
    public async Task CtrlUClearsTheMessage()
    {
        // Arrange
        var h = new Harness();
        h.Key(Sdl.K_TAB);

        // Act
        h.Key('u', Ctrl);

        // Assert
        await Assert.That(h.Form.Message.Text).IsEqualTo("");
    }

    [Test]
    public async Task MessageKeysMoveAndEdit()
    {
        // Arrange
        var h = new Harness();
        h.Key(Sdl.K_TAB);
        h.Key('u', Ctrl);
        h.Overlay.HandleText("ab\ncd", 0);

        // Act
        h.Key(Sdl.K_UP);
        h.Key(Sdl.K_HOME);
        h.Key(Sdl.K_DELETE);
        h.Key(Sdl.K_END);
        h.Key(Sdl.K_DOWN);
        h.Key(Sdl.K_LEFT);
        h.Key(Sdl.K_BACKSPACE);
        h.Key(Sdl.K_RIGHT);

        // Assert
        await Assert.That(h.Form.Message.Text).IsEqualTo("b\nd");
        await Assert.That(h.Form.Message.Caret).IsEqualTo(3);
    }

    [Test]
    public async Task LettersInTheMessageAreLeftToTheTextEvent()
    {
        // Arrange
        var h = new Harness();
        h.Key(Sdl.K_TAB);
        string before = h.Form.Message.Text;

        // Act: '2' would pick Merge outside the message.
        h.Key('2');

        // Assert
        await Assert.That(h.Form.Option).IsEqualTo(FinishOption.Squash);
        await Assert.That(h.Form.Message.Text).IsEqualTo(before);
    }

    [Test]
    [Arguments('1', FinishOption.Squash)]
    [Arguments('2', FinishOption.Merge)]
    [Arguments('3', FinishOption.Rebase)]
    [Arguments('4', FinishOption.PullRequest)]
    public async Task NumberKeysPickAnOption(char key, FinishOption expected)
    {
        // Arrange
        var h = new Harness(FinishOption.Merge);

        // Act
        h.Key(key);

        // Assert
        await Assert.That(h.Form.Option).IsEqualTo(expected);
    }

    [Test]
    [Arguments(Sdl.K_RIGHT, FinishOption.Rebase)]
    [Arguments('l', FinishOption.Rebase)]
    [Arguments(Sdl.K_LEFT, FinishOption.Squash)]
    [Arguments('h', FinishOption.Squash)]
    public async Task ArrowsAndHLCycleTheOptions(uint key, FinishOption expected)
    {
        // Arrange
        var h = new Harness(FinishOption.Merge);

        // Act
        h.Key(key);

        // Assert
        await Assert.That(h.Form.Option).IsEqualTo(expected);
    }

    [Test]
    public async Task EscapeCloses()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key(Sdl.K_ESCAPE);

        // Assert
        await Assert.That(h.Overlay.IsOpen).IsFalse();
        await Assert.That(h.Overlay.IsShowing(h.Form)).IsFalse();
    }

    [Test]
    public async Task AClickOutsideCloses()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Overlay.HandleMouseDown(1, 1, 0);

        // Assert
        await Assert.That(h.Overlay.IsOpen).IsFalse();
    }

    [Test]
    public async Task EnterConfirmsAConfirmation()
    {
        // Arrange
        var overlay = new FinishWorktreeOverlay();
        int confirmed = 0;
        var confirm = Confirm(() => confirmed++);
        overlay.Open(confirm, new Theme(), 0);

        // Act
        overlay.HandleKey(Sdl.K_RETURN, 0, 0);

        // Assert
        await Assert.That(confirmed).IsEqualTo(1);
        await Assert.That(overlay.IsShowing(confirm)).IsTrue();
    }

    [Test]
    public async Task ABusyConfirmationIgnoresEnter()
    {
        // Arrange
        var overlay = new FinishWorktreeOverlay();
        int confirmed = 0;
        overlay.Open(Confirm(() => confirmed++, busy: true), new Theme(), 0);

        // Act
        overlay.HandleKey(Sdl.K_KP_ENTER, 0, 0);

        // Assert
        await Assert.That(confirmed).IsEqualTo(0);
    }

    [Test]
    public async Task EscapeKeepsEverythingInAConfirmation()
    {
        // Arrange
        var overlay = new FinishWorktreeOverlay();
        int confirmed = 0;
        overlay.Open(Confirm(() => confirmed++), new Theme(), 0);

        // Act
        overlay.HandleKey(Sdl.K_ESCAPE, 0, 0);

        // Assert
        await Assert.That(overlay.IsOpen).IsFalse();
        await Assert.That(confirmed).IsEqualTo(0);
    }

    [Test]
    public async Task OpeningAConfirmationReplacesTheForm()
    {
        // Arrange
        var h = new Harness();
        var confirm = Confirm(() => { });

        // Act
        h.Overlay.Open(confirm, new Theme(), 0);

        // Assert
        await Assert.That(h.Overlay.IsShowing(h.Form)).IsFalse();
        await Assert.That(h.Overlay.IsShowing(confirm)).IsTrue();
        await Assert.That(h.Overlay.EditingMessage).IsFalse();
    }
}

/// <summary>The worktree switcher and cleanup list: selection, checks, and what each key asks for.</summary>
public class WorktreesOverlayTests
{
    private static WorktreeRow Row(string path, bool selectable = true, int dashboards = 0) => new()
    {
        Entry = new WorktreeEntry(path, Head: null, Branch: Path.GetFileName(path), Bare: false, Detached: false, Locked: null, Prunable: null, IsMain: false),
        Title = Path.GetFileName(path),
        Selectable = selectable,
        Dashboards = [.. Enumerable.Range(0, dashboards).Select(i => new AspireDashboard("/w/AppHost.csproj", $"https://localhost:{17000 + i}", 17000 + i))],
    };

    private static WorktreesOverlay Open(bool cleanup, params WorktreeRow[] rows)
    {
        var overlay = new WorktreesOverlay();
        overlay.Open("Worktrees", "app", cleanup, new Theme(), 0);
        overlay.SetRows([.. rows]);
        return overlay;
    }

    private static string? Activated(WorktreesOverlay overlay)
    {
        string? got = null;
        overlay.Activated = r => got = r.Entry.Path;
        overlay.HandleKey(Sdl.K_RETURN, 0);
        return got;
    }

    [Test]
    public async Task EnterActivatesTheSelectedWorktree()
    {
        // Arrange
        var overlay = Open(false, Row("/w/a"), Row("/w/b"));
        overlay.HandleKey('j', 0);

        // Act
        string? got = Activated(overlay);

        // Assert
        await Assert.That(got).IsEqualTo("/w/b");
    }

    [Test]
    public async Task SelectionWrapsBothWays()
    {
        // Arrange
        var overlay = Open(false, Row("/w/a"), Row("/w/b"), Row("/w/c"));

        // Act
        overlay.HandleKey(Sdl.K_UP, 0);

        // Assert
        await Assert.That(Activated(overlay)).IsEqualTo("/w/c");
    }

    [Test]
    public async Task NewRowsKeepTheSelectionOnTheSameWorktree()
    {
        // Arrange
        var overlay = Open(false, Row("/w/a"), Row("/w/b"));
        overlay.HandleKey(Sdl.K_TAB, 0);

        // Act: a new worktree appears before it.
        overlay.SetRows([Row("/w/0"), Row("/w/a"), Row("/w/b")]);

        // Assert
        await Assert.That(Activated(overlay)).IsEqualTo("/w/b");
    }

    [Test]
    public async Task SpaceChecksARowInCleanupMode()
    {
        // Arrange
        var overlay = Open(true, Row("/w/a"), Row("/w/b"));

        // Act
        overlay.HandleKey(' ', 0);

        // Assert
        await Assert.That(overlay.CheckedRows.Select(r => r.Entry.Path)).IsEquivalentTo(["/w/a"]);
    }

    [Test]
    public async Task ARowThatCannotGoIsNeverChecked()
    {
        // Arrange
        var overlay = Open(true, Row("/w/a", selectable: false));

        // Act
        overlay.HandleKey(' ', 0);

        // Assert
        await Assert.That(overlay.CheckedRows).IsEmpty();
    }

    [Test]
    public async Task ChecksSurviveARefresh()
    {
        // Arrange
        var overlay = Open(true, Row("/w/a"), Row("/w/b"));
        overlay.HandleKey(' ', 0);

        // Act
        overlay.SetRows([Row("/w/a"), Row("/w/b")]);

        // Assert
        await Assert.That(overlay.CheckedRows.Select(r => r.Entry.Path)).IsEquivalentTo(["/w/a"]);
    }

    [Test]
    public async Task ACheckIsDroppedWhenTheRowCanNoLongerGo()
    {
        // Arrange
        var overlay = Open(true, Row("/w/a"));
        overlay.HandleKey(' ', 0);

        // Act: something opened in it meanwhile.
        overlay.SetRows([Row("/w/a", selectable: false)]);

        // Assert
        await Assert.That(overlay.CheckedRows).IsEmpty();
    }

    [Test]
    public async Task EnterInCleanupModeAsksToRemove()
    {
        // Arrange
        var overlay = Open(true, Row("/w/a"));
        bool remove = false, activated = false;
        overlay.RemoveRequested = () => remove = true;
        overlay.Activated = _ => activated = true;

        // Act
        overlay.HandleKey(Sdl.K_KP_ENTER, 0);

        // Assert
        await Assert.That(remove).IsTrue();
        await Assert.That(activated).IsFalse();
    }

    [Test]
    public async Task SwitcherLettersAskForTheirActions()
    {
        // Arrange
        var overlay = Open(false, Row("/w/a", dashboards: 1));
        var asked = new List<string>();
        overlay.NewRequested = () => asked.Add("new");
        overlay.FinishRequested = r => asked.Add("finish " + r.Title);
        overlay.CleanUpRequested = () => asked.Add("cleanup");
        overlay.RefreshRequested = () => asked.Add("refresh");
        overlay.DashboardRequested = d => asked.Add("dashboard " + d.Url);

        // Act
        foreach (char key in "nfcrd") overlay.HandleKey(key, 0);

        // Assert
        await Assert.That(asked).IsEquivalentTo(["new", "finish a", "cleanup", "refresh", "dashboard https://localhost:17000"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CleanupModeOnlyRefreshes()
    {
        // Arrange
        var overlay = Open(true, Row("/w/a", dashboards: 1));
        var asked = new List<string>();
        overlay.NewRequested = () => asked.Add("new");
        overlay.FinishRequested = _ => asked.Add("finish");
        overlay.CleanUpRequested = () => asked.Add("cleanup");
        overlay.RefreshRequested = () => asked.Add("refresh");
        overlay.DashboardRequested = _ => asked.Add("dashboard");

        // Act
        foreach (char key in "nfcrd") overlay.HandleKey(key, 0);

        // Assert
        await Assert.That(asked).IsEquivalentTo(["refresh"]);
    }

    [Test]
    public async Task DWithoutADashboardDoesNothing()
    {
        // Arrange
        var overlay = Open(false, Row("/w/a"));
        bool asked = false;
        overlay.DashboardRequested = _ => asked = true;

        // Act
        overlay.HandleKey('d', 0);

        // Assert
        await Assert.That(asked).IsFalse();
    }

    [Test]
    public async Task EscapeAndAClickOutsideClose()
    {
        // Arrange
        var a = Open(false, Row("/w/a"));
        var b = Open(false, Row("/w/a"));

        // Act
        a.HandleKey(Sdl.K_ESCAPE, 0);
        b.HandleMouseDown(1, 1, 0);

        // Assert
        await Assert.That(a.IsOpen).IsFalse();
        await Assert.That(b.IsOpen).IsFalse();
    }

    [Test]
    public async Task OpeningStartsEmptyInTheRequestedMode()
    {
        // Arrange
        var overlay = Open(false, Row("/w/a"));

        // Act
        overlay.Open("Clean Up", "app", cleanup: true, new Theme(), 0);

        // Assert
        await Assert.That(overlay.IsCleanup).IsTrue();
        await Assert.That(Activated(overlay)).IsNull();
    }
}

/// <summary>The agents overview: selection, jumping, and answering a permission prompt from the list.</summary>
public class AgentsOverlayTests
{
    private sealed class P(string name)
    {
        public override string ToString() => name;
    }

    private static AgentRow<P> Row(P pane, AgentPrompt? prompt = null, AgentUsage? usage = null) =>
        new(pane, 0, 1, 1, AgentState.Working, pane.ToString(), "", "", "", usage, false, 0, prompt);

    private static AgentsOverlay<P> Open(List<AgentRow<P>> rows)
    {
        var overlay = new AgentsOverlay<P> { Source = () => rows };
        overlay.Open(new Theme(), 0);
        return overlay;
    }

    [Test]
    public async Task EnterJumpsToTheSelectedAgent()
    {
        // Arrange
        P a = new("a"), b = new("b");
        var overlay = Open([Row(a), Row(b)]);
        P? jumped = null;
        overlay.Jump = p => jumped = p;
        overlay.HandleKey('j', 0);

        // Act
        overlay.HandleKey(Sdl.K_RETURN, 0);

        // Assert
        await Assert.That(jumped).IsSameReferenceAs(b);
        await Assert.That(overlay.IsOpen).IsFalse();
    }

    [Test]
    public async Task UpFromTheTopWrapsToTheBottom()
    {
        // Arrange
        P a = new("a"), b = new("b");
        var overlay = Open([Row(a), Row(b)]);
        P? jumped = null;
        overlay.Jump = p => jumped = p;

        // Act
        overlay.HandleKey('k', 0);
        overlay.HandleKey(Sdl.K_KP_ENTER, 0);

        // Assert
        await Assert.That(jumped).IsSameReferenceAs(b);
    }

    [Test]
    public async Task ARefreshKeepsTheSelectionOnItsPane()
    {
        // Arrange
        P a = new("a"), b = new("b"), c = new("c");
        var rows = new List<AgentRow<P>> { Row(a), Row(b) };
        var overlay = new AgentsOverlay<P> { Source = () => rows };
        overlay.Open(new Theme(), 0);
        overlay.HandleKey(Sdl.K_DOWN, 0);
        P? jumped = null;
        overlay.Jump = p => jumped = p;

        // Act: c became more urgent and sorts first.
        rows = [Row(c), Row(a), Row(b)];
        overlay.Refresh();
        overlay.HandleKey(Sdl.K_RETURN, 0);

        // Assert
        await Assert.That(jumped).IsSameReferenceAs(b);
    }

    [Test]
    [Arguments('y', "allow")]
    [Arguments('n', "deny")]
    public async Task YAndNAnswerTheSelectedPrompt(char key, string expected)
    {
        // Arrange
        var prompt = new AgentPrompt("Run make?", [], new AgentAnswer("Yes", "allow"), new AgentAnswer("No", "deny"));
        var overlay = Open([Row(new P("a"), prompt)]);
        string? answer = null;
        overlay.Answer = (_, a) => answer = a.Keys;

        // Act
        overlay.HandleKey(key, 0);

        // Assert
        await Assert.That(answer).IsEqualTo(expected);
    }

    [Test]
    public async Task YWithAModifierDoesNotAnswer()
    {
        // Arrange
        var prompt = new AgentPrompt("Run make?", [], new AgentAnswer("Yes", "allow"), new AgentAnswer("No", "deny"));
        var overlay = Open([Row(new P("a"), prompt)]);
        bool answered = false;
        overlay.Answer = (_, _) => answered = true;

        // Act
        overlay.HandleKey('y', 0, plain: false);

        // Assert
        await Assert.That(answered).IsFalse();
    }

    [Test]
    public async Task YOnARowWithoutAPromptDoesNothing()
    {
        // Arrange
        var overlay = Open([Row(new P("a"))]);
        bool answered = false;
        overlay.Answer = (_, _) => answered = true;

        // Act
        overlay.HandleKey('y', 0);

        // Assert
        await Assert.That(answered).IsFalse();
        await Assert.That(overlay.IsOpen).IsTrue();
    }

    [Test]
    public async Task AClickOffTheListCloses()
    {
        // Arrange
        var overlay = Open([Row(new P("a"))]);

        // Act
        overlay.HandleMouseDown(1, 1, 0);

        // Assert
        await Assert.That(overlay.IsOpen).IsFalse();
    }

    [Test]
    public async Task EnterOnAnEmptyListJustStaysOpen()
    {
        // Arrange
        var overlay = Open([]);
        bool jumped = false;
        overlay.Jump = _ => jumped = true;

        // Act
        overlay.HandleKey(Sdl.K_DOWN, 0);
        overlay.HandleKey(Sdl.K_RETURN, 0);

        // Assert
        await Assert.That(jumped).IsFalse();
        await Assert.That(overlay.IsOpen).IsTrue();
    }

    [Test]
    public async Task EscapeCloses()
    {
        // Arrange
        var overlay = Open([Row(new P("a"))]);

        // Act
        overlay.HandleKey(Sdl.K_ESCAPE, 0);

        // Assert
        await Assert.That(overlay.IsOpen).IsFalse();
        await Assert.That(overlay.IsAnimating(0)).IsTrue();
    }
}
