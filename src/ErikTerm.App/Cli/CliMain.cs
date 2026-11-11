using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ErikTerm.App.Ipc;

namespace ErikTerm.App.Cli;

/// <summary>
/// <c>erikterm cli ...</c>: talks to a running instance over its control socket. Runs before SDL is
/// touched, so it opens no window and works over SSH or from scripts.
/// </summary>
internal static class CliMain
{
    private const string WaitForState = "wait-for-state";

    public static int Run(IReadOnlyList<string> args) =>
        Run(args, Console.In, Console.Out, Console.Error, Environment.GetEnvironmentVariable);

    public static int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr,
        Func<string, string?> env, TimeSpan? timeout = null)
    {
        CliInvocation inv;
        try
        {
            inv = CliParser.Parse(args);
        }
        catch (CliUsageException ex)
        {
            stderr.WriteLine($"erikterm cli: {ex.Message}");
            stderr.WriteLine("Run 'erikterm cli --help' for usage.");
            return CliExit.Failed;
        }
        if (inv.Help)
        {
            stdout.WriteLine(CliParser.Usage);
            return 0;
        }
        if (inv.Request.Command == "report-usage") return AgentCli.Statusline(inv, stdin, stdout, env); // never fails loudly

        string? socket = ResolveSocket(inv, env, out bool ownInstance);
        if (socket is null) return ReportNoInstance(stderr, env);

        var request = inv.Request;
        ApplyCallerContext(inv, stdin, env, ownInstance);

        IpcResponse response;
        try
        {
            response = IpcClient.Send(socket, request, ReplyTimeout(request, timeout));
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
        {
            return ReportSendFailure(request, socket, ex, stdout, stderr);
        }
        if (!response.Ok)
        {
            stderr.WriteLine($"erikterm cli: {response.Error ?? "failed"}");
            return response.ExitCode ?? CliExit.Failed;
        }

        WriteResult(inv, response, stdout);
        return CliExit.Ok;
    }

    /// <summary>How long to wait for the reply: the commands that are answered late wait for what they asked for.</summary>
    private static TimeSpan? ReplyTimeout(IpcRequest request, TimeSpan? general) => request.Command switch
    {
        WaitForState when request.TimeoutMs is { } ms => TimeSpan.FromMilliseconds(ms) + WaitGrace,
        WaitForState => Timeout.InfiniteTimeSpan,
        "new-worktree" => NewWorktreeTimeout,
        _ => general,
    };

    /// <summary>The instance enforces --timeout; this is for one too busy to answer.</summary>
    private static readonly TimeSpan WaitGrace = TimeSpan.FromSeconds(2);

    /// <summary>Longer than git is given for a large checkout (WorktreeGit).</summary>
    private static readonly TimeSpan NewWorktreeTimeout = TimeSpan.FromMinutes(6);

    private static int ReportSendFailure(IpcRequest request, string socket, Exception ex, TextWriter stdout, TextWriter stderr)
    {
        if (request.Command == WaitForState && ex is TimeoutException)
        {
            stderr.WriteLine("erikterm cli: timed out waiting for erikterm");
            return CliExit.TimedOut;
        }
        if (request.Command == WaitForState && ex is IOException) // the instance quit mid-wait, and its panes with it
        {
            if (WaitStates.Parse(request.State)?.Contains(WaitStates.Exit) == true)
            {
                stdout.WriteLine(WaitStates.Exit);
                stdout.Flush();
                return CliExit.Ok;
            }
            stderr.WriteLine("erikterm cli: erikterm quit while waiting");
            return CliExit.PaneGone;
        }
        stderr.WriteLine($"erikterm cli: {socket}: {ex.Message}");
        return CliExit.Failed;
    }

    private static int ReportNoInstance(TextWriter stderr, Func<string, string?> env)
    {
        string dir = IpcPaths.Directory(env);
        if (Directory.Exists(dir) && IpcPaths.DirectoryProblem(dir) is { } problem)
            stderr.WriteLine($"erikterm cli: not using {dir}: {problem}; pass --socket PATH");
        else
            stderr.WriteLine($"erikterm cli: no running erikterm found (looked in {dir}); pass --socket PATH");
        return CliExit.Failed;
    }

    private static void ApplyCallerContext(CliInvocation inv, TextReader stdin, Func<string, string?> env, bool ownInstance)
    {
        var request = inv.Request;
        if (ownInstance && request.PaneId is null && int.TryParse(env(IpcPaths.PaneVariable), out int envPane))
            request.PaneId = envPane;
        if (inv.ReadStdin) request.Text = stdin.ReadToEnd();
        if (inv.HookInput)
        {
            string hook = stdin.ReadToEnd();
            request.Message ??= AgentCli.HookMessage(hook);
            request.Kind ??= AgentCli.HookKind(hook);
        }
    }

    private static void WriteResult(CliInvocation inv, IpcResponse response, TextWriter stdout)
    {
        switch (inv.Request.Command)
        {
            case "list":
                var panes = response.Panes ?? [];
                stdout.Write(inv.Format == "json"
                    ? JsonSerializer.Serialize(panes, IpcJson.Pretty.ListPaneInfo) + "\n"
                    : FormatTable(panes));
                break;
            case "split-pane" or "spawn" or "get-pane-direction" or "new-worktree":
                if (response.PaneId is { } id) stdout.WriteLine(id);
                break;
            case "get-text" or WaitForState:
                stdout.Write(response.Output ?? "");
                break;
            case "agents":
                var agents = response.Agents ?? [];
                stdout.Write(inv.Format == "json"
                    ? JsonSerializer.Serialize(agents, IpcJson.Pretty.ListAgentInfo) + "\n"
                    : AgentCli.FormatAgents(agents));
                break;
        }
        stdout.Flush();
    }

    /// <summary>
    /// --socket, else $ERIKTERM_SOCKET (when it still exists), else the last-focused / only running
    /// instance. <paramref name="ownInstance"/>: the socket is the one this shell's pane belongs to,
    /// so $ERIKTERM_PANE is meaningful there.
    /// </summary>
    public static string? ResolveSocket(CliInvocation inv, Func<string, string?> env, out bool ownInstance)
    {
        string? fromEnv = env(IpcPaths.SocketVariable) is { Length: > 0 } e && File.Exists(e) ? e : null;
        if (inv.Socket is { } explicitSocket)
        {
            ownInstance = fromEnv is not null && SamePath(explicitSocket, fromEnv);
            return explicitSocket;
        }
        if (fromEnv is not null)
        {
            ownInstance = true;
            return fromEnv;
        }
        ownInstance = false;
        return IpcPaths.Discover(IpcPaths.Directory(env));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The <c>wezterm cli list</c> table: TABID PANEID SIZE TITLE CWD (plus WINID / WORKSPACE).</summary>
    public static string FormatTable(IReadOnlyList<PaneInfo> panes)
    {
        string[] header = ["WINID", "TABID", "PANEID", "WORKSPACE", "SIZE", "TITLE", "CWD"];
        var rows = new List<string[]> { header };
        foreach (var p in panes)
            rows.Add([p.WindowId.ToString(), p.TabId.ToString(), p.PaneId.ToString(), p.Workspace,
                $"{p.Size.Cols}x{p.Size.Rows}", p.Title, p.Cwd]);
        var sb = new StringBuilder();
        CliTable.Append(sb, rows, rightAligned: 3);
        return sb.ToString();
    }
}
