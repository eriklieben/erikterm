using ErikTerm.App.Containers;
using ErikTerm.App.Integrations;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Session;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>The SDL window itself, for the parts that size, place or hit-test it: <see cref="TermWindow"/> implements it.</summary>
internal interface IWindowSurface : IWindowChrome
{
    /// <summary>The process's current settings (replaced on a config reload).</summary>
    Settings Settings { get; }
    nint SdlWindow { get; }
    nint Renderer { get; }
    /// <summary>How large text and chrome are drawn (the display's scale).</summary>
    float Scale { get; }
    /// <summary>Device pixels per window point: SDL's coordinates to ours.</summary>
    float Density { get; }
    (int W, int H) WindowPixels();
    Padding ScaledPadding();
}

/// <summary>
/// What the window's parts ask of the git / integrations side of the window (links, agents, worktrees, teammates,
/// containers, tmux, notifications, snippets, recent, review, OSC 52). <see cref="TermWindow"/> implements each one by
/// calling the member of the same name, so this is the whole seam between the two halves.
/// </summary>
internal interface IWindowIntegrations
{
    /// <summary>The info bar along the window's top (question and notification bars, prompts).</summary>
    InfoBar InfoBar { get; }
    /// <summary>Runs <paramref name="work"/> on the main thread at the next tick (from any thread).</summary>
    void Post(Action work);
    void CopyText(string text, string what);
    string? PreferredEditor();
    Tab? SpawnToolTab(string[] argv, string cwd, string errorTitle);
    int Osc52WakeMs(long now);

    // Actions and commands
    void ExecuteShellAction(ActionSpec action, uint key, ushort mod);
    void RunAgentAction(string name);
    void RunWorktreeAction(string name);
    void OpenReview(bool pickBase);
    void RunTeammateToggle(string arg);
    void RunNotificationsAction(string name);
    void SendSnippetNamed(string name);
    void SendAgentNewline(uint key, ushort mod);
    void AddShellCommands(Action<string, string, int, string, Func<bool>?> add);
    void AddAgentCommands(Action<string, string, int, string, Func<bool>?> add);
    /// <summary>Snippets, recent, teammates, notifications, git, worktrees, review and containers, in the palette's order.</summary>
    void AddIntegrationCommandGroup(List<Command> list);
    /// <summary>tmux, then the process's session and config entries, in the palette's order.</summary>
    void AddTmuxAndProcessCommands(List<Command> list);
    void AddShellMenuItems(List<ContextMenu.Item> items, float x, float y);
    bool HasAgentTargets { get; }
    void OpenAgentSend();
    bool JumpToPane(Pane pane);
    bool IsRealTmux(Pane pane);

    // Tabs
    string AgentTabLabel(Pane pane, string label);
    string ContainerTitle(PaneContainer container, Pane pane);
    bool TabInContainer(Tab tab);
    RepoBadgeModel? TabRepoBadge(Tab tab);
    string[]? ContainerShellFor(Pane from);

    // Exited panes
    bool ContinueWorktreeLaunch(Pane pane);
    bool ContinueRepoPlan(Pane pane);
    bool RepoPlanKeyDown(Pane pane, uint key);

    // Session
    string[]? RestoreContainerCommand(SessionContainer? saved);
    SessionContainer? CaptureContainer(Pane pane);
    void ConsiderAgentResume(Pane pane, SessionPane saved);
    SessionAgent? CaptureAgentResume(Pane pane);

    // Teammates and layout
    void SyncTeammates(LayoutTree<Pane> layout);
    int TeammateStripHeight(Pane pane, RectI r);
    void SizeParkedTeammates(LayoutTree<Pane> layout, RectI bounds, bool background);
    bool SizeTerminal(Pane pane, RectI r, int strip);

    // Debug script steps
    bool RunDropScriptStep(string kind, string arg, long now);
    bool RunTeammateScriptStep(string kind, string arg, long now);
    void RunShellScriptStep(string kind, string arg, long now);
}

