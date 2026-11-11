using System.Runtime.Versioning;
using ErikTerm.App.Notifications;
using ErikTerm.Config;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

internal sealed class FakeDesktop : IDesktopNotifier
{
    public bool Available { get; set; } = true;
    public readonly List<(int Pane, string Title, string Body, bool Urgent)> Sent = [];
    public void Send(int paneId, string title, string markupBody, bool urgent) => Sent.Add((paneId, title, markupBody, urgent));
}

public class NotificationRoutingTests
{
    private static NotificationRequest Req(int pane, string title = "Build", string body = "done",
        NotificationPriority p = NotificationPriority.Normal, NotificationSource s = NotificationSource.Program) =>
        new(pane, s, p, "make", title, body);

    private static NotificationContext Away(long now) => new(PaneSeen: false, WindowFocused: false, now);
    private static NotificationContext Here(long now) => new(PaneSeen: false, WindowFocused: true, now);

    [Test]
    public async Task AskModeSendsNothingAndAsks()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d);

        // Act
        var modeBefore = c.Mode;
        var route = c.Post(Req(1), Away(1000));

        // Assert
        await Assert.That(modeBefore).IsEqualTo(OsNotificationMode.Ask);
        await Assert.That(d.Sent).IsEmpty();
        await Assert.That(route).HasFlag(NotificationRoutes.Ask);
        await Assert.That(route).HasFlag(NotificationRoutes.Bar);
        await Assert.That(c.AskVisible).IsTrue();
        await Assert.That(c.AskApp).IsEqualTo("make");
        await Assert.That(c.UnreadCount).IsEqualTo(1);
    }

    [Test]
    public async Task AskNeedsAWayToNotify()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop { Available = false });

        // Act
        c.Post(Req(1), Away(1000));

        // Assert
        await Assert.That(c.AskPending).IsFalse(); // no notify-send: nothing to ask about
    }

    [Test]
    public async Task DecidingRemembersAndStopsAsking()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        OsNotificationMode? saved = null;
        c.Decided = m => saved = m;
        c.Post(Req(1), Away(1000));

        // Act
        c.Decide(OsNotificationMode.On);

        // Assert
        await Assert.That(saved).IsEqualTo(OsNotificationMode.On);
        await Assert.That(c.AskVisible).IsFalse();
    }

    [Test]
    public async Task AfterDecidingOnTheDesktopIsNotified()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d);
        c.Post(Req(1), Away(1000));
        c.Decide(OsNotificationMode.On);

        // Act
        c.Post(Req(2), Away(5000));

        // Assert
        await Assert.That(d.Sent).HasSingleItem();
    }

    [Test]
    public async Task DeferHidesTheQuestionForNow()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1), Away(1000));

        // Act
        c.DeferAsk();

        // Assert
        await Assert.That(c.AskVisible).IsFalse();
        await Assert.That(c.Mode).IsEqualTo(OsNotificationMode.Ask);
    }

    [Test]
    public async Task OffKeepsItInTheBar()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d) { StoredMode = OsNotificationMode.Off };

        // Act
        var route = c.Post(Req(1), Away(1000));
        var barItem = c.BarItem(1000, 10_000, out _);

        // Assert
        await Assert.That(d.Sent).IsEmpty();
        await Assert.That(route).DoesNotHaveFlag(NotificationRoutes.Ask);
        await Assert.That(barItem).IsSameReferenceAs(c.Items[0]);
    }

    [Test]
    public async Task ConfigWinsOverTheStoredAnswer()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d) { StoredMode = OsNotificationMode.On, ConfigMode = OsNotificationMode.Off };

        // Act
        c.Post(Req(1), Away(1000));

        // Assert
        await Assert.That(d.Sent).IsEmpty();
    }

    [Test]
    public async Task WithoutAConfigModeTheStoredAnswerApplies()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d) { StoredMode = OsNotificationMode.On, ConfigMode = OsNotificationMode.Off };
        c.Post(Req(1), Away(1000));

        // Act
        c.ConfigMode = null;
        c.Post(Req(2), Away(5000));

        // Assert
        await Assert.That(d.Sent).HasSingleItem();
    }

    [Test]
    public async Task FocusedPaneIsNeverNotified()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d) { StoredMode = OsNotificationMode.On };

        // Act
        var route = c.Post(Req(1), new NotificationContext(PaneSeen: true, WindowFocused: true, 1000));

        // Assert
        await Assert.That(route).IsEqualTo(NotificationRoutes.None);
        await Assert.That(c.Items).IsEmpty();
        await Assert.That(d.Sent).IsEmpty();
    }

    [Test]
    public async Task FocusedWindowGetsTheBarNotTheDesktop()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d) { StoredMode = OsNotificationMode.On };

        // Act
        var route = c.Post(Req(1), Here(1000));

        // Assert
        await Assert.That(d.Sent).IsEmpty();
        await Assert.That(route).HasFlag(NotificationRoutes.Bar);
        await Assert.That(route).DoesNotHaveFlag(NotificationRoutes.Toast); // the bar replaces toasts
    }

    [Test]
    public async Task FocusedWindowWithoutTheBarGetsAToast()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop()) { StoredMode = OsNotificationMode.On };
        c.Post(Req(1), Here(1000));
        c.BarEnabled = false;

        // Act
        var route = c.Post(Req(2, "x"), Here(2000));

        // Assert
        await Assert.That(route).HasFlag(NotificationRoutes.Toast);
    }

    [Test]
    public async Task AgentNotificationsFalseKeepsAgentsOffTheDesktop()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d) { StoredMode = OsNotificationMode.On, AgentDesktop = false };

        // Act
        var route = c.Post(Req(1, "Claude needs permission", "", NotificationPriority.Waiting, NotificationSource.Agent), Away(1000));

        // Assert
        await Assert.That(d.Sent).IsEmpty();
        await Assert.That(route).HasFlag(NotificationRoutes.Bar); // still in erikterm
    }

    [Test]
    public async Task AgentNotificationsFalseStillLetsProgramsThrough()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d) { StoredMode = OsNotificationMode.On, AgentDesktop = false };
        c.Post(Req(1, "Claude needs permission", "", NotificationPriority.Waiting, NotificationSource.Agent), Away(1000));

        // Act
        c.Post(Req(2), Away(5000)); // programs aren't agents

        // Assert
        await Assert.That(d.Sent).HasSingleItem();
    }

    [Test]
    public async Task DesktopRateLimits()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d) { StoredMode = OsNotificationMode.On };

        // Act
        c.Post(Req(1, "a"), Away(10_000));
        c.Post(Req(2, "b"), Away(10_500));  // global: 1 s between any two
        c.Post(Req(1, "c"), Away(12_000));  // per pane: 8 s
        c.Post(Req(2, "d"), Away(12_100));
        c.Post(Req(1, "e", p: NotificationPriority.Waiting), Away(14_000)); // waiting skips the per-pane limit

        // Assert
        await Assert.That(d.Sent.Select(s => s.Title)).IsEquivalentTo(["a", "d", "e"], CollectionOrdering.Matching);
        await Assert.That(d.Sent[2].Urgent).IsTrue();
        await Assert.That(c.Items.Count).IsEqualTo(5); // every one is in the history
    }

    [Test]
    public async Task FloodedPaneIsCut()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());

        // Act
        for (int i = 0; i < 20; i++) c.Post(Req(1, $"n{i}"), Here(1000 + i));

        // Assert
        await Assert.That(c.Items.Count).IsEqualTo(NotificationCenter.FloodMax);
    }

    [Test]
    public async Task FloodedPaneIsHeardAgainAfterTheFloodWindow()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        for (int i = 0; i < 20; i++) c.Post(Req(1, $"n{i}"), Here(1000 + i));

        // Act
        c.Post(Req(1, "later"), Here(1000 + NotificationCenter.FloodWindowMs + 1));

        // Assert
        await Assert.That(c.Items.Count).IsEqualTo(NotificationCenter.FloodMax + 1);
    }

    [Test]
    public async Task RepeatsAreCoalesced()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());

        // Act
        c.Post(Req(1), Here(1000));
        c.Post(Req(1), Here(4000));

        // Assert
        var it = await Assert.That(c.Items).HasSingleItem();
        await Assert.That(it.Count).IsEqualTo(2);
        await Assert.That(it.At).IsEqualTo(4000);
    }

    [Test]
    public async Task TextIsCleanedAndTheDesktopBodyEscaped()
    {
        // Arrange
        var d = new FakeDesktop();
        var c = new NotificationCenter(d) { StoredMode = OsNotificationMode.On };
        // U+202E: right-to-left override.
        var request = Req(1, "-x\e[31mevil\u202Etxt", "<a href=\"x\">click</a> & <b>me</b>\u0007" + new string('z', 1000));

        // Act
        c.Post(request, Away(1000));
        var it = c.Items[0];
        var sent = d.Sent[0];

        // Assert
        await Assert.That(it.Title).DoesNotContain("\e", StringComparison.Ordinal);
        await Assert.That(it.Title).DoesNotContain("\u202E", StringComparison.Ordinal);
        await Assert.That(it.Body).DoesNotContain("\u0007", StringComparison.Ordinal);
        await Assert.That(it.Body.Length).IsLessThanOrEqualTo(NotificationText.MaxBody);
        await Assert.That(sent.Title).DoesNotStartWith("-", StringComparison.Ordinal);
        await Assert.That(sent.Body).DoesNotContain("<", StringComparison.Ordinal);
        await Assert.That(sent.Body).StartsWith("&lt;a href=&quot;x&quot;&gt;click&lt;/a&gt; &amp; &lt;b&gt;");
    }
}

