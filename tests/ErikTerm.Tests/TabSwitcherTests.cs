using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>Ctrl+Tab's recency list and quick-tap timing, the list's selection and filter, and the tab bar when it overflows.</summary>
public class TabRecencyTests
{
    private sealed class T(string n)
    {
        public override string ToString() => n;
    }

    private static readonly T A = new("A"), B = new("B"), C = new("C"), D = new("D");

    private static TabList<T> ListOf(params T[] tabs)
    {
        var list = new TabList<T>();
        foreach (var t in tabs) list.Add(t);
        return list;
    }

    private static string Order(TabList<T> list) => string.Join("", list.Recent);

    [Test]
    public async Task NewTabsAreMostRecentlyUsedFirst()
    {
        // Arrange + Act
        var list = ListOf(A, B, C);

        // Assert
        await Assert.That(Order(list)).IsEqualTo("CBA");
    }

    [Test]
    public async Task ActivatingATabMovesItToTheFront()
    {
        // Arrange
        var list = ListOf(A, B, C);

        // Act
        list.Activate(A);

        // Assert
        await Assert.That(Order(list)).IsEqualTo("ACB");
    }

    [Test]
    public async Task ActivatingTheActiveTabChangesNothing()
    {
        // Arrange
        var list = ListOf(A, B);

        // Act
        list.Activate(B);

        // Assert
        await Assert.That(Order(list)).IsEqualTo("BA");
    }

    [Test]
    public async Task ReorderingDoesNotTouchRecency()
    {
        // Arrange
        var list = ListOf(A, B, C);

        // Act
        list.Move(0, 2);

        // Assert
        await Assert.That(Order(list)).IsEqualTo("CBA");
    }

    [Test]
    public async Task ClosingABackgroundTabDropsIt()
    {
        // Arrange
        var list = ListOf(A, B, C);

        // Act
        list.Remove(A);

        // Assert
        await Assert.That(Order(list)).IsEqualTo("CB");
    }

    [Test]
    public async Task ClosingTheActiveTabPutsTheTakingOverTabFirst()
    {
        // Arrange: bar order A B C, active B; closing it activates its right neighbour C.
        var list = ListOf(A, B, C);
        list.Activate(B);

        // Act
        list.Remove(B);

        // Assert
        await Assert.That(Order(list)).IsEqualTo("CA");
    }

