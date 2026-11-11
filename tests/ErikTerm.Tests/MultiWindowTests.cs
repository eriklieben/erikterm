using ErikTerm.App.Cli;
using ErikTerm.App.Ipc;
using ErikTerm.App.Session;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>Windows of one process: which window owns which tab, moving tabs, closing, focus (ErikTerm.Ui/WindowSet.cs).</summary>
public class WindowSetTests
{
    private sealed class Win(string name)
    {
        public readonly string Name = name;
        public readonly TabList<string> Tabs = new();
        public override string ToString() => Name;
    }

    private static (WindowSet<Win, string> Set, Win A, Win B) TwoWindows()
    {
        var set = new WindowSet<Win, string>();
        var a = new Win("A");
        a.Tabs.Add("a1");
        a.Tabs.Add("a2");
        a.Tabs.Add("a3");
        a.Tabs.Activate(1); // a2
        var b = new Win("B");
        b.Tabs.Add("b1");
        set.Add(a, a.Tabs);
        set.Add(b, b.Tabs); // focused: the newest
        return (set, a, b);
    }

    [Test]
    public async Task TabsBelongToOneWindow()
    {
        // Arrange
        var (set, a, b) = TwoWindows();

        // Act
        var ownerOfA3 = set.WindowOf("a3");
        var ownerOfB1 = set.WindowOf("b1");
        var ownerOfUnknown = set.WindowOf("nope");
        var windows = set.Windows.ToArray();
        var focused = set.Focused;

        // Assert
        await Assert.That(ownerOfA3).IsSameReferenceAs(a);
        await Assert.That(ownerOfB1).IsSameReferenceAs(b);
        await Assert.That(ownerOfUnknown).IsNull();
        await Assert.That(windows).IsEquivalentTo(new[] { a, b }, CollectionOrdering.Matching);
        await Assert.That(focused).IsSameReferenceAs(b);
    }

    [Test]
    public async Task MovingTheActiveTabActivatesItsNeighbourAndTheTabInTheTarget()
    {
        // Arrange
        var (set, a, b) = TwoWindows();

        // Act
        var move = set.MoveTab("a2", b, 0);
        var owner = set.WindowOf("a2");

        // Assert
        await Assert.That(move.From).IsSameReferenceAs(a);
        await Assert.That(move.WasActive).IsTrue();
        await Assert.That(move.SourceActive).IsEqualTo("a3"); // the right neighbour takes over, as after a close
        await Assert.That(move.TargetPrevious).IsEqualTo("b1");
        await Assert.That(move.SourceEmpty).IsFalse();
        await Assert.That(a.Tabs.Items).IsEquivalentTo(new[] { "a1", "a3" }, CollectionOrdering.Matching);
        await Assert.That(b.Tabs.Items).IsEquivalentTo(new[] { "a2", "b1" }, CollectionOrdering.Matching);
        await Assert.That(b.Tabs.Active).IsEqualTo("a2");
        await Assert.That(owner).IsSameReferenceAs(b);
    }

    [Test]
    public async Task MovingABackgroundTabKeepsTheSourcesActiveTab()
    {
        // Arrange
        var (set, a, b) = TwoWindows();

        // Act
        var move = set.MoveTab("a1", b, -1); // negative: the end

        // Assert
        await Assert.That(move.WasActive).IsFalse();
        await Assert.That(a.Tabs.Active).IsEqualTo("a2");
        await Assert.That(b.Tabs.Items).IsEquivalentTo(new[] { "b1", "a1" }, CollectionOrdering.Matching);
        await Assert.That(move.Index).IsEqualTo(1);
    }

    [Test]
    public async Task MovingTheLastTabEmptiesTheWindow()
    {
        // Arrange
        var (set, a, b) = TwoWindows();

        // Act
        var move = set.MoveTab("b1", a, 99); // clamped to the end
        int tabsLeftInB = b.Tabs.Count;
        string? activeInA = a.Tabs.Active;
        // The emptied window closes; focus goes back to the window used before it.
        set.Focus(b);
        var focusAfterClose = set.Remove(b);
        int windowsLeft = set.Count;

        // Assert
        await Assert.That(move.SourceEmpty).IsTrue();
        await Assert.That(tabsLeftInB).IsEqualTo(0);
        await Assert.That(move.Index).IsEqualTo(3);
        await Assert.That(activeInA).IsEqualTo("b1");
        await Assert.That(focusAfterClose).IsSameReferenceAs(a);
        await Assert.That(windowsLeft).IsEqualTo(1);
    }

