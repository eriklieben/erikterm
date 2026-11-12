using ErikTerm.App.Native;
using ErikTerm.App.Notifications;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>The Ctrl+Tab list's overlay: when it shows, and what its keys, wheel and clicks return.</summary>
public class TabSwitcherOverlayTests
{
    private static List<TabSwitcherOverlay.Row> Rows(params string[] titles) =>
        [.. titles.Select((t, i) => new TabSwitcherOverlay.Row { Id = i + 1, Title = t, Number = (i + 1).ToString() })];

    private static TabSwitcherOverlay Open(TabSwitcherMode mode, int selected, params string[] titles)
    {
        var overlay = new TabSwitcherOverlay();
        overlay.Open(Rows(titles), selected, mode, null, new Theme(), 1000);
        return overlay;
    }

    [Test]
    public async Task AHoldShowsThePopupOnlyAfterTheDelay()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Hold, 0, "a", "b");

        // Act
        bool early = overlay.Tick(1000 + TabSwitcherModel.QuickTapMs - 1);
        bool due = overlay.Tick(1000 + TabSwitcherModel.QuickTapMs);
        bool again = overlay.Tick(1000 + TabSwitcherModel.QuickTapMs + 10);

        // Assert: it reveals once.
        await Assert.That(early).IsFalse();
        await Assert.That(due).IsTrue();
        await Assert.That(again).IsFalse();
    }

    [Test]
    public async Task AHoldWakesTheLoopForThePopup()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Hold, 0, "a", "b");

        // Act
        int before = overlay.WakeMs(1050);
        overlay.Tick(1000 + TabSwitcherModel.QuickTapMs);
        int after = overlay.WakeMs(1200);

        // Assert
        await Assert.That(before).IsEqualTo(TabSwitcherModel.QuickTapMs - 50);
        await Assert.That(after).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task AStickyListShowsAtOnce()
    {
        // Act
        var overlay = Open(TabSwitcherMode.Sticky, 0, "a", "b");

        // Assert
        await Assert.That(overlay.Mode).IsEqualTo(TabSwitcherMode.Sticky);
        await Assert.That(overlay.WakeMs(1000)).IsEqualTo(int.MaxValue);
        await Assert.That(overlay.Tick(1000)).IsFalse();
        await Assert.That(overlay.IsVisible(1000)).IsTrue();
    }

    [Test]
    [Arguments(Sdl.K_ESCAPE, "Cancel")]
    [Arguments(Sdl.K_RETURN, "Accept")]
    [Arguments(Sdl.K_KP_ENTER, "Accept")]
    [Arguments(Sdl.K_DOWN, "Handled")]
    [Arguments((uint)'x', "None")]
    public async Task KeysSayWhatTheCallerShouldDo(uint key, string expected)
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 0, "a", "b");

        // Act
        var result = overlay.HandleKey(key, 0, 1000);

        // Assert
        await Assert.That(result.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task AClosedListHandlesNoKeys()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 0, "a");
        overlay.Close();

        // Act
        var result = overlay.HandleKey(Sdl.K_RETURN, 0, 1000);

        // Assert
        await Assert.That(result).IsEqualTo(TabSwitcherOverlay.KeyResult.None);
        await Assert.That(overlay.IsOpen).IsFalse();
        await Assert.That(overlay.IsVisible(1000)).IsFalse();
    }

    [Test]
    public async Task DownThenAcceptGivesTheNextTab()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 0, "a", "b", "c");

        // Act
        overlay.HandleKey(Sdl.K_DOWN, 0, 1000);
        int? id = overlay.Accept();

        // Assert
        await Assert.That(id).IsEqualTo(2);
        await Assert.That(overlay.IsOpen).IsFalse();
    }

    [Test]
    public async Task ShiftTabStepsBackAndWraps()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Hold, 0, "a", "b", "c");

        // Act
        overlay.HandleKey(Sdl.K_TAB, Sdl.KMOD_SHIFT, 1000);

        // Assert
        await Assert.That(overlay.Accept()).IsEqualTo(3);
    }

    [Test]
    public async Task UpAndTabStep()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 1, "a", "b", "c");

        // Act
        overlay.HandleKey(Sdl.K_TAB, 0, 1000);
        overlay.HandleKey(Sdl.K_TAB, 0, 1000);
        overlay.HandleKey(Sdl.K_UP, 0, 1000);

        // Assert
        await Assert.That(overlay.Accept()).IsEqualTo(3);
    }

    [Test]
    public async Task HomeAndEndJumpToTheEnds()
    {
        // Arrange
        var a = Open(TabSwitcherMode.Sticky, 1, "a", "b", "c");
        var b = Open(TabSwitcherMode.Sticky, 1, "a", "b", "c");

        // Act
        a.HandleKey(Sdl.K_HOME, 0, 1000);
        b.HandleKey(Sdl.K_END, 0, 1000);

        // Assert
        await Assert.That(a.Accept()).IsEqualTo(1);
        await Assert.That(b.Accept()).IsEqualTo(3);
    }

    [Test]
    public async Task PageKeysClampInsteadOfWrapping()
    {
        // Arrange
        var down = Open(TabSwitcherMode.Sticky, 1, "a", "b", "c");
        var up = Open(TabSwitcherMode.Sticky, 1, "a", "b", "c");

        // Act
        down.HandleKey(Sdl.K_PAGEDOWN, 0, 1000);
        up.HandleKey(Sdl.K_PAGEUP, 0, 1000);

        // Assert
        await Assert.That(down.Accept()).IsEqualTo(3);
        await Assert.That(up.Accept()).IsEqualTo(1);
    }

    [Test]
    public async Task TypedTextFiltersTheList()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 0, "logs", "editor", "build");

        // Act
        overlay.HandleText("bu", 1000);

        // Assert
        await Assert.That(overlay.Accept()).IsEqualTo(3);
    }

    [Test]
    public async Task TextFromAKeyFiltersToo()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Hold, 0, "logs", "editor", "build");

        // Act
        overlay.TypeFromKey("ed", 1000);

        // Assert
        await Assert.That(overlay.Accept()).IsEqualTo(2);
    }

    [Test]
    public async Task BackspaceWidensTheFilter()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 0, "logs", "editor");
        overlay.HandleText("ex", 1000);

        // Act
        var result = overlay.HandleKey(Sdl.K_BACKSPACE, 0, 1000);

        // Assert
        await Assert.That(result).IsEqualTo(TabSwitcherOverlay.KeyResult.Handled);
        await Assert.That(overlay.Accept()).IsEqualTo(2);
    }

    [Test]
    public async Task TheWheelMovesOneEntryAgainstItsDirection()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 1, "a", "b", "c");

        // Act
        overlay.HandleWheel(-3, 1000);

        // Assert
        await Assert.That(overlay.Accept()).IsEqualTo(3);
    }

    [Test]
    public async Task AZeroWheelChangesNothing()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 1, "a", "b", "c");

        // Act
        overlay.HandleWheel(0, 1000);

        // Assert
        await Assert.That(overlay.Accept()).IsEqualTo(2);
    }

    [Test]
    public async Task ClicksBeforeThePopupShowsAreIgnored()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Hold, 0, "a", "b");

        // Act
        var result = overlay.HandleMouseDown(5, 5, 1001);

        // Assert
        await Assert.That(result).IsEqualTo(TabSwitcherOverlay.KeyResult.None);
    }

    [Test]
    public async Task AClickOutsideTheShownPanelCancels()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 0, "a", "b");

        // Act
        var result = overlay.HandleMouseDown(5, 5, 1000);

        // Assert
        await Assert.That(result).IsEqualTo(TabSwitcherOverlay.KeyResult.Cancel);
    }

    [Test]
    public async Task SyncFollowsTheTabsAndClosesWhenNoneAreLeft()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 0, "a", "b");

        // Act
        overlay.Sync([]);

        // Assert
        await Assert.That(overlay.IsOpen).IsFalse();
    }

    [Test]
    public async Task SyncKeepsTheSelectedTabWhenAnotherCloses()
    {
        // Arrange
        var overlay = Open(TabSwitcherMode.Sticky, 1, "a", "b", "c");

        // Act
        overlay.Sync([.. Rows("a", "b", "c").Where(r => r.Id != 1)]);

        // Assert
        await Assert.That(overlay.Accept()).IsEqualTo(2);
    }

    [Test]
    public async Task SyncWhileClosedDoesNothing()
    {
        // Arrange
        var overlay = new TabSwitcherOverlay();

        // Act
        overlay.Sync(Rows("a"));

        // Assert
        await Assert.That(overlay.IsOpen).IsFalse();
        await Assert.That(overlay.Accept()).IsNull();
    }
}

