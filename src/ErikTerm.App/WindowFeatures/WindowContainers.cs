using ErikTerm.App.Containers;
using ErikTerm.App.Integrations;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Pty;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// Panes whose shell runs in a container: a pane's foreground program is a container exec (<c>docker exec -it …</c>,
/// <c>podman exec</c>, <c>devcontainer exec</c>, or the exec <c>erikterm devcontainer</c> becomes), so the window looks
/// the container up (in the background: <c>inspect</c>) and remembers it on the pane (<see cref="Pane.Container"/>).
/// Then splits and new tabs open another shell in the same container and directory, the pane's directory maps to the
/// host for git status and file links, the session restores the shell in the container, and the tab shows a container
/// icon (Ui/TabBar.cs). Leaving the container (the exec ends) clears it.
///
/// Runs in the integrations' scan (every 250 ms): the foreground process group is one ioctl per pane, its argv is
/// read again only when the group changes.
/// </summary>
internal sealed class WindowContainers(IFeatureWindow window, IPaneLauncher launcher, BackgroundJobs jobs, PaneTracks tracks)
{
    private sealed class ContainerWatch
    {
        public int Pgid = -2;
        public long CheckedAt;
        public bool Pending;     // erikterm devcontainer is still setting up: it execs the engine in place (same pid)
        public bool Live;
        public int Generation;   // a lookup's result only counts for the program it was started for
        public string? Key;      // the exec the pane's Container (or the lookup in flight) is for
        public string? UriBefore; // the OSC 7 report when the foreground program changed: the host shell's, not the container's
    }

    private readonly Dictionary<int, ContainerWatch> _containerWatches = [];
    private readonly List<int> _deadContainerWatches = [];

    private const int PendingRecheckMs = 400;