    [Test]
    public async Task WithinOneWindowItIsAReorder()
    {
        // Arrange
        var (set, a, _) = TwoWindows();

        // Act
        var move = set.MoveTab("a1", a, 2);

        // Assert
        await Assert.That(a.Tabs.Items).IsEquivalentTo(new[] { "a2", "a3", "a1" }, CollectionOrdering.Matching);
        await Assert.That(a.Tabs.Active).IsEqualTo("a1");
        await Assert.That(move.To).IsSameReferenceAs(a);
    }

    [Test]
    public async Task TearOffMakesAWindowOfItsOwn()
    {
        // Arrange
        var (set, a, _) = TwoWindows();
        var c = new Win("C");

        // Act
        var move = set.MoveTabToNewWindow("a3", c, c.Tabs);
        var owner = set.WindowOf("a3");
        var focused = set.Focused;
        int windowCount = set.Count;

        // Assert
        await Assert.That(move.From).IsSameReferenceAs(a);
        await Assert.That(move.WasActive).IsFalse();
        await Assert.That(c.Tabs.Items).IsEquivalentTo(new[] { "a3" }, CollectionOrdering.Matching);
        await Assert.That(c.Tabs.Active).IsEqualTo("a3");
        await Assert.That(owner).IsSameReferenceAs(c);
        await Assert.That(focused).IsSameReferenceAs(c);
        await Assert.That(windowCount).IsEqualTo(3);
    }

    [Test]
    [Arguments("a1", null, true)]       // three tabs: one can leave for a new window
    [Arguments("b1", null, false)]      // its window's only tab: a new window would just replace it
    [Arguments("b1", "A", true)]        // but it can join another window (B then closes)
    [Arguments("a1", "A", false)]
    [Arguments("nope", "B", false)]
    [Arguments("a1", "unknown", false)]
    public async Task WhereATabMayGo(string tab, string? target, bool expected)
    {
        // Arrange
        var (set, a, b) = TwoWindows();
        Win? to = target switch { null => null, "A" => a, "B" => b, _ => new Win(target) };

        // Act
        bool canMove = set.CanMove(tab, to);

        // Assert
        await Assert.That(canMove).IsEqualTo(expected);
    }

    [Test]
    public async Task FocusIsMostRecentlyUsedFirst()
    {
        // Arrange
        var (set, a, b) = TwoWindows();
        var c = new Win("C");
        c.Tabs.Add("c1");

        // Act
        set.Add(c, c.Tabs, focus: false);
        var recencyAfterAdd = set.ByRecency.ToArray();
        set.Focus(c);
        set.Focus(a);
        var recencyAfterFocus = set.ByRecency.ToArray();
        var focusAfterRemovingA = set.Remove(a);
        var focusAfterRemovingC = set.Remove(c);
        var focusAfterRemovingB = set.Remove(b);
        int windowsLeft = set.Count;

        // Assert
        await Assert.That(recencyAfterAdd).IsEquivalentTo(new[] { b, a, c }, CollectionOrdering.Matching);
        await Assert.That(recencyAfterFocus).IsEquivalentTo(new[] { a, c, b }, CollectionOrdering.Matching);
        await Assert.That(focusAfterRemovingA).IsSameReferenceAs(c);
        await Assert.That(focusAfterRemovingC).IsSameReferenceAs(b);
        await Assert.That(focusAfterRemovingB).IsNull(); // the last window: the process exits
        await Assert.That(windowsLeft).IsEqualTo(0);
    }

    [Test]
    public async Task TabListInsert()
    {
        // Arrange
        var tabs = new TabList<string>();
        tabs.Add("x");
        tabs.Add("y");

        // Act
        int insertedAt = tabs.Insert(1, "new");
        var itemsAfterInsert = tabs.Items.ToArray();
        string? activeAfterInsert = tabs.Active;
        int appendedAt = tabs.Insert(-1, "end");
        int clampedTo = tabs.Insert(42, "clamped");

        // Assert
        await Assert.That(insertedAt).IsEqualTo(1);
        await Assert.That(itemsAfterInsert).IsEquivalentTo(new[] { "x", "new", "y" }, CollectionOrdering.Matching);
        await Assert.That(activeAfterInsert).IsEqualTo("new");
        await Assert.That(appendedAt).IsEqualTo(3);
        await Assert.That(clampedTo).IsEqualTo(4);
    }
}

public class MultiWindowSessionTests
{
    private static SessionWindow Window(int tabs, int? x = null, int? y = null)
    {
        var w = new SessionWindow { Width = 900, Height = 600, X = x, Y = y };
        for (int i = 0; i < tabs; i++) w.Tabs.Add(new SessionTab { Layout = new SessionNode { Pane = new SessionPane { Cwd = "/tmp/" + i } } });
        return w;
    }