/// <summary>The vim cheat sheet's keys: which close it, and that it owns the keyboard while open.</summary>
public class VimHelpOverlayTests
{
    private static VimHelpOverlay Open()
    {
        var overlay = new VimHelpOverlay();
        overlay.Open(new Theme(), 0);
        return overlay;
    }

    [Test]
    [Arguments(Sdl.K_ESCAPE, (ushort)0)]
    [Arguments(Sdl.K_RETURN, (ushort)0)]
    [Arguments(Sdl.K_KP_ENTER, (ushort)0)]
    [Arguments((uint)'q', (ushort)0)]
    [Arguments((uint)'/', Sdl.KMOD_SHIFT)]
    [Arguments((uint)'?', Sdl.KMOD_SHIFT)]
    public async Task TheseKeysCloseIt(uint key, ushort mod)
    {
        // Arrange
        var overlay = Open();

        // Act
        bool handled = overlay.HandleKey(key, mod, 10);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(overlay.IsOpen).IsFalse();
    }

    [Test]
    [Arguments((uint)'/')]
    [Arguments((uint)'j')]
    [Arguments((uint)'k')]
    [Arguments((uint)'d')]
    [Arguments((uint)'u')]
    [Arguments((uint)'g')]
    [Arguments(Sdl.K_END)]
    [Arguments(Sdl.K_PAGEDOWN)]
    [Arguments((uint)'z')]
    public async Task OtherKeysAreSwallowedAndKeepItOpen(uint key)
    {
        // Arrange
        var overlay = Open();

        // Act
        bool handled = overlay.HandleKey(key, 0, 10);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(overlay.IsOpen).IsTrue();
    }

