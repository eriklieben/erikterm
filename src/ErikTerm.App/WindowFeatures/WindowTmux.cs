using ErikTerm.App.Ipc;
using ErikTerm.App.Panes;
using ErikTerm.App.Teammates;
using ErikTerm.App.Tmux;
using ErikTerm.App.Ui;
using ErikTerm.Pty;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>What <see cref="WindowTmux"/> needs beyond the feature window.</summary>
internal interface ITmuxWindow : IFeatureWindow
{
    /// <summary>The process has a control socket and a shim directory (App.Tmux.cs).</summary>
    bool TmuxShimAvailable { get; }
}

/// <summary>
/// tmux emulation, window half (the shim directory and environment are App.Tmux.cs): the palette toggle, what
/// <c>list</c> adds for the shim, and the requests the shim needs beyond <c>erikterm cli</c>'s. Requests reach the
/// window holding the target pane / tab (pane and tab ids are process-wide), so tmux targets work across windows.
/// </summary>
internal sealed class WindowTmux(ITmuxWindow window, IPaneLauncher launcher, IpcTargets targets, WindowTeammates<Pane> teammates, Lock gate)
{
    private bool? _emulationOverride; // the palette toggle, for this window

    private static long Now => Environment.TickCount64;

    /// <summary>New panes get $TMUX and the shim (tmux_emulation, or the palette toggle for this window).</summary>
    public bool Emulation => _emulationOverride ?? window.Settings.TmuxEmulation;

    /// <summary>
    /// The pane's foreground program is real tmux (not the shim, which renames itself and is this executable).
    /// While it is, the emulation leaves the pane alone and Ctrl+H/J/K/L pass through to tmux.
    /// </summary>
    public static bool IsRealTmux(Pane pane)
    {
        if (!TmuxSession.IsTmuxProcessName(pane.Pty.ForegroundProcessName)) return false;
        int pgid = pane.Pty.ForegroundProcessGroup();
        if (pgid <= 0 || ProcessInfo.ExecutablePath(pgid) is not { } exe) return true;
        return !TmuxSession.IsShimExecutable(exe, Environment.ProcessPath);
    }

    /// <summary>Hook from ListPanes: what the shim needs for targets and formats.</summary>
    public void AddInfo(PaneInfo info, Pane pane, RectI bounds, bool tabActive)
    {
        info.TabActive = tabActive;
        var t = pane.Term;
        info.Pid = pane.Pty.ProcessId;
        info.Left = Math.Max(0, (pane.Target.X - bounds.X) / Math.Max(1, window.CellWidth));
        info.Top = Math.Max(0, (pane.Target.Y - bounds.Y) / Math.Max(1, window.CellHeight));
        info.RealTmux = IsRealTmux(pane);
        info.PaneTitle = pane.TmuxTitle;
        info.CreatedByTmux = pane.CreatedByTmux;
        info.CursorKeysApplication = t.CursorKeysApplication;
        info.HistorySize = (int)Math.Min(int.MaxValue, t.ScreenTopLine - t.FirstLine);
        info.HistoryLimit = window.Settings.ScrollbackLines;
        int pgid = pane.Pty.ForegroundProcessGroup();
        info.CurrentPath = ProcessInfo.WorkingDirectory(pgid) ?? pane.WorkingDirectory ?? ProcessInfo.WorkingDirectory(pane.Pty.ProcessId);
    }

    // ==== Requests the shim adds ===============================================================

    public IpcResponse? HandleIpc(IpcRequest r) => r.Command switch
    {
        "respawn-pane" => IpcRespawnPane(r),
        "select-layout" => IpcSelectLayout(r),
        "resize-pane" => IpcResizePane(r),
        "set-pane-title" => IpcSetPaneTitle(r),
        _ => null,
    };

    /// <summary>tmux respawn-pane: a new program in an existing pane (Claude Code starts each agent this way, in a pane made with `cat`).</summary>
    private IpcResponse IpcRespawnPane(IpcRequest r)
    {
        var (tab, pane) = targets.Target(r);
        if (!r.Kill && !pane.Exited) throw new IpcException($"pane %{pane.Id} still active");
        if (r.Cwd is { } cwd && !Directory.Exists(cwd)) throw new IpcException($"no such directory: {cwd}");
        var settings = window.Settings;
        string[] argv = r.Argv ?? launcher.DefaultShell();
        teammates.OnRespawn(r, window.Tabs.IndexOf(tab), pane, argv); // Claude Code starting a teammate (Teammates/WindowTeammates.cs)
        var env = launcher.ChildEnvironment();
        launcher.AddIpcEnvironment(env, Emulation, pane.Id);
        string? directory = r.Cwd ?? pane.WorkingDirectory ?? ProcessInfo.WorkingDirectory(pane.Pty.ProcessId) ?? settings.DefaultCwd;
        Worktrees.WorktreePorts.Shared.AddEnvironment(env, directory);
        int cols, rows;
        lock (gate) (cols, rows) = (pane.Term.Columns, pane.Term.Rows);
        var pty = Pty.Pty.Spawn(new PtyOptions
        {
            Program = argv[0], Arguments = argv, Environment = env, Columns = cols, Rows = rows,
            WorkingDirectory = directory,
        });
        pty.Resize(cols, rows, pane.Target.W, pane.Target.H);
        lock (gate) pane.Term.TerminalName = env.TryGetValue("TERM", out var termName) ? termName : Core.TermCapabilities.BaseEntry;
        bool wasHeld = pane.Held;
        pane.Respawn(pty, gate, window.Wake);
        pane.LaunchArgv = argv; // what Restart Pane runs (Windowing/ExitHoldFlow.cs)
        pane.LaunchCwd = r.Cwd ?? pane.LaunchCwd;
        if (wasHeld)
        {
            pane.ExitBar?.Remove(launcher.ExitBarKey, Now); // Windowing/ExitHoldFlow.cs
            window.ApplyLayout(animate: false); // the exit bar's rows go back to the grid
        }
        pane.TmuxTitle ??= TmuxSession.TitleFromCommand(argv);
        return IpcResponse.Success();
    }