    [Test]
    public async Task EveryWindowRoundTrips()
    {
        // Arrange
        var file = new SessionFile { Windows = [Window(2, 100, 50), Window(1), Window(3, -1200, 0)] };

        // Act
        string json = SessionJson.Serialize(file);
        var back = SessionJson.Deserialize(json, out string? problem)!;

        // Assert
        await Assert.That(json).Contains("\"x\": 100");
        await Assert.That(problem).IsNull();
        await Assert.That(back.Windows.Count).IsEqualTo(3);
        await Assert.That(back.Windows.Select(w => w.Tabs.Count)).IsEquivalentTo(new[] { 2, 1, 3 }, CollectionOrdering.Matching);
        await Assert.That((back.Windows[0].X, back.Windows[0].Y)).IsEqualTo((100, 50));
        await Assert.That(back.Windows[1].X).IsNull(); // unknown (Wayland): not written, the compositor places the window
        await Assert.That(json).DoesNotContain("\"x\": null");
        await Assert.That(back.Windows[2].X).IsEqualTo(-1200);
    }

    [Test]
    public async Task FilesWithoutPositionsStillLoad()
    {
        // Arrange
        const string json = """{ "version": 1, "windows": [ { "width": 800, "height": 500, "tabs": [ { "layout": { "pane": { "cwd": "/" } } } ] } ] }""";

        // Act
        var file = SessionJson.Deserialize(json, out _)!;
        var windows = SessionWindows.Restorable(file, 16);

        // Assert
        await Assert.That(windows).HasSingleItem();
        await Assert.That(windows[0].X).IsNull();
    }

    [Test]
    public async Task RestoresWindowsWithTabsInOrderUpToTheLimit()
    {
        // Arrange
        var file = new SessionFile { Windows = [Window(1), Window(0), Window(2), null!, Window(1), Window(1)] };

        // Act
        var windows = SessionWindows.Restorable(file, 3);

        // Assert
        await Assert.That(windows.Select(w => w.Tabs.Count)).IsEquivalentTo(new[] { 1, 2, 1 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)] // no session file
    [Arguments(true)]  // a file without windows
    public async Task NothingToRestore(bool fileExists)
    {
        // Arrange
        SessionFile? file = fileExists ? new SessionFile() : null;

        // Act
        var windows = SessionWindows.Restorable(file, 16);

        // Assert
        await Assert.That(windows).IsEmpty();
    }

    [Test]
    public async Task ImplausiblePositionsAreDropped()
    {
        // Arrange
        var file = new SessionFile { Windows = [Window(1, 1_000_000, 5), Window(1, 10, null), Window(1, 20, 30)] };

        // Act
        var windows = SessionWindows.Restorable(file, 16);

        // Assert
        await Assert.That(windows[0].X).IsNull();
        await Assert.That(windows[0].Y).IsNull();
        await Assert.That(windows[1].X).IsNull(); // half a position is no position
        await Assert.That((windows[2].X, windows[2].Y)).IsEqualTo((20, 30));
    }
}

public class IpcWindowTests
{
    // Window 7 holds panes 1, 2 (tabs 10, 11); window 9 holds pane 5 (tab 12).
    private static readonly IpcWindowEntry[] Windows =
    [
        new(7, p => p is 1 or 2, t => t is 10 or 11),
        new(9, p => p == 5, t => t == 12),
    ];

    private static int Route(IpcRequest r, int focused = 1) => IpcRouting.Resolve(r, Windows, focused, out _);

    [Test]
    public async Task WindowIdComesFirst()
    {
        // Act
        int index = Route(new IpcRequest { Command = "spawn", WindowId = 7, PaneId = 5 });

        // Assert
        await Assert.That(index).IsEqualTo(0);
    }

    [Test]
    [Arguments(5, null, 0, 1)]
    [Arguments(2, null, 1, 0)]
    [Arguments(null, 11, 1, 0)]
    [Arguments(null, 12, 0, 1)]
    public async Task PanesAndTabsFindTheirWindow(int? paneId, int? tabId, int focused, int expected)
    {
        // Arrange
        var request = new IpcRequest { PaneId = paneId, TabId = tabId };

        // Act
        int index = Route(request, focused);

        // Assert
        await Assert.That(index).IsEqualTo(expected);
    }

