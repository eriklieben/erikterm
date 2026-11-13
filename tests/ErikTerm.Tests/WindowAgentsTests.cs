using System.Text;
using ErikTerm.App;
using ErikTerm.App.Agents;
using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Native;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

/// <summary>A window's agent status without a window: what a poll reads, what it tells the tab and the notification center, the overview's rows, and sending text.</summary>
public class WindowAgentsTests
{
    private const long T0 = 1_000_000;

    private sealed class FakePane(int id, int rows = 6) : IAgentPane
    {
        public int Id { get; } = id;
        public Terminal Term { get; } = new(rows > 6 ? 80 : 40, rows, 100);
        public string Label { get; set; } = "shell";
        public bool LayoutHidden => false;
        public AgentPane Agent { get; } = new();
        public AgentState AgentState => Agent.State;
        public bool AgentActive => Agent.Active;
        public bool Exited { get; set; }
        public Selection? Selection { get; set; }
        public Teammate? Teammate { get; set; }
        public string? ForegroundName { get; set; }
        public AgentDefinition? Foreground;
        public int ProcessGroup = 42;
        public string? KnownDirectory { get; set; }
        public RectF AgentBounds(long now) => new(0, 0, 400, 200);
        public int ForegroundProcessGroup() => ProcessGroup;
        public AgentDefinition? IdentifyForeground() => Foreground;
    }

    private sealed class FakeWindow(FakePane first) : IAgentWindow<FakePane>
    {
        public FakePane Focused { get; set; } = first;
        public FakePane? Zoomed { get; set; }
        public List<LayoutTree<FakePane>> Tabs { get; } = [new LayoutTree<FakePane>(first)];
        public LayoutTree<FakePane> Layout => Tabs[ActiveTabIndex];
        public int ShownPaneCount => Layout.LeafList.Count;
        public int TabCount => Tabs.Count;
        public int ActiveTabIndex { get; set; }
        public uint Id => 7;
        public bool IsFocused { get; set; } = true;
        public bool IsCurrentWindow { get; set; } = true;
        public bool TabBarShown => false;
        public (int Width, int Height) PixelSize => (1000, 800);
        public int? ActiveTabId => 100 + ActiveTabIndex;
        public Theme Theme { get; } = new();
        public bool SuppressText { get; set; }

        public readonly List<AgentState> TabStates = [];
        public readonly List<(FakePane Pane, string Text)> Sent = [];
        public readonly List<string> Toasts = [];
        public readonly List<string> FocusedToasts = [];
        public readonly List<bool> Flashes = [];
        public readonly List<string> ActionToasts = [];
        public Action? LastAction;
        public List<Command>? Palette;
        public CommandPalette.Options? PaletteOptions;
        public int Syncs;

        public LayoutTree<FakePane> TabLayout(int tabIndex) => Tabs[tabIndex];
        public List<FakePane> VisiblePanes() => Layout.LeafList;
        public FakePane? FindPane(int id) => Tabs.SelectMany(t => t.LeafList).FirstOrDefault(p => p.Id == id);
        public void Focus(FakePane pane) => Focused = pane;
        public void SetZoom(FakePane? pane) => Zoomed = pane;
        public void ApplyLayout(bool animate) { }
        public void ActivateTab(int index) => ActiveTabIndex = index;
        public void ClearTabZoom(int tabIndex) { }
        public void Redraw() { }
        public void Toast(ToastKind kind, string title, string? detail = null) => Toasts.Add(title);
        public bool ZoomBadgeShown(long now) => false;
        public int TabId(int tabIndex) => 100 + tabIndex;
        public void SyncTabs() => Syncs++;
        public void ShowTabAgentState(int tabIndex, AgentState state)
        {
            while (TabStates.Count <= tabIndex) TabStates.Add(AgentState.None);
            TabStates[tabIndex] = state;
        }
        public void ClearTabAgentState(int tabIndex) => ShowTabAgentState(tabIndex, AgentState.None);
        public void FlashTaskbar(bool untilFocused) => Flashes.Add(untilFocused);
        public void ToastInFocusedWindow(ToastKind kind, string title, string? detail) => FocusedToasts.Add(title);
        public void ToastWithAction(ToastKind kind, string title, string? detail, string actionLabel, Action action) { ActionToasts.Add(title); LastAction = action; }
        public void CloseMenu(long now) { }
        public void CloseOverlaysForAgents(long now) { }
        public void OpenPalette(List<Command> commands, CommandPalette.Options options, long now) { Palette = commands; PaletteOptions = options; }
        public void SendInput(FakePane pane, string text) => Sent.Add((pane, text));
        public void SendKey(uint key, ushort mod) { }
        public void BroadcastKey(uint key, ushort mod) { }
    }

