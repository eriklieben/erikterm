using System.Net.Sockets;
using System.Text;
using ErikTerm.App.Notifications;

namespace ErikTerm.Tests;

public class HyprlandParsingTests
{
    private const string ClientsJson = """
        [{
            "address": "0x560cda64bab0",
            "mapped": true,
            "hidden": false,
            "at": [0, 0],
            "size": [100, 100],
            "workspace": { "id": 2, "name": "2" },
            "floating": false,
            "class": "erikterm",
            "title": "first \"window\"",
            "initialClass": "erikterm",
            "pid": 3780,
            "grouped": [],
            "tags": []
        },{
            "address": "0x560cda6604f0",
            "mapped": false,
            "workspace": { "id": -1, "name": "" },
            "class": "other",
            "title": "",
            "pid": 12
        }]
        """;

    [Test]
    public async Task ClientsKeepTheFieldsThatMatter()
    {
        // Act
        var clients = HyprlandWorkspaces.ParseClients(ClientsJson);

        // Assert
        await Assert.That(clients!.Count).IsEqualTo(2);
        await Assert.That(clients[0]).IsEqualTo(new HyprlandClient(3780, "erikterm", "first \"window\"", true, new HyprlandWorkspace(2, "2")));
        await Assert.That(clients[1].Mapped).IsFalse();
    }

    [Test]
    public async Task AClientWithoutAMappedFieldCountsAsMapped()
    {
        // Arrange
        const string json = """[{"workspace":{"id":1,"name":"1"},"class":"a","title":"t","pid":1}]""";

        // Act
        var clients = HyprlandWorkspaces.ParseClients(json);

        // Assert
        await Assert.That(clients![0].Mapped).IsTrue();
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("""[{"workspace":{"id":1,"name":"1"},"class":"a","title":"t"}]""")]
    [Arguments("""[{"workspace":{"id":"1","name":"1"},"class":"a","title":"t","pid":1}]""")]
    [Arguments("""[{"workspace":7,"class":"a","title":"t","pid":1}]""")]
    [Arguments("[1]")]
    public async Task ClientsThatCantBeReadGiveNothing(string json)
    {
        // Act
        var clients = HyprlandWorkspaces.ParseClients(json);

        // Assert
        await Assert.That(clients).IsNull();
    }

    [Test]
    public async Task TheActiveWorkspaceIsReadFromItsObject()
    {
        // Arrange
        const string json = """{"id":2,"name":"web","monitor":"DP-1","windows":3,"lastwindowtitle":"x"}""";

        // Act
        var workspace = HyprlandWorkspaces.ParseActiveWorkspace(json);

        // Assert
        await Assert.That(workspace).IsEqualTo(new HyprlandWorkspace(2, "web"));
    }

    [Test]
    [Arguments("unknown request")]
    [Arguments("[]")]
    [Arguments("""{"name":"web"}""")]
    public async Task AnActiveWorkspaceThatCantBeReadGivesNothing(string json)
    {
        // Act
        var workspace = HyprlandWorkspaces.ParseActiveWorkspace(json);

        // Assert
        await Assert.That(workspace).IsNull();
    }

    [Test]
    [RunOn(OS.Windows | OS.MacOs)]
    public async Task OtherPlatformsHaveNoHyprlandSocket()
    {
        // Act
        string? path = HyprlandWorkspaces.SocketPath(name => name == "HYPRLAND_INSTANCE_SIGNATURE" ? "abc" : "/run/user/1000");

        // Assert
        await Assert.That(path).IsNull();
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task TheSocketPathNeedsTheInstanceSignatureAndARuntimeDirectory()
    {
        // Arrange
        static Func<string, string?> Env(string? signature, string? runtime) =>
            name => name == "HYPRLAND_INSTANCE_SIGNATURE" ? signature : name == "XDG_RUNTIME_DIR" ? runtime : null;

        // Act
        string? set = HyprlandWorkspaces.SocketPath(Env("abc", "/run/user/1000"));
        string? noSignature = HyprlandWorkspaces.SocketPath(Env(null, "/run/user/1000"));
        string? noRuntime = HyprlandWorkspaces.SocketPath(Env("abc", ""));
        string? pathInSignature = HyprlandWorkspaces.SocketPath(Env("../x", "/run/user/1000"));
        string? tooLong = HyprlandWorkspaces.SocketPath(Env("abc", "/" + new string('r', 200)));

        // Assert
        await Assert.That(set).IsEqualTo("/run/user/1000/hypr/abc/.socket.sock");
        await Assert.That(noSignature).IsNull();
        await Assert.That(noRuntime).IsNull();
        await Assert.That(pathInSignature).IsNull();
        await Assert.That(tooLong).IsNull();
    }
}

public class HyprlandMatchingTests
{
    private const int Me = 100, Stranger = 200;
    private const string Class = "erikterm";
    private static readonly HyprlandWorkspace W2 = new(2, "2"), W3 = new(3, "3");

