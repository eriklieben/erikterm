using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ErikTerm.App.Notifications;

/// <summary>A Hyprland workspace as its JSON names it.</summary>
internal readonly record struct HyprlandWorkspace(int Id, string Name);

/// <summary>The fields of one entry of <c>j/clients</c> that the matching needs.</summary>
internal readonly record struct HyprlandClient(int Pid, string Class, string Title, bool Mapped, HyprlandWorkspace Workspace);

/// <summary>How one erikterm window looks from the compositor's side.</summary>
/// <param name="Pid">This process.</param>
/// <param name="Class">The Wayland app id (erikterm, or the --class value).</param>
/// <param name="Title">The title last given to the compositor.</param>
/// <param name="WindowsInProcess">How many windows this process has open.</param>
internal readonly record struct HyprlandWindowIdentity(int Pid, string Class, string Title, int WindowsInProcess);

/// <summary>What Hyprland reported at one moment.</summary>
internal sealed record HyprlandSnapshot(IReadOnlyList<HyprlandClient> Clients, HyprlandWorkspace Active)
{
    /// <summary>True when both say the same thing (both null counts).</summary>
    public static bool Same(HyprlandSnapshot? a, HyprlandSnapshot? b)
    {
        if (a is null || b is null) return ReferenceEquals(a, b);
        return a.Active == b.Active && a.Clients.SequenceEqual(b.Clients);
    }
}

