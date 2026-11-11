using ErikTerm.App;
using ErikTerm.App.Agents;
using ErikTerm.App.Panes;
using ErikTerm.App.Session;
using ErikTerm.App.Ui;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

/// <summary>"Resume N agents": which saved panes are offered, that nothing starts without the click, and what the click does.</summary>
public class AgentResumeTests
{
    private readonly long T0 = Environment.TickCount64; // the real clock: Resume stamps its starts with it, and a fresh CI machine has little uptime
    private const string SessionId = "1f2e3d4c-5b6a-4788-9900-aabbccddeeff";

    private sealed class FakePane(int id) : IAgentPane
    {
        public int Id { get; } = id;
        public Terminal Term { get; } = new(40, 6, 100);
        public string Label { get; set; } = "shell";
        public bool LayoutHidden => false;
        public AgentPane Agent { get; } = new();
        public AgentState AgentState => Agent.State;
        public bool AgentActive => Agent.Active;
        public bool Exited { get; set; }
        public Selection? Selection { get; set; }
        public Teammate? Teammate { get; set; }
        public string? ForegroundName { get; set; } = "zsh";
        public AgentDefinition? Foreground;
        public string? KnownDirectory { get; set; }
        public RectF AgentBounds(long now) => new(0, 0, 400, 200);
        public int ForegroundProcessGroup() => 42;
        public AgentDefinition? IdentifyForeground() => Foreground;
    }

    private sealed class FakeWindow : IAgentResumeWindow<FakePane>
    {
        public List<LayoutTree<FakePane>> Tabs { get; } = [];
        public FakePane Focused => Tabs[0].LeafList[0];
        public FakePane? Zoomed => null;
        public LayoutTree<FakePane> Layout => Tabs[ActiveTabIndex];
        public int ShownPaneCount => Layout.LeafList.Count;
        public int TabCount => Tabs.Count;
        public int ActiveTabIndex { get; set; }
        public Theme Theme { get; } = new();
        public bool SuppressText { get; set; }

        public readonly List<(FakePane Pane, string[] Argv, string Directory)> Started = [];
        public readonly List<string> Toasts = [];
        public HashSet<string> MissingPrograms { get; } = [];
        public bool StartFails;

        public LayoutTree<FakePane> TabLayout(int tabIndex) => Tabs[tabIndex];
        public List<FakePane> VisiblePanes() => Layout.LeafList;
        public FakePane? FindPane(int id) => Tabs.SelectMany(t => t.LeafList).FirstOrDefault(p => p.Id == id);
        public void Focus(FakePane pane) { }
        public void SetZoom(FakePane? pane) { }
        public void ApplyLayout(bool animate) { }
        public void ActivateTab(int index) => ActiveTabIndex = index;
        public void ClearTabZoom(int tabIndex) { }
        public void Redraw() { }
        public void Toast(ToastKind kind, string title, string? detail = null) => Toasts.Add(detail is null ? title : $"{title}: {detail}");

        public bool AgentProgramFound(string program) => !MissingPrograms.Contains(program);

        public bool StartAgentInPane(FakePane pane, string[] argv, string directory)
        {
            if (StartFails) return false;
            Started.Add((pane, argv, directory));
            return true;
        }
    }

    private readonly FakeWindow _window = new();
    private readonly WindowAgentResume<FakePane> _resume;
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("erikterm-resume-test-");
    private int _nextPane = 1;

    public AgentResumeTests() => _resume = new WindowAgentResume<FakePane>(_window);

    [After(Test)]
    public void Cleanup()
    {
        if (_dir.Exists) _dir.Delete(recursive: true);
    }

    private FakePane AddPane()
    {
        var pane = new FakePane(_nextPane++);
        _window.Tabs.Add(new LayoutTree<FakePane>(pane));
        return pane;
    }

    private SessionPane Saved(string? agent = "claude-code", string? session = SessionId, string? directory = null) => new()
    {
        Cwd = directory ?? _dir.FullName,
        Agent = agent is null ? null : new SessionAgent { Id = agent, SessionId = session ?? "" },
    };

    private FakePane Offered(string? agent = "claude-code", string? session = SessionId)
    {
        var pane = AddPane();
        _resume.Consider(pane, Saved(agent, session));
        return pane;
    }