    private static HyprlandClient Client(string title, HyprlandWorkspace workspace, int pid = Me, string cls = Class, bool mapped = true) =>
        new(pid, cls, title, mapped, workspace);

    private static HyprlandWindowIdentity Window(string title, int windows = 2) => new(Me, Class, title, windows);

    [Test]
    public async Task TheTitleTellsTwoWindowsOfOneProcessApart()
    {
        // Arrange
        HyprlandClient[] clients = [Client("build", W2), Client("logs", W3)];

        // Act
        var found = HyprlandWorkspaces.Find(clients, Window("logs"));

        // Assert
        await Assert.That(found).IsEqualTo(W3);
    }

    [Test]
    public async Task EqualTitlesOnDifferentWorkspacesAreAmbiguous()
    {
        // Arrange
        HyprlandClient[] clients = [Client("zsh", W2), Client("zsh", W3)];

        // Act
        var found = HyprlandWorkspaces.Find(clients, Window("zsh"));

        // Assert
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task EqualTitlesOnOneWorkspaceStillNameIt()
    {
        // Arrange
        HyprlandClient[] clients = [Client("zsh", W3), Client("zsh", W3), Client("other", W2)];

        // Act
        var found = HyprlandWorkspaces.Find(clients, Window("zsh", windows: 3));

        // Assert
        await Assert.That(found).IsEqualTo(W3);
    }

    [Test]
    public async Task NoMatchingTitleGivesNothing()
    {
        // Arrange
        HyprlandClient[] clients = [Client("build", W2), Client("logs", W3)];

        // Act
        var found = HyprlandWorkspaces.Find(clients, Window("just renamed"));

        // Assert
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task AnotherProcessesWindowWithTheSameTitleDoesntCount()
    {
        // Arrange
        HyprlandClient[] clients = [Client("zsh", W2), Client("zsh", W3, pid: Stranger), Client("x", W3)];

        // Act
        var found = HyprlandWorkspaces.Find(clients, Window("zsh"));

        // Assert
        await Assert.That(found).IsEqualTo(W2);
    }

    [Test]
    public async Task AClientOfAnotherClassDoesntCount()
    {
        // Arrange
        HyprlandClient[] clients = [Client("zsh", W2), Client("zsh", W3, cls: "dropdown")];

        // Act
        var found = HyprlandWorkspaces.Find(clients, Window("zsh"));

        // Assert
        await Assert.That(found).IsEqualTo(W2);
    }

    [Test]
    public async Task AnUnmappedClientDoesntCount()
    {
        // Arrange
        HyprlandClient[] clients = [Client("zsh", W2), Client("zsh", W3, mapped: false)];

        // Act
        var found = HyprlandWorkspaces.Find(clients, Window("zsh"));

        // Assert
        await Assert.That(found).IsEqualTo(W2);
    }

    [Test]
    public async Task AProcessWithOneWindowNeedsNoTitle()
    {
        // Arrange
        HyprlandClient[] clients = [Client("the compositor has an older title", W3)];

        // Act
        var found = HyprlandWorkspaces.Find(clients, Window("newer title", windows: 1));

        // Assert
        await Assert.That(found).IsEqualTo(W3);
    }

    [Test]
    public async Task TwoOwnWindowsButOneClientStillNeedsTheTitle()
    {
        // Arrange
        HyprlandClient[] clients = [Client("the other window", W3)];

        // Act
        var found = HyprlandWorkspaces.Find(clients, Window("this window", windows: 2));

        // Assert
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task AnEmptyTitleIsNeverMatchedAmongSeveralWindows()
    {
        // Arrange
        HyprlandClient[] clients = [Client("", W3), Client("x", W2)];

        // Act
        var found = HyprlandWorkspaces.Find(clients, Window(""));

        // Assert
        await Assert.That(found).IsNull();
    }

    [Test]
    public async Task AWindowOnTheActiveWorkspaceIsNotElsewhere()
    {
        // Arrange
        var snapshot = new HyprlandSnapshot([Client("build", W2), Client("logs", W3)], W2);

        // Act
        var elsewhere = HyprlandWorkspaces.Elsewhere(snapshot, Window("build"));

        // Assert
        await Assert.That(elsewhere).IsNull();
    }

    [Test]
    public async Task AWindowOnAnotherWorkspaceIsElsewhere()
    {
        // Arrange
        var snapshot = new HyprlandSnapshot([Client("build", W2), Client("logs", W3)], W2);

        // Act
        var elsewhere = HyprlandWorkspaces.Elsewhere(snapshot, Window("logs"));

        // Assert
        await Assert.That(elsewhere).IsEqualTo(W3);
    }

    [Test]
    public async Task AnUnknownActiveWorkspaceMentionsNothing()
    {
        // Arrange
        var snapshot = new HyprlandSnapshot([Client("logs", W3)], new HyprlandWorkspace(-1, ""));

        // Act
        var elsewhere = HyprlandWorkspaces.Elsewhere(snapshot, Window("logs"));

        // Assert
        await Assert.That(elsewhere).IsNull();
    }

    [Test]
    public async Task SnapshotsCompareByWhatTheySay()
    {
        // Arrange
        var a = new HyprlandSnapshot([Client("logs", W3)], W2);
        var same = new HyprlandSnapshot([Client("logs", W3)], W2);
        var moved = new HyprlandSnapshot([Client("logs", W2)], W2);

        // Act
        bool equal = HyprlandSnapshot.Same(a, same);
        bool different = HyprlandSnapshot.Same(a, moved);
        bool oneMissing = HyprlandSnapshot.Same(a, null);
        bool bothMissing = HyprlandSnapshot.Same(null, null);

        // Assert
        await Assert.That(equal).IsTrue();
        await Assert.That(different).IsFalse();
        await Assert.That(oneMissing).IsFalse();
        await Assert.That(bothMissing).IsTrue();
    }
}

public class HyprlandWordingTests
{
    [Test]
    [Arguments(3, "3", "workspace 3")]
    [Arguments(5, "dev", "workspace dev")]
    [Arguments(-1337, "notes", "workspace notes")]
    [Arguments(4, "", "workspace 4")]
    [Arguments(-99, "special", "special workspace")]
    [Arguments(-98, "special:term", "special workspace term")]
    [Arguments(-98, "special:", "special workspace")]
    public async Task AWorkspaceIsDescribedByItsNameOrNumber(int id, string name, string expected)
    {
        // Act
        string? text = HyprlandWorkspaces.Describe(new HyprlandWorkspace(id, name));

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments(-1, "")]
    [Arguments(-1337, "")]
    [Arguments(-1, "ghost")]
    public async Task AWorkspaceWithoutAUsableNameIsNotDescribed(int id, string name)
    {
        // Act
        string? text = HyprlandWorkspaces.Describe(new HyprlandWorkspace(id, name));

        // Assert
        await Assert.That(text).IsNull();
    }

    [Test]
    public async Task ControlCharactersAndBidiMarksAreRemovedFromTheName()
    {
        // Arrange
        var workspace = new HyprlandWorkspace(5, "de\u001b[31mv\u202e\n");

        // Act
        string? text = HyprlandWorkspaces.Describe(workspace);

        // Assert
        await Assert.That(text).IsEqualTo("workspace de [31mv");
    }

    [Test]
    public async Task ALongNameIsCapped()
    {
        // Arrange
        var workspace = new HyprlandWorkspace(5, new string('n', 200));

        // Act
        string? text = HyprlandWorkspaces.Describe(workspace);

        // Assert
        await Assert.That(text!.Length).IsLessThanOrEqualTo("workspace ".Length + 32);
    }

    [Test]
    public async Task TheDesktopTitleAddsTheWorkspace()
    {
        // Act
        string title = HyprlandWorkspaces.DesktopTitle("Claude needs permission", new HyprlandWorkspace(3, "3"));

        // Assert
        await Assert.That(title).IsEqualTo("Claude needs permission, workspace 3");
    }

    [Test]
    public async Task MarkupInAWorkspaceNameNeverReachesTheDesktopTitle()
    {
        // Act
        string title = HyprlandWorkspaces.DesktopTitle("Done", new HyprlandWorkspace(5, "<b>a&amp;b</b>"));

        // Assert
        await Assert.That(title).IsEqualTo("Done, workspace baamp;b/b");
    }

    [Test]
    public async Task ANameThatIsOnlyMarkupFallsBackToTheNumber()
    {
        // Act
        string title = HyprlandWorkspaces.DesktopTitle("Done", new HyprlandWorkspace(5, "<>&"));

        // Assert
        await Assert.That(title).IsEqualTo("Done, workspace 5");
    }

    [Test]
    public async Task ANameThatLeavesNothingLeavesTheTitleAlone()
    {
        // Act
        string title = HyprlandWorkspaces.DesktopTitle("Done", new HyprlandWorkspace(-1337, "<>"));

        // Assert
        await Assert.That(title).IsEqualTo("Done");
    }

    [Test]
    [Arguments(3, "3", "Workspace 3")]
    [Arguments(5, "dev", "Workspace dev")]
    [Arguments(-98, "special:term", "Special workspace term")]
    public async Task TheInWindowLabelStartsWithACapital(int id, string name, string expected)
    {
        // Act
        string? label = HyprlandWorkspaces.Label(new HyprlandWorkspace(id, name));

        // Assert
        await Assert.That(label).IsEqualTo(expected);
    }
}

[NotInParallel]
public class HyprlandSocketTests : IDisposable
{
    private const string Clients = """[{"mapped":true,"workspace":{"id":3,"name":"3"},"class":"erikterm","title":"t","pid":1}]""";
    private const string Active = """{"id":2,"name":"2"}""";

    /// <summary>Long enough for a busy CI runner: these tests check what is asked and answered, not how fast.</summary>
    private const int ReplyTimeoutMs = 15_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "et-hy-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _requests = [];
    private Socket? _listener;

    private string Path_ => Path.Combine(_dir, "s");

    private void Serve(Func<string, string?> answer)
    {
        Directory.CreateDirectory(_dir);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(Path_));
        _listener.Listen(4);
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptAsync();
                    var buffer = new byte[256];
                    int n = await client.ReceiveAsync(buffer, SocketFlags.None);
                    string request = Encoding.ASCII.GetString(buffer, 0, n);
                    lock (_requests) _requests.Add(request);
                    if (answer(request) is not { } reply) continue; // stays silent, the connection open
                    await client.SendAsync(Encoding.UTF8.GetBytes(reply), SocketFlags.None);
                    // Shut down before closing: on Windows a bare close can drop the reply still in flight, and the
                    // client then reads a cut-off answer (this test then saw two "changes" for two equal answers).
                    client.Shutdown(SocketShutdown.Send);
                    client.Dispose();
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { /* the test is over */ }
        });
    }

