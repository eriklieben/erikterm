using ErikTerm.App.Agents;
using ErikTerm.App.Ipc;
using ErikTerm.App.Panes;
using ErikTerm.App.Tabs;
using ErikTerm.App.Teammates;
using ErikTerm.App.Vim;
using ErikTerm.App.Worktrees;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>What <see cref="WindowIpc"/> needs beyond the feature window.</summary>
internal interface IIpcWindow : IFeatureWindow
{
    /// <summary>Sets the window title until the focused program next sets its own.</summary>
    void SetTitleUntilProgramSets(string title);
}

/// <summary>The feature classes a request may be for, besides the panes and tabs themselves.</summary>
internal sealed record IpcHandlers(
    WindowTmux Tmux, WindowAgents<Pane> Agents, WindowTeammates<Pane> Teammates, WindowWorktrees<Pane> Worktrees,
    WindowTabColors<Tab> TabColors, WindowVim<Pane> Vim);

/// <summary>
/// `erikterm cli` requests for one window (App.Ipc.cs routes each request to the window holding its pane / tab, or
/// the one named by --window-id, else the most recently focused one). Runs on the main thread.
/// </summary>
internal sealed class WindowIpc(IIpcWindow window, IPaneLauncher launcher, IPaneInput input, IpcTargets targets, IpcHandlers handlers, Lock gate)
{
    private static long Now => Environment.TickCount64;

    // ==== Requests =============================================================================

    public IpcResponse Handle(IpcRequest r)
    {
        try
        {
            window.SyncActiveTab();
            return r.Command switch
            {
                "split-pane" => SplitPane(r),
                "spawn" => IpcSpawnTab(r),
                "send-text" => IpcSendText(r),
                "get-text" => IpcGetText(r),
                "activate-pane" => IpcActivatePane(r),
                "activate-pane-direction" => IpcActivateDirection(r),
                "get-pane-direction" => IpcGetDirection(r),
                "adjust-pane-size" => IpcAdjustSize(r),
                "zoom-pane" => IpcZoom(r),
                "kill-pane" => IpcKillPane(r),
                "activate-tab" => IpcActivateTab(r),
                "set-tab-title" => IpcSetTabTitle(r),
                "set-tab-color" => handlers.TabColors.IpcSet(targets.Tab(r), r), // Tabs/WindowTabColors.cs
                "finish-worktree" => IpcFinishWorktree(r), // from a review pane (WindowReview)
                "new-worktree" => handlers.Worktrees.IpcNewWorktree(targets.Target(r).Pane, r), // Worktrees/WindowWorktrees.cs
                "set-window-title" => IpcSetWindowTitle(r),
                "set-agent-status" => handlers.Agents.IpcSetStatus(targets.Target(r).Pane, r), // Agents/WindowAgents.cs
                "report-usage" => handlers.Agents.IpcReportUsage(targets.Target(r).Pane, r),
                "send-to-agent" => handlers.Agents.IpcSendToAgent(targets.Target(r).Pane, r), // from a review pane (Compare/ReviewCommentHost.cs)
                "set-pane-color" => handlers.Teammates.IpcSetColor(targets.Target(r).Pane, r), // Teammates/WindowTeammates.cs
                _ => handlers.Tmux.HandleIpc(r) ?? handlers.Vim.HandleIpc(r) ?? IpcResponse.Failure($"unknown command '{r.Command}'"), // WindowTmux, Vim/WindowVim.cs
            };
        }
        catch (IpcException ex)
        {
            return IpcResponse.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return IpcResponse.Failure(ex.Message);
        }
    }

    private void IpcShowTab(Tab tab)
    {
        if (!targets.IsActive(tab)) window.ActivateTab(window.Tabs.IndexOf(tab));
    }

    private static Direction IpcDirection(string? s) => s?.ToLowerInvariant() switch
    {
        "left" => Direction.Left,
        "right" => Direction.Right,
        "up" => Direction.Up,
        "down" => Direction.Down,
        _ => throw new IpcException($"invalid direction '{s}' (expected Left, Right, Up or Down)"),
    };

