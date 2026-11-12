using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.App.Worktrees;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

/// <summary>The worktree features of a window: row texts, what the window's panes are grouped by, the last-moment agent checks, and a launch's next program.</summary>
public class WindowWorktreesTests
{
    private sealed class FakePane(int id) : IWorktreePane
    {
        public int Id { get; } = id;
        public Terminal Term { get; } = new(40, 6, 100);
        public string Label => "shell";
        public bool LayoutHidden => false;
        public string? Cwd;
        public AgentState AgentState { get; set; }
        public bool AgentActive { get; set; }
        public string AgentLabel { get; set; } = "Agent";
        public int? ExitCode { get; set; }
        public bool CloseOnExit { get; set; }
        public string[] LaunchArgv { get; set; } = [];
        public string? LaunchCwd { get; set; }
        public IReadOnlyList<string>? LaunchedAgent { get; set; }
        public WorktreeLaunchPlan? WorktreeLaunch { get; set; }
        public string? StartDirectory(string? defaultCwd) => Cwd;
    }

    private sealed class FakeWindow(FakePane first) : IWorktreeWindow<FakePane>
    {
        private readonly Queue<Action> _background = new();

        public FakePane Focused { get; } = first;
        public FakePane? Zoomed => null;
        public List<LayoutTree<FakePane>> Tabs { get; } = [new LayoutTree<FakePane>(first)];
        public LayoutTree<FakePane> Layout => Tabs[0];
        public int ShownPaneCount => Layout.LeafList.Count;
        public int TabCount => Tabs.Count;
        public int ActiveTabIndex => 0;
        public Theme Theme { get; } = new();
        public Theme OverlayTheme => Theme;
        public bool SuppressText { get; set; }
        public bool GitHubEnabled => false;
        public bool GitHubSignedIn => false;

        public readonly List<RepoPlan> Plans = [];
        public readonly List<(FakePane Pane, string[] Argv)> Respawned = [];
        public readonly List<string> Toasts = [];

        public LayoutTree<FakePane> TabLayout(int tabIndex) => Tabs[tabIndex];
        public List<FakePane> VisiblePanes() => Layout.LeafList;
        public FakePane? FindPane(int id) => Layout.LeafList.FirstOrDefault(p => p.Id == id);
        public void Focus(FakePane pane) { }
        public void SetZoom(FakePane? pane) { }
        public void ApplyLayout(bool animate) { }
        public void ActivateTab(int index) { }
        public void ClearTabZoom(int tabIndex) { }
        public void Redraw() { }
        public void DefaultCursor() { }
        public void Toast(ToastKind kind, string title, string? detail = null) => Toasts.Add(title);
        public string[] ShortcutFor(ActionSpec action) => [];
        public void CloseMenus(long now) { }
        public string? IntegrationCwd(FakePane pane) => pane.Cwd;
        public readonly List<string> OpenedLinks = [];
        public string? OpenDashboardLink(string url) { OpenedLinks.Add(url); return null; }
        public void StartRepoPlan(RepoPlan plan, Action<string>? failed, Action? started) { Plans.Add(plan); started?.Invoke(); }
        public FakePane NewPane { get; } = new(7);
        public readonly List<WorktreeTabLaunch> TabLaunches = [];
        public readonly List<(FakePane Target, IReadOnlyList<string> Argv, bool Focus)> SplitLaunches = [];
        public string? LaunchFailure;

        public string? OpenLaunchTab(WorktreeTabLaunch launch, Action<FakePane> prepared)
        {
            if (LaunchFailure is not null) return LaunchFailure;
            TabLaunches.Add(launch);
            prepared(NewPane);
            return null;
        }

        public FakePane? OpenLaunchSplitNextTo(FakePane target, IReadOnlyList<string> argv, string cwd, bool focus, Action<Terminal>? echo)
        {
            SplitLaunches.Add((target, argv, focus));
            return NewPane;
        }

        public FakePane? OpenLaunchSplit(IReadOnlyList<string> argv, string cwd, bool focus, Action<Terminal>? echo) => null;
        public bool Respawn(FakePane pane, string[] argv, string? cwd) { Respawned.Add((pane, argv)); return true; }
        public void OpenShellTab(string cwd, string? branch) { }

        public void RunInBackground<T>(Func<T?> work, Action<T?> then, bool wake = false) where T : class => _background.Enqueue(() => then(work()));

