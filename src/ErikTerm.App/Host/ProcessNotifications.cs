using System.Diagnostics;
using ErikTerm.App.Native;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;

namespace ErikTerm.App.Host;

/// <summary>
/// Notifications, process half: the one <see cref="NotificationCenter"/> (routing, history, unread) every window posts
/// to, the remembered answer to "… wants to send desktop notifications" (preferences.json, never the config file),
/// notify-send, where a notification's pane is now (another window, workspace or display) and jumping to it in
/// whichever window has it. Each window draws the bars and the panel (WindowNotifications.cs).
/// </summary>
internal sealed class ProcessNotifications(IWindowOwner owner, ProcessConfig config, ProcessPanes panes) : IPaneTabs
{
    private readonly IWindowOwner _owner = owner;
    private readonly ProcessConfig _config = config;
    private readonly ProcessPanes _panes = panes;
    private NotificationCenter? _center;
    private Config.Settings? _centerSettings;
    private string _preferencesPath = "";
    private long _pruneAt;
    private HyprlandWorkspaces? _hyprland;
    private int _displayStamp;
    private long _displaysRefreshAt;

    private ErikTerm.Ui.WindowSet<TermWindow, Tab> Windows => _owner.Windows;

    /// <summary>Hyprland's workspaces; does nothing off Hyprland. Main thread, the first time.</summary>
    private HyprlandWorkspaces Hyprland => _hyprland ??= new HyprlandWorkspaces(HyprlandWorkspaces.SocketPath(Environment.GetEnvironmentVariable), _owner.Wake);

    /// <summary>The process's notification center, in step with the current settings.</summary>
    public NotificationCenter Center
    {
        get
        {
            if (_center is null)
            {
                _preferencesPath = PreferencesStore.DefaultPath();
                _center = new NotificationCenter(new NotifySend(this))
                {
                    StoredMode = PreferencesStore.ParseMode(PreferencesStore.Load(_preferencesPath).OsNotifications) is { } m and not OsNotificationMode.Ask ? m : null,
                    Decided = SavePreference,
                };
            }
            var settings = _config.Settings;
            if (!ReferenceEquals(_centerSettings, settings))
            {
                // Config (re)loaded: "on"/"off" there win over the remembered answer; "ask" or unset defer to it.
                _centerSettings = settings;
                var c = _center;
                c.ConfigMode = PreferencesStore.ParseMode(settings.OsNotifications) is { } cm and not OsNotificationMode.Ask ? cm : null;
                c.AgentDesktop = settings.AgentNotifications;
                c.BarEnabled = settings.NotificationBar;
            }
            return _center;
        }
    }

    public bool AnyWindowFocused
    {
        get
        {
            for (int i = 0; i < Windows.Count; i++) if (!Windows[i].CloseRequested && Windows[i].IsWindowFocused) return true;
            return false;
        }
    }

