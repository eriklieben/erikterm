using System.Text.Json;

namespace ErikTerm.App.Containers;

/// <summary>A bind mount: a host directory and where the container sees it.</summary>
internal readonly record struct ContainerMount(string Source, string Destination);

/// <summary>
/// What <c>docker inspect</c> / <c>podman inspect</c> says about a container, as far as erikterm needs it: which one it
/// is, whether it runs, the dev container labels (the host folder it was made for, its <c>remoteUser</c>), and its
/// bind mounts, which translate paths between the container and the host.
/// </summary>
internal sealed record ContainerInfo(
    string Id,
    string Name,
    bool Running,
    string StartedAt,
    string? LocalFolder,
    string? RemoteUser,
    string? WorkingDir,
    IReadOnlyList<ContainerMount> Mounts,
    string? ComposeWorkingDir = null)
{
    public const string LocalFolderLabel = "devcontainer.local_folder";
    public const string ComposeServiceLabel = "com.docker.compose.service";
    public const string ComposeProjectLabel = "com.docker.compose.project";
    public const string ComposeWorkingDirLabel = "com.docker.compose.project.working_dir";
    public const string MetadataLabel = "devcontainer.metadata";

    /// <summary>The short id people know from <c>docker ps</c>.</summary>
    public string ShortId => Id.Length > 12 ? Id[..12] : Id;

    /// <summary>The dev container's folder name, else the container's name.</summary>
    public string DisplayName
    {
        get
        {
            if (LocalFolder is { Length: > 0 } f && LastSegment(f) is { Length: > 0 } n) return n;
            return Name.Length > 0 ? Name : ShortId;
        }
    }

    /// <summary>Where the workspace is inside: its folder's mount, else the image's working directory, else /.</summary>
    public string WorkspaceFolder =>
        (LocalFolder is { } f ? ContainerPaths.ToContainer(Mounts, f) : null) ?? (WorkingDir is { Length: > 0 } w ? w : "/");

    /// <summary>The first container in an inspect array, or null when there is none or it isn't one.</summary>
    public static ContainerInfo? ParseInspect(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) root = root.GetArrayLength() > 0 ? root[0] : default;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? id = Str(root, "Id");
            if (id is not { Length: > 0 }) return null;
            string name = (Str(root, "Name") ?? "").TrimStart('/');
            bool running = root.TryGetProperty("State", out var state) && state.ValueKind == JsonValueKind.Object
                && state.TryGetProperty("Running", out var r) && r.ValueKind == JsonValueKind.True;
            string startedAt = state.ValueKind == JsonValueKind.Object ? Str(state, "StartedAt") ?? "" : "";
            string? localFolder = null, metadata = null, workingDir = null, composeDir = null;
            if (root.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object)
            {
                workingDir = Str(config, "WorkingDir");
                if (config.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
                {
                    localFolder = Str(labels, LocalFolderLabel);
                    metadata = Str(labels, MetadataLabel);
                    composeDir = Str(labels, ComposeWorkingDirLabel);
                }
            }
            return new ContainerInfo(id, name, running, startedAt, localFolder, RemoteUserOf(metadata), workingDir, MountsOf(root), composeDir);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Which of a service's containers a compose exec talks to: the one whose project directory holds
    /// <paramref name="cwd"/> (the deepest, when projects nest), else the only one; null when that can't be told.
    /// </summary>
    public static ContainerInfo? PickCompose(IReadOnlyList<ContainerInfo> candidates, string? cwd)
    {
        ContainerInfo? best = null;
        int bestLength = -1;
        foreach (var c in candidates)
        {
            if (cwd is null || c.ComposeWorkingDir is not { Length: > 0 } dir) continue;
            string d = dir.TrimEnd('/');
            bool holds = cwd == d || cwd.StartsWith(d + "/", StringComparison.Ordinal);
            if (holds && d.Length > bestLength) { best = c; bestLength = d.Length; }
        }
        return best ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    /// <summary>
    /// The user a dev container's tools run as: the last <c>remoteUser</c> in the metadata label (an array of the
    /// image's, the features' and devcontainer.json's settings, later ones winning), else the last <c>containerUser</c>.
    /// </summary>
    public static string? RemoteUserOf(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            string? remote = null, container = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray()) TakeUsers(e, ref remote, ref container);
            else TakeUsers(doc.RootElement, ref remote, ref container);
            return remote ?? container;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The users one metadata entry sets, over the ones of the entries before it.</summary>
    private static void TakeUsers(JsonElement e, ref string? remote, ref string? container)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        remote = Str(e, "remoteUser") ?? remote;
        container = Str(e, "containerUser") ?? container;
    }

    private static List<ContainerMount> MountsOf(JsonElement root)
    {
        var mounts = new List<ContainerMount>();
        if (!root.TryGetProperty("Mounts", out var list) || list.ValueKind != JsonValueKind.Array) return mounts;
        foreach (var m in list.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            // Volumes live in the engine's storage (not a directory of ours); only bind mounts map paths.
            if (!string.Equals(Str(m, "Type"), "bind", StringComparison.OrdinalIgnoreCase)) continue;
            if (Str(m, "Source") is { Length: > 0 } src && Str(m, "Destination") is { Length: > 0 } dst)
                mounts.Add(new ContainerMount(src, dst));
        }
        return mounts;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string LastSegment(string path) => path.TrimEnd('/', '\\') is var t && t.LastIndexOfAny(['/', '\\']) is var i && i >= 0 ? t[(i + 1)..] : path;
}

/// <summary>Paths between a container and the host, through its bind mounts (the longest matching mount wins).</summary>
internal static class ContainerPaths
{
    /// <summary>The host path of a path inside the container, or null when no bind mount holds it.</summary>
    public static string? ToHost(IReadOnlyList<ContainerMount> mounts, string containerPath) =>
        Translate(mounts, containerPath, m => m.Destination, m => m.Source);

    /// <summary>Where the container sees a host path, or null when no bind mount holds it.</summary>
    public static string? ToContainer(IReadOnlyList<ContainerMount> mounts, string hostPath) =>
        Translate(mounts, hostPath, m => m.Source, m => m.Destination);

    private static string? Translate(IReadOnlyList<ContainerMount> mounts, string path, Func<ContainerMount, string> from, Func<ContainerMount, string> to)
    {
        if (path.Length == 0 || path[0] != '/' && !IsDrivePath(path)) return null;
        string? best = null;
        int bestLength = -1;
        foreach (var m in mounts)
        {
            string prefix = Trim(from(m));
            if (prefix.Length <= bestLength) continue;
            if (path == prefix || prefix == "/") { best = Join(to(m), prefix == "/" ? path.TrimStart('/') : ""); bestLength = prefix.Length; }
            else if (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal) && path[prefix.Length] == '/')
            {
                best = Join(to(m), path[(prefix.Length + 1)..]);
                bestLength = prefix.Length;
            }
        }
        return best;
    }

    private static bool IsDrivePath(string p) => p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '/' or '\\';

    private static string Trim(string p) => p.Length > 1 ? p.TrimEnd('/') : p;

    private static string Join(string root, string rest)
    {
        if (rest.Length == 0) return Trim(root);
        return Trim(root) is "/" ? "/" + rest : Trim(root) + "/" + rest;
    }
}
