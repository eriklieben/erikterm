using ErikTerm.App.Notifications;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

/// <summary>The notification center's history bookkeeping: titles it makes up, unread per pane, repeats and pruning.</summary>
// Internal: the center's NotificationPriority (an app type) is a test parameter.
internal class NotificationCenterEdgeTests
{
    private static NotificationRequest Req(int pane, string title = "Build", string body = "done", string app = "make",
        NotificationPriority p = NotificationPriority.Normal) =>
        new(pane, NotificationSource.Program, p, app, title, body);

    private static readonly NotificationContext Away = new(PaneSeen: false, WindowFocused: false, 0);

    private static NotificationContext At(long now) => new(PaneSeen: false, WindowFocused: false, now);

    [Test]
    [Arguments("", "", "make", "make")]          // nothing but the app: its name
    [Arguments("", "", "", "erikterm")]          // nothing at all
    [Arguments("", "tests passed", "make", "tests passed")]   // the body becomes the title
    public async Task AnUntitledNotificationGetsATitle(string title, string body, string app, string expected)
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());

        // Act
        c.Post(Req(1, title, body, app), Away);

        // Assert
        var item = c.Items.Single();
        await Assert.That(item.Title).IsEqualTo(expected);
        await Assert.That(item.Body).IsEqualTo("");
    }

    [Test]
    public async Task HasUnreadIsPerPane()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1), Away);

        // Act
        bool one = c.HasUnread(1), two = c.HasUnread(2);

        // Assert
        await Assert.That((one, two)).IsEqualTo((true, false));
    }

    [Test]
    public async Task HasUnreadWithNothingUnreadIsFalse()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1), Away);
        c.MarkAllRead();

        // Act
        bool unread = c.HasUnread(1);

        // Assert
        await Assert.That(unread).IsFalse();
    }

    [Test]
    public async Task MarkingAPaneSeenWithNothingUnreadChangesNothing()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1), Away);
        c.MarkAllRead();
        int version = c.Version;

        // Act
        c.MarkPaneSeen(1);
        c.MarkAllRead();

        // Assert
        await Assert.That(c.Version).IsEqualTo(version);
    }

    [Test]
    public async Task MarkingAnotherPaneSeenKeepsThisOneUnread()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1), Away);
        int version = c.Version;

        // Act
        c.MarkPaneSeen(2);

        // Assert
        await Assert.That(c.UnreadCount).IsEqualTo(1);
        await Assert.That(c.Version).IsEqualTo(version);
    }

    [Test]
    public async Task MarkReadOfAnUnknownOrReadItemChangesNothing()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1), Away);
        long id = c.Items[0].Id;
        c.MarkRead(id);
        int version = c.Version;

        // Act
        c.MarkRead(id);
        c.MarkRead(9999);
        c.Remove(9999);

        // Assert
        await Assert.That(c.Version).IsEqualTo(version);
    }

    [Test]
    public async Task ClearingAnEmptyHistoryChangesNothing()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        int version = c.Version;

        // Act
        c.Clear();

        // Assert
        await Assert.That(c.Version).IsEqualTo(version);
    }

    [Test]
    public async Task ARepeatKeepsTheHigherPriority()
    {
        // Arrange: the same text, first waiting, then normal
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1, p: NotificationPriority.Waiting), At(0));

        // Act
        c.Post(Req(1, p: NotificationPriority.Normal), At(10));

        // Assert
        var item = c.Items.Single();
        await Assert.That(item.Count).IsEqualTo(2);
        await Assert.That(item.Priority).IsEqualTo(NotificationPriority.Waiting);
    }

    [Test]
    public async Task ARepeatRaisesThePriority()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1), At(0));

        // Act
        c.Post(Req(1, p: NotificationPriority.Waiting), At(10));

        // Assert
        await Assert.That(c.Items.Single().Priority).IsEqualTo(NotificationPriority.Waiting);
    }

    [Test]
    public async Task ResolvingAPaneWithoutWaitingItemsChangesNothing()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1), Away);
        int version = c.Version;

        // Act
        c.ResolveWaiting(1);

        // Assert
        await Assert.That(c.Version).IsEqualTo(version);
    }

    [Test]
    public async Task ManyPanesPostingAreStillRateLimitedPerPane()
    {
        // Arrange: 300 panes post once (pruning the flood table), long after which pane 1 posts five times quickly
        var c = new NotificationCenter(new FakeDesktop());
        for (int pane = 1; pane <= 300; pane++) c.Post(Req(pane, title: $"t{pane}"), At(0));

        // Act
        for (int i = 0; i < NotificationCenter.FloodMax + 1; i++) c.Post(Req(1, title: $"again {i}"), At(10_000 + i));

        // Assert: four of the five made it
        int fromOne = c.Items.Count(it => it.PaneId == 1 && it.Title.StartsWith("again", StringComparison.Ordinal));
        await Assert.That(fromOne).IsEqualTo(NotificationCenter.FloodMax);
    }

    [Test]
    [Arguments(AgentState.Waiting, NotificationPriority.Waiting)]
    [Arguments(AgentState.Done, NotificationPriority.Normal)]
    public async Task AgentStatesMapToPriorities(AgentState state, NotificationPriority expected)
    {
        // Act
        var priority = NotificationCenter.PriorityFor(state);

        // Assert
        await Assert.That(priority).IsEqualTo(expected);
    }
}
