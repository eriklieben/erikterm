using ErikTerm.App.Ipc;
using ErikTerm.App.Panes;
using ErikTerm.App.Teammates;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

/// <summary>Agent-team teammates without a window: parking and opening panes, the tmux requests that make them, and the pills' hit test.</summary>
public class WindowTeammatesTests
{
    private sealed class FakePane(int id) : ITeammatePane<FakePane>
    {
        private RectTween _rect;
        private Tween _focus = new(1), _appear = new(1);
        public int Id { get; } = id;
        public Terminal Term { get; } = new(10, 4, 10);
        public string Label => "pane";
        public bool LayoutHidden => Teammate is { Parked: true };
        public Teammate? Teammate { get; set; }
        public List<FakePane> Teammates { get; } = [];
        public int StripPx { get; set; }
        public AgentPane Agent { get; } = new();
        public RectI Target { get; set; }
        public ref RectTween Rect => ref _rect;
        public ref Tween Focus => ref _focus;
        public ref Tween Appear => ref _appear;
        public bool CreatedByTmux { get; set; }
        public string? TmuxTitle { get; set; }
        public string? StartDirectory(string? defaultCwd) => "/start";
    }

    private sealed class FakeWindow(FakePane first) : ITeammateWindow<FakePane>
    {
        public FakePane Focused { get; set; } = first;
        public FakePane? Zoomed { get; set; }
        public List<LayoutTree<FakePane>> Tabs { get; } = [new LayoutTree<FakePane>(first)];
        public Dictionary<int, FakePane> FocusedByTab { get; } = [];
        public LayoutTree<FakePane> Layout => Tabs[ActiveTabIndex];
        public int ShownPaneCount => Layout.LeafList.Count(p => !p.LayoutHidden);
        public int TabCount => Tabs.Count;
        public int ActiveTabIndex { get; set; }
        public Theme Theme { get; } = new();
        public bool SuppressText { get; set; }
        public Settings Settings { get; set; } = new();
        public ColorScheme Scheme { get; set; } = ColorSchemes.Erik;
        public float Anim => 0;
        public int LayoutGap => 2;

        public readonly List<string> Toasts = [];
        public readonly List<(string[]? Argv, string? Cwd, RectI Area)> Spawned = [];
        public int NextPaneId = 50, Layouts, BackgroundResizes, Pointers, Defaults;
        public readonly List<FakePane> FocusCalls = [];
        public readonly List<(FakePane Pane, bool Background)> Resized = [];
        public bool ResizeReport = true;

        public LayoutTree<FakePane> TabLayout(int tabIndex) => Tabs[tabIndex];
        public List<FakePane> VisiblePanes() => Layout.LeafList.Where(p => !p.LayoutHidden).ToList();
        public FakePane? FindPane(int id) => Tabs.SelectMany(t => t.LeafList).FirstOrDefault(p => p.Id == id);
        public void Focus(FakePane pane) { Focused = pane; FocusCalls.Add(pane); }
        public void SetZoom(FakePane? pane) => Zoomed = pane;
        public void ApplyLayout(bool animate) => Layouts++;
        public void ActivateTab(int index) => ActiveTabIndex = index;
        public void ClearTabZoom(int tabIndex) { }
        public void Redraw() { }
        public void Toast(ToastKind kind, string title, string? detail = null) => Toasts.Add(title);
        public FakePane TabFocused(int tabIndex) => FocusedByTab[tabIndex];
        public void SetTabFocused(int tabIndex, FakePane pane) => FocusedByTab[tabIndex] = pane;
        public FakePane SpawnTeammate(string[]? argv, string? cwd, RectI area) { Spawned.Add((argv, cwd, area)); return new FakePane(NextPaneId++); }
        public void ResizeBackgroundTabs() => BackgroundResizes++;
        public bool SizeTerminal(FakePane pane, RectI r, int strip) => ResizeReport;
        public void ParkedResized(FakePane pane, bool background) => Resized.Add((pane, background));
        public void DropZoom(long now) => Zoomed = null;
        public void PointerCursor() => Pointers++;
        public void DefaultCursor() => Defaults++;
        public string[] ShortcutFor(ActionSpec action) => [];
    }

    private readonly FakePane _lead = new(1);
    private readonly FakeWindow _window;
    private readonly WindowTeammates<FakePane> _sut;

    public WindowTeammatesTests()
    {
        _lead.Target = new RectI(0, 0, 800, 600);
        _window = new FakeWindow(_lead);
        _sut = new WindowTeammates<FakePane>(_window);
    }

