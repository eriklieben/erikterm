using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ErikTerm.App.Ipc;

namespace ErikTerm.App.Tmux;

/// <summary>
/// <c>tmux</c> inside an erikterm pane (the same binary, run through the <c>tmux</c> symlink in the pane's shim
/// directory, or as <c>erikterm tmux-shim …</c>). Commands on the fake session — the one <c>$TMUX</c> names — are
/// translated into control-socket requests: windows are tabs, panes are panes (<c>%id</c> = erikterm's pane id).
/// Everything else (no arguments, <c>new-session</c>, <c>attach</c>, <c>-L</c>/<c>-S</c> other servers, targets
/// that aren't in the fake session) runs the real tmux with the emulation's environment removed.
/// </summary>
internal sealed partial class TmuxShim
{
    private const string KillPaneCommand = "kill-pane";

    private readonly IReadOnlyList<string> _args;
    private readonly TextReader _stdin;
    private readonly TextWriter _out, _err;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly Func<string, string?> _env;
    private readonly Func<IpcRequest, IpcResponse> _send;
    private readonly IRealTmux _real;
    private readonly string? _fakeSocket;
    private readonly int? _callerPane;
    private bool _explicitFakeSocket;
    private TmuxSession? _session;
    private string _handled = "emulated";

    private TmuxShim(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr,
        IReadOnlyDictionary<string, string> env, Func<IpcRequest, IpcResponse>? send, IRealTmux real)
    {
        (_args, _stdin, _out, _err, _environment, _real) = (args, stdin, stdout, stderr, env, real);
        _env = k => env.GetValueOrDefault(k);
        string? tmux = _env(TmuxEnvironment.TmuxVariable);
        _fakeSocket = TmuxEnvironment.IsFake(tmux) ? TmuxEnvironment.SocketOf(tmux) : null;
        _send = send ?? (r => IpcClient.Send(_fakeSocket!, r));
        _callerPane = ParsePaneId(_env(TmuxEnvironment.PaneVariable)) ?? (int.TryParse(_env(IpcPaths.PaneVariable), out int p) ? p : null);
    }

    /// <summary>True when argv[0] is "tmux": erikterm was run through a pane's shim symlink.</summary>
    public static bool InvokedAsTmux()
    {
        string? argv0 = null;
        if (OperatingSystem.IsLinux())
        {
            try
            {
                byte[] raw = File.ReadAllBytes("/proc/self/cmdline");
                int nul = Array.IndexOf(raw, (byte)0);
                argv0 = Encoding.UTF8.GetString(raw, 0, nul < 0 ? raw.Length : nul);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* falls back to the process arguments below */ }
        }
        else if (OperatingSystem.IsMacOS()) argv0 = MacArgv0();
        argv0 ??= Environment.GetCommandLineArgs().FirstOrDefault();
        return argv0 is not null && Path.GetFileNameWithoutExtension(argv0) == "tmux";
    }

    /// <summary>The real entry point (Program.Main).</summary>
    public static int RunMain(IReadOnlyList<string> args)
    {
        SetProcessName();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        string path = TmuxEnvironment.ScrubPath(env.GetValueOrDefault("PATH"));
        var real = new RealTmux(TmuxHandoff.FindReal(path, Environment.ProcessPath), env.GetValueOrDefault("TMUX_TMPDIR"));
        return Run(args, Console.In, Console.Out, Console.Error, env, null, real);
    }

