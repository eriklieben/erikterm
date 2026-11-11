using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using ErikTerm.App.Cli;
using ErikTerm.App.Ipc;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class CliParserTests
{
    private static IpcRequest Req(params string[] args) => CliParser.Parse(args).Request;

    [Test]
    public async Task SplitPaneDefaultsToBottom()
    {
        // Act
        var r = Req("split-pane");

        // Assert
        await Assert.That(r.Command).IsEqualTo("split-pane");
        await Assert.That(r.Direction).IsEqualTo("Down");
        await Assert.That(r.Percent).IsNull();
        await Assert.That(r.Argv).IsNull();
    }

    [Test]
    public async Task SplitPaneFlagsAndProgram()
    {
        // Act
        var r = Req("split-pane", "--right", "--percent", "30", "--pane-id=4", "--", "htop", "-d", "10");

        // Assert
        await Assert.That(r.Direction).IsEqualTo("Right");
        await Assert.That(r.Percent).IsEqualTo(30);
        await Assert.That(r.PaneId).IsEqualTo(4);
        await Assert.That(r.Argv!).IsEquivalentTo(["htop", "-d", "10"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("--left", "Left")]
    [Arguments("--top", "Up")]
    [Arguments("--horizontal", "Right")]
    public async Task SplitPaneDirectionFlags(string flag, string expected)
    {
        // Act
        var r = Req("split-pane", flag);

        // Assert
        await Assert.That(r.Direction).IsEqualTo(expected);
    }

    [Test]
    public async Task CwdIsMadeAbsolute()
    {
        // Arrange
        string expected = Path.GetFullPath(".");

        // Act
        var r = Req("spawn", "--cwd", ".");

        // Assert
        await Assert.That(r.Cwd).IsEqualTo(expected);
    }

    [Test]
    public async Task FlagsMayFollowPositionals()
    {
        // Act
        var inv = CliParser.Parse(["send-text", "echo hi\n", "--no-paste"]);

        // Assert
        await Assert.That(inv.Request.NoPaste).IsTrue();
        await Assert.That(inv.Request.Text).IsEqualTo("echo hi\n");
        await Assert.That(inv.ReadStdin).IsFalse();
    }

    [Test]
    public async Task SendTextWithoutTextReadsStdin()
    {
        // Act
        var inv = CliParser.Parse(["send-text"]);

        // Assert
        await Assert.That(inv.ReadStdin).IsTrue();
    }

    [Test]
    [Arguments("activate-pane-direction", "left", "Left")]
    [Arguments("get-pane-direction", "DOWN", "Down")]
    public async Task DirectionsIgnoreCase(string command, string direction, string expected)
    {
        // Act
        var r = Req(command, direction);

        // Assert
        await Assert.That(r.Direction).IsEqualTo(expected);
    }

    [Test]
    public async Task AdjustPaneSizeTakesAnAmount()
    {
        // Act
        var adj = Req("adjust-pane-size", "--amount", "5", "Right");

        // Assert
        await Assert.That((adj.Direction, adj.Amount)).IsEqualTo(("Right", 5));
    }

    [Test]
    public async Task AdjustPaneSizeDefaultsToOneCell()
    {
        // Act
        var adj = Req("adjust-pane-size", "Up");

        // Assert
        await Assert.That(adj.Amount).IsEqualTo(1);
    }

    [Test]
    public async Task GetTextLinesMayBeNegative()
    {
        // Act
        var r = Req("get-text", "--start-line", "-50", "--end-line", "-1", "--escapes");

        // Assert
        await Assert.That((r.StartLine, r.EndLine, r.Escapes)).IsEqualTo((-50, -1, true));
    }

    [Test]
    public async Task ZoomPaneZoomsByDefault()
    {
        // Act
        var r = Req("zoom-pane");

        // Assert
        await Assert.That(r.Zoom).IsEqualTo("zoom");
    }

    [Test]
    public async Task ZoomPaneToggle()
    {
        // Act
        var r = Req("zoom-pane", "--toggle");

        // Assert
        await Assert.That(r.Zoom).IsEqualTo("toggle");
    }

    [Test]
    public async Task ActivateTabByIndexMayBeNegative()
    {
        // Act
        var r = Req("activate-tab", "--tab-index", "-1");

        // Assert
        await Assert.That(r.TabIndex).IsEqualTo(-1);
    }

    [Test]
    public async Task ActivateTabRelative()
    {
        // Act
        var r = Req("activate-tab", "--tab-relative", "1");

        // Assert
        await Assert.That(r.TabRelative).IsEqualTo(1);
    }

    [Test]
    public async Task SetTabTitle()
    {
        // Act
        var r = Req("set-tab-title", "logs");

        // Assert
        await Assert.That(r.Title).IsEqualTo("logs");
    }

    [Test]
    public async Task ListFormatAndSocket()
    {
        // Act
        var inv = CliParser.Parse(["--socket", "/tmp/x.sock", "list", "--format", "json"]);

        // Assert
        await Assert.That(inv.Socket).IsEqualTo("/tmp/x.sock");
        await Assert.That(inv.Format).IsEqualTo("json");
    }

    [Test]
    public async Task ListFormatDefaultsToTable()
    {
        // Act
        var inv = CliParser.Parse(["list"]);

        // Assert
        await Assert.That(inv.Format).IsEqualTo("table");
    }

    [Test]
    [Arguments("frobnicate")]
    [Arguments("list", "--pane-id", "1")]
    [Arguments("list", "--format", "xml")]
    [Arguments("split-pane", "--left", "--right")]
    [Arguments("split-pane", "--percent", "abc")]
    [Arguments("split-pane", "--percent", "100")]
    [Arguments("activate-pane-direction")]
    [Arguments("activate-pane-direction", "Sideways")]
    [Arguments("activate-tab")]
    [Arguments("activate-tab", "--tab-index", "1", "--tab-relative", "1")]
    [Arguments("get-text", "--start-line")]
    [Arguments("kill-pane", "--bogus")]
    [Arguments("kill-pane", "--", "sh")]
    [Arguments("set-tab-title")]
    [Arguments("zoom-pane", "--zoom", "--unzoom")]
    public async Task RejectsBadUsage(params string[] args)
    {
        // Act
        var act = () => CliParser.Parse(args);

        // Assert
        await Assert.That(act).ThrowsExactly<CliUsageException>();
    }

    [Test]
    [Arguments]
    [Arguments("--help")]
    [Arguments("split-pane", "-h")]
    public async Task Help(params string[] args)
    {
        // Act
        var inv = CliParser.Parse(args);

        // Assert
        await Assert.That(inv.Help).IsTrue();
    }
}

public class IpcProtocolTests
{
    [Test]
    public async Task RequestRoundTrip()
    {
        // Arrange
        var r = new IpcRequest
        {
            Command = "split-pane", PaneId = 3, Direction = "Right", Percent = 30, Cwd = "/tmp",
            Argv = ["sh", "-c", "echo \"hi\"\n"], Text = "ünïcødé ✓ \e[1m",
        };

        // Act
        string line = IpcJson.Encode(r);
        var back = IpcJson.DecodeRequest(line)!;

        // Assert
        await Assert.That(line).DoesNotContain('\n'); // one request per line
        await Assert.That(line).Contains("\"pane_id\":3");
        await Assert.That(line).Contains("\"command\":\"split-pane\"");
        await Assert.That(line).DoesNotContain("tab_id"); // nulls are omitted
        await Assert.That(back.Command).IsEqualTo(r.Command);
        await Assert.That(back.PaneId).IsEqualTo(r.PaneId);
        await Assert.That(back.Direction).IsEqualTo(r.Direction);
        await Assert.That(back.Percent).IsEqualTo(r.Percent);
        await Assert.That(back.Argv!).IsEquivalentTo(r.Argv, CollectionOrdering.Matching);
        await Assert.That(back.Text).IsEqualTo(r.Text);
        await Assert.That(back.TabId).IsNull();
    }

    [Test]
    public async Task ResponseRoundTrip()
    {
        // Arrange
        var resp = new IpcResponse
        {
            Ok = true, PaneId = 7,
            Panes = [new PaneInfo { PaneId = 7, TabId = 1, Title = "zsh", Cwd = "file://h/home", Size = new PaneSize { Rows = 24, Cols = 80 }, IsActive = true }],
        };

        // Act
        var back = IpcJson.DecodeResponse(IpcJson.Encode(resp))!;

        // Assert
        await Assert.That(back.Ok).IsTrue();
        await Assert.That(back.PaneId).IsEqualTo(7);
        var p = await Assert.That(back.Panes!).HasSingleItem();
        await Assert.That((p!.PaneId, p.TabId, p.Title, p.Size.Cols, p.Size.Rows, p.IsActive)).IsEqualTo((7, 1, "zsh", 80, 24, true));
    }

    [Test]
    public async Task SnakeCaseFromOtherClients()
    {
        // Act
        var r = IpcJson.DecodeRequest("""{"command":"get-text","pane_id":2,"start_line":-5,"no_paste":true}""")!;

        // Assert
        await Assert.That((r.PaneId, r.StartLine, r.NoPaste)).IsEqualTo((2, -5, true));
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{\"command\":")]
    [Arguments("[1,2]")]
    public async Task MalformedIsNull(string line)
    {
        // Act
        var r = IpcJson.DecodeRequest(line);

        // Assert
        await Assert.That(r).IsNull();
    }

    [Test]
    public async Task TableLooksLikeWezterms()
    {
        // Arrange
        PaneInfo[] panes =
        [
            new PaneInfo { TabId = 1, PaneId = 1, Title = "zsh", Cwd = "file:///home", Size = new PaneSize { Cols = 80, Rows = 24 } },
            new PaneInfo { TabId = 1, PaneId = 12, Title = "nvim", Cwd = "", Size = new PaneSize { Cols = 100, Rows = 40 } },
        ];

        // Act
        string table = CliMain.FormatTable(panes);
        var lines = table.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Assert
        await Assert.That(lines.Length).IsEqualTo(3);
        await Assert.That(lines[0]).StartsWith("WINID TABID PANEID WORKSPACE SIZE   TITLE CWD");
        await Assert.That(lines[2]).IsEqualTo("    0     1     12 default   100x40 nvim");
    }
}

public class RequestQueueTests
{
    [Test]
    public async Task PostWakesTheMainThreadButRunsNothing()
    {
        // Arrange
        int wakes = 0;
        var q = new RequestQueue<int, string>(() => Interlocked.Increment(ref wakes));

        // Act
        var a = q.Post(1);
        _ = q.Post(2);

        // Assert
        await Assert.That(wakes).IsEqualTo(2);
        await Assert.That(a.IsCompleted).IsFalse(); // nothing runs until the main thread drains
    }

    [Test]
    public async Task DrainRunsRequestsInOrder()
    {
        // Arrange
        var q = new RequestQueue<int, string>(() => { });
        var a = q.Post(1);
        var b = q.Post(2);
        var seen = new List<int>();

        // Act
        int drained = q.Drain(x => { seen.Add(x); return "r" + x; });
        string resultA = await a;
        string resultB = await b;

        // Assert
        await Assert.That(drained).IsEqualTo(2);
        await Assert.That(seen).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
        await Assert.That(resultA).IsEqualTo("r1");
        await Assert.That(resultB).IsEqualTo("r2");
    }

    [Test]
    public async Task DrainingAnEmptyQueueRunsNothing()
    {
        // Arrange
        var q = new RequestQueue<int, string>(() => { });
        _ = q.Post(1);
        _ = q.Post(2);
        q.Drain(x => "r" + x);

        // Act
        int drained = q.Drain(_ => throw new InvalidOperationException());

        // Assert
        await Assert.That(drained).IsEqualTo(0);
    }

    [Test]
    public async Task HandlerExceptionFailsOnlyThatRequest()
    {
        // Arrange
        var q = new RequestQueue<int, int>(() => { });
        var bad = q.Post(0);
        var good = q.Post(5);

        // Act
        q.Drain(x => x == 0 ? throw new InvalidOperationException("boom") : x * 2);

        // Assert
        await Assert.That(() => bad).ThrowsExactly<InvalidOperationException>().WithMessage("boom");
        await Assert.That(await good).IsEqualTo(10);
    }

    [Test]
    public async Task CloseFailsPendingRequests()
    {
        // Arrange
        var q = new RequestQueue<int, int>(() => { });
        var pending = q.Post(1);

        // Act
        q.Close();

        // Assert
        await Assert.That(() => pending).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task PostingAfterCloseFailsAndQueuesNothing()
    {
        // Arrange
        var q = new RequestQueue<int, int>(() => { });
        _ = q.Post(1);
        q.Close();

        // Act
        var post = () => q.Post(2);

        // Assert
        await Assert.That(post).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(q.Count).IsEqualTo(0); // after the refused post: nothing left behind
    }

    [Test]
    public async Task ManyThreadsPosting()
    {
        // Arrange
        var q = new RequestQueue<int, int>(() => { });
        var posted = new Task<int>[200];

        // Act
        Parallel.For(0, 200, i => posted[i] = q.Post(i));
        int drained = q.Drain(x => -x);
        var results = await Task.WhenAll(posted);

        // Assert
        await Assert.That(drained).IsEqualTo(200);
        await Assert.That(results.Order()).IsEquivalentTo(Enumerable.Range(0, 200).Select(i => -i).Order(), CollectionOrdering.Matching);
    }
}

public class TextExportTests
{
    [Test]
    [Arguments(null, null, "two\nthree\nfour\n")]
    [Arguments(-1, 0, "one\ntwo\n")]
    [Arguments(-100, -1, "one\n")] // clamped to the scrollback that exists
    [Arguments(2, 1, "")]
    public async Task PlainScreenAndScrollback(int? startLine, int? endLine, string expected)
    {
        // Arrange
        var t = new Terminal(10, 3, 100);
        t.Feed("one\r\ntwo\r\nthree\r\nfour"u8);

        // Act
        string text = TextExport.Plain(t, startLine, endLine);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task EscapesCarryColorsAndAttributes()
    {
        // Arrange
        var t = new Terminal(20, 1, 0);
        t.Feed("a\e[1;31mb\e[0;38;2;1;2;3mc\e[0;44md\e[0m"u8);

        // Act
        string s = TextExport.WithEscapes(t, 0, 0);

        // Assert
        await Assert.That(s).IsEqualTo("a\e[0;1;31mb\e[0;38;2;1;2;3mc\e[0;44md\e[0m\n");
    }
}

/// <summary>A real socket server with a fake handler, driven through the CLI entry point — no window needed.</summary>
[NotInParallel(Isolation.Sockets)]
public class IpcEndToEndTests
{
    // Generous: a loaded machine can take a while to schedule the server's threads.
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(30);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "et-ipc-" + Guid.NewGuid().ToString("N")[..8]);
    private string Socket => Path.Combine(_dir, "erikterm-1.sock");

    [After(Test)]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Server + queue + a "main thread" that drains it, like App does. Dispose stops and removes it.</summary>
    private sealed class FakeErikterm : IDisposable
    {
        private readonly IpcServer _server;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<IpcRequest> _seen = [];

        public FakeErikterm(string socket, Func<IpcRequest, IpcResponse> handler)
        {
            var wake = new SemaphoreSlim(0);
            var queue = new RequestQueue<IpcRequest, IpcResponse>(() => wake.Release());
            _server = new IpcServer(socket, queue.Post);
            _server.Start();
            // The token, not the source: Dispose disposes the source, and the thread may only get to run after that.
            var stopping = _stop.Token;
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        wake.Wait(stopping);
                        queue.Drain(r => { lock (_seen) _seen.Add(r); return handler(r); });
                    }
                }
                catch (OperationCanceledException) { queue.Close(); }
            }) { IsBackground = true }.Start();
        }

        /// <summary>The requests handled so far, copied under the lock.</summary>
        public List<IpcRequest> Seen()
        {
            lock (_seen) return _seen.ToList();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _server.Dispose();
            _stop.Dispose();
        }
    }

    /// <summary>The handler the CLI round-trip tests talk to.</summary>
    private FakeErikterm StartWithPanes() => new(Socket, r => r.Command switch
    {
        "list" => new IpcResponse { Ok = true, Panes = [new PaneInfo { PaneId = 1, TabId = 1, Title = "sh" }] },
        "split-pane" => new IpcResponse { Ok = true, PaneId = 2 },
        "get-text" => new IpcResponse { Ok = true, Output = "hello\n" },
        "get-pane-direction" => new IpcResponse { Ok = true },
        "kill-pane" => IpcResponse.Failure("no pane with id 99"),
        _ => IpcResponse.Success(),
    });

    private Dictionary<string, string> PaneEnv() => new() { ["ERIKTERM_SOCKET"] = Socket, ["ERIKTERM_PANE"] = "1" };

    private static (int Code, string Out, string Err) Cli(string[] args, Dictionary<string, string> env, string stdin = "")
    {
        // WriteLine's line ending is the OS's (as in a console); the tests compare what was printed, on every OS.
        var o = new StringWriter { NewLine = "\n" };
        var e = new StringWriter { NewLine = "\n" };
        int code = CliMain.Run(args, new StringReader(stdin), o, e, k => env.GetValueOrDefault(k), CliTimeout);
        return (code, o.ToString(), e.ToString());
    }

    [Test]
    public async Task SplitPanePrintsTheNewPaneId()
    {
        // Arrange
        using var fake = StartWithPanes();

        // Act
        var split = Cli(["split-pane", "--right"], PaneEnv());

        // Assert
        await Assert.That(split).IsEqualTo((0, "2\n", ""));
    }

    [Test]
    public async Task ErikTermPaneIsTheDefaultTarget()
    {
        // Arrange
        using var fake = StartWithPanes();

        // Act
        Cli(["split-pane", "--right"], PaneEnv());
        var seen = fake.Seen();

        // Assert
        await Assert.That(seen[0].PaneId).IsEqualTo(1);
        await Assert.That(seen[0].Direction).IsEqualTo("Right");
    }

    [Test]
    public async Task PaneIdFlagOverridesTheDefaultTarget()
    {
        // Arrange
        using var fake = StartWithPanes();

        // Act
        Cli(["kill-pane", "--pane-id", "99"], PaneEnv());
        var seen = fake.Seen();

        // Assert
        await Assert.That(seen.Single(r => r.Command == "kill-pane").PaneId).IsEqualTo(99);
    }

    [Test]
    public async Task GetTextPrintsThePaneText()
    {
        // Arrange
        using var fake = StartWithPanes();

        // Act
        var result = Cli(["get-text"], PaneEnv());

        // Assert
        await Assert.That(result).IsEqualTo((0, "hello\n", ""));
    }

    [Test]
    public async Task GetPaneDirectionAtAnEdgePrintsNothing()
    {
        // Arrange
        using var fake = StartWithPanes();

        // Act
        var result = Cli(["get-pane-direction", "Left"], PaneEnv());

        // Assert
        await Assert.That(result).IsEqualTo((0, "", ""));
    }

    [Test]
    public async Task ListAsJson()
    {
        // Arrange
        using var fake = StartWithPanes();

        // Act
        var json = Cli(["list", "--format", "json"], PaneEnv());

        // Assert
        await Assert.That(json.Code).IsEqualTo(0);
        await Assert.That(json.Out).Contains("\"pane_id\": 1");
    }

    [Test]
    public async Task SendTextReadsStdin()
    {
        // Arrange
        using var fake = StartWithPanes();

        // Act
        var result = Cli(["send-text", "--no-paste"], PaneEnv(), stdin: "from stdin");
        var seen = fake.Seen();

        // Assert
        await Assert.That(result.Code).IsEqualTo(0);
        await Assert.That(seen.Single(r => r.Command == "send-text").Text).IsEqualTo("from stdin");
    }

    [Test]
    public async Task FailuresExitWithOneAndExplainOnStderr()
    {
        // Arrange
        using var fake = StartWithPanes();

        // Act
        var kill = Cli(["kill-pane", "--pane-id", "99"], PaneEnv());

        // Assert
        await Assert.That(kill.Code).IsEqualTo(1);
        await Assert.That(kill.Err).Contains("no pane with id 99");
    }

    [Test]
    public async Task SocketIsRemovedOnDispose()
    {
        // Arrange
        var fake = StartWithPanes();
        Cli(["get-text"], PaneEnv());

        // Act
        bool existedWhileServing = File.Exists(Socket);
        fake.Dispose();
        bool existsAfterDispose = File.Exists(Socket);

        // Assert
        await Assert.That(existedWhileServing).IsTrue();
        await Assert.That(existsAfterDispose).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task SocketIsPrivate()
    {
        // Arrange
        using var fake = new FakeErikterm(Socket, _ => IpcResponse.Success());

        // Act
        var socketMode = File.GetUnixFileMode(Socket);
        var dirMode = File.GetUnixFileMode(_dir);

        // Assert
        await Assert.That(socketMode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(dirMode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task MalformedLineGetsAnErrorAndConnectionStaysUsable(CancellationToken cancellationToken)
    {
        // Arrange
        using var fake = new FakeErikterm(Socket, _ => new IpcResponse { Ok = true, Output = "fine" });
        using var s = new System.Net.Sockets.Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Connect(new UnixDomainSocketEndPoint(Socket));
        using var stream = new NetworkStream(s);
        var reader = new LineReader(stream);

        // Act
        stream.Write("garbage\n{\"command\":\"list\"}\n"u8);
        var first = IpcJson.DecodeResponse((await reader.ReadLineAsync(cancellationToken))!)!;
        var second = IpcJson.DecodeResponse((await reader.ReadLineAsync(cancellationToken))!)!;

        // Assert
        await Assert.That(first.Ok).IsFalse();
        await Assert.That(first.Error).Contains("malformed");
        await Assert.That(second.Output).IsEqualTo("fine");
    }

    [Test]
    public async Task DiscoveryFindsTheLiveSocketAndCleansStaleOnes()
    {
        // Arrange
        using var fake = new FakeErikterm(Socket, _ => IpcResponse.Success());
        // What a crashed instance leaves behind: a socket nobody listens on, named after a pid that can't exist.
        string stale = Path.Combine(_dir, "erikterm-2147483646.sock");
        MakeStaleSocket(stale);
        bool staleExisted = File.Exists(stale);

        // Act
        string? discovered = IpcPaths.Discover(_dir);
        bool staleRemains = File.Exists(stale);

        // Assert
        await Assert.That(staleExisted).IsTrue();
        await Assert.That(discovered).IsEqualTo(Socket);
        await Assert.That(staleRemains).IsFalse();
    }

    /// <summary>A socket file nobody listens on: bound under another name and moved (.NET unlinks the bound path on close).</summary>
    internal static void MakeStaleSocket(string path)
    {
        string bound = path + ".tmp";
        using (var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
        {
            s.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(bound));
            File.Move(bound, path);
        }
    }

    [Test]
    public async Task DiscoveryPrefersTheMarker()
    {
        // Arrange
        using var fake = new FakeErikterm(Socket, _ => IpcResponse.Success());

        // Act
        IpcPaths.WriteMarker(_dir, Socket);
        string? marker = IpcPaths.ReadMarker(_dir);
        string? discovered = IpcPaths.Discover(_dir);

        // Assert
        await Assert.That(marker).IsEqualTo(Socket);
        await Assert.That(discovered).IsEqualTo(Socket);
    }

    [Test]
    public async Task ClearingTheMarkerRemovesIt()
    {
        // Arrange
        using var fake = new FakeErikterm(Socket, _ => IpcResponse.Success());
        IpcPaths.WriteMarker(_dir, Socket);

        // Act
        IpcPaths.ClearMarker(_dir, Socket);
        string? marker = IpcPaths.ReadMarker(_dir);

        // Assert
        await Assert.That(marker).IsNull();
    }

    [Test]
    [RunOn(OS.Linux)] // XDG_RUNTIME_DIR is Linux's: macOS uses its per-user temp directory, Windows the test below
    public async Task WithoutErikTermSocketTheCliLooksInTheRuntimeDirectory()
    {
        // Arrange
        // Without ERIKTERM_SOCKET the CLI discovers the instance (and ignores ERIKTERM_PANE).
        string runtimeDir = Path.GetDirectoryName(_dir)!;
        var env = new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = runtimeDir, ["ERIKTERM_PANE"] = "5" };

        // Act
        string dir = IpcPaths.Directory(k => env.GetValueOrDefault(k));

        // Assert
        await Assert.That(dir).IsEqualTo(Path.Combine(runtimeDir, "erikterm"));
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task OnWindowsTheCliLooksInLocalAppData()
    {
        // Arrange: XDG_RUNTIME_DIR means nothing on Windows.
        var env = new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = _dir };

        // Act
        string dir = IpcPaths.Directory(k => env.GetValueOrDefault(k));

        // Assert
        await Assert.That(dir).IsEqualTo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm"));
    }

    [Test]
    public async Task NothingRunning()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = _dir };

        // Act
        var r = Cli(["list"], env);

        // Assert
        await Assert.That(r.Code).IsEqualTo(1);
        await Assert.That(r.Err).Contains("no running erikterm");
    }

    [Test]
    public async Task MissingSocketFails()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = _dir };

        // Act
        var r = Cli(["--socket", Path.Combine(_dir, "nope.sock"), "list"], env);

        // Assert
        await Assert.That(r.Code).IsEqualTo(1);
    }

    [Test]
    public async Task LineReaderHandlesSplitsAndCrlf(CancellationToken cancellationToken)
    {
        // Arrange
        var ms = new MemoryStream(Encoding.UTF8.GetBytes("ab\r\ncd\n\nlast"));
        var r = new LineReader(ms);

        // Act
        var lines = new List<string?>();
        for (int i = 0; i < 5; i++) lines.Add(await r.ReadLineAsync(cancellationToken));

        // Assert
        await Assert.That(lines[0]).IsEqualTo("ab");
        await Assert.That(lines[1]).IsEqualTo("cd");
        await Assert.That(lines[2]).IsEqualTo("");
        await Assert.That(lines[3]).IsEqualTo("last");
        await Assert.That(lines[4]).IsNull();
    }
}