    private IpcResponse? Split(string[]? argv = null, bool teammate = true, bool tmux = true, int? caller = 1, int tab = 0, FakePane? target = null) =>
        _sut.AddSplit(new IpcRequest { Tmux = tmux, Teammate = teammate, Argv = argv, CallerPaneId = caller }, tab, target ?? _lead, SplitAxis.Horizontal, false, 0.5);

    private FakePane ParkedTeammate(int id = 2, bool confirmed = true)
    {
        var pane = new FakePane(id) { Target = new RectI(400, 0, 400, 600), Teammate = new Teammate { LeadId = _lead.Id, Name = "t" + id, Confirmed = confirmed } };
        _window.Layout.Split(_lead, pane, SplitAxis.Horizontal, false, 0.3);
        _sut.Sync(_window.Layout);
        return pane;
    }

    [Test]
    public async Task ATeammateSplitGoesInTheTreeParkedAndTheFocusStays()
    {
        // Act
        var response = Split(["cat"]);

        // Assert: the lead is the caller, the tree has it, nothing asked for the focus
        var pane = _window.Layout.LeafList[1];
        await Assert.That(response!.PaneId).IsEqualTo(pane.Id);
        await Assert.That(pane.Teammate!.LeadId).IsEqualTo(_lead.Id);
        await Assert.That(pane.Teammate.Parked).IsTrue();
        await Assert.That(pane.CreatedByTmux).IsTrue();
        await Assert.That(_window.Focused).IsEqualTo(_lead);
        await Assert.That(_window.FocusCalls.Count).IsEqualTo(0);
        await Assert.That(_window.Layouts).IsEqualTo(1);
    }

