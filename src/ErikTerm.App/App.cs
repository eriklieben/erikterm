using System.Collections.Concurrent;
using ErikTerm.App.Agents;
using ErikTerm.App.Host;
using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Native;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Search;
using ErikTerm.App.Session;
using ErikTerm.App.Ui;
using ErikTerm.App.Vim;
using ErikTerm.App.Worktrees;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App;

/// <summary>
/// The process: the SDL event loop and the windows (<see cref="TermWindow"/>), each with its own renderer, fonts,
/// atlas, tabs and overlays. SDL addresses events to a window by id; <see cref="Dispatch"/> hands them to it. PTY
/// reader threads feed terminals under the one <see cref="Lock"/> and wake the loop with one process-wide event,
/// whichever window their pane is in. What the process keeps besides lives in the parts it holds (Host/): the
/// configuration (<see cref="ProcessConfig"/>), the control socket (<see cref="ControlSocket"/>, <see cref="IpcRequests"/>),
/// the session (<see cref="SessionKeeper"/>), moving tabs between windows (<see cref="TabTearOff"/>), every window's
/// panes as one set (<see cref="ProcessPanes"/>), notifications (<see cref="ProcessNotifications"/>), frame pacing
/// (<see cref="FramePacer"/>) and the debug script (<see cref="DebugScript"/>). The windows talk to the process
/// through this class: the members below hand each request to the part that owns it.
/// </summary>
#pragma warning disable S6640 // Native interop: the SDL event loop passes event structs by pointer (WaitEventTimeout, PushEvent).
internal sealed unsafe class App : IConfigOwner, IAccessibilityOwner, ITearOffOwner, IDebugScriptOwner, INotificationProcess, IAgentProcess<Pane>,
    Tabs.ITabSwitcherProcess, IVimProcess, IWorktreeProcess<Pane>
