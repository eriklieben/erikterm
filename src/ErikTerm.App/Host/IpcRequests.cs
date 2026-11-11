using ErikTerm.App.Agents;
using ErikTerm.App.Ipc;
using ErikTerm.App.Panes;

namespace ErikTerm.App.Host;

/// <summary>
/// Runs <c>erikterm cli</c> requests on the main thread (<see cref="ControlSocket"/> brings them). Requests about a
/// pane, tab or window go to the window holding it (WindowFeatures/WindowIpc.cs); <c>list</c>, <c>agents</c>, window commands,
/// <c>wait-for-state</c> and <c>new-window</c> (a second <c>erikterm</c> launch with single_instance) are handled here.
/// </summary>
internal sealed class IpcRequests(IWindowOwner owner, ProcessConfig config, ProcessPanes panes)
{
    private readonly PaneWaitList _paneWaits = new();

    private static long Now => Environment.TickCount64;

    private ErikTerm.Ui.WindowSet<TermWindow, Tab> Windows => owner.Windows;

    public IpcResponse Handle(IpcRequest r)
    {
        try
        {
            switch (r.Command)
            {
                case "list":
                    var list = new List<PaneInfo>();
                    for (int i = 0; i < Windows.Count; i++) if (!Windows[i].CloseRequested) Windows[i].ListPanes(list);
                    return new IpcResponse { Ok = true, Panes = list };
                case "reload-config":
                    config.OnConfigChanged();
                    return IpcResponse.Success();
                case "new-window": return NewWindow(r);
                case "activate-window": return ActivateWindow(r);
                case "agents": return Agents(); // every window's
                case "wait-for-state": return WaitForState(r);
                case "spawn" when r.NewWindow: return SpawnWindow(r);
            }
            var w = WindowFor(r);
            var response = w.HandleIpc(r); // WindowFeatures/WindowIpc.cs
            w.RequestRedraw();
            return response;
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

    private IpcResponse Agents()
    {
        long now = Now;
        var list = new List<AgentInfo>();
        foreach (var row in panes.AgentRows()) list.Add(WindowAgents<Pane>.ToAgentInfo(row, now));
        return new IpcResponse { Ok = true, Agents = list };
    }

    // ---- wait-for-state ----------------------------------------------------------------------------------------

    /// <summary>Never blocks: the reply is sent when <see cref="CheckPaneWaits"/> settles the wait.</summary>
    private IpcResponse WaitForState(IpcRequest r)
    {
        if (WaitStates.Parse(r.State) is not { } targets) throw new IpcException($"invalid state '{r.State}' (expected {WaitStates.Expected})");
        if (!config.Settings.AgentStatus && targets.Any(t => t != WaitStates.Exit)) throw new IpcException("agent_status is off in the config: only --state exit can be waited for");
        int id = r.PaneId ?? WindowFor(r).FocusedPaneId;
        if (PaneWaitViewOf(id) is null) return IpcResponse.Failure($"no pane with id {id}", CliExit.PaneGone);
        var later = _paneWaits.Add(id, targets, r.TimeoutMs is { } ms ? Now + ms : null);
        CheckPaneWaits(Now);
        return new IpcResponse { Ok = true, Later = later };
    }

    /// <summary>Hook from the event loop, after the windows' ticks have polled the agents.</summary>
    public void CheckPaneWaits(long now) => _paneWaits.Check(now, PaneWaitViewOf);

    /// <summary>When the soonest wait-for-state --timeout runs out.</summary>
    public int WakeMs(long now) => _paneWaits.WakeMs(now);

    private PaneWaitView? PaneWaitViewOf(int id)
    {
        for (int i = 0; i < Windows.Count; i++)
            if (!Windows[i].CloseRequested && Windows[i].SyncedPane(id) is { } pane) return new PaneWaitView(pane.Agent.State, pane.Held);
        return null;
    }

    // ---- Windows -----------------------------------------------------------------------------------------------

    /// <summary>The window a request is about (IpcRouting): --window-id, the window holding --pane-id / --tab-id, or the focused one.</summary>
    private TermWindow WindowFor(IpcRequest r)
    {
        var entries = new List<IpcWindowEntry>(Windows.Count);
        var live = new List<TermWindow>(Windows.Count);
        int focused = -1;
        for (int i = 0; i < Windows.Count; i++)
        {
            var w = Windows[i];
            if (w.CloseRequested) continue;
            if (ReferenceEquals(w, Windows.Focused)) focused = live.Count;
            live.Add(w);
            entries.Add(new IpcWindowEntry((int)w.Id, id => w.HasPane(id), id => w.TabById(id) is not null));
        }
        int index = IpcRouting.Resolve(r, entries, focused, out string? error);
        return index >= 0 ? live[index] : throw new IpcException(error ?? "no window");
    }

    /// <summary>`spawn --new-window`: a window with one pane (in --cwd, or the requesting pane's directory).</summary>
    private IpcResponse SpawnWindow(IpcRequest r)
    {
        if (r.Cwd is { } cwd && !Directory.Exists(cwd)) throw new IpcException($"no such directory: {cwd}");
        string? from = null;
        if (r.PaneId is { } paneId)
            for (int i = 0; i < Windows.Count && from is null; i++)
                if (Windows[i].PaneStartDirectory(paneId) is { } dir) from = dir;
        return OpenWindowFor(r.Argv, r.Cwd ?? from ?? config.Settings.DefaultCwd);
    }

    private IpcResponse OpenWindowFor(string[]? argv, string? cwd)
    {
        TermWindow w;
        try
        {
            w = owner.CreateWindow(new TermWindow.OpenOptions { Command = argv, Cwd = cwd, Quiet = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new IpcException($"couldn't open a window: {ex.Message}");
        }
        return new IpcResponse { Ok = true, WindowId = (int)w.Id, PaneId = w.FocusedPaneId };
    }

    private IpcResponse ActivateWindow(IpcRequest r)
    {
        if (r.WindowId is not { } id) throw new IpcException("activate-window needs --window-id");
        if (id <= 0 || owner.WindowById((uint)id) is not { CloseRequested: false } w) throw new IpcException($"no window with id {id}");
        w.RequestAttention();
        return IpcResponse.Success();
    }

    // ---- Single instance ---------------------------------------------------------------------------------------

    /// <summary>
    /// A second <c>erikterm</c> (single_instance = true, no --new-process; <see cref="LaunchForwarder"/>) asks for a
    /// window instead of starting a process. Only accepted with the same config file, --set overrides and --class.
    /// </summary>
    private IpcResponse NewWindow(IpcRequest r)
    {
        if (!SameInstanceConfig(r.ConfigFile, r.Overrides, r.WindowClass))
            return IpcResponse.Failure("this instance runs a different configuration");
        if (r.Cwd is { } cwd && !Directory.Exists(cwd)) r.Cwd = null;
        var response = OpenWindowFor(r.Argv, r.Cwd ?? config.Settings.DefaultCwd);
        if (owner.WindowById((uint)(response.WindowId ?? 0)) is { } w) w.RequestAttention();
        return response;
    }

    private bool SameInstanceConfig(string? configFile, string[]? overrides, string? windowClass)
    {
        if (configFile is null || !string.Equals(Path.GetFullPath(configFile), config.ConfigPath, StringComparison.Ordinal)) return false;
        if (!(overrides ?? []).SequenceEqual(config.Overrides.Select(o => o.Text))) return false;
        return string.Equals(windowClass ?? "", App.WindowClass ?? "", StringComparison.Ordinal);
    }
}