    private IpcResponse IpcSelectLayout(IpcRequest r)
    {
        var tab = targets.Tab(r);
        bool active = targets.IsActive(tab);
        var layout = active ? window.Layout : tab.Layout;
        var preset = r.Layout == "even" ? (LayoutPreset?)null : LayoutPresets.Parse(r.Layout ?? "") ?? throw new IpcException($"invalid layout: {r.Layout}");
        if (teammates.TeamSubtree(layout) is { } team)
        {
            // Claude Code rebalancing its teammates: only the lead and its teammates move (Teammates/WindowTeammates.cs).
            if (preset is { } p) layout.Replace(team, LayoutPresets.Build(p, CollectLeaves(team)));
            window.ApplyLayout(animate: active);
            return IpcResponse.Success();
        }
        if (active && window.Zoomed is not null) window.SetZoom(null);
        else if (!active) tab.Zoomed = null;
        if (preset is { } all) LayoutPresets.Apply(layout, all);
        else LayoutPresets.Equalize(layout);
        window.ApplyLayout(animate: active);
        return IpcResponse.Success();
    }

    private IpcResponse IpcResizePane(IpcRequest r)
    {
        var (tab, pane) = targets.Target(r);
        var layout = targets.IsActive(tab) ? window.Layout : tab.Layout;
        var bounds = window.ContentBounds();
        int cellW = window.CellWidth, cellH = window.CellHeight, gap = window.Gap;
        int min = 4 * cellW;
        bool changed = false;
        int? w = null, h = null;
        if (r.Width is { } cw) w = cw * cellW;
        else if (r.WidthPercent is { } pw) w = bounds.W * pw / 100;
        if (r.Height is { } ch) h = ch * cellH;
        else if (r.HeightPercent is { } ph) h = bounds.H * ph / 100;
        if (w is { } px) changed |= LayoutPresets.Resize(layout, pane, SplitAxis.Horizontal, px, bounds, gap, min);
        if (h is { } py) changed |= LayoutPresets.Resize(layout, pane, SplitAxis.Vertical, py, bounds, gap, min);
        if (changed) window.ApplyLayout(animate: targets.IsActive(tab));
        return IpcResponse.Success();
    }

    private IpcResponse IpcSetPaneTitle(IpcRequest r)
    {
        var (_, pane) = targets.Target(r);
        pane.TmuxTitle = string.IsNullOrEmpty(r.Title) ? null : r.Title;
        teammates.OnPaneTitle(pane); // a teammate's name (Teammates/WindowTeammates.cs)
        window.Redraw();
        return IpcResponse.Success();
    }

    private static List<Pane> CollectLeaves(LayoutTree<Pane>.Node n)
    {
        var list = new List<Pane>();
        Walk(n);
        return list;

        void Walk(LayoutTree<Pane>.Node x)
        {
            if (x.IsLeaf) { list.Add(x.Pane!); return; }
            Walk(x.First!);
            Walk(x.Second!);
        }
    }

    // ==== Palette ==============================================================================

    public void AddCommands(List<Command> list)
    {
        list.Add(new Command
        {
            Id = "tmux-emulation", Category = "Terminal", Icon = Icons.Terminal,
            Title = Emulation ? "Toggle tmux Emulation (on: new panes look like tmux panes)" : "Toggle tmux Emulation (off)",
            Run = () =>
            {
                _emulationOverride = !Emulation;
                if (!window.TmuxShimAvailable && Emulation)
                    window.Toast(ToastKind.Error, "tmux emulation unavailable", "No control socket or shim directory for this window");
                else if (Emulation)
                    window.Toast(ToastKind.Success, "tmux emulation on", "New panes get $TMUX, so Claude Code opens agent panes here; `tmux` itself still starts real tmux");
                else
                    window.Toast(ToastKind.Info, "tmux emulation off", "New panes don't pretend to be inside tmux");
            },
        });
    }
}
