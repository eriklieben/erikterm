using ErikTerm.App.Panes;

namespace ErikTerm.App.Containers;

/// <summary>
/// The container a pane's shell runs in (WindowFeatures/WindowContainers.cs sets it when the foreground program is a container
/// exec, and clears it when that ends). Translates the directory the shell inside reports (OSC 7, a path in the
/// container) to the host, so git status, file links and the repository badge follow it.
/// </summary>
internal sealed class PaneContainer(ContainerExecTarget target, ContainerInfo info, string? uriAtStart)
{
    public ContainerExecTarget Target { get; } = target;
    public ContainerInfo Info { get; } = info;

    /// <summary>The OSC 7 report from before the container's shell started (the host shell's): not a directory in the container.</summary>
    private readonly string? _uriAtStart = uriAtStart;

    /// <summary>
    /// Where the shell inside is: its OSC 7 (whatever host name the container gives itself), else the exec's
    /// <c>--workdir</c>, else the workspace folder.
    /// </summary>
    public string ContainerDirectory(string? uri)
    {
        if (uri is not null && !ReferenceEquals(uri, _uriAtStart) && PaneCwd.FromOsc7(uri, static _ => true, windows: false) is { Length: > 0 } reported)
            return reported;
        return Target.Workdir is { Length: > 0 } w && w[0] == '/' ? w : Info.WorkspaceFolder;
    }

    /// <summary>That directory on the host (through the bind mounts), else the dev container's folder, else null.</summary>
    public string? HostDirectory(string? uri, Func<string, bool> exists)
    {
        if (ContainerPaths.ToHost(Info.Mounts, ContainerDirectory(uri)) is { } host && exists(host)) return host;
        return Info.LocalFolder is { } folder && exists(folder) ? folder : null;
    }

    /// <summary>A path printed in the container, on the host: absolute ones through the bind mounts, others unchanged (they resolve against the host directory).</summary>
    public string ToHostPath(string path) =>
        path.StartsWith('/') && ContainerPaths.ToHost(Info.Mounts, path) is { } host ? host : path;

    /// <summary>The pane command for another shell in this container, starting in <paramref name="workdir"/>.</summary>
    public string[] ShellCommand(string self, string workdir, bool shellIntegration) =>
        ContainerShellCommand.Build(self, Target.Engine, Target.EngineOptions, Info.Id, Info.LocalFolder, Target.User, workdir, shellIntegration);
}

/// <summary>
/// The pane command for a shell in a container: <c>erikterm devcontainer shell</c> with the container, its folder (to
/// find it again after a rebuild), the user and the directory, and <c>--fallback-shell</c> (a host shell when the
/// container is gone, never a dead pane).
/// </summary>
internal static class ContainerShellCommand
{
    public static string[] Build(string self, string engine, IReadOnlyList<string> engineOptions, string id, string? workspaceFolder,
        string? user, string? workdir, bool shellIntegration)
    {
        var argv = new List<string> { self, "devcontainer", "shell", "--engine", engine };
        foreach (var o in engineOptions) argv.AddRange(["--engine-option", o]);
        argv.AddRange(["--container", id]);
        if (workspaceFolder is { Length: > 0 } folder) argv.AddRange(["--workspace-folder", folder]);
        if (user is { Length: > 0 }) argv.AddRange(["--user", user]);
        if (workdir is { Length: > 0 }) argv.AddRange(["--workdir", workdir]);
        argv.Add("--fallback-shell");
        if (!shellIntegration) argv.Add("--no-shell-integration");
        return [.. argv];
    }
}