public class NotificationHistoryTests
{
    private static NotificationRequest Req(int pane, string title, NotificationPriority p = NotificationPriority.Normal) =>
        new(pane, NotificationSource.Agent, p, "Claude", title, "", AgentState: p == NotificationPriority.Waiting ? AgentState.Waiting : AgentState.Done);

    private static NotificationContext Here(long now) => new(false, true, now);

    /// <summary>A waiting agent in pane 1 (at 1000) and a finished one in pane 2 (at 2000).</summary>
    private static NotificationCenter WaitingThenDone()
    {
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1, "wait", NotificationPriority.Waiting), Here(1000));
        c.Post(Req(2, "done"), Here(2000));
        return c;
    }

    [Test]
    public async Task UnreadFirstThenNewest()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1, "a"), Here(1000));
        c.Post(Req(2, "b"), Here(2000));
        c.Post(Req(3, "c"), Here(3000));
        var list = new List<NotificationItem>();

        // Act
        c.MarkRead(c.Items[2].Id); // "c"
        c.Sorted(list);

        // Assert
        await Assert.That(list.Select(i => i.Title)).IsEquivalentTo(["b", "a", "c"], CollectionOrdering.Matching);
        await Assert.That(c.UnreadCount).IsEqualTo(2);
    }

    [Test]
    public async Task KeepsTheLastHundred()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());

        // Act
        for (int i = 0; i < 150; i++) c.Post(Req(i, $"n{i}"), Here(1000 + i));

        // Assert
        await Assert.That(c.Items.Count).IsEqualTo(NotificationCenter.Capacity);
        await Assert.That(c.Items[0].Title).IsEqualTo("n50");
    }

    [Test]
    public async Task SeeingThePaneReadsIt()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1, "a"), Here(1000));
        c.Post(Req(2, "b"), Here(1000));

        // Act
        c.MarkPaneSeen(1);

        // Assert
        await Assert.That(c.UnreadCount).IsEqualTo(1);
        await Assert.That(c.Items[0].Read).IsTrue();
    }

    [Test]
    public async Task BarPrefersWaitingWhichNeverTimesOut()
    {
        // Arrange
        var c = WaitingThenDone();

        // Act
        var now = c.BarItem(2000, 10_000, out long hide);
        var muchLater = c.BarItem(999_999, 10_000, out _);

        // Assert
        await Assert.That(now?.Title).IsEqualTo("wait");
        await Assert.That(hide).IsEqualTo(long.MaxValue);
        await Assert.That(muchLater?.Title).IsEqualTo("wait"); // waiting never times out
    }

    [Test]
    public async Task BarAutoHidesTheRestOnceWaitingIsResolved()
    {
        // Arrange
        var c = WaitingThenDone();
        c.ResolveWaiting(1);

        // Act
        var shown = c.BarItem(3000, 10_000, out long hide);
        var atTimeout = c.BarItem(12_000, 10_000, out _);

        // Assert
        await Assert.That(shown?.Title).IsEqualTo("done");
        await Assert.That(hide).IsEqualTo(12_000);
        await Assert.That(atTimeout).IsNull();
    }

    [Test]
    public async Task BarTimeoutZeroKeepsTheItemUntilDismissed()
    {
        // Arrange
        var c = WaitingThenDone();
        c.ResolveWaiting(1);

        // Act
        var muchLater = c.BarItem(999_999, 0, out _); // timeout 0: until dismissed

        // Assert
        await Assert.That(muchLater?.Title).IsEqualTo("done");
    }

    [Test]
    public async Task RemoveDropsTheItemAndBumpsTheVersion()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1, "a"), Here(1000));
        c.Post(Req(2, "b"), Here(1000));
        int v = c.Version;

        // Act
        c.Remove(c.Items[0].Id);

        // Assert
        var left = await Assert.That(c.Items).HasSingleItem();
        await Assert.That(left.Title).IsEqualTo("b");
        await Assert.That(c.Version).IsGreaterThan(v);
    }

    [Test]
    public async Task ClearEmptiesTheHistory()
    {
        // Arrange
        var c = new NotificationCenter(new FakeDesktop());
        c.Post(Req(1, "a"), Here(1000));
        c.Post(Req(2, "b"), Here(1000));
        c.Remove(c.Items[0].Id);

        // Act
        c.Clear();

        // Assert
        await Assert.That(c.Items).IsEmpty();
        await Assert.That(c.UnreadCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(3u, "Tab 2 \u00B7 ~/src")]
    [Arguments(4u, "Window 3 \u00B7 Tab 2 \u00B7 ~/src")]
    public async Task WhereNamesOtherWindowsOnly(uint viewingWindow, string expected)
    {
        // Arrange
        var it = new NotificationItem { WindowId = 3, TabLabel = "Tab 2", Place = "~/src" };

        // Act
        string where = it.Where(viewingWindow);

        // Assert
        await Assert.That(where).IsEqualTo(expected);
    }
}