    [Test]
    public async Task ClosingTheLastTabEmptiesIt()
    {
        // Arrange
        var list = ListOf(A);

        // Act
        list.Remove(A);

        // Assert
        await Assert.That(list.Recent.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TearingOffATabRemovesItFromThisWindowsRecency()
    {
        // Arrange: tear-off goes through Remove on the source list.
        var source = ListOf(A, B, C);
        var target = ListOf(D);

        // Act
        source.Remove(B);
        target.Insert(0, B);

        // Assert
        await Assert.That(Order(source)).IsEqualTo("CA");
        await Assert.That(Order(target)).IsEqualTo("BD");
    }

    [Test]
    public async Task BackgroundTabsGoToTheEnd()
    {
        // Arrange: Ctrl+Enter in the worktree launcher adds a tab, then re-activates the old one.
        var list = ListOf(A, B);
        list.Add(C);
        list.Activate(B);

        // Act
        list.Demote(C);

        // Assert: Ctrl+Tab flips to A, the tab actually used before, not the new one.
        await Assert.That(Order(list)).IsEqualTo("BAC");
    }

    [Test]
    public async Task ResetPutsTheActiveTabFirstThenBarOrder()
    {
        // Arrange: a restored session's tabs, in the wrong recency order.
        var list = ListOf(A, B, C);
        list.Activate(B);

        // Act
        list.ResetRecency();

        // Assert
        await Assert.That(Order(list)).IsEqualTo("BAC");
    }
}

public class TabSwitcherTimingTests
{
    [Test]
    [Arguments(0L, 149L, true)]
    [Arguments(0L, 150L, false)]
    [Arguments(1000L, 1001L, true)]
    [Arguments(1000L, 1200L, false)]
    public async Task QuickTapIsBelowTheDelay(long opened, long released, bool quick)
    {
        // Act
        bool result = TabSwitcherModel.IsQuickTap(opened, released);

        // Assert
        await Assert.That(result).IsEqualTo(quick);
    }

    [Test]
    public async Task HoldPopupWaitsForTheDelay()
    {
        // Act
        bool early = TabSwitcherModel.PopupDue(TabSwitcherMode.Hold, 100, 200);
        bool due = TabSwitcherModel.PopupDue(TabSwitcherMode.Hold, 100, 260);

        // Assert
        await Assert.That(early).IsFalse();
        await Assert.That(due).IsTrue();
    }

    [Test]
    public async Task StickyPopupShowsAtOnce()
    {
        // Act
        bool due = TabSwitcherModel.PopupDue(TabSwitcherMode.Sticky, 100, 100);

        // Assert
        await Assert.That(due).IsTrue();
    }

    [Test]
    public async Task WakeIsTheTimeLeftBeforeThePopup()
    {
        // Arrange
        var model = new TabSwitcherModel();
        model.Open([new TabSwitcherEntry(1, "a")], 0, TabSwitcherMode.Hold, 1000);

        // Act
        int wake = model.PopupWakeMs(1050);

        // Assert
        await Assert.That(wake).IsEqualTo(100);
    }
}

public class TabSwitcherListTests
{
    private static TabSwitcherEntry[] Tabs(params string[] titles) => [.. titles.Select((t, i) => new TabSwitcherEntry(i + 1, t))];

    private static TabSwitcherModel OpenOn(int selected, params string[] titles)
    {
        var model = new TabSwitcherModel();
        model.Open(Tabs(titles), selected, TabSwitcherMode.Hold, 0);
        return model;
    }

    [Test]
    public async Task OpenStartsOnTheGivenEntry()
    {
        // Act
        var model = OpenOn(1, "a", "b", "c");

        // Assert
        await Assert.That(model.SelectedId).IsEqualTo(2);
    }

    [Test]
    public async Task MoveWrapsPastTheEnd()
    {
        // Arrange
        var model = OpenOn(2, "a", "b", "c");

        // Act
        model.Move(1);

        // Assert
        await Assert.That(model.SelectedIndex).IsEqualTo(0);
    }

    [Test]
    public async Task MoveWrapsBeforeTheStart()
    {
        // Arrange
        var model = OpenOn(0, "a", "b", "c");

        // Act
        model.Move(-1);

        // Assert
        await Assert.That(model.SelectedIndex).IsEqualTo(2);
    }

    [Test]
    public async Task PageMovesClampAtTheEnds()
    {
        // Arrange
        var model = OpenOn(0, "a", "b", "c");

        // Act
        model.Move(10, clamp: true);

        // Assert
        await Assert.That(model.SelectedIndex).IsEqualTo(2);
    }

    [Test]
    public async Task TypingFiltersAndSelectsTheBestMatch()
    {
        // Arrange
        var model = OpenOn(0, "logs", "editor", "build");

        // Act
        model.Type("bu");

        // Assert
        await Assert.That(model.Matches.Count).IsEqualTo(1);
        await Assert.That(model.SelectedId).IsEqualTo(3);
    }

    [Test]
    public async Task BackspaceEditsTheFilter()
    {
        // Arrange
        var model = OpenOn(0, "logs", "editor");
        model.Type("ex");

        // Act
        model.Backspace();

        // Assert
        await Assert.That(model.Query).IsEqualTo("e");
        await Assert.That(model.Matches.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ClearingTheFilterReturnsToTheInitialSelection()
    {
        // Arrange
        var model = OpenOn(1, "alpha", "beta", "gamma");
        model.Type("g");

        // Act
        model.Backspace();

        // Assert
        await Assert.That(model.SelectedIndex).IsEqualTo(1);
    }

    [Test]
    public async Task FilteringSearchesTheExtraText()
    {
        // Arrange
        var model = new TabSwitcherModel();
        model.Open([new TabSwitcherEntry(1, "shell", "feature/login"), new TabSwitcherEntry(2, "shell2", "main")], 0, TabSwitcherMode.Hold, 0);

        // Act
        model.Type("login");

        // Assert
        await Assert.That(model.SelectedId).IsEqualTo(1);
    }

    [Test]
    public async Task MatchPositionsStayWithinTheTitle()
    {
        // Arrange
        var model = new TabSwitcherModel();
        model.Open([new TabSwitcherEntry(1, "ab", "xyzzy")], 0, TabSwitcherMode.Hold, 0);

        // Act
        model.Type("xyz");

        // Assert
        await Assert.That(model.Matches[0].Positions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AcceptReturnsTheSelectedTabAndCloses()
    {
        // Arrange
        var model = OpenOn(2, "a", "b", "c");

        // Act
        int? id = model.Accept();

        // Assert
        await Assert.That(id).IsEqualTo(3);
        await Assert.That(model.IsOpen).IsFalse();
    }

    [Test]
    public async Task AcceptWithNoMatchReturnsNull()
    {
        // Arrange
        var model = OpenOn(0, "a", "b");
        model.Type("zzz");

        // Act
        int? id = model.Accept();

        // Assert
        await Assert.That(id).IsNull();
    }

    [Test]
    public async Task SelectionFollowsItsTabWhenAnotherLeaves()
    {
        // Arrange: selected is "c" (id 3).
        var model = OpenOn(2, "a", "b", "c");

        // Act: tab "a" was closed while the list is open.
        model.Sync(Tabs("a", "b", "c").Where(t => t.Id != 1).ToArray());

        // Assert
        await Assert.That(model.SelectedId).IsEqualTo(3);
    }

    [Test]
    public async Task ClosingTheSelectedTabSelectsTheEntryThatTookItsPlace()
    {
        // Arrange: selected is "b" (id 2), the middle entry.
        var model = OpenOn(1, "a", "b", "c");

        // Act: "b" was torn off.
        model.Sync(Tabs("a", "b", "c").Where(t => t.Id != 2).ToArray());

        // Assert: "c" takes its place, and never another window's tab.
        await Assert.That(model.SelectedId).IsEqualTo(3);
    }

    [Test]
    public async Task SyncAppendsNewTabsLast()
    {
        // Arrange
        var model = OpenOn(0, "a", "b");

        // Act
        model.Sync([.. Tabs("a", "b"), new TabSwitcherEntry(9, "background")]);

        // Assert
        await Assert.That(model.Matches[^1].Entry.Id).IsEqualTo(9);
    }

    [Test]
    public async Task SyncRematchesARenamedTab()
    {
        // Arrange
        var model = OpenOn(0, "alpha", "beta");
        model.Type("zeta");
        await Assert.That(model.Matches.Count).IsEqualTo(0);

        // Act
        model.Sync([new TabSwitcherEntry(1, "zeta"), new TabSwitcherEntry(2, "beta")]);

        // Assert
        await Assert.That(model.Matches.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SyncClosesWhenEveryTabIsGone()
    {
        // Arrange
        var model = OpenOn(0, "a");

        // Act
        bool open = model.Sync([]);

        // Assert
        await Assert.That(open).IsFalse();
        await Assert.That(model.IsOpen).IsFalse();
    }
}

public class TabStripTests
{
    [Test]
    public async Task TabsThatFitShrinkEvenly()
    {
        // Act: 4 tabs of up to 230 wide in 1000 pixels.
        var strip = TabStrip.Layout(4, 1000, 4, 64, 230, 80, 0);

        // Assert
        await Assert.That(strip.Overflowing).IsFalse();
        await Assert.That(strip.TabWidth).IsEqualTo(MathF.Round((1000 - 4 * 3) / 4f).ClampMax(230));
    }

    [Test]
    public async Task ManyTabsOverflowAtTheMinimumWidth()
    {
        // Act: 30 tabs would need 30 * 64 + 29 * 4 = 2036 pixels.
        var strip = TabStrip.Layout(30, 800, 4, 64, 230, 80, 0);

        // Assert
        await Assert.That(strip.Overflowing).IsTrue();
        await Assert.That(strip.FitCount).IsGreaterThan(0);
        await Assert.That(strip.Hidden(30)).IsEqualTo(30 - strip.FitCount);
    }

    [Test]
    public async Task OverflowingTabsFillTheViewportWithWholeTabs()
    {
        // Act
        var strip = TabStrip.Layout(30, 800, 4, 64, 230, 80, 0);

        // Assert: what doesn't fit is hidden, so every shown tab has its width and none is cut off at rest.
        float used = strip.FitCount * strip.TabWidth + (strip.FitCount - 1) * 4;
        await Assert.That(used <= strip.ViewportWidth).IsTrue();
    }

    [Test]
    public async Task LayoutClampsTheFirstTab()
    {
        // Act: a stale first index, after tabs were closed.
        var strip = TabStrip.Layout(20, 800, 4, 64, 230, 80, 99);

        // Assert
        await Assert.That(strip.First).IsEqualTo(20 - strip.FitCount);
    }

    [Test]
    [Arguments(0, 3, 10, 4, 0)]   // already in view
    [Arguments(0, 6, 10, 4, 3)]   // right of the window: shifted just enough
    [Arguments(5, 1, 10, 4, 1)]   // left of the window
    [Arguments(0, 9, 10, 4, 6)]   // the last tab: the window ends there
    public async Task RevealShiftsTheWindowTheLeast(int first, int index, int count, int fit, int expected)
    {
        // Act
        int result = TabStrip.Reveal(first, index, count, fit);

        // Assert
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments(-3, 10, 4, 0)]
    [Arguments(3, 10, 4, 3)]
    [Arguments(99, 10, 4, 6)]
    public async Task ClampFirstKeepsTheWindowOverTheTabs(int first, int count, int fit, int expected)
    {
        // Act
        int result = TabStrip.ClampFirst(first, count, fit);

        // Assert
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task HiddenAttentionCountsOnlyTabsOutOfView()
    {
        // Arrange: tabs 0..5 with attention on 1 (in view) and 5 (hidden); window shows 1..3.
        bool[] attention = [false, true, false, false, false, true];

        // Act
        int hidden = TabStrip.HiddenAttention(attention, first: 1, fit: 3);

        // Assert
        await Assert.That(hidden).IsEqualTo(1);
    }

    [Test]
    public async Task HiddenAttentionIsZeroWhenAllInView()
    {
        // Arrange
        bool[] attention = [true, true];

        // Act
        int hidden = TabStrip.HiddenAttention(attention, first: 0, fit: 2);

        // Assert
        await Assert.That(hidden).IsEqualTo(0);
    }

    [Test]
    public async Task MoreLabelCountsTheHiddenTabs()
    {
        // Act
        string label = TabStrip.MoreLabel(7);

        // Assert
        await Assert.That(label).IsEqualTo("7 more");
    }
}

public class CtrlTabOrderSettingTests
{
    private static LoadResult Parse(string toml) => SettingsLoader.Parse(toml, "cfg.toml", new LoadOptions());

    [Test]
    public async Task DefaultsToRecent()
    {
        // Act
        var settings = new Settings();

        // Assert
        await Assert.That(settings.CtrlTabOrder).IsEqualTo("recent");
    }

    [Test]
    [Arguments("position", "position")]
    [Arguments("recent", "recent")]
    [Arguments("POSITION", "position")]
    public async Task ParsesOrder(string value, string expected)
    {
        // Act
        var result = Parse($"ctrl_tab_order = \"{value}\"");

        // Assert
        await Assert.That(result.Settings.CtrlTabOrder).IsEqualTo(expected);
    }

    [Test]
    public async Task WarnsAboutAnUnknownOrderAndKeepsTheDefault()
    {
        // Act
        var result = Parse("ctrl_tab_order = \"sideways\"");

        // Assert
        await Assert.That(result.Settings.CtrlTabOrder).IsEqualTo("recent");
        await Assert.That(result.Diagnostics.Any(d => d.Contains("ctrl_tab_order"))).IsTrue();
    }

    [Test]
    public async Task IsAKnownKey()
    {
        // Act + Assert
        await Assert.That(SettingsLoader.KnownKeys).Contains("ctrl_tab_order");
    }

    [Test]
    public async Task DefaultChordsOpenTheSwitcher()
    {
        // Act
        var keys = new Settings().EffectiveKeys().ToDictionary(b => b.Chord.ToString(), b => b.Action.ToString());

        // Assert
        await Assert.That(keys["Ctrl+Tab"]).IsEqualTo("TabSwitcher(1)");
        await Assert.That(keys["Ctrl+Shift+Tab"]).IsEqualTo("TabSwitcher(-1)");
    }

    [Test]
    public async Task ActivateTabRelativeStillParses()
    {
        // Act
        bool ok = ActionSpec.TryParse("ActivateTabRelative(1)", out var spec, out _);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(spec.Name).IsEqualTo("ActivateTabRelative");
    }

    [Test]
    public async Task ShowTabSwitcherIsBindable()
    {
        // Act
        bool ok = ActionSpec.TryParse("ShowTabSwitcher", out _, out _);

        // Assert
        await Assert.That(ok).IsTrue();
    }
}

internal static class TabSwitcherMath
{
    public static float ClampMax(this float value, float max) => MathF.Min(value, max);
}

/// <summary>Ctrl tapped twice on its own opens the tab list; ordinary typing never does.</summary>
public class DoubleTapDetectorTests
{
    private static bool Tap(DoubleTapDetector d, long down, long up)
    {
        d.Down(down);
        return d.Up(up);
    }

    [Test]
    public async Task TwoQuickTapsFireOnTheSecondRelease()
    {
        // Arrange
        var d = new DoubleTapDetector();

        // Act
        bool first = Tap(d, 0, 50);
        bool second = Tap(d, 150, 200);

        // Assert
        await Assert.That(first).IsFalse();
        await Assert.That(second).IsTrue();
    }

    [Test]
    public async Task TheSecondPressAloneDoesNotFire()
    {
        // Arrange
        var d = new DoubleTapDetector();
        Tap(d, 0, 50);

        // Act
        d.Down(150);
        d.Reset(); // the C of a Ctrl+C pressed while Ctrl is down
        bool up = d.Up(200);

        // Assert
        await Assert.That(up).IsFalse();
    }

    [Test]
    public async Task AnotherKeyBetweenTheTapsCancels()
    {
        // Arrange
        var d = new DoubleTapDetector();
        Tap(d, 0, 50);
        d.Reset();

        // Act
        bool second = Tap(d, 150, 200);

        // Assert
        await Assert.That(second).IsFalse();
    }

    [Test]
    public async Task TooLongAGapCancels()
    {
        // Arrange
        var d = new DoubleTapDetector();
        Tap(d, 0, 50);

        // Act
        bool second = Tap(d, 50 + DoubleTapDetector.MaxGapMs + 1, 50 + DoubleTapDetector.MaxGapMs + 40);

        // Assert
        await Assert.That(second).IsFalse();
    }

    [Test]
    public async Task AHeldPressIsNotATap()
    {
        // Arrange
        var d = new DoubleTapDetector();
        Tap(d, 0, DoubleTapDetector.MaxHoldMs + 1);

        // Act
        bool second = Tap(d, DoubleTapDetector.MaxHoldMs + 50, DoubleTapDetector.MaxHoldMs + 90);

        // Assert
        await Assert.That(second).IsFalse();
    }

    [Test]
    public async Task AThirdTapStartsOver()
    {
        // Arrange
        var d = new DoubleTapDetector();
        Tap(d, 0, 50);
        Tap(d, 100, 150);

        // Act
        bool third = Tap(d, 200, 250);

        // Assert
        await Assert.That(third).IsFalse();
    }

    [Test]
    [Arguments("", true)]
    [Arguments("tab_switcher_double_ctrl = false", false)]
    public async Task TheSettingDefaultsToOn(string toml, bool expected)
    {
        // Act
        var r = SettingsLoader.Parse(toml);

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.TabSwitcherDoubleCtrl).IsEqualTo(expected);
    }
}