    // ---- Resume commands ---------------------------------------------------------------------------

    [Test]
    [Arguments("claude-code", "claude --resume abc-1")]
    [Arguments("codex", "codex resume abc-1")]
    [Arguments("gemini-cli", "gemini --resume abc-1")]
    [Arguments("opencode", "opencode --session abc-1")]
    [Arguments("amp", "amp threads continue abc-1")]
    [Arguments("cursor-agent", "cursor-agent --resume abc-1")]
    [Arguments("copilot-cli", "copilot --resume=abc-1")]
    [Arguments("droid", "droid --resume abc-1")]
    public async Task EachAgentResumesWithItsOwnCommand(string agent, string expected)
    {
        // Arrange
        const string id = "abc-1";

        // Act
        var argv = AgentResume.Command(agent, id);

        // Assert
        await Assert.That(string.Join(' ', argv!)).IsEqualTo(expected);
    }

    [Test]
    public async Task AnAgentWithoutSessionsHasNoResumeCommand()
    {
        // Arrange
        const string agent = "aider";

        // Act
        var argv = AgentResume.Command(agent, SessionId);

        // Assert
        await Assert.That(argv).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("-x")]
    [Arguments("--dangerously-skip-permissions")]
    [Arguments("a b")]
    [Arguments("a;rm -rf")]
    [Arguments("a\nb")]
    [Arguments("$(id)")]
    public async Task AnUnusableSessionIdGivesNoCommand(string id)
    {
        // Arrange
        const string agent = "claude-code";

        // Act
        var argv = AgentResume.Command(agent, id);

        // Assert
        await Assert.That(argv).IsNull();
    }

    [Test]
    public async Task ASessionIdLongerThanAnyRealOneIsRefused()
    {
        // Arrange
        string id = new('a', 129);

        // Act
        bool valid = AgentResume.IsSessionId(id);

        // Assert
        await Assert.That(valid).IsFalse();
    }

    // ---- Which panes are offered -------------------------------------------------------------------

    [Test]
    public async Task APaneWithAnAgentAndADirectoryIsOffered()
    {
        // Arrange
        Offered();

        // Act
        _resume.Tick(T0);

        // Assert
        await Assert.That(_resume.Count).IsEqualTo(1);
        await Assert.That(_resume.Label).IsEqualTo("Resume 1 agent");
    }

    [Test]
    public async Task TheLabelCountsThePanes()
    {
        // Arrange
        Offered();
        Offered("codex");
        Offered("copilot-cli");

        // Act
        _resume.Tick(T0);

        // Assert
        await Assert.That(_resume.Label).IsEqualTo("Resume 3 agents");
    }

    [Test]
    public async Task APaneFromAnOldFileWithoutTheFieldIsNotOffered()
    {
        // Arrange
        var pane = AddPane();

        // Act
        _resume.Consider(pane, Saved(agent: null));
        _resume.Tick(T0);

        // Assert
        await Assert.That(_resume.Count).IsEqualTo(0);
    }

