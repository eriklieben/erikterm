using ErikTerm.App.Ipc;
using ErikTerm.App.Tmux;

namespace ErikTerm.Tests;

/// <summary>
/// The tmux commands the shim emulates, one at a time, against an in-memory erikterm (the shim's own seam for its
/// control requests): what each sends, prints and refuses. The socket path only has to exist as a file; nothing
/// listens on it. TmuxShimEndToEndTests runs the same shim over a real socket.
/// </summary>
public class TmuxShimCommandTests
{
    private readonly string _dir;
    private readonly string _socket;

    public TmuxShimCommandTests()
    {
        _dir = Directory.CreateTempSubdirectory("et-shim-").FullName;
        _socket = Path.Combine(_dir, "erikterm-4242.sock");
        File.WriteAllText(_socket, "");
    }

    [After(Test)]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class NoRealTmux : IRealTmux
    {
        public string? Path => null;
        public bool ServerRunning => false;
        public int Exec(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, TextWriter stderr) => 127;
        public int Run(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env) => 1;
    }

    /// <summary>Two tabs: panes 1 (focused) and 2 in tab 10, pane 3 in tab 20. New panes get the next id.</summary>
    private sealed class Erikterm
    {
        public readonly List<PaneInfo> Panes =
        [
            new() { TabId = 10, TabIndex = 0, PaneId = 1, IsActive = true, IsFocused = true, Size = new() { Cols = 80, Rows = 24 }, Title = "zsh" },
            new() { TabId = 10, TabIndex = 0, PaneId = 2, Size = new() { Cols = 80, Rows = 24 }, Title = "vim", CreatedByTmux = true },
            new() { TabId = 20, TabIndex = 1, PaneId = 3, IsActive = true, Size = new() { Cols = 80, Rows = 24 }, Title = "htop" },
        ];
        public readonly List<IpcRequest> Seen = [];
        public Exception? Throw;
        public string? Fail;
        private int _next = 4;

        public IpcResponse Handle(IpcRequest r)
        {
            if (Throw is { } ex) throw ex;
            Seen.Add(r);
            if (r.Command == "list") return new IpcResponse { Ok = true, Panes = [.. Panes] };
            if (Fail is { } f) return IpcResponse.Failure(f);
            switch (r.Command)
            {
                case "split-pane" or "spawn":
                {
                    var p = new PaneInfo { TabId = r.Command == "spawn" ? 30 : 10, TabIndex = r.Command == "spawn" ? 2 : 0, PaneId = _next++, Size = new() { Cols = 40, Rows = 24 }, Title = "sh", CreatedByTmux = true };
                    Panes.Add(p);
                    return new IpcResponse { Ok = true, PaneId = p.PaneId };
                }
                case "get-text": return new IpcResponse { Ok = true, Output = "text\n" };
                default: return IpcResponse.Success();
            }
        }

        public IpcRequest Last(string command) => Seen.Last(r => r.Command == command);
    }

    private readonly Erikterm _term = new();

    private Dictionary<string, string> Env(Dictionary<string, string>? extra = null)
    {
        var env = new Dictionary<string, string>
        {
            ["TMUX"] = $"{_socket},4242,0", ["TMUX_PANE"] = "%1", ["SHELL"] = "/bin/sh", ["PATH"] = "/usr/bin", ["HOME"] = "/home/u",
        };
        foreach (var (k, v) in extra ?? []) env[k] = v;
        return env;
    }

    private (int Code, string Out, string Err) Tmux(string line, string stdin = "", Dictionary<string, string>? env = null) =>
        Tmux(line.Split(' '), stdin, env);