    /// <summary>Appends this window's panes (App.Ipc.cs lists every window).</summary>
    public void ListPanes(List<PaneInfo> list)
    {
        window.SyncActiveTab();
        var bounds = window.ContentBounds();
        var tabs = window.Tabs;
        lock (gate)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                var tab = tabs.Items[i];
                bool active = targets.IsActive(tab);
                var focused = active ? window.Focused : tab.Focused;
                var zoomed = active ? window.Zoomed : tab.Zoomed;
                foreach (var pane in tab.Layout.Leaves())
                {
                    var info = PaneInfoOf(tab, i, pane, focused, zoomed, active);
                    handlers.Tmux.AddInfo(info, pane, bounds, active); // WindowTmux
                    list.Add(info);
                }
            }
        }
    }

    private PaneInfo PaneInfoOf(Tab tab, int tabIndex, Pane pane, Pane? focused, Pane? zoomed, bool active)
    {
        var t = pane.Term;
        return new PaneInfo
        {
            WindowId = (int)window.Id, TabId = tab.Id, TabIndex = tabIndex, TabTitle = tab.CustomTitle, PaneId = pane.Id,
            Size = new PaneSize { Rows = t.Rows, Cols = t.Columns, PixelWidth = pane.Target.W, PixelHeight = pane.Target.H },
            // The title a program set, else (like wezterm) the foreground program's name.
            Title = t.Title is "" or "erikterm" ? pane.Pty.ForegroundProcessName ?? t.Title : t.Title,
            Cwd = t.WorkingDirectoryUri ?? "",
            CursorX = t.CursorX, CursorY = t.CursorY,
            IsActive = pane == focused, IsZoomed = pane == zoomed, IsFocused = active && pane == focused && window.IsCurrentWindow,
            TtyName = pane.Pty.SlavePath, ForegroundProcessName = pane.Pty.ForegroundProcessName,
            UserVars = t.UserVars.Count > 0 ? new Dictionary<string, string>(t.UserVars) : null,
            Agent = WindowAgents<Pane>.PaneFields(pane).Agent ?? (window.Settings.AgentStatus ? null : ((IAgentPane)pane).IdentifyForeground()?.Id), AgentStatus = WindowAgents<Pane>.PaneFields(pane).Status, // Agents/WindowAgents.cs
            Parked = pane.LayoutHidden, // Teammates/WindowTeammates.cs
        };
    }

    /// <summary>split-pane; also a worktree launch's split next to a pane (<paramref name="echo"/>: its setup's echo).</summary>
    public IpcResponse SplitPane(IpcRequest r, Action<Terminal>? echo = null)
    {
        var (tab, target) = targets.Target(r);
        if (r.Cwd is { } cwd && !Directory.Exists(cwd)) throw new IpcException($"no such directory: {cwd}");
        var dir = IpcDirection(r.Direction ?? "Down");
        var axis = dir is Direction.Left or Direction.Right ? SplitAxis.Horizontal : SplitAxis.Vertical;
        bool newFirst = dir is Direction.Left or Direction.Up;
        var targetRect = target.Target;
        double share = IpcSplitShare(r, axis, targetRect); // the new pane's share
        double ratio = newFirst ? share : 1 - share; // LayoutTree's ratio is the first child's share
        // Claude Code's agent-team teammates: parked as pills below the lead (Teammates/WindowTeammates.cs).
        if (handlers.Teammates.AddSplit(r, window.Tabs.IndexOf(tab), target, axis, newFirst, ratio) is { } parked) return parked;

        bool background = r.NoFocus && !targets.IsActive(tab); // tmux -d: a split in another tab stays there
        if (!background) IpcPrepareForegroundSplit(tab);
        var (first, _, second) = LayoutTree<Pane>.SplitRect(targetRect, axis, ratio, window.Gap);
        var area = newFirst ? first : second;
        var pane = launcher.SpawnPane(r.Argv, r.Cwd ?? target.StartDirectory(window.Settings.DefaultCwd), area, echo, fromTmux: r.Tmux);
        pane.CreatedByTmux = r.Tmux;
        if (r.Tmux && r.Argv is { } argv) pane.TmuxTitle = Tmux.TmuxSession.TitleFromCommand(argv);
        if (background) return IpcFinishBackgroundSplit(tab, target, pane, axis, newFirst, ratio, area);
        window.Layout.Split(target, pane, axis, newFirst, ratio);

        // Grows out of the target's far edge, as interactive splits do.
        pane.Rect.Snap(SplitGrowStart(axis, newFirst, targetRect, area));
        pane.Appear.Snap(0);
        pane.Appear.Set(1, Now, window.Anim * 1.5f);
        pane.Focus.Snap(0);
        if (!r.NoFocus) window.Focus(pane);
        window.ApplyLayout(animate: true);
        return new IpcResponse { Ok = true, PaneId = pane.Id };
    }

    private void IpcPrepareForegroundSplit(Tab tab)
    {
        IpcShowTab(tab);
        if (window.Zoomed is not null) window.SetZoom(null);
    }

    private IpcResponse IpcFinishBackgroundSplit(Tab tab, Pane target, Pane pane, SplitAxis axis, bool newFirst, double ratio, RectI area)
    {
        tab.Layout.Split(target, pane, axis, newFirst, ratio);
        pane.Rect.Snap(area);
        pane.Focus.Snap(0);
        launcher.ResizeBackgroundTabs();
        return new IpcResponse { Ok = true, PaneId = pane.Id };
    }

    private double IpcSplitShare(IpcRequest r, SplitAxis axis, RectI targetRect)
    {
        if (r.Cells is not { } cells) return (r.Percent ?? 50) / 100.0; // tmux -l N: N cells
        int cell = axis == SplitAxis.Horizontal ? window.CellWidth : window.CellHeight;
        int avail = (axis == SplitAxis.Horizontal ? targetRect.W : targetRect.H) - window.Gap;
        return avail > 0 ? Math.Clamp(cells * cell / (double)avail, 0.05, 0.95) : 0.5;
    }

    private static RectF SplitGrowStart(SplitAxis axis, bool newFirst, RectI targetRect, RectI area)
    {
        if (axis == SplitAxis.Horizontal) return new RectF(newFirst ? targetRect.X : targetRect.Right - 1, area.Y, 1, area.H);
        return new RectF(area.X, newFirst ? targetRect.Y : targetRect.Bottom - 1, area.W, 1);
    }

    private IpcResponse IpcSpawnTab(IpcRequest r)
    {
        var (_, from) = targets.Target(r);
        if (r.Cwd is { } cwd && !Directory.Exists(cwd)) throw new IpcException($"no such directory: {cwd}");
        var tabs = window.Tabs;
        var previous = tabs.Active!;
        var tab = new Tab();
        tabs.Add(tab); // before sizing the pane: the bar may appear and take room
        Pane pane;
        try
        {
            pane = launcher.SpawnPane(r.Argv, r.Cwd ?? from.StartDirectory(window.Settings.DefaultCwd), window.ContentBounds(), fromTmux: r.Tmux);
        }
        catch
        {
            tabs.Remove(tab);
            tabs.Activate(previous);
            throw;
        }
        tab.Layout = new LayoutTree<Pane>(pane);
        tab.Focused = pane;
        pane.CreatedByTmux = r.Tmux;
        if (!string.IsNullOrEmpty(r.Title)) tab.CustomTitle = r.Title; // tmux new-window -n
        if (r.NoFocus) // tmux new-window -d: the tab opens in the background
        {
            tabs.Activate(previous);
            tabs.Demote(tab);
            window.ApplyLayout(animate: false); // the tab bar may have appeared; also sizes the new tab
            return new IpcResponse { Ok = true, PaneId = pane.Id, WindowId = (int)window.Id };
        }
        launcher.SwitchTo(previous, tab, +1);
        return new IpcResponse { Ok = true, PaneId = pane.Id, WindowId = (int)window.Id };
    }

    private IpcResponse IpcSendText(IpcRequest r)
    {
        var (_, pane) = targets.Target(r);
        string text = r.Text ?? "";
        if (r.NoPaste) { if (text.Length > 0) input.SendInput(pane, text); }
        else input.PasteText(pane, text);
        return IpcResponse.Success();
    }

    private IpcResponse IpcGetText(IpcRequest r)
    {
        var (_, pane) = targets.Target(r);
        string text;
        lock (gate)
            text = (r.Escapes, r.NoJoin) switch
            {
                (true, _) => TextExport.WithEscapes(pane.Term, r.StartLine, r.EndLine),
                (_, true) => TextExport.PlainRows(pane.Term, r.StartLine, r.EndLine),
                _ => TextExport.Plain(pane.Term, r.StartLine, r.EndLine),
            };
        return new IpcResponse { Ok = true, Output = text };
    }

    private IpcResponse IpcActivatePane(IpcRequest r)
    {
        var (tab, pane) = targets.Target(r);
        if (pane.LayoutHidden) return IpcResponse.Success(); // a parked teammate stays a pill (tmux select-pane from Claude Code)
        IpcShowTab(tab);
        window.Focus(pane);
        return IpcResponse.Success();
    }

    private Pane? IpcNeighbor(Tab tab, Pane pane, string? direction) =>
        tab.Layout.Neighbor(pane, IpcDirection(direction), window.ContentBounds(), window.Gap);

    private IpcResponse IpcActivateDirection(IpcRequest r)
    {
        var (tab, pane) = targets.Target(r);
        if (IpcNeighbor(tab, pane, r.Direction) is not { } next) return IpcResponse.Success(); // at the edge: nothing to do
        IpcShowTab(tab);
        if (window.Zoomed is not null) window.SetZoom(null);
        window.Focus(next);
        return IpcResponse.Success();
    }

    private IpcResponse IpcGetDirection(IpcRequest r)
    {
        var (tab, pane) = targets.Target(r);
        return new IpcResponse { Ok = true, PaneId = IpcNeighbor(tab, pane, r.Direction)?.Id };
    }

    private IpcResponse IpcAdjustSize(IpcRequest r)
    {
        var (tab, pane) = targets.Target(r);
        var dir = IpcDirection(r.Direction);
        int cellW = window.CellWidth;
        int px = Math.Max(1, r.Amount ?? 1) * (dir is Direction.Left or Direction.Right ? cellW : window.CellHeight);
        if (tab.Layout.Adjust(pane, dir, px, window.ContentBounds(), window.Gap, 4 * cellW) && targets.IsActive(tab)) window.ApplyLayout(animate: true);
        return IpcResponse.Success();
    }

    private IpcResponse IpcZoom(IpcRequest r)
    {
        var (tab, pane) = targets.Target(r);
        bool active = targets.IsActive(tab);
        if (pane.LayoutHidden) return IpcResponse.Success(); // a parked teammate has nothing to zoom
        bool zoomed = (active ? window.Zoomed : tab.Zoomed) == pane;
        bool want = r.Zoom switch { "unzoom" => false, "toggle" => !zoomed, _ => true };
        if (want && tab.Layout.Count < 2) return IpcResponse.Success(); // a lone pane fills the tab already
        if (!active) tab.Zoomed = want ? pane : null;
        else if (want) { window.Focus(pane); window.SetZoom(pane); }
        else if (window.Zoomed is not null) window.SetZoom(null);
        return IpcResponse.Success();
    }

    private IpcResponse IpcKillPane(IpcRequest r)
    {
        var (tab, pane) = targets.Target(r);
        if (targets.IsActive(tab)) launcher.ClosePane(pane);
        else
        {
            pane.CloseOnExit = true; // asked to go: never held (exit_behavior)
            pane.Pty.Hangup();
            pane.Exited = true; // ReapBackgroundTabs removes it on this loop iteration
        }
        return IpcResponse.Success();
    }

    private IpcResponse IpcActivateTab(IpcRequest r)
    {
        var tabs = window.Tabs;
        int index;
        if (r.TabId is { } id)
            index = window.TabById(id) is { } t ? tabs.IndexOf(t) : throw new IpcException($"no tab with id {id}");
        else if (r.TabRelative is { } delta)
        {
            int n = tabs.Count;
            index = ((tabs.ActiveIndex + delta) % n + n) % n;
        }
        else
        {
            index = tabs.Resolve(r.TabIndex ?? 0);
            if (index < 0) throw new IpcException($"no tab at index {r.TabIndex} ({tabs.Count} open)");
        }
        window.ActivateTab(index);
        return IpcResponse.Success();
    }

    private IpcResponse IpcSetTabTitle(IpcRequest r)
    {
        var tab = targets.Tab(r);
        tab.Rename(string.IsNullOrEmpty(r.Title) ? null : r.Title); // also tmux rename-window (Tmux/TmuxShim.cs)
        tab.TitleAt = 0;
        return IpcResponse.Success();
    }

    /// <summary>Sets the window title until the focused program next sets its own.</summary>
    private IpcResponse IpcSetWindowTitle(IpcRequest r)
    {
        window.SetTitleUntilProgramSets(r.Title ?? "");
        return IpcResponse.Success();
    }

    private IpcResponse IpcFinishWorktree(IpcRequest r)
    {
        if (r.Cwd is not { } cwd || !Directory.Exists(cwd)) throw new IpcException("finish-worktree needs an existing worktree directory");
        handlers.Worktrees.OpenFinish(cwd);
        return IpcResponse.Success();
    }
}