        /// <summary>Runs the queued background jobs and whatever they queue in turn.</summary>
        public void Drain()
        {
            while (_background.TryDequeue(out var job)) job();
        }
    }

    private sealed class FakeProcess : IWorktreeProcess<FakePane>
    {
        public Settings Settings { get; } = new();
        public readonly Dictionary<string, List<WorktreePaneRef<FakePane>>> Inside = new(StringComparer.Ordinal);
        public List<string> LastRoots = [];

        public Dictionary<string, List<WorktreePaneRef<FakePane>>> PanesByWorktree(List<string> roots)
        {
            LastRoots = roots;
            return Inside;
        }

        public bool JumpToWorktree(string path, List<string> roots) => false;
        public GitHubStatus? KnownGitHubStatus(string root, string branch) => null;

        public void AddAgent(string worktree) =>
            Inside[worktree] = [new WorktreePaneRef<FakePane>(new FakePane(99), AgentState.Working, AgentActive: true, "Claude")];
    }

    private readonly FakePane _pane = new(1);
    private readonly FakeWindow _window;
    private readonly FakeProcess _process = new();
    private readonly WindowWorktrees<FakePane> _sut;

    public WindowWorktreesTests()
    {
        _window = new FakeWindow(_pane);
        _sut = new WindowWorktrees<FakePane>(_window, _process, new Lock(), _ => []);
    }

    private void Press(uint key) => _sut.KeyDown(key, 0, Environment.TickCount64);

    /// <summary>A temporary repository with main, and a worktree on branch feat at main's commit (so it counts as landed).</summary>
    private sealed class TempRepo : IDisposable
    {
        private readonly string _parent;

        public TempRepo()
        {
            _parent = Directory.CreateTempSubdirectory("etww").FullName;
            string root = Path.Combine(_parent, "repo");
            Directory.CreateDirectory(root);
            Git(root, "init", "-q", "-b", "main");
            Commit(root, "first");
            Root = Git(root, "rev-parse", "--show-toplevel").Trim();
            Worktree = Path.Combine(_parent, "wt");
            Git(Root, "worktree", "add", "-q", "-b", "feat", Worktree, "main");
            Worktree = Git(Worktree, "rev-parse", "--show-toplevel").Trim();
        }

        public string Root { get; }
        public string Worktree { get; }

        public static void Commit(string cwd, string message) =>
            Git(cwd, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "-c", "commit.gpgsign=false", "commit", "-q", "--allow-empty", "-m", message);

        private static string Git(string cwd, params string[] arguments)
        {
            var result = ToolRunner.Run(["git", "-C", cwd, .. arguments], null, 30_000);
            if (!result.Ok) throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {result.Stderr}");
            return result.Stdout;
        }

        public void Dispose()
        {
            // git marks its objects read-only, and Windows won't delete those.
            foreach (string file in Directory.EnumerateFiles(_parent, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_parent, recursive: true);
        }
    }

    // ---- Row status -----------------------------------------------------------------------------

    [Test]
    public async Task StatusTextIsAnEllipsisBeforeTheFirstAnswerAndCleanWithoutChanges()
    {
        // Act
        string unknown = WindowWorktrees<FakePane>.StatusText(null, null, out bool unknownWarn);
        string clean = WindowWorktrees<FakePane>.StatusText(new WorktreeRowStatus(0, 0, 0, false), null, out bool cleanWarn);

        // Assert
        await Assert.That(unknown).IsEqualTo("…");
        await Assert.That(unknownWarn).IsFalse();
        await Assert.That(clean).IsEqualTo("clean");
        await Assert.That(cleanWarn).IsFalse();
    }

    [Test]
    public async Task StatusTextShowsAheadBehindAndWarnsOnChanges()
    {
        // Arrange
        var status = new WorktreeRowStatus(Changes: 3, Ahead: 2, Behind: 1, TimedOut: false);

        // Act
        string text = WindowWorktrees<FakePane>.StatusText(status, null, out bool warn);

        // Assert
        await Assert.That(text).IsEqualTo("↑2 ↓1 ● 3");
        await Assert.That(warn).IsTrue();
    }