    private void SavePreference(OsNotificationMode mode)
    {
        try
        {
            var prefs = PreferencesStore.Load(_preferencesPath);
            prefs.OsNotifications = mode == OsNotificationMode.Ask ? null : PreferencesStore.ModeWord(mode);
            PreferencesStore.Save(_preferencesPath, prefs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Windows.Focused?.Toast(ToastKind.Error, "Couldn't save the notification setting", ex.Message);
        }
    }

    public int TabOf(int paneId) => _panes.Find(paneId, out var tab, out _) is not null ? tab!.Id : 0;

    public int LeadOf(int paneId) => _panes.Find(paneId, out _, out _)?.Teammate?.LeadId ?? 0;

    /// <summary>
    /// Changes whenever panes come, go or change tab or team, in any window. The windows compare it each tick while
    /// the bar is scoped to a tab or the panel is open (notifications are placed by where their panes are now). Allocation-free.
    /// </summary>
    public int PaneStamp()
    {
        int h = 17 + Hyprland.Stamp + _displayStamp;
        unchecked
        {
            for (int i = 0; i < Windows.Count; i++)
            {
                var w = Windows[i];
                if (w.CloseRequested) continue;
                h = h * 31 + (int)w.Id;
                var tabs = w.Tabs.Items;
                for (int t = 0; t < tabs.Count; t++)
                {
                    h = h * 31 + tabs[t].Id;
                    var leaves = tabs[t].Layout.LeafList;
                    for (int p = 0; p < leaves.Count; p++)
                        h = h * 31 + leaves[p].Id + (leaves[p].Teammate?.LeadId ?? 0) * 7919;
                }
            }
        }
        return h;
    }

    /// <summary>The panel's label for a pane's tab as it is now: "Workspace 3 · Window 2 · Tab 3 · build", or "On DELL U2720Q · …" on Windows (its own window isn't named). Null when the tab is gone.</summary>
    public string? TabLabel(int paneId, uint viewer)
    {
        if (HomeTabOf(paneId, out var w, out int index) is not { } home) return null;
        string title = NotificationText.Clean(home.Title, 32);
        string tab = NotificationText.Join($"Tab {index + 1}", title);
        if (w!.Id == viewer) return tab;
        return NotificationText.Join(ElsewhereLabel(w) ?? "", NotificationText.Join($"Window {w.Id}", tab));
    }

    /// <summary>"Workspace 3" or "On DELL U2720Q" when the pane's tab is in another window, away from where you are looking; else null.</summary>
    public string? Elsewhere(int paneId, uint viewer) =>
        HomeTabOf(paneId, out var w, out _) is not null && w!.Id != viewer ? ElsewhereLabel(w) : null;

    /// <summary>Hook from the windows' notification tick while these labels are on show.</summary>
    public void RefreshPlaces(long now)
    {
        if (OperatingSystem.IsWindows()) RefreshDisplays(now);
        else Hyprland.Refresh(now);
    }

    private string? ElsewhereLabel(TermWindow w) => OperatingSystem.IsWindows() ? DisplayLabel(w) : WorkspaceLabel(w);

    /// <summary>Windows, main thread (SDL's display calls).</summary>
    private static string? DisplayLabel(TermWindow w)
    {
        nint window = w.SdlWindow;
        nint monitor = WindowsMonitors.OfWindow(Sdl.Hwnd(window)), current = WindowsMonitors.OfForegroundWindow();
        if (monitor == 0 || monitor == current) return null;
        return NotificationDisplays.Label(NotificationDisplays.OtherDisplayName(monitor, current, Sdl.GetDisplayName(Sdl.GetDisplayForWindow(window)), Sdl.GetDisplayNames()));
    }

    /// <summary>Windows: the labels are rebuilt when the display in use, or a window's display, has changed.</summary>
    private void RefreshDisplays(long now)
    {
        if (now < _displaysRefreshAt) return;
        _displaysRefreshAt = now + 1000;
        unchecked
        {
            int h = (int)WindowsMonitors.OfForegroundWindow();
            for (int i = 0; i < Windows.Count; i++)
                h = h * 31 + (int)WindowsMonitors.OfWindow(Sdl.Hwnd(Windows[i].SdlWindow));
            _displayStamp = h;
        }
    }

    private string? WorkspaceLabel(TermWindow w) =>
        Hyprland.Latest is { } snapshot && HyprlandWorkspaces.Elsewhere(snapshot, IdentityOf(w)) is { } workspace ? HyprlandWorkspaces.Label(workspace) : null;

    private HyprlandWindowIdentity IdentityOf(TermWindow w)
    {
        int open = 0;
        for (int i = 0; i < Windows.Count; i++) if (!Windows[i].CloseRequested) open++;
        return new HyprlandWindowIdentity(Environment.ProcessId, App.WindowClass ?? "erikterm", w.CompositorTitle, open);
    }

    /// <summary>The tab a notification's pane belongs to now, with its window and index there; null when it is gone.</summary>
    private Tab? HomeTabOf(int paneId, out TermWindow? window, out int index)
    {
        int tabId = NotificationScope.HomeTab(this, paneId);
        for (int i = 0; tabId != 0 && i < Windows.Count; i++)
        {
            var w = Windows[i];
            if (w.CloseRequested) continue;
            var tabs = w.Tabs.Items;
            for (int t = 0; t < tabs.Count; t++)
            {
                if (tabs[t].Id != tabId) continue;
                window = w;
                index = t;
                return tabs[t];
            }
        }
        window = null;
        index = -1;
        return null;
    }

    /// <summary>Jump (bar, panel): show the pane in the window that has it now, raising that window. False when it's gone.</summary>
    public bool Jump(NotificationItem item)
    {
        Center.MarkRead(item.Id);
        for (int i = 0; i < Windows.Count; i++)
        {
            var w = Windows[i];
            if (w.CloseRequested || w.PaneById(item.PaneId) is not { } pane) continue;
            w.JumpToPane(pane);
            if (!ReferenceEquals(Windows.Focused, w) || !w.IsWindowFocused) w.RequestAttention();
            return true;
        }
        Center.ResolveWaiting(item.PaneId);
        return false;
    }

    /// <summary>Hook from the windows' notification tick: waiting items whose pane has closed stop sticking to the bar.</summary>
    public void PrunePanes(long now)
    {
        if (now < _pruneAt || _center is not { UnreadCount: > 0 } c) return;
        _pruneAt = now + 2000;
        foreach (var it in c.Items)
        {
            if (!it.Sticky || it.Read) continue;
            bool live = false;
            for (int i = 0; i < Windows.Count && !live; i++) live = !Windows[i].CloseRequested && Windows[i].PaneById(it.PaneId) is not null;
            if (!live) { c.ResolveWaiting(it.PaneId); return; } // the list changed: the rest next time
        }
    }

    /// <summary>
    /// notify-send with an argument list (no shell); the body is markup-escaped by the center. When the notification daemon
    /// supports actions, clicking it ("default" action) focuses the pane; at most a few such processes wait at once.
    /// On Hyprland a window on another workspace gets it named in the title, asked for off the main thread.
    /// </summary>
    private sealed class NotifySend(ProcessNotifications process) : IDesktopNotifier
    {
        private int _waiting;
        private bool? _onPath;
        private volatile bool _failed; // set by a send on a pool thread

        public bool Available => !_failed && (_onPath ??= !OperatingSystem.IsWindows() && FindOnPath("notify-send"));

        public void Send(int paneId, string title, string markupBody, bool urgent)
        {
            if (!Available) return;
            if (!process.Hyprland.Enabled || process._panes.Find(paneId, out _, out var window) is null)
            {
                Start(paneId, title, markupBody, urgent);
                return;
            }
            var identity = process.IdentityOf(window!); // the window's title is read here, on the main thread
            _ = Task.Run(async () => Start(paneId, await TitleWithWorkspace(title, identity).ConfigureAwait(false), markupBody, urgent));
        }

        private async Task<string> TitleWithWorkspace(string title, HyprlandWindowIdentity identity)
        {
            var snapshot = await process.Hyprland.FetchAsync().ConfigureAwait(false);
            return snapshot is not null && HyprlandWorkspaces.Elsewhere(snapshot, identity) is { } workspace ? HyprlandWorkspaces.DesktopTitle(title, workspace) : title;
        }

        private void Start(int paneId, string title, string markupBody, bool urgent)
        {
            bool wait = Volatile.Read(ref _waiting) < 3;
#pragma warning disable S4036 // Deliberately resolved through the user's PATH (it lives elsewhere on NixOS etc.); erikterm starts the user's shell and tools with that same PATH.
            var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardOutput = wait, RedirectStandardError = true };
#pragma warning restore S4036
            psi.ArgumentList.Add("--app-name=erikterm");
            psi.ArgumentList.Add("--icon=utilities-terminal");
            psi.ArgumentList.Add(urgent ? "--urgency=critical" : "--urgency=normal");
            if (wait)
            {
                psi.ArgumentList.Add("--action=default=Show");
                psi.ArgumentList.Add("--wait");
            }
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(markupBody);
            try
            {
                var p = Process.Start(psi);
                if (p is null) return;
                if (!wait) { p.Dispose(); return; }
                Interlocked.Increment(ref _waiting);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                        await p.WaitForExitAsync().ConfigureAwait(false);
                        if (output.Trim() == "default") { process._panes.RequestFocus(paneId); process._owner.Wake(); }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* the notifier went away: no action to carry out */ }
                    finally
                    {
                        Interlocked.Decrement(ref _waiting);
                        p.Dispose();
                    }
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                _failed = true;
            }
        }

        private static bool FindOnPath(string exe)
        {
            string[] path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            return path.Any(dir => File.Exists(Path.Combine(dir, exe)));
        }
    }
}