    public void Dispose()
    {
        _listener?.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Test]
    public async Task OnlyTheTwoReadOnlyRequestsAreSent()
    {
        // Arrange
        Serve(r => r == "j/clients" ? Clients : Active);
        var sut = new HyprlandWorkspaces(Path_, timeoutMs: ReplyTimeoutMs);

        // Act
        var snapshot = await sut.FetchAsync();

        // Assert
        await Assert.That(_requests).IsEquivalentTo(new[] { "j/clients", "j/activeworkspace" });
        await Assert.That(snapshot!.Active).IsEqualTo(new HyprlandWorkspace(2, "2"));
        await Assert.That(snapshot.Clients[0].Workspace).IsEqualTo(new HyprlandWorkspace(3, "3"));
        await Assert.That(sut.Latest).IsSameReferenceAs(snapshot);
    }

    [Test]
    public async Task AMissingSocketGivesNothing()
    {
        // Arrange
        var sut = new HyprlandWorkspaces(Path_, timeoutMs: ReplyTimeoutMs);

        // Act
        var snapshot = await sut.FetchAsync();

        // Assert
        await Assert.That(snapshot).IsNull();
    }

    [Test]
    public async Task ASilentCompositorIsGivenUpOnAtTheTimeout()
    {
        // Arrange
        Serve(_ => null);
        var sut = new HyprlandWorkspaces(Path_, timeoutMs: 50);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Act
        var snapshot = await sut.FetchAsync();

        // Assert
        await Assert.That(snapshot).IsNull();
        await Assert.That(clock.ElapsedMilliseconds).IsLessThan(2000);
    }