    [Test]
    public async Task StatusTextSaysSlowWhenTheCheckTimedOut()
    {
        // Act
        string text = WindowWorktrees<FakePane>.StatusText(new WorktreeRowStatus(5, 5, 5, true), null, out bool warn);

        // Assert
        await Assert.That(text).IsEqualTo("slow");
        await Assert.That(warn).IsFalse();
    }

    // ---- Panes by worktree ----------------------------------------------------------------------

    [Test]
    public async Task CollectPanesGroupsAllTabsByWorktreeAndKeepsAgentFacts()
    {
        // Arrange
        _pane.Cwd = "/r/wt/src";
        _pane.AgentState = AgentState.Working;
        _pane.AgentActive = true;
        _pane.AgentLabel = "Claude";
        var second = new FakePane(2) { Cwd = "/r/wt" };
        var elsewhere = new FakePane(3) { Cwd = "/other" };
        _window.Tabs.Add(new LayoutTree<FakePane>(second));
        _window.Tabs.Add(new LayoutTree<FakePane>(elsewhere));
        var map = new Dictionary<string, List<WorktreePaneRef<FakePane>>>(StringComparer.Ordinal);

        // Act
        _sut.CollectPanes(map, ["/r/wt", "/r"]);

        // Assert
        await Assert.That(map.Keys.Single()).IsEqualTo("/r/wt");
        await Assert.That(map["/r/wt"].Select(p => p.Pane.Id).ToArray()).IsEquivalentTo([1, 2]);
        await Assert.That(map["/r/wt"][0].AgentActive).IsTrue();
        await Assert.That(map["/r/wt"][0].AgentName).IsEqualTo("Claude");
    }

    // ---- Aspire dashboards ----------------------------------------------------------------------

