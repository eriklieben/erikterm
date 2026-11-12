using ErikTerm.App;
using ErikTerm.App.Panes;
using ErikTerm.App.Session;
using ErikTerm.App.Teammates;
using ErikTerm.App.Ui;
using ErikTerm.App.WindowFeatures;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>
/// A window for the classes in src/ErikTerm.App/WindowFeatures, without SDL: real tabs and layouts of real
/// <see cref="Pane"/>s (<see cref="TestPanes"/>), and a record of what the feature asked of the window — toasts, the
/// palette it opened, text it typed into panes, panes and tabs it started. The active tab's state lives on its
/// <see cref="Tab"/> (there is no separate live layout to sync). Dispose it to hang up every pane it made.
/// </summary>
internal sealed class FakeFeatureWindow : IRecentWindow, IIpcWindow, ITmuxWindow, IReviewWindow, ILinkWindow, IPaneLauncher, IPaneInput,
    ITeammateWindow<Pane>, IDisposable
{
    private readonly List<Pane> _owned = [];
    private readonly List<Pane> _visible = [];

    public FakeFeatureWindow(Settings? settings = null, string? text = null)
    {
        Settings = settings ?? new Settings();
        AddTab(text);
    }

    // ---- What the feature did ------------------------------------------------------------------

    public readonly List<(ToastKind Kind, string Title, string? Detail)> Toasts = [];
    public readonly List<(Pane Pane, string Text, bool Broadcast)> Pastes = [];
    public readonly List<(Pane Pane, string Text)> Inputs = [];
    public readonly List<string> Broadcasts = [];
    public readonly List<(uint Key, ushort Mod)> PassedThrough = [];
    public readonly List<string> TabsSpawnedIn = []; // "" for no directory
    public readonly List<(SplitAxis Axis, IReadOnlyList<string>? Command, string? Cwd, bool Focus)> Splits = [];
    public readonly List<(IReadOnlyList<string>? Command, string? Cwd)> Spawned = [];
    public readonly List<(Tab From, Tab To, int Dir)> Switches = [];
    public readonly List<string> Calls = [];
    public (List<Command> Commands, CommandPalette.Options Options)? Palette;
    public int Redraws, Wakes;

    /// <summary>Makes SpawnPane fail like a program that can't start.</summary>
    public Exception? SpawnFails;
    /// <summary>What Split returns: a new pane (default) or null, as when it couldn't start.</summary>
    public bool SplitFails;

    // ---- Building it ---------------------------------------------------------------------------

    public Pane NewPane(string? text = null)
    {
        var pane = TestPanes.Spawn(text: text);
        _owned.Add(pane);
        return pane;
    }

    /// <summary>A new tab with one pane, made active.</summary>
    public Tab AddTab(string? text = null)
    {
        var pane = NewPane(text);
        var tab = new Tab { Layout = new LayoutTree<Pane>(pane), Focused = pane };
        Tabs.Activate(Tabs.Add(tab));
        return tab;
    }

    /// <summary>A second pane next to the focused one in the active tab.</summary>
    public Pane SplitActive(string? text = null)
    {
        var pane = NewPane(text);
        Layout.Split(Focused, pane, SplitAxis.Horizontal);
        return pane;
    }

    public void Dispose()
    {
        foreach (var p in _owned) p.Dispose();
    }

    // ---- IWindowPanes / IWindowChrome ----------------------------------------------------------

    public Tab ActiveTab => Tabs.Active!;
    public Pane Focused => ActiveTab.Focused;
    public Pane? Zoomed => ActiveTab.Zoomed;
    public LayoutTree<Pane> Layout => ActiveTab.Layout;
    public int ShownPaneCount => Layout.LeafList.Count(p => !p.LayoutHidden);
    public int TabCount => Tabs.Count;
    public int ActiveTabIndex => Tabs.ActiveIndex;
    public LayoutTree<Pane> TabLayout(int tabIndex) => Tabs.Items[tabIndex].Layout;
    public List<Pane> VisiblePanes()
    {
        _visible.Clear();
        _visible.AddRange(Zoomed is { } z ? [z] : Layout.LeafList);
        return _visible;
    }
    public Pane? FindPane(int id)
    {
        foreach (var tab in Tabs.Items)
            foreach (var p in tab.Layout.LeafList)
                if (p.Id == id) return p;
        return null;
    }
    public void Focus(Pane pane) => ActiveTab.Focused = pane;
    public void SetZoom(Pane? pane) => ActiveTab.Zoomed = pane;
    public void ApplyLayout(bool animate) => Calls.Add($"ApplyLayout({animate})");
    public void ActivateTab(int index) => Tabs.Activate(index);
    public void ClearTabZoom(int tabIndex) => Tabs.Items[tabIndex].Zoomed = null;
    public Theme Theme { get; } = new();
    public bool SuppressText { get; set; }
    public void Redraw() => Redraws++;
    public void Toast(ToastKind kind, string title, string? detail = null) => Toasts.Add((kind, title, detail));

    // ---- IFeatureWindow ------------------------------------------------------------------------

    public uint Id { get; set; } = 1;
    public Settings Settings { get; set; }
    public bool IsFocused { get; set; } = true;
    public bool IsCurrentWindow { get; set; } = true;
    public float Anim => Theme.AnimationMs;
    public Theme OverlayTheme => Theme;
    public TabList<Tab> Tabs { get; } = new();
    public void SyncActiveTab() { }
    public Tab? TabById(int id) => Tabs.Items.FirstOrDefault(t => t.Id == id);
    public RectI ContentBounds() => new(0, 0, 800, 600);
    public int CellWidth => 10;
    public int CellHeight => 20;
    public int Gap => 4;
    public void ToastWithAction(ToastKind kind, string title, string? detail, string actionLabel, Action? action) => Toasts.Add((kind, title, detail));
    public void Wake() => Wakes++;
    public void CloseMenus(long now) => Calls.Add("CloseMenus");
    public void CloseContextMenu(long now) => Calls.Add("CloseContextMenu");
    public void ClosePalette(long now) => Calls.Add("ClosePalette");
    public void OpenPalette(List<Command> commands, CommandPalette.Options options) => Palette = (commands, options);
    public string[] ShortcutFor(ActionSpec action) => [];
    public string? ShortcutText(string action) => null;

    // ---- The feature-specific windows ----------------------------------------------------------

    public RecentHistory? Recent { get; set; }
    public bool ForgetResult = true;
    public bool ForgetRecent()
    {
        Calls.Add("ForgetRecent");
        return ForgetResult;
    }
    public void SetTitleUntilProgramSets(string title) => Calls.Add("Title " + title);
    public bool TmuxShimAvailable { get; set; } = true;
    public bool JumpToReview(string root) { Calls.Add("JumpToReview " + root); return false; }
    public string? FocusedWorktreeCwd() => null;
    public bool OverlayOpen { get; set; }
    public void VimHintChosen(QuickSelect.Item item, bool yank, bool open) => Calls.Add("VimHintChosen");

    // ---- ITeammateWindow (for WindowTeammates, which WindowTmux and WindowIpc take) -------------

    public ColorScheme Scheme => ColorSchemes.Erik;
    public int LayoutGap => Gap;
    public Pane TabFocused(int tabIndex) => Tabs.Items[tabIndex].Focused;
    public void SetTabFocused(int tabIndex, Pane pane) => Tabs.Items[tabIndex].Focused = pane;
    public Pane SpawnTeammate(string[]? argv, string? cwd, RectI area) => SpawnPane(argv, cwd, area, fromTmux: true);
    public bool SizeTerminal(Pane pane, RectI r, int strip) => false;
    public void ParkedResized(Pane pane, bool background) => Calls.Add("ParkedResized");
    public void DropZoom(long now) => ActiveTab.Zoomed = null;

    // ---- IPaneLauncher -------------------------------------------------------------------------

    public Pane SpawnPane(IReadOnlyList<string>? command, string? cwd, RectI area, Action<Terminal>? prepare = null, bool fromTmux = false)
    {
        if (SpawnFails is { } ex) throw ex;
        Spawned.Add((command, cwd));
        var pane = NewPane();
        prepare?.Invoke(pane.Term);
        return pane;
    }
    public Pane? Split(SplitAxis axis, IReadOnlyList<string>? command = null, string? cwd = null, bool focus = true, Action<Terminal>? prepare = null)
    {
        Splits.Add((axis, command, cwd, focus));
        if (SplitFails) return null;
        var pane = NewPane();
        Layout.Split(Focused, pane, axis);
        if (focus) Focus(pane);
        return pane;
    }
    public bool RespawnPane(Pane pane, string[] argv, string? cwd) { Calls.Add("Respawn " + string.Join(' ', argv)); return true; }
    public void ClosePane(Pane pane) => Calls.Add("ClosePane " + pane.Id);
    public void SpawnTabIn(string? cwd, string[]? command = null) => TabsSpawnedIn.Add(cwd ?? "");
    public void SwitchTo(Tab from, Tab to, int dir)
    {
        Switches.Add((from, to, dir));
        Tabs.Activate(to);
    }
    public void ResizeBackgroundTabs() => Calls.Add("ResizeBackgroundTabs");
    public string[] DefaultShell() => ["/bin/sh"];
    public Dictionary<string, string> ChildEnvironment() => [];
    public void AddIpcEnvironment(Dictionary<string, string> env, bool tmuxEmulation, int? paneId) { }
    public string ExitBarKey => "exit";
    public object ExitBarOwner => this;

    // ---- IPaneInput ----------------------------------------------------------------------------

    public void PasteText(Pane pane, string text, bool broadcast = false) => Pastes.Add((pane, text, broadcast));
    public void SendInput(Pane pane, string text) => Inputs.Add((pane, text));
    public void BroadcastText(string text) => Broadcasts.Add(text);
    public void PassThrough(uint key, ushort mod) => PassedThrough.Add((key, mod));

    // ---- IPointerWindow ------------------------------------------------------------------------

    public Pane? PointerPane;
    public void ScriptMouseMove(float x, float y, long now) => Calls.Add("ScriptMouseMove");
    public void ScriptClick(float x, float y, long now) => Calls.Add("ScriptClick");
    public Pane? PaneAt(float x, float y) => PointerPane;
    public (int Col, int Row) CellAt(Pane pane, float x, float y) => ((int)(x / CellWidth), (int)(y / CellHeight));
    public bool DragInProgress { get; set; }
    public bool WantsMouse(Pane pane) => false;
    public void PointerCursor() => Calls.Add("PointerCursor");
    public void DefaultCursor() => Calls.Add("DefaultCursor");
    public void TextCursor() => Calls.Add("TextCursor");
    public void OpenContextMenu(float x, float y, long now) => Calls.Add("OpenContextMenu");
}