    [Test]
    [Arguments(8, null, null, "no window with id 8")]
    [Arguments(null, 3, null, "no pane with id 3")]
    [Arguments(null, null, 99, "no tab with id 99")]
    public async Task UnknownIdsAreReported(int? windowId, int? paneId, int? tabId, string expectedError)
    {
        // Arrange
        var request = new IpcRequest { WindowId = windowId, PaneId = paneId, TabId = tabId };

        // Act
        int index = IpcRouting.Resolve(request, Windows, 0, out string? error);

        // Assert
        await Assert.That(index).IsEqualTo(-1);
        await Assert.That(error).IsEqualTo(expectedError);
    }

    [Test]
    [Arguments("send-text", 1, 1)]
    [Arguments("", -1, 0)]
    public async Task OtherwiseTheFocusedWindow(string command, int focused, int expected)
    {
        // Arrange
        var request = new IpcRequest { Command = command };

        // Act
        int index = Route(request, focused);

        // Assert
        await Assert.That(index).IsEqualTo(expected);
    }

    [Test]
    public async Task NoWindowsIsAnError()
    {
        // Act
        int index = IpcRouting.Resolve(new IpcRequest(), [], 0, out string? error);

        // Assert
        await Assert.That(index).IsEqualTo(-1);
        await Assert.That(error).IsEqualTo("no windows are open");
    }

    [Test]
    public async Task CliNewWindowFlag()
    {
        // Act
        var spawn = CliParser.Parse(["spawn", "--new-window", "--cwd", "/tmp"]).Request;

        // Assert
        await Assert.That(spawn.NewWindow).IsTrue();
        await Assert.That(spawn.WindowId).IsNull();
    }

    [Test]
    public async Task CliWindowIdFlag()
    {
        // Act
        var into = CliParser.Parse(["spawn", "--window-id", "3", "--", "htop"]).Request;

        // Assert
        await Assert.That(into.WindowId).IsEqualTo(3);
        await Assert.That(into.NewWindow).IsFalse();
        await Assert.That(into.Argv!).IsEquivalentTo(new[] { "htop" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CliWindowIdWithEqualsSign()
    {
        // Act
        int? windowId = CliParser.Parse(["activate-window", "--window-id=4"]).Request.WindowId;

        // Assert
        await Assert.That(windowId).IsEqualTo(4);
    }

    [Test]
    [Arguments("activate-window")]
    [Arguments("spawn --new-window --window-id 2")]
    [Arguments("split-pane --new-window")]
    public async Task CliRejectsMisusedWindowFlags(string commandLine)
    {
        // Arrange
        string[] args = commandLine.Split(' ');

        // Act
        var act = () => CliParser.Parse(args);

        // Assert
        await Assert.That(act).ThrowsExactly<CliUsageException>();
    }

    [Test]
    public async Task WindowFieldsOnTheWire()
    {
        // Arrange
        var r = new IpcRequest { Command = "new-window", WindowId = 2, NewWindow = true, ConfigFile = "/c.toml", Overrides = ["font_size=12"], WindowClass = "dropdown" };

        // Act
        string line = IpcJson.Encode(r);
        var back = IpcJson.DecodeRequest(line)!;

        // Assert
        await Assert.That(line).Contains("\"window_id\":2");
        await Assert.That(line).Contains("\"new_window\":true");
        await Assert.That(line).Contains("\"window_class\":\"dropdown\"");
        await Assert.That(back.Overrides!).IsEquivalentTo(new[] { "font_size=12" }, CollectionOrdering.Matching);
        await Assert.That(back.ConfigFile).IsEqualTo("/c.toml");
    }

    [Test]
    public async Task ResponseWindowIdOnTheWire()
    {
        // Arrange
        var response = new IpcResponse { Ok = true, WindowId = 5, PaneId = 9 };

        // Act
        var resp = IpcJson.DecodeResponse(IpcJson.Encode(response))!;

        // Assert
        await Assert.That(resp.WindowId).IsEqualTo(5);
    }

    [Test]
    public async Task ListShowsWindowIds()
    {
        // Act
        string table = CliMain.FormatTable([new PaneInfo { WindowId = 12, TabId = 3, PaneId = 4, Title = "zsh" }]);

        // Assert
        await Assert.That(table).StartsWith("WINID");
        await Assert.That(table).Contains("   12     3      4 default");
    }

    [Test]
    public async Task SingleInstanceIsOnByDefault()
    {
        // Act
        bool singleInstance = SettingsLoader.Parse("").Settings.SingleInstance;

        // Assert
        await Assert.That(singleInstance).IsTrue();
    }

    [Test]
    public async Task SingleInstanceSetting()
    {
        // Act
        var r = SettingsLoader.Parse("single_instance = false");

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.SingleInstance).IsFalse();
    }
}