public class NotificationTextTests
{
    [Test]
    [Arguments("plain", "plain")]
    [Arguments("a & b", "a &amp; b")]
    [Arguments("<b>x</b>", "&lt;b&gt;x&lt;/b&gt;")]
    [Arguments("it's \"q\"", "it&apos;s &quot;q&quot;")]
    public async Task EscapesMarkup(string input, string expected)
    {
        // Act
        string escaped = NotificationText.EscapeMarkup(input);

        // Assert
        await Assert.That(escaped).IsEqualTo(expected);
    }

    [Test]
    [Arguments("a\r\n\tb", "a b")]
    [Arguments("a\u202Eb\u2066c", "abc")] // right-to-left override, left-to-right isolate
    [Arguments("\e\u0007", "")]
    public async Task CleanStripsControlsAndBidi(string input, string expected)
    {
        // Act
        string cleaned = NotificationText.Clean(input, 50);

        // Assert
        await Assert.That(cleaned).IsEqualTo(expected);
    }

    [Test]
    public async Task CleanCutsToTheMaximumLength()
    {
        // Act
        string cleaned = NotificationText.Clean(new string('x', 100), 10);

        // Assert
        await Assert.That(cleaned.Length).IsEqualTo(10);
    }

    [Test]
    [Arguments(0L, "just now")]
    [Arguments(59_000L, "just now")]
    [Arguments(60_000L, "1 min ago")]
    [Arguments(3_600_000L, "1 h ago")]
    [Arguments(90_000_000L, "1 d ago")]
    public async Task Ages(long ms, string expected)
    {
        // Act
        string age = NotificationText.Age(ms);

        // Assert
        await Assert.That(age).IsEqualTo(expected);
    }

