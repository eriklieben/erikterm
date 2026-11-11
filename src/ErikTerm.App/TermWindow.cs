using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ErikTerm.App.Accessibility;
using ErikTerm.App.Agents;
using ErikTerm.App.Chrome;
using ErikTerm.App.Containers;
using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Native;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Search;
using ErikTerm.App.Rendering;
using ErikTerm.App.Session;
using ErikTerm.App.Tabs;
using ErikTerm.App.Teammates;
using ErikTerm.App.Ui;
using ErikTerm.App.Vim;
using ErikTerm.App.Windowing;
using ErikTerm.App.WindowFeatures;
using ErikTerm.App.Worktrees;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Pty;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;
using ErikTerm.Ui.Compare;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.App;

/// <summary>
/// One window: panes in a split layout, input routing, actions, overlays and its frame. The process-level
/// <see cref="App"/> (App.Process.cs) owns settings, the event loop, the control socket and the session, and hands
/// each window the events SDL addresses to it (by window id).
/// Threads: the main thread owns SDL, layout and UI state; each pane has a PTY reader thread that
/// feeds its terminal under <see cref="_lock"/> (one lock for the process). Rendering builds the frame under the
/// lock and submits it outside it; with vsync on, bursts of output collapse into one frame.
/// </summary>
#pragma warning disable S6640 // Native interop: SDL returns window sizes and event text through pointers.
internal sealed unsafe class TermWindow
    : IWindowHost, ITabSwitcherWindow<Tab>, ITabColorWindow<Tab>, IPaneSearchWindow<Pane>, IVimWindow<Pane>, IAccessibleWindow,
      IAgentWindow<Pane>, IAgentResumeWindow<Pane>, ITeammateWindow<Pane>, IWorktreeWindow<Pane>, INotificationWindow, // the feature classes' side: end of this file
      ILinkWindow, IReviewWindow, IRecentWindow, ITmuxWindow, IIpcWindow, IPaneLauncher, IPaneInput
