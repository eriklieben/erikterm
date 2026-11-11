using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ErikTerm.App.Ipc;
using ErikTerm.Ui.AgentStatus;
using ErikTerm.Ui.Compare;

namespace ErikTerm.App.Compare;

/// <summary>A pane as a control socket listed it.</summary>
internal readonly record struct ListedPane(string Socket, PaneInfo Pane);

/// <summary>Which panes of an <c>erikterm cli list</c> run a coding agent in a given worktree. Pure, so it's testable without a terminal.</summary>
internal static class ReviewAgentFinder
{
    /// <param name="root">The worktree under review, as <c>git rev-parse --show-toplevel</c> gives it.</param>
    /// <param name="topLevel">The top level of a directory. Asked per pane because worktrees nest: a directory under a worktree can be in another one.</param>
    /// <param name="ownPane">The pane the review runs in, if any: the nearest agent is offered first.</param>
    public static List<ReviewAgentTarget> Targets(IReadOnlyList<ListedPane> panes, string root, Func<string, string?> topLevel, (string Socket, int Pane)? ownPane, bool ignoreCase = false)
    {
        var tops = new Dictionary<string, string?>(StringComparer.Ordinal);
        var own = panes.Where(p => ownPane is { } o && p.Socket == o.Socket && p.Pane.PaneId == o.Pane).Select(p => (ListedPane?)p).FirstOrDefault();
        bool manyWindows = panes.Select(p => (p.Socket, p.Pane.WindowId)).Distinct().Skip(1).Any();
        return panes
            .Where(p => p.Pane.Agent is { Length: > 0 } && InWorktree(p.Pane, root, topLevel, tops, ignoreCase))
            .OrderBy(p => p.Pane.CreatedByTmux || p.Pane.Parked ? 1 : 0) // a teammate only when asked for: the lead runs the work
            .ThenBy(p => Distance(p, own))
            .Select(p => ToTarget(p, manyWindows))
            .ToList();
    }

    private static bool InWorktree(PaneInfo pane, string root, Func<string, string?> topLevel, Dictionary<string, string?> tops, bool ignoreCase)
    {
        if (pane.CurrentPath is not { Length: > 0 } path) return false;
        if (!tops.TryGetValue(path, out string? top)) tops[path] = top = topLevel(path);
        return top is not null && ReviewTabs.Find([new ReviewPaneRef(pane.PaneId, top)], root, ignoreCase) is not null;
    }

    private static int Distance(ListedPane pane, ListedPane? own)
    {
        if (own is not { } o || pane.Socket != o.Socket || pane.Pane.WindowId != o.Pane.WindowId) return 2;
        return pane.Pane.TabId == o.Pane.TabId ? 0 : 1;
    }

    private static ReviewAgentTarget ToTarget(ListedPane p, bool manyWindows)
    {
        string name = AgentCatalog.ById(p.Pane.Agent)?.Name ?? p.Pane.Agent!;
        string place = manyWindows ? $"Window {p.Pane.WindowId} · Tab {p.Pane.TabIndex + 1}" : $"Tab {p.Pane.TabIndex + 1}";
        return new ReviewAgentTarget(p.Socket, p.Pane.PaneId, name, $"{place} · {Home(p.Pane.CurrentPath!)}", p.Pane.AgentStatus ?? "idle");
    }

    private static string Home(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }
}

internal sealed record ReviewCommentFile(string Root, string Branch, List<ReviewComment> Comments);

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReviewCommentFile))]
internal sealed partial class ReviewCommentJson : JsonSerializerContext;

/// <summary>
/// The review's way out: unsent comments in <c>$XDG_STATE_HOME/erikterm/review-comments/</c> (one 0600 file per worktree
/// and branch, gone once nothing is left to send, nothing in the repository), and the agents through the control sockets.
/// </summary>
internal sealed class ReviewCommentHost(
    string? stateDirectory = null, Func<string, IpcRequest, IpcResponse>? transport = null, Func<List<string>>? sockets = null, Func<string, string?>? topLevel = null) : IReviewCommentHost
{
    private const long MaxFileBytes = 4 << 20;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly string _directory = stateDirectory ?? DefaultDirectory();
    private readonly Func<string, IpcRequest, IpcResponse> _send = transport ?? ((socket, request) => IpcClient.Send(socket, request, Timeout));
    private readonly Func<List<string>> _sockets = sockets ?? DefaultSockets;
    private readonly Func<string, string?> _topLevel = topLevel ?? GitTool.TopLevel;

    private static string DefaultDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm", "review-comments");
        string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(state, "erikterm", "review-comments");
    }

    internal string FileFor(string root, string branch)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(root + "\0" + branch));
        return Path.Combine(_directory, Convert.ToHexStringLower(hash.AsSpan(0, 12)) + ".json");
    }

    public IReadOnlyList<ReviewComment> Load(string root, string branch)
    {
        string path = FileFor(root, branch);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return [];
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), ReviewCommentJson.Default.ReviewCommentFile);
            return file?.Comments ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public string? Save(string root, string branch, IReadOnlyList<ReviewComment> comments)
    {
        string path = FileFor(root, branch);
        try
        {
            if (comments.Count == 0) { File.Delete(path); return null; }
            WriteAtomically(path, JsonSerializer.Serialize(new ReviewCommentFile(root, branch, [.. comments]), ReviewCommentJson.Default.ReviewCommentFile));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private void WriteAtomically(string path, string json)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = $"{path}.{Environment.ProcessId}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        using (var writer = new StreamWriter(stream)) writer.Write(json);
        File.Move(temporary, path, overwrite: true);
    }

    // ---- Agents -----------------------------------------------------------------------------------

    /// <summary>The surrounding erikterm first, then every other instance of this user: an agent can be running in any of them.</summary>
    private static List<string> DefaultSockets()
    {
        var list = new List<string>();
        if (Environment.GetEnvironmentVariable(IpcPaths.SocketVariable) is { Length: > 0 } own && File.Exists(own)) list.Add(own);
        try
        {
            string directory = IpcPaths.Directory();
            if (!Directory.Exists(directory) || IpcPaths.DirectoryProblem(directory) is not null) return list;
            list.AddRange(IpcPaths.LiveSockets(directory).Where(s => IpcPaths.IsOwnSocket(directory, s) && !list.Contains(s)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* none to add: the ones found so far will do */ }
        return list;
    }

    public ReviewAgentSearch FindAgents(string root)
    {
        var running = _sockets();
        if (running.Count == 0) return new ReviewAgentSearch([], "erikterm isn't running here, so no agent can be reached");
        var panes = new List<ListedPane>();
        foreach (string socket in running)
        {
            try
            {
                if (_send(socket, new IpcRequest { Command = "list" }).Panes is { } listed)
                    panes.AddRange(listed.Select(p => new ListedPane(socket, p)));
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException) { /* an instance that went away has no agents */ }
        }
        (string, int)? own = running.Count > 0 && int.TryParse(Environment.GetEnvironmentVariable(IpcPaths.PaneVariable), out int id) ? (running[0], id) : null;
        return new ReviewAgentSearch(ReviewAgentFinder.Targets(panes, root, _topLevel, own, OperatingSystem.IsWindows()));
    }

    public string? Send(ReviewAgentTarget target, string prompt)
    {
        try
        {
            var response = _send(target.Socket, new IpcRequest { Command = "send-to-agent", PaneId = target.PaneId, Text = prompt, Submit = true });
            return response.Ok ? null : response.Error ?? $"{target.Agent} didn't take the comments";
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
        {
            return $"Couldn't reach erikterm: {ex.Message}";
        }
    }
}
