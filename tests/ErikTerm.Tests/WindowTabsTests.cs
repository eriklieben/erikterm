using ErikTerm.App;
using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Native;
using ErikTerm.App.Notifications;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

internal sealed class FakeTab(int id) : ITabView
{
    public int Id { get; } = id;
    public string Title { get; set; } = "tab " + id;
    public AgentState AgentState { get; set; }
    public bool Activity { get; set; }
    public bool Unread { get; set; }
    public ProgressReport Progress { get; set; }
    public bool InContainer { get; set; }
    public string? Color { get; set; }
    public uint? ProgramColor { get; set; }
    public TabColorCache ColorCache { get; } = new();
}

internal abstract class FakeTabWindow : ITabWindow<FakeTab>
{
    public TabList<FakeTab> Tabs { get; } = new();
    public bool TabBarShown { get; set; } = true;
    public Theme Theme { get; } = new();
    public bool SuppressText { get; set; }
    public readonly List<string> Toasts = [];
    public int Syncs, Redraws;
    public int? Activated;
    public void SyncTabs() => Syncs++;
    public void ActivateTab(int index) { Activated = index; Tabs.Activate(index); }
    public void Redraw() => Redraws++;
    public void Toast(ToastKind kind, string title, string? detail = null) => Toasts.Add(title);
}

/// <summary>The title a tab falls back on (Tabs/TabTitles.cs).</summary>
public class TabTitleTests
{
    [Test]
    [Arguments("vim", "ignored", null, "vim")]
    [Arguments("zsh", "my title", null, "my title")]
    [Arguments("zsh", "erikterm", null, "zsh")]
    [Arguments("zsh", "", null, "zsh")]
    [Arguments("zsh", "title", "/home/me", "~")]
    [Arguments("zsh", "title", "/home/me/src/app", "app")]
    [Arguments("fish", "", "/home/me/src/app/", "app")]
    [Arguments("bash", "", "/", "/")]
    public async Task ShellsShowWhereTheyAreOtherProgramsTheirName(string label, string title, string? cwd, string expected)
    {
        // Act
        string shown = TabTitles.Auto(label, title, cwd, "/home/me");

        // Assert
        await Assert.That(shown).IsEqualTo(expected);
    }
}

/// <summary>Tab colors without a window: setting them, what the palette offers, and the per-scheme cache the tab bar relies on.</summary>
public class WindowTabColorsTests
{
    private sealed class FakeWindow : FakeTabWindow, ITabColorWindow<FakeTab>
    {
        public ColorScheme Scheme { get; set; } = ColorSchemes.Erik;
        public string? PaletteQuery;
        public void OpenPalette(string query) => PaletteQuery = query;
    }

    private readonly FakeWindow _window = new();
    private readonly FakeTab _tab = new(1);
    private readonly WindowTabColors<FakeTab> _sut;

    public WindowTabColorsTests()
    {
        _window.Tabs.Add(_tab);
        _sut = new WindowTabColors<FakeTab>(_window);
    }

    [Test]
    public async Task ColorOfResolvesANamedColorAgainstTheScheme()
    {
        // Arrange
        _sut.Set(_tab, "red");

        // Act
        var color = _sut.ColorOf(_tab);

        // Assert
        await Assert.That(color).IsEqualTo(ColorSchemes.Erik.Ansi[1]);
    }

    [Test]
    public async Task AProgramColorShowsWithoutATabColor()
    {
        // Arrange
        _tab.ProgramColor = 0x112233;

        // Act
        var color = _sut.ColorOf(_tab);

        // Assert
        await Assert.That(color).IsEqualTo((uint?)0x112233);
    }

    [Test]
    public async Task ChangingTheSchemeFollowsANamedColor()
    {
        // Arrange
        _sut.Set(_tab, "red");
        var before = _sut.ColorOf(_tab);

        // Act
        _window.Scheme = ColorSchemes.TokyoNight;
        var after = _sut.ColorOf(_tab);

        // Assert
        await Assert.That(before).IsNotEqualTo(after);
        await Assert.That(after).IsEqualTo(ColorSchemes.TokyoNight.Ansi[1]);
    }