    private sealed class FakeProcess : IAgentProcess<FakePane>
    {
        public Settings Settings { get; set; } = new();
        public Dictionary<KeyChord, ActionSpec> Keys { get; } = [];
        public NotificationCenter Notifications { get; } = new(new FakeDesktop());
        public bool AnyWindowFocused => true;
        public int WindowCount => 1;
        public readonly List<FakePane> Jumped = [];
        public Func<List<AgentRow<FakePane>>>? Rows;
        public Func<List<AgentPaneRef<FakePane>>>? Panes;
        public void DrainAgentFocusRequests() { }
        public List<AgentRow<FakePane>> AgentRows() => Rows?.Invoke() ?? [];
        public void JumpToAgentPane(FakePane pane) => Jumped.Add(pane);
        public List<AgentPaneRef<FakePane>> CollectAgentPanes() => Panes?.Invoke() ?? [];
    }

    private readonly FakePane _pane = new(1);
    private readonly FakeWindow _window;
    private readonly FakeProcess _process = new();
    private readonly WindowAgents<FakePane> _sut;

    public WindowAgentsTests()
    {
        _window = new FakeWindow(_pane);
        _sut = new WindowAgents<FakePane>(_window, _process, new Lock());
    }

    private static void Feed(FakePane pane, string text) => pane.Term.Feed(Encoding.UTF8.GetBytes(text));

    /// <summary>A pane that reports a status record, as a coding agent would (OSC 7501).</summary>
    private static void Report(FakePane pane, string state, string extra = "") =>
        Feed(pane, $"\x1b]7501;state={state}:app=claude-code{extra}\x1b\\");

    [Test]
    public async Task A_blocked_record_outranks_the_main_working_record()
    {
        // Arrange
        _pane.Foreground = AgentCatalog.ByStatusApp("claude-code");
        Report(_pane, "working");
        Report(_pane, "blocked", ":id=sub:kind=permission");

        // Act
        _sut.Tick(T0);

        // Assert
        await Assert.That(_pane.Agent.State).IsEqualTo(AgentState.Waiting);
        await Assert.That(_pane.Agent.Tracker.Kind).IsEqualTo("permission");
        await Assert.That(_window.TabStates[0]).IsEqualTo(AgentState.Waiting);
    }

    [Test]
    public async Task A_status_record_alone_identifies_the_agent()
    {
        // Arrange
        Report(_pane, "working");

        // Act
        _sut.Tick(T0);

        // Assert
        await Assert.That(_pane.Agent.Definition?.Id).IsEqualTo("claude-code");
        await Assert.That(_sut.AnyAgents).IsTrue();
    }

    [Test]
    public async Task A_tab_shows_its_most_urgent_pane()
    {
        // Arrange
        var other = new FakePane(2);
        var tree = new LayoutTree<FakePane>(_pane);
        tree.Split(_pane, other, SplitAxis.Horizontal);
        _window.Tabs[0] = tree;
        Report(_pane, "working");
        Report(other, "blocked", ":kind=question");

        // Act
        _sut.Tick(T0);

        // Assert
        await Assert.That(_window.TabStates[0]).IsEqualTo(AgentState.Waiting);
    }

