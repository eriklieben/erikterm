using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.App.Vim;

/// <summary>What vim mode reads and sets on a pane: <see cref="Pane"/> implements it, tests use a fake.</summary>
internal interface IVimPane : IPaneView
{
    /// <summary>Vim mode while it's on for this pane; null otherwise.</summary>
    VimEngine? Vim { get; set; }
    Selection? Selection { get; set; }
    /// <summary>The pane's place in the layout (not the animating rectangle), for edge scrolling.</summary>
    RectI Target { get; }
    /// <summary>Where the pill and the prompt draw: the pane's rectangle above the teammate strip and the exit bar.</summary>
    RectF OverlayArea(long now);
    /// <summary>Drops the smooth-scroll offset (leaving vim mode to the live screen).</summary>
    void SnapScroll();
    string? AgentName { get; }
    string? TmuxTitle { get; }
    /// <summary>The program in the foreground isn't the pane's own shell.</summary>
    bool RunsProgram { get; }
}

/// <summary>What vim mode needs of the search bar's engine and highlights: <see cref="SearchBar"/> implements it.</summary>
internal interface IVimSearch<TPane> where TPane : class
{
    TPane? Pane { get; }
    bool IsVim { get; }
    string? VimError { get; }
    TerminalSearch Engine { get; }
    void BeginVim(TPane pane, string pattern, Theme th, long now);
    void EndVim(long now);
    void CompleteScan(Terminal t, double budgetMs = 60);
    void SetCurrent(SearchMatch? match, long now);
}

/// <summary>The window's pane and tab commands vim mode runs.</summary>
internal interface IVimCommands<TPane> where TPane : class
{
    void Split(SplitAxis axis, IReadOnlyList<string>? command);
    /// <summary>Runs argv in a split to the right, started where the focused pane's tool would start.</summary>
    void SplitRun(string[] argv);
    void ClosePane(TPane pane);
    void RequestClosePane(TPane pane);
    void CloseActiveTab(bool force);
    void SpawnTab();
    bool MovePaneToNewTab(TPane pane);
    void ActivateDirection(Direction direction);
    /// <summary>Grows the focused pane by a number of cells along an axis; false when it can't.</summary>
    bool GrowFocused(SplitAxis axis, int cells);
    void Execute(ActionSpec action, uint key, ushort mod);
    void PasteText(TPane pane, string text);
    /// <summary>The pane an IPC request names (any tab) and its tab's index, or throws <see cref="IpcException"/>.</summary>
    (int TabIndex, TPane Pane) IpcTarget(IpcRequest request);
    int IpcTab(IpcRequest request);
}

/// <summary>Finding and opening what vim's cursor is on: links, files, Neovim in the tab.</summary>
internal interface IVimOpeners<TPane> where TPane : class
{
    bool FindLinkAt(TPane pane, long line, int col, out string uri, out SmartLink? smart);
    void OpenSmartLink(TPane pane, SmartLink link);
    void OpenLink(string uri);
    string? PreferredEditor();
    (TPane Pane, string Server)? RunningNvim();
    /// <summary>Runs <paramref name="work"/> off the main thread, then <paramref name="then"/> on it.</summary>
    void RunInBackground(Func<string?> work, Action<string?> then);
}

/// <summary>The clipboard and the primary selection.</summary>
internal interface IVimClipboard
{
    void SetClipboard(string text);
    void SetPrimary(string text);
    string Clipboard();
    string Primary();
}

/// <summary>What <see cref="WindowVim{TPane}"/> needs from its window: TermWindow implements it, tests use a fake.</summary>
internal interface IVimWindow<TPane> : IWindowPanes<TPane>, IWindowChrome, IVimCommands<TPane>, IVimOpeners<TPane>, IVimClipboard
    where TPane : class, IPaneView
{
    Theme OverlayTheme { get; }
    Theme PaneOverlayTheme(TPane pane, long now);
    bool TabBarEditing { get; }
    /// <summary>The search bar and the context menu give way to vim's prompts.</summary>
    void CloseBarsForVim(long now);
    /// <summary>The cell under window coordinates (column, row within the pane).</summary>
    (int Col, int Row) CellAt(TPane pane, float x, float y);
    string? ShortcutText(string action);
    bool QuickSelectOpen { get; }
    void OpenHints(long now);
    /// <summary>Closes the hints if they're open on this pane.</summary>
    void CloseHints(TPane pane, long now);
    ColorScheme? SchemePreview { get; set; }
    void ApplyScheme();
    /// <summary>The debug script's keys (vim:KEYS), through the window's real routing.</summary>
    void ScriptKeyDown(uint key, ushort mod, long now);
    void ScriptTextInput(string text, long now);
}

/// <summary>What <see cref="WindowVim{TPane}"/> needs from the process: App implements it, tests use a fake.</summary>
internal interface IVimProcess
{
    Dictionary<KeyChord, ActionSpec> Keys { get; }
    List<ColorScheme> AllSchemes();
    void SetSchemeOverride(ColorScheme scheme);
    void ApplySchemeToAllWindows();
    void AddSessionOverride(ConfigOverride setting);
}