    [Test]
    public async Task ASplitInAnotherTabResizesThatTabInsteadOfLayingOut()
    {
        // Arrange
        var other = new FakePane(9) { Target = new RectI(0, 0, 400, 300) };
        _window.Tabs.Add(new LayoutTree<FakePane>(other));

        // Act
        Split(["cat"], caller: 9, tab: 1, target: other);

        // Assert
        await Assert.That(_window.Layouts).IsEqualTo(0);
        await Assert.That(_window.BackgroundResizes).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task AnOrdinarySplitIsLeftToTheCaller(bool tmux, bool teammate)
    {
        // Act
        var response = Split(tmux: tmux, teammate: teammate);

        // Assert
        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task WithPillsOffEverySplitIsOrdinary()
    {
        // Arrange
        _window.Settings = new Settings { TmuxAgentPanes = TmuxAgentPanes.Split };

        // Act
        var response = Split();

        // Assert
        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task RespawnNamesAPlaceholderTeammate()
    {
        // Arrange
        Split(["cat"]);
        var pane = _window.Layout.LeafList[1];

        // Act
        _sut.OnRespawn(new IpcRequest(), 0, pane, ["/usr/bin/agent", "--agent-name", "alpha", "--team-name", "red", "--agent-color", "blue"]);

        // Assert
        await Assert.That(pane.Teammate!.Confirmed).IsTrue();
        await Assert.That(pane.Teammate.Name).IsEqualTo("alpha");
        await Assert.That(pane.Teammate.Team).IsEqualTo("red");
        await Assert.That(pane.Teammate.Color).IsEqualTo("blue");
    }

    [Test]
    public async Task RespawnOfAnUnconfirmedPlaceholderWithAnotherProgramMakesItAnOrdinaryPane()
    {
        // Arrange
        Split(["cat"]);
        var pane = _window.Layout.LeafList[1];
        int layouts = _window.Layouts;

        // Act
        _sut.OnRespawn(new IpcRequest(), 0, pane, ["htop"]);

        // Assert
        await Assert.That(pane.Teammate).IsNull();
        await Assert.That(_window.Layouts).IsEqualTo(layouts + 1);
    }

    [Test]
    public async Task RespawnParksAnUnflaggedPaneAndMovesTheFocusToAnotherPane()
    {
        // Arrange
        var pane = new FakePane(2) { CreatedByTmux = true };
        _window.Layout.Split(_lead, pane, SplitAxis.Horizontal, false, 0.5);
        _window.Focused = pane;
        _window.Zoomed = pane;

        // Act
        _sut.OnRespawn(new IpcRequest { Teammate = true, CallerPaneId = 1 }, 0, pane, ["/usr/bin/agent", "--agent-name", "alpha"]);

        // Assert
        await Assert.That(pane.Teammate!.Parked).IsTrue();
        await Assert.That(_window.Focused).IsEqualTo(_lead);
        await Assert.That(_window.Zoomed).IsNull();
    }

    [Test]
    public async Task RespawnWithoutALeadToParkUnderLeavesThePaneAlone()
    {
        // Arrange
        var pane = new FakePane(2) { CreatedByTmux = true };
        _window.Layout.Split(_lead, pane, SplitAxis.Horizontal, false, 0.5);

        // Act: the caller is the pane itself
        _sut.OnRespawn(new IpcRequest { Teammate = true, CallerPaneId = 2 }, 0, pane, ["/usr/bin/agent", "--agent-name", "alpha"]);

        // Assert
        await Assert.That(pane.Teammate).IsNull();
    }

    [Test]
    public async Task RespawnInABackgroundTabMovesThatTabsFocus()
    {
        // Arrange
        var bgLead = new FakePane(8);
        var pane = new FakePane(9) { CreatedByTmux = true };
        var bg = new LayoutTree<FakePane>(bgLead);
        bg.Split(bgLead, pane, SplitAxis.Horizontal, false, 0.5);
        _window.Tabs.Add(bg);
        _window.FocusedByTab[1] = pane;

        // Act
        _sut.OnRespawn(new IpcRequest { Teammate = true, CallerPaneId = 8 }, 1, pane, ["/usr/bin/agent", "--agent-name", "alpha"]);

        // Assert
        await Assert.That(_window.FocusedByTab[1]).IsEqualTo(bgLead);
        await Assert.That(_window.BackgroundResizes).IsEqualTo(1);
    }

    [Test]
    public async Task PaneTitleNamesAnUnconfirmedTeammateOnly()
    {
        // Arrange
        var confirmed = new FakePane(2) { Teammate = new Teammate { Name = "named", Confirmed = true }, TmuxTitle = "ignored" };
        var unconfirmed = new FakePane(3) { Teammate = new Teammate { Name = "teammate" }, TmuxTitle = new string('x', 50) };

        // Act
        _sut.OnPaneTitle(confirmed);
        _sut.OnPaneTitle(unconfirmed);

        // Assert: capped at 40
        await Assert.That(confirmed.Teammate!.Name).IsEqualTo("named");
        await Assert.That(unconfirmed.Teammate!.Name).IsEqualTo(new string('x', 40));
    }

    [Test]
    public async Task SetPaneColorKeepsAColorForATeammateOnly()
    {
        // Arrange
        var teammate = new FakePane(2) { Teammate = new Teammate() };

        // Act
        _sut.IpcSetColor(teammate, new IpcRequest { Color = "red" });
        _sut.IpcSetColor(_lead, new IpcRequest { Color = "red" });

        // Assert
        await Assert.That(teammate.Teammate!.Color).IsEqualTo("red");
        await Assert.That(_lead.Teammate).IsNull();
    }

    [Test]
    public async Task OpeningGivesAFairHalfAndFocusesIt()
    {
        // Arrange: the lead was left with 30%
        var pane = ParkedTeammate();
        _window.Zoomed = _lead;

        // Act
        _sut.Open(pane);

        // Assert
        var split = _window.Layout.CommonAncestor([_lead, pane])!;
        await Assert.That(split.Ratio).IsEqualTo(0.5);
        await Assert.That(pane.Teammate!.Parked).IsFalse();
        await Assert.That(_window.Focused).IsEqualTo(pane);
        await Assert.That(_window.Zoomed).IsNull();
    }

    [Test]
    public async Task OpeningOnlyTheNarrowSideMovesTheDivider()
    {
        // Arrange: the lead already has more than half
        var pane = new FakePane(2) { Teammate = new Teammate { LeadId = _lead.Id }, Target = new RectI(0, 0, 10, 10) };
        _window.Layout.Split(_lead, pane, SplitAxis.Horizontal, false, 0.8);
        _sut.Sync(_window.Layout);

        // Act
        _sut.Open(pane);

        // Assert
        await Assert.That(_window.Layout.CommonAncestor([_lead, pane])!.Ratio).IsEqualTo(0.8);
    }

    [Test]
    public async Task ParkingGivesTheFocusBackToTheLead()
    {
        // Arrange
        var pane = ParkedTeammate();
        _sut.Open(pane);
        _window.Zoomed = pane;

        // Act
        _sut.RunToggleAction("");

        // Assert
        await Assert.That(pane.Teammate!.Parked).IsTrue();
        await Assert.That(_window.Focused).IsEqualTo(_lead);
        await Assert.That(_window.Zoomed).IsNull();
    }

    [Test]
    public async Task ToggleActionWithANumberPicksThatTeammate()
    {
        // Arrange
        ParkedTeammate(2);
        ParkedTeammate(3);
        var (first, second) = (_lead.Teammates[0], _lead.Teammates[1]); // layout order

        // Act
        _sut.RunToggleAction("2");

        // Assert: only the second one opened
        await Assert.That(first.LayoutHidden).IsTrue();
        await Assert.That(second.LayoutHidden).IsFalse();
    }

    [Test]
    public async Task ToggleActionWithoutTeammatesSaysSo()
    {
        // Act
        _sut.RunToggleAction("1");

        // Assert
        await Assert.That(_window.Toasts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TheFocusedTeammatesLeadIsTheCurrentLead()
    {
        // Arrange
        var pane = ParkedTeammate();
        _window.Focused = pane;

        // Act
        var lead = _sut.CurrentLead();

        // Assert
        await Assert.That(lead).IsEqualTo(_lead);
    }

    [Test]
    public async Task SelectLayoutArrangesTheTeamNotTheTab()
    {
        // Arrange: a bystander pane the agent's rearranging must not touch
        var bystander = new FakePane(7);
        _window.Layout.Split(_lead, bystander, SplitAxis.Vertical, false, 0.5);
        var teammate = new FakePane(2) { Teammate = new Teammate { LeadId = _lead.Id } };
        _window.Layout.Split(_lead, teammate, SplitAxis.Horizontal, false, 0.5);
        _sut.Sync(_window.Layout);

        // Act
        var node = _sut.TeamSubtree(_window.Layout);

        // Assert: the subtree of lead and teammate, without the bystander
        await Assert.That(node).IsNotNull();
        await Assert.That(ReferenceEquals(node, _window.Layout.Root)).IsFalse();
        await Assert.That(_window.Layout.CommonAncestor([_lead, teammate])).IsEqualTo(node);
    }

    [Test]
    public async Task SelectLayoutAppliesToTheWholeTabWhenThereIsNoTeam()
    {
        // Act
        var subtree = _sut.TeamSubtree(_window.Layout);

        // Assert
        await Assert.That(subtree).IsNull();
    }

    [Test]
    public async Task ParkedPanesAreSizedWhereTheyWouldBeAndReportedInTheBackground()
    {
        // Arrange
        var pane = ParkedTeammate();
        _lead.Target = new RectI(0, 0, 800, 600);

        // Act
        _sut.SizeParked(_window.Layout, new RectI(0, 0, 800, 600), background: true);

        // Assert: it got a place at the right of the split and the window heard it resized
        await Assert.That(pane.Target.W).IsGreaterThan(0);
        await Assert.That(pane.Target.X).IsGreaterThan(0);
        await Assert.That(_window.Resized[0]).IsEqualTo((pane, true));
    }

    [Test]
    public async Task ATinyPaneKeepsItsRows()
    {
        // Arrange
        ParkedTeammate();

        // Act
        int roomy = _sut.StripHeightFor(_lead, new RectI(0, 0, 800, 600));
        int tiny = _sut.StripHeightFor(_lead, new RectI(0, 0, 800, 40));

        // Assert
        await Assert.That(roomy).IsGreaterThan(0);
        await Assert.That(tiny).IsEqualTo(0);
    }

    [Test]
    public async Task HoveringAPillShowsThePointerAndOffItTheDefault()
    {
        // Arrange: the lead's strip along its bottom holds one pill
        var pane = ParkedTeammate();
        _lead.StripPx = 30;
        pane.Teammate!.PillRect = new RectF(100, 575, 60, 20);

        // Act
        bool onPill = _sut.MouseMove(120, 585, 0);
        bool onStrip = _sut.MouseMove(10, 585, 1);
        bool off = _sut.MouseMove(10, 100, 2);

        // Assert
        await Assert.That(onPill).IsTrue();
        await Assert.That(onStrip).IsTrue();
        await Assert.That(off).IsFalse();
        await Assert.That(_window.Pointers).IsEqualTo(1);
        await Assert.That(_window.Defaults).IsEqualTo(1);
    }

    [Test]
    public async Task ClickingAPillOpensItAndTheStripSwallowsOtherClicks()
    {
        // Arrange
        var pane = ParkedTeammate();
        _lead.StripPx = 30;
        pane.Teammate!.PillRect = new RectF(100, 575, 60, 20);

        // Act
        bool onPill = _sut.MouseDown(1, 120, 585);
        bool opened = !pane.LayoutHidden;
        bool onStrip = _sut.MouseDown(1, 10, 585);
        bool elsewhere = _sut.MouseDown(1, 10, 100);

        // Assert
        await Assert.That(onPill).IsTrue();
        await Assert.That(opened).IsTrue();
        await Assert.That(onStrip).IsTrue();
        await Assert.That(elsewhere).IsFalse();
    }
}