    [Test]
    public async Task AnAnswerThatDoesntParseGivesNothing()
    {
        // Arrange
        Serve(_ => "unknown request");
        var sut = new HyprlandWorkspaces(Path_, timeoutMs: ReplyTimeoutMs);

        // Act
        var snapshot = await sut.FetchAsync();

        // Assert
        await Assert.That(snapshot).IsNull();
    }

    [Test]
    public async Task ChangesAreReportedOnceAndAFailureClearsTheLastAnswer()
    {
        // Arrange
        bool fail = false;
        int changes = 0;
        Serve(r => fail ? "nope" : r == "j/clients" ? Clients : Active);
        var sut = new HyprlandWorkspaces(Path_, () => changes++, timeoutMs: ReplyTimeoutMs);

        // Act
        await sut.FetchAsync();
        await sut.FetchAsync();
        int afterTwoEqualAnswers = changes;
        fail = true;
        await sut.FetchAsync();

        // Assert
        await Assert.That(afterTwoEqualAnswers).IsEqualTo(1);
        await Assert.That(changes).IsEqualTo(2);
        await Assert.That(sut.Latest).IsNull();
    }

    [Test]
    public async Task WithoutASocketPathItIsDisabledAndRefreshDoesNothing()
    {
        // Arrange
        var sut = new HyprlandWorkspaces(null);

        // Act
        sut.Refresh(1000);

        // Assert
        await Assert.That(sut.Enabled).IsFalse();
        await Assert.That(sut.Latest).IsNull();
    }
}