/// <summary>
/// Which Hyprland workspace an erikterm window is on, for notifications that come from a window on another workspace.
/// Reads Hyprland's request socket with the read-only <c>j/clients</c> and <c>j/activeworkspace</c>; every failure
/// (not Hyprland, no socket, a slow or odd answer) means no workspace is mentioned. The parsing, matching and wording
/// are static so they can be tested without a compositor.
/// <para>A window is told from the process's other windows by its title, since the compositor's list has nothing else
/// that erikterm can also see. A process with one window needs no title. When windows can't be told apart and they
/// aren't all on the same workspace, nothing is reported.</para>
/// </summary>
internal sealed class HyprlandWorkspaces(string? socketPath, Action? changed = null, int timeoutMs = HyprlandWorkspaces.DefaultTimeoutMs)
{
    public const int DefaultTimeoutMs = 150;
    private const int RefreshIntervalMs = 2000;
    private const int MaxReplyBytes = 8 * 1024 * 1024;
    private const int MaxNameChars = 32;
    /// <summary>Hyprland's id for "no workspace".</summary>
    private const int InvalidWorkspaceId = -1;
    private const string SpecialName = "special", SpecialPrefix = "special:";
    private const string ClientsRequest = "j/clients", ActiveWorkspaceRequest = "j/activeworkspace";

    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true };

    private HyprlandSnapshot? _latest;
    private int _stamp, _busy;
    private long _refreshAt;

    /// <summary>The socket path from the environment; null off Hyprland, or when the path can't be a socket.</summary>
    public static string? SocketPath(Func<string, string?> env)
    {
        if (!OperatingSystem.IsLinux()) return null;
        if (env("HYPRLAND_INSTANCE_SIGNATURE") is not { Length: > 0 } signature || signature.Contains('/')) return null;
        if (env("XDG_RUNTIME_DIR") is not { Length: > 0 } runtime) return null;
        string path = Path.Combine(runtime, "hypr", signature, ".socket.sock");
        return Encoding.UTF8.GetByteCount(path) <= Ipc.IpcPaths.LinuxMaxSocketPathBytes ? path : null;
    }

    public bool Enabled => socketPath is not null;

    /// <summary>The last answer, for the in-window labels; null until there is one, or after a failed query.</summary>
    public HyprlandSnapshot? Latest => Volatile.Read(ref _latest);

    /// <summary>Changes whenever <see cref="Latest"/> says something new.</summary>
    public int Stamp => Volatile.Read(ref _stamp);

    /// <summary>Asks again in the background, at most every couple of seconds. Main thread.</summary>
    public void Refresh(long now)
    {
        if (!Enabled || now < _refreshAt || Interlocked.Exchange(ref _busy, 1) != 0) return;
        _refreshAt = now + RefreshIntervalMs;
        _ = Task.Run(async () =>
        {
            try { await FetchAsync().ConfigureAwait(false); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    /// <summary>A fresh answer within the timeout, kept as <see cref="Latest"/>; null when there is none. Never throws for I/O.</summary>
    public async Task<HyprlandSnapshot?> FetchAsync()
    {
        var snapshot = await QueryAsync().ConfigureAwait(false);
        if (!HyprlandSnapshot.Same(Latest, snapshot))
        {
            Volatile.Write(ref _latest, snapshot);
            Interlocked.Increment(ref _stamp);
            changed?.Invoke();
        }
        return snapshot;
    }

    private async Task<HyprlandSnapshot?> QueryAsync()
    {
        if (socketPath is null) return null;
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            string clients = await RequestAsync(socketPath, ClientsRequest, cts.Token).ConfigureAwait(false);
            string active = await RequestAsync(socketPath, ActiveWorkspaceRequest, cts.Token).ConfigureAwait(false);
            return ParseClients(clients) is { } list && ParseActiveWorkspace(active) is { } workspace ? new HyprlandSnapshot(list, workspace) : null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>One request per connection: Hyprland answers and closes.</summary>
    private static async Task<string> RequestAsync(string path, string request, CancellationToken token)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token).ConfigureAwait(false);
        await socket.SendAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None, token).ConfigureAwait(false);
        using var reply = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false)) > 0)
        {
            if (reply.Length + read > MaxReplyBytes) throw new IOException("reply too long");
            await reply.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(reply.GetBuffer(), 0, (int)reply.Length);
    }

    // ---- Parsing ----------------------------------------------------------------------------------

    /// <summary>The list from <c>j/clients</c>; null when it isn't one or any entry lacks a needed field (a skipped client could hide a clash).</summary>
    public static IReadOnlyList<HyprlandClient>? ParseClients(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, JsonOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var clients = new List<HyprlandClient>(doc.RootElement.GetArrayLength());
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (ReadClient(element) is not { } client) return null;
                clients.Add(client);
            }
            return clients;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The workspace from <c>j/activeworkspace</c>; null when the text isn't one.</summary>
    public static HyprlandWorkspace? ParseActiveWorkspace(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, JsonOptions);
            return ReadWorkspace(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HyprlandClient? ReadClient(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (ReadInt(e, "pid") is not { } pid || ReadString(e, "class") is not { } cls || ReadString(e, "title") is not { } title) return null;
        if (!e.TryGetProperty("workspace", out var workspace) || ReadWorkspace(workspace) is not { } place) return null;
        bool mapped = !e.TryGetProperty("mapped", out var m) || m.ValueKind != JsonValueKind.False;
        return new HyprlandClient(pid, cls, title, mapped, place);
    }

    private static HyprlandWorkspace? ReadWorkspace(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        return ReadInt(e, "id") is { } id && ReadString(e, "name") is { } name ? new HyprlandWorkspace(id, name) : null;
    }

    private static int? ReadInt(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;

    private static string? ReadString(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- Matching ---------------------------------------------------------------------------------

    /// <summary>
    /// The workspace of <paramref name="window"/>, or null when it can't be told. Only mapped clients of this process
    /// and class count. A process with one window and one such client needs no title; otherwise the title must match,
    /// and clients with the same title count only when they are all on one workspace.
    /// </summary>
    public static HyprlandWorkspace? Find(IReadOnlyList<HyprlandClient> clients, in HyprlandWindowIdentity window)
    {
        HyprlandWorkspace? titled = null, only = null;
        int own = 0;
        bool titleClash = false;
        foreach (var c in clients)
        {
            if (!c.Mapped || c.Pid != window.Pid || !string.Equals(c.Class, window.Class, StringComparison.Ordinal)) continue;
            own++;
            only = c.Workspace;
            if (!string.Equals(c.Title, window.Title, StringComparison.Ordinal)) continue;
            titleClash |= titled is { } seen && seen.Id != c.Workspace.Id;
            titled = c.Workspace;
        }
        if (own == 1 && window.WindowsInProcess == 1) return only;
        return titleClash || window.Title.Length == 0 ? null : titled;
    }

    /// <summary>The window's workspace when it isn't the one being looked at; null when it is, or when that isn't known.</summary>
    public static HyprlandWorkspace? Elsewhere(HyprlandSnapshot snapshot, in HyprlandWindowIdentity window)
    {
        if (snapshot.Active.Id == InvalidWorkspaceId || Find(snapshot.Clients, window) is not { } workspace) return null;
        return workspace.Id != snapshot.Active.Id ? workspace : null;
    }

    // ---- Wording ----------------------------------------------------------------------------------

    /// <summary>"workspace 3", "workspace dev", "special workspace", "special workspace notes"; null when it has no usable name or number.</summary>
    public static string? Describe(HyprlandWorkspace workspace)
    {
        if (workspace.Id == InvalidWorkspaceId) return null;
        string name = NotificationText.Clean(workspace.Name, MaxNameChars);
        if (name == SpecialName) return "special workspace";
        if (name.StartsWith(SpecialPrefix, StringComparison.Ordinal))
            return name.Length > SpecialPrefix.Length ? $"special workspace {name[SpecialPrefix.Length..]}" : "special workspace";
        if (name.Length > 0) return $"workspace {name}";
        return workspace.Id > 0 ? "workspace " + workspace.Id.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>For the in-window places, next to "Window 2": "Workspace 3".</summary>
    public static string? Label(HyprlandWorkspace workspace) =>
        Describe(workspace) is { } text ? string.Concat(char.ToUpperInvariant(text[0]).ToString(), text.AsSpan(1)) : null;

    /// <summary>
    /// "Claude needs permission, workspace 3". The summary of a desktop notification is plain text, though some daemons
    /// parse markup in it, and escaping would show up literally on the rest: the characters that matter are dropped from the name.
    /// </summary>
    public static string DesktopTitle(string title, HyprlandWorkspace workspace) =>
        Describe(workspace with { Name = WithoutMarkup(workspace.Name) }) is { } text ? $"{title}, {text}" : title;

    private static string WithoutMarkup(string s) => s.Replace("&", "").Replace("<", "").Replace(">", "");
}