    [Test]
    public async Task ShiftGScrollsToTheEndWithoutClosing()
    {
        // Arrange
        var overlay = Open();

        // Act
        bool handled = overlay.HandleKey('g', Sdl.KMOD_SHIFT, 10);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(overlay.IsOpen).IsTrue();
    }

    [Test]
    public async Task WhileClosedItHandlesNothing()
    {
        // Arrange
        var overlay = new VimHelpOverlay();

        // Act
        bool key = overlay.HandleKey(Sdl.K_ESCAPE, 0, 0);
        bool wheel = overlay.HandleWheel(1, 0);
        bool click = overlay.HandleMouseDown(1, 1, 0);

        // Assert
        await Assert.That(key || wheel || click).IsFalse();
    }

    [Test]
    public async Task TheWheelIsTakenWhileOpen()
    {
        // Arrange
        var overlay = Open();

        // Act
        bool handled = overlay.HandleWheel(-2, 10);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(overlay.IsOpen).IsTrue();
    }

    [Test]
    public async Task AClickOutsideTheCardClosesIt()
    {
        // Arrange
        var overlay = Open();

        // Act
        bool handled = overlay.HandleMouseDown(1, 1, 10);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(overlay.IsOpen).IsFalse();
    }

    [Test]
    public async Task ItAnimatesRightAfterOpeningAndSettles()
    {
        // Arrange
        var overlay = Open();

        // Act
        bool opening = overlay.IsAnimating(10);
        bool later = overlay.IsAnimating(60_000);

        // Assert
        await Assert.That(opening).IsTrue();
        await Assert.That(later).IsFalse();
    }
}

/// <summary>The Notifications panel: its order, jumping, dismissing, the This tab view, and marking read on close.</summary>
public class NotificationsPanelTests
{
    private sealed class Panes : IPaneTabs
    {
        public readonly Dictionary<int, int> Tabs = [];
        public int TabOf(int paneId) => Tabs.GetValueOrDefault(paneId);
        public int LeadOf(int paneId) => 0;
    }