    [Test]
    public async Task Waiting_out_of_sight_posts_a_notification_and_flashes_the_taskbar()
    {
        // Arrange
        _window.IsFocused = false;
        _pane.Foreground = AgentCatalog.ByStatusApp("claude-code");
        Report(_pane, "blocked", ":kind=permission");

        // Act
        _sut.Tick(T0);

        // Assert
        await Assert.That(_process.Notifications.Items.Count).IsEqualTo(1);
        await Assert.That(_process.Notifications.Items[0].Title).Contains("permission");
        await Assert.That(_window.Flashes).IsEquivalentTo(new[] { true });
    }

    [Test]
    public async Task Waiting_in_the_focused_pane_stays_quiet()
    {
        // Arrange
        _pane.Foreground = AgentCatalog.ByStatusApp("claude-code");
        Report(_pane, "blocked", ":kind=permission");

        // Act
        _sut.Tick(T0);

        // Assert
        await Assert.That(_process.Notifications.Items.Count).IsEqualTo(0);
        await Assert.That(_window.Flashes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_agents_terminal_notification_means_waiting_with_its_text()
    {
        // Arrange
        _window.IsFocused = false;
        _pane.Foreground = AgentCatalog.ByStatusApp("claude-code");
        _sut.Tick(T0);
        Feed(_pane, "\x1b]777;notify;Claude;Needs your input\x1b\\");

        // Act
        _sut.Tick(T0 + 500);

        // Assert
        await Assert.That(_pane.Agent.State).IsEqualTo(AgentState.Waiting);
        await Assert.That(_pane.Agent.Tracker.Message).IsEqualTo("Needs your input");
    }

    [Test]
    public async Task A_programs_notification_goes_out_as_itself_when_no_agent_runs()
    {
        // Arrange
        _window.IsFocused = false;
        _pane.ForegroundName = "make";
        _sut.Tick(T0);
        Feed(_pane, "\x1b]777;notify;Build;finished\x1b\\");

        // Act
        _sut.Tick(T0 + 500);

        // Assert
        await Assert.That(_process.Notifications.Items.Count).IsEqualTo(1);
        await Assert.That(_process.Notifications.Items[0].App).IsEqualTo("make");
    }

    [Test]
    public async Task An_agent_leaving_the_foreground_forgets_its_state()
    {
        // Arrange
        _pane.Foreground = AgentCatalog.ByStatusApp("claude-code");
        Report(_pane, "working");
        _sut.Tick(T0);
        _pane.Foreground = null;
        _pane.ProcessGroup = 43;
        _pane.Term.ClearProgramStatus(transientOnly: false);

        // Act
        _sut.Tick(T0 + 500);

        // Assert
        await Assert.That(_pane.Agent.Definition).IsNull();
        await Assert.That(_pane.Agent.State).IsEqualTo(AgentState.None);
    }

    [Test]
    public async Task Turning_agent_status_off_clears_everything_once()
    {
        // Arrange
        Report(_pane, "working");
        _sut.Tick(T0);
        _process.Settings = _process.Settings with { AgentStatus = false };

        // Act
        _sut.Tick(T0 + 500);

        // Assert
        await Assert.That(_pane.Agent.Definition).IsNull();
        await Assert.That(_pane.Agent.State).IsEqualTo(AgentState.None);
        await Assert.That(_pane.Term.ProgramStatusEnabled).IsFalse();
        await Assert.That(_window.TabStates[0]).IsEqualTo(AgentState.None);
        await Assert.That(_sut.AnyAgents).IsFalse();
    }

    [Test]
    public async Task The_tab_label_names_the_agent_and_its_task()
    {
        // Arrange
        _pane.Agent.Definition = AgentCatalog.ByStatusApp("claude-code");
        Feed(_pane, "\x1b]0;✳ Fix the parser\x1b\\");

        // Act
        string label = _sut.TabLabel(_pane, "claude");

        // Assert
        await Assert.That(label).IsEqualTo("Claude: Fix the parser");
    }

    [Test]
    public async Task The_tab_label_is_left_alone_without_an_agent()
    {
        // Act
        string label = _sut.TabLabel(_pane, "vim");

        // Assert
        await Assert.That(label).IsEqualTo("vim");
    }

    [Test]
    public async Task Hook_status_reports_a_pane_and_rejects_nonsense()
    {
        // Act
        var ok = _sut.IpcSetStatus(_pane, new IpcRequest { State = "waiting", Message = "Approve?", Kind = "bogus" });

        // Assert
        await Assert.That(ok.Ok).IsTrue();
        _sut.Tick(T0);
        await Assert.That(_pane.Agent.Tracker.Message).IsEqualTo("Approve?");
        await Assert.That(_pane.Agent.Tracker.Kind).IsNull(); // unknown kinds are dropped
        await Assert.That(() => _sut.IpcSetStatus(_pane, new IpcRequest { State = "nonsense" })).Throws<IpcException>();
    }

    [Test]
    public async Task Usage_reports_are_cleaned_and_stored()
    {
        // Arrange
        var usage = new UsageInfo { Model = "opus\u001b[0m", CostUsd = 1.5, ContextPercent = 40, Cwd = "/nonexistent-dir" };

        // Act
        _sut.IpcReportUsage(_pane, new IpcRequest { Usage = usage });

        // Assert
        await Assert.That(_pane.Agent.Usage!.Model).IsEqualTo("opus [0m");
        await Assert.That(_pane.Agent.UsageCwd).IsNull();
    }

    [Test]
    public async Task List_fields_say_unknown_only_when_a_state_arrived_without_an_agent()
    {
        // Arrange
        _pane.Agent.Tracker.Report(new AgentReport(AgentState.Working, AgentSource.Hook, T0));
        _pane.Agent.Tracker.Update(new AgentObservation(false, default, AgentState.None, 0, 0, true), T0);
        var plain = new FakePane(2);
        var idle = new FakePane(3);
        idle.Agent.Definition = AgentCatalog.ByStatusApp("claude-code");

        // Act
        var working = WindowAgents<FakePane>.PaneFields(_pane);
        var nothing = WindowAgents<FakePane>.PaneFields(plain);
        var quiet = WindowAgents<FakePane>.PaneFields(idle);

        // Assert
        await Assert.That(working).IsEqualTo(("unknown", (string?)"working"));
        await Assert.That(nothing).IsEqualTo(((string?)null, (string?)null));
        await Assert.That(quiet).IsEqualTo(("claude-code", (string?)"idle"));
    }

    [Test]
    public async Task The_overview_rows_describe_each_agent()
    {
        // Arrange
        _pane.Agent.Definition = AgentCatalog.ByStatusApp("claude-code");
        Report(_pane, "blocked", ":kind=permission");
        _pane.KnownDirectory = "/tmp/work";
        _sut.Tick(T0);
        var rows = new List<AgentRow<FakePane>>();

        // Act
        _sut.CollectRows(rows, manyWindows: false, T0 + 2000);

        // Assert
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Title).Contains("permission");
        await Assert.That(rows[0].Where).IsEqualTo("Tab 1 · /tmp/work");
        await Assert.That(rows[0].TabId).IsEqualTo(100);
        await Assert.That(rows[0].Focused).IsTrue();
        await Assert.That(rows[0].WindowId).IsEqualTo(7u);
    }

    [Test]
    public async Task A_row_becomes_a_cli_agents_entry()
    {
        // Arrange
        _pane.Agent.Definition = AgentCatalog.ByStatusApp("claude-code");
        Report(_pane, "working");
        _sut.Tick(T0);
        var rows = new List<AgentRow<FakePane>>();
        _sut.CollectRows(rows, manyWindows: false, T0);

        // Act
        var info = WindowAgents<FakePane>.ToAgentInfo(rows[0], T0 + 3000);

        // Assert
        await Assert.That(info.WindowId).IsEqualTo(7);
        await Assert.That(info.TabId).IsEqualTo(100);
        await Assert.That(info.State).IsEqualTo("working");
        await Assert.That(info.SecondsInState).IsEqualTo(3);
    }

    [Test]
    public async Task The_home_directory_shortens_to_a_tilde()
    {
        // Arrange
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var inside = new FakePane(2) { KnownDirectory = home + "/src" };
        var unknown = new FakePane(3) { Label = "zsh" };

        // Act / Assert
        await Assert.That(WindowAgents<FakePane>.PaneDirName(inside)).IsEqualTo("~/src");
        await Assert.That(WindowAgents<FakePane>.PaneDirName(unknown)).IsEqualTo("zsh");
    }

    [Test]
    public async Task Jump_activates_the_tab_and_focuses_the_pane()
    {
        // Arrange
        var other = new FakePane(2);
        _window.Tabs.Add(new LayoutTree<FakePane>(other));

        // Act
        bool found = _sut.JumpToPane(other);

        // Assert
        await Assert.That(found).IsTrue();
        await Assert.That(_window.ActiveTabIndex).IsEqualTo(1);
        await Assert.That(_window.Focused).IsEqualTo(other);
    }

    [Test]
    public async Task Jump_leaves_a_zoom_on_another_pane_and_returns_false_for_strangers()
    {
        // Arrange
        var other = new FakePane(2);
        var tree = new LayoutTree<FakePane>(_pane);
        tree.Split(_pane, other, SplitAxis.Horizontal);
        _window.Tabs[0] = tree;
        _window.Zoomed = other;

        // Act
        bool here = _sut.JumpToPane(_pane);
        bool stranger = _sut.JumpToPane(new FakePane(9));

        // Assert
        await Assert.That(here).IsTrue();
        await Assert.That(_window.Zoomed).IsNull();
        await Assert.That(stranger).IsFalse();
    }

    [Test]
    public async Task The_overview_keys_close_and_swallow_text_only_while_open()
    {
        // Arrange
        bool closedKey = _sut.OverlayKey('j', 0, T0);
        _sut.OpenOverview();

        // Act
        bool openKey = _sut.OverlayKey(Sdl.K_ESCAPE, 0, T0);

        // Assert
        await Assert.That(closedKey).IsFalse();
        await Assert.That(openKey).IsTrue();
        await Assert.That(_window.SuppressText).IsTrue();
        await Assert.That(_sut.OverlayOpen).IsFalse();
    }

    [Test]
    public async Task The_overview_shortcut_also_closes_it()
    {
        // Arrange
        var chord = KeyNames.Chord('a', (ushort)(Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT))!.Value;
        ActionSpec.TryParse("AgentsOverview", out var action, out _);
        _process.Keys[chord] = action!;
        _sut.OpenOverview();

        // Act
        _sut.OverlayKey('a', (ushort)(Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT), T0);

        // Assert
        await Assert.That(_sut.OverlayOpen).IsFalse();
    }

    [Test]
    public async Task An_exited_agent_is_not_offered()
    {
        // Arrange
        var target = new FakePane(2) { Exited = true };
        _process.Panes = () => [new AgentPaneRef<FakePane>(target, 0, new AgentCandidate(2, "Claude", true, 7, 100))];
        _pane.Selection = SelectAll(_pane, "fix this");

        // Act
        _sut.OpenSend();

        // Assert
        await Assert.That(_window.Toasts).Contains("No coding agent to send to");
        await Assert.That(_window.Sent.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_selection_is_pasted_and_jumps_there()
    {
        // Arrange
        var target = new FakePane(2);
        _process.Panes = () => [new AgentPaneRef<FakePane>(target, 0, new AgentCandidate(2, "Claude", false, 7, 100))];
        _pane.Selection = SelectAll(_pane, "fix this");
        _sut.OpenSend();

        // Act
        _window.Palette![0].RunWith!.Invoke(RunModifiers.None);

        // Assert
        await Assert.That(_window.Sent[0].Pane).IsEqualTo(target);
        await Assert.That(_window.Sent[0].Text).Contains("fix this");
        await Assert.That(_process.Jumped).IsEquivalentTo(new[] { target });
        await Assert.That(_window.ActionToasts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Ctrl_enter_submits_and_offers_a_jump_instead_of_moving()
    {
        // Arrange
        var target = new FakePane(2);
        _process.Panes = () => [new AgentPaneRef<FakePane>(target, 0, new AgentCandidate(2, "Claude", false, 7, 100))];
        _pane.Selection = SelectAll(_pane, "fix this");
        _sut.OpenSend();

        // Act
        _window.Palette![0].RunWith!.Invoke(RunModifiers.Ctrl);

        // Assert
        await Assert.That(_process.Jumped.Count).IsEqualTo(0);
        await Assert.That(_window.ActionToasts).Contains("Sent to Claude");
        _window.LastAction!();
        await Assert.That(_process.Jumped).IsEquivalentTo(new[] { target });
    }

    [Test]
    public async Task Without_a_selection_the_palette_becomes_a_prompt()
    {
        // Arrange
        var target = new FakePane(2);
        _process.Panes = () => [new AgentPaneRef<FakePane>(target, 0, new AgentCandidate(2, "Claude", false, 7, 100))];
        _sut.OpenSend();

        // Act
        _window.Palette![0].RunWith!.Invoke(RunModifiers.None);
        var prompt = _window.PaletteOptions!.Prompt!;
        prompt("hello", RunModifiers.None);

        // Assert
        await Assert.That(_window.PaletteOptions!.Placeholder).IsEqualTo("Message for Claude…");
        await Assert.That(_window.Sent[0].Text).Contains("hello");
    }

    private static readonly string[] Dialog =
    [
        "────────────────────────────────────────────────────────────────────────────────",
        " Bash command",
        "",
        "   rm -rf build",
        "",
        " Do you want to proceed?",
        " ❯ 1. Yes",
        "   2. Yes, and don't ask again for rm commands in /work",
        "   3. No, and tell Claude what to do differently (esc)",
        "",
        " Esc to cancel",
    ];

    private (FakePane Pane, WindowAgents<FakePane> Sut) AnswerSetup(string[] screen)
    {
        var pane = new FakePane(2, rows: 14);
        var window = new FakeWindow(pane);
        var sut = new WindowAgents<FakePane>(window, _process, new Lock());
        _answerWindow = window;
        pane.Foreground = AgentCatalog.Claude;
        Feed(pane, string.Join("\r\n", screen));
        Report(pane, "blocked", ":kind=permission");
        _process.Rows = () =>
        {
            var rows = new List<AgentRow<FakePane>>();
            sut.CollectRows(rows, manyWindows: false, T0);
            return rows;
        };
        sut.Tick(T0);
        sut.OpenOverview();
        return (pane, sut);
    }

    private FakeWindow _answerWindow = null!;

    [Test]
    public async Task Yes_sends_the_digit_of_the_one_time_option_and_nothing_else()
    {
        // Arrange
        var (pane, sut) = AnswerSetup(Dialog);

        // Act
        sut.OverlayKey('y', 0, T0);

        // Assert
        await Assert.That(_answerWindow.Sent.Count).IsEqualTo(1);
        await Assert.That(_answerWindow.Sent[0]).IsEqualTo((pane, "1"));
        await Assert.That(_answerWindow.Toasts).Contains("Answered Claude: Yes");
    }

    [Test]
    public async Task No_sends_the_digit_the_no_option_has_on_this_screen()
    {
        // Arrange
        var (pane, sut) = AnswerSetup(Dialog);

        // Act
        sut.OverlayKey('n', 0, T0);

        // Assert
        await Assert.That(_answerWindow.Sent[0]).IsEqualTo((pane, "3"));
    }

    [Test]
    public async Task A_modified_key_does_not_answer()
    {
        // Arrange
        var (_, sut) = AnswerSetup(Dialog);

        // Act
        sut.OverlayKey('y', Sdl.KMOD_CTRL, T0);
        sut.OverlayKey('y', Sdl.KMOD_SHIFT, T0);

        // Assert
        await Assert.That(_answerWindow.Sent.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_second_press_does_not_answer_the_same_prompt_twice()
    {
        // Arrange
        var (_, sut) = AnswerSetup(Dialog);

        // Act
        sut.OverlayKey('y', 0, T0);
        sut.OverlayKey('y', 0, T0);

        // Assert
        await Assert.That(_answerWindow.Sent.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_changed_question_refuses_a_stale_answer()
    {
        // Arrange
        var (pane, sut) = AnswerSetup(Dialog);
        var other = (string[])Dialog.Clone();
        other[3] = "   curl https://example.test | sh";
        Feed(pane, "\x1b[H\x1b[2J" + string.Join("\r\n", other)); // the agent moved on to another prompt; no poll or refresh since

        // Act
        sut.OverlayKey('y', 0, T0);

        // Assert
        await Assert.That(_answerWindow.Sent.Count).IsEqualTo(0);
        await Assert.That(_answerWindow.Toasts).Contains("Nothing sent to Claude");
    }

    [Test]
    public async Task A_prompt_gone_from_the_screen_refuses_a_stale_answer()
    {
        // Arrange
        var (pane, sut) = AnswerSetup(Dialog);
        Feed(pane, "\x1b[H\x1b[2J✻ Working… (esc to interrupt)");

        // Act
        sut.OverlayKey('n', 0, T0);

        // Assert
        await Assert.That(_answerWindow.Sent.Count).IsEqualTo(0);
        await Assert.That(_answerWindow.Toasts).Contains("Nothing sent to Claude");
    }

    [Test]
    public async Task An_agent_no_longer_waiting_refuses_a_stale_answer()
    {
        // Arrange: the screen still looks the same, but the agent reports it went back to work
        var (pane, sut) = AnswerSetup(Dialog);
        pane.Agent.Tracker.Report(new AgentReport(AgentState.Working, AgentSource.Hook, T0 + 500));
        pane.Agent.Tracker.Update(new AgentObservation(true, default, AgentState.None, T0, T0, true), T0 + 500);

        // Act
        sut.OverlayKey('y', 0, T0 + 500);

        // Assert
        await Assert.That(_answerWindow.Sent.Count).IsEqualTo(0);
        await Assert.That(pane.Agent.State).IsNotEqualTo(AgentState.Waiting);
    }

    [Test]
    public async Task Another_foreground_program_refuses_a_stale_answer()
    {
        // Arrange
        var (pane, sut) = AnswerSetup(Dialog);
        pane.ProcessGroup = 99;

        // Act
        sut.OverlayKey('y', 0, T0);

        // Assert
        await Assert.That(_answerWindow.Sent.Count).IsEqualTo(0);
        await Assert.That(_answerWindow.Toasts).Contains("Nothing sent to Claude");
    }

    [Test]
    public async Task An_unreadable_prompt_offers_nothing_to_press()
    {
        // Arrange: waiting, but the dialog has no top border on screen
        var (_, sut) = AnswerSetup(Dialog[1..]);

        // Act
        sut.OverlayKey('y', 0, T0);

        // Assert
        await Assert.That(_answerWindow.Sent.Count).IsEqualTo(0);
        await Assert.That(_answerWindow.Toasts.Count).IsEqualTo(0);
    }

    private static Selection SelectAll(FakePane pane, string text)
    {
        Feed(pane, text);
        return new Selection { AnchorLine = pane.Term.ViewTopLine, AnchorCol = 0, HeadLine = pane.Term.ViewTopLine, HeadCol = text.Length, Mode = SelectionMode.Cell };
    }
}