    private (int Code, string Out, string Err) Tmux(string[] args, string stdin = "", Dictionary<string, string>? env = null)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = TmuxShim.Run(args, new StringReader(stdin), o, e, Env(env), _term.Handle, new NoRealTmux());
        return (code, o.ToString(), e.ToString());
    }

    // ---- New panes and windows -----------------------------------------------------------------------

    [Test]
    public async Task NewWindowOpensATabAndPrintsTheNewPane()
    {
        // Act
        var result = Tmux("new-window -d -n logs -P -F #{pane_id}");

        // Assert
        var spawn = _term.Last("spawn");
        await Assert.That(result).IsEqualTo((0, "%4\n", ""));
        await Assert.That((spawn.Title, spawn.NoFocus, spawn.Tmux)).IsEqualTo(("logs", true, true));
    }

    [Test]
    public async Task NewWindowAtAFreeIndexStillOpens()
    {
        // Act
        var result = Tmux("new-window -t :7");

        // Assert
        await Assert.That(result.Code).IsEqualTo(0);
        await Assert.That(_term.Seen.Any(r => r.Command == "spawn")).IsTrue();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // there, $SHELL must exist to be used
    public async Task OneArgumentIsAShellCommand()
    {
        // Act
        Tmux(["split-window", "-h", "make test"]);

        // Assert
        await Assert.That(_term.Last("split-pane").Argv!).IsEquivalentTo(["/bin/sh", "-c", "make test"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SeveralArgumentsAreTheArgv()
    {
        // Act
        Tmux("split-window -v htop -d 5");

        // Assert
        await Assert.That(_term.Last("split-pane").Argv!).IsEquivalentTo(["htop", "-d", "5"], CollectionOrdering.Matching);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task EnvironmentVariablesGoInFrontThroughEnv()
    {
        // Act
        Tmux("split-window -e FOO=bar -e X=1 htop");

        // Assert
        await Assert.That(_term.Last("split-pane").Argv!).IsEquivalentTo(["env", "FOO=bar", "X=1", "/bin/sh", "-c", "htop"], CollectionOrdering.Matching);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task EnvironmentVariablesWithoutACommandRunTheShell()
    {
        // Act
        Tmux("split-window -e FOO=bar");

        // Assert
        await Assert.That(_term.Last("split-pane").Argv!).IsEquivalentTo(["env", "FOO=bar", "/bin/sh"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("split-window -h", "Right")]
    [Arguments("split-window -hb", "Left")]
    [Arguments("split-window -v", "Down")]
    [Arguments("split-window -vb", "Up")]
    public async Task SplitDirections(string line, string direction)
    {
        // Act
        Tmux(line);

        // Assert
        await Assert.That(_term.Last("split-pane").Direction).IsEqualTo(direction);
    }

    [Test]
    public async Task SplitSizesInCellsOrPercent()
    {
        // Act
        Tmux("split-window -l 30%");
        int? percent = _term.Last("split-pane").Percent;
        Tmux("split-window -l 20");
        int? cells = _term.Last("split-pane").Cells;
        Tmux("split-window -p 40");

        // Assert
        await Assert.That(percent).IsEqualTo(30);
        await Assert.That(cells).IsEqualTo(20);
        await Assert.That(_term.Last("split-pane").Percent).IsEqualTo(40);
    }

    [Test]
    [Arguments("split-window -l lots", "split-window -l: invalid size: lots")]
    [Arguments("split-window -p 100", "split-window -p: invalid size: 100")]
    public async Task ABadSizeIsAUsageError(string line, string error)
    {
        // Act
        var result = Tmux(line);

        // Assert
        await Assert.That(result).IsEqualTo((1, "", error + "\n"));
    }

    [Test]
    public async Task SplitInputFromStdinIsNotSupported()
    {
        // Act
        var result = Tmux("split-window -I");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
        await Assert.That(result.Err).Contains("not supported by erikterm's tmux emulation");
    }

    [Test]
    public async Task AStartDirectoryIsResolved()
    {
        // Act
        Tmux($"split-window -c {_dir}");

        // Assert
        await Assert.That(_term.Last("split-pane").Cwd).IsEqualTo(Path.GetFullPath(_dir));
    }

    [Test]
    public async Task AMissingStartDirectoryIsAnError()
    {
        // Act
        var result = Tmux($"split-window -c {_dir}/nope");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
        await Assert.That(result.Err).Contains("No such file or directory");
        await Assert.That(_term.Seen.Any(r => r.Command == "split-pane")).IsFalse();
    }

    [Test]
    public async Task RespawnPaneKillsAndRunsAgain()
    {
        // Act
        var result = Tmux("respawn-pane -k -t %2 top");

        // Assert
        var r = _term.Last("respawn-pane");
        await Assert.That(result.Code).IsEqualTo(0);
        await Assert.That((r.PaneId, r.Kill)).IsEqualTo(((int?)2, true));
    }

    // ---- Keys and text -----------------------------------------------------------------------------

    [Test]
    [Arguments("send-keys -X cancel", "send-keys -X (copy mode)")]
    [Arguments("send-keys -M", "send-keys -M")]
    public async Task CopyModeAndMouseKeysAreNotSupported(string line, string what)
    {
        // Act
        var result = Tmux(line);

        // Assert
        await Assert.That(result.Err).IsEqualTo($"{what} (not supported by erikterm's tmux emulation)\n");
    }

    [Test]
    public async Task SendKeysRepeats()
    {
        // Act
        Tmux("send-keys -N 3 -l ab");

        // Assert
        await Assert.That(_term.Last("send-text").Text).IsEqualTo("ababab");
    }

    [Test]
    public async Task SendKeysTakesHex()
    {
        // Act
        Tmux("send-keys -H 68 69");

        // Assert
        await Assert.That(_term.Last("send-text").Text).IsEqualTo("hi");
    }

    [Test]
    public async Task SendKeysWithResetResetsTheTerminalFirst()
    {
        // Act
        Tmux("send-keys -R -l x");

        // Assert
        await Assert.That(_term.Last("send-text").Text).IsEqualTo("\ecx");
    }

    [Test]
    public async Task SendingNothingSendsNothing()
    {
        // Act
        var result = Tmux("send-keys -l");

        // Assert
        await Assert.That(result.Code).IsEqualTo(0);
        await Assert.That(_term.Seen.Any(r => r.Command == "send-text")).IsFalse();
    }

    [Test]
    public async Task CapturingIntoABufferKeepsNothing()
    {
        // Act
        var result = Tmux("capture-pane -t %1");

        // Assert
        await Assert.That(result).IsEqualTo((0, "", ""));
        await Assert.That(_term.Seen.Any(r => r.Command == "get-text")).IsFalse();
    }

    [Test]
    public async Task CaptureTakesALineRangeAndEscapes()
    {
        // Act
        var result = Tmux("capture-pane -p -e -S -10 -E 5");

        // Assert
        var r = _term.Last("get-text");
        await Assert.That(result.Out).IsEqualTo("text\n");
        await Assert.That((r.StartLine, r.EndLine, r.Escapes)).IsEqualTo(((int?)-10, (int?)5, true));
    }

    // ---- display-message ------------------------------------------------------------------------------

    [Test]
    public async Task DisplayMessagePrintsTheFormatExpanded()
    {
        // Act
        var result = Tmux(["display-message", "-p", "-t", "%2", "#{pane_id} #{session_name}"]);

        // Assert
        await Assert.That(result).IsEqualTo((0, "%2 erikterm\n", ""));
    }

    [Test]
    public async Task DisplayMessageLiterallyPrintsTheFormatAsIs()
    {
        // Act
        var result = Tmux(["display-message", "-p", "-l", "#{pane_id}"]);

        // Assert
        await Assert.That(result.Out).IsEqualTo("#{pane_id}\n");
    }

    [Test]
    public async Task DisplayMessageWithoutPrintShowsNothing()
    {
        // Act
        var result = Tmux(["display-message", "-I", "hello"], stdin: "input");

        // Assert
        await Assert.That(result).IsEqualTo((0, "", ""));
    }

    [Test]
    public async Task DisplayMessageAllListsTheVariables()
    {
        // Act
        var result = Tmux("display-message -a");

        // Assert
        await Assert.That(result.Out).Contains("pane_id=%1\n");
        await Assert.That(result.Out).Contains("session_name=erikterm\n");
    }

    // ---- Listing ----------------------------------------------------------------------------------

    [Test]
    public async Task ListPanesListsTheCallersWindow()
    {
        // Act
        var result = Tmux(["list-panes", "-F", "#{pane_id}"]);

        // Assert
        await Assert.That(result.Out).IsEqualTo("%1\n%2\n");
    }

    [Test]
    public async Task ListPanesAllListsEveryWindow()
    {
        // Act
        var result = Tmux(["list-panes", "-a", "-F", "#{pane_id}"]);
        var session = Tmux(["list-panes", "-s", "-t", "erikterm", "-F", "#{pane_id}"]);

        // Assert
        await Assert.That(result.Out).IsEqualTo("%1\n%2\n%3\n");
        await Assert.That(session.Out).IsEqualTo("%1\n%2\n%3\n");
    }

    [Test]
    public async Task ListPanesFilters()
    {
        // Act
        var result = Tmux(["list-panes", "-a", "-F", "#{pane_id}", "-f", "#{pane_active}"]);

        // Assert
        await Assert.That(result.Out).IsEqualTo("%1\n%3\n");
    }

    [Test]
    public async Task ListPanesHasTmuxsDefaultFormat()
    {
        // Act
        var result = Tmux("list-panes");

        // Assert
        await Assert.That(result.Out).StartsWith("0: [80x24]");
        await Assert.That(result.Out).Contains("%1 (active)");
    }

    [Test]
    public async Task ListWindowsListsTheTabs()
    {
        // Act
        var result = Tmux(["list-windows", "-F", "#{window_index}:#{window_panes}"]);
        var all = Tmux(["list-windows", "-a"]);

        // Assert
        await Assert.That(result.Out).IsEqualTo("0:2\n1:1\n");
        await Assert.That(all.Out).StartsWith("erikterm:0: ");
    }

    [Test]
    public async Task ListSessionsAndClientsAreTheOneSession()
    {
        // Act
        var sessions = Tmux("list-sessions");
        var clients = Tmux(["list-clients", "-F", "#{session_name}"]);

        // Assert
        await Assert.That(sessions.Out).StartsWith("erikterm: 2 windows");
        await Assert.That(clients.Out).IsEqualTo("erikterm\n");
    }

    // ---- select-pane ------------------------------------------------------------------------------

    [Test]
    public async Task SelectPaneTitleSetsIt()
    {
        // Act
        Tmux(["select-pane", "-t", "%2", "-T", "build #{pane_id}"]);

        // Assert
        var r = _term.Last("set-pane-title");
        await Assert.That((r.PaneId, r.Title)).IsEqualTo(((int?)2, "build %2"));
    }

    [Test]
    [Arguments("select-pane -P bg=red")]
    [Arguments("select-pane -g")]
    [Arguments("select-pane -d")]
    [Arguments("select-pane -m")]
    public async Task StylesInputAndMarksChangeNothing(string line)
    {
        // Act
        var result = Tmux(line);

        // Assert
        await Assert.That(result.Code).IsEqualTo(0);
        await Assert.That(_term.Seen.Count(r => r.Command != "list")).IsEqualTo(0);
    }

    [Test]
    public async Task SelectPaneLastIsNotSupported()
    {
        // Act
        var result = Tmux("select-pane -l");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
    }

    [Test]
    [Arguments("select-pane -L", "Left")]
    [Arguments("select-pane -R", "Right")]
    [Arguments("select-pane -U", "Up")]
    [Arguments("select-pane -D", "Down")]
    public async Task SelectPaneDirections(string line, string direction)
    {
        // Act
        Tmux(line);

        // Assert
        await Assert.That(_term.Last("activate-pane-direction").Direction).IsEqualTo(direction);
    }

    [Test]
    public async Task SelectPaneActivatesIt()
    {
        // Act
        Tmux("select-pane -t %2");

        // Assert
        await Assert.That(_term.Last("activate-pane").PaneId).IsEqualTo(2);
    }

    // ---- Killing ------------------------------------------------------------------------------------

    [Test]
    public async Task KillPaneAllKillsTheOthersInItsWindow()
    {
        // Act
        Tmux("kill-pane -a -t %1");

        // Assert
        await Assert.That(_term.Seen.Where(r => r.Command == "kill-pane").Select(r => r.PaneId)).IsEquivalentTo([(int?)2]);
    }

    [Test]
    public async Task KillWindowKillsItsPanes()
    {
        // Act
        Tmux("kill-window -t :1");

        // Assert
        await Assert.That(_term.Seen.Where(r => r.Command == "kill-pane").Select(r => r.PaneId)).IsEquivalentTo([(int?)3]);
    }

    [Test]
    public async Task KillWindowAllKillsTheOtherWindows()
    {
        // Act
        Tmux("kill-window -a -t :1");

        // Assert
        await Assert.That(_term.Seen.Where(r => r.Command == "kill-pane").Select(r => r.PaneId)).IsEquivalentTo([(int?)1, 2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task KillSessionOfErikTermIsRefused()
    {
        // Act
        var result = Tmux("kill-session -t erikterm");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
        await Assert.That(result.Err).Contains("close the window instead");
    }

    // ---- Resizing and layouts ------------------------------------------------------------------------

    [Test]
    public async Task ResizePaneZoomTogglesZoom()
    {
        // Act
        Tmux("resize-pane -Z -t %2");

        // Assert
        await Assert.That(_term.Last("zoom-pane").Zoom).IsEqualTo("toggle");
    }

    [Test]
    public async Task ResizePaneByADirection()
    {
        // Act
        Tmux("resize-pane -L 5");
        int? five = _term.Last("adjust-pane-size").Amount;
        Tmux("resize-pane -D");

        // Assert
        await Assert.That(five).IsEqualTo(5);
        await Assert.That(_term.Last("adjust-pane-size").Amount).IsEqualTo(1);
    }

    [Test]
    public async Task ResizePaneToASize()
    {
        // Act
        Tmux("resize-pane -x 50% -y 10");

        // Assert
        var r = _term.Last("resize-pane");
        await Assert.That((r.WidthPercent, r.Height, r.Width, r.HeightPercent)).IsEqualTo(((int?)50, (int?)10, (int?)null, (int?)null));
    }

    [Test]
    [Arguments("resize-pane")]
    [Arguments("resize-pane -M")]
    [Arguments("resize-pane -T")]
    public async Task ResizePaneWithoutASizeSendsNothing(string line)
    {
        // Act
        var result = Tmux(line);

        // Assert
        await Assert.That(result.Code).IsEqualTo(0);
        await Assert.That(_term.Seen.Count(r => r.Command != "list")).IsEqualTo(0);
    }

    [Test]
    [Arguments("select-layout -E", "even")]
    [Arguments("select-layout tiled", "tiled")]
    [Arguments("select-layout main-v", "main-vertical")]
    [Arguments("select-layout even-horizontal-mirrored", "even-horizontal")]
    public async Task SelectLayoutNamesTheLayout(string line, string layout)
    {
        // Act
        Tmux(line);

        // Assert
        var r = _term.Last("select-layout");
        await Assert.That((r.Layout, r.TabId)).IsEqualTo((layout, (int?)10));
    }

    [Test]
    public async Task AnAmbiguousLayoutIsAnError()
    {
        // Act
        var result = Tmux("select-layout even");

        // Assert
        await Assert.That(result).IsEqualTo((1, "", "invalid layout: even\n"));
    }

    [Test]
    [Arguments("select-layout")]
    [Arguments("select-layout -o")]
    [Arguments("select-layout -n")]
    public async Task SteppingThroughLayoutsChangesNothing(string line)
    {
        // Act
        var result = Tmux(line);

        // Assert
        await Assert.That(result.Code).IsEqualTo(0);
        await Assert.That(_term.Seen.Any(r => r.Command == "select-layout")).IsFalse();
    }

    // ---- Windows, sessions, clients -----------------------------------------------------------------

    [Test]
    [Arguments("select-window -n", 1)]
    [Arguments("select-window -p", -1)]
    public async Task SelectWindowNextAndPrevious(string line, int relative)
    {
        // Act
        Tmux(line);

        // Assert
        await Assert.That(_term.Last("activate-tab").TabRelative).IsEqualTo(relative);
    }

    [Test]
    public async Task SelectWindowByIndex()
    {
        // Act
        Tmux("select-window -t :1");

        // Assert
        await Assert.That(_term.Last("activate-tab").TabId).IsEqualTo(20);
    }

    [Test]
    public async Task SelectWindowLastIsNotSupported()
    {
        // Act
        var result = Tmux("select-window -l");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
    }

    [Test]
    public async Task RenameWindowSetsTheTabTitle()
    {
        // Act
        Tmux("rename-window -t :1 monitor");

        // Assert
        var r = _term.Last("set-tab-title");
        await Assert.That((r.TabId, r.Title)).IsEqualTo(((int?)20, "monitor"));
    }

    [Test]
    [Arguments("rename-session x")]
    [Arguments("last-pane")]
    public async Task UnsupportedCommandsSaySo(string line)
    {
        // Act
        var result = Tmux(line);

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
        await Assert.That(result.Err).EndsWith("(not supported by erikterm's tmux emulation)\n");
    }

    [Test]
    public async Task HasSessionKnowsItsOwn()
    {
        // Act
        var result = Tmux("has-session -t erikterm");

        // Assert
        await Assert.That(result).IsEqualTo((0, "", ""));
    }

    [Test]
    public async Task SwitchClientToAPaneActivatesIt()
    {
        // Act
        Tmux("switch-client -t %3");

        // Assert
        await Assert.That(_term.Last("activate-pane").PaneId).IsEqualTo(3);
    }

    [Test]
    public async Task SwitchClientWithoutATargetChangesNothing()
    {
        // Act
        var plain = Tmux("switch-client");
        var next = Tmux("switch-client -n");

        // Assert: there is one session to switch to.
        await Assert.That(plain.Code).IsEqualTo(0);
        await Assert.That(next.Code).IsEqualTo(1);
    }

    [Test]
    public async Task SwitchClientToAnotherSessionWithoutRealTmuxIsNotFound()
    {
        // Act
        var result = Tmux("switch-client -t other");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
        await Assert.That(result.Err).Contains("other");
    }

    // ---- Options, environment, buffers ---------------------------------------------------------------

    [Test]
    public async Task ShowOptionsListsThemAll()
    {
        // Act
        var result = Tmux("show-options -g");

        // Assert
        await Assert.That(result.Code).IsEqualTo(0);
        await Assert.That(result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).IsGreaterThan(1);
    }

    [Test]
    public async Task ShowOptionsValuesOnly()
    {
        // Act
        var all = Tmux("show-options -gv");

        // Assert: no "name value" pairs, just values.
        await Assert.That(all.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(l => !l.Contains(' ') || l.StartsWith('"'))).IsTrue();
    }

    [Test]
    public async Task ShowOneOptionByName()
    {
        // Act
        var named = Tmux("show-options -g base-index");
        var value = Tmux("show-options -gv base-index");

        // Assert
        await Assert.That(named.Out).IsEqualTo("base-index 0\n");
        await Assert.That(value.Out).IsEqualTo("0\n");
    }

    [Test]
    public async Task ShowEnvironmentReportsAVariable()
    {
        // Act
        var known = Tmux("show-environment HOME");
        var unknown = Tmux("show-environment NOPE_NOT_SET");
        var all = Tmux("show-environment");

        // Assert
        await Assert.That(known).IsEqualTo((0, "HOME=/home/u\n", ""));
        await Assert.That(unknown).IsEqualTo((1, "", "unknown variable: NOPE_NOT_SET\n"));
        await Assert.That(all).IsEqualTo((0, "", ""));
    }

    [Test]
    public async Task SetEnvironmentAndRefreshAreAccepted()
    {
        // Act
        int[] codes = [Tmux("set-environment FOO bar").Code, Tmux("refresh-client").Code, Tmux("start-server").Code];

        // Assert
        await Assert.That(codes).IsEquivalentTo([0, 0, 0]);
    }

    [Test]
    public async Task LoadBufferFromStdinReadsIt()
    {
        // Act
        var result = Tmux("load-buffer -", stdin: "copied");

        // Assert
        await Assert.That(result).IsEqualTo((0, "", ""));
    }

    [Test]
    public async Task LoadBufferFromAMissingFileIsAnError()
    {
        // Act
        var result = Tmux($"load-buffer {_dir}/nope.txt");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
        await Assert.That(result.Err).Contains("No such file or directory");
    }

    [Test]
    public async Task SetOptionOnAPaneWithAColorColorsIt()
    {
        // Act
        Tmux("set-option -p -t %2 pane-border-style fg=blue");

        // Assert
        await Assert.That(_term.Last("set-pane-color").PaneId).IsEqualTo(2);
    }

    [Test]
    public async Task SetOptionOnAMissingPaneChangesNothingTmuxWouldReport()
    {
        // Act
        var result = Tmux("set-option -p -t %99 pane-border-style fg=blue");

        // Assert
        await Assert.That(result.Code).IsEqualTo(0);
    }

    // ---- How it fails --------------------------------------------------------------------------------

    [Test]
    public async Task ARefusedRequestIsReportedLikeTmux()
    {
        // Arrange
        _term.Fail = "no pane with id 2";

        // Act
        var result = Tmux("select-pane -t %2");

        // Assert
        await Assert.That(result).IsEqualTo((1, "", "no pane with id 2\n"));
    }

    [Test]
    public async Task ANoServerIsReported()
    {
        // Arrange
        _term.Throw = new IOException("connection refused");

        // Act
        var result = Tmux("list-panes");

        // Assert
        await Assert.That(result).IsEqualTo((1, "", $"no server running on {_socket}\n"));
    }

    [Test]
    public async Task VersionNamesTheEmulation()
    {
        // Act
        var result = Tmux("-V");

        // Assert
        await Assert.That(result).IsEqualTo((0, $"tmux {TmuxSession.VersionNumber} (erikterm)\n", ""));
    }

    [Test]
    public async Task OutsideTheEmulationWithoutTmuxItSaysTmuxIsMissing()
    {
        // Arrange: no $TMUX of erikterm's.
        var env = new Dictionary<string, string> { ["TMUX"] = "", ["TMUX_PANE"] = "" };

        // Act
        var result = Tmux("list-panes", env: env);

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
        await Assert.That(result.Err).StartsWith("tmux isn't installed");
    }

    [Test]
    public async Task NewSessionWithoutTmuxExplainsTheEmulation()
    {
        // Act
        var result = Tmux("new-session -d");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
        await Assert.That(result.Err).Contains("a real session needs real tmux");
    }

    [Test]
    public async Task EveryInvocationIsLoggedWhenAsked()
    {
        // Arrange
        string log = Path.Combine(_dir, "tmux.log");

        // Act
        Tmux(["display-message", "-p", "it's"], env: new() { ["ERIKTERM_TMUX_LOG"] = log });

        // Assert: arguments with quotes are quoted for the shell.
        string text = File.ReadAllText(log);
        await Assert.That(text).Contains("tmux display-message -p 'it'\\''s' => emulated (exit 0)");
    }
}
