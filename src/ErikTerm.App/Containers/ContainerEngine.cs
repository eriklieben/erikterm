using ErikTerm.App.Integrations;

namespace ErikTerm.App.Containers;

/// <summary>
/// docker or podman, run through <see cref="ToolRunner"/> (argv lists, a timeout, nothing that can prompt): looking a
/// container up, finding a folder's dev container by its label, starting a stopped one. <see cref="Options"/> are the
/// global options (another context, host or connection) that go before every subcommand.
/// </summary>
internal sealed class ContainerEngine(string program, IReadOnlyList<string> options)
{
    private const string FilterFlag = "--filter";

    public const int TimeoutMs = 10_000;

    public string Program { get; } = program;
    public IReadOnlyList<string> Options { get; } = options;

    /// <summary>The engine argv: program, global options, then <paramref name="args"/>.</summary>
    public List<string> Argv(params ReadOnlySpan<string> args)
    {
        var argv = new List<string>(1 + Options.Count + args.Length) { Program };
        argv.AddRange(Options);
        foreach (var a in args) argv.Add(a);
        return argv;
    }

    /// <summary>
    /// The engine to use: <paramref name="requested"/> (a name or path of docker / podman), else docker, else podman,
    /// as found on PATH. Null when none is installed, or the request names something else.
    /// </summary>
    public static ContainerEngine? Find(string? requested, IReadOnlyList<string>? options = null, Func<string, string?>? find = null)
    {
        find ??= ToolRunner.Find;
        options ??= [];
        if (requested is { Length: > 0 })
        {
            if (ContainerExec.EngineName(requested) is not { } name) return null;
            bool isPath = requested.Contains('/') || requested.Contains('\\');
            string? path = isPath ? requested : find(name);
            return path is null || (isPath && !File.Exists(path)) ? null : new ContainerEngine(path, options);
        }
        foreach (var name in (ReadOnlySpan<string>)["docker", "podman"])
            if (find(name) is { } path) return new ContainerEngine(path, options);
        return null;
    }

    public ContainerInfo? Inspect(string container)
    {
        var r = ToolRunner.Run(Argv("inspect", "--type", "container", container), null, TimeoutMs);
        return r.Ok ? ContainerInfo.ParseInspect(r.Stdout) : null;
    }

    /// <summary>The dev container made for <paramref name="localFolder"/>: a running one first, else a stopped one; null when there is none.</summary>
    public ContainerInfo? FindDevContainer(string localFolder)
    {
        foreach (bool all in (ReadOnlySpan<bool>)[false, true])
        {
            string filter = $"label={ContainerInfo.LocalFolderLabel}={localFolder}";
            var argv = all ? Argv("ps", "-a", "-q", "--no-trunc", FilterFlag, filter) : Argv("ps", "-q", "--no-trunc", FilterFlag, filter);
            var r = ToolRunner.Run(argv, null, TimeoutMs);
            if (!r.Ok) return null;
            foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Inspect(line) is { } info) return info;
        }
        return null;
    }

    /// <summary>The running container of a compose service (in <paramref name="project"/> when given) a compose exec started in <paramref name="cwd"/> talks to.</summary>
    public ContainerInfo? FindComposeContainer(string service, string? project, string? cwd)
    {
        var argv = Argv("ps", "-q", "--no-trunc", FilterFlag, $"label={ContainerInfo.ComposeServiceLabel}={service}");
        if (project is { Length: > 0 }) argv.AddRange([FilterFlag, $"label={ContainerInfo.ComposeProjectLabel}={project}"]);
        var r = ToolRunner.Run(argv, null, TimeoutMs);
        if (!r.Ok) return null;
        var candidates = new List<ContainerInfo>();
        foreach (var line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20))
            if (Inspect(line) is { } info) candidates.Add(info);
        return ContainerInfo.PickCompose(candidates, cwd);
    }

    public bool Start(string id) => ToolRunner.Run(Argv("start", id), null, 60_000).Ok;

    /// <summary>
    /// The devcontainer CLI: on PATH, else where npm, pnpm or VS Code (its "Install devcontainer CLI" command) put it,
    /// since a session started from the desktop often lacks those PATH entries.
    /// </summary>
    public static string? FindDevcontainerCli()
    {
        if (ToolRunner.Find("devcontainer") is { } onPath) return onPath;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] dirs = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm")]
            :
            [
                Path.Combine(home, ".local", "bin"), Path.Combine(home, ".npm-global", "bin"), Path.Combine(home, ".local", "share", "pnpm"),
                Path.Combine(home, ".config", "Code", "User", "globalStorage", "ms-vscode-remote.remote-containers", "cli-bin"),
                Path.Combine(home, "Library", "Application Support", "Code", "User", "globalStorage", "ms-vscode-remote.remote-containers", "cli-bin"),
                "/usr/local/bin", "/opt/homebrew/bin",
            ];
        return ToolRunner.Search(OperatingSystem.IsWindows() ? "devcontainer.cmd" : "devcontainer", string.Join(Path.PathSeparator, dirs));
    }

    /// <summary>The folder a dev container is for: <c>.devcontainer/devcontainer.json</c>, <c>.devcontainer.json</c> or <c>.devcontainer/*/devcontainer.json</c> in it.</summary>
    public static bool HasDevcontainerConfig(string folder)
    {
        try
        {
            if (File.Exists(Path.Combine(folder, ".devcontainer", "devcontainer.json")) || File.Exists(Path.Combine(folder, ".devcontainer.json"))) return true;
            string dir = Path.Combine(folder, ".devcontainer");
            return Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any(d => File.Exists(Path.Combine(d, "devcontainer.json")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The nearest folder from <paramref name="start"/> up that has a dev container configuration, or null.</summary>
    public static string? ConfigFolderFor(string start, Func<string, bool>? hasConfig = null)
    {
        hasConfig ??= HasDevcontainerConfig;
        for (string? d = Path.GetFullPath(start); d is not null; d = Path.GetDirectoryName(d))
            if (hasConfig(d)) return d;
        return null;
    }

    /// <summary>The folder a dev container belongs to, from <paramref name="start"/>: the nearest one up with a dev container configuration, else the git repository's root, else <paramref name="start"/>.</summary>
    public static string WorkspaceFolderFor(string start, Func<string, bool>? hasConfig = null)
    {
        hasConfig ??= HasDevcontainerConfig;
        string full = Path.GetFullPath(start);
        if (ConfigFolderFor(full, hasConfig) is { } configured) return configured;
        for (string? d = full; d is not null; d = Path.GetDirectoryName(d))
            if (Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"))) return d;
        return full;
    }
}