    [Test]
    public async Task DOpensTheDashboardOfTheSelectedWorktree()
    {
        // Arrange
        using var repo = new TempRepo();
        _pane.Cwd = repo.Root;
        var dashboard = new AspireDashboard(repo.Root + "/apphost.cs", "https://localhost:41795/login?t=abc", 41795);
        var sut = new WindowWorktrees<FakePane>(_window, _process, new Lock(), roots => new() { [roots[0]] = [dashboard] });
        sut.Execute("ShowWorktrees");
        _window.Drain();

        // Act
        sut.KeyDown('d', 0, Environment.TickCount64);

        // Assert
        await Assert.That(_window.OpenedLinks.Single()).IsEqualTo(dashboard.Url);
        await Assert.That(_window.Toasts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DDoesNothingWithoutADashboard()
    {
        // Arrange
        using var repo = new TempRepo();
        _pane.Cwd = repo.Root;
        _sut.Execute("ShowWorktrees");
        _window.Drain();

        // Act
        Press('d');

        // Assert
        await Assert.That(_window.OpenedLinks.Count).IsEqualTo(0);
    }

    // ---- Input --------------------------------------------------------------------------------

    [Test]
    public async Task WithNothingOpenKeysAndTextPassThrough()
    {
        // Act
        bool key = _sut.KeyDown(Sdl.K_RETURN, 0, 0);
        bool text = _sut.TextInput("x", 0);

        // Assert
        await Assert.That(key).IsFalse();
        await Assert.That(text).IsFalse();
        await Assert.That(_sut.AnyOpen).IsFalse();
    }

    [Test]
    public async Task ADirectoryThatIsGoneIsReportedInsteadOfOpeningAnything()
    {
        // Arrange
        _pane.Cwd = "/nonexistent/etww";

        // Act
        _sut.Execute("ShowWorktrees");

        // Assert
        await Assert.That(_sut.AnyOpen).IsFalse();
        await Assert.That(_window.Toasts.Single()).IsEqualTo("No directory to look for a repository in");
    }

    // ---- A launch's next program ------------------------------------------------------------------

    [Test]
    public async Task APaneWithoutALaunchIsNotContinued()
    {
        // Act
        bool continued = _sut.ContinueLaunch(_pane);

        // Assert
        await Assert.That(continued).IsFalse();
        await Assert.That(_window.Respawned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ASetupThatSucceededStartsTheAgentInTheSamePane()
    {
        // Arrange
        _pane.WorktreeLaunch = new WorktreeLaunchPlan(["setup"], ["agent"], ["shell"]);
        _pane.ExitCode = 0;
        _pane.LaunchCwd = "/r/wt";

        // Act
        bool continued = _sut.ContinueLaunch(_pane);

        // Assert
        await Assert.That(continued).IsTrue();
        await Assert.That(_window.Respawned.Single().Argv).IsEquivalentTo(["agent"]);
        await Assert.That(_pane.WorktreeLaunch).IsNotNull();
    }

    [Test]
    public async Task ASetupThatFailedSkipsTheAgentAndTheLaunchEnds()
    {
        // Arrange
        _pane.WorktreeLaunch = new WorktreeLaunchPlan(["setup"], ["agent"], ["shell"]);
        _pane.ExitCode = 1;

        // Act
        bool continued = _sut.ContinueLaunch(_pane);

        // Assert
        await Assert.That(continued).IsTrue();
        await Assert.That(_window.Respawned.Single().Argv).IsEquivalentTo(["shell"]);
        await Assert.That(_pane.WorktreeLaunch).IsNull();
        await Assert.That(_pane.Term.GetText(0, 0, 3, 40)).Contains("the agent was not started");
    }

    // ---- Cleanup: nothing runs unless it's still safe -------------------------------------------

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task ConfirmingCleanupStartsAVisiblePlanBehindGuards()
    {
        // Arrange
        using var repo = new TempRepo();
        _pane.Cwd = repo.Root;
        _sut.Execute("CleanUpWorktrees");
        _window.Drain();
        Press(Sdl.K_RETURN); // the list's Enter opens the confirmation

        // Act
        Press(Sdl.K_RETURN);
        _window.Drain();

        // Assert
        var plan = _window.Plans.Single();
        await Assert.That(plan.Title).IsEqualTo("Clean up");
        await Assert.That(plan.Steps[0].Argv.Contains("remove")).IsTrue();
        await Assert.That(plan.Steps[0].Guard is not null).IsTrue();
        await Assert.That(plan.Steps.Any(s => s.Argv.Contains("--force"))).IsFalse();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task AnAgentStartedAfterTheListWasReadStopsTheCleanup()
    {
        // Arrange
        using var repo = new TempRepo();
        _pane.Cwd = repo.Root;
        _sut.Execute("CleanUpWorktrees");
        _window.Drain();
        Press(Sdl.K_RETURN);
        _process.AddAgent(repo.Worktree);

        // Act
        Press(Sdl.K_RETURN);
        _window.Drain();

        // Assert
        await Assert.That(_window.Plans.Count).IsEqualTo(0);
        await Assert.That(_sut.AnyOpen).IsTrue();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task AWorktreeWithAnAgentCannotBePickedForCleanup()
    {
        // Arrange
        using var repo = new TempRepo();
        _pane.Cwd = repo.Root;
        _process.AddAgent(repo.Worktree);
        _sut.Execute("CleanUpWorktrees");
        _window.Drain();

        // Act
        Press(Sdl.K_RETURN);
        _window.Drain();

        // Assert
        await Assert.That(_window.Plans.Count).IsEqualTo(0);
        await Assert.That(_sut.AnyOpen).IsTrue();
    }

    // ---- Finish: refused again at the last moment --------------------------------------------------

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task AnAgentStartedAfterTheFormOpenedStopsTheFinish()
    {
        // Arrange
        using var repo = new TempRepo();
        TempRepo.Commit(repo.Worktree, "work");
        _sut.OpenFinish(repo.Worktree);
        _window.Drain();
        _process.AddAgent(repo.Worktree);

        // Act
        Press(Sdl.K_RETURN); // Enter on the form runs it

        // Assert
        await Assert.That(_process.LastRoots).IsEquivalentTo([repo.Worktree, repo.Root]); // the check of StartFinish ran
        await Assert.That(_window.Plans.Count).IsEqualTo(0);
    }

    // ---- erikterm cli new-worktree -----------------------------------------------------------------

    private async Task<IpcResponse> NewWorktree(IpcRequest request)
    {
        request.Command = "new-worktree";
        var first = _sut.IpcNewWorktree(_pane, request);
        await Assert.That(first.Later).IsNotNull(); // git never runs on the main thread
        _window.Drain();
        return await first.Later!.Task;
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task NewWorktreeCreatesItAndRepliesWithThePane()
    {
        // Arrange
        using var repo = new TempRepo();

        // Act
        var reply = await NewWorktree(new IpcRequest { Cwd = repo.Root, Branch = "from-cli" });
        string dir = Path.GetFullPath(Path.Combine(repo.Root, "../repo.worktrees/from-cli"));

        // Assert
        await Assert.That(reply.Ok).IsTrue();
        await Assert.That(reply.PaneId).IsEqualTo(7);
        await Assert.That(Directory.Exists(dir)).IsTrue();
        await Assert.That(_window.TabLaunches[0].Title).IsEqualTo("from-cli");
        await Assert.That(_window.TabLaunches[0].Cwd).IsEqualTo(dir);
        await Assert.That(_window.NewPane.LaunchedAgent).IsNull(); // a shell: nothing to keep out of the session
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task NewWorktreeOpensASplitNextToTheRequestingPane()
    {
        // Arrange
        using var repo = new TempRepo();

        // Act
        var reply = await NewWorktree(new IpcRequest { Cwd = repo.Root, Branch = "beside", Placement = "split", NoFocus = true });

        // Assert
        await Assert.That(reply.PaneId).IsEqualTo(7);
        await Assert.That(_window.SplitLaunches[0].Target).IsEqualTo(_pane);
        await Assert.That(_window.SplitLaunches[0].Focus).IsFalse();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task NewWorktreeReportsGitsRefusal()
    {
        // Arrange
        using var repo = new TempRepo();

        // Act
        var reply = await NewWorktree(new IpcRequest { Cwd = repo.Root, Branch = "feat" }); // TempRepo's worktree has it

        // Assert
        await Assert.That(reply.Ok).IsFalse();
        await Assert.That(reply.Error!).Contains("already exists");
        await Assert.That(_window.TabLaunches.Count).IsEqualTo(0);
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task NewWorktreeOutsideARepositoryFails()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("etnorepo");

        // Act
        var reply = await NewWorktree(new IpcRequest { Cwd = dir.FullName });
        dir.Delete();

        // Assert
        await Assert.That(reply.Ok).IsFalse();
        await Assert.That(reply.Error!).Contains("Not in a git repository");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task NewWorktreeSaysWhenThePaneCouldNotStart()
    {
        // Arrange
        using var repo = new TempRepo();
        _window.LaunchFailure = "no pty";

        // Act
        var reply = await NewWorktree(new IpcRequest { Cwd = repo.Root, Branch = "ready" });

        // Assert
        await Assert.That(reply.Ok).IsFalse();
        await Assert.That(reply.Error!).Contains("the worktree is ready");
        await Assert.That(reply.Error!).Contains("no pty");
    }

    [Test]
    public async Task NewWorktreeRefusesADirectoryThatIsGone()
    {
        // Act
        var thrown = Assert.Throws<ErikTerm.App.IpcException>(() => _sut.IpcNewWorktree(_pane, new IpcRequest { Cwd = "/no/such/directory" }));

        // Assert
        await Assert.That(thrown.Message).Contains("no such directory");
    }

    private static readonly WorktreeRepo PlanRepo = new("/src/repo", "repo", "main", ["main"]);
    private static readonly WorktreeAgent Agent = new("ag", "Agent", "ag", ["--flag"], ["--", WorktreeLauncher.PromptPlaceholder]);
    private static readonly WorktreeAgent Promptless = new("mute", "Mute", "mute", [], []);

    private static (WorktreeRequest? Request, string? Error) Plan(IpcRequest r)
    {
        var context = new CliWorktreeContext([Agent, Promptless], ["sh", "-l"],
            command => command == "gone" ? null : "/bin/" + command, "../{repo}.worktrees/{name}", "make setup");
        var request = WindowWorktrees<FakePane>.PlanCliWorktree(r, PlanRepo, "/src/repo", in context, out string? error);
        return (request, error);
    }

    [Test]
    public async Task APlanWithoutAgentHasNoAgentArgv()
    {
        // Act
        var (request, error) = Plan(new IpcRequest { Branch = "b" });

        // Assert
        await Assert.That(error).IsNull();
        await Assert.That(request!.AgentArgv).IsEmpty();
        await Assert.That(request.Base).IsEqualTo("main");
        await Assert.That(request.Setup).IsEqualTo("make setup");
        await Assert.That(request.Directory).IsEqualTo(Path.GetFullPath("/src/repo.worktrees/b"));
    }

    [Test]
    public async Task APlanNamesTheBranchAfterThePromptLikeTheForm()
    {
        // Act
        var (request, _) = Plan(new IpcRequest { Agent = "ag", Prompt = "Fix the parser" });

        // Assert
        await Assert.That(request!.Branch).IsEqualTo("fix-the-parser");
        await Assert.That(request.AgentArgv).IsEquivalentTo(["ag", "--flag", "--", "Fix the parser"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task APlanGeneratesANameWithoutAPrompt()
    {
        // Act
        var (request, _) = Plan(new IpcRequest());

        // Assert
        await Assert.That(request!.Branch.Split('-').Length).IsEqualTo(4);
    }

    [Test]
    public async Task APlanTakesTheAgentArgumentsAsGiven()
    {
        // Act
        var (request, _) = Plan(new IpcRequest { Agent = "ag", AgentArguments = "--model 'a b'", Base = "dev" });

        // Assert
        await Assert.That(request!.AgentArgv).IsEquivalentTo(["ag", "--model", "a b"], CollectionOrdering.Matching);
        await Assert.That(request.Base).IsEqualTo("dev");
    }

    [Test]
    [Arguments("nope", null, "no agent 'nope' (one of: ag, mute)")]
    [Arguments("mute", "hello", "Mute takes no first prompt")]
    public async Task APlanRefusesWhatTheAgentCannotDo(string agent, string? prompt, string expected)
    {
        // Act
        var (request, error) = Plan(new IpcRequest { Agent = agent, Prompt = prompt });

        // Assert
        await Assert.That(request).IsNull();
        await Assert.That(error).IsEqualTo(expected);
    }

    [Test]
    public async Task APlanRefusesAnAgentThatIsNotInstalled()
    {
        // Arrange
        var gone = new WorktreeAgent("g", "Gone", "gone", [], []);
        var context = new CliWorktreeContext([gone], ["sh"], _ => null, "../w/{name}", null);

        // Act
        var request = WindowWorktrees<FakePane>.PlanCliWorktree(new IpcRequest { Agent = "g" }, PlanRepo, "/src/repo", in context, out string? error);

        // Assert
        await Assert.That(request).IsNull();
        await Assert.That(error!).Contains("isn't on PATH");
    }

    [Test]
    public async Task APlanRefusesABadBranch()
    {
        // Act
        var (request, error) = Plan(new IpcRequest { Branch = "-oops" });

        // Assert
        await Assert.That(request).IsNull();
        await Assert.That(error!).Contains("can't start with -");
    }

    // ---- A repository the tests below only read --------------------------------------------------

    /// <summary>Main plus the feat worktree with one commit of its own; made once, as git is slow to set up.</summary>
    private static readonly Lazy<TempRepo> SharedRepo = new(() =>
    {
        var repo = new TempRepo();
        TempRepo.Commit(repo.Worktree, "work");
        return repo;
    });

    [After(Class)]
    public static void DeleteSharedRepo()
    {
        if (SharedRepo.IsValueCreated) SharedRepo.Value.Dispose();
    }

    // ---- Row status with a pull request ------------------------------------------------------------

    [Test]
    [Arguments("OPEN", false, "Pass", "#12 ✓")]
    [Arguments("OPEN", true, "Fail", "#12 draft ✗")]
    [Arguments("MERGED", false, "Pending", "#12 merged …")]
    [Arguments("OPEN", false, "None", "#12")]
    public async Task StatusTextNamesThePullRequestAndItsChecks(string state, bool draft, string checks, string expected)
    {
        // Arrange
        var gh = new GitHubStatus { PrNumber = 12, PrState = state, Draft = draft, Checks = Enum.Parse<CheckState>(checks) };

        // Act
        string text = WindowWorktrees<FakePane>.StatusText(new WorktreeRowStatus(0, 0, 0, false), gh, out _);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    // ---- The palette -------------------------------------------------------------------------------

    [Test]
    public async Task ThePaletteOffersNewAgentWorktreeAndTheGitEntries()
    {
        // Arrange
        var list = new List<Command>();

        // Act
        _sut.AddCommands(list);

        // Assert: git is on PATH wherever the tests run.
        await Assert.That(list.Select(c => c.Id)).IsEquivalentTo(["NewAgentWorktree", "ShowWorktrees", "FinishWorktree", "CleanUpWorktrees"], CollectionOrdering.Matching);
        await Assert.That(list[1].Title).IsEqualTo("Worktrees…");
    }

    // ---- New Agent Worktree ------------------------------------------------------------------------

    [Test]
    public async Task TheFormNeedsADirectory()
    {
        // Arrange
        _pane.Cwd = null;

        // Act
        _sut.OpenAgentForm();

        // Assert
        await Assert.That(_window.Toasts.Single()).IsEqualTo("No directory to look for a repository in");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task TheFormNeedsARepository()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("etww-norepo");
        try
        {
            _pane.Cwd = dir.FullName;

            // Act
            _sut.OpenAgentForm();
            _window.Drain();

            // Assert
            await Assert.That(_window.Toasts.Single()).IsEqualTo("Can't open New Agent Worktree");
            await Assert.That(_sut.AnyOpen).IsFalse();
        }
        finally { dir.Delete(recursive: true); }
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task InARepositoryTheFormOpensAndTakesTheKeys()
    {
        // Arrange
        var repo = SharedRepo.Value;
        _pane.Cwd = repo.Root;

        // Act
        _sut.OpenAgentForm();
        _window.Drain();
        bool text = _sut.TextInput("fix it", 0);
        bool key = _sut.KeyDown(Sdl.K_DOWN, 0, 0);

        // Assert
        await Assert.That(_sut.AnyOpen).IsTrue();
        await Assert.That(text).IsTrue();
        await Assert.That(key).IsTrue();
        await Assert.That(_sut.IsAnimating(Environment.TickCount64)).IsTrue();
    }

    /// <summary>Opens the form on a fresh repository and points its base at a branch that doesn't exist, so git refuses.</summary>
    private void FormWithAMissingBase(TempRepo repo)
    {
        _pane.Cwd = repo.Root;
        _sut.OpenAgentForm();
        _window.Drain();
        _sut.KeyDown(Sdl.K_UP, 0, 0); // Prompt → Base
        _sut.KeyDown('u', Sdl.KMOD_CTRL, 0);
        _sut.TextInput("no-such-base", 0);
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task GitsRefusalShowsInTheFormAndKeepsItOpen()
    {
        // Arrange
        var repo = SharedRepo.Value;
        FormWithAMissingBase(repo);

        // Act
        _sut.KeyDown(Sdl.K_RETURN, 0, 0);
        _window.Drain();

        // Assert
        await Assert.That(_sut.AnyOpen).IsTrue();
        await Assert.That(_window.TabLaunches).IsEmpty();
        await Assert.That(_window.Toasts).IsEmpty();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task InTheBackgroundGitsRefusalIsAToast()
    {
        // Arrange
        var repo = SharedRepo.Value;
        FormWithAMissingBase(repo);

        // Act
        _sut.KeyDown(Sdl.K_RETURN, Sdl.KMOD_CTRL, 0);
        _window.Drain();

        // Assert: one toast that it's being created, one that it couldn't be.
        await Assert.That(_window.Toasts.Count).IsEqualTo(2);
        await Assert.That(_window.Toasts[1]).StartsWith("Couldn't create worktree");
        await Assert.That(_window.TabLaunches).IsEmpty();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task OpenOverlaysTakeTheMouseAndCloseOverlaysClosesThem()
    {
        // Arrange
        var repo = SharedRepo.Value;
        _pane.Cwd = repo.Root;
        _sut.OpenAgentForm();
        _window.Drain();

        // Act
        bool move = _sut.MouseMove(1, 1, 0);
        _sut.CloseOverlays(0);
        bool after = _sut.MouseDown(1, 1, 0);

        // Assert
        await Assert.That(move).IsTrue();
        await Assert.That(after).IsFalse();
        await Assert.That(_sut.AnyOpen).IsFalse();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task AClickOutsideTheFormClosesIt()
    {
        // Arrange
        var repo = SharedRepo.Value;
        _pane.Cwd = repo.Root;
        _sut.OpenAgentForm();
        _window.Drain();

        // Act
        bool handled = _sut.MouseDown(1, 1, 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(_sut.AnyOpen).IsFalse();
    }

    // ---- The list ------------------------------------------------------------------------------------

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task TheListNeedsARepository()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("etww-norepo");
        try
        {
            _pane.Cwd = dir.FullName;

            // Act
            _sut.Execute("ShowWorktrees");
            _window.Drain();

            // Assert
            await Assert.That(_window.Toasts.Single()).IsEqualTo("Can't list worktrees");
            await Assert.That(_sut.AnyOpen).IsFalse();
        }
        finally { dir.Delete(recursive: true); }
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task TheListTakesKeysTextAndTheMouse()
    {
        // Arrange
        var repo = SharedRepo.Value;
        _pane.Cwd = repo.Root;
        _sut.Execute("ShowWorktrees");
        _window.Drain();

        // Act
        bool key = _sut.KeyDown('j', 0, 0);
        bool text = _sut.TextInput("j", 0);
        bool move = _sut.MouseMove(1, 1, 0);

        // Assert
        await Assert.That(key).IsTrue();
        await Assert.That(text).IsTrue();
        await Assert.That(move).IsTrue();
        await Assert.That(_window.SuppressText).IsTrue();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task EnterOnAWorktreeClosesTheList()
    {
        // Arrange
        var repo = SharedRepo.Value;
        _pane.Cwd = repo.Root;
        _sut.Execute("ShowWorktrees");
        _window.Drain();

        // Act
        Press(Sdl.K_RETURN);

        // Assert
        await Assert.That(_sut.AnyOpen).IsFalse();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task RefreshingAndTickingKeepTheListOpen()
    {
        // Arrange
        var repo = SharedRepo.Value;
        _pane.Cwd = repo.Root;
        _sut.Execute("ShowWorktrees");
        _window.Drain();

        // Act
        Press('r');
        _sut.Tick(Environment.TickCount64 + 60_000);
        _window.Drain();

        // Assert
        await Assert.That(_sut.AnyOpen).IsTrue();
        await Assert.That(_window.Toasts).IsEmpty();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task NInTheListOpensTheForm()
    {
        // Arrange
        var repo = SharedRepo.Value;
        _pane.Cwd = repo.Root;
        _sut.Execute("ShowWorktrees");
        _window.Drain();

        // Act
        Press('n');
        _window.Drain();
        bool text = _sut.TextInput("x", 0);

        // Assert: the form, not the list, has the text now.
        await Assert.That(_sut.AnyOpen).IsTrue();
        await Assert.That(text).IsTrue();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task CInTheListSwitchesToCleanup()
    {
        // Arrange
        var repo = SharedRepo.Value;
        _pane.Cwd = repo.Root;
        _sut.Execute("ShowWorktrees");
        _window.Drain();

        // Act
        Press('c');
        _window.Drain();
        Press(Sdl.K_RETURN); // cleanup's Enter: the confirmation for what has landed
        _window.Drain();

        // Assert
        await Assert.That(_sut.AnyOpen).IsTrue();
    }

    // ---- Finish --------------------------------------------------------------------------------------

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task FinishNeedsARepository()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("etww-norepo");
        try
        {
            _pane.Cwd = dir.FullName;

            // Act
            _sut.Execute("FinishWorktree");
            _window.Drain();

            // Assert
            await Assert.That(_window.Toasts.Single()).IsEqualTo("Can't finish this worktree");
        }
        finally { dir.Delete(recursive: true); }
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task FInTheListOpensFinishForThatWorktree()
    {
        // Arrange
        var repo = SharedRepo.Value;
        _pane.Cwd = repo.Root;
        _sut.Execute("ShowWorktrees");
        _window.Drain();
        Press('j'); // the feat worktree, after main

        // Act
        Press('f');
        _window.Drain();
        _sut.KeyDown(Sdl.K_TAB, 0, 0); // into the message

        // Assert: typing in the message lets the text through.
        await Assert.That(_sut.AnyOpen).IsTrue();
        await Assert.That(_window.SuppressText).IsFalse();
        await Assert.That(_sut.TextInput("x", 0)).IsTrue();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task EscapeClosesFinish()
    {
        // Arrange
        var repo = SharedRepo.Value;
        _sut.OpenFinish(repo.Worktree);
        _window.Drain();

        // Act
        bool handled = _sut.KeyDown(Sdl.K_ESCAPE, 0, 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(_sut.AnyOpen).IsFalse();
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    [ExcludeOn(OS.Windows)]
    public async Task FinishOnTheMainWorktreeIsRefusedInTheForm()
    {
        // Arrange: main is the target itself.
        var repo = SharedRepo.Value;

        // Act
        _sut.OpenFinish(repo.Root);
        _window.Drain();
        Press(Sdl.K_RETURN);

        // Assert
        await Assert.That(_window.Plans).IsEmpty();
    }
}