#pragma warning restore S6640
{
    private readonly App _app;

    /// <summary>Screen readers on Windows (Accessibility/WindowAccessibility.cs); null elsewhere and until the window exists.</summary>
    private WindowAccessibility? _accessibility;
    nint IAccessibleWindow.SdlWindow => _window;
    IReadOnlyList<Pane> IAccessibleWindow.VisiblePanes => VisiblePanes();
    Pane IAccessibleWindow.FocusedPane => _focused;
    bool IAccessibleWindow.IsWindowFocused => _windowFocused;
    string? IAccessibleWindow.ActiveTabTitle => _tabs.Active?.Title;
    (int Width, int Height) IAccessibleWindow.CellSize => (_fonts.CellWidth, _fonts.CellHeight);
    float IAccessibleWindow.Density => _density;
    void IAccessibleWindow.FocusPane(Pane pane) => Focus(pane);
    void IAccessibleWindow.Redraw() => _redraw = true;
    internal int AccessibilityWakeMs(long now) => _accessibility?.WakeMs(now) ?? int.MaxValue;
    private readonly Lock _lock;

    // Process-wide (App.Process.cs): the same for every window.
    private Settings _settings => _app.Settings;
    private Dictionary<KeyChord, ActionSpec> _keys => _app.Keys;
    private string _configPath => _app.ConfigPath;
    private ColorScheme? _schemeOverride { get => _app.SchemeOverride; set => _app.SchemeOverride = value; } // chosen from the palette this session

    public ColorScheme? SchemePreview { get; set; } // highlighted in the palette right now
    private Theme _theme = new();
    private Theme _overlayTheme = new(); // _theme at the window's font zoom: what pop-ups draw with (SyncOverlayTheme)
    private Theme? _overlayBase;
    private float _overlayZoom = -1;

    private nint _window, _renderer;
    private FontSet _fonts = null!;
    private GlyphAtlas _atlas = null!;
    private DrawList _dl = null!;
    private readonly PaneRenderer _paneRenderer = new(); // per window: image textures belong to this window's renderer

    private LayoutTree<Pane> _layout = null!;
    private readonly List<(Pane Pane, RectI Rect)> _paneRects = [];
    private readonly List<LayoutTree<Pane>.Divider> _dividers = [];
    private Pane _focused = null!;
    private Pane? _zoomed;
    private Tween _zoomBadge = new(0);

    private readonly CommandPalette _palette = new();
    private readonly ContextMenu _menu = new();
    private readonly PaneSelect _paneSelect = new();
    private readonly Toasts _toasts = new();
    private readonly TabList<Tab> _tabs = new();
    private readonly TabBar _tabBar = new();
    private readonly SearchBar _search = new();
    private readonly AboutScreen _about = new();

    // The parts this window is made of (Chrome/, Windowing/, Rendering/FramePacing.cs, Panes/WindowScrollbar.cs, and
    // the feature classes in Tabs/, Search/, Vim/). They reach the window through IWindowHost (Windowing/IWindowHost.cs).
    private readonly WindowTabSwitcher<Tab> _tabSwitcher;
    private readonly WindowTabColors<Tab> _tabColors;
    private readonly WindowPaneSearch<Pane> _paneSearch;
    private readonly WindowVim<Pane> _vim;
    private readonly FloatingChrome _floating;
    private readonly WindowBackdrop _backdrop;
    private readonly WindowHitTest _hitTest;
    private readonly WindowChrome _chrome;
    private readonly FramePacing _frames = new();
    private readonly WindowScrollbar _scrollbar;
    private readonly WindowSearch _windowSearch;
    private readonly WindowBroadcast _broadcast;
    private readonly KeyInputRouter _keyInput;
    private readonly ImeComposer _ime = new();
    private readonly WindowProgress _progress;
    private readonly CloseConfirmFlow _closeConfirm;
    private readonly ExitHoldFlow _exitHold;
    private readonly BackgroundTabs _backgroundTabs;
    private readonly TabBarController _tabCtl;
    private readonly TabTearOff _tearOff;
    private readonly WindowSession _session;
    private readonly WindowThemes _themes;
    private readonly WindowErrors _errors;
    private readonly AboutPanel _aboutPanel;
    private readonly PaneActions _paneActions;
    private readonly CommandCatalog _commands;
    private readonly ActionDispatcher _actions;
    private readonly ScriptSteps _script;
    private readonly QuickSelect _quickSelect = new(); // labels on what's on screen (WindowFeatures/WindowLinks.cs)
    private readonly InfoBar _infoBar = new(); // also posted to by OSC 52, close confirmation, git operations and themes

    // Feature classes. What they may ask of this window is at the end of this file.
    private readonly WindowNotifications _notifications;
    private readonly WindowWorktrees<Pane> _worktrees;
    private readonly WindowAgents<Pane> _agents;
    private readonly WindowAgentResume<Pane> _agentResume;
    private readonly WindowTeammates<Pane> _teammates;
    private readonly BackgroundJobs _jobs;                  // git, GitHub, worktrees, links, themes
    private readonly PaneTracks _tracks = new();            // each pane's directory and repository
    private readonly IpcTargets _ipcTargets;
    private readonly WindowTmux _tmux;
    private readonly WindowIpc _ipc;
    private readonly WindowLaunches _launches;
    private readonly WindowGitHub _github;
    private readonly WindowEditor _editor;
    private readonly WindowRepoPlans _repoPlans;
    private readonly WindowContainers _containers;
    private readonly WindowIntegrations _integrations;
    private readonly WindowReview _review;
    private readonly WindowLinks _links;
    private readonly WindowDrop _drop;
    private readonly WindowOsc52 _osc52;
    private readonly WindowRecent _recent;
    private readonly WindowSnippets _snippets;

    private float _scale = 1;       // how large text and chrome are drawn (the display's scale)
    private float _density = 1;     // device pixels per window point: SDL's coordinates -> ours
    private float FontZoom { get; set; } = 1;
    private bool _windowFocused = true;
    public bool SuppressText { get; set; }
    private bool _redraw = true;
    private string _title = "";
    private string _compositorTitle = "erikterm"; // what SDL last told the compositor; the name passed to CreateWindow
    private bool _vsync = true;

    // Mouse state. The cursors are process-wide: SDL has one current cursor.
    private nint _cursorText => _app.CursorText;
    private nint _cursorDefault => _app.CursorDefault;
    private nint _cursorEw => _app.CursorEw;
    private nint _cursorNs => _app.CursorNs;
    private nint _cursorPointer => _app.CursorPointer;
    private LayoutTree<Pane>.Node? _hoverDivider, _dragDivider;
    private Tween _dividerGlow = new(0);
    private Pane? _selecting;
    private Pane? _mouseReportPane;
    private int _buttonsDown;

    // Debug: ERIKTERM_SCREENSHOT=/path/frame.bmp saves the first frame drawn after ERIKTERM_SCREENSHOT_DELAY ms
    // (a {window} in the path: every window, else the first one). Set by App.CreateWindow.
    internal string? _screenshotPath;
    internal long _screenshotAt;

    /// <summary>SDL's id for this window: events carry it, `erikterm cli` shows it as window_id.</summary>
    public uint Id { get; private set; }

    /// <summary>Panes and tabs are all gone (or the window was asked to close): App closes it after this loop iteration.</summary>
    internal bool CloseRequested => _closeRequested;

    /// <summary>Saved with the session (false for windows started with -e).</summary>
    internal bool SavesSession = true;

    internal nint SdlWindow => _window;
    internal float Scale => _scale;
    internal float Density => _density;
    internal bool IsWindowFocused => _windowFocused;

    /// <summary>Debug script steps (App.RunScript) for this window: the original single-window step set.</summary>
    internal void RunScript(string kind, string arg, long now)
    {
        switch (kind)
        {
            case "action" when ActionSpec.TryParse(arg, out var a, out _): _actions.Execute(a); break;
            case "preedit": OnTextEditing(arg); break; // what an input method would be composing
            case "type": SuppressText = false; OnTextInput(arg, now); break; // text of its own, not the echo of a key: step
            case "ckey": OnKeyDown(ScriptSteps.Key(arg), Sdl.KMOD_CTRL, now); break; // Ctrl+key (Ctrl+Enter in the worktree form)
            case "key": OnKeyDown(ScriptSteps.Key(arg), 0, now); break; // same names as skey:/alt: (Windowing/ScriptSteps.cs), incl. tab
            case "menu":
                var t = _focused.Target;
                _commands.OpenContextMenu(t.X + t.W * 0.3f, t.Y + t.H * 0.25f, now);
                break;
            case "hover-menu": _menu.HandleKey(Sdl.K_DOWN, now); break;
            case "select":
                lock (_lock)
                {
                    var term = _focused.Term;
                    _focused.Selection = new Selection { AnchorLine = term.ViewTopLine, AnchorCol = 0, HeadLine = term.ViewTopLine + 1, HeadCol = 12, Mode = SelectionMode.Cell };
                }
                break;
            case "record": Rendering.DrawList.Record(arg == "on"); break; // the demo recorder (ERIKTERM_RECORD_DIR, Rendering/DrawList.cs)
            case "typex": SuppressText = false; OnTextInput(System.Text.RegularExpressions.Regex.Unescape(arg), now); break; // type: with C# escapes (\x20)
            case "toast": Toast(ToastKind.Success, "Configuration reloaded", "erikterm.toml"); break;
            case "unfocus": _windowFocused = false; break; // as if another app had focus (notification routing)
            case "throw": _errors.DebugThrowFrames = int.TryParse(arg, out int frames) ? frames : 1; break; // the next N frames fail (Windowing/WindowErrors.cs)
            case "infobar": // presses the shown bar's N-th button
                if (int.TryParse(arg, out int button) && _infoBar.Current is { } bar && button < bar.Buttons.Length) bar.Buttons[button].Run();
                break;
            default: _script.Run(kind, arg, now); break; // Windowing/ScriptSteps.cs
        }
        _redraw = true;
    }

    public TermWindow(App app)
    {
        _app = app;
        _lock = app.Lock;
        _notifications = new WindowNotifications(this, app, _infoBar);
        _vim = new WindowVim<Pane>(this, app, _search, _lock);
        _worktrees = new WindowWorktrees<Pane>(this, app, _lock);
        _agents = new WindowAgents<Pane>(this, app, _lock);
        _agentResume = new WindowAgentResume<Pane>(this);
        _paneSearch = new WindowPaneSearch<Pane>(this, _lock);
        _teammates = new WindowTeammates<Pane>(this);
        _tabColors = new WindowTabColors<Tab>(this);
        _tabSwitcher = new WindowTabSwitcher<Tab>(this, app);
        _floating = new FloatingChrome(this);
        _backdrop = new WindowBackdrop(this);
        _hitTest = new WindowHitTest(this, _floating, _tabBar);
        _windowSearch = new WindowSearch(this, _lock, _search, _vim, _paneSearch, _menu, _palette);
        _scrollbar = new WindowScrollbar(this, _lock, _paneRenderer, _windowSearch);
        _broadcast = new WindowBroadcast(this, _lock, _tabs, _menu, _paneSelect);
        _keyInput = new KeyInputRouter(this, _lock, _broadcast);
        _progress = new WindowProgress(this, _lock, _tabs, _tabColors);
        _closeConfirm = new CloseConfirmFlow(this, _tabs);
        _exitHold = new ExitHoldFlow(this, _lock, _closeConfirm);
        _backgroundTabs = new BackgroundTabs(this, _lock, _tabs);
        _tabCtl = new TabBarController(this, _tabs, _tabBar, _floating, _tabSwitcher, _tabColors, _paneRenderer, _backgroundTabs, _progress,
            _windowSearch, _closeConfirm, _exitHold, _menu, _paneSelect);
        _tearOff = new TabTearOff(this, _lock, _tabs, _tabBar, _tabCtl, _tabColors, _keyInput, _windowSearch, _closeConfirm, _menu, _paneSelect);
        _session = new WindowSession(this, _lock, _tabs, _floating, _backgroundTabs);
        _themes = new WindowThemes(this, _menu, _palette, _toasts);
        _errors = new WindowErrors(this);
        _aboutPanel = new AboutPanel(this, _about, _menu, _palette, _paneSelect);
        _paneActions = new PaneActions(this, _lock, _paneSelect, _backdrop);
        _commands = new CommandCatalog(this, _tabCtl, _tearOff, _themes, _broadcast, _exitHold, _closeConfirm, _paneActions, _aboutPanel, _vim,
            _tabColors, _menu);
        _actions = new ActionDispatcher(this, _paneActions, _tabCtl, _tabs, _windowSearch, _closeConfirm, _exitHold, _broadcast, _themes, _aboutPanel,
            _commands, _tabSwitcher, _tabColors, _vim, _menu, _palette);
        _chrome = new WindowChrome(this, _tabCtl, _windowSearch, _paneSearch, _paneRenderer);
        _script = new ScriptSteps(this, _lock, _tabBar, _tabSwitcher, _vim, _exitHold);
        _paneRenderer.Search = _search;

        // WindowFeatures/: git, GitHub, links, the editor, IPC, … (each takes the roles of this window it needs).
        _jobs = new BackgroundJobs(Wake);
        _ipcTargets = new IpcTargets(this);
        _tmux = new WindowTmux(this, this, _ipcTargets, _teammates, _lock);
        _ipc = new WindowIpc(this, this, this, _ipcTargets, new IpcHandlers(_tmux, _agents, _teammates, _worktrees, _tabColors, _vim), _lock);
        _launches = new WindowLaunches(this, this, _ipc);
        var git = new WindowGit(this, _jobs, _tracks, _launches);
        _github = new WindowGitHub(this, _jobs, _tracks, _launches, _lock);
        _editor = new WindowEditor(this, _jobs, _tracks, git, _github, _launches, _lock);
        _repoPlans = new WindowRepoPlans(this, this, _jobs, _lock);
        var scan = new RepoScan(_jobs, _tracks, git);
        var gitOps = new WindowGitOperations(this, this, scan, _infoBar, _repoPlans, _editor, _launches);
        _containers = new WindowContainers(this, this, _jobs, _tracks);
        _integrations = new WindowIntegrations(this, scan, _github, gitOps, _containers, _editor);
        _review = new WindowReview(this, _jobs, _tracks, gitOps, _launches, _lock);
        _links = new WindowLinks(this, this, _quickSelect, _editor, new WindowPrompts(this, this, _lock), _lock);
        _drop = new WindowDrop(this, this, this);
        _osc52 = new WindowOsc52(this, _infoBar, app.ClipboardWrites);
        _recent = new WindowRecent(this, this, this, _lock);
        _snippets = new WindowSnippets(this, this, _recent, _lock);
    }

    private static long Now => Environment.TickCount64;
    private ColorScheme Scheme => SchemePreview ?? _schemeOverride ?? _settings.Colors;
    private float FontPixels => (float)_settings.FontSize * 96f / 72f * _scale * FontZoom;
    private int Gap => Math.Max(3, (int)MathF.Round(9 * _scale));
    private float Anim => _settings.AnimationDurationMs;

    // ==== Startup ==============================================================================

    /// <summary>What a new window starts with: a saved window, a tab moved from another window, or a new pane.</summary>
    internal sealed class OpenOptions
    {
        public SessionWindow? Restore;
        public string[]? Command;
        public string? Cwd;
        public Tab? Adopt;
        /// <summary>Size in points (else initial_cols / initial_rows).</summary>
        public (int W, int H)? Size;
        /// <summary>Desktop position in points; compositors that place windows themselves (Wayland) ignore it.</summary>
        public (int X, int Y)? Position;
        public bool Quiet;
    }

    /// <summary>Creates the SDL window and renderer and fills it (was the first half of App.Run).</summary>
    public void Open(OpenOptions o)
    {
        _session.PrepareRestore(o.Restore); // saved tabs + font zoom (Windowing/WindowSession.cs)
        if (o.Command is not null) SavesSession = false;
        try
        {
            ulong flags = Sdl.WINDOW_RESIZABLE | Sdl.WINDOW_HIGH_PIXEL_DENSITY | Sdl.WINDOW_TRANSPARENT | Sdl.WINDOW_HIDDEN;
            if (!_settings.WindowDecorations) flags |= Sdl.WINDOW_BORDERLESS;
            _window = Sdl.CreateWindow("erikterm", 1000, 640, flags);
            if (_window == 0) throw new InvalidOperationException("SDL_CreateWindow: " + Sdl.GetError());
            WindowIcon.Apply(_window); // Native/WindowIcon.cs
            _renderer = Sdl.CreateRenderer(_window, null);
            if (_renderer == 0) throw new InvalidOperationException("SDL_CreateRenderer: " + Sdl.GetError());
            Sdl.SetRenderVSync(_renderer, 1);
            Id = Sdl.GetWindowID(_window);
            (_scale, _density) = (UiScale(), WindowDensity());
            _hitTest.Apply(); // borderless: name the drag/resize regions ourselves (Chrome/WindowHitTest.cs)
            _accessibility = WindowAccessibility.Attach(this, _lock, Wake); // UI Automation on Windows

            _fonts = new FontSet(_settings.Font, _settings.FontFallback, FontPixels, _settings.LineHeight);
            _atlas = new GlyphAtlas(_renderer, _fonts);
            _dl = new DrawList(_renderer, _atlas, _fonts);
            RebuildTheme();
            _floating.ApplyFrame(); // rounded corners + themed border: needs the theme (Chrome/FloatingChrome.cs)
            _backdrop.Apply(); // blur behind a see-through window (Chrome/WindowBackdrop.cs)
            if (!o.Quiet) Console.Error.WriteLine($"erikterm: {_fonts.Description}");

            var pad = ScaledPadding();
            if (o.Size is { } size && size.W >= 100 && size.H >= 60)
                Sdl.SetWindowSize(_window, Math.Min(size.W, 16384), Math.Min(size.H, 16384));
            else
                Sdl.SetWindowSize(_window,
                    (int)((_settings.InitialCols * _fonts.CellWidth + pad.Left + pad.Right) / _density),
                    (int)((_settings.InitialRows * _fonts.CellHeight + pad.Top + pad.Bottom) / _density));
            _session.ApplySessionWindow(); // saved size / position / full screen (Windowing/WindowSession.cs)
            if (o.Position is { } pos) Sdl.SetWindowPosition(_window, pos.X, pos.Y);
            Sdl.ShowWindow(_window);

            _palette.Execute = _commands.RunCommand;
            _palette.PreviewChanged = PreviewScheme;
            _paneSelect.Selected = Focus;

            if (o.Adopt is { } adopted) _tearOff.AdoptFirstTab(adopted); // a tab torn off another window (Windowing/TabTearOff.cs)
            else if (!_session.RestoreSession()) // Windowing/WindowSession.cs
            {
                var first = SpawnPane(o.Command, o.Cwd ?? _settings.DefaultCwd, ContentBounds());
                _layout = new LayoutTree<Pane>(first);
                _focused = first;
            }
            _tabCtl.Init(_tearOff); // the first tab, the tab bar's buttons (Windowing/TabBarController.cs)
            ApplyLayout(animate: false);
            Sdl.StartTextInput(_window);
        }
        catch
        {
            Destroy(disposePanes: o.Adopt is null);
            throw;
        }
    }

    /// <summary>Frees the window (was the cleanup of App.Run). Panes still in it are closed unless they moved on.</summary>
    public void Destroy(bool disposePanes = true)
    {
        if (disposePanes)
        {
            _tabCtl.DisposeBackgroundTabs();
            if (_layout is not null) foreach (var p in _layout.LeafList) p.Dispose();
        }
        _layout = null!;
        _atlas?.Dispose();
        _fonts?.Dispose();
        _hitTest.Release(); // before the window goes (Chrome/WindowHitTest.cs)
        _accessibility?.Dispose();
        _accessibility = null;
        if (_renderer != 0) Sdl.DestroyRenderer(_renderer);
        if (_window != 0) Sdl.DestroyWindow(_window);
        _renderer = _window = 0;
    }

    /// <summary>Device pixels per window point: the factor between SDL's coordinates and the pixels we draw in.</summary>
    private float WindowDensity()
    {
        float d = Sdl.GetWindowPixelDensity(_window);
        return d > 0 ? d : 1;
    }

    /// <summary>
    /// How large to draw everything. Usually the pixel density, and identical to it on Wayland and macOS, where a
    /// window's points are its logical units. Windows is the odd one out: it sizes windows in physical pixels and
    /// reports a density of 1 whatever the display scale, so the scale has to come from the display there, or a
    /// 150% screen gets a 100% terminal.
    /// </summary>
    private float UiScale() => MathF.Max(WindowDensity(), MathF.Max(1, Sdl.GetWindowDisplayScale(_window)));

    // ==== Panes ================================================================================

    /// <summary><paramref name="prepare"/> runs on the new terminal before the program starts (session restore replays scrollback).</summary>
    private Pane SpawnPane(IReadOnlyList<string>? command, string? cwd, RectI area, Action<Terminal>? prepare = null, bool fromTmux = false)
    {
        int cols = Math.Max(2, area.W / _fonts.CellWidth), rows = Math.Max(1, area.H / _fonts.CellHeight);
        string[] argv = command?.ToArray() ?? _settings.DefaultProg?.ToArray() ?? DefaultShellCommand();
        var env = ChildEnvironment();
        _app.AddIpcEnvironment(env, _tmux.Emulation); // ERIKTERM_SOCKET / ERIKTERM_PANE, tmux emulation (App.Ipc.cs)
        if (cwd is not null && !Directory.Exists(cwd)) cwd = null;
        Worktrees.WorktreePorts.Shared.AddEnvironment(env, cwd); // ERIKTERM_PORT_BASE / ERIKTERM_PORT_COUNT
        // LaunchArgv stays the plain command: a restart injects again (Windowing/ExitHoldFlow.cs).
        string[] spawnArgv = command is null && !fromTmux ? WithShellIntegration(argv, env) : argv;

        var term = new Terminal(cols, rows, _settings.ScrollbackLines);
        if (env.TryGetValue("TERM", out var termName)) term.TerminalName = termName; // XTGETTCAP TN
        ApplySchemeTo(term);
        term.DefaultCursorShape = _settings.CursorShape;
        term.DefaultCursorBlink = _settings.CursorBlinks;
        term.AllowImageFiles = _settings.AllowKittyFileTransmission; // kitty t=f/t=t/t=s (off by default)
        (term.CellPixelWidth, term.CellPixelHeight) = (_fonts.CellWidth, _fonts.CellHeight); // image sizing
        _osc52.Attach(term); // OSC 52 (WindowFeatures/WindowOsc52.cs)
        prepare?.Invoke(term);
        var pty = Pty.Pty.Spawn(new PtyOptions
        {
            Program = spawnArgv[0],
            Arguments = spawnArgv,
            Environment = env,
            WorkingDirectory = cwd,
            Columns = cols,
            Rows = rows,
        });
        var pane = new Pane(term, pty) { LaunchArgv = argv, LaunchCwd = cwd }; // Restart Pane runs them again (Windowing/ExitHoldFlow.cs)
        term.Response = data => pty.WriteReply(data); // queued and capped: never blocks the reader under the lock
        pane.StartReader(_lock, Wake);
        return pane;
    }

    /// <summary>
    /// The environment for a pane's program: erikterm's defaults, TERM from the <c>term</c> setting (xterm-erikterm only
    /// when its terminfo is installed), then set_environment_variables, which win.
    /// </summary>
    private Dictionary<string, string> ChildEnvironment()
    {
        var env = Pty.Pty.DefaultEnvironment();
        if (!OperatingSystem.IsWindows())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var lookup = new Dictionary<string, string>(env);
            foreach (var (k, v) in _settings.SetEnvironmentVariables) lookup[k] = v; // TERMINFO_DIRS set there counts
            env["TERM"] = TerminfoLocator.Resolve(_settings.Term, k => lookup.TryGetValue(k, out var v) ? v : null, home);
        }
        foreach (var (k, v) in _settings.SetEnvironmentVariables) env[k] = v;
        return env;
    }

    /// <summary>Shell integration for the shell erikterm starts itself (Integrations/ShellIntegration.cs): the argv to run, with the injection added to env.</summary>
    private string[] WithShellIntegration(string[] argv, Dictionary<string, string> env) =>
        _settings.ShellIntegration == ShellIntegrationMode.Auto ? ShellIntegration.Apply(argv, env) : argv;

    /// <summary>The shell a pane runs by default (a login shell on macOS: Pty.DefaultShellCommand).</summary>
    private static string[] DefaultShellCommand() => Pty.Pty.DefaultShellCommand();

    private void Wake() => _app.Wake(); // process-wide wake event (App.Process.cs)

    /// <summary>The title the compositor has for this window: what tells it from the process's other windows on Hyprland.</summary>
    internal string CompositorTitle => _compositorTitle;

    private void SetCompositorTitle(string title)
    {
        _compositorTitle = title;
        Sdl.SetWindowTitle(_window, title);
    }

    private Pane? Split(SplitAxis axis, IReadOnlyList<string>? command = null, string? cwd = null, bool focus = true, Action<Core.Terminal>? prepare = null)
    {
        var target = _focused;
        if (_zoomed is not null) SetZoom(null);
        var targetRect = target.Target;
        var (_, _, second) = LayoutTree<Pane>.SplitRect(targetRect, axis, 0.5, Gap);
        Pane pane;
        try
        {
            // A shell split from a pane in a container opens in that container (WindowFeatures/WindowContainers.cs).
            pane = SpawnPane(command ?? (cwd is null ? _containers.ShellFor(target) : null), cwd ?? target.StartDirectory(_settings.DefaultCwd), second, prepare);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Toast(ToastKind.Error, "Couldn't open a new pane", ex.Message);
            return null;
        }
        pane.CloseOnExit = command is not null; // erikterm's own tool splits (diffs, key bindings, editors) never hold (exit_behavior)
        _layout.Split(target, pane, axis);

        // The new pane grows out of the divider and fades in; the old one eases to its new size.
        var start = axis == SplitAxis.Horizontal
            ? new RectF(targetRect.Right - 1, second.Y, 1, second.H)
            : new RectF(second.X, targetRect.Bottom - 1, second.W, 1);
        pane.Rect.Snap(start);
        pane.Appear.Snap(0);
        pane.Appear.Set(1, Now, Anim * 1.5f);
        pane.Focus.Snap(0);
        if (focus) Focus(pane);
        ApplyLayout(animate: true);
        return pane;
    }

    private void ClosePane(Pane pane)
    {
        if (!_layout.Contains(pane)) return;
        if (_zoomed == pane) _zoomed = null;
        var next = _layout.Remove(pane);
        if (_selecting == pane) _selecting = null;
        if (_mouseReportPane == pane) _mouseReportPane = null;
        _scrollbar.Forget(pane);
        pane.Dispose();
        if (next is null)
        {
            if (!_tabCtl.OnLastPaneClosed()) _closeRequested = true;
            return;
        }
        _teammates.Sync(_layout); // a closed lead's teammates open (Teammates/WindowTeammates.cs)
        if (next.LayoutHidden) next = TeamRoster.FirstShown(_layout) ?? next;
        if (_focused == pane || !_layout.Contains(_focused)) Focus(next);
        ApplyLayout(animate: true);
    }

    private bool _closeRequested;

    private void Focus(Pane pane)
    {
        if (pane.LayoutHidden && _layout.Contains(pane)) { _teammates.Open(pane); return; } // a parked teammate opens (Teammates/WindowTeammates.cs)
        if (_focused == pane) return;
        long now = Now;
        _focused?.Focus.Set(0, now, Anim * 1.5f);
        ReportFocusMoved(_focused, pane);
        _focused = pane;
        pane.Focus.Set(1, now, Anim);
        if (_zoomed is not null && _zoomed != pane) SetZoom(pane);
        _redraw = true;
    }

    /// <summary>Focus moved between panes or tabs of a focused window: programs that asked (mode 1004) hear about it.</summary>
    private void ReportFocusMoved(Pane? from, Pane to)
    {
        if (!_windowFocused || ReferenceEquals(from, to)) return;
        if (from is { Term.FocusReporting: true }) WriteInput(from, "\e[O"u8);
        if (to.Term.FocusReporting) WriteInput(to, "\e[I"u8);
    }

    private void SetZoom(Pane? pane)
    {
        _zoomed = pane;
        _zoomBadge.Set(pane is null ? 0 : 1, Now, Anim);
        ApplyLayout(animate: true);
    }

    private RectI ContentBounds()
    {
        var (w, h) = WindowPixels();
        var p = ScaledPadding();
        int bar = _tabCtl.Reserve() + _notifications.ReserveTop, below = _notifications.ReserveBottom; // info / notification bars
        // The floating chrome pushes the panes down instead of covering them, and deliberately does not take the
        // room off their height: same rows and columns, so nothing reflows (Chrome/FloatingChrome.cs).
        return new RectI(p.Left, p.Top + bar + _floating.Lift, Math.Max(1, w - p.Left - p.Right), Math.Max(1, h - p.Top - bar - p.Bottom - below));
    }

    private Padding ScaledPadding()
    {
        int S(int v) => (int)MathF.Round(v * _scale);
        var p = _settings.WindowPadding;
        return new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));
    }

    /// <summary>Recomputes pane rectangles, resizes terminals/PTYs to fit, and (optionally) animates the change.</summary>
    private void ApplyLayout(bool animate, float durationMs = 0)
    {
        var bounds = ContentBounds();
        _teammates.Sync(_layout); // leads' teammate lists, orphans open (Teammates/WindowTeammates.cs)
        _layout.Layout(bounds, Gap, _paneRects, _dividers); // parked teammates take no room
        long now = Now;
        float duration = 0;
        if (animate) duration = durationMs > 0 ? durationMs : Anim * 1.3f; // the window bar passes its own

        foreach (var (pane, rect) in _paneRects)
        {
            var r = _zoomed is not null && pane == _zoomed ? bounds : rect;
            pane.Target = r;
            if (duration > 0) pane.Rect.Set(r, now, duration); else pane.Rect.Snap(r);
            // The grid leaves out the teammate strip at the bottom of a lead (Teammates/WindowTeammates.cs).
            if (SizeTerminal(pane, r, _teammates.StripHeightFor(pane, r)))
                CheckReflow(pane); // selection, search, hover refer to old line numbers
        }
        _teammates.SizeParked(_layout, bounds, background: false); // where parked teammates would be (Teammates/WindowTeammates.cs)
        _backgroundTabs.Resize(bounds, _tabCtl.Outgoing); // Windowing/BackgroundTabs.cs
        _redraw = true;
    }

    private readonly List<Pane> _visiblePanes = [];

    /// <summary>The panes on screen (a reused list: don't hold on to it across calls).</summary>
    private List<Pane> VisiblePanes()
    {
        _visiblePanes.Clear();
        if (_zoomed is not null) _visiblePanes.Add(_zoomed);
        else foreach (var (pane, _) in _paneRects) _visiblePanes.Add(pane);
        return _visiblePanes;
    }

    // ==== Frame (driven by App.EventLoop, App.Process.cs) ==========================================

    internal void ReapExitedPanes()
    {
        foreach (var pane in _layout.LeafList)
            if (pane.Exited && !_exitHold.HoldIfWanted(pane)) ClosePane(pane); // exit_behavior may keep it (Windowing/ExitHoldFlow.cs)
        _tabCtl.ReapBackgroundTabs();
    }

    internal void Tick(long now)
    {
        // A fallback font found off-thread for a code point drawn without one: drop the cached .notdef glyphs and redraw.
        if (_fonts.PollFallbacks())
        {
            _atlas.Clear();
            _redraw = true;
        }
        foreach (var pane in _layout.LeafList)
        {
            int bells = pane.Term.BellCount;
            if (bells != pane.SeenBellCount)
            {
                pane.SeenBellCount = bells;
                if (_settings.VisualBell)
                {
                    pane.Bell.Snap(1);
                    pane.Bell.Set(0, now, 450);
                }
            }
            CheckReflow(pane);
            _scrollbar.TickSmoothScroll(pane, now); // Panes/WindowScrollbar.cs
            bool scrolledBack = pane.Term.ViewOffset > 0;
            float want = scrolledBack || _scrollbar.ShowsMarks(pane) || now - pane.ScrollActivityAt < 900 ? 1 : 0;
#pragma warning disable S1244 // Target only ever holds the 0 or 1 set here, so exact comparison is the point.
            if (pane.Scrollbar.Target != want) pane.Scrollbar.Set(want, now, want > 0 ? 120 : 400);
#pragma warning restore S1244
        }
        _chrome.Tick(now); // tab bar, search bar, floating chrome (Chrome/WindowChrome.cs)
        TickShell(now); // links, OSC 52 (WindowFeatures/WindowLinks.cs, WindowOsc52.cs)
        TickIntegrations(now); // git status, GitHub, drag and drop (WindowFeatures/WindowIntegrations.cs, WindowDrop.cs)
        _session.Tick(now); // windowed size, activity pruning (Windowing/WindowSession.cs); saving is App.TickSession
        _agents.Tick(now); // coding-agent status (Agents/WindowAgents.cs)
        _agentResume.Tick(now); // the "Resume N agents" pill (Agents/WindowAgentResume.cs)
        _notifications.TickFinishedCommands(now); // long-running commands that finished out of sight
        _progress.Tick(now); // OSC 9;4 per pane and tab (Windowing/WindowProgress.cs)
        _notifications.Tick(now); // notification bar, question bar, panel
        _vim.Tick(); // vim mode: evicted lines, incremental search (Vim/WindowVim.cs)
        _broadcast.Tick(); // Windowing/WindowBroadcast.cs
        _worktrees.Tick(now); // Worktrees/WindowWorktrees.cs
        _paneSearch.Tick(); // results of the search across panes (Search/WindowPaneSearch.cs)
        _themes.Tick(now); // Windowing/WindowThemes.cs
        _accessibility?.Tick(now); // screen readers: focus, text events, announcements
    }

    internal bool IsAnimating(long now)
    {
        if (OverlaysAnimating(now)) return true;
        if (_chrome.IsAnimating(now)) return true; // Chrome/WindowChrome.cs
        if (_links.IsAnimating(now, _layout.LeafList)) return true; // WindowFeatures/WindowLinks.cs
        if (_agents.IsAnimating(now) || _agentResume.IsAnimating(now)) return true; // Agents/
        if (_notifications.IsAnimating(now)) return true;
        if (_integrations.IsAnimating(now) || _drop.IsAnimating(now)) return true; // WindowFeatures/
        if (_vim.IsAnimating(now)) return true; // Vim/WindowVim.cs
        if (_teammates.IsAnimating(now)) return true; // Teammates/WindowTeammates.cs
        if (ExitHoldFlow.IsAnimating(_layout, now)) return true; // Windowing/ExitHoldFlow.cs
#pragma warning disable S3267 // IsAnimating runs every frame: no LINQ iterator or closure here.
        foreach (var p in _layout.LeafList)
            if (p.Rect.IsAnimating(now) || p.Focus.IsAnimating(now) || p.Appear.IsAnimating(now) || p.Scrollbar.IsAnimating(now) || p.Bell.IsAnimating(now)
                || p.Scroll.IsAnimating(now))
                return true;
#pragma warning restore S3267
        return false; // the scrollbar's fade-out is a timed wake (ScrollbarWakeMs), not a reason to draw every frame
    }

    private bool OverlaysAnimating(long now) =>
        _tabSwitcher.IsAnimating(now) || _worktrees.IsAnimating(now) || _palette.IsAnimating(now) || _menu.IsAnimating(now) || _paneSelect.IsAnimating(now)
        || _toasts.IsAnimating() || _about.IsAnimating(now) || _zoomBadge.IsAnimating(now) || _dividerGlow.IsAnimating(now);

    /// <summary>Whether <see cref="Render"/> would draw (a cheap pre-check, so App can pick the window that waits for vsync).</summary>
    internal bool WantsFrame(bool animating)
    {
        if (_redraw || animating) return true;
        foreach (var p in _layout.LeafList) if (p.Term.Dirty && !p.LayoutHidden) return true; // parked teammates draw nothing
        return false;
    }

    /// <summary>With several windows drawing, only one waits for vsync per loop iteration (App.RenderWindows).</summary>
    internal void SetVSync(bool on)
    {
        if (on == _vsync) return;
        _vsync = on;
        Sdl.SetRenderVSync(_renderer, on ? 1 : 0);
    }

    internal void Render(bool animating)
    {
        long now = Now;
        if (_frames.HoldForFlood(now, _redraw, _layout)) return; // Rendering/FramePacing.cs
        bool dirty;
        lock (_lock)
        {
            dirty = _redraw || animating;
            foreach (var p in _layout.LeafList) dirty |= p.Term.Dirty && !p.LayoutHidden;
            if (!dirty) return;

            _frames.Stats?.BeginFrame();
            _dl.Begin();
            if (_errors.DebugThrowFrames > 0) _errors.DebugThrow(); // debug script throw:N (Windowing/WindowErrors.cs)
            _atlas.Flushed = false;
            DrawPanes(now);
            if (_atlas.Flushed) { _dl.Begin(); _atlas.Flushed = false; DrawPanes(now); } // atlas rebuilt mid-frame
            _ime.UpdateArea(_window, _focused, _dl, now); // Windowing/ImeComposer.cs
            foreach (var p in _layout.LeafList) (p.Term.Dirty, p.LinesScrolledAtFrame) = (false, p.Term.LinesScrolled);
            _frames.Drawn(now);

            if (!ReferenceEquals(_title, _focused.Term.Title))
            {
                _title = _focused.Term.Title;
                SetCompositorTitle(_title);
            }
        }

        DrawOverlays(now);
        if (_atlas.Flushed)
        {
            // An overlay filled the atlas: everything drawn before it sampled the old contents.
            _atlas.Flushed = false;
            _dl.Begin();
            lock (_lock) DrawPanes(now);
            DrawOverlays(now);
        }
        _frames.Stats?.EndFrame(_dl.QuadCount, _paneRenderer, Id);

        _redraw = false;
        if (_screenshotPath is not null && now >= _screenshotAt)
        {
            _dl.CapturePath = _screenshotPath;
            _screenshotPath = null;
        }
        float opacity = _backdrop.Opaque ? 1 : _theme.WindowOpacity;
        _dl.Present(Scheme.Background, opacity); // blocks on vsync
        if (_dl.RecordSkipped) _redraw = true;
    }

    private void DrawOverlays(long now)
    {
        var (w, h) = WindowPixels();
        var window = new RectF(0, 0, w, h);
        SyncOverlayTheme(w, h);
        _progress.Draw(_dl, _theme, now); // OSC 9;4 along the panes' top edges (Windowing/WindowProgress.cs)
        _agents.DrawBadges(_dl, now); // pane pills, waiting rims (Agents/WindowAgents.cs)
        _broadcast.Draw(_dl, _theme, _layout, now); // the panes receiving broadcast input (Windowing/WindowBroadcast.cs)
        _ime.Draw(_dl, _theme, _focused, now); // text being composed in an input method (Windowing/ImeComposer.cs)
        _teammates.DrawStrips(_dl, now); // agent-team teammates below their lead (Teammates/WindowTeammates.cs)
        _exitHold.Draw(_dl, _theme, VisiblePanes(), now); // held panes: "make exited with code 2 [Restart] [Close]" (Windowing/ExitHoldFlow.cs)
        DrawZoomBadge(window, now);
        DrawAgentResume(window, now); // "Resume N agents" (Agents/WindowAgentResume.cs)
        DrawIntegrationOverlays(now); // repo pills, drop target (WindowFeatures/WindowIntegrations.cs, WindowDrop.cs)
        _chrome.Draw(_dl, now); // Chrome/WindowChrome.cs
        DrawInfoBars(now); // question + notification bars
        DrawShellOverlays(now); // quick select, link hint (WindowFeatures/WindowLinks.cs)
        _vim.DrawOverlays(_dl, window, now); // mode pills, prompts, cheat sheet (Vim/WindowVim.cs)
        _paneSelect.Draw(_dl, _overlayTheme, now);
        _agents.DrawOverlay(_dl, _overlayTheme, window, now);
        _notifications.DrawPanel(_dl, _overlayTheme, window, now);
        _paneSearch.Draw(_dl, _overlayTheme, window, now); // Search/WindowPaneSearch.cs
        _worktrees.DrawOverlays(_dl, window, now); // launcher, switcher, finish, cleanup (Worktrees/WindowWorktrees.cs)
        _tabSwitcher.Draw(_dl, _overlayTheme, window, now); // Ctrl+Tab and the bar's "⌄ N more" (Tabs/WindowTabSwitcher.cs)
        _palette.Draw(_dl, _overlayTheme, window, now);
        _about.Draw(_dl, _overlayTheme, window, now);
        _menu.Draw(_dl, _overlayTheme, window, now);
        _toasts.Draw(_dl, _overlayTheme, window, now, _vim.OverlayOccupied); // above vim mode's pills and prompts
    }

    /// <summary>
    /// The pop-ups' theme follows this window's font zoom (Ctrl+=), as far as the window has room (OverlayZoom). The bars and
    /// pills that take layout room or sit in the chrome (tab bar, info bars, teammate strips, repo and agent pills)
    /// keep <see cref="_theme"/>: growing them would take rows from the terminal the zoom was meant to enlarge.
    /// </summary>
    private void SyncOverlayTheme(int windowW, int windowH)
    {
        float zoom = OverlayZoom.For(FontZoom, windowW, windowH, _theme.UiPx, _theme.Scale);
#pragma warning disable S1244 // Cache key: _overlayZoom is exactly the value OverlayZoom.For returned last time.
        if (zoom == _overlayZoom && ReferenceEquals(_overlayBase, _theme)) return;
#pragma warning restore S1244
        _overlayZoom = zoom;
        _overlayBase = _theme;
#pragma warning disable S1244 // OverlayZoom.For returns exactly 1 when no zoom applies; a value near 1 still means a real zoom.
        _overlayTheme = zoom == 1 ? _theme : _theme.WithOverlayZoom(zoom);
#pragma warning restore S1244
    }

    /// <summary>Pop-ups anchored in a pane (search, quick select, link hint, vim): zoomed when the pane is wide enough to hold them.</summary>
    private Theme PaneOverlayTheme(Pane? pane, long now) =>
        pane is not null && pane.Rect.Value(now).W >= _overlayTheme.S(240) ? _overlayTheme : _theme;

    private void DrawPanes(long now)
    {
        _tabCtl.DrawOutgoing(_dl, _theme, _windowFocused, now); // Windowing/TabBarController.cs
        foreach (var pane in VisiblePanes())
        {
            RectF r = pane.Rect.Value(now);
            if (pane.BottomPx > 0) r = r with { H = MathF.Max(0, r.H - pane.BottomPx) }; // the teammate strip, a held pane's exit bar
            _paneRenderer.Draw(_dl, pane, r, _theme, pane == _focused, _windowFocused, now);
        }
        if (_zoomed is not null) return;

        // Dividers: a hairline centred in the gap; the hovered/dragged one glows in the accent color.
        float glow = _dividerGlow.Value(now);
        float line = MathF.Max(1, MathF.Round(_scale));
        foreach (var d in _dividers)
        {
            var r = DisplayedDividerRect(d, now);
            bool hot = d.Split == (_dragDivider ?? _hoverDivider);
            bool horizontal = d.Split.Axis == SplitAxis.Horizontal;
            float thick = hot ? line + MathF.Round(_scale * 2 * glow) : line;
            uint color = hot ? Colors.Mix(_theme.Split, _theme.Accent, glow) : _theme.Split;
            RectF bar = horizontal
                ? new RectF(MathF.Round(r.X + (r.W - thick) / 2), r.Y + _scale * 4, thick, r.H - _scale * 8)
                : new RectF(r.X + _scale * 4, MathF.Round(r.Y + (r.H - thick) / 2), r.W - _scale * 8, thick);
            _dl.RoundRect(bar, thick / 2, color, 1);
        }
    }

    /// <summary>Divider position follows the animated panes on either side of it.</summary>
    private RectF DisplayedDividerRect(LayoutTree<Pane>.Divider d, long now)
    {
        // Find the pane rect touching the divider on its first side and use its animated edge.
        RectF target = d.Rect;
        foreach (var (pane, rect) in _paneRects)
        {
            var anim = pane.Rect.Value(now);
            if (d.Split.Axis == SplitAxis.Horizontal && rect.Right == d.Rect.X && rect.Y >= d.Bounds.Y && rect.Bottom <= d.Bounds.Bottom)
                return target with { X = anim.Right };
            if (d.Split.Axis == SplitAxis.Vertical && rect.Bottom == d.Rect.Y && rect.X >= d.Bounds.X && rect.Right <= d.Bounds.Right)
                return target with { Y = anim.Bottom };
        }
        return target;
    }

    private void DrawZoomBadge(RectF window, long now)
    {
        float v = _zoomBadge.Value(now);
        if (v <= 0.001f) return;
        float px = _theme.UiPx * 0.85f;
        string text = "Zoomed";
        float w = MathF.Round(_dl.Measure(text, px) + px * 2.2f + _theme.S(20));
        float h = MathF.Round(px * 2.1f);
        var r = new RectF(window.Right - w - _theme.S(14), _theme.S(10) + _tabCtl.Reserve() + _notifications.ReserveTop - (1 - v) * _theme.S(8), w, h);
        _dl.Shadow(r.Offset(0, _theme.S(4)), h / 2, _theme.S(10), 0.35f * v);
        _dl.Panel(r, h / 2, _theme.PanelBg, 0.92f * v, Colors.Mix(_theme.PanelBg, _theme.Accent, 0.5f), v);
        float b = Widgets.Baseline(_dl, px, r.Y, h);
        float x = r.X + _theme.S(10);
        x += _dl.Glyph(Icons.ZoomIn, x, b, px, _theme.Accent, v) + px * 0.7f;
        _dl.Text(text, x, b, px, _theme.PanelFg, v);
    }

    /// <summary>A program is in a synchronized update: the loop polls until it ends (or 150 ms pass).</summary>
    internal bool WaitsForSynchronizedOutput
    {
        get
        {
            foreach (var tab in _tabs.Items)
                foreach (var pane in tab.Layout.LeafList)
                    if (pane.Term.HoldsSynchronizedOutput) return true;
            return false;
        }
    }

    /// <summary>Hook from App.RenderWindows: the ERIKTERM_SCREENSHOT frame is due.</summary>
    internal void CheckScreenshot(long now)
    {
        if (_screenshotPath is not null && now >= _screenshotAt) _redraw = true;
    }

    /// <summary>Debug script shot:PATH — save the next frame.</summary>
    internal void ScheduleScreenshot(string path)
    {
        _screenshotPath = path;
        _screenshotAt = 0;
        _redraw = true;
    }

    internal void RequestRedraw() => _redraw = true;

    /// <summary>
    /// Refits the panes to the window as it is now. Normally EVENT_WINDOW_PIXEL_SIZE_CHANGED does this, but while a
    /// window edge is being dragged on Windows that event is still sitting in the queue behind a blocked event loop,
    /// so the resize watch calls this before drawing (App.LiveResize.cs).
    /// </summary>
    internal void ResizeToWindow() => ApplyLayout(animate: false);

    /// <summary>Whether (x, y) in window pixels is inside the window.</summary>
    internal bool ContainsPixel(float x, float y)
    {
        var (w, h) = WindowPixels();
        return x >= 0 && y >= 0 && x < w && y < h;
    }

    /// <summary>Hands the parser output whose synchronized update never ended (Terminal.Sync.cs).</summary>
    internal void ReleaseExpiredSynchronizedOutput(long now)
    {
        lock (_lock)
            foreach (var tab in _tabs.Items)
                foreach (var pane in tab.Layout.LeafList)
                    pane.Term.ReleaseExpiredSynchronizedOutput(now);
    }

    private long _lastUnpacedFrameAt;

    /// <summary>Whether a window that doesn't wait for vsync may draw now; starts the next interval when it may.</summary>
    internal bool UnpacedFrameDue(long now, int intervalMs)
    {
        if (now - _lastUnpacedFrameAt < intervalMs) return false;
        _lastUnpacedFrameAt = now;
        return true;
    }

    private (int W, int H) WindowPixels()
    {
        int w, h;
        Sdl.GetWindowSizeInPixels(_window, &w, &h);
        return (w, h);
    }

    // ==== Events ===============================================================================

    /// <summary>An event SDL addressed to this window (App.Dispatch). False: the window should close.</summary>
    internal bool Handle(ref Sdl.Event ev)
    {
        if (ev.Type is not (Sdl.EVENT_TEXT_INPUT or Sdl.EVENT_MOUSE_MOTION))
            _keyInput.FlushPendingKey(); // input stays in order behind a key held for its text (Windowing/KeyInputRouter.cs)
        long now = Now;
        switch (ev.Type)
        {
            case Sdl.EVENT_WINDOW_CLOSE_REQUESTED:
                return !_closeConfirm.RequestCloseWindow(); // asks first when something runs (Windowing/CloseConfirmFlow.cs)

            case Sdl.EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                ApplyLayout(animate: false);
                break;

            case Sdl.EVENT_WINDOW_DISPLAY_SCALE_CHANGED:
                (_scale, _density) = (UiScale(), WindowDensity());
                RebuildTheme();
                ApplyFontSize();
                break;

            case Sdl.EVENT_WINDOW_FOCUS_GAINED:
            case Sdl.EVENT_WINDOW_FOCUS_LOST:
                OnWindowFocusChanged(ev.Type == Sdl.EVENT_WINDOW_FOCUS_GAINED);
                break;

            case Sdl.EVENT_WINDOW_MOUSE_LEAVE:
                SetHoverDivider(null, now);
                _tabBar.HandleMouseLeave();
                _floating.MouseLeave(now); // the caption buttons' hover; the chrome goes back in (Chrome/FloatingChrome.cs)
                _links.Hover(false, -1, -1, now); // WindowFeatures/WindowLinks.cs
                break;

            case >= Sdl.EVENT_WINDOW_FIRST and <= Sdl.EVENT_WINDOW_LAST:
                _redraw = true;
                break;

            case >= Sdl.EVENT_DROP_FILE and <= Sdl.EVENT_DROP_POSITION:
                _drop.OnDropEvent(ref ev, _density, now); // drag and drop: paste paths (WindowFeatures/WindowDrop.cs)
                break;

            case Sdl.EVENT_KEY_DOWN:
                OnKeyDown(ev.Key, KeyMod(ev.KeyMod), now, ev.KeyRepeat != 0, ev.KeyScancode);
                break;

            case Sdl.EVENT_KEY_UP:
                _keyInput.SendKeyRelease(ev.Key, ev.KeyScancode, KeyMod(ev.KeyMod)); // Windowing/KeyInputRouter.cs
                _tabSwitcher.ModifierUp(KeyMod(ev.KeyMod)); // Ctrl released: switch (Tabs/WindowTabSwitcher.cs)
                _tabSwitcher.DoubleCtrlKeyUp(ev.Key, now); // Ctrl, Ctrl: the tab list
                _links.OnModifiers(now); // Ctrl released: disarm the hovered link (WindowFeatures/WindowLinks.cs)
                return true;

            case Sdl.EVENT_TEXT_INPUT:
                OnTextInput(Marshal.PtrToStringUTF8((nint)ev.Text) ?? "", now);
                break;

            case Sdl.EVENT_TEXT_EDITING:
                OnTextEditing(Marshal.PtrToStringUTF8((nint)ev.Text) ?? "");
                break;

            case Sdl.EVENT_MOUSE_MOTION:
                // Moving over plain text changes nothing on screen: OnMouseMove asks for a frame when it does.
                OnMouseMove(ev.MouseX * _density, ev.MouseY * _density, now);
                return true;

            case Sdl.EVENT_MOUSE_BUTTON_DOWN:
                _tabSwitcher.ResetDoubleTap();
                OnMouseDown(ev.MouseButton, ev.MouseClicks, ev.MouseX * _density, ev.MouseY * _density, now);
                break;

            case Sdl.EVENT_MOUSE_BUTTON_UP:
                OnMouseUp(ev.MouseButton, ev.MouseX * _density, ev.MouseY * _density, now);
                break;

            case Sdl.EVENT_MOUSE_WHEEL:
                OnWheel(ev.WheelIntegerY != 0 ? ev.WheelIntegerY : Math.Sign(ev.WheelY), ev.WheelY, ev.WheelMouseX * _density, ev.WheelMouseY * _density, now);
                break;
        }
        _redraw = true;
        return true;
    }

    private void OnWindowFocusChanged(bool focused)
    {
        _windowFocused = focused;
        if (!_windowFocused) OnTextEditing(""); // a composition doesn't survive losing the keyboard
        if (_windowFocused) _app.OnWindowFocused(this); // most recently used window (App.Process.cs)
        else if (_tabSwitcher.IsOpen) _tabSwitcher.Close(); // the modifier may be released elsewhere, unseen
        _tabSwitcher.ResetDoubleTap();
        _backdrop.UpdateForFocus(focused); // acrylic turns gray in an inactive window (Chrome/WindowBackdrop.cs)
        if (_focused.Term.FocusReporting) WriteInput(_focused, _windowFocused ? "\e[I"u8 : "\e[O"u8);
        _redraw = true;
    }

    // ---- Keyboard -----------------------------------------------------------------------------

    /// <summary>A key event's modifiers; on macOS with Option mapped to Alt as macos_option_as_alt says (MacKeys.cs).</summary>
    private ushort KeyMod(ushort mod) => OperatingSystem.IsMacOS() ? MacKeys.MapOption(mod, _settings.MacosOptionAsAlt) : mod;

    private void OnKeyDown(uint key, ushort mod, long now, bool repeat = false, uint scancode = 0)
    {
        Pane.QuietCollection.NoteInput(Environment.TickCount64); // no full GC while you type (Pane.cs)
        if (_ime.Composing) return; // the input method's: Enter commits, Escape cancels, nothing reaches the program
        SuppressText = false;
        _keyInput.FlushPendingKey(); // Windowing/KeyInputRouter.cs

        _tabSwitcher.NoteKeyForDoubleCtrl(key, mod, repeat, now); // Tabs/WindowTabSwitcher.cs
        _links.OnModifiers(now); // Ctrl pressed: arm the hovered link (WindowFeatures/WindowLinks.cs)
        if (OverlayKeyDown(key, mod, now)) return;

        if (KeyNames.Chord(key, mod) is { } chord && _keys.TryGetValue(chord, out var action))
        {
            _actions.Execute(action, key, mod);
            SuppressText = true;
            return;
        }

        // The same keypress may also produce a text-input event (or, with kitty flag 16, it is waiting for it).
        if (_keyInput.SendKey(key, scancode, mod, repeat ? KeyEventKind.Repeat : KeyEventKind.Press)) SuppressText = true; // Windowing/KeyInputRouter.cs
    }

    /// <summary>The overlays and bars, in stacking order; true when one of them took the key.</summary>
    private bool OverlayKeyDown(uint key, ushort mod, long now)
    {
        if (_closeConfirm.PromptKey(key)) { SuppressText = true; return true; } // "Close this pane?" is up (Windowing/CloseConfirmFlow.cs)
        if (_tabSwitcher.KeyDown(key, mod, now)) return true; // the Ctrl+Tab list takes everything (Tabs/WindowTabSwitcher.cs)
        if (_worktrees.KeyDown(key, mod, now) || _paneSearch.KeyDown(key, mod, now)) return true; // Worktrees/WindowWorktrees.cs, Search/WindowPaneSearch.cs
        if (_quickSelect.IsOpen) { _links.QuickSelectKey(key, mod, now); SuppressText = true; return true; }
        if (_paneSelect.IsOpen) { _paneSelect.HandleKey(key, now); SuppressText = true; return true; }
        if (_agents.OverlayKey(key, mod, now)) return true; // Agents/WindowAgents.cs
        if (_notifications.IsPanelOpen) { _notifications.PanelKey(key, mod, now); SuppressText = true; return true; }
        if (_about.IsOpen) { _about.HandleKey(key, mod, now); SuppressText = true; return true; }
        if (_palette.IsOpen) { _palette.HandleKey(key, mod, now); return true; }
        if (_menu.IsOpen) { _menu.HandleKey(key, now); SuppressText = true; return true; }
        if (_vim.KeyDown(key, mod, now)) return true; // vim mode and its cheat sheet (Vim/WindowVim.cs)
        if (_chrome.KeyDown(key, mod, now)) return true; // tab rename, search bar (Chrome/WindowChrome.cs)
        if (_exitHold.KeyDown(key, mod)) { SuppressText = true; return true; } // a held pane: Enter restarts, Esc closes (Windowing/ExitHoldFlow.cs)
        return false;
    }

    private void OnTextInput(string text, long now)
    {
        if (_keyInput.TakePendingKeyText(text)) { SuppressText = false; return; } // Windowing/KeyInputRouter.cs
        if (SuppressText) { SuppressText = false; return; }
        if (text.Length == 0) return;
        if (_tabSwitcher.IsOpen) { _tabSwitcher.HandleText(text, now); _redraw = true; return; } // Tabs/WindowTabSwitcher.cs
        if (_worktrees.TextInput(text, now) || _paneSearch.TextInput(text, now)) return; // Worktrees/WindowWorktrees.cs, Search/WindowPaneSearch.cs
        if (_palette.IsOpen) { _palette.HandleText(text, now); return; }
        if (_paneSelect.IsOpen || _menu.IsOpen || _quickSelect.IsOpen || _about.IsOpen || _agents.OverlayOpen || _notifications.IsPanelOpen) return;
        if (_vim.TextInput(text)) return; // Vim/WindowVim.cs
        if (_chrome.TextInput(text, now)) return;
        _accessibility?.Typed(text, now); // its echo isn't announced again
        SendInput(_focused, text);
        _broadcast.Text(text); // Windowing/WindowBroadcast.cs
    }

    /// <summary>The text an input method is composing (drawn at the cursor: Windowing/ImeComposer.cs); "" when it ends.</summary>
    private void OnTextEditing(string text)
    {
        if (_ime.SetText(text)) _redraw = true;
    }

    private void SendInput(Pane pane, string text)
    {
        lock (_lock)
        {
            pane.Term.ResetView();
            pane.Selection = null;
        }
        pane.Scroll.Snap(); // typing returns to the live screen at once
        pane.Cursor.Touch(Now);
        pane.Agent.LastInputAt = Now; // its echo isn't agent work (Agents/WindowAgents.cs)
        WriteInput(pane, Encoding.UTF8.GetBytes(text));
    }

    private long _inputDroppedToastAt = long.MinValue / 2; // / 2: "now - it" mustn't overflow

    /// <summary>
    /// Queues input for the pane's program (never blocks: Pty.Write hands it to a writer thread). When the program hasn't
    /// read the last 16 MB the input is dropped whole, and the user hears about it (once every few seconds).
    /// </summary>
    private void WriteInput(Pane pane, ReadOnlySpan<byte> data)
    {
        if (pane.Pty.Write(data) || pane.Exited) return;
        long now = Now;
        if (now - _inputDroppedToastAt < 5000) return;
        _inputDroppedToastAt = now;
        Toast(ToastKind.Error, "Input dropped", $"{pane.Label} isn't reading its input ({pane.Pty.Input.Pending / (1024 * 1024)} MB waiting)");
    }

    /// <summary>Sends the key that triggered an action to the program instead (wezterm-style pass-through).</summary>
    private void PassThrough(uint key, ushort mod)
    {
        bool appCursor;
        KittyKeyboardModes kitty;
        lock (_lock) (appCursor, kitty) = (_focused.Term.CursorKeysApplication, _focused.Term.KeyboardFlags);
        if (KeyEncoder.Encode(key, mod, appCursor, kitty) is { } seq) SendInput(_focused, seq);
        _broadcast.Key(key, 0, mod, KeyEventKind.Press);
    }

    // ---- Mouse --------------------------------------------------------------------------------

    private Pane? PaneAt(float x, float y)
    {
        return VisiblePanes().FirstOrDefault(p => p.Target.Contains(x, y));
    }

    private LayoutTree<Pane>.Node? DividerAt(float x, float y)
    {
        if (_zoomed is not null) return null;
        float slop = 3 * _scale, line = MathF.Max(1, MathF.Round(_scale));
        foreach (var d in _dividers)
        {
            if (!GapHitTest.NearDivider(d.Rect, slop, x, y)) continue;
            // Past the hairline, the gap is the right pane's prompt gutter where that is clickable (WindowFeatures/WindowLinks.cs).
            bool gutter = d.Split.Axis == SplitAxis.Horizontal && _links.GutterClickable(d.Rect.Right - 0.5f, y);
            if (GapHitTest.HitsDivider(d.Rect, d.Split.Axis, line, slop, x, y, gutter)) return d.Split;
        }
        return null;
    }

    private void SetHoverDivider(LayoutTree<Pane>.Node? node, long now)
    {
        if (node == _hoverDivider) return;
        _hoverDivider = node;
        if (_dragDivider is null) _dividerGlow.Set(node is null ? 0 : 1, now, Anim);
    }

    private void SetCursor(nint cursor) => _app.SetCursor(cursor); // one current cursor per process (App.Process.cs)

    private List<ColorScheme> AllSchemes() => _app.AllSchemes(); // App.ConfigExtras.cs

    private (int Col, int Row, int Boundary) CellAt(Pane pane, float x, float y)
    {
        var r = pane.Target;
        float fx = (x - r.X) / _fonts.CellWidth, fy = (y - r.Y) / _fonts.CellHeight;
        int col = Math.Clamp((int)MathF.Floor(fx), 0, pane.Term.Columns - 1);
        int row = Math.Clamp((int)MathF.Floor(fy), 0, pane.Term.Rows - 1);
        int boundary = Math.Clamp((int)MathF.Round(fx), 0, pane.Term.Columns);
        return (col, row, boundary);
    }

    private static MouseModifiers MouseMods()
    {
        ushort mod = Sdl.GetModState();
        var m = MouseModifiers.None;
        if ((mod & Sdl.KMOD_SHIFT) != 0) m |= MouseModifiers.Shift;
        if ((mod & Sdl.KMOD_LALT) != 0) m |= MouseModifiers.Alt;
        if ((mod & Sdl.KMOD_CTRL) != 0) m |= MouseModifiers.Ctrl;
        return m;
    }

    /// <summary>Holding Shift bypasses mouse reporting so you can always select text.</summary>
    private static bool _shiftHeld => (Sdl.GetModState() & Sdl.KMOD_SHIFT) != 0;

    private static bool WantsMouse(Pane pane, bool shiftHeld) =>
        pane.Term.MouseTracking != MouseTracking.None && !shiftHeld;


    private void ReportMouse(Pane pane, MouseEventKind kind, MouseButton button, float x, float y)
    {
        var (col, row, _) = CellAt(pane, x, y);
        string? seq;
        lock (_lock)
        {
            if (!MouseEncoder.Wants(pane.Term, kind, _buttonsDown != 0)) return;
            seq = MouseEncoder.Encode(pane.Term, kind, button, col, row, MouseMods());
        }
        if (seq is not null) WriteInput(pane, Encoding.ASCII.GetBytes(seq));
    }

    private void OnMouseMove(float x, float y, long now)
    {
        if (OverlayMouseMove(x, y, now)) return;
        if (BarsAndPillsMouseMove(x, y, now)) return;
        if (DragMouseMove(x, y, now)) return;
        HoverMouseMove(x, y, now);
    }

    private bool OverlayMouseMove(float x, float y, long now)
    {
        if (_tabSwitcher.IsOpen) { _tabSwitcher.HandleMouseMove(x, y, now); SetCursor(_cursorDefault); _redraw = true; return true; } // Tabs/WindowTabSwitcher.cs
        if (PopupMouseMove(x, y, now)) return true; // Worktrees/WindowWorktrees.cs, Search/WindowPaneSearch.cs
        if (_palette.IsOpen) { _palette.HandleMouseMove(x, y, now); SetCursor(_cursorDefault); _redraw = true; return true; }
        if (_menu.IsOpen) { _menu.HandleMouseMove(x, y, now); SetCursor(_cursorDefault); _redraw = true; return true; }
        if (_about.IsOpen) { SetCursor(_about.HandleMouseMove(x, y, now) ? _cursorPointer : _cursorDefault); _redraw = true; return true; }
        if (_paneSelect.IsOpen) { SetCursor(_cursorPointer); return true; }
        if (_agents.OverlayOpen) { _agents.OverlayMouseMove(x, y, now); SetCursor(_cursorDefault); _redraw = true; return true; }
        if (_notifications.IsPanelOpen) { _notifications.PanelMouseMove(x, y, now); SetCursor(_cursorDefault); _redraw = true; return true; }
        if (_quickSelect.IsOpen) { SetCursor(_quickSelect.HitTest(x, y) >= 0 ? _cursorPointer : _cursorDefault); _redraw = true; return true; }
        return false;
    }

    private bool BarsAndPillsMouseMove(float x, float y, long now)
    {
        if (_vim.MouseDrag(x, y)) return true; // a drag in vim mode selects (Vim/WindowVim.cs)
        if (BarsMouseMove(x, y)) return true; // the info and notification bars, then the agent-resume pill
        if (_exitHold.MouseMove(x, y)) return true; // Windowing/ExitHoldFlow.cs
        if (_chrome.MouseMove(x, y, now)) return true; // Chrome/WindowChrome.cs
        if (_broadcast.MouseMove(x, y)) return true; // the BROADCAST pills (Windowing/WindowBroadcast.cs)
        if (_scrollbar.MouseDrag(y, now)) return true; // dragging a thumb (Panes/WindowScrollbar.cs)
        return _dragDivider is null && _mouseReportPane is null && _selecting is not { Selection.Dragging: true } && _teammates.MouseMove(x, y, now); // Teammates/WindowTeammates.cs
    }

    private bool DragMouseMove(float x, float y, long now)
    {
        if (_dragDivider is not null)
        {
            MoveDraggedDivider(_dragDivider, x, y);
            return true;
        }

        if (_mouseReportPane is not null)
        {
            ReportMouse(_mouseReportPane, MouseEventKind.Motion, MouseButton.Left, x, y);
            return true;
        }

        if (_selecting is { Selection: { Dragging: true } sel } pane)
        {
            ExtendSelection(pane, sel, x, y, now);
            return true;
        }
        return false;
    }

    private void MoveDraggedDivider(LayoutTree<Pane>.Node divider, float x, float y)
    {
        var bounds = _layout.BoundsOf(divider, ContentBounds(), Gap);
        bool horizontal = divider.Axis == SplitAxis.Horizontal;
        int size = (horizontal ? bounds.W : bounds.H) - Gap;
        int offset = (int)((horizontal ? x - bounds.X : y - bounds.Y) - Gap / 2f);
        LayoutTree<Pane>.SetDivider(divider, offset, size, 4 * (horizontal ? _fonts.CellWidth : _fonts.CellHeight));
        ApplyLayout(animate: false);
    }

    private void ExtendSelection(Pane pane, Selection sel, float x, float y, long now)
    {
        _redraw = true;
        var r = pane.Target;
        lock (_lock)
        {
            // Dragging past the edge scrolls the history.
            if (y < r.Y) { pane.Term.ScrollView(1); pane.ScrollActivityAt = now; }
            else if (y >= r.Bottom) { pane.Term.ScrollView(-1); pane.ScrollActivityAt = now; }
            var (col, row, boundary) = CellAt(pane, x, y);
            sel.HeadLine = pane.Term.ViewTopLine + row;
            sel.HeadCol = sel.Mode == SelectionMode.Cell ? boundary : col;
        }
    }

    private void HoverMouseMove(float x, float y, long now)
    {
        var divider = DividerAt(x, y);
        SetHoverDivider(divider, now);
        if (divider is null && _scrollbar.Hover(x, y, now)) { _links.Hover(false, x, y, now); return; } // reveals the scrollbar (Panes/WindowScrollbar.cs)
        if (_links.Hover(divider is null, x, y, now)) return; // armed link / prompt gutter set the cursor (WindowFeatures/WindowLinks.cs)
        if (divider is not null) SetCursor(divider.Axis == SplitAxis.Horizontal ? _cursorEw : _cursorNs);
        else if (PaneAt(x, y) is { } over)
        {
            SetCursor(WantsMouse(over, _shiftHeld) ? _cursorDefault : _cursorText);
            if (over.Term.MouseTracking == MouseTracking.AnyMotion) ReportMouse(over, MouseEventKind.Motion, MouseButton.None, x, y);
        }
        else SetCursor(_cursorDefault);
    }

    private void OnMouseDown(byte button, byte clicks, float x, float y, long now)
    {
        _buttonsDown |= 1 << button;
        if (OverlayMouseDown(x, y, now)) return;
        if (BarsAndPillsMouseDown(button, clicks, x, y, now)) return;

        var pane = PaneAt(x, y);
        if (pane is null) return;
        Focus(pane);
        var mb = button switch { Sdl.BUTTON_LEFT => MouseButton.Left, Sdl.BUTTON_MIDDLE => MouseButton.Middle, _ => MouseButton.Right };

        if (button == Sdl.BUTTON_LEFT && _links.TryOpenLinkAt(pane, x, y)) return; // Ctrl+click (WindowFeatures/WindowLinks.cs)
        if (button == Sdl.BUTTON_LEFT && _vim.MouseDown(pane, clicks, x, y)) return; // moves vim's cursor (Vim/WindowVim.cs)
        if (WantsMouse(pane, _shiftHeld))
        {
            _mouseReportPane = pane;
            ReportMouse(pane, MouseEventKind.Press, mb, x, y);
            return;
        }

        switch (button)
        {
            case Sdl.BUTTON_LEFT:
                StartSelection(pane, clicks, x, y);
                break;
            case Sdl.BUTTON_MIDDLE:
                PasteText(pane, Sdl.GetPrimarySelectionText(), broadcast: true);
                break;
            case Sdl.BUTTON_RIGHT:
                _commands.OpenContextMenu(x, y, now);
                break;
        }
    }

    private bool OverlayMouseDown(float x, float y, long now)
    {
        if (_toasts.HandleMouseDown(x, y, _theme, now)) return true; // toasts with an action (CI passed → Open)
        if (_tabSwitcher.MouseDown(x, y, now)) return true; // Tabs/WindowTabSwitcher.cs
        if (PopupMouseDown(x, y, now)) return true; // Worktrees/WindowWorktrees.cs, Search/WindowPaneSearch.cs
        if (_paneSelect.IsOpen) { _paneSelect.HandleMouseDown(x, y, now); return true; }
        if (_agents.OverlayOpen) { _agents.OverlayMouseDown(x, y, now); return true; }
        if (_notifications.IsPanelOpen) { _notifications.PanelMouseDown(x, y, now); return true; }
        if (_quickSelect.IsOpen) { _links.QuickSelectMouseDown(x, y, now); return true; }
        if (_palette.IsOpen) { _palette.HandleMouseDown(x, y, now); return true; }
        if (_about.IsOpen) { _about.HandleMouseDown(x, y, now); return true; }
        if (_vim.HelpMouseDown(x, y, now)) return true; // Vim/WindowVim.cs
        return _menu.IsOpen && _menu.HandleMouseDown(x, y, now);
    }

    private bool BarsAndPillsMouseDown(byte button, byte clicks, float x, float y, long now)
    {
        bool left = button == Sdl.BUTTON_LEFT;
        if (left && (_notifications.BarsMouseDown(x, y) || _agentResume.MouseDown(x, y))) return true; // the info and notification bars, then the pill
        if (left && _exitHold.MouseDown(x, y)) return true; // Windowing/ExitHoldFlow.cs
        if (_chrome.MouseDown(button, clicks, x, y, now)) return true; // Chrome/WindowChrome.cs
        if (_teammates.MouseDown(button, x, y)) return true; // pills: open / park a teammate (Teammates/WindowTeammates.cs)
        if (left && _broadcast.MouseDown(x, y)) return true; // the BROADCAST pill's ✕ (Windowing/WindowBroadcast.cs)

        if (left && DividerAt(x, y) is { } divider)
        {
            _dragDivider = divider;
            _dividerGlow.Set(1, now, Anim);
            return true;
        }
        if (left && _scrollbar.MouseDown(x, y, now)) return true; // a tick, or the thumb (Panes/WindowScrollbar.cs)
        return left && _links.TryGutterClick(x, y); // select a command's output (WindowFeatures/WindowLinks.cs)
    }

    private void StartSelection(Pane pane, byte clicks, float x, float y)
    {
        lock (_lock)
        {
            var (col, row, boundary) = CellAt(pane, x, y);
            long line = pane.Term.ViewTopLine + row;
            var mode = clicks switch
            {
                >= 3 => SelectionMode.Line,
                2 => SelectionMode.Word,
                _ => SelectionMode.Cell,
            };
            int c = mode == SelectionMode.Cell ? boundary : col;
            foreach (var other in _layout.LeafList) other.Selection = null;
            pane.Selection = new Selection { AnchorLine = line, AnchorCol = c, HeadLine = line, HeadCol = c, Mode = mode, Dragging = true };
        }
        _selecting = pane;
    }

    private void OnMouseUp(byte button, float x, float y, long now)
    {
        _buttonsDown &= ~(1 << button);
        if (_menu.IsOpen) { _menu.HandleMouseUp(x, y, now); return; }
        if (_about.IsOpen) { _about.HandleMouseUp(x, y, now); return; }
        if (_chrome.MouseUp(x, y, now)) return; // Chrome/WindowChrome.cs
        if (_scrollbar.EndDrag()) return; // Panes/WindowScrollbar.cs

        if (_dragDivider is not null)
        {
            _dragDivider = null;
            _dividerGlow.Set(DividerAt(x, y) is null ? 0 : 1, now, Anim);
            return;
        }

        if (button == Sdl.BUTTON_LEFT && _vim.MouseUp()) return; // Vim/WindowVim.cs

        if (_mouseReportPane is { } reporting)
        {
            ReportMouseRelease(reporting, button, x, y);
            return;
        }

        if (button == Sdl.BUTTON_LEFT && _selecting is { } pane) FinishSelection(pane);
    }

    private void ReportMouseRelease(Pane pane, byte button, float x, float y)
    {
        var mb = button switch { Sdl.BUTTON_LEFT => MouseButton.Left, Sdl.BUTTON_MIDDLE => MouseButton.Middle, _ => MouseButton.Right };
        ReportMouse(pane, MouseEventKind.Release, mb, x, y);
        if (_buttonsDown == 0) _mouseReportPane = null;
    }

    private void FinishSelection(Pane pane)
    {
        _selecting = null;
        string text = "";
        lock (_lock)
        {
            if (pane.Selection is { } sel)
            {
                sel.Dragging = false;
                if (sel.Range(pane.Term) is null) pane.Selection = null;
                else text = sel.Text(pane.Term);
            }
        }
        if (text.Length > 0) Sdl.SetPrimarySelectionText(text); // X11/Wayland convention: select = copy to primary
    }

    private void OnWheel(int lines, float precise, float x, float y, long now)
    {
        if (lines == 0 && precise == 0) return;
        if (OverlayWheel(lines, precise, x, y, now)) return;

        var pane = PaneAt(x, y) ?? _focused;
        bool tracking, alt, altScroll, appCursor;
        lock (_lock)
        {
            tracking = pane.Term.MouseTracking != MouseTracking.None;
            alt = pane.Term.IsAlternateScreen;
            altScroll = pane.Term.AlternateScroll;
            appCursor = pane.Term.CursorKeysApplication;
        }

        if (lines == 0 && (tracking && !_shiftHeld || alt && altScroll)) return;
        if (tracking && !_shiftHeld) ReportWheel(pane, lines, x, y);
        else if (alt && altScroll) SendWheelArrows(pane, lines, appCursor);
        else
        {
            _scrollbar.ScrollHistory(pane, lines, precise, now); // smooth / pixel scrolling (Panes/WindowScrollbar.cs)
            pane.ScrollActivityAt = now;
        }
    }

    private bool OverlayWheel(int lines, float precise, float x, float y, long now)
    {
        if (_tabSwitcher.IsOpen) { _tabSwitcher.HandleWheel(lines, now); return true; } // Tabs/WindowTabSwitcher.cs
        if (PopupWheel(lines)) return true; // Worktrees/WindowWorktrees.cs, Search/WindowPaneSearch.cs
        if (_palette.IsOpen) { _palette.HandleWheel(lines, now); return true; }
        if (_about.IsOpen) { _about.HandleWheel(lines, now); return true; }
        if (_vim.HelpWheel(lines, now)) return true; // Vim/WindowVim.cs
        if (_menu.IsOpen || _agents.OverlayOpen || _notifications.IsPanelOpen) return true;
        // Quick select labels and the pane picker sit on the current view: scrolling under them would misplace them.
        if (_quickSelect.IsOpen || _paneSelect.IsOpen) return true;
        if (_tabBar.HandleWheel(lines, precise, x, y)) { _redraw = true; return true; } // an overflowing tab bar scrolls (Ui/TabBar.cs)
        return false;
    }

    private void ReportWheel(Pane pane, int lines, float x, float y)
    {
        for (int i = 0; i < Math.Abs(lines); i++)
            ReportMouse(pane, MouseEventKind.Press, lines > 0 ? MouseButton.WheelUp : MouseButton.WheelDown, x, y);
    }

    private void SendWheelArrows(Pane pane, int lines, bool appCursor)
    {
        string arrow;
        if (appCursor) arrow = lines > 0 ? "\eOA" : "\eOB";
        else arrow = lines > 0 ? "\e[A" : "\e[B";
        WriteInput(pane, Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(arrow, Math.Abs(lines) * 3))));
    }

    // ==== Clipboard ============================================================================

    private void Copy(bool toClipboard, bool toPrimary)
    {
        string text;
        lock (_lock) text = _focused.Selection?.Text(_focused.Term) ?? "";
        if (text.Length == 0) return;
        if (toClipboard) Sdl.SetClipboardText(text);
        if (toPrimary) Sdl.SetPrimarySelectionText(text);
        int chars = text.EnumerateRunes().Count();
        Toast(ToastKind.Success, "Copied", $"{chars} character{(chars == 1 ? "" : "s")}");
    }

    /// <summary>
    /// Every paste goes through here: control characters and ESC sequences are removed (Security/PasteSanitizer.cs).
    /// <paramref name="broadcast"/>: a paste the user made into the focused pane, which broadcast input copies to the
    /// other panes, each with its own bracketing (Windowing/WindowBroadcast.cs); scripted pastes (cli send-text) stay put.
    /// </summary>
    private void PasteText(Pane pane, string text, bool broadcast = false)
    {
        if (text.Length == 0) return;
        bool bracketed;
        lock (_lock) bracketed = pane.Term.BracketedPaste;
        var paste = Security.PasteSanitizer.Prepare(text, bracketed, _settings.PasteSanitize);
        if (paste.Removed > 0) Toast(ToastKind.Info, "Paste sanitized", $"removed {paste.Removed} control character{(paste.Removed == 1 ? "" : "s")}");
        if (paste.Text.Length > 0) SendInput(pane, paste.Text);
        if (!broadcast || !ReferenceEquals(pane, _focused)) return;
        var targets = _broadcast.Targets();
        for (int i = 0; i < targets.Count; i++) PasteText(targets[i], text);
    }

    // ==== UI helpers ===========================================================================

    internal void Toast(ToastKind kind, string title, string? detail = null) =>
        _toasts.Show(kind, title, detail, _theme, Now);

    private void RebuildTheme()
    {
        _theme = Theme.From(_settings, Scheme, _scale, _app.Accessibility.HighContrast); // App.Accessibility.cs
        _redraw = true;
    }

    private void ApplySchemeTo(Terminal term)
    {
        (_settings with { Colors = Scheme }).ApplyTo(term.Palette);
        term.ColorsChanged(); // DECSET 2031: tells the program when the background turns dark / light
    }

    internal void ApplyScheme()
    {
        lock (_lock)
            foreach (var p in _layout.LeafList) ApplySchemeTo(p.Term);
        RebuildTheme();
    }

    private void PreviewScheme(Command? cmd)
    {
        SchemePreview = cmd is null ? null : AllSchemes().FirstOrDefault(s => "scheme:" + s.Name == cmd.Id);
        ApplyScheme();
    }

    private void ApplyFontSize()
    {
        _fonts.SetPixelSize(FontPixels, _settings.LineHeight);
        ApplyLayout(animate: false);
    }

    // ==== Tabs as the live state (Windowing/TabBarController.cs switches, Windowing/WindowSession.cs restores) ====

    /// <summary>The tab's layout, focus and zoom become the window's live state (session restore, a tab adopted from another window).</summary>
    private void LoadTab(Tab tab)
    {
        _layout = tab.Layout;
        _focused = tab.Focused;
        _zoomed = tab.Zoomed;
        _zoomBadge.Snap(_zoomed is null ? 0 : 1);
    }

    /// <summary>A tab switch: <paramref name="to"/> becomes the live state, its terminals pick up the current colors and settings.</summary>
    private void EnterTab(Tab to)
    {
        var focusedBefore = _focused;
        _layout = to.Layout;
        _focused = to.Focused;
        _zoomed = to.Zoomed;
        ReportFocusMoved(focusedBefore, _focused);
        _zoomBadge.Snap(_zoomed is null ? 0 : 1);
        lock (_lock)
        {
            foreach (var term in _layout.LeafList.Select(p => p.Term))
            {
                ApplySchemeTo(term);
                term.DefaultCursorShape = _settings.CursorShape;
                term.DefaultCursorBlink = _settings.CursorBlinks;
                term.AllowImageFiles = _settings.AllowKittyFileTransmission; // a reload while this tab was in the background
                term.Dirty = true;
            }
        }
        ApplyLayout(animate: false);
        if (_focused.LayoutHidden || !_layout.Contains(_focused)) _focused = TeamRoster.FirstShown(_layout) ?? _focused; // never a parked teammate
        _focused.Focus.Snap(1);
    }

    /// <summary>Nothing is being dragged, selected or reported any more (a tab switch).</summary>
    private void ResetPointer()
    {
        _selecting = null;
        _mouseReportPane = null;
        _scrollbar.Forget(null);
        _hoverDivider = _dragDivider = null;
    }

    /// <summary>Takes the pane out of the active tab's split (vim's Ctrl-w T moves it to a tab of its own).</summary>
    private void TakePaneOut(Pane pane)
    {
        if (_zoomed is not null) SetZoom(null);
        var next = _layout.Remove(pane)!;
        if (_focused == pane) _focused = next;
        if (_selecting == pane) _selecting = null;
        if (_mouseReportPane == pane) _mouseReportPane = null;
    }

    /// <summary>The pane closed or leaves this window: nothing here points at it any more.</summary>
    private void ForgetPane(Pane pane)
    {
        if (_selecting == pane) _selecting = null;
        if (_mouseReportPane == pane) _mouseReportPane = null;
        _scrollbar.Forget(pane);
        _search.Detach(pane);
        _tabCtl.ForgetOutgoing(pane);
        // The pane may live on in another window: drop this window's hover / scroll state for it.
        _windowSearch.ForgetScroll(pane);
        _links.Forget(pane); // hovered link, gutter, quick select (WindowFeatures/WindowLinks.cs)
        _keyInput.Forget(pane);
    }

    /// <summary>Hook from ApplyLayout / Tick: after a reflow, drop UI state that holds old line numbers.</summary>
    private void CheckReflow(Pane pane)
    {
        bool reflowed;
        lock (_lock)
        {
            var selection = pane.Selection;
            reflowed = ReflowInvalidation.Check(pane.Term, ref pane.SeenReflowCount, ref selection, pane.Links);
            pane.Selection = selection;
        }
        if (!reflowed) return;
        long now = Now;
        _vim.Reflowed(pane); // Vim/WindowVim.cs
        if (_selecting == pane) _selecting = null;
        _windowSearch.Reflowed(pane); // the search bar and a scroll toward a match (Windowing/WindowSearch.cs)
        _links.Reflowed(pane, now); // quick select, hovered link, gutter (WindowFeatures/WindowLinks.cs)
        _redraw = true;
    }

    // ==== Configuration ========================================================================

    /// <summary>Hook from App.MaybeReloadConfig (App.Process.cs), for every window: what the reloaded settings change here.</summary>
    internal void ApplyReloadedSettings(Settings old)
    {
        bool fontChanged = _settings.Font != old.Font || !_settings.FontFallback.SequenceEqual(old.FontFallback);
        lock (_lock)
        {
            if (fontChanged)
            {
                var fonts = new FontSet(_settings.Font, _settings.FontFallback, FontPixels, _settings.LineHeight);
                _atlas.Dispose();
                _fonts.Dispose();
                _fonts = fonts;
                _atlas = new GlyphAtlas(_renderer, fonts);
                _dl.Atlas = _atlas;
                _dl.Fonts = _fonts;
            }
#pragma warning disable S1244 // Comparing a setting with its previous value: any difference, however small, means the config changed.
            else if (_settings.FontSize != old.FontSize || _settings.LineHeight != old.LineHeight)
#pragma warning restore S1244
                _fonts.SetPixelSize(FontPixels, _settings.LineHeight);

            foreach (var term in _layout.LeafList.Select(p => p.Term))
            {
                ApplySchemeTo(term);
                term.DefaultCursorShape = _settings.CursorShape;
                term.DefaultCursorBlink = _settings.CursorBlinks;
                term.AllowImageFiles = _settings.AllowKittyFileTransmission;
            }
        }
        if (_settings.ScrollbackLines != old.ScrollbackLines) _backgroundTabs.ApplyScrollbackLimit(); // Windowing/BackgroundTabs.cs
        RebuildTheme();
        if (_settings.WindowDecorations != old.WindowDecorations)
        {
            Sdl.SetWindowBordered(_window, _settings.WindowDecorations);
            _hitTest.Apply(); // Chrome/WindowHitTest.cs
        }
        _floating.ApplyFrame(); // corners + border follow window_decorations and the scheme (Chrome/FloatingChrome.cs)
        _backdrop.Apply(); // Chrome/WindowBackdrop.cs
        ApplyLayout(animate: true);
    }

    // ==== Pop-ups over the panes (input routing) ==================================================

    private bool PopupMouseMove(float x, float y, long now)
    {
        if (_worktrees.MouseMove(x, y, now)) return true;
        if (!_paneSearch.MouseMove(x, y)) return false;
        SetCursor(_cursorDefault);
        return true;
    }

    private bool PopupMouseDown(float x, float y, long now) => _worktrees.MouseDown(x, y, now) || _paneSearch.MouseDown(x, y, now);
    private bool PopupWheel(int lines) => _worktrees.AnyOpen || _paneSearch.Wheel(lines);

    /// <summary>Errors keep coming (Windowing/WindowErrors.cs): the overlays and popups close, the likeliest culprits.</summary>
    private void CloseOverlaysAfterError(long now)
    {
        _menu.Close(now);
        _palette.Close(now);
        _paneSelect.Close(now);
        _about.Close(now);
        _agents.CloseOverlay(now);
        _worktrees.CloseOverlays(now);
        _notifications.ClosePanel(now);
        _quickSelect.Close(now);
        _vim.CloseHelp(now);
        _search.Close(now);
        _paneSearch.Close(now);
    }

    // ==== Entry points for App and Host/ ===========================================================

    internal int ChromeWakeMs(long now) => _chrome.WakeMs(now);
    internal int ScrollbarWakeMs(long now) => WindowScrollbar.WakeMs(_layout, now);
    internal void ReportFrameStats(long now) => _frames.ReportStats(_paneRenderer, now, Id);
    internal bool HoldsFloodFrame(long now) => _frames.IsHolding(now);
    internal int FloodFrameWakeMs(long now) => _frames.WakeMs(now);
    internal int ProgressWakeMs(long now) => _progress.WakeMs(now);
    internal void FlushPendingKeyIfDue(long now) => _keyInput.FlushPendingKeyIfDue(now);
    internal int PendingKeyWakeMs(long now) => _keyInput.PendingKeyWakeMs(now);
    internal void OnInternalError(string? logPath, long now) => _errors.OnInternalError(logPath, now);
    internal bool RequestCloseWindow() => _closeConfirm.RequestCloseWindow();

    /// <summary>This window's tabs (registered with App.Windows, the process's <see cref="WindowSet{TWindow, TTab}"/>).</summary>
    internal TabList<Tab> Tabs => _tabs;
    internal Tab? TabById(int id) => _tabCtl.TabById(id);

    internal bool PrepareTabLeave(Tab tab) => _tearOff.PrepareTabLeave(tab);
    internal void FinishTabLeave(bool wasActive, Tab? next, bool barWasShown) => _tearOff.FinishTabLeave(wasActive, next, barWasShown);
    /// <summary>Before a tab arrives: the active tab's live state goes back into it (it may slide out).</summary>
    internal void PrepareTabArrive() => _tabCtl.SyncActive();
    internal void FinishTabArrive(Tab tab, Tab? previous) => _tearOff.FinishTabArrive(tab, previous);
    internal bool PanesMoved => _tearOff.PanesMoved;
    internal void RequestAttention() => _tearOff.RequestAttention();
    internal string MenuTitle => _tearOff.MenuTitle;
    internal int ShowDropTarget(float x, float y) => _tearOff.ShowDropTarget(x, y);
    internal void HideDropTarget() => _tearOff.HideDropTarget();
    internal int DropIndexAt(float x, float y) => _tearOff.DropIndexAt(x, y);
    internal (float X, float Y) TabGrabOffset => _tabBar.GrabOffset;
    internal (string Number, string Title)? TabGhostLabel => _tabBar.Ghost is { } g ? (g.Number, g.Title) : null;
    internal TabGhost? CreateTabGhost(int x, int y) => _tearOff.CreateTabGhost(x, y);
    internal void DrawTabGhost(TabGhost ghost, string number, string title) => ghost.Draw(_theme, number, title);

    internal void CollectPaneSearchTargets(List<PaneSearchTarget<Pane>> into, string windowPrefix) => _windowSearch.CollectPaneSearchTargets(into, windowPrefix);
    internal bool RevealSearchHit(Pane pane, PaneSearchQuery query, SearchMatch match) => _windowSearch.RevealSearchHit(pane, query, match);

    internal bool ClosedLastPane => _session.ClosedLastPane;
    internal (int W, int H) SizePoints => _session.WindowPoints();
    internal (int X, int Y)? Position => _session.Position;
    internal SessionWindow? CaptureWindow(bool includeScrollback) => _session.CaptureWindow(includeScrollback);

    /// <summary>Font problems found when this window's fonts were loaded (a family that isn't installed, …).</summary>
    internal IReadOnlyList<string> FontWarnings => _fonts?.Warnings ?? [];
    internal void ShowConfigProblems(List<string> problems, bool kept) => _aboutPanel.ShowConfigProblems(problems, kept);

    /// <summary>A hint was picked (WindowFeatures/WindowLinks.cs): opening it (Ctrl / Enter) is the editor's, the rest vim's.</summary>
    private void VimHintChosen(QuickSelect.Item item, bool yank, bool open)
    {
        var pane = _quickSelect.Pane ?? _focused;
        if (open && _editor.OpenQuickSelectItem(pane, item)) return; // WindowFeatures/WindowEditor.cs
        VimPos? at = item.Segments.Count > 0 ? new VimPos(item.Segments[0].Line, item.Segments[0].StartCol) : null;
        _vim.HintChosen(pane, item.Text, at, yank);
    }

    // ==== What the window's parts may ask of it (Windowing/IWindowHost.cs) =========================

    Settings IWindowSurface.Settings => _settings;
    nint IWindowSurface.SdlWindow => _window;
    nint IWindowSurface.Renderer => _renderer;
    float IWindowSurface.Scale => _scale;
    float IWindowSurface.Density => _density;
    (int W, int H) IWindowSurface.WindowPixels() => WindowPixels();
    Padding IWindowSurface.ScaledPadding() => ScaledPadding();

    Dictionary<KeyChord, ActionSpec> IWindowHost.Keys => _keys;
    string IWindowHost.ConfigPath => _configPath;
    int IWindowHost.ConfigProblemCount => _app.ConfigProblems().Count;
    bool IWindowHost.IsWayland => _app.IsWayland;
    TabList<Tab> IWindowHost.Tabs => _tabs;
    bool IWindowHost.TabBarShown => _tabCtl.Shown;
    float IWindowHost.AnimationMs => Anim;
    bool IWindowHost.SmoothScrolling => _settings.SmoothScrolling && Anim > 0;
    bool IWindowHost.IsWindowFocused => _windowFocused;
    int IWindowHost.Gap => Gap;
    FontSet IWindowHost.Fonts => _fonts;
    GlyphAtlas IWindowHost.Atlas => _atlas;
    float IWindowHost.FontZoom { get => FontZoom; set => FontZoom = value; }
    bool IWindowHost.Opaque => _backdrop.Opaque;
    IReadOnlyList<(Pane Pane, RectI Rect)> IWindowHost.PaneRects => _paneRects;
    int IWindowHost.WindowCount => _app.Windows.Count;

    ColorScheme IWindowHost.Scheme => Scheme;
    ColorScheme? IWindowHost.SchemeOverride { get => _schemeOverride; set => _schemeOverride = value; }
    List<ColorScheme> IWindowHost.AllSchemes() => AllSchemes();
    void IWindowHost.ApplySchemeTo(Terminal term) => ApplySchemeTo(term);
    void IWindowHost.ApplySchemeToAllWindows() => _app.ApplySchemeToAllWindows();
    void IWindowHost.ReloadSchemesQuietly() => _app.ReloadSchemesQuietly();
    bool IWindowHost.ThemeDownloadRunning { get => _app.ThemeDownloadRunning; set => _app.ThemeDownloadRunning = value; }

    RectI IWindowHost.ContentBounds() => ContentBounds();
    void IWindowHost.ApplyLayout(bool animate, float durationMs) => ApplyLayout(animate, durationMs);
    void IWindowHost.ApplyFontSize() => ApplyFontSize();
    Pane IWindowHost.SpawnPane(IReadOnlyList<string>? command, string? cwd, RectI area, Action<Terminal>? prepare) => SpawnPane(command, cwd, area, prepare);
    Pane? IWindowHost.Split(SplitAxis axis, IReadOnlyList<string>? command) => Split(axis, command);
    void IWindowHost.ClosePane(Pane pane) => ClosePane(pane);
    Pane? IWindowHost.PaneAt(float x, float y) => PaneAt(x, y);
    void IWindowHost.ForgetPane(Pane pane) => ForgetPane(pane);
    void IWindowHost.TakePaneOut(Pane pane) => TakePaneOut(pane);
    Theme IWindowHost.PaneOverlayTheme(Pane? pane, long now) => PaneOverlayTheme(pane, now);
    string[] IWindowHost.WithShellIntegration(string[] argv, Dictionary<string, string> env) => WithShellIntegration(argv, env);
    void IWindowHost.Wake() => Wake();

    Dictionary<string, string> IWindowHost.PaneEnvironment(int paneId, string? cwd)
    {
        var env = ChildEnvironment();
        _app.AddIpcEnvironment(env, _tmux.Emulation, paneId); // the same ERIKTERM_PANE / TMUX_PANE as before
        Worktrees.WorktreePorts.Shared.AddEnvironment(env, cwd);
        return env;
    }

    void IWindowHost.LoadTab(Tab tab) => LoadTab(tab);
    void IWindowHost.EnterTab(Tab tab) => EnterTab(tab);
    void IWindowHost.CloseTab(Tab tab) => _tabCtl.CloseTab(tab);
    void IWindowHost.LastPaneClosed() => _session.ClosedLastPane = true;

    void IWindowHost.LastTabClosed()
    {
        _session.ClosedLastPane = true;
        _closeRequested = true;
    }

    void IWindowHost.RequestClose() => _closeRequested = true;
    bool IWindowHost.RequestCloseWindow() => _closeConfirm.RequestCloseWindow();

    void IWindowHost.Execute(ActionSpec action) => _actions.Execute(action);
    void IWindowHost.SendInput(Pane pane, string text) => SendInput(pane, text);
    void IWindowHost.WriteInput(Pane pane, ReadOnlySpan<byte> data) => WriteInput(pane, data);
    void IWindowHost.PasteText(Pane pane, string text, bool broadcast) => PasteText(pane, text, broadcast);
    void IWindowHost.PassThrough(uint key, ushort mod) => PassThrough(key, mod);
    void IWindowHost.Copy(bool toClipboard, bool toPrimary) => Copy(toClipboard, toPrimary);
    bool IWindowHost.WantsMouse(Pane pane) => WantsMouse(pane, _shiftHeld);
    bool IWindowHost.PointerBusy => _dragDivider is not null || _selecting is not null || _mouseReportPane is not null;
    bool IWindowHost.ChromeHeldOut => _tabBar.IsDragging || _tabBar.IsEditing || _menu.IsOpen || _tabSwitcher.IsOpen;
    void IWindowHost.ResetPointer() => ResetPointer();
    void IWindowHost.ClearDividerHover() => _hoverDivider = _dragDivider = null;
    void IWindowHost.SetCursor(nint cursor) => SetCursor(cursor);
    nint IWindowHost.CursorDefault => _cursorDefault;
    nint IWindowHost.CursorPointer => _cursorPointer;
    void IWindowHost.KeyDown(uint key, ushort mod, long now) => OnKeyDown(key, mod, now);
    void IWindowHost.MouseMove(float x, float y, long now) => OnMouseMove(x, y, now);
    void IWindowHost.MouseDown(byte button, byte clicks, float x, float y, long now) => OnMouseDown(button, clicks, x, y, now);
    void IWindowHost.MouseUp(byte button, float x, float y, long now) => OnMouseUp(button, x, y, now);
    string[] IWindowHost.ShortcutFor(ActionSpec action) => _commands.ShortcutFor(action);
    string? IWindowHost.ShortcutText(string action) => _commands.ShortcutText(action);
    void IWindowHost.OpenConfigFile() => _commands.OpenConfigFile();
    void IWindowHost.ReloadConfiguration() => _app.OnConfigChanged();
    void IWindowHost.CloseOverlaysAfterError(long now) => CloseOverlaysAfterError(now);

    /// <summary>A new window where the focused pane is: in its container too, when it is in one (WindowFeatures/WindowContainers.cs).</summary>
    void IWindowHost.SpawnWindow() => _app.SpawnWindow(_focused.StartDirectory(null), this, _containers.ShellFor(_focused));
    bool IWindowHost.CanDetachTab() => _app.CanDetachTab(this);
    void IWindowHost.OnTabDragged(float x, float y) => _app.OnTabDragged(this, x, y);
    void IWindowHost.OnTabDropped(Tab tab, float x, float y) => _app.OnTabDropped(this, tab, x, y);
    bool IWindowHost.CanMoveTab(Tab tab) => _app.CanMoveTab(tab, null);
    void IWindowHost.MoveTab(Tab tab, TermWindow target) => _app.MoveTab(tab, target, -1);
    void IWindowHost.MoveTabToNewWindow(Tab tab) => _app.MoveTabToNewWindow(this, tab);
    void IWindowHost.MoveTabToWindow(Tab tab, int windowId) => _app.MoveTabToWindow(this, tab, windowId);
    List<TermWindow> IWindowHost.OtherWindows() => _app.OtherWindows(this);

    // ---- IWindowIntegrations (Windowing/IWindowHost.cs): the window's parts reach the feature classes through these ----

    InfoBar IWindowIntegrations.InfoBar => _infoBar;
    void IWindowIntegrations.Post(Action work) => _jobs.Post(work);
    void IWindowIntegrations.CopyText(string text, string what) => Clipboard.Copy(this, text, what);
    string? IWindowIntegrations.PreferredEditor() => _editor.PreferredEditor();
    Tab? IWindowIntegrations.SpawnToolTab(string[] argv, string cwd, string errorTitle) => _launches.SpawnToolTab(argv, cwd, errorTitle);
    int IWindowIntegrations.Osc52WakeMs(long now) => _osc52.WakeMs(now);
    void IWindowIntegrations.ExecuteShellAction(ActionSpec action, uint key, ushort mod) => _links.Execute(action, key, mod);

    void IWindowIntegrations.RunAgentAction(string name)
    {
        if (name == "AgentsOverview") _agents.OpenOverview(); // Agents/WindowAgents.cs
        else _agents.OpenSend();
    }

    void IWindowIntegrations.RunWorktreeAction(string name)
    {
        if (name == "NewAgentWorktree") _worktrees.OpenAgentForm(); // Worktrees/WindowWorktrees.cs
        else _worktrees.Execute(name);
    }

    void IWindowIntegrations.OpenReview(bool pickBase) => _review.Open(pickBase);
    void IWindowIntegrations.RunTeammateToggle(string arg) => _teammates.RunToggleAction(arg);
    void IWindowIntegrations.RunNotificationsAction(string name) => _notifications.Execute(name);
    void IWindowIntegrations.SendSnippetNamed(string name) => _snippets.SendNamed(name);
    void IWindowIntegrations.SendAgentNewline(uint key, ushort mod) => _agents.SendNewline(key, mod);
    void IWindowIntegrations.AddShellCommands(Action<string, string, int, string, Func<bool>?> add) => WindowPrompts.AddCommands(add);
    void IWindowIntegrations.AddAgentCommands(Action<string, string, int, string, Func<bool>?> add) => _agents.AddCommands(add);

    void IWindowIntegrations.AddIntegrationCommandGroup(List<Command> list)
    {
        _snippets.AddCommands(list); // Snippet: name
        _recent.AddCommands(list); // Run again: …, Go to: …
        _teammates.AddCommands(list); // Open / park teammate: … (Teammates/WindowTeammates.cs)
        _notifications.AddCommands(list); // Notifications…, desktop notifications on/off
        _integrations.AddCommands(list); // Git: …, GitHub: … for the focused pane
        _worktrees.AddCommands(list); // New Agent Worktree…, Git: Worktrees…, Finish, Clean Up (Worktrees/WindowWorktrees.cs)
        _review.AddCommands(list); // Git: Compare with main
        _containers.AddCommands(list); // Dev Container: Open, Rebuild
    }

    void IWindowIntegrations.AddTmuxAndProcessCommands(List<Command> list)
    {
        _tmux.AddCommands(list);
        _app.AddSessionCommands(list, this); // App.Session.cs
        _app.AddConfigCommands(list, this); // Show Config Problems (App.Process.cs)
    }

    void IWindowIntegrations.AddShellMenuItems(List<ContextMenu.Item> items, float x, float y) => _links.AddMenuItems(items, x, y);
    bool IWindowIntegrations.HasAgentTargets => _agents.Targets().Count > 0;
    void IWindowIntegrations.OpenAgentSend() => _agents.OpenSend();
    bool IWindowIntegrations.JumpToPane(Pane pane) => _agents.JumpToPane(pane);
    bool IWindowIntegrations.IsRealTmux(Pane pane) => WindowTmux.IsRealTmux(pane);
    string IWindowIntegrations.AgentTabLabel(Pane pane, string label) => _agents.TabLabel(pane, label);
    string IWindowIntegrations.ContainerTitle(PaneContainer container, Pane pane) => WindowContainers.Title(container, pane);
    bool IWindowIntegrations.TabInContainer(Tab tab) => WindowContainers.TabInContainer(tab);
    RepoBadgeModel? IWindowIntegrations.TabRepoBadge(Tab tab) => TabRepoBadge(tab);
    string[]? IWindowIntegrations.ContainerShellFor(Pane from) => _containers.ShellFor(from);
    bool IWindowIntegrations.ContinueWorktreeLaunch(Pane pane) => _worktrees.ContinueLaunch(pane);
    bool IWindowIntegrations.ContinueRepoPlan(Pane pane) => _repoPlans.Continue(pane);
    bool IWindowIntegrations.RepoPlanKeyDown(Pane pane, uint key) => _repoPlans.KeyDown(pane, key);
    string[]? IWindowIntegrations.RestoreContainerCommand(SessionContainer? saved) => _containers.RestoreCommand(saved);
    SessionContainer? IWindowIntegrations.CaptureContainer(Pane pane) => WindowContainers.Capture(pane);
    void IWindowIntegrations.ConsiderAgentResume(Pane pane, SessionPane saved) => _agentResume.Consider(pane, saved);
    SessionAgent? IWindowIntegrations.CaptureAgentResume(Pane pane) => _agentResume.Capture(pane);
    void IWindowIntegrations.SyncTeammates(LayoutTree<Pane> layout) => _teammates.Sync(layout);
    int IWindowIntegrations.TeammateStripHeight(Pane pane, RectI r) => _teammates.StripHeightFor(pane, r);
    void IWindowIntegrations.SizeParkedTeammates(LayoutTree<Pane> layout, RectI bounds, bool background) => _teammates.SizeParked(layout, bounds, background);
    bool IWindowIntegrations.SizeTerminal(Pane pane, RectI r, int strip) => SizeTerminal(pane, r, strip);
    // ghfake (made-up CI states) rides along with the drag-and-drop steps, as before.
    bool IWindowIntegrations.RunDropScriptStep(string kind, string arg, long now) => _drop.RunScriptStep(kind, arg, now) || _github.RunScriptStep(kind, arg);
    bool IWindowIntegrations.RunTeammateScriptStep(string kind, string arg, long now) => _teammates.RunScriptStep(kind, arg, now, this);
    void IWindowIntegrations.RunShellScriptStep(string kind, string arg, long now) => _links.RunScriptStep(kind, arg, now);

    // ---- The feature classes in Tabs/, Search/ and Vim/ --------------------------------------------

    TabList<Tab> ITabWindow<Tab>.Tabs => _tabs;
    bool ITabWindow<Tab>.TabBarShown => _tabCtl.Shown;
    void ITabWindow<Tab>.ActivateTab(int index) => _tabCtl.ActivateTabIndex(index);
    void ITabSync.SyncTabs() => _tabCtl.SyncActive();

    bool ITabSwitcherWindow<Tab>.OverlayOpen => OverlayOpen;
    bool ITabSwitcherWindow<Tab>.AnimateProgress => _progress.AnimateProgress;

    void ITabSwitcherWindow<Tab>.CloseOverlaysForTabSwitcher(long now)
    {
        _menu.Close(now);
        _palette.Close(now);
        _paneSelect.Close(now);
    }

    void ITabSwitcherWindow<Tab>.ActivateTabRelative(int delta) => _tabCtl.ActivateTabRelative(delta);
    RepoBadgeModel? ITabSwitcherWindow<Tab>.TabRepoBadge(Tab tab) => TabRepoBadge(tab);
    uint? ITabSwitcherWindow<Tab>.TabColorOf(Tab tab) => _tabColors.ColorOf(tab);

    ColorScheme ITabColorWindow<Tab>.Scheme => Scheme;

    void ITabColorWindow<Tab>.OpenPalette(string query)
    {
        _actions.Execute(new ActionSpec("ActivateCommandPalette", null));
        _palette.HandleText(query, Now);
    }

    Dictionary<KeyChord, ActionSpec> IPaneSearchWindow<Pane>.Keys => _keys;
    List<PaneSearchTarget<Pane>> IPaneSearchWindow<Pane>.PaneSearchTargets() => _app.PaneSearchTargets(this);
    bool IPaneSearchWindow<Pane>.RevealSearchHit(Pane pane, PaneSearchQuery query, SearchMatch match) => _app.RevealSearchHit(pane, query, match);
    string IPaneSearchWindow<Pane>.PasteText(string? arg) => arg == "PrimarySelection" ? Sdl.GetPrimarySelectionText() : Sdl.GetClipboardText();

    string? IPaneSearchWindow<Pane>.SelectionForSearch()
    {
        lock (_lock)
            return _focused.Selection?.Text(_focused.Term) is { Length: > 0 and <= 200 } text && !text.Contains('\n') ? text : null;
    }

    void IPaneSearchWindow<Pane>.CloseOverlaysForPaneSearch(long now)
    {
        _menu.Close(now);
        _palette.Close(now);
        _agents.CloseOverlay(now);
        _notifications.ClosePanel(now);
    }

    Pane IWindowPanes<Pane>.Focused => _focused;
    Pane? IWindowPanes<Pane>.Zoomed => _zoomed;
    LayoutTree<Pane> IWindowPanes<Pane>.Layout => _layout;
    int IWindowPanes<Pane>.ShownPaneCount => ShownPaneCount;
    int IWindowPanes<Pane>.TabCount => _tabs.Count;
    int IWindowPanes<Pane>.ActiveTabIndex => _tabs.ActiveIndex;
    List<Pane> IWindowPanes<Pane>.VisiblePanes() => VisiblePanes();
    Pane? IWindowPanes<Pane>.FindPane(int id) => FindPane(id);
    void IWindowPanes<Pane>.Focus(Pane pane) => Focus(pane);
    void IWindowPanes<Pane>.SetZoom(Pane? pane) => SetZoom(pane);
    void IWindowPanes<Pane>.ApplyLayout(bool animate) => ApplyLayout(animate);
    void IWindowPanes<Pane>.ActivateTab(int index) => _tabCtl.ActivateTabIndex(index);

    LayoutTree<Pane> IWindowPanes<Pane>.TabLayout(int tabIndex)
    {
        var tab = _tabs.Items[tabIndex];
        return IsActive(tab) ? _layout : tab.Layout;
    }

    void IWindowPanes<Pane>.ClearTabZoom(int tabIndex)
    {
        var tab = _tabs.Items[tabIndex];
        if (!IsActive(tab)) tab.Zoomed = null;
        else if (_zoomed is not null) SetZoom(null);
    }

    Theme IWindowChrome.Theme => _theme;
    void IWindowChrome.Redraw() => _redraw = true;
    void IWindowChrome.Toast(ToastKind kind, string title, string? detail) => Toast(kind, title, detail);

    void IVimCommands<Pane>.Split(SplitAxis axis, IReadOnlyList<string>? command) => Split(axis, command);
    void IVimCommands<Pane>.SplitRun(string[] argv) => _launches.SplitRun(_tracks.Cwd(_focused), argv);
    void IVimCommands<Pane>.ClosePane(Pane pane) => ClosePane(pane);
    void IVimCommands<Pane>.RequestClosePane(Pane pane) => _closeConfirm.RequestClosePane(pane);
    void IVimCommands<Pane>.SpawnTab() => _tabCtl.SpawnTab();
    bool IVimCommands<Pane>.MovePaneToNewTab(Pane pane) => _tabCtl.MovePaneToNewTab(pane);
    void IVimCommands<Pane>.ActivateDirection(Direction direction) => _paneActions.ActivateDirection(direction, 0, 0);
    void IVimCommands<Pane>.Execute(ActionSpec action, uint key, ushort mod) => _actions.Execute(action, key, mod);
    void IVimCommands<Pane>.PasteText(Pane pane, string text) => PasteText(pane, text);
    int IVimCommands<Pane>.IpcTab(IpcRequest request) => _tabs.IndexOf(_ipcTargets.Tab(request));

    void IVimCommands<Pane>.CloseActiveTab(bool force)
    {
        if (_tabs.Active is not { } tab) return;
        if (force) _tabCtl.CloseTab(tab);
        else _closeConfirm.RequestCloseTab(tab);
    }

    bool IVimCommands<Pane>.GrowFocused(SplitAxis axis, int cells) => _paneActions.GrowFocused(axis, cells);

    (int TabIndex, Pane Pane) IVimCommands<Pane>.IpcTarget(IpcRequest request)
    {
        var (tab, pane) = _ipcTargets.Target(request);
        return (_tabs.IndexOf(tab), pane);
    }

    bool IVimOpeners<Pane>.FindLinkAt(Pane pane, long line, int col, out string uri, out SmartLink? smart) =>
        _links.FindLinkAt(pane, line, col, out uri, out smart);
    void IVimOpeners<Pane>.OpenSmartLink(Pane pane, SmartLink link) => _editor.OpenSmartLink(pane, link);
    void IVimOpeners<Pane>.OpenLink(string uri) => Clipboard.OpenLink(this, uri);
    string? IVimOpeners<Pane>.PreferredEditor() => _editor.PreferredEditor();
    (Pane Pane, string Server)? IVimOpeners<Pane>.RunningNvim() => _editor.RunningNvim();
    void IVimOpeners<Pane>.RunInBackground(Func<string?> work, Action<string?> then) => _jobs.Run(work, then, wakeLoop: true);

    void IVimClipboard.SetClipboard(string text) => Sdl.SetClipboardText(text);
    void IVimClipboard.SetPrimary(string text) => Sdl.SetPrimarySelectionText(text);
    string IVimClipboard.Clipboard() => Sdl.GetClipboardText();
    string IVimClipboard.Primary() => Sdl.GetPrimarySelectionText();

    Theme IVimWindow<Pane>.OverlayTheme => _overlayTheme;
    Theme IVimWindow<Pane>.PaneOverlayTheme(Pane pane, long now) => PaneOverlayTheme(pane, now);
    bool IVimWindow<Pane>.TabBarEditing => _tabBar.IsEditing;
    string? IVimWindow<Pane>.ShortcutText(string action) => _commands.ShortcutText(action);
    bool IVimWindow<Pane>.QuickSelectOpen => _quickSelect.IsOpen;
    void IVimWindow<Pane>.OpenHints(long now) => _links.OpenQuickSelect(now, QuickSelectPurpose.VimHints);
    void IVimWindow<Pane>.ApplyScheme() => ApplyScheme();
    void IVimWindow<Pane>.ScriptKeyDown(uint key, ushort mod, long now) => OnKeyDown(key, mod, now);
    void IVimWindow<Pane>.ScriptTextInput(string text, long now) => OnTextInput(text, now);

    void IVimWindow<Pane>.CloseBarsForVim(long now)
    {
        _windowSearch.Close(resetView: false);
        _menu.Close(now);
    }

    (int Col, int Row) IVimWindow<Pane>.CellAt(Pane pane, float x, float y)
    {
        var (col, row, _) = CellAt(pane, x, y);
        return (col, row);
    }

    void IVimWindow<Pane>.CloseHints(Pane pane, long now)
    {
        if (_quickSelect.IsOpen && _quickSelect.Purpose == QuickSelectPurpose.VimHints && _quickSelect.Pane == pane) _quickSelect.Close(now);
    }

    // ==== Panes, for the feature classes and App ===================================================

    /// <summary>Panes on screen in the active tab (parked teammates don't count).</summary>
    private int ShownPaneCount => _paneRects.Count;

    private bool IsActive(Tab tab) => ReferenceEquals(tab, _tabs.Active);

    /// <summary>Some overlay takes the keys and the pointer.</summary>
    private bool OverlayOpen => _tabSwitcher.IsOpen || _worktrees.AnyOpen || _paneSearch.IsOpen || _quickSelect.IsOpen || _palette.IsOpen || _menu.IsOpen || _paneSelect.IsOpen;

    /// <summary>
    /// Sizes a pane's terminal and PTY for <paramref name="r"/>, leaving its teammate strip and a held pane's exit bar
    /// (Windowing/ExitHoldFlow.cs) out of the grid. True when it changed.
    /// </summary>
    private bool SizeTerminal(Pane pane, RectI r, int strip)
    {
        pane.StripPx = strip;
        pane.HeldPx = _exitHold.BarHeightFor(pane, r, strip);
        strip += pane.HeldPx;
        int cols = Math.Max(2, r.W / _fonts.CellWidth), rows = Math.Max(1, (r.H - strip) / _fonts.CellHeight);
        bool resize;
        lock (_lock)
        {
            resize = cols != pane.Term.Columns || rows != pane.Term.Rows;
            (pane.Term.CellPixelWidth, pane.Term.CellPixelHeight) = (_fonts.CellWidth, _fonts.CellHeight); // image sizing
            if (resize) pane.Term.Resize(cols, rows);
        }
        if (resize) pane.Pty.Resize(cols, rows, r.W, r.H - strip);
        return resize;
    }

    /// <summary>A pane of this window by id, any tab (the active one's live layout); null when it isn't here.</summary>
    private Pane? FindPane(int id)
    {
        var tabs = _tabs.Items;
        for (int i = 0; i < tabs.Count; i++)
        {
            var leaves = (ReferenceEquals(tabs[i], _tabs.Active) ? _layout : tabs[i].Layout).LeafList;
            for (int j = 0; j < leaves.Count; j++) if (leaves[j].Id == id) return leaves[j];
        }
        return null;
    }

    /// <summary>Whether a pane of this window (any tab) shows <paramref name="term"/>.</summary>
    internal bool HasTerminal(Terminal term)
    {
        return _tabs.Items.Any(tab => tab.Layout.Leaves().Any(pane => ReferenceEquals(pane.Term, term)));
    }

    internal int FocusedPaneId => _focused.Id;

    /// <summary>The directory to start in from pane <paramref name="id"/> if it's in this window (spawn --new-window from a pane).</summary>
    internal string? PaneStartDirectory(int id)
    {
        foreach (var tab in _tabs.Items)
            foreach (var pane in tab.Layout.Leaves())
                if (pane.Id == id) return pane.StartDirectory(null);
        return null;
    }

    internal bool HasPane(int id) => PaneById(id) is not null;

    internal Pane? PaneById(int id)
    {
        foreach (var tab in _tabs.Items)
            foreach (var pane in tab.Layout.Leaves())
                if (pane.Id == id) return pane;
        return null;
    }

    /// <summary><see cref="PaneById"/> for callers outside a request, where the active tab hasn't been synced.</summary>
    internal Pane? SyncedPane(int id)
    {
        _tabCtl.SyncActive();
        return PaneById(id);
    }

    // ==== Hooks into the feature classes ===========================================================

    /// <summary>`erikterm cli` requests (WindowFeatures/WindowIpc.cs).</summary>
    internal IpcResponse HandleIpc(IpcRequest r) => _ipc.Handle(r);

    /// <summary>Appends this window's panes (App.Ipc.cs lists every window).</summary>
    internal void ListPanes(List<PaneInfo> list) => _ipc.ListPanes(list);

    /// <summary>An OSC 52 request from one of this window's panes (App.TickClipboard).</summary>
    internal void ApplyClipboardWrite(Terminal term, ClipboardTarget target, string text) => _osc52.Apply(term, target, text);

    /// <summary>Hook from App.ReviewPanes: panes of this window showing a review.</summary>
    internal void CollectReviewPanes(List<ReviewPaneRef> into) => _review.CollectPanes(into);

    internal GitHubStatus? GitHubStatusFor(string root, string branch) => _github.StatusFor(root, branch);

    /// <summary>Hook from App.PanesByWorktree: panes in this window, by the worktree they're in.</summary>
    internal void CollectWorktreePanes(Dictionary<string, List<WorktreePaneRef<Pane>>> into, List<string> roots)
    {
        _tabCtl.SyncActive();
        _worktrees.CollectPanes(into, roots);
    }

    /// <summary>Hooks from the event loop: the next agent poll or pulse frame, a look at the agents on offer, a notification change.</summary>
    internal int AgentWakeMs(long now) => _agents.WakeMs(now);
    internal int AgentResumeWakeMs(long now) => _agentResume.WakeMs(now);
    internal int NotificationWakeMs(long now) => _notifications.WakeMs(now);

    /// <summary>Hook from the event loop: background results to collect, a pill to fade, a spinner step, a stale drag.</summary>
    internal int IntegrationWakeMs(long now) =>
        Math.Min(_integrations.WakeMs(now, _windowFocused, _tabCtl.Shown, _settings.GitStatus, _focused), _drop.WakeMs(now));

    internal bool JumpToPane(Pane pane) => _agents.JumpToPane(pane);
    internal void CollectAgentRows(List<AgentRow<Pane>> rows, bool manyWindows, long now) => _agents.CollectRows(rows, manyWindows, now);
    internal void CollectAgentPanes(List<AgentPaneRef<Pane>> into) => _agents.CollectPanes(into);

    /// <summary>Tick hook: OSC 52 policy and expiry, then the hovered link.</summary>
    private void TickShell(long now)
    {
        _osc52.Tick(now, _settings.Osc52, _layout.LeafList);
        _links.Tick(now, _layout);
    }

    /// <summary>Tick hook: background results, the integrations' scan, the repo pills, a drag that left.</summary>
    private void TickIntegrations(long now)
    {
        _integrations.Tick(now, _settings, _focused, _windowFocused, _tabCtl.Shown, _links.MouseX, _links.MouseY);
        _drop.Tick(now, _layout);
    }

    /// <summary>DrawOverlays hook (above the panes, below menus and toasts).</summary>
    private void DrawIntegrationOverlays(long now)
    {
        bool zoomBadge = _zoomed is not null && _zoomBadge.Value(now) > 0.01f;
        var frame = new RepoPillFrame(_dl, _theme, _settings.GitStatus, _agents.AnyAgents && _settings.AgentStatus, zoomBadge, _windowFocused);
        _integrations.DrawPills(frame, VisiblePanes(), now);
        _drop.Draw(_dl, _theme, now);
    }

    /// <summary>Render hook: quick select and the link hint (outside the terminal lock: they use snapshots).</summary>
    private void DrawShellOverlays(long now)
    {
        _quickSelect.Draw(_dl, PaneOverlayTheme(_quickSelect.Pane, now), now);
        _links.DrawHint(_dl, PaneOverlayTheme(_links.HintPane, now), now);
    }

    private void DrawAgentResume(RectF window, long now) =>
        _agentResume.Draw(_dl, window, window.Bottom - _theme.S(12) - _notifications.ReserveBottom, now);

    private void DrawInfoBars(long now)
    {
        if (!_notifications.AnyBarVisible(now)) return;
        var (w, h) = WindowPixels();
        _notifications.DrawBars(_dl, w, h, ScaledPadding(), _tabCtl.Reserve(), now);
    }

    /// <summary>The badge of a tab: its focused pane's (no allocation; called while drawing the tab bar).</summary>
    private RepoBadgeModel? TabRepoBadge(Tab tab) =>
        _integrations.TabBadge(ReferenceEquals(tab, _tabs.Active) ? _focused : tab.Focused, _settings.GitStatus);

    /// <summary>Hook from OnMouseMove: the info and notification bars, then the pill.</summary>
    private bool BarsMouseMove(float x, float y)
    {
        if (_dragDivider is not null || _selecting is not null || _mouseReportPane is not null) return false; // a drag goes on underneath
        bool over = _notifications.BarsMouseMove(x, y, out bool control);
        if (over) { SetCursor(control ? _cursorPointer : _cursorDefault); return true; }
        over = _agentResume.MouseMove(x, y);
        if (over) SetCursor(_cursorPointer);
        return over;
    }

    // ==== What the feature classes may ask of this window ==========================================

    // ---- IFeatureWindow (WindowFeatures/) ----

    uint IFeatureWindow.Id => Id;
    Settings IFeatureWindow.Settings => _settings;
    bool IFeatureWindow.IsFocused => _windowFocused;
    bool IFeatureWindow.IsCurrentWindow => ReferenceEquals(_app.Windows.Focused, this);
    float IFeatureWindow.Anim => Anim;
    Theme IFeatureWindow.OverlayTheme => _overlayTheme;
    TabList<Tab> IFeatureWindow.Tabs => _tabs;
    void IFeatureWindow.SyncActiveTab() => _tabCtl.SyncActive();
    Tab? IFeatureWindow.TabById(int id) => _tabCtl.TabById(id);
    RectI IFeatureWindow.ContentBounds() => ContentBounds();
    int IFeatureWindow.CellWidth => _fonts.CellWidth;
    int IFeatureWindow.CellHeight => _fonts.CellHeight;
    int IFeatureWindow.Gap => Gap;
    void IFeatureWindow.Wake() => Wake();
    void IFeatureWindow.CloseContextMenu(long now) => _menu.Close(now);
    void IFeatureWindow.ClosePalette(long now) => _palette.Close(now);
    void IFeatureWindow.OpenPalette(List<Command> commands, CommandPalette.Options options) => _palette.Open(commands, _theme, Now, options);
    string[] IFeatureWindow.ShortcutFor(ActionSpec action) => _commands.ShortcutFor(action);
    string? IFeatureWindow.ShortcutText(string action) => _commands.ShortcutText(action);

    void IFeatureWindow.ToastWithAction(ToastKind kind, string title, string? detail, string actionLabel, Action? action) =>
        _toasts.Show(kind, title, detail, _theme, Now, actionLabel, action);

    void IFeatureWindow.CloseMenus(long now)
    {
        _menu.Close(now);
        _palette.Close(now);
    }

    bool IReviewWindow.JumpToReview(string root) => _app.JumpToReview(root);
    string? IReviewWindow.FocusedWorktreeCwd() => _worktrees.FocusedCwd();
    RecentHistory? IRecentWindow.Recent => _app.Recent;
    bool IRecentWindow.ForgetRecent() => _app.ForgetRecent();
    bool ITmuxWindow.TmuxShimAvailable => _app.TmuxShimAvailable;
    bool ILinkWindow.OverlayOpen => OverlayOpen;
    void ILinkWindow.VimHintChosen(QuickSelect.Item item, bool yank, bool open) => VimHintChosen(item, yank, open); // TermWindow.cs

    void IIpcWindow.SetTitleUntilProgramSets(string title)
    {
        lock (_lock) _title = _focused.Term.Title;
        SetCompositorTitle(title);
    }

    // ---- Panes: launching, input, pointer ----

    Pane IPaneLauncher.SpawnPane(IReadOnlyList<string>? command, string? cwd, RectI area, Action<Terminal>? prepare, bool fromTmux) =>
        SpawnPane(command, cwd, area, prepare, fromTmux);
    Pane? IPaneLauncher.Split(SplitAxis axis, IReadOnlyList<string>? command, string? cwd, bool focus, Action<Terminal>? prepare) =>
        Split(axis, command, cwd, focus, prepare);
    bool IPaneLauncher.RespawnPane(Pane pane, string[] argv, string? cwd) => _exitHold.RespawnPane(pane, argv, cwd); // Windowing/ExitHoldFlow.cs
    void IPaneLauncher.ClosePane(Pane pane) => ClosePane(pane);
    void IPaneLauncher.SpawnTabIn(string? cwd, string[]? command) => _tabCtl.SpawnTabIn(cwd, command);
    void IPaneLauncher.SwitchTo(Tab from, Tab to, int dir) => _tabCtl.SwitchTo(from, to, dir);
    void IPaneLauncher.ResizeBackgroundTabs() => _backgroundTabs.Resize(ContentBounds(), _tabCtl.Outgoing);
    string[] IPaneLauncher.DefaultShell() => _settings.DefaultProg?.ToArray() ?? DefaultShellCommand();
    Dictionary<string, string> IPaneLauncher.ChildEnvironment() => ChildEnvironment();
    void IPaneLauncher.AddIpcEnvironment(Dictionary<string, string> env, bool tmuxEmulation, int? paneId) => _app.AddIpcEnvironment(env, tmuxEmulation, paneId);
    string IPaneLauncher.ExitBarKey => ExitHoldFlow.MessageKey;
    object IPaneLauncher.ExitBarOwner => this;

    void IPaneInput.PasteText(Pane pane, string text, bool broadcast) => PasteText(pane, text, broadcast);
    void IPaneInput.SendInput(Pane pane, string text) => SendInput(pane, text);
    void IPaneInput.BroadcastText(string text) => _broadcast.Text(text);
    void IPaneInput.PassThrough(uint key, ushort mod) => PassThrough(key, mod);

    Pane? IPointerWindow.PaneAt(float x, float y) => PaneAt(x, y);
    bool IPointerWindow.DragInProgress => _dragDivider is not null || _selecting is not null || _mouseReportPane is not null;
    bool IPointerWindow.WantsMouse(Pane pane) => WantsMouse(pane, _shiftHeld);
    void IPointerWindow.PointerCursor() => SetCursor(_cursorPointer);
    void IPointerWindow.DefaultCursor() => SetCursor(_cursorDefault);
    void IPointerWindow.TextCursor() => SetCursor(_cursorText);
    void IPointerWindow.OpenContextMenu(float x, float y, long now) => _commands.OpenContextMenu(x, y, now);

    (int Col, int Row) IPointerWindow.CellAt(Pane pane, float x, float y)
    {
        var (col, row, _) = CellAt(pane, x, y);
        return (col, row);
    }

    void IScriptPointer.ScriptMouseMove(float x, float y, long now) => OnMouseMove(x, y, now);

    void IScriptPointer.ScriptClick(float x, float y, long now)
    {
        OnMouseMove(x, y, now);
        OnMouseDown(Sdl.BUTTON_LEFT, 1, x, y, now);
        OnMouseUp(Sdl.BUTTON_LEFT, x, y, now);
    }

    // ---- IAgentWindow (Agents/WindowAgents.cs) ----

    uint IAgentWindow<Pane>.Id => Id;
    bool IAgentWindow<Pane>.IsFocused => _windowFocused;
    bool IAgentWindow<Pane>.IsCurrentWindow => ReferenceEquals(_app.Windows.Focused, this);
    bool IAgentWindow<Pane>.TabBarShown => _tabCtl.Shown;
    bool IAgentWindow<Pane>.ZoomBadgeShown(long now) => _zoomed is not null && _zoomBadge.Value(now) > 0.01f;
    int? IAgentWindow<Pane>.ActiveTabId => _tabs.Active?.Id;
    int IAgentWindow<Pane>.TabId(int tabIndex) => _tabs.Items[tabIndex].Id;

    void IAgentWindow<Pane>.ShowTabAgentState(int tabIndex, AgentState state)
    {
        var tab = _tabs.Items[tabIndex];
        if (tab.AgentState == state) return;
        tab.AgentState = state;
        tab.TitleAt = 0;
        _redraw = true;
    }

    void IAgentWindow<Pane>.ClearTabAgentState(int tabIndex) => _tabs.Items[tabIndex].AgentState = AgentState.None;
    void IAgentWindow<Pane>.FlashTaskbar(bool untilFocused) => Sdl.FlashWindow(_window, untilFocused ? Sdl.FLASH_UNTIL_FOCUSED : Sdl.FLASH_CANCEL);

    void IAgentWindow<Pane>.ToastInFocusedWindow(ToastKind kind, string title, string? detail) =>
        (_app.Windows.Focused ?? this).Toast(kind, title, detail);

    void IAgentWindow<Pane>.ToastWithAction(ToastKind kind, string title, string? detail, string actionLabel, Action action) =>
        _toasts.Show(kind, title, detail, _theme, Now, actionLabel, action);

    void IAgentWindow<Pane>.CloseMenu(long now) => _menu.Close(now);
    void IAgentWindow<Pane>.OpenPalette(List<Command> commands, CommandPalette.Options options, long now) => _palette.Open(commands, _theme, now, options);

    void IAgentWindow<Pane>.CloseOverlaysForAgents(long now)
    {
        _menu.Close(now);
        _palette.Close(now);
        _notifications.ClosePanel(now);
    }

    void IAgentInput<Pane>.SendInput(Pane pane, string text) => SendInput(pane, text);
    void IAgentInput<Pane>.SendKey(uint key, ushort mod) => _keyInput.SendKey(key, 0, mod, KeyEventKind.Press);
    void IAgentInput<Pane>.BroadcastKey(uint key, ushort mod) => _broadcast.Key(key, 0, mod, KeyEventKind.Press);

    // ---- IAgentResumeWindow (Agents/WindowAgentResume.cs) ----

    // Windows: only an .exe can be the pane's process (an npm .cmd shim needs cmd.exe, and the bare shim is a sh script).
    bool IAgentResumeWindow<Pane>.AgentProgramFound(string program) =>
        ToolRunner.Search(program, Environment.GetEnvironmentVariable("PATH")) is { } path
        && (!OperatingSystem.IsWindows() || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    bool IAgentResumeWindow<Pane>.StartAgentInPane(Pane pane, string[] argv, string directory)
    {
        string[] shell = _settings.DefaultProg?.ToArray() ?? DefaultShellCommand();
        var plan = new WorktreeLaunchPlan(null, argv, shell, place: "this directory");
        if (!_exitHold.RespawnPane(pane, argv, directory)) return false; // Windowing/ExitHoldFlow.cs
        pane.LaunchArgv = shell;
        pane.LaunchedAgent = argv; // one launch: Restart Pane gives a shell, and restore_programs won't re-run it
        pane.WorktreeLaunch = plan;
        return true;
    }

    // ---- ITeammateWindow (Teammates/WindowTeammates.cs) ----

    Settings ITeammateWindow<Pane>.Settings => _settings;
    ColorScheme ITeammateWindow<Pane>.Scheme => Scheme;
    float ITeammateWindow<Pane>.Anim => Anim;
    int ITeammateWindow<Pane>.LayoutGap => Gap;
    Pane ITeammateWindow<Pane>.TabFocused(int tabIndex) => _tabs.Items[tabIndex].Focused;
    void ITeammateWindow<Pane>.SetTabFocused(int tabIndex, Pane pane) => _tabs.Items[tabIndex].Focused = pane;
    Pane ITeammateWindow<Pane>.SpawnTeammate(string[]? argv, string? cwd, RectI area) => SpawnPane(argv, cwd, area, fromTmux: true);
    void ITeammateWindow<Pane>.ResizeBackgroundTabs() => _backgroundTabs.Resize(ContentBounds(), _tabCtl.Outgoing);
    bool ITeammateWindow<Pane>.SizeTerminal(Pane pane, RectI r, int strip) => SizeTerminal(pane, r, strip);

    void ITeammateWindow<Pane>.ParkedResized(Pane pane, bool background)
    {
        if (background) _backgroundTabs.Activity.Resized(pane.Id, Now);
        else CheckReflow(pane);
    }

    void ITeammateWindow<Pane>.DropZoom(long now)
    {
        _zoomed = null;
        _zoomBadge.Set(0, now, Anim);
    }

    void ITeammateWindow<Pane>.PointerCursor() => SetCursor(_cursorPointer);
    void ITeammateWindow<Pane>.DefaultCursor() => SetCursor(_cursorDefault);
    string[] ITeammateWindow<Pane>.ShortcutFor(ActionSpec action) => _commands.ShortcutFor(action);

    // ---- IWorktreeWindow (Worktrees/WindowWorktrees.cs): launches open through WindowFeatures/WindowLaunches.cs ----

    string? IWorktreeLaunches<Pane>.OpenLaunchTab(WorktreeTabLaunch launch, Action<Pane> prepared) => _launches.OpenLaunchTab(launch, prepared);
    Pane? IWorktreeLaunches<Pane>.OpenLaunchSplit(IReadOnlyList<string> argv, string cwd, bool focus, Action<Terminal>? echo) => _launches.OpenLaunchSplit(argv, cwd, focus, echo);
    Pane? IWorktreeLaunches<Pane>.OpenLaunchSplitNextTo(Pane target, IReadOnlyList<string> argv, string cwd, bool focus, Action<Terminal>? echo) =>
        _launches.OpenLaunchSplitNextTo(target, argv, cwd, focus, echo);
    bool IWorktreeLaunches<Pane>.Respawn(Pane pane, string[] argv, string? cwd) => _exitHold.RespawnPane(pane, argv, cwd);
    void IWorktreeLaunches<Pane>.OpenShellTab(string cwd, string? branch) => _launches.OpenShellTab(cwd, branch);
    void IWorktreeRepoPlans.StartRepoPlan(RepoPlan plan, Action<string>? failed, Action? started) => _repoPlans.Start(plan, failed, started);

    Theme IWorktreeWindow<Pane>.OverlayTheme => _overlayTheme;
    bool IWorktreeWindow<Pane>.GitHubEnabled => _github.Enabled;
    bool IWorktreeWindow<Pane>.GitHubSignedIn => _github.SignedIn;
    void IWorktreeWindow<Pane>.DefaultCursor() => SetCursor(_cursorDefault);
    string[] IWorktreeWindow<Pane>.ShortcutFor(ActionSpec action) => _commands.ShortcutFor(action);
    string? IWorktreeWindow<Pane>.IntegrationCwd(Pane pane) => _tracks.Cwd(pane);
    string? IWorktreeWindow<Pane>.OpenDashboardLink(string url) => LinkOpener.Open(url);
    void IWorktreeWindow<Pane>.RunInBackground<T>(Func<T?> work, Action<T?> then, bool wake) where T : class => _jobs.Run(work, then, wake);

    void IWorktreeWindow<Pane>.CloseMenus(long now)
    {
        _menu.Close(now);
        _palette.Close(now);
    }

    // ---- INotificationWindow (Notifications/WindowNotifications.cs) ----

    uint INotificationWindow.Id => Id;
    bool INotificationWindow.IsFocused => _windowFocused;
    int INotificationWindow.FocusedPaneId => _focused.Id;
    int? INotificationWindow.ActiveTabId => _tabs.Active?.Id;
    void INotificationWindow.ApplyBarLayout() => ApplyLayout(animate: true);
    string[] INotificationWindow.ShortcutFor(ActionSpec action) => _commands.ShortcutFor(action);

    void INotificationWindow.CloseOverlaysForPanel(long now)
    {
        _menu.Close(now);
        _palette.Close(now);
        _agents.CloseOverlay(now);
    }

    /// <summary>Settings… on the question bar: the palette, showing just the notification entries.</summary>
    void INotificationWindow.OpenPalette(string filter)
    {
        long now = Now;
        _actions.Execute(new ActionSpec("ActivateCommandPalette", null));
        _palette.HandleText(filter, now);
    }

    /// <summary>The walk over every pane that also feeds the recent directories and commands (WindowFeatures/WindowRecent.cs).</summary>
    void INotificationWindow.CollectFinishedCommands(List<FinishedCommandNotice> into, long now) => _recent.CollectFinishedCommands(into, now);

    void INotificationWindow.DeliverFinishedCommand(int tabIndex, int paneId, string title, string body, long now)
    {
        if (PaneById(paneId) is not { } pane) return;
        bool seen = ReferenceEquals(_tabs.Items[tabIndex], _tabs.Active) && pane == _focused && _windowFocused;
        // From erikterm itself (the ask bar says "erikterm wants to send desktop notifications"); Jump goes to the pane.
        _agents.Deliver(tabIndex, pane, new PaneNotification(NotificationSource.Program, NotificationPriority.Normal, "erikterm", title, body), seen, now); // Agents/WindowAgents.cs
    }
}
