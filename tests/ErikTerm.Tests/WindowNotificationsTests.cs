using ErikTerm.App;
using ErikTerm.App.Notifications;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class WindowNotificationsTests
{
    private const int Tab1 = 101, Tab2 = 102, HerePane = 1, OtherPane = 2;
    private const long T0 = 100_000;

    private sealed class FakeProcess : INotificationProcess
    {
        public NotificationCenter Notifications { get; } = new(new FakeDesktop());
        public Settings Settings { get; set; } = new();
        public Dictionary<KeyChord, ActionSpec> Keys { get; } = [];
        public string ConfigPath => "/home/me/.config/erikterm/erikterm.toml";
        public void DrainAgentFocusRequests() { }
        public int NotificationPaneStamp() => 1;
        public void PruneNotificationPanes(long now) { }
        public string? NotificationTabLabel(int paneId, uint viewer) => null;
        public string? Workspace;
        public int WorkspaceRefreshes;
        public string? NotificationElsewhere(int paneId, uint viewer) => Workspace;
        public void RefreshNotificationPlaces(long now) => WorkspaceRefreshes++;
        public bool JumpToNotification(NotificationItem item) => true;
        public int TabOf(int paneId) => paneId == HerePane ? Tab1 : Tab2;
        public int LeadOf(int paneId) => 0;
    }

    private sealed class FakeWindow : INotificationWindow
    {
        public uint Id => 1;
        public Theme Theme { get; } = new();
        public bool SuppressText { get; set; }
        public bool IsFocused { get; set; }
        public int FocusedPaneId => HerePane;
        public int? ActiveTabId { get; set; } = Tab1;
        public int Layouts;
        public readonly List<string> Toasts = [];
        public readonly List<FinishedCommandNotice> Found = [];
        public readonly List<(int TabIndex, int PaneId, string Title)> Delivered = [];
        public void Redraw() { }
        public void ApplyBarLayout() => Layouts++;
        public void CloseOverlaysForPanel(long now) { }
        public void Toast(ToastKind kind, string title, string? detail = null) => Toasts.Add(title);
        public void OpenPalette(string filter) { }
        public string[] ShortcutFor(ActionSpec action) => [];
        public void CollectFinishedCommands(List<FinishedCommandNotice> into, long now) { into.AddRange(Found); Found.Clear(); }
        public void DeliverFinishedCommand(int tabIndex, int paneId, string title, string body, long now) => Delivered.Add((tabIndex, paneId, title));
    }

    private readonly FakeProcess _process = new();
    private readonly FakeWindow _window = new();
    private readonly InfoBar _infoBar = new();
    private readonly WindowNotifications _sut;

    public WindowNotificationsTests() => _sut = new WindowNotifications(_window, _process, _infoBar);

    private void Post(int pane, string title, long at, bool windowFocused = true) =>
        _process.Notifications.Post(new NotificationRequest(pane, NotificationSource.Program, NotificationPriority.Normal, "make", title, "body"),
            new NotificationContext(PaneSeen: false, windowFocused, at));

    [Test]
    public async Task AnItemOnThisTabCountsTheOtherTabsOnItsChip()
    {
        // Arrange
        Post(HerePane, "mine", T0);
        Post(OtherPane, "other", T0 + 1);

        // Act
        _sut.Tick(T0 + 2);

        // Assert
        await Assert.That(_sut.BarMessage?.Text).IsEqualTo("mine");
        await Assert.That(_sut.BarMessage?.Chip).IsEqualTo("+1 in other tabs");
    }

    [Test]
    public async Task OnlyOtherTabsUnreadShowsTheElsewhereBar()
    {
        // Arrange
        Post(OtherPane, "other", T0);

        // Act
        _sut.Tick(T0 + 1);

        // Assert
        await Assert.That(_sut.BarMessage?.Text).IsEqualTo("New in other tabs");
        await Assert.That(_sut.BarMessage?.Buttons.Length).IsEqualTo(0);
    }

    [Test]
    public async Task ClosingTheElsewhereBarLeavesTheNotificationUnread()
    {
        // Arrange
        long now = Environment.TickCount64;
        Post(OtherPane, "other", now - 10);
        _sut.Tick(now);

        // Act
        _sut.BarMessage!.Dismissed!();
        _sut.Tick(now + 1);

        // Assert
        await Assert.That(_sut.BarMessage).IsNull();
        await Assert.That(_process.Notifications.UnreadCount).IsEqualTo(1);
    }

    [Test]
    public async Task ScopeAllCountsEveryUnreadOnTheChip()
    {
        // Arrange
        _process.Settings = new Settings { NotificationBarScope = "all" };
        Post(HerePane, "mine", T0);
        Post(OtherPane, "other", T0 + 1);

        // Act
        _sut.Tick(T0 + 2);

        // Assert
        await Assert.That(_sut.BarMessage?.Text).IsEqualTo("other");
        await Assert.That(_sut.BarMessage?.Chip).IsEqualTo("2 unread");
    }

    [Test]
    public async Task ScopeAllNamesTheWorkspaceOfANotificationFromElsewhere()
    {
        // Arrange
        _process.Settings = new Settings { NotificationBarScope = "all" };
        _process.Workspace = "Workspace 3";
        Post(OtherPane, "other", T0);

        // Act
        _sut.Tick(T0 + 1);

        // Assert
        await Assert.That(_sut.BarMessage?.Label).IsEqualTo("Workspace 3");
        await Assert.That(_process.WorkspaceRefreshes).IsEqualTo(1);
    }

    [Test]
    public async Task TheTabScopedBarNeverAsksForAWorkspace()
    {
        // Arrange
        _process.Workspace = "Workspace 3";
        Post(HerePane, "mine", T0);

        // Act
        _sut.Tick(T0 + 1);

        // Assert
        await Assert.That(_process.WorkspaceRefreshes).IsEqualTo(0);
    }

    [Test]
    public async Task TheBarHidesAtItsTimeoutAndGivesRoomBack()
    {
        // Arrange
        _process.Settings = new Settings { NotificationBarTimeoutMs = 3000 };
        Post(HerePane, "mine", T0);
        _sut.Tick(T0 + 1);
        int wake = _sut.WakeMs(T0 + 1);
        int layoutsWhileShown = _window.Layouts;

        // Act
        _sut.Tick(T0 + 1 + wake);

        // Assert
        await Assert.That(wake).IsEqualTo(2999);
        await Assert.That(_sut.BarMessage).IsNull();
        await Assert.That(layoutsWhileShown).IsEqualTo(1);
        await Assert.That(_window.Layouts).IsEqualTo(2);
        await Assert.That(_sut.WakeMs(T0 + 1 + wake)).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task TheBottomPositionReservesRoomBelowInsteadOfAbove()
    {
        // Arrange
        _process.Settings = new Settings { NotificationBarPosition = "bottom" };
        Post(HerePane, "mine", T0);

        // Act
        _sut.Tick(T0 + 1);

        // Assert
        await Assert.That(_sut.ReserveTop).IsEqualTo(0);
        await Assert.That(_sut.ReserveBottom).IsEqualTo(InfoBar.Reserve(_window.Theme));
    }

    [Test]
    public async Task AWantedDesktopNotificationAsksOnTheInfoBar()
    {
        // Arrange
        Post(HerePane, "mine", T0, windowFocused: false);

        // Act
        _sut.Tick(T0 + 1);

        // Assert
        await Assert.That(_infoBar.Current?.Text).IsEqualTo("make wants to send desktop notifications");
        await Assert.That(_sut.ReserveTop).IsEqualTo(2 * InfoBar.Reserve(_window.Theme));
    }

    [Test]
    public async Task AModeFixedByTheConfigIsNotChangedByTheToggle()
    {
        // Arrange
        _process.Notifications.ConfigMode = OsNotificationMode.Off;

        // Act
        _sut.Execute("ToggleDesktopNotifications");

        // Assert
        await Assert.That(_process.Notifications.StoredMode).IsNull();
        await Assert.That(_window.Toasts).IsEquivalentTo(["os_notifications = \"off\" is set in erikterm.toml"]);
    }

    [Test]
    public async Task AFinishedCommandIsDeliveredOnce()
    {
        // Arrange
        _window.Found.Add(new FinishedCommandNotice(1, OtherPane, new FinishedCommand("make", 2, 72_000, 0, 1), "make"));

        // Act
        _sut.TickFinishedCommands(T0);
        _sut.TickFinishedCommands(T0 + 1);

        // Assert
        await Assert.That(_window.Delivered.Count).IsEqualTo(1);
        await Assert.That(_window.Delivered[0]).IsEqualTo((1, OtherPane, "✗ make exited 2 after 1m 12s"));
    }

    [Test]
    public async Task TheKeyBoundToThePanelClosesIt()
    {
        // Arrange
        KeyChord.TryParse("n", out var chord, out _);
        _process.Keys[chord] = new ActionSpec("NotificationsPanel", null);
        _sut.Execute("NotificationsPanel");

        // Act
        _sut.PanelKey('n', 0, T0);

        // Assert
        await Assert.That(_sut.IsPanelOpen).IsFalse();
    }
}