    /// <param name="send">Sends one control request (null: the socket in <c>$TMUX</c>).</param>
    public static int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr,
        IReadOnlyDictionary<string, string> env, Func<IpcRequest, IpcResponse>? send, IRealTmux real)
    {
        // tmux ends every line with LF, and what it prints is parsed (Claude Code reads a new pane's #{pane_id}
        // straight back); a Windows TextWriter would write CRLF.
        stdout.NewLine = "\n";
        stderr.NewLine = "\n";
        var shim = new TmuxShim(args, stdin, stdout, stderr, env, send, real);
        int code = shim.Execute();
        stdout.Flush();
        shim.Log(code);
        return code;
    }

    private int Execute()
    {
        TmuxInvocation inv;
        try
        {
            inv = TmuxParser.Parse(_args);
        }
        catch (TmuxUsageException ex)
        {
            if (_fakeSocket is null && _real.Path is not null) return ToReal("not inside the emulation");
            _handled = "usage error";
            _err.WriteLine(ex.Message);
            return 1;
        }

        string? socket = _fakeSocket is not null && File.Exists(_fakeSocket) ? _fakeSocket : null;
        var decision = TmuxHandoff.Decide(inv, socket, _real.Path is not null, _real.Path is not null && _real.ServerRunning);
        if (decision.Route == TmuxRoute.Real) return ToReal(decision.Reason, inv);
        _explicitFakeSocket = inv.SocketPath is not null;

        if (inv.Version)
        {
            _out.WriteLine($"tmux {TmuxSession.VersionNumber} (erikterm)");
            return 0;
        }
        if (inv.UnknownCommand is { } unknown)
        {
            _handled = "unsupported";
            _err.WriteLine($"unknown command: {unknown}");
            return 1;
        }

        return RunCommands(inv);
    }

    private int RunCommands(TmuxInvocation inv)
    {
        try
        {
            foreach (var cmd in inv.Commands)
            {
                int code = Run(cmd);
                if (code != 0) return code;
            }
            return 0;
        }
        catch (TmuxNotOursException ex)
        {
            // Real tmux's, if a real server is running (e.g. a session made with new-session -d); else just not there.
            if (_real.Path is not null && !_explicitFakeSocket && _real.ServerRunning) return ToReal(ex.Message, inv);
            _handled = "not found";
            _err.WriteLine(ex.Message);
            return 1;
        }
        catch (TmuxTargetException ex)
        {
            _handled = "not found";
            _err.WriteLine(ex.Message);
            return 1;
        }
        catch (TmuxUsageException ex)
        {
            _handled = "usage error";
            _err.WriteLine(ex.Message);
            return 1;
        }
        catch (TmuxUnsupportedException ex)
        {
            _handled = "unsupported";
            _err.WriteLine($"{ex.Message} (not supported by erikterm's tmux emulation)");
            return 1;
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
        {
            _handled = "no server";
            _err.WriteLine($"no server running on {_fakeSocket}");
            return 1;
        }
    }

    // ==== Real tmux ============================================================================

    private int ToReal(string reason, TmuxInvocation? inv = null)
    {
        _handled = "real tmux: " + reason;
        if (_real.Path is null)
        {
            if (_args.Count == 0 || inv?.Commands.FirstOrDefault()?.Name is "new-session" or "attach-session")
            {
                // Windows has no tmux to install, so don't send anyone looking for one.
                _err.WriteLine("tmux isn't installed. erikterm's tmux emulation answers programs that use tmux " +
                               "(e.g. Claude Code's agent panes), but a real session needs real tmux" +
                               (OperatingSystem.IsWindows() ? ", which doesn't run on Windows." : " — install it from your package manager."));
                _err.WriteLine("(erikterm has tabs and split panes of its own: Ctrl+Shift+T, Ctrl+Shift+\\, Ctrl+Shift+-.)");
                return 1;
            }
            _err.WriteLine($"tmux isn't installed ({reason})");
            return 1;
        }
        var env = TmuxEnvironment.ForRealTmux(_environment);
        Log(-1);
        _logged = true;
        return _real.Exec(WithoutFakeSocket(_args), env, _err);
    }

    /// <summary>The command line minus <c>-S &lt;fake socket&gt;</c> (real tmux would try to talk to erikterm's socket).</summary>
    private List<string> WithoutFakeSocket(IReadOnlyList<string> args)
    {
        var list = new List<string>(args);
        if (_fakeSocket is null) return list;
        int i = 0;
        while (i < list.Count && list[i].StartsWith('-') && list[i] != "--")
        {
            if (list[i] == "-S" && i + 1 < list.Count && list[i + 1] == _fakeSocket) list.RemoveRange(i, 2);
            else if (list[i] == "-S" + _fakeSocket) list.RemoveAt(i);
            else if (list[i] is "-L" or "-f" or "-c" or "-T" or "-S") i += 2;
            else i++;
        }
        return list;
    }

    /// <summary>Destructive commands on a pane the shim didn't make: if real tmux knows that pane id, it's real tmux's.</summary>
    private void ForwardIfRealPane(PaneInfo pane, string target)
    {
        if (pane.CreatedByTmux || _explicitFakeSocket || _real.Path is null || !target.StartsWith('%') || !_real.ServerRunning) return;
        var env = TmuxEnvironment.ForRealTmux(_environment);
        if (_real.Run(["display-message", "-p", "-t", target, "#{pane_id}"], env) == 0)
            throw new TmuxNotOursException($"{target} is a real tmux pane");
    }

    // ==== Control requests =====================================================================

    private IpcResponse Send(IpcRequest r)
    {
        var resp = _send(r);
        if (!resp.Ok) throw new TmuxTargetException(resp.Error ?? $"{r.Command} failed");
        return resp;
    }

    private TmuxSession Session
    {
        get
        {
            if (_session is not null) return _session;
            var panes = Send(new IpcRequest { Command = "list" }).Panes ?? [];
            int pid = 0;
            if (_fakeSocket is not null && TmuxEnvironment.SocketOf(_env(TmuxEnvironment.TmuxVariable)) == _fakeSocket)
                int.TryParse(_env(TmuxEnvironment.TmuxVariable)!.Split(',').ElementAtOrDefault(1), out pid);
            return _session = new TmuxSession(panes, _callerPane) { Socket = _fakeSocket ?? "", ServerPid = pid };
        }
    }

    private void Invalidate() => _session = null;

    private static int? ParsePaneId(string? s) =>
        s is { Length: > 1 } && s[0] == '%' && int.TryParse(s.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static void RefuseRealTmux(PaneInfo pane)
    {
        if (pane.RealTmux)
            throw new TmuxTargetException($"can't find pane: {TmuxSession.PaneId(pane)} (it is running real tmux, so erikterm's tmux emulation leaves it alone)");
    }

    // ==== Commands =============================================================================

    private int Run(TmuxCommand c) => c.Name switch
    {
        "split-window" => SplitWindow(c),
        "new-window" => NewWindow(c),
        "respawn-pane" => RespawnPane(c),
        "send-keys" => SendKeys(c),
        "capture-pane" => CapturePane(c),
        "display-message" => DisplayMessage(c),
        "list-panes" => ListPanes(c),
        "list-windows" => ListWindows(c),
        "list-sessions" => ListSessions(c),
        "list-clients" => ListClients(c),
        "select-pane" => SelectPane(c),
        KillPaneCommand => KillPane(c),
        "kill-window" => KillWindow(c),
        "kill-session" => KillSession(c),
        "resize-pane" => ResizePane(c),
        "select-layout" => SelectLayout(c),
        "select-window" => SelectWindow(c),
        "rename-window" => RenameWindow(c),
        "rename-session" => throw new TmuxUnsupportedException("rename-session"),
        "has-session" => HasSession(c),
        "switch-client" => SwitchClient(c),
        "show-options" or "show-window-options" => ShowOptions(c),
        "set-option" or "set-window-option" => SetOption(c), // accepted; erikterm draws no borders or status line
        "show-environment" => ShowEnvironment(c),
        "set-environment" => 0,
        "load-buffer" => LoadBuffer(c),
        "refresh-client" or "start-server" => 0,
        "last-pane" => throw new TmuxUnsupportedException("last-pane"),
        _ => throw new TmuxUnsupportedException(c.Name),
    };

    /// <summary>
    /// The program for a new pane, as tmux runs it: one argument is a shell command (<c>default-shell -c</c>,
    /// see <see cref="TmuxShell"/>), several are argv, none is the default shell; <c>-e VAR=value</c> goes in
    /// front through env(1) — on Windows there is no env(1) to spawn, so the whole line goes through the shell.
    /// </summary>
    private string[]? Program(TmuxCommand c)
    {
        var (shell, flag, posix) = TmuxShell.ForCommand(_env);
        var vars = c.AllValues.Where(v => v.Flag == 'e').Select(v => v.Value).ToList();
        // Without a POSIX shell, say so instead of handing cmd.exe a line it would mangle. Claude Code's pane
        // commands are POSIX (the `cat` placeholder, then `cd '…' && env … '<claude>' --agent-id …`), and so is
        // anything with -e VAR=value; a plain Windows command line from anyone else still runs under cmd.exe.
        if (!posix && (vars.Count > 0 || c.Args.Count > 0 && TmuxTeammates.FromClaudeCode(_env)))
            throw new TmuxUnsupportedException(
                "a POSIX shell command line without a POSIX shell to run it: install Git for Windows, or point $SHELL at one");
        string[]? argv = c.Args.Count switch
        {
            0 => null,
            1 => [shell, flag, c.Args[0]],
            _ => c.Args.ToArray(),
        };
        if (vars.Count == 0) return argv;
        if (OperatingSystem.IsWindows())
        {
            // There is no env(1) to spawn; the POSIX shell runs the whole line instead.
            string line = string.Join(' ', vars.Select(QuoteArg).Concat((argv ?? [shell]).Select(QuoteArg)));
            return [shell, flag, "env " + line];
        }
        var withEnv = new List<string> { "env" };
        withEnv.AddRange(vars);
        withEnv.AddRange(argv ?? [shell]);
        return withEnv.ToArray();
    }

    /// <summary>-c DIR: formats expanded (e.g. "#{pane_current_path}"), relative to the caller's directory.</summary>
    private string? StartDirectory(TmuxCommand c, PaneInfo context)
    {
        if (c.Value('c') is not { } dir) return null;
        dir = Session.Expand(dir, context);
        // A format with nothing to report (Windows can't tell a pane's current directory) means "wherever tmux would".
        if (dir.Length == 0) return null;
        if (dir.StartsWith('~')) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + dir[1..];
        string full = Path.GetFullPath(dir);
        if (!Directory.Exists(full)) throw new TmuxTargetException($"{dir}: No such file or directory");
        return full;
    }

    private void PrintCreated(TmuxCommand c, int paneId, string defaultFormat)
    {
        if (!c.Has('P')) return;
        Invalidate();
        var pane = Session.Panes.FirstOrDefault(p => p.PaneId == paneId) ?? throw new TmuxTargetException($"can't find pane: %{paneId}");
        _out.WriteLine(Session.Expand(c.Value('F') ?? defaultFormat, pane));
    }

    private int SplitWindow(TmuxCommand c)
    {
        var target = Session.ResolvePane(c.Target);
        RefuseRealTmux(target);
        if (c.Has('I')) throw new TmuxUnsupportedException("split-window -I");
        bool before = c.Has('b');
        string dir;
        if (c.Has('h')) dir = before ? "Left" : "Right";
        else dir = before ? "Up" : "Down";
        var r = new IpcRequest
        {
            Command = "split-pane", PaneId = target.PaneId, Direction = dir, NoFocus = c.Has('d'), Tmux = true,
            Cwd = StartDirectory(c, target), Argv = Program(c),
            Teammate = TmuxTeammates.IsTeammateSplit(c.Args, _env), CallerPaneId = _callerPane,
        };
        if (c.Value('l') is { } size) SetSize(r, size, "split-window");
        else if (c.Value('p') is { } pct) r.Percent = Number(pct, "split-window -p", 1, 99);
        var resp = Send(r);
        PrintCreated(c, resp.PaneId ?? 0, "#{session_name}:#{window_index}.#{pane_index}");
        return 0;
    }

    private static void SetSize(IpcRequest r, string size, string what)
    {
        if (size.EndsWith('%')) r.Percent = Number(size[..^1], what + " -l", 1, 99);
        else r.Cells = Number(size, what + " -l", 1, 10000);
    }

    private static int Number(string s, string what, int min, int max) =>
        int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max
            ? n : throw new TmuxUsageException($"{what}: invalid size: {s}");

    private int NewWindow(TmuxCommand c)
    {
        var from = Session.Current;
        if (c.Target is { } t)
        {
            try { Session.ResolveWindow(t); }
            catch (TmuxTargetException) { /* a window index that's free: new tabs go at the end anyway */ }
        }
        var resp = Send(new IpcRequest
        {
            Command = "spawn", PaneId = from.PaneId, NoFocus = c.Has('d'), Tmux = true, Title = c.Value('n'),
            Cwd = StartDirectory(c, from), Argv = Program(c),
        });
        PrintCreated(c, resp.PaneId ?? 0, "#{session_name}:#{window_index}.#{pane_index}");
        return 0;
    }

    private int RespawnPane(TmuxCommand c)
    {
        var pane = Session.ResolvePane(c.Target);
        RefuseRealTmux(pane);
        ForwardIfRealPane(pane, c.Target ?? "");
        Send(new IpcRequest
        {
            Command = "respawn-pane", PaneId = pane.PaneId, Kill = c.Has('k'), Cwd = StartDirectory(c, pane), Argv = Program(c),
            Teammate = TmuxTeammates.FromClaudeCode(_env), CallerPaneId = _callerPane,
        });
        return 0;
    }

    private int SendKeys(TmuxCommand c)
    {
        var pane = Session.ResolvePane(c.Target);
        RefuseRealTmux(pane);
        if (c.Has('X')) throw new TmuxUnsupportedException("send-keys -X (copy mode)");
        if (c.Has('M')) throw new TmuxUnsupportedException("send-keys -M");
        int repeat = c.Value('N') is { } n ? Number(n, "send-keys -N", 1, 10000) : 1;
        string text;
        if (c.Has('H')) text = TmuxKeys.FromHex(c.Args);
        else if (c.Has('l')) text = string.Concat(c.Args);
        else text = string.Concat(c.Args.Select(a => TmuxKeys.Translate(a, pane.CursorKeysApplication)));
        if (c.Has('R')) text = "\ec" + text; // reset the terminal first
        if (repeat > 1) text = string.Concat(Enumerable.Repeat(text, repeat));
        if (text.Length > 0) Send(new IpcRequest { Command = "send-text", PaneId = pane.PaneId, Text = text, NoPaste = true });
        return 0;
    }

    private int CapturePane(TmuxCommand c)
    {
        var pane = Session.ResolvePane(c.Target);
        if (!c.Has('p')) return 0; // no paste buffers: capturing into one keeps nothing
        int? start = c.Value('S') switch { null => null, "-" => int.MinValue / 2, var s => Number(s, "capture-pane -S", int.MinValue / 2, 1_000_000) };
        int? end = c.Value('E') switch { null or "-" => null, var s => Number(s, "capture-pane -E", int.MinValue / 2, 1_000_000) };
        var resp = Send(new IpcRequest
        {
            Command = "get-text", PaneId = pane.PaneId, StartLine = start, EndLine = end, Escapes = c.Has('e'), NoJoin = !c.Has('J'),
        });
        _out.Write(resp.Output ?? "");
        return 0;
    }

    private const string DisplayTemplate = "[#{session_name}] #{window_index}:#{window_name}, current pane #{pane_index}";

    private int DisplayMessage(TmuxCommand c)
    {
        var pane = Session.ResolvePane(c.Target);
        if (c.Has('a'))
        {
            var vars = Session.Variables(pane);
            foreach (string name in KnownVariables.Order(StringComparer.Ordinal))
                if (vars(name) is { } v) _out.WriteLine($"{name}={v}");
            return 0;
        }
        string format = c.Args.Count > 0 ? c.Args[0] : c.Value('F') ?? DisplayTemplate;
        if (c.Has('I')) _stdin.ReadToEnd();
        if (!c.Has('p')) return 0; // there is no status line to show it on
        _out.WriteLine(c.Has('l') ? format : Session.Expand(format, pane));
        return 0;
    }

    private static readonly string[] KnownVariables =
    [
        "client_control_mode", "client_height", "client_name", "client_termfeatures", "client_termname", "client_termtype",
        "client_tty", "client_width", "cursor_x", "cursor_y", "history_limit", "history_size", "host", "host_short",
        "pane_active", "pane_at_bottom", "pane_at_left", "pane_at_right", "pane_at_top", "pane_bottom", "pane_current_command",
        "pane_current_path", "pane_dead", "pane_height", "pane_id", "pane_in_mode", "pane_index", "pane_left", "pane_pid",
        "pane_right", "pane_title", "pane_top", "pane_tty", "pane_width", "pid", "session_attached", "session_id",
        "session_name", "session_windows", "socket_path", "version", "window_active", "window_flags", "window_height",
        "window_id", "window_index", "window_name", "window_panes", "window_width", "window_zoomed_flag",
    ];

    private const string PaneLine = "[#{pane_width}x#{pane_height}] [history #{history_size}/#{history_limit}, #{history_bytes} bytes] #{pane_id}#{?pane_active, (active),}";

    private int ListPanes(TmuxCommand c)
    {
        IEnumerable<PaneInfo> panes;
        string format;
        if (c.Has('a') || c.Has('s'))
        {
            if (c.Has('s')) Session.ResolveSession(c.Target);
            panes = Session.Panes;
            format = (c.Has('a') ? "#{session_name}:" : "") + "#{window_index}.#{pane_index}: " + PaneLine;
        }
        else
        {
            panes = Session.ResolveWindow(c.Target).Panes;
            format = "#{pane_index}: " + PaneLine;
        }
        Print(panes, c.Value('F') ?? format, c.Value('f'));
        return 0;
    }

    private void Print(IEnumerable<PaneInfo> panes, string format, string? filter)
    {
        foreach (var p in panes)
        {
            if (filter is not null && Session.Expand(filter, p) is "" or "0") continue;
            _out.WriteLine(Session.Expand(format, p));
        }
    }

    private int ListWindows(TmuxCommand c)
    {
        if (!c.Has('a')) Session.ResolveSession(c.Target);
        string format = (c.Has('a') ? "#{session_name}:" : "") +
                        "#{window_index}: #{window_name}#{window_flags} (#{window_panes} panes) [#{window_width}x#{window_height}] #{window_id}#{?window_active, (active),}";
        Print(Session.Windows.Select(w => w.Active), c.Value('F') ?? format, c.Value('f'));
        return 0;
    }

    private int ListSessions(TmuxCommand c)
    {
        Print([Session.Current], c.Value('F') ?? "#{session_name}: #{session_windows} windows (attached)", c.Value('f'));
        return 0;
    }

    private int ListClients(TmuxCommand c)
    {
        Print([Session.Current], c.Value('F') ?? "#{client_name}: #{session_name} [#{client_width}x#{client_height} #{client_termname}] (utf8)", c.Value('f'));
        return 0;
    }

    private int SelectPane(TmuxCommand c)
    {
        var pane = Session.ResolvePane(c.Target);
        if (c.Value('T') is { } title)
        {
            Send(new IpcRequest { Command = "set-pane-title", PaneId = pane.PaneId, Title = Session.Expand(title, pane) });
            return 0;
        }
        // Styles, input on/off and marks change nothing erikterm draws.
        if (c.Value('P') is not null || c.Has('g') || c.Has('d') || c.Has('e') || c.Has('M') || c.Has('m')) return 0;
        if (c.Has('l')) throw new TmuxUnsupportedException("select-pane -l");
        string? dir = DirectionFlag(c);
        Send(dir is null
            ? new IpcRequest { Command = "activate-pane", PaneId = pane.PaneId }
            : new IpcRequest { Command = "activate-pane-direction", PaneId = pane.PaneId, Direction = dir });
        return 0;
    }

    private int KillPane(TmuxCommand c)
    {
        var pane = Session.ResolvePane(c.Target);
        if (c.Has('a'))
        {
            foreach (var other in Session.WindowOf(pane).Panes.Where(p => p.PaneId != pane.PaneId).ToList())
                Send(new IpcRequest { Command = KillPaneCommand, PaneId = other.PaneId });
            return 0;
        }
        ForwardIfRealPane(pane, c.Target ?? "");
        Send(new IpcRequest { Command = KillPaneCommand, PaneId = pane.PaneId });
        return 0;
    }

    private int KillWindow(TmuxCommand c)
    {
        var window = Session.ResolveWindow(c.Target);
        var windows = c.Has('a') ? Session.Windows.Where(w => w != window).ToList() : [window];
        foreach (var w in windows)
            foreach (var p in w.Panes.ToList())
                Send(new IpcRequest { Command = KillPaneCommand, PaneId = p.PaneId });
        return 0;
    }

    private int KillSession(TmuxCommand c)
    {
        Session.ResolveSession(c.Target);
        throw new TmuxUnsupportedException("kill-session on erikterm's own session (close the window instead)");
    }

    private int ResizePane(TmuxCommand c)
    {
        var pane = Session.ResolvePane(c.Target);
        if (c.Has('Z'))
        {
            Send(new IpcRequest { Command = "zoom-pane", PaneId = pane.PaneId, Zoom = "toggle" });
            return 0;
        }
        if (c.Has('M') || c.Has('T')) return 0;
        string? dir = DirectionFlag(c);
        if (dir is not null)
        {
            int amount = c.Args.Count > 0 ? Number(c.Args[0], "resize-pane", 1, 10000) : 1;
            Send(new IpcRequest { Command = "adjust-pane-size", PaneId = pane.PaneId, Direction = dir, Amount = amount });
            return 0;
        }
        var r = new IpcRequest { Command = "resize-pane", PaneId = pane.PaneId };
        (r.Width, r.WidthPercent) = ResizeAmount(c.Value('x'), "resize-pane -x");
        (r.Height, r.HeightPercent) = ResizeAmount(c.Value('y'), "resize-pane -y");
        if (r.Width is null && r.WidthPercent is null && r.Height is null && r.HeightPercent is null) return 0;
        Send(r);
        return 0;
    }

    /// <summary>A size argument as (cells, percent): one is set when the value is given.</summary>
    private static (int? Cells, int? Percent) ResizeAmount(string? value, string flag)
    {
        if (value is null) return (null, null);
        if (value.EndsWith('%')) return (null, Number(value[..^1], flag, 1, 100));
        return (Number(value, flag, 1, 10000), null);
    }

    private static readonly string[] Layouts = ["even-horizontal", "even-vertical", "main-horizontal", "main-vertical", "tiled"];

    /// <summary>A layout name or unambiguous prefix (tmux also has the *-mirrored variants, mapped to the plain ones).</summary>
    public static string? LayoutName(string name)
    {
        name = name.Replace("-mirrored", "", StringComparison.Ordinal);
        if (Layouts.Contains(name)) return name;
        var matches = Layouts.Where(l => l.StartsWith(name, StringComparison.Ordinal)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private int SelectLayout(TmuxCommand c)
    {
        var window = Session.ResolveWindow(c.Target);
        string? layout;
        if (c.Has('E')) layout = "even";
        else if (c.Has('o') || c.Has('n') || c.Has('p') || c.Args.Count == 0) return 0; // no layout history to step through
        else layout = LayoutName(c.Args[0]) ?? throw new TmuxTargetException($"invalid layout: {c.Args[0]}");
        Send(new IpcRequest { Command = "select-layout", TabId = window.TabId, PaneId = window.Active.PaneId, Layout = layout });
        return 0;
    }

    private int SelectWindow(TmuxCommand c)
    {
        if (c.Has('n') || c.Has('p'))
        {
            Send(new IpcRequest { Command = "activate-tab", PaneId = Session.Current.PaneId, TabRelative = c.Has('n') ? 1 : -1 }); // in the caller's window
            return 0;
        }
        if (c.Has('l')) throw new TmuxUnsupportedException("select-window -l");
        var window = Session.ResolveWindow(c.Target);
        Send(new IpcRequest { Command = "activate-tab", TabId = window.TabId });
        return 0;
    }

    private int RenameWindow(TmuxCommand c)
    {
        var window = Session.ResolveWindow(c.Target);
        Send(new IpcRequest { Command = "set-tab-title", TabId = window.TabId, Title = c.Args[0] });
        return 0;
    }

    private int HasSession(TmuxCommand c)
    {
        Session.ResolveSession(c.Target);
        return 0;
    }

    private int SwitchClient(TmuxCommand c)
    {
        if (c.Target is not { } target)
        {
            if (c.Has('n') || c.Has('p') || c.Has('l')) throw new TmuxUnsupportedException("switch-client -n/-p/-l (there is one session)");
            return 0;
        }
        try
        {
            var pane = Session.ResolvePane(target);
            Send(new IpcRequest { Command = "activate-pane", PaneId = pane.PaneId });
            return 0;
        }
        catch (TmuxNotOursException) when (_real.Path is not null)
        {
            // A real session (e.g. one just made with new-session -d): attach to it in a new tab.
            Send(new IpcRequest
            {
                Command = "spawn", PaneId = Session.Current.PaneId, Title = target,
                Argv = ["env", "-u", TmuxEnvironment.TmuxVariable, "-u", TmuxEnvironment.PaneVariable, _real.Path, "attach-session", "-t", target],
            });
            _handled = "attached the real session in a new tab";
            return 0;
        }
    }

    /// <summary>
    /// Accepted and ignored (erikterm draws no borders or status line) — except the color Claude Code gives a
    /// teammate's pane (<c>set-option -p -t %N pane-border-style fg=blue</c>), which colors its pill.
    /// </summary>
    private int SetOption(TmuxCommand c)
    {
        if (!c.Has('p') || c.Args.Count < 2 || c.Args[0] is not ("pane-border-style" or "pane-active-border-style" or "window-style")) return 0;
        if (TmuxTeammates.StyleForeground(c.Args[1]) is not { } color) return 0;
        try
        {
            var pane = Session.ResolvePane(c.Target);
            _send(new IpcRequest { Command = "set-pane-color", PaneId = pane.PaneId, Color = color }); // a failure changes nothing tmux would report
        }
        catch (Exception ex) when (ex is TmuxNotOursException or TmuxTargetException or SocketException or IOException or TimeoutException) { /* a failure changes nothing tmux would report */ }
        return 0;
    }

    private int ShowOptions(TmuxCommand c)
    {
        bool valueOnly = c.Has('v'), quiet = c.Has('q');
        var options = Session.Options;
        if (c.Args.Count == 0)
        {
            foreach (var (k, v) in options.OrderBy(o => o.Key, StringComparer.Ordinal))
                _out.WriteLine(valueOnly ? v : $"{k} {Quote(v)}");
            return 0;
        }
        string name = c.Args[0];
        if (options.TryGetValue(name, out string? value))
        {
            _out.WriteLine(valueOnly ? value : $"{name} {Quote(value)}");
            return 0;
        }
        if (quiet) return 0;
        _err.WriteLine($"invalid option: {name}");
        return 1;
    }

    private static string Quote(string v) => v.Length == 0 || v.Contains(' ') || v.Contains('#') ? $"\"{v}\"" : v;

    private int ShowEnvironment(TmuxCommand c)
    {
        // The emulated server's global environment is erikterm's, which has nothing tmux-specific to report.
        if (c.Args.Count == 0) return 0;
        string name = c.Args[0];
        if (_env(name) is { } v)
        {
            _out.WriteLine($"{name}={v}");
            return 0;
        }
        _err.WriteLine($"unknown variable: {name}");
        return 1;
    }

    private int LoadBuffer(TmuxCommand c)
    {
        // Paste buffers aren't kept; with -w the program has also sent OSC 52, which erikterm applies.
        if (c.Args[0] == "-") _stdin.ReadToEnd();
        else if (!File.Exists(c.Args[0])) throw new TmuxTargetException($"{c.Args[0]}: No such file or directory");
        return 0;
    }

    // ==== Logging ==============================================================================

    private bool _logged;

    private void Log(int code)
    {
        if (_logged || _env(TmuxEnvironment.LogVariable) is not { Length: > 0 } path) return;
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff} [{Environment.ProcessId}] tmux {string.Join(' ', _args.Select(QuoteArg))} => {_handled}" +
                          (code >= 0 ? $" (exit {code})" : "") + "\n";
            File.AppendAllText(path, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a debug log only: it must not fail the command */ }
    }

    private static string? DirectionFlag(TmuxCommand c)
    {
        if (c.Has('L')) return "Left";
        if (c.Has('R')) return "Right";
        if (c.Has('U')) return "Up";
        return c.Has('D') ? "Down" : null;
    }

    private static string QuoteArg(string a) =>
        a.Length > 0 && a.All(ch => char.IsAsciiLetterOrDigit(ch) || "-_./%@:#{}=,".Contains(ch)) ? a : "'" + a.Replace("'", "'\\''") + "'";

    // ==== Native ===============================================================================

    /// <summary>Names the process "erikterm-tmux" so erikterm doesn't mistake the shim for real tmux in the foreground.</summary>
    #pragma warning disable S6640 // prctl(PR_SET_NAME) reads a C string from a stack buffer
    private static unsafe void SetProcessName()
    #pragma warning restore S6640
    {
        if (!OperatingSystem.IsLinux()) return;
        byte* name = stackalloc byte[16];
        "erikterm-tmux\0"u8.CopyTo(new Span<byte>(name, 16));
        try { prctl(15 /* PR_SET_NAME */, (nint)name, 0, 0, 0); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* no prctl here: the process keeps its name */ }
    }

    [LibraryImport("libc.so.6")]
    private static partial int prctl(int option, nint arg2, nint arg3, nint arg4, nint arg5);

    /// <summary>macOS: argv[0] as exec'd (crt's _NSGetArgv), so a run through the shim's "tmux" symlink is recognised.</summary>
    #pragma warning disable S6640 // _NSGetArgv hands back the C runtime's argv as pointers
    private static unsafe string? MacArgv0()
    #pragma warning restore S6640
    {
        try
        {
            byte** argv = *_NSGetArgv();
            return argv is not null && argv[0] is not null ? Marshal.PtrToStringUTF8((nint)argv[0]) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    #pragma warning disable S6640 // native _NSGetArgv signature
    private static unsafe partial byte*** _NSGetArgv();
    #pragma warning restore S6640
}

public sealed class TmuxUnsupportedException(string message) : Exception(message);