/// <summary>
/// What the classes TermWindow is made of (chrome, tabs, search, session, input, actions…) may ask of their window:
/// <see cref="TermWindow"/> implements it. The pane and tab basics come from <see cref="IWindowPanes{TPane}"/>.
/// </summary>
internal interface IWindowHost : IWindowSurface, IWindowPanes<Pane>, ITabSync, IWindowIntegrations
{
    Dictionary<KeyChord, ActionSpec> Keys { get; }
    string ConfigPath { get; }
    int ConfigProblemCount { get; }
    bool IsWayland { get; }
    TabList<Tab> Tabs { get; }
    bool TabBarShown { get; }
    /// <summary>animation_duration_ms (0: no animations).</summary>
    float AnimationMs { get; }
    bool SmoothScrolling { get; }
    bool IsWindowFocused { get; }
    int Gap { get; }
    FontSet Fonts { get; }
    GlyphAtlas Atlas { get; }
    float FontZoom { get; set; }
    /// <summary>Toggle Transparency made the window opaque.</summary>
    bool Opaque { get; }
    IReadOnlyList<(Pane Pane, RectI Rect)> PaneRects { get; }
    int WindowCount { get; }

    // Colors
    ColorScheme Scheme { get; }
    ColorScheme? SchemePreview { get; set; }
    ColorScheme? SchemeOverride { get; set; }
    List<ColorScheme> AllSchemes();
    void ApplySchemeTo(Terminal term);
    void ApplySchemeToAllWindows();
    void ReloadSchemesQuietly();
    bool ThemeDownloadRunning { get; set; }

    // Layout and panes
    RectI ContentBounds();
    void ApplyLayout(bool animate, float durationMs);
    void ApplyFontSize();
    Pane SpawnPane(IReadOnlyList<string>? command, string? cwd, RectI area, Action<Terminal>? prepare = null);
    Pane? Split(SplitAxis axis, IReadOnlyList<string>? command = null);
    void ClosePane(Pane pane);
    Pane? PaneAt(float x, float y);
    /// <summary>The pane closed or leaves the window: nothing here may point at it any more.</summary>
    void ForgetPane(Pane pane);
    /// <summary>Takes <paramref name="pane"/> out of the active tab's split (unzooming first), fixing focus and pointer state.</summary>
    void TakePaneOut(Pane pane);
    Theme PaneOverlayTheme(Pane? pane, long now);
    Dictionary<string, string> PaneEnvironment(int paneId, string? cwd);
    string[] WithShellIntegration(string[] argv, Dictionary<string, string> env);
    void Wake();

    // Tabs becoming the live state
    /// <summary>The tab's layout, focus and zoom become the window's (session restore, a tab adopted from another window).</summary>
    void LoadTab(Tab tab);
    /// <summary>Like <see cref="LoadTab"/> for a tab switch: focus reports, colors and settings refreshed, laid out, focus kept off parked teammates.</summary>
    void EnterTab(Tab tab);
    void CloseTab(Tab tab);
    /// <summary>The last pane of the last tab closed: the user is done with this window.</summary>
    void LastPaneClosed();
    /// <summary>The last tab was removed: the window closes (and says the user closed it).</summary>
    void LastTabClosed();
    /// <summary>App closes the window after this loop iteration.</summary>
    void RequestClose();
    bool RequestCloseWindow();

    // Input
    void Execute(ActionSpec action);
    void SendInput(Pane pane, string text);
    void WriteInput(Pane pane, ReadOnlySpan<byte> data);
    void PasteText(Pane pane, string text, bool broadcast);
    void PassThrough(uint key, ushort mod);
    void Copy(bool toClipboard, bool toPrimary);
    bool WantsMouse(Pane pane);
    /// <summary>A divider drag, a selection or a mouse report is under way (bars and pills don't hover then).</summary>
    bool PointerBusy { get; }
    /// <summary>A tab drag or rename, a menu or the tab switcher keeps the floating chrome out wherever the pointer is.</summary>
    bool ChromeHeldOut { get; }
    void ResetPointer();
    void ClearDividerHover();
    void SetCursor(nint cursor);
    nint CursorDefault { get; }
    nint CursorPointer { get; }
    void KeyDown(uint key, ushort mod, long now);
    void MouseMove(float x, float y, long now);
    void MouseDown(byte button, byte clicks, float x, float y, long now);
    void MouseUp(byte button, float x, float y, long now);
    string[] ShortcutFor(ActionSpec action);
    string? ShortcutText(string action);
    void OpenConfigFile();
    void ReloadConfiguration();
    void CloseOverlaysAfterError(long now);

    // Other windows (App.TearOff.cs, App.Process.cs)
    void SpawnWindow();
    bool CanDetachTab();
    void OnTabDragged(float x, float y);
    void OnTabDropped(Tab tab, float x, float y);
    bool CanMoveTab(Tab tab);
    void MoveTab(Tab tab, TermWindow target);
    void MoveTabToNewWindow(Tab tab);
    void MoveTabToWindow(Tab tab, int windowId);
    List<TermWindow> OtherWindows();
}
