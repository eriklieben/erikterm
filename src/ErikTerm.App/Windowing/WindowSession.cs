using ErikTerm.App.Chrome;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Session;
using ErikTerm.App.Ui;
using ErikTerm.App.WindowFeatures;
using ErikTerm.Core;
using ErikTerm.Pty;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// Session restore, the window's half: rebuilding one saved window (tabs, splits, directories, size / position / full
/// screen, font zoom) and capturing it. Loading, saving and the debounce are App.Session.cs.
/// </summary>
#pragma warning disable S6640 // Native interop: SDL returns the window position through pointers.
internal sealed unsafe class WindowSession(IWindowHost window, Lock gate, TabList<Tab> tabs, FloatingChrome chrome, BackgroundTabs background)
#pragma warning restore S6640
{
    private SessionWindow? _restoreWindow;
    private long _sessionCheckAt;
    private (int W, int H) _windowedSize;

    /// <summary>The user exited the last shell (App forgets the session if this was the last window).</summary>
    public bool ClosedLastPane { get; set; }

    // ---- Startup hooks (called from Open) ------------------------------------------------------

    /// <summary>Hook at the start of Open: the saved window to rebuild, applying its font zoom before the fonts load.</summary>
    public void PrepareRestore(SessionWindow? saved)
    {
        if (saved?.Tabs is not { Count: > 0 }) return;
        _restoreWindow = saved;
        if (double.IsFinite(saved.FontZoom) && saved.FontZoom > 0) window.FontZoom = (float)Math.Clamp(saved.FontZoom, 0.4, 4);
    }

    /// <summary>Hook after the initial window size is set: the saved size, position and full-screen state.</summary>
    public void ApplySessionWindow()
    {
        nint sdlWindow = window.SdlWindow;
        if (_restoreWindow is { } w && w.Width >= 100 && w.Height >= 60)
            Sdl.SetWindowSize(sdlWindow, Math.Min(w.Width, 16384), Math.Min(w.Height, 16384));
        if (_restoreWindow is { X: int x, Y: int y }) Sdl.SetWindowPosition(sdlWindow, x, y); // ignored where the compositor places windows
        _windowedSize = WindowPoints();
        if (_restoreWindow is { Fullscreen: true }) Sdl.SetWindowFullscreen(sdlWindow, true);
    }

    private List<(Tab Tab, SessionTab Saved)> AddRestoredTabs(List<SessionTab> saved)
    {
        var added = new List<(Tab Tab, SessionTab Saved)>();
        foreach (var s in saved)
        {
            var tab = new Tab
            {
                CustomTitle = s.Title is { Length: > 0 } title ? title : null,
                Color = Config.TabColors.TryNormalize(s.Color, out var color) ? color : null, // a hand-edited file may hold anything
            };
            tabs.Add(tab);
            added.Add((tab, s));
        }
        return added;
    }

    private void RestoreTabPanes(Tab tab, SessionTab s, RectI bounds, List<Pane> spawned)
    {
        var root = SessionLayout.Sanitize(s.Layout);
        var leaves = SessionLayout.Leaves(root);
        var rects = SessionLayout.LeafRects(root, bounds, window.Gap);
        var panes = new List<Pane>(leaves.Count);
        for (int i = 0; i < leaves.Count; i++)
        {
            var pane = SpawnRestoredPane(leaves[i], rects[i]);
            spawned.Add(pane);
            panes.Add(pane);
        }
        tab.Layout = SessionLayout.Rebuild(root, panes);
        tab.Focused = panes[Math.Clamp(s.Focused, 0, panes.Count - 1)];
        tab.Zoomed = s.Zoomed is int z && z >= 0 && z < panes.Count && panes.Count > 1 ? panes[z] : null;
        foreach (var p in panes) if (p != tab.Focused) p.Focus.Snap(0);
    }

    /// <summary>Hook instead of spawning the first pane: rebuilds the saved tabs. False when there is nothing to restore.</summary>
    public bool RestoreSession()
    {
        var saved = _restoreWindow;
        _restoreWindow = null;
        if (saved?.Tabs is null) return false;
        var savedTabs = saved.Tabs.Where(t => t is not null).Take(SessionLayout.MaxTabs).ToList();
        if (savedTabs.Count == 0) return false;

        // Tabs first: the tab bar's height depends on how many there are, and panes are spawned at their final size.
        var restored = AddRestoredTabs(savedTabs);
        tabs.Activate(Math.Clamp(saved.ActiveTab, 0, restored.Count - 1));
        tabs.ResetRecency(); // Add made an order nobody chose (Ctrl+Tab)

        var bounds = window.ContentBounds();
        var spawned = new List<Pane>();
        try
        {
            foreach (var (tab, s) in restored) RestoreTabPanes(tab, s, bounds, spawned);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"erikterm: couldn't restore the session: {ex.Message}");
            foreach (var p in spawned) p.Dispose();
            while (tabs.Count > 0) tabs.Remove(tabs.Items[0]);
            return false;
        }

        window.LoadTab(tabs.Active!);
        window.Toast(ToastKind.Info, $"Restored {Plural(restored.Count, "tab")}, {Plural(spawned.Count, "pane")}");
        return true;
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private Pane SpawnRestoredPane(SessionPane saved, RectI rect)
    {
        var settings = window.Settings;
        string? cwd = saved.Cwd is { Length: > 0 } c && Directory.Exists(c) ? c : settings.DefaultCwd;
        int keep = settings.RestoreScrollbackLines;
        Action<Terminal>? replay = keep > 0 && saved.Scrollback is { Count: > 0 } lines
            ? t => t.Feed(SessionScrollback.Replay(lines.Skip(Math.Max(0, lines.Count - keep)).ToList()))
            : null;
        // A restored program is the pane's process (its saved argv, then the shell), never typed in (Session/RestoreLaunch.cs).
        string[] shell = settings.DefaultProg?.ToArray() ?? Pty.Pty.DefaultShellCommand();
        // A shell in a container opens there again (WindowFeatures/WindowContainers.cs); else a restored program, else the shell.
        string[]? command = window.RestoreContainerCommand(saved.Container) ?? RestoreLaunch.CommandFor(saved.Program, settings.RestorePrograms, shell);
        if (command is not null)
        {
            try { return window.SpawnPane(command, cwd, rect, replay); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* e.g. no /bin/sh: a plain shell */ }
        }
        Pane pane;
        try
        {
            pane = window.SpawnPane(null, cwd, rect, replay);
        }
        catch (Exception ex) when (cwd is not null && ex is IOException or InvalidOperationException)
        {
            pane = window.SpawnPane(null, null, rect, replay);
        }
        window.ConsiderAgentResume(pane, saved); // only a plain shell: a restored program already runs (Agents/WindowAgentResume.cs)
        return pane;
    }

    // ---- Running ---------------------------------------------------------------------------------

    /// <summary>Hook from Tick, about once a second: the windowed size (saved while full screen) and activity bookkeeping.</summary>
    public void Tick(long now)
    {
        if (now < _sessionCheckAt) return;
        _sessionCheckAt = now + 1000;
        if ((Sdl.GetWindowFlags(window.SdlWindow) & Sdl.WINDOW_FULLSCREEN) == 0) _windowedSize = WindowPoints();
        background.PruneActivity();
    }

    /// <summary>The window's size in points (a torn-off tab's window starts at its source window's size).</summary>
    public (int W, int H) WindowPoints()
    {
        var (w, h) = window.WindowPixels();
        float density = window.Density;
        float scale = density > 0 ? density : 1;
        // Not the strip the window is lent while the floating chrome is out (FloatingChrome), or a
        // reading taken mid-hover would be saved and the window would come back taller every session.
        return ((int)MathF.Round(w / scale), (int)MathF.Round(h / scale) - chrome.RoomPoints);
    }

    /// <summary>Desktop position in points, where the platform tells (not Wayland: null).</summary>
    public (int X, int Y)? Position
    {
        get
        {
            if (window.IsWayland) return null;
            int x, y;
            return Sdl.GetWindowPosition(window.SdlWindow, &x, &y) ? (x, y) : null;
        }
    }

    // ---- Capture -----------------------------------------------------------------------------

    /// <summary>This window for session.json (App.CaptureSession); null when it has no tabs.</summary>
    public SessionWindow? CaptureWindow(bool includeScrollback)
    {
        if (tabs.Count == 0 || window.Layout is null) return null;
        window.SyncTabs();
        bool full = (Sdl.GetWindowFlags(window.SdlWindow) & Sdl.WINDOW_FULLSCREEN) != 0;
        var size = full ? _windowedSize : WindowPoints();
        var position = full ? null : Position;
        var captured = new SessionWindow
        {
            Width = size.W,
            Height = size.H,
            X = position?.X,
            Y = position?.Y,
            Fullscreen = full,
            FontZoom = Math.Round(window.FontZoom, 4),
            ActiveTab = Math.Max(0, tabs.ActiveIndex),
        };
        foreach (var tab in tabs.Items)
        {
            if (tab.Layout is null) continue;
            // Agent-team teammates are ephemeral: the lead's Claude Code makes new ones (Teammates/WindowTeammates.cs).
            int zoomed = SessionLayout.LeafIndex(tab.Layout, tab.Zoomed, IsTeammate);
            captured.Tabs.Add(new SessionTab
            {
                Title = tab.SessionTitle,
                Color = tab.Color,
                Focused = Math.Max(0, SessionLayout.LeafIndex(tab.Layout, tab.Focused, IsTeammate)),
                Zoomed = zoomed >= 0 ? zoomed : null,
                Layout = SessionLayout.Capture(tab.Layout, p => CapturePane(p, includeScrollback), IsTeammate),
            });
        }
        return captured;
    }

    private static bool IsTeammate(Pane p) => p.Teammate is not null;

    private SessionPane CapturePane(Pane pane, bool includeScrollback)
    {
        var settings = window.Settings;
        var saved = new SessionPane { Cwd = pane.SessionDirectory };
        saved.Container = window.CaptureContainer(pane); // WindowFeatures/WindowContainers.cs
        if (saved.Container is null && pane.Pty.ForegroundProcessName is { } fg && !RestorePrograms.IsShell(fg))
            saved.Program = RestorePrograms.Capture(fg, pane.Pty.ForegroundArguments(), settings.RestorePrograms, pane.LaunchedAgent); // Worktrees/WindowWorktrees.cs
        saved.Agent = window.CaptureAgentResume(pane); // Agents/WindowAgentResume.cs
        if (includeScrollback && settings.RestoreScrollbackLines > 0)
        {
            lock (gate) saved.Scrollback = pane.Term.ExportStyledLines(settings.RestoreScrollbackLines);
            if (saved.Scrollback.Count == 0) saved.Scrollback = null;
        }
        return saved;
    }
}