    [Test]
    public async Task AnUnknownColorTellsTheUserAndKeepsTheOldOne()
    {
        // Arrange
        _sut.Set(_tab, "red");
        _window.Toasts.Clear();

        // Act
        _sut.Set(_tab, "notacolor");

        // Assert
        await Assert.That(_tab.Color).IsEqualTo("red");
        await Assert.That(_window.Toasts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task WithTheBarHiddenItSaysThatTheColorDoesntShow()
    {
        // Arrange
        _window.TabBarShown = false;

        // Act
        _sut.Set(_tab, "blue");

        // Assert
        await Assert.That(_window.Toasts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ThePaletteMarksTheCurrentColor()
    {
        // Arrange
        _sut.Set(_tab, "red");
        var list = new List<Command>();

        // Act
        _sut.AddCommands(list);

        // Assert: Set Tab Color…, the offered colors, and Reset
        await Assert.That(list.Count).IsEqualTo(TabColors.Offered.Length + 2);
        await Assert.That(list.Single(c => c.Id == "tab-color:red").Title).EndsWith("(current)");
    }

    [Test]
    public async Task TheColorPickerOpensThePaletteOnTheColors()
    {
        // Act
        _sut.OpenPicker();

        // Assert
        await Assert.That(_window.PaletteQuery).IsEqualTo("Tab Color ");
    }

    [Test]
    public async Task IpcRejectsAnUnknownColor()
    {
        // Act and Assert
        await Assert.That(() => _sut.IpcSet(_tab, new IpcRequest { Color = "nope" })).Throws<IpcException>();
    }
}

/// <summary>The Ctrl+Tab list without a window: the double tap of Ctrl, holding and releasing, keys while it is open, and what counts as unread.</summary>
public class WindowTabSwitcherTests
{
    private const uint Ctrl = 0x400000E0;

    private sealed class FakeWindow : FakeTabWindow, ITabSwitcherWindow<FakeTab>
    {
        public bool OverlayOpen { get; set; }
        public bool AnimateProgress { get; set; }
        public int ClosedOverlays;
        public readonly List<int> RelativeSteps = [];
        public void CloseOverlaysForTabSwitcher(long now) => ClosedOverlays++;
        public void ActivateTabRelative(int delta) => RelativeSteps.Add(delta);
        public RepoBadgeModel? TabRepoBadge(FakeTab tab) => null;
        public uint? TabColorOf(FakeTab tab) => tab.ColorCache.Shown(tab.Color, tab.ProgramColor, ColorSchemes.Erik);
    }

    private sealed class FakeProcess : ITabSwitcherProcess
    {
        public Settings Settings { get; set; } = new();
        public Dictionary<KeyChord, ActionSpec> Keys { get; } = [];
        public NotificationCenter Notifications { get; } = new(new FakeDesktop());
        public int TabOf(int paneId) => paneId;
        public int LeadOf(int paneId) => 0;
    }

    private readonly FakeWindow _window = new();
    private readonly FakeProcess _process = new();
    private readonly WindowTabSwitcher<FakeTab> _sut;

    public WindowTabSwitcherTests()
    {
        _window.Tabs.Add(new FakeTab(1));
        _window.Tabs.Add(new FakeTab(2));
        _window.Tabs.Add(new FakeTab(3));
        _window.Tabs.Activate(0);
        _sut = new WindowTabSwitcher<FakeTab>(_window, _process);
    }

    private void DoubleTap(long at)
    {
        _sut.NoteKeyForDoubleCtrl(Ctrl, 0, repeat: false, at);
        _sut.DoubleCtrlKeyUp(Ctrl, at + 20);
        _sut.NoteKeyForDoubleCtrl(Ctrl, 0, repeat: false, at + 40);
        _sut.DoubleCtrlKeyUp(Ctrl, at + 60);
    }

    [Test]
    public async Task TappingCtrlTwiceOpensTheListAndAgainClosesIt()
    {
        // Act
        DoubleTap(0);
        bool opened = _sut.IsOpen;
        DoubleTap(1000);

        // Assert
        await Assert.That(opened).IsTrue();
        await Assert.That(_sut.IsOpen).IsFalse();
    }

    [Test]
    public async Task AnotherKeyBetweenTheTapsIsNotADoubleTap()
    {
        // Act
        _sut.NoteKeyForDoubleCtrl(Ctrl, 0, repeat: false, 0);
        _sut.DoubleCtrlKeyUp(Ctrl, 20);
        _sut.NoteKeyForDoubleCtrl(0x61, 0, repeat: false, 30);
        _sut.NoteKeyForDoubleCtrl(Ctrl, 0, repeat: false, 40);
        _sut.DoubleCtrlKeyUp(Ctrl, 60);

        // Assert
        await Assert.That(_sut.IsOpen).IsFalse();
    }

    [Test]
    public async Task ADoubleTapWaitsForOtherOverlays()
    {
        // Arrange
        _window.OverlayOpen = true;

        // Act
        DoubleTap(0);

        // Assert
        await Assert.That(_sut.IsOpen).IsFalse();
    }

    [Test]
    public async Task HoldingTheChordOpensOnThePreviousTabAndReleasingSwitches()
    {
        // Arrange: tab 2 was used before tab 1
        _window.Tabs.Activate(1);
        _window.Tabs.Activate(0);

        // Act
        _sut.RunAction("1", Sdl.KMOD_CTRL, 0);
        _sut.ModifierUp(Sdl.KMOD_CTRL); // Ctrl still down: nothing yet
        bool stillOpen = _sut.IsOpen;
        _sut.ModifierUp(0);

        // Assert
        await Assert.That(stillOpen).IsTrue();
        await Assert.That(_sut.IsOpen).IsFalse();
        await Assert.That(_window.Activated).IsEqualTo(1);
    }

    [Test]
    public async Task WithoutAModifierTheListStaysUp()
    {
        // Act
        _sut.RunAction("1", 0, 0);
        _sut.ModifierUp(0);

        // Assert
        await Assert.That(_sut.IsOpen).IsTrue();
    }

    [Test]
    public async Task PositionOrderStepsTheBarInsteadOfOpeningTheList()
    {
        // Arrange
        _process.Settings = new Settings { CtrlTabOrder = "position" };

        // Act
        _sut.RunAction("-1", Sdl.KMOD_CTRL, 0);

        // Assert
        await Assert.That(_window.RelativeSteps).IsEquivalentTo([-1]);
        await Assert.That(_sut.IsOpen).IsFalse();
    }

    [Test]
    public async Task OneTabHasNothingToSwitchTo()
    {
        // Arrange
        _window.Tabs.Remove(_window.Tabs.Items[2]);
        _window.Tabs.Remove(_window.Tabs.Items[1]);

        // Act
        _sut.RunAction("1", Sdl.KMOD_CTRL, 0);

        // Assert
        await Assert.That(_sut.IsOpen).IsFalse();
    }

    [Test]
    public async Task TypingWithCtrlHeldFiltersFromTheKeyPress()
    {
        // Arrange: only tab 3 has a "z"
        _window.Tabs.Items[2].Title = "zebra";
        _sut.OpenSticky(0);

        // Act
        bool handled = _sut.KeyDown('z', Sdl.KMOD_CTRL, 10);
        _sut.KeyDown(Sdl.K_RETURN, 0, 20);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(_window.Activated).IsEqualTo(2);
    }

    [Test]
    public async Task PlainTypingIsLeftForTheTextEvent()
    {
        // Arrange
        _sut.OpenSticky(0);

        // Act
        _sut.KeyDown('z', 0, 10);

        // Assert
        await Assert.That(_window.SuppressText).IsFalse();
    }

    [Test]
    public async Task EscapeClosesAndKeepsTheTextEventAway()
    {
        // Arrange
        _sut.OpenSticky(0);

        // Act
        _sut.KeyDown(Sdl.K_ESCAPE, 0, 10);

        // Assert
        await Assert.That(_sut.IsOpen).IsFalse();
        await Assert.That(_window.SuppressText).IsTrue();
    }

    [Test]
    public async Task ATabWithAnUnreadNotificationShowsIt()
    {
        // Arrange: pane 2 lives in tab 2 (the fake's map)
        _process.Notifications.Post(new NotificationRequest(2, NotificationSource.Program, NotificationPriority.Normal, "make", "built", "ok"), new NotificationContext(false, true, 0));

        // Act
        bool tab2 = _sut.HasUnread(_window.Tabs.Items[1]);
        bool tab1 = _sut.HasUnread(_window.Tabs.Items[0]);

        // Assert
        await Assert.That(tab2).IsTrue();
        await Assert.That(tab1).IsFalse();
    }

    [Test]
    public async Task OpeningClosesTheOverlaysItReplacesAndSyncsFirst()
    {
        // Act
        _sut.OpenFromBar(new RectF(0, 0, 10, 10));

        // Assert
        await Assert.That(_window.Syncs).IsEqualTo(1);
        await Assert.That(_window.ClosedOverlays).IsEqualTo(1);
    }
}