    [Test]
    public async Task AgeWakesAtTheNextMinute()
    {
        // Act
        long wait = NotificationText.UntilAgeChanges(75_000);

        // Assert
        await Assert.That(wait).IsEqualTo(45_000);
    }
}

public class PreferencesTests
{
    // A fresh directory per test (TUnit creates an instance per test); nothing is created until a test does.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "erikterm-prefs-" + Guid.NewGuid().ToString("N"));

    private string PrefsPath => Path.Combine(_dir, "erikterm", PreferencesStore.FileName);

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Test]
    public async Task MissingFileLoadsAsNotAskedYet()
    {
        // Act
        var loaded = PreferencesStore.Load(PrefsPath); // missing file

        // Assert
        await Assert.That(loaded.OsNotifications).IsNull();
    }

    [Test]
    public async Task RoundTrips()
    {
        // Arrange
        string path = PrefsPath;

        // Act
        PreferencesStore.Save(path, new PreferencesFile { OsNotifications = "off" });
        var loaded = PreferencesStore.Load(path);
        string[] files = Directory.GetFiles(Path.GetDirectoryName(path)!);
        string json = File.ReadAllText(path);

        // Assert
        await Assert.That(loaded.OsNotifications).IsEqualTo("off");
        await Assert.That(files).HasSingleItem(); // no temporary file left behind
        await Assert.That(json).Contains("\"os_notifications\": \"off\"");
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task SavedFileIsPrivate()
    {
        // Arrange
        string path = PrefsPath;

        // Act
        PreferencesStore.Save(path, new PreferencesFile { OsNotifications = "off" });
        var mode = File.GetUnixFileMode(path);

        // Assert
        await Assert.That(mode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task MalformedFileIsIgnored()
    {
        // Arrange
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, PreferencesStore.FileName);
        File.WriteAllText(path, "{ not json");

        // Act
        var loaded = PreferencesStore.Load(path);

        // Assert
        await Assert.That(loaded.OsNotifications).IsNull();
    }

    [Test]
    [Arguments("on", "On")]
    [Arguments("OFF", "Off")]
    [Arguments("ask", "Ask")]
    [Arguments("maybe", null)]
    public async Task ParsesModes(string s, string? expected)
    {
        // Act
        string? mode = PreferencesStore.ParseMode(s)?.ToString();

        // Assert
        await Assert.That(mode).IsEqualTo(expected);
    }

    [Test]
    public async Task SettingsParse()
    {
        // Act
        var r = SettingsLoader.Parse("""
            os_notifications = "off"
            notification_bar_position = "bottom"
            notification_bar_timeout_ms = 0
            notification_bar = false
            """);

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.OsNotifications).IsEqualTo("off");
        await Assert.That(r.Settings.NotificationBarPosition).IsEqualTo("bottom");
        await Assert.That(r.Settings.NotificationBarTimeoutMs).IsEqualTo(0);
        await Assert.That(r.Settings.NotificationBar).IsFalse();
    }

    [Test]
    public async Task OsNotificationsIsUnsetByDefault()
    {
        // Act
        var r = SettingsLoader.Parse("");

        // Assert
        await Assert.That(r.Settings.OsNotifications).IsNull();
    }

    [Test]
    public async Task UnknownOsNotificationsValueIsReported()
    {
        // Act
        var r = SettingsLoader.Parse("os_notifications = \"loud\"");

        // Assert
        await Assert.That(r.Diagnostics).IsNotEmpty();
    }
}