#pragma warning restore S6640
{
    /// <summary>--new-process: don't hand this launch to a running erikterm (single_instance).</summary>
    internal static bool NewProcess { get; set; }
    /// <summary>--class NAME (also the SDL app id): only instances with the same class share a process.</summary>
    internal static string? WindowClass { get; set; }
    /// <summary>--no-restore: start with a fresh window (it is still saved).</summary>
    internal static bool NoRestore { get; set; }

    private readonly string[]? _command;
    private readonly ProcessConfig _config;
    private readonly AccessibilityWatcher _accessibility;
    private readonly ControlSocket _socket;
    private readonly IpcRequests _ipc;
    private readonly Func<IpcRequest, IpcResponse> _handleIpc;
    private readonly SessionKeeper _session;
    private readonly QuitSignals _quitSignals;
    private readonly RecentKeeper _recent = new(RecentHistory.DefaultPath());
    private readonly ProcessPanes _panes;
    private readonly ProcessNotifications _notifications;
    private readonly TabTearOff _tearOff;
    private readonly DebugScript _script;
    private readonly WindowErrors _errors = new();
    private readonly FramePacer _pacer;
    private readonly LiveResizeWatch _liveResize;
    private readonly SystemCursors _cursors = new();

    /// <summary>Guards every terminal: reader threads feed under it, the main thread reads and draws under it.</summary>
    internal readonly Lock Lock = new();

    /// <summary>OSC 52 writes from reader threads; applied on the main thread by the window that has the pane (TickClipboard).</summary>
    internal readonly ConcurrentQueue<(Terminal Term, ClipboardTarget Target, string Text)> ClipboardWrites = new();

    private uint _wakeEvent;
    private int _wakePending;
    private bool _quit;

    public App(string configPath, string[]? command, IReadOnlyList<ConfigOverride> overrides)
    {
        _command = command;
        _accessibility = new AccessibilityWatcher(this);
        _config = new ProcessConfig(configPath, overrides, this, _accessibility);
        _panes = new ProcessPanes(Windows);
        _socket = new ControlSocket(Wake);
        _ipc = new IpcRequests(this, _config, _panes);
        _handleIpc = _ipc.Handle;
        _session = new SessionKeeper(SessionStore.DefaultPath(WindowClass), CaptureSession);
        _quitSignals = new QuitSignals(() => { _quit = true; Wake(); });
        _notifications = new ProcessNotifications(this, _config, _panes);
        _tearOff = new TabTearOff(this);
        _script = new DebugScript(this, _tearOff);
        _pacer = new FramePacer(Windows, _errors, _tearOff);
        _liveResize = new LiveResizeWatch(this, _pacer);
    }

    private static long Now => Environment.TickCount64;

    // ==== Startup ==============================================================================

    public int Run()
    {
        _config.LoadInitial();
        // single_instance: a window in the running erikterm instead (Ipc/LaunchForwarder.cs)
        if (!NewProcess && Settings.SingleInstance && LaunchForwarder.TryForward(_command, ConfigPath, _config.Overrides, WindowClass)) return 0;
        _accessibility.StartRead(); // Linux: gsettings / the portal while SDL starts
        (Rendering.FontLocator.Current as Rendering.FontconfigLocator)?.Prefetch(Settings.Font); // runs while SDL starts
        _recent.Apply(Settings.RememberCommands); // recent commands and directories
        var restore = Settings.RestoreSession && _command is null && !NoRestore ? _session.LoadRestorable() : []; // saved windows
        _script.LoadFile();

        // SDL_VIDEO_WAYLAND_PREFER_LIBDECOR stays off: libdecor loads GTK (60 ms of startup, 26 MB). SDL still uses it by
        // itself on a compositor that can't draw window decorations.
        Sdl.SetHint("SDL_KEYCODE_OPTIONS", "unmodified,latin_letters"); // bindings match regardless of Shift/layout
        // Closing the last window goes through its close prompt (Windowing/CloseConfirmFlow.cs), not straight to SDL_EVENT_QUIT.
        Sdl.SetHint("SDL_QUIT_ON_LAST_WINDOW_CLOSE", "0");
        Sdl.SetHint("SDL_IME_IMPLEMENTED_UI", "composition"); // text being composed is drawn at the cursor (Windowing/ImeComposer.cs)
        if (!Sdl.Init(Sdl.INIT_VIDEO | Sdl.INIT_EVENTS)) throw new InvalidOperationException("SDL_Init: " + Sdl.GetError());
        if (OperatingSystem.IsMacOS()) MacMenu.DetachCloseShortcut(); // Cmd+W is a binding, not "close the window" (Native/MacMenu.cs)
        try
        {
            _wakeEvent = Sdl.RegisterEvents(1);
            Rendering.FallbackResolver.Shared.Wake = Wake; // fallback fonts are found off-thread; redraw when one arrives
            _tearOff.IsWayland = Sdl.GetCurrentVideoDriver() == "wayland";
            _accessibility.ApplyFirst(); // reduce motion, contrast, text size from the system (Host/AccessibilityWatcher.cs)
            _cursors.Create();
            _quitSignals.Register(); // SIGTERM / SIGHUP save the session
            _socket.Start(); // control socket for `erikterm cli`; before the first pane spawns

            if (restore.Count > 0)
            {
                for (int i = 0; i < restore.Count; i++)
                    CreateWindow(new TermWindow.OpenOptions { Restore = restore[i], Quiet = i > 0 });
                restore.Clear();
            }
            else CreateWindow(new TermWindow.OpenOptions { Command = _command });
            _session.Started = true;

            _config.Watch();
            _config.WatchSchemeFiles();
            _liveResize.Start(); // Windows: draw from inside the modal resize loop
            EventLoop();
            return 0;
        }
        finally
        {
            _liveResize.Stop(); // before the windows go
            SaveSessionOnExit(); // never throws
            _recent.Save();
            _config.Dispose(); // the config and scheme file watchers
            _socket.Stop();
            _tearOff.HideGhost();
            for (int i = Windows.Count - 1; i >= 0; i--)
            {
                var w = Windows[i];
                Windows.Remove(w);
                w.Destroy(disposePanes: !w.PanesMoved);
            }
            Sdl.Quit();
        }
    }

    /// <summary>Run's cleanup, while the windows are still open: the signals go, then the session is saved (or forgotten).</summary>
    private void SaveSessionOnExit()
    {
        try
        {
            _quitSignals.Dispose();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"erikterm: session: {ex.Message}");
            return;
        }
        _session.SaveOnExit();
    }

    // ==== Windows ==============================================================================

    /// <summary>The open windows, which owns which tab, and focus order (ErikTerm.Ui/WindowSet.cs).</summary>
    public WindowSet<TermWindow, Tab> Windows { get; } = new();

    /// <summary>Opens a window (throws when SDL or the first pane fails) and makes it the debug script's target.</summary>
    public TermWindow CreateWindow(TermWindow.OpenOptions options, TermWindow? prepared = null)
    {
        var w = prepared ?? new TermWindow(this);
        w.Open(options);
        if (!Windows.Contains(w)) Windows.Add(w, w.Tabs);
        else Windows.Focus(w);
        _script.OnWindowOpened(w);
        return w;
    }

    /// <summary>SpawnWindow (Ctrl+Shift+N): a new window in this process.</summary>
    public TermWindow? SpawnWindow(string? cwd, TermWindow? from, string[]? command = null)
    {
        try
        {
            return CreateWindow(new TermWindow.OpenOptions { Cwd = cwd, Command = command, Quiet = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            from?.Toast(ToastKind.Error, "Couldn't open a new window", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Closes a window (its close button, its last tab gone). The last window isn't closed here: the loop ends, and Run's
    /// cleanup saves the session with it first.
    /// </summary>
    public void CloseWindow(TermWindow w)
    {
        if (!Windows.Contains(w)) return;
        if (Windows.Count == 1)
        {
            _quit = true;
            if (w.ClosedLastPane) _session.ClosedLastPane = true; // the user exited the last shell: forget the session
            return;
        }
        _tearOff.Cancel(w);
        Windows.Remove(w);
        _script.OnWindowClosed(w);
        w.Destroy(disposePanes: !w.PanesMoved);
    }

    /// <summary>Closes windows that asked to (last pane exited, last tab moved away) at a point where nothing is using them.</summary>
    private void CloseRequestedWindows()
    {
        for (int i = Windows.Count - 1; i >= 0 && !_quit; i--)
        {
            if (i >= Windows.Count) continue;
            var w = Windows[i];
            if (w.CloseRequested) CloseWindow(w);
        }
    }

    public TermWindow? WindowById(uint id)
    {
        if (id == 0) return null;
        for (int i = 0; i < Windows.Count; i++)
            if (Windows[i].Id == id) return Windows[i];
        return null;
    }

    /// <summary>The other open windows, in the order they were opened (palette / menu entries).</summary>
    internal List<TermWindow> OtherWindows(TermWindow w)
    {
        var list = new List<TermWindow>();
        for (int i = 0; i < Windows.Count; i++)
            if (!ReferenceEquals(Windows[i], w) && !Windows[i].CloseRequested) list.Add(Windows[i]);
        return list;
    }

    /// <summary>Hook from TermWindow.Handle on focus gain: the most recently used window gets keyboard-less requests (cli).</summary>
    internal void OnWindowFocused(TermWindow w)
    {
        Windows.Focus(w);
        _accessibility.RequestRead(); // back from the system's settings, perhaps
    }

    public void Wake()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) == 0)
        {
            var ev = new Sdl.Event { Type = _wakeEvent };
            Sdl.PushEvent(&ev);
        }
    }

    private void ToastAll(ToastKind kind, string title, string? detail = null)
    {
        for (int i = 0; i < Windows.Count; i++) Windows[i].Toast(kind, title, detail);
    }

    /// <summary>A theme picked in the palette: every window's panes take it.</summary>
    internal void ApplySchemeToAllWindows()
    {
        for (int i = 0; i < Windows.Count; i++) Windows[i].ApplyScheme();
    }

    /// <summary>Every open window that saves (not -e windows), in the order they were opened; null when there are none.</summary>
    private SessionFile? CaptureSession(bool includeScrollback)
    {
        var file = new SessionFile();
        for (int i = 0; i < Windows.Count; i++)
        {
            var w = Windows[i];
            if (!w.SavesSession || w.CloseRequested) continue;
            if (w.CaptureWindow(includeScrollback) is { } saved) file.Windows.Add(saved); // Windowing/WindowSession.cs
        }
        return file.Windows.Count > 0 ? file : null;
    }

    // ==== Event loop ===========================================================================

    private void EventLoop()
    {
        long lastEventAt = Now;
        while (!_quit)
        {
            int counted = Windows.Count;
            WaitForEvents(NextWaitMs(counted, lastEventAt), ref lastEventAt);
            if (_quit) return;
            FlushDueKeys();
            CloseRequestedWindows();
            if (_quit) return;

            Volatile.Write(ref _wakePending, 0);
            DrainIpc(); // `erikterm cli` requests
            ReapExitedPanes();
            CloseRequestedWindows();
            if (_quit) return;
            _config.ReloadIfDue();
            _accessibility.Tick();
            _tearOff.Tick(Now);
            TickClipboard();
            TickWindows();
            _ipc.CheckPaneWaits(Now); // wait-for-state
            _session.Tick(Now); // debounced session save
            _recent.Tick(Now);
            _script.Run(Now);
            CloseRequestedWindows();
            if (_quit) return;
            _pacer.RenderWindows(counted);
            if (_pacer.AnyWindowHoldsFloodFrame()) Volatile.Write(ref _wakePending, 1); // the hold's own deadline ends this pass's wait
        }
    }

    /// <summary>How long the loop may sleep: what it is doing now, capped by the soonest wake-up any window asks for.</summary>
    private int NextWaitMs(int count, long lastEventAt)
    {
        long now = Now;
        int timeout = _pacer.ScanWindows(count, now, out bool animating, out bool sync, out bool screenshot);
        int wait = LoopWaitMs(animating, sync, screenshot, now, lastEventAt);
        timeout = Math.Min(Math.Min(Math.Min(wait, timeout), _tearOff.WakeMs(now)), _pacer.UnpacedWakeMs);
        return Math.Min(timeout, _ipc.WakeMs(now)); // a wait-for-state --timeout
    }

    private int LoopWaitMs(bool animating, bool sync, bool screenshot, long now, long lastEventAt)
    {
        if (animating) return _pacer.AnimatingWaitMs; // no vsync wait will pace the loop
        if (_script.HasSteps) return DebugScript.WaitMs;
        if (sync || _config.ReloadPending) return 10;
        return screenshot ? 50 : IdleTick.Ms(now, lastEventAt);
    }

    private void WaitForEvents(int timeout, ref long lastEventAt)
    {
        Sdl.Event ev;
        if (!Sdl.WaitEventTimeout(&ev, timeout)) return;
        lastEventAt = Now;
        do
        {
            Dispatch(ref ev);
        } while (!_quit && Sdl.PollEvent(&ev));
    }

    private void FlushDueKeys()
    {
        for (int i = 0; i < Windows.Count; i++) Windows[i].FlushPendingKeyIfDue(Now); // Windowing/KeyInputRouter.cs
    }

    private void ReapExitedPanes()
    {
        for (int i = 0; i < Windows.Count; i++) if (!Windows[i].CloseRequested) Windows[i].ReapExitedPanes();
    }

    private void TickWindows()
    {
        for (int i = 0; i < Windows.Count; i++)
        {
            var w = Windows[i];
            if (w.CloseRequested) continue;
            try { w.ReleaseExpiredSynchronizedOutput(Now); w.Tick(Now); }
            catch (Exception ex) when (!WindowErrors.IsFatal(ex)) { _errors.Report(w, "tick", ex); }
        }
    }

    /// <summary>One SDL event: the process's own, else the window it names.</summary>
    private void Dispatch(ref Sdl.Event ev)
    {
        if (ev.Type == _wakeEvent) return;
        if (ev.Type == Sdl.EVENT_QUIT) { _quit = true; return; }
        if (ev.Type == Sdl.EVENT_SYSTEM_THEME_CHANGED) { _accessibility.RequestRead(now: true); return; }
        _tearOff.OnEvent(ref ev); // a pending drop: did the pointer show up in another window?
        if (WindowById(ev.WindowID) is not { CloseRequested: false } w) return; // the ghost popup, a closed window, no window
        bool keep;
        try { keep = w.Handle(ref ev); }
        catch (Exception ex) when (!WindowErrors.IsFatal(ex)) { _errors.Report(w, "event", ex); keep = true; }
        if (!keep) CloseWindow(w);
    }

    /// <summary>Runs pending CLI requests and keeps the last-focused marker current.</summary>
    private void DrainIpc()
    {
        if (!_socket.Running) return;
        bool focused = false;
        for (int i = 0; i < Windows.Count; i++) focused |= Windows[i].IsWindowFocused;
        _socket.Drain(focused, _handleIpc);
    }

    /// <summary>OSC 52: the window that has the pane now applies (and toasts) the write.</summary>
    private void TickClipboard()
    {
        while (ClipboardWrites.TryDequeue(out var c))
        {
            TermWindow? owner = null;
            for (int i = 0; i < Windows.Count && owner is null; i++)
                if (Windows[i].HasTerminal(c.Term)) owner = Windows[i];
            (owner ?? Windows.Focused)?.ApplyClipboardWrite(c.Term, c.Target, c.Text);
        }
    }

    // ==== What the windows ask of the process ==================================================

    // Configuration (Host/ProcessConfig.cs)
    public Settings Settings => _config.Settings;
    public Dictionary<KeyChord, ActionSpec> Keys => _config.Keys;
    public string ConfigPath => _config.ConfigPath;
    /// <summary>A theme chosen from the palette (any window); applies to all windows until the config reloads.</summary>
    internal ColorScheme? SchemeOverride { get => _config.SchemeOverride; set => _config.SchemeOverride = value; }
    /// <summary>What the accessibility settings came to with the system's preferences (Theme.From takes HighContrast).</summary>
    internal EffectiveAccessibility Accessibility => _accessibility.Effective;
    internal void OnConfigChanged() => _config.OnConfigChanged();
    internal void ReloadSchemesQuietly() => _config.ReloadSchemesQuietly();
    internal List<ColorScheme> AllSchemes() => _config.AllSchemes();
    internal List<string> ConfigProblems() => _config.Problems();

    /// <summary>Hook from TermWindow.BuildCommands: Show Config Problems (a list in the palette).</summary>
    internal void AddConfigCommands(List<Command> list, TermWindow w)
    {
        int count = ConfigProblems().Count;
        list.Add(new Command
        {
            Id = "config-problems", Category = "Window",
            Title = count == 0 ? "Show Config Problems (none)" : $"Show Config Problems ({count})",
            Icon = Icons.Warning,
            Run = () => w.ShowConfigProblems(ConfigProblems(), _config.Kept),
        });
    }

    // Session (Session/SessionKeeper.cs) and recent commands (Session/RecentKeeper.cs)
    /// <summary>Hook from TermWindow.BuildCommands (toasts go to that window).</summary>
    internal void AddSessionCommands(List<Command> list, TermWindow w) => _session.AddCommands(list, w.Toast);
    /// <summary>Recent commands and directories of every window; null while remember_commands is off.</summary>
    internal RecentHistory? Recent => _recent.Recent;
    /// <summary>Clear Recent Commands and Directories (palette).</summary>
    internal bool ForgetRecent() => _recent.Forget();
    /// <summary>A theme catalogue download is under way (Integrations/ThemeCatalog.cs); set and cleared from worker tasks too.</summary>
    internal volatile bool ThemeDownloadRunning;

    // Control socket (Ipc/ControlSocket.cs)
    /// <summary>
    /// Hook from SpawnPane: ERIKTERM_SOCKET / ERIKTERM_PANE, so `erikterm cli` in the pane targets it, and the tmux
    /// emulation's variables when <paramref name="tmuxEmulation"/> is on for the window.
    /// <paramref name="paneId"/>: an existing pane (respawn), else the next one.
    /// </summary>
    internal void AddIpcEnvironment(Dictionary<string, string> env, bool tmuxEmulation, int? paneId = null) =>
        _socket.AddEnvironment(env, tmuxEmulation, paneId ?? Pane.PeekNextId());
    /// <summary>Whether the tmux emulation's shim directory exists (the emulation can work in this process).</summary>
    internal bool TmuxShimAvailable => _socket.TmuxShimAvailable;

    // Mouse cursors (Host/SystemCursors.cs)
    internal nint CursorText => _cursors.Text;
    internal nint CursorDefault => _cursors.Default;
    internal nint CursorEw => _cursors.Ew;
    internal nint CursorNs => _cursors.Ns;
    internal nint CursorPointer => _cursors.Pointer;
    internal void SetCursor(nint cursor) => _cursors.Set(cursor);

    // Moving tabs between windows (Host/TabTearOff.cs)
    /// <summary>The compositor places windows and doesn't tell their positions (no global pointer between windows).</summary>
    internal bool IsWayland => _tearOff.IsWayland;
    internal bool CanMoveTab(Tab tab, TermWindow? to) => _tearOff.CanMoveTab(tab, to);
    internal bool CanDetachTab(TermWindow from) => _tearOff.CanDetachTab(from);
    internal void MoveTab(Tab tab, TermWindow to, int index) => _tearOff.MoveTab(tab, to, index);
    internal void MoveTabToWindow(TermWindow from, Tab tab, int windowId) => _tearOff.MoveTabToWindow(from, tab, windowId);
    internal void MoveTabToNewWindow(TermWindow from, Tab tab) => _tearOff.MoveTabToNewWindow(from, tab);
    internal void OnTabDragged(TermWindow from, float x, float y) => _tearOff.OnTabDragged(from, x, y);
    internal void OnTabDropped(TermWindow from, Tab tab, float x, float y) => _tearOff.OnTabDropped(from, tab, x, y);

    // Every window's panes (Host/ProcessPanes.cs)
    internal List<PaneSearchTarget<Pane>> PaneSearchTargets(TermWindow origin) => _panes.SearchTargets(origin);
    internal bool RevealSearchHit(Pane pane, PaneSearchQuery query, SearchMatch match) => _panes.RevealSearchHit(pane, query, match);
    internal bool JumpToReview(string root) => _panes.JumpToReview(root);

    /// <summary>The process's notification center, in step with the current settings (Host/ProcessNotifications.cs).</summary>
    public NotificationCenter Notifications => _notifications.Center;

    // ==== The parts' and the windows' interfaces ===============================================

    bool IWindowOwner.Quitting => _quit;
    bool IAccessibilityOwner.LoopRunning => _wakeEvent != 0;
    bool IAccessibilityOwner.IsWayland => _tearOff.IsWayland;

    void IAccessibilityOwner.SystemAccessibilityChanged()
    {
        var before = _accessibility.Effective;
        var old = _config.Reapply();
        if (_accessibility.Effective == before && Settings == old) return; // a contrast theme's colours can change with the flags as they were
        for (int i = 0; i < Windows.Count; i++) Windows[i].ApplyReloadedSettings(old); // Windowing/ActionDispatcher.cs
    }
    TermWindow ITearOffOwner.NewWindow() => new(this);
    void ITearOffOwner.RetargetScript(TermWindow w) => _script.Target = w;

    void IConfigOwner.ApplyReloadedSettings(Settings old)
    {
        _recent.Apply(Settings.RememberCommands);
        for (int i = 0; i < Windows.Count; i++) Windows[i].ApplyReloadedSettings(old); // Windowing/ActionDispatcher.cs
    }

    void IConfigOwner.ToastAll(ToastKind kind, string title, string? detail) => ToastAll(kind, title, detail);

    void IConfigOwner.AddFontWarnings(List<string> problems)
    {
        for (int i = 0; i < Windows.Count; i++)
            foreach (var warning in Windows[i].FontWarnings.Where(w => !problems.Contains(w)))
                problems.Add(warning);
    }

    int IPaneTabs.TabOf(int paneId) => _notifications.TabOf(paneId);
    int IPaneTabs.LeadOf(int paneId) => _notifications.LeadOf(paneId);

    void INotificationProcess.DrainAgentFocusRequests() => _panes.DrainFocusRequests();
    int INotificationProcess.NotificationPaneStamp() => _notifications.PaneStamp();
    void INotificationProcess.PruneNotificationPanes(long now) => _notifications.PrunePanes(now);
    string? INotificationProcess.NotificationTabLabel(int paneId, uint viewer) => _notifications.TabLabel(paneId, viewer);
    string? INotificationProcess.NotificationElsewhere(int paneId, uint viewer) => _notifications.Elsewhere(paneId, viewer);
    void INotificationProcess.RefreshNotificationPlaces(long now) => _notifications.RefreshPlaces(now);
    bool INotificationProcess.JumpToNotification(NotificationItem item) => _notifications.Jump(item);

    bool IAgentProcess<Pane>.AnyWindowFocused => _notifications.AnyWindowFocused;
    int IAgentProcess<Pane>.WindowCount => Windows.Count;
    void IAgentProcess<Pane>.DrainAgentFocusRequests() => _panes.DrainFocusRequests();
    List<AgentRow<Pane>> IAgentProcess<Pane>.AgentRows() => _panes.AgentRows();
    void IAgentProcess<Pane>.JumpToAgentPane(Pane pane) => _panes.JumpToAgentPane(pane);
    List<AgentPaneRef<Pane>> IAgentProcess<Pane>.CollectAgentPanes() => _panes.CollectAgentPanes();

    List<ColorScheme> IVimProcess.AllSchemes() => _config.AllSchemes();
    void IVimProcess.SetSchemeOverride(ColorScheme scheme) => _config.SchemeOverride = scheme;
    void IVimProcess.ApplySchemeToAllWindows() => ApplySchemeToAllWindows();
    void IVimProcess.AddSessionOverride(ConfigOverride setting) => _config.AddSessionOverride(setting);

    Dictionary<string, List<WorktreePaneRef<Pane>>> IWorktreeProcess<Pane>.PanesByWorktree(List<string> roots) => _panes.ByWorktree(roots);
    bool IWorktreeProcess<Pane>.JumpToWorktree(string path, List<string> roots) => _panes.JumpToWorktree(path, roots);
    GitHubStatus? IWorktreeProcess<Pane>.KnownGitHubStatus(string root, string branch) => _panes.KnownGitHubStatus(root, branch);
}