    /// <summary>Scan hook (<see cref="WindowIntegrations"/>), before the directories are tracked.</summary>
    public void Track(long now)
    {
        foreach (var w in _containerWatches.Values) w.Live = false;
        var tabs = window.Tabs;
        var items = tabs.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var leaves = (ReferenceEquals(items[i], tabs.Active) ? window.Layout : items[i].Layout).LeafList;
            for (int j = 0; j < leaves.Count; j++) TrackContainer(leaves[j], now);
        }
        if (tabs.Count == 0) { var leaves = window.Layout.LeafList; for (int j = 0; j < leaves.Count; j++) TrackContainer(leaves[j], now); }
        if (_containerWatches.Count == 0) return;
        foreach (var (id, w) in _containerWatches) if (!w.Live) _deadContainerWatches.Add(id);
        foreach (int id in _deadContainerWatches) _containerWatches.Remove(id);
        _deadContainerWatches.Clear();
    }

    private void TrackContainer(Pane pane, long now)
    {
        if (!_containerWatches.TryGetValue(pane.Id, out var w)) _containerWatches[pane.Id] = w = new ContainerWatch();
        w.Live = true;
        if (pane.Exited) { SetContainer(pane, w, null, null); return; }
        int pgid;
        try { pgid = pane.Pty.ForegroundProcessGroup(); }
        catch (ObjectDisposedException) { return; }
        if (pgid == w.Pgid && !(w.Pending && now - w.CheckedAt >= PendingRecheckMs)) return;
        Recheck(pane, w, pgid, now);
    }

    /// <summary>Looks at the pane's foreground program <paramref name="pgid"/> (a new one, or erikterm devcontainer still setting up).</summary>
    private void Recheck(Pane pane, ContainerWatch w, int pgid, long now)
    {
        // Taken when the program changes, before erikterm devcontainer's setup: by the time it execs the engine, the
        // shell in the container may have reported its directory already.
        if (pgid != w.Pgid) w.UriBefore = pgid == pane.Pty.ProcessId && w.Pgid == -2 ? null : pane.Term.WorkingDirectoryUri;
        w.Pgid = pgid;
        w.CheckedAt = now;
        string[]? argv = pgid > 0 ? ProcessInfo.CommandLine(pgid) : null;
        w.Pending = ContainerExec.IsDevcontainerCommand(argv, Environment.ProcessPath);
        if (w.Pending) return;
        if (ContainerExec.Parse(argv) is not { } target) { SetContainer(pane, w, null, null); return; }

        string key = string.Join('\0', argv!);
        if (key == w.Key) return; // the same exec, looked up already (or being looked up)
        w.Key = key;
        StartLookUp(pane, w, target, pgid, key);
    }

    /// <summary>Asks the engine about <paramref name="target"/> off the UI thread; the answer counts while the exec is still the pane's.</summary>
    private void StartLookUp(Pane pane, ContainerWatch w, ContainerExecTarget target, int pgid, string key)
    {
        int generation = ++w.Generation;
        string? uri = w.UriBefore;
        // devcontainer exec without --workspace-folder means the folder it was started in.
        // A compose exec's project is the one whose directory holds where it was started.
        string? startedIn = target is { ViaDevcontainerCli: true, Container: null, WorkspaceFolder: null } or { ComposeService: not null }
            ? ProcessInfo.WorkingDirectory(pgid) : null;
        jobs.Run(() => LookUpContainer(target, startedIn), info =>
        {
            if (w.Generation != generation || !w.Live) return;
            SetContainer(pane, w, info is null ? null : new PaneContainer(target, info, uri), key);
        });
    }

    /// <summary>Thread pool: the container an exec talks to, or null when the engine doesn't know it.</summary>
    private static ContainerInfo? LookUpContainer(ContainerExecTarget target, string? startedIn)
    {
        var engine = ContainerEngine.Find(target.Engine, target.EngineOptions) ?? (target.ViaDevcontainerCli ? ContainerEngine.Find(null) : null);
        if (engine is null) return null;
        if (target.Container is { } id) return engine.Inspect(id);
        if (target.ComposeService is { } service) return engine.FindComposeContainer(service, target.ComposeProject, startedIn);
        string? folder = target.WorkspaceFolder ?? startedIn;
        return folder is null ? null : engine.FindDevContainer(Path.GetFullPath(folder));
    }

    private void SetContainer(Pane pane, ContainerWatch w, PaneContainer? container, string? key)
    {
        if (container is null) w.Key = key;
        if (ReferenceEquals(pane.Container, container)) return;
        pane.Container = container;
        if (tracks.Of(pane) is { } tr) tr.OscSeen = false; // its directory (and repository) is another one now
        foreach (var tab in window.Tabs.Items) tab.TitleAt = 0;
        window.Redraw();
    }

    /// <summary>
    /// The command for a shell opened from <paramref name="from"/> (a split, a new tab): another shell in its container,
    /// in the directory it is in there; null when it isn't in one (the default shell then).
    /// </summary>
    public string[]? ShellFor(Pane from)
    {
        if (from.Container is not { } c || Environment.ProcessPath is not { } self) return null;
        return c.ShellCommand(self, c.ContainerDirectory(from.Term.WorkingDirectoryUri), window.Settings.ShellIntegration == ShellIntegrationMode.Auto);
    }

    /// <summary>Whether any pane of the tab has its shell in a container (the tab's container icon).</summary>
    public static bool TabInContainer(Tab tab)
    {
        var leaves = tab.Layout.LeafList;
        for (int i = 0; i < leaves.Count; i++) if (leaves[i].Container is not null) return true;
        return false;
    }

    /// <summary>A container pane's tab title: the directory it is in there (the exec's own name says nothing).</summary>
    public static string Title(PaneContainer c, Pane pane)
    {
        string dir = c.ContainerDirectory(pane.Term.WorkingDirectoryUri).TrimEnd('/');
        return dir.LastIndexOf('/') is var slash && slash >= 0 && slash < dir.Length - 1 ? dir[(slash + 1)..] : c.Info.DisplayName;
    }

    /// <summary>Session save: the container and where in it, so the next start opens the shell there again.</summary>
    public static Session.SessionContainer? Capture(Pane pane)
    {
        if (pane.Container is not { } c) return null;
        return new Session.SessionContainer
        {
            Engine = c.Target.Engine,
            EngineOptions = c.Target.EngineOptions.Count > 0 ? [.. c.Target.EngineOptions] : null,
            Id = c.Info.Id,
            WorkspaceFolder = c.Info.LocalFolder,
            User = c.Target.User,
            Workdir = c.ContainerDirectory(pane.Term.WorkingDirectoryUri),
        };
    }

    /// <summary>Session restore: the pane command for a saved container shell, or null when the saved entry isn't usable.</summary>
    public string[]? RestoreCommand(Session.SessionContainer? saved)
    {
        if (saved is null || Environment.ProcessPath is not { } self) return null;
        return saved.Command(self, window.Settings.ShellIntegration == ShellIntegrationMode.Auto);
    }

    // ---- Command palette -----------------------------------------------------------------------------

    /// <summary>
    /// "Dev Container: Open" and "Rebuild" for the focused pane's folder, when it has a dev container configuration (or
    /// the pane is in a dev container): a new tab running <c>erikterm devcontainer</c>, whose output (a build) is visible.
    /// </summary>
    public void AddCommands(List<Command> list)
    {
        if (Environment.ProcessPath is not { } self) return;
        var focused = window.Focused;
        string? folder = focused.Container?.Info.LocalFolder;
        if (folder is null && tracks.Cwd(focused) is { } cwd) folder = ContainerEngine.ConfigFolderFor(cwd);
        if (folder is null) return;
        string name = Path.GetFileName(folder.TrimEnd('/', '\\'));
        bool integration = window.Settings.ShellIntegration == ShellIntegrationMode.Auto;
        string[] Argv(string mode, params string[] extra) =>
            [self, "devcontainer", mode, "--workspace-folder", folder, .. extra, "--fallback-shell", .. integration ? Array.Empty<string>() : ["--no-shell-integration"]];
        if (focused.Container is null)
            list.Add(new Command { Id = "devcontainer-open", Category = "Dev Container", Title = $"Open {name} in Its Container", Icon = Icons.Container, Run = () => OpenContainerTab(folder, Argv("shell")) });
        list.Add(new Command { Id = "devcontainer-rebuild", Category = "Dev Container", Title = $"Rebuild {name}'s Container", Icon = Icons.Refresh, Run = () => OpenContainerTab(folder, Argv("up", "--rebuild")) });
    }

    private void OpenContainerTab(string folder, string[] argv)
    {
        window.ClosePalette(Environment.TickCount64);
        launcher.SpawnTabIn(folder, argv);
    }
}
