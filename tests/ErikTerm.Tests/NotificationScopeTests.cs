using ErikTerm.App.Notifications;
using ErikTerm.Config;

namespace ErikTerm.Tests;

public class NotificationScopeTests
{
    private sealed class Panes : IPaneTabs
    {
        public readonly Dictionary<int, int> Tabs = [];
        public readonly Dictionary<int, int> Leads = [];
        public int TabOf(int paneId) => Tabs.GetValueOrDefault(paneId);
        public int LeadOf(int paneId) => Leads.GetValueOrDefault(paneId);
    }

    private const int Tab1 = 101, Tab2 = 102;

    private static Panes TwoTabs()
    {
        var panes = new Panes();
        panes.Tabs[1] = Tab1;
        panes.Tabs[2] = Tab2;
        return panes;
    }

    private static NotificationCenter Center() => new(new FakeDesktop());

    private static void Post(NotificationCenter c, int pane, string title, long at,
        NotificationPriority priority = NotificationPriority.Normal) =>
        c.Post(new NotificationRequest(pane, NotificationSource.Program, priority, "make", title, "body"),
            new NotificationContext(PaneSeen: false, WindowFocused: true, at));

    [Test]
    public async Task TheBarShowsTheOpenTabsNotification()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 1, "mine", 1000);
        Post(c, 2, "other", 2000);

        // Act
        var item = c.BarItem(2500, 10_000, out _, new NotificationScope(panes, Tab1));

        // Assert
        await Assert.That(item?.Title).IsEqualTo("mine");
    }

    [Test]
    public async Task WithoutAScopeTheBarShowsTheLatestOfAnyTab()
    {
        // Arrange
        var c = Center();
        Post(c, 1, "mine", 1000);
        Post(c, 2, "other", 2000);

        // Act
        var item = c.BarItem(2500, 10_000, out _);

        // Assert
        await Assert.That(item?.Title).IsEqualTo("other");
    }

    [Test]
    public async Task AWaitingItemInAnotherTabDoesNotTakeTheBar()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 1, "mine", 1000);
        Post(c, 2, "waiting", 2000, NotificationPriority.Waiting);

        // Act
        var item = c.BarItem(2500, 10_000, out _, new NotificationScope(panes, Tab1));

        // Assert
        await Assert.That(item?.Title).IsEqualTo("mine");
    }

    [Test]
    public async Task SwitchingTabsShowsThatTabsNotification()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 1, "one", 1000);
        Post(c, 2, "two", 1500);

        // Act
        var item = c.BarItem(2000, 10_000, out _, new NotificationScope(panes, Tab2));

        // Assert
        await Assert.That(item?.Title).IsEqualTo("two");
    }

    [Test]
    public async Task AMovedPaneTakesItsNotificationAlong()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 2, "moved", 1000);
        panes.Tabs[2] = Tab1;

        // Act
        var item = c.BarItem(2000, 10_000, out _, new NotificationScope(panes, Tab1));

        // Assert
        await Assert.That(item?.Title).IsEqualTo("moved");
    }

    [Test]
    public async Task AMovedPaneLeavesItsOldTab()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 2, "moved", 1000);
        panes.Tabs[2] = Tab1;

        // Act
        var item = c.BarItem(2000, 10_000, out _, new NotificationScope(panes, Tab2));

        // Assert
        await Assert.That(item).IsNull();
    }

    [Test]
    public async Task ATeammatesNotificationGoesWithItsLeadsTab()
    {
        // Arrange: the teammate pane sits in the other tab, its lead here.
        var panes = TwoTabs();
        panes.Leads[2] = 1;
        var c = Center();
        Post(c, 2, "teammate", 1000);

        // Act
        var item = c.BarItem(2000, 10_000, out _, new NotificationScope(panes, Tab1));

        // Assert
        await Assert.That(item?.Title).IsEqualTo("teammate");
    }

    [Test]
    public async Task ATeammateWhoseLeadIsGoneStaysInItsOwnTab()
    {
        // Arrange
        var panes = TwoTabs();
        panes.Leads[2] = 99; // no such pane any more
        var c = Center();
        Post(c, 2, "teammate", 1000);

        // Act
        var item = c.BarItem(2000, 10_000, out _, new NotificationScope(panes, Tab2));

        // Assert
        await Assert.That(item?.Title).IsEqualTo("teammate");
    }

    [Test]
    public async Task OtherTabsAreCounted()
    {
        // Arrange
        var panes = TwoTabs();
        panes.Tabs[3] = Tab2;
        var c = Center();
        Post(c, 1, "mine", 1000);
        Post(c, 2, "a", 1100);
        Post(c, 3, "b", 1200);

        // Act
        int elsewhere = c.CountElsewhere(new NotificationScope(panes, Tab1));

        // Assert
        await Assert.That(elsewhere).IsEqualTo(2);
    }

    [Test]
    public async Task ReadNotificationsAreNotCountedAsOtherTabs()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 2, "seen", 1000);
        c.MarkPaneSeen(2);

        // Act
        int elsewhere = c.CountElsewhere(new NotificationScope(panes, Tab1));

        // Assert
        await Assert.That(elsewhere).IsEqualTo(0);
    }

    [Test]
    public async Task AClosedPaneIsInNoOtherTab()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 7, "closed", 1000);

        // Act
        int elsewhere = c.CountElsewhere(new NotificationScope(panes, Tab1));

        // Assert
        await Assert.That(elsewhere).IsEqualTo(0);
    }

    [Test]
    public async Task AClosedPaneIsStillInTheHistory()
    {
        // Arrange
        var c = Center();
        Post(c, 7, "closed", 1000);

        // Act
        int unread = c.UnreadCount;

        // Assert
        await Assert.That(unread).IsEqualTo(1);
    }

    [Test]
    public async Task TheOwnTabIsCounted()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 1, "a", 1000);
        Post(c, 1, "b", 1100);
        Post(c, 2, "c", 1200);

        // Act
        int here = c.CountHere(new NotificationScope(panes, Tab1));

        // Assert
        await Assert.That(here).IsEqualTo(2);
    }

    [Test]
    public async Task FreshNewsFromAnotherTabIsAnnounced()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 2, "other", 1000);

        // Act
        var item = c.NewestElsewhere(2000, 10_000, long.MinValue, new NotificationScope(panes, Tab1), out long hideAt);

        // Assert
        await Assert.That(item?.Title).IsEqualTo("other");
        await Assert.That(hideAt).IsEqualTo(11_000);
    }

    [Test]
    public async Task OldNewsFromAnotherTabIsNotAnnounced()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 2, "other", 1000);

        // Act
        var item = c.NewestElsewhere(20_000, 10_000, long.MinValue, new NotificationScope(panes, Tab1), out _);

        // Assert
        await Assert.That(item).IsNull();
    }

    [Test]
    public async Task AWaitingItemElsewhereIsAnnouncedOnlyForAMoment()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 2, "waiting", 1000, NotificationPriority.Waiting);

        // Act
        var item = c.NewestElsewhere(20_000, 10_000, long.MinValue, new NotificationScope(panes, Tab1), out _);

        // Assert
        await Assert.That(item).IsNull();
    }

    [Test]
    public async Task HidingTheAnnouncementHidesOnlyWhatWasThere()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 2, "other", 1000);

        // Act
        var item = c.NewestElsewhere(1500, 10_000, 1200, new NotificationScope(panes, Tab1), out _);

        // Assert
        await Assert.That(item).IsNull();
    }

    [Test]
    public async Task NewNewsAfterHidingIsAnnounced()
    {
        // Arrange
        var panes = TwoTabs();
        var c = Center();
        Post(c, 2, "first", 1000);
        Post(c, 2, "second", 2000);

        // Act
        var item = c.NewestElsewhere(2500, 10_000, 1200, new NotificationScope(panes, Tab1), out _);

        // Assert
        await Assert.That(item?.Title).IsEqualTo("second");
    }

    [Test]
    public async Task ScopeDefaultsToTheTab()
    {
        // Act
        var r = SettingsLoader.Parse("");

        // Assert
        await Assert.That(r.Settings.NotificationBarScope).IsEqualTo("tab");
    }

    [Test]
    public async Task ScopeAllParses()
    {
        // Act
        var r = SettingsLoader.Parse("notification_bar_scope = \"all\"");

        // Assert
        await Assert.That(r.Settings.NotificationBarScope).IsEqualTo("all");
        await Assert.That(r.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task AnUnknownScopeIsReported()
    {
        // Act
        var r = SettingsLoader.Parse("notification_bar_scope = \"window\"");

        // Assert
        await Assert.That(r.Diagnostics).Contains(d => d.Contains("expected", StringComparison.Ordinal));
    }

    [Test]
    public async Task AnUnknownScopeKeepsTheDefault()
    {
        // Act
        var r = SettingsLoader.Parse("notification_bar_scope = \"window\"");

        // Assert
        await Assert.That(r.Settings.NotificationBarScope).IsEqualTo("tab");
    }

    [Test]
    public async Task ScopeIsAKnownSetting()
    {
        // Assert
        await Assert.That(SettingsLoader.KnownKeys).Contains("notification_bar_scope");
    }
}