    private static NotificationCenter Center() => new(new FakeDesktop());

    private static void Post(NotificationCenter c, int pane, string title, long at) =>
        c.Post(new NotificationRequest(pane, NotificationSource.Program, NotificationPriority.Normal, "make", title, "body"),
            new NotificationContext(PaneSeen: false, WindowFocused: true, at));

    private sealed class Harness
    {
        public readonly NotificationCenter Center = NotificationsPanelTests.Center();
        public readonly NotificationsPanel Panel = new();
        public readonly List<string> Jumped = [];

        public Harness(params string[] titles)
        {
            for (int i = 0; i < titles.Length; i++) Post(Center, i + 1, titles[i], 1000 * (i + 1));
            Panel.Center = Center;
            Panel.Jump = it => Jumped.Add(it.Title);
        }

        public void Open() => Panel.Open(new Theme(), 10_000);
        public void Key(uint key, ushort mod = 0) => Panel.HandleKey(key, mod, 10_000);
        public List<string> Titles => [.. Center.Items.Select(i => i.Title).Order()];
    }

    [Test]
    public async Task EnterJumpsToTheNewestAndMarksItRead()
    {
        // Arrange
        var h = new Harness("old", "new");
        h.Open();

        // Act
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Jumped).IsEquivalentTo(["new"]);
        await Assert.That(h.Panel.IsOpen).IsFalse();
        await Assert.That(h.Center.Items.Single(i => i.Title == "new").Read).IsTrue();
    }

    [Test]
    public async Task DownMovesToTheNextRow()
    {
        // Arrange
        var h = new Harness("old", "new");
        h.Open();

        // Act
        h.Key('j');
        h.Key(Sdl.K_KP_ENTER);

        // Assert
        await Assert.That(h.Jumped).IsEquivalentTo(["old"]);
    }

    [Test]
    public async Task UpFromTheTopWraps()
    {
        // Arrange
        var h = new Harness("a", "b", "c");
        h.Open();

        // Act
        h.Key(Sdl.K_UP);
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Jumped).IsEquivalentTo(["a"]);
    }

    [Test]
    public async Task DeleteDismissesTheSelectedRow()
    {
        // Arrange
        var h = new Harness("old", "new");
        h.Open();

        // Act
        h.Key('d');

        // Assert
        await Assert.That(h.Titles).IsEquivalentTo(["old"]);
    }

    [Test]
    [Arguments(Sdl.K_DELETE)]
    [Arguments(Sdl.K_BACKSPACE)]
    [Arguments((uint)'d')]
    public async Task ShiftDeleteClearsTheHistory(uint key)
    {
        // Arrange
        var h = new Harness("a", "b");
        h.Open();

        // Act
        h.Key(key, Sdl.KMOD_SHIFT);

        // Assert
        await Assert.That(h.Center.Items).IsEmpty();
    }

    [Test]
    public async Task ClosingMarksEverythingRead()
    {
        // Arrange
        var h = new Harness("a", "b");
        h.Open();

        // Act
        h.Key(Sdl.K_ESCAPE);

        // Assert
        await Assert.That(h.Panel.IsOpen).IsFalse();
        await Assert.That(h.Center.UnreadCount).IsEqualTo(0);
    }

    [Test]
    public async Task TheThisTabViewListsOnlyTheOpenTabsRows()
    {
        // Arrange: pane 1 is in tab 10, pane 2 in tab 20; the open tab is 10.
        var h = new Harness("mine", "other");
        var panes = new Panes();
        panes.Tabs[1] = 10;
        panes.Tabs[2] = 20;
        h.Panel.PlacesChanged(new NotificationScope(panes, 10));
        h.Open();

        // Act
        h.Key('t');
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Jumped).IsEquivalentTo(["mine"]);
    }

    [Test]
    public async Task ClosingTheThisTabViewMarksOnlyItsRowsRead()
    {
        // Arrange
        var h = new Harness("mine", "other");
        var panes = new Panes();
        panes.Tabs[1] = 10;
        panes.Tabs[2] = 20;
        h.Panel.PlacesChanged(new NotificationScope(panes, 10));
        h.Open();
        h.Key('t');

        // Act
        h.Key(Sdl.K_ESCAPE);

        // Assert
        await Assert.That(h.Center.Items.Single(i => i.Title == "mine").Read).IsTrue();
        await Assert.That(h.Center.Items.Single(i => i.Title == "other").Read).IsFalse();
    }

    [Test]
    public async Task ClearAllInTheThisTabViewKeepsOtherTabs()
    {
        // Arrange
        var h = new Harness("mine", "other");
        var panes = new Panes();
        panes.Tabs[1] = 10;
        panes.Tabs[2] = 20;
        h.Panel.PlacesChanged(new NotificationScope(panes, 10));
        h.Open();
        h.Key('t');

        // Act
        h.Key(Sdl.K_DELETE, Sdl.KMOD_SHIFT);

        // Assert
        await Assert.That(h.Titles).IsEquivalentTo(["other"]);
    }

    [Test]
    public async Task TTwiceListsEveryTabAgainWithTheSelectionOnItsRow()
    {
        // Arrange
        var h = new Harness("mine", "other");
        var panes = new Panes();
        panes.Tabs[1] = 10;
        panes.Tabs[2] = 20;
        h.Panel.PlacesChanged(new NotificationScope(panes, 10));
        h.Open();

        // Act
        h.Key('t');
        h.Key('t');
        h.Key(Sdl.K_UP);
        h.Key(Sdl.K_RETURN);

        // Assert: the selection stayed on "mine", now second; the newer "other" is back above it.
        await Assert.That(h.Jumped).IsEquivalentTo(["other"]);
    }

    [Test]
    public async Task TickRebuildsOnlyWhenTheHistoryChanged()
    {
        // Arrange
        var h = new Harness("a");
        h.Open();

        // Act
        bool idle = h.Panel.Tick(10_001);
        Post(h.Center, 5, "b", 10_002);
        bool changed = h.Panel.Tick(10_003);

        // Assert
        await Assert.That(idle).IsFalse();
        await Assert.That(changed).IsTrue();
    }

    [Test]
    public async Task TheSelectionStaysOnItsRowWhenANewOneArrives()
    {
        // Arrange
        var h = new Harness("a", "b");
        h.Open();
        h.Key(Sdl.K_DOWN); // "a"

        // Act
        Post(h.Center, 7, "c", 10_001);
        h.Panel.Tick(10_002);
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Jumped).IsEquivalentTo(["a"]);
    }

    [Test]
    public async Task ItWakesForTheAgesOnlyWhileOpen()
    {
        // Arrange
        var h = new Harness("a");

        // Act
        int closed = h.Panel.WakeMs(10_000);
        h.Open();
        int open = h.Panel.WakeMs(10_000);

        // Assert
        await Assert.That(closed).IsEqualTo(int.MaxValue);
        await Assert.That(open).IsGreaterThan(0).And.IsLessThanOrEqualTo(60_000);
    }

    [Test]
    public async Task WithoutACenterTickDoesNothing()
    {
        // Arrange
        var panel = new NotificationsPanel();
        panel.Open(new Theme(), 0);

        // Act
        bool ticked = panel.Tick(1);

        // Assert
        await Assert.That(ticked).IsFalse();
    }

    [Test]
    public async Task AClickOutsideThePanelCloses()
    {
        // Arrange
        var h = new Harness("a");
        h.Open();

        // Act
        h.Panel.HandleMouseDown(1, 1, 10_000);

        // Assert
        await Assert.That(h.Panel.IsOpen).IsFalse();
        await Assert.That(h.Jumped).IsEmpty();
    }

    [Test]
    public async Task EnterOnAnEmptyListDoesNothing()
    {
        // Arrange
        var h = new Harness();
        h.Open();

        // Act
        h.Key(Sdl.K_DOWN);
        h.Key('d');
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Jumped).IsEmpty();
        await Assert.That(h.Panel.IsOpen).IsTrue();
    }
}