    [Test]
    public async Task APaneWhoseDirectoryIsGoneIsNotOffered()
    {
        // Arrange
        var pane = AddPane();
        string missing = Path.Combine(_dir.FullName, "deleted");

        // Act
        _resume.Consider(pane, Saved(directory: missing));
        _resume.Tick(T0);

        // Assert
        await Assert.That(_resume.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ASessionIdThatCouldBeAFlagIsNotOffered()
    {
        // Arrange
        var pane = AddPane();

        // Act
        _resume.Consider(pane, Saved(session: "--dangerously-skip-permissions"));
        _resume.Tick(T0);

        // Assert
        await Assert.That(_resume.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AnAgentWithoutAResumeCommandIsNotOffered()
    {
        // Arrange
        var pane = AddPane();

        // Act
        _resume.Consider(pane, Saved("aider"));
        _resume.Tick(T0);

        // Assert
        await Assert.That(_resume.Count).IsEqualTo(0);
    }

    [Test]
    public async Task APaneRunningSomethingIsNotCountedUntilItsBackAtTheShell()
    {
        // Arrange
        var pane = Offered();
        pane.ForegroundName = "nvim";
        _resume.Tick(T0);
        int whileBusy = _resume.Count;

        // Act
        pane.ForegroundName = "zsh";
        _resume.Tick(T0 + 2000);

        // Assert
        await Assert.That(whileBusy).IsEqualTo(0);
        await Assert.That(_resume.Count).IsEqualTo(1);
    }

    [Test]
    public async Task AnAgentStartedByHandTakesThePaneOffTheOffer()
    {
        // Arrange
        var pane = Offered();
        _resume.Tick(T0);

        // Act
        pane.Agent.Definition = AgentCatalog.Claude;
        pane.ForegroundName = "claude";
        _resume.Tick(T0 + 2000);
        pane.Agent.Definition = null;
        pane.ForegroundName = "zsh";
        _resume.Tick(T0 + 4000);

        // Assert
        await Assert.That(_resume.Count).IsEqualTo(0);
        await Assert.That(pane.Agent.SavedAgent).IsNull();
    }

    [Test]
    public async Task AClosedPaneLeavesTheOffer()
    {
        // Arrange
        var pane = Offered();
        var other = Offered("codex");
        _resume.Tick(T0);

        // Act
        _window.Tabs.RemoveAt(0);
        _resume.Tick(T0 + 2000);

        // Assert
        await Assert.That(_resume.Count).IsEqualTo(1);
        await Assert.That(other.Agent.SavedAgent).IsNotNull();
        await Assert.That(pane.Agent.SavedAgent).IsNull();
    }

    // ---- Never without the click -------------------------------------------------------------------

    [Test]
    public async Task NothingStartsWithoutTheClick()
    {
        // Arrange
        var pane = Offered();
        Offered("codex");

        // Act
        for (long t = 0; t < 120; t++) _resume.Tick(T0 + t * 1000);

        // Assert
        await Assert.That(_window.Started).IsEmpty();
        await Assert.That(pane.Agent.SavedAgent).IsNotNull();
    }

    // ---- The click ---------------------------------------------------------------------------------

    [Test]
    public async Task TheClickStartsEachAgentInItsOwnPaneInEveryTab()
    {
        // Arrange
        var claude = Offered();
        var codex = Offered("codex", "019a0000-1111");
        _resume.Tick(T0);

        // Act
        _resume.Resume();

        // Assert
        await Assert.That(_window.Started.Count).IsEqualTo(2);
        await Assert.That(_window.Started[0].Pane).IsEqualTo(claude);
        await Assert.That(string.Join(' ', _window.Started[0].Argv)).IsEqualTo($"claude --resume {SessionId}");
        await Assert.That(_window.Started[0].Directory).IsEqualTo(_dir.FullName);
        await Assert.That(_window.Started[1].Pane).IsEqualTo(codex);
        await Assert.That(string.Join(' ', _window.Started[1].Argv)).IsEqualTo("codex resume 019a0000-1111");
    }

    [Test]
    public async Task TheOfferIsGoneOnceUsed()
    {
        // Arrange
        Offered();
        _resume.Tick(T0);

        // Act
        _resume.Resume();
        _resume.Resume();
        _resume.Tick(T0 + 2000);

        // Assert
        await Assert.That(_resume.Count).IsEqualTo(0);
        await Assert.That(_window.Started.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ABusyPaneIsSkippedAndSaidSo()
    {
        // Arrange
        var free = Offered();
        var busy = Offered("codex");
        _resume.Tick(T0);
        busy.ForegroundName = "htop";

        // Act
        _resume.Resume();

        // Assert
        await Assert.That(_window.Started.Count).IsEqualTo(1);
        await Assert.That(_window.Started[0].Pane).IsEqualTo(free);
        await Assert.That(_window.Toasts[0]).IsEqualTo("Resumed 1 of 2 agents: pane is busy");
        await Assert.That(busy.Agent.SavedAgent).IsNull();
    }

    [Test]
    public async Task ADirectoryRemovedBeforeTheClickIsSkipped()
    {
        // Arrange
        Offered();
        _resume.Tick(T0);
        _dir.Delete(recursive: true);

        // Act
        _resume.Resume();

        // Assert
        await Assert.That(_window.Started).IsEmpty();
        await Assert.That(_window.Toasts[0]).IsEqualTo("Resumed 0 of 1 agent: directory is gone");
    }

    [Test]
    public async Task AnAgentNotOnPathIsSkippedAndNamed()
    {
        // Arrange
        Offered();
        _window.MissingPrograms.Add("claude");
        _resume.Tick(T0);

        // Act
        _resume.Resume();

        // Assert
        await Assert.That(_window.Started).IsEmpty();
        await Assert.That(_window.Toasts[0]).IsEqualTo("Resumed 0 of 1 agent: claude isn't on PATH");
    }

    [Test]
    public async Task AFailedStartIsReported()
    {
        // Arrange
        var pane = Offered();
        _window.StartFails = true;
        _resume.Tick(T0);

        // Act
        _resume.Resume();

        // Assert
        await Assert.That(_window.Toasts[0]).IsEqualTo("Resumed 0 of 1 agent: claude couldn't start");
        await Assert.That(pane.Agent.SavedAgent).IsNull();
    }

    [Test]
    public async Task DismissingStartsNothingAndForgetsTheOffer()
    {
        // Arrange
        var pane = Offered();
        _resume.Tick(T0);

        // Act
        _resume.Dismiss();
        _resume.Tick(T0 + 2000);

        // Assert
        await Assert.That(_window.Started).IsEmpty();
        await Assert.That(_resume.Count).IsEqualTo(0);
        await Assert.That(_resume.Capture(pane)).IsNull();
    }

    // ---- What is saved -----------------------------------------------------------------------------

    [Test]
    public async Task ARunningAgentWithASessionIdIsCaptured()
    {
        // Arrange
        var pane = AddPane();
        pane.Agent.Definition = AgentCatalog.Claude;
        pane.Agent.Usage = new AgentUsage("Opus", null, null, null, null, SessionId);

        // Act
        var saved = _resume.Capture(pane);

        // Assert
        await Assert.That(saved!.Id).IsEqualTo("claude-code");
        await Assert.That(saved.SessionId).IsEqualTo(SessionId);
    }

    [Test]
    public async Task AnAgentWithoutASessionIdIsNotCaptured()
    {
        // Arrange
        var pane = AddPane();
        pane.Agent.Definition = AgentCatalog.Claude;
        pane.Agent.Usage = new AgentUsage("Opus", null, null, null, null, null);

        // Act
        var saved = _resume.Capture(pane);

        // Assert
        await Assert.That(saved).IsNull();
    }

    [Test]
    public async Task UsageLeftBehindByAnAgentThatExitedIsNotCaptured()
    {
        // Arrange (agent_status off: nothing clears Usage or sets Definition)
        var pane = AddPane();
        pane.Agent.Usage = new AgentUsage("Opus", null, null, null, null, SessionId);
        pane.Foreground = null;

        // Act
        var saved = _resume.Capture(pane);

        // Assert
        await Assert.That(saved).IsNull();
    }

    [Test]
    public async Task AnUnansweredOfferStaysInTheNextSave()
    {
        // Arrange
        var pane = Offered();
        _resume.Tick(T0);

        // Act
        var saved = _resume.Capture(pane);

        // Assert
        await Assert.That(saved!.SessionId).IsEqualTo(SessionId);
    }

    [Test]
    public async Task AResumedAgentStaysSavedUntilItReportsItsOwnSession()
    {
        // Arrange
        var pane = Offered();
        _resume.Tick(T0);
        _resume.Resume();
        pane.Agent.Definition = AgentCatalog.Claude;
        long now = Environment.TickCount64;
        _resume.Tick(now + 2000);
        var beforeReport = _resume.Capture(pane);

        // Act
        pane.Agent.Usage = new AgentUsage("Opus", null, null, null, null, "new-session");
        _resume.Tick(now + 4000);

        // Assert
        await Assert.That(beforeReport!.SessionId).IsEqualTo(SessionId);
        await Assert.That(pane.Agent.SavedAgent).IsNull();
        await Assert.That(_resume.Capture(pane)!.SessionId).IsEqualTo("new-session");
    }

    [Test]
    public async Task AResumedAgentThatNeverCameUpIsForgotten()
    {
        // Arrange
        var pane = Offered();
        _resume.Tick(T0);
        _resume.Resume();
        long now = Environment.TickCount64;

        // Act
        _resume.Tick(now + 11_000);

        // Assert
        await Assert.That(pane.Agent.SavedAgent).IsNull();
    }
}
