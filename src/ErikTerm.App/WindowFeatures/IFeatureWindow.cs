using ErikTerm.App.Panes;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// What the window's feature classes (git status, GitHub, links, the editor, IPC, …) may read and ask of their window:
/// its panes and tabs, its look, and its menus. TermWindow implements it; each class takes this plus only the roles it
/// needs (<see cref="IPaneLauncher"/>, <see cref="IPaneInput"/>, <see cref="IPointerWindow"/>).
/// </summary>
internal interface IFeatureWindow : IWindowPanes<Pane>, IWindowChrome
{
    uint Id { get; }
    Settings Settings { get; }
    /// <summary>The window has keyboard focus.</summary>
    bool IsFocused { get; }
    /// <summary>The window is the app's focused one.</summary>
    bool IsCurrentWindow { get; }
    /// <summary>The animation duration, in ms.</summary>
    float Anim { get; }
    Theme OverlayTheme { get; }
    TabList<Tab> Tabs { get; }
    /// <summary>Writes the active tab's live layout, focus and zoom back to its <see cref="Tab"/>.</summary>
    void SyncActiveTab();
    Tab? TabById(int id);
    /// <summary>Where the panes go: the window minus padding, tab bar and info bars.</summary>
    RectI ContentBounds();
    int CellWidth { get; }
    int CellHeight { get; }
    /// <summary>The gap between panes, in pixels.</summary>
    int Gap { get; }
    /// <summary>A toast with a button (<paramref name="action"/> null: none).</summary>
    void ToastWithAction(ToastKind kind, string title, string? detail, string actionLabel, Action? action);
    /// <summary>Wakes the event loop (from any thread).</summary>
    void Wake();
    /// <summary>The context menu and the command palette close.</summary>
    void CloseMenus(long now);
    void CloseContextMenu(long now);
    void ClosePalette(long now);
    void OpenPalette(List<Command> commands, CommandPalette.Options options);
    string[] ShortcutFor(ActionSpec action);
    string? ShortcutText(string action);
}

/// <summary>Starting programs: panes, splits and tabs of the window.</summary>
internal interface IPaneLauncher
{
    /// <summary>A new pane's terminal and program, not yet in a layout; throws when the program can't start.</summary>
    Pane SpawnPane(IReadOnlyList<string>? command, string? cwd, RectI area, Action<Terminal>? prepare = null, bool fromTmux = false);
    /// <summary>A split next to the focused pane; null (and a toast) when it couldn't start.</summary>
    Pane? Split(SplitAxis axis, IReadOnlyList<string>? command = null, string? cwd = null, bool focus = true, Action<Terminal>? prepare = null);
    /// <summary>Starts a program in an exited pane; false when it couldn't start.</summary>
    bool RespawnPane(Pane pane, string[] argv, string? cwd);
    void ClosePane(Pane pane);
    /// <summary>A new tab whose shell starts in <paramref name="cwd"/> (or runs <paramref name="command"/>).</summary>
    void SpawnTabIn(string? cwd, string[]? command = null);
    /// <summary>Makes <paramref name="to"/> the live tab, sliding in from <paramref name="dir"/>.</summary>
    void SwitchTo(Tab from, Tab to, int dir);
    /// <summary>Applies a layout change in the other tabs.</summary>
    void ResizeBackgroundTabs();
    /// <summary>default_prog, else the platform's shell.</summary>
    string[] DefaultShell();
    /// <summary>The environment a pane's program gets (TERM, set_environment_variables).</summary>
    Dictionary<string, string> ChildEnvironment();
    /// <summary>ERIKTERM_SOCKET / ERIKTERM_PANE and, with tmux emulation, TMUX / TMUX_PANE.</summary>
    void AddIpcEnvironment(Dictionary<string, string> env, bool tmuxEmulation, int? paneId);
    /// <summary>The key of a held pane's exit bar message (Windowing/ExitHoldFlow.cs).</summary>
    string ExitBarKey { get; }
    /// <summary>What a pane's exit bar records as its owner: this window.</summary>
    object ExitBarOwner { get; }
}

/// <summary>Typing into panes, through the window's input path.</summary>
internal interface IPaneInput
{
    /// <summary>A paste: sanitized, bracketed when the program asked; <paramref name="broadcast"/>: to the broadcast group too.</summary>
    void PasteText(Pane pane, string text, bool broadcast = false);
    void SendInput(Pane pane, string text);
    /// <summary>Text to the other panes of the broadcast group.</summary>
    void BroadcastText(string text);
    /// <summary>The key goes to the focused program as if it weren't bound.</summary>
    void PassThrough(uint key, ushort mod);
}

/// <summary>The pointer: what is under it, the cursor shape, and the debug script's mouse.</summary>
internal interface IPointerWindow : IScriptPointer
{
    Pane? PaneAt(float x, float y);
    (int Col, int Row) CellAt(Pane pane, float x, float y);
    /// <summary>A divider drag, a selection or mouse reporting goes on: hovers underneath stay quiet.</summary>
    bool DragInProgress { get; }
    /// <summary>Whether the pane's program gets the mouse (reporting on and Shift not held).</summary>
    bool WantsMouse(Pane pane);
    void PointerCursor();
    void DefaultCursor();
    void TextCursor();
    void OpenContextMenu(float x, float y, long now);
}
