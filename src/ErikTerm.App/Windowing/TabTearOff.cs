using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// The window's side of moving tabs between windows (App.TearOff.cs drives it): letting a tab go and taking one in,
/// the drop target marker for a tab dragged over from another window, the dragged tab's ghost popup, and the
/// "Move Tab to …" entries in the tab context menu and the palette.
/// </summary>
internal sealed class TabTearOff(
    IWindowHost window, Lock gate, TabList<Tab> tabs, TabBar bar, TabBarController tabBar, WindowTabColors<Tab> tabColors,
    KeyInputRouter keyInput, WindowSearch search, CloseConfirmFlow closeConfirm, ContextMenu menu, PaneSelect paneSelect)
{
    private bool _panesMoved;

    private static long Now => Environment.TickCount64;

    /// <summary>The window emptied because its last tab moved to another window: closing it must not close those panes.</summary>
    public bool PanesMoved => _panesMoved;

    // ==== Moving tabs between windows (App.MoveTab, App.TearOff.cs) ===============================

    /// <summary>Before <paramref name="tab"/> leaves this window: its live state is saved into it and nothing here points at its panes.</summary>
    public bool PrepareTabLeave(Tab tab)
    {
        tabBar.SyncActive();
        keyInput.FlushPendingKey();
        bool barWasShown = tabBar.Shown;
        foreach (var pane in tab.Layout.LeafList) window.ForgetPane(pane);
        if (ReferenceEquals(tab, tabs.Active))
        {
            long now = Now;
            search.Close(resetView: false);
            menu.Close(now);
            paneSelect.Close(now);
            window.ClearDividerHover();
        }
        return barWasShown;
    }

    /// <summary>
    /// After the tab left (the tab list already updated): show the tab that took over, or — when it was the last one — let
    /// App close this window, without closing the panes, which now belong to the other window.
    /// </summary>
    public void FinishTabLeave(bool wasActive, Tab? next, bool barWasShown)
    {
        if (next is null)
        {
            _panesMoved = true;
            window.RequestClose();
            return;
        }
        if (wasActive) tabBar.SwitchTo(next, next, 0);
        if (barWasShown != tabBar.Shown) window.ApplyLayout(animate: true);
        window.Redraw();
    }

    /// <summary>A tab from another window is in this window's list and active: switch to it (resizes its PTYs to this window).</summary>
    public void FinishTabArrive(Tab tab, Tab? previous)
    {
        PrepareMovedPanes(tab);
        int dir = previous is null || tabs.IndexOf(tab) >= tabs.IndexOf(previous) ? 1 : -1;
        tabBar.SwitchTo(previous ?? tab, tab, dir);
        RequestAttention();
    }

    /// <summary>Open hook: the first tab of a window made by tearing a tab off another one.</summary>
    public void AdoptFirstTab(Tab tab)
    {
        if (tabs.IndexOf(tab) < 0) tabs.Add(tab);
        PrepareMovedPanes(tab);
        window.LoadTab(tab);
        lock (gate)
        {
            foreach (var term in window.Layout.LeafList.Select(p => p.Term))
            {
                window.ApplySchemeTo(term);
                term.Dirty = true;
            }
        }
        tab.Activity = false;
        tab.TitleAt = 0;
    }

    /// <summary>
    /// Panes arriving from another window: their cached rows were built with that window's atlas (dropped; RowCache.Config
    /// would notice too), image textures are uploaded again by this window's renderer on first draw, and ApplyLayout
    /// resizes the terminals and PTYs to this window's cell size.
    /// </summary>
    private static void PrepareMovedPanes(Tab tab)
    {
        foreach (var p in tab.Layout.LeafList)
        {
            p.RowCache.Clear();
            p.Links.HoveredUri = null;
            p.Links.Armed = false;
            p.Links.Hovered.Clear();
            p.Focus.Snap(p == tab.Focused ? 1 : 0);
            p.Appear.Snap(1);
            p.Cursor.Touch(Now);
        }
    }

    /// <summary>Asks the window manager to raise / activate this window (on Wayland the compositor may just mark it urgent).</summary>
    public void RequestAttention() => Sdl.RaiseWindow(window.SdlWindow);

    /// <summary>Title for palette / menu entries naming this window: its active tab's.</summary>
    public string MenuTitle
    {
        get
        {
            if (tabs.Active is not { } tab) return "";
            var pane = window.Focused ?? tab.Focused;
            return tab.Title.Length > 0 ? tab.Title : pane?.Label ?? "";
        }
    }

    // ---- Drop target (a tab dragged from another window) -------------------------------------------

    /// <summary>
    /// A tab from another window is over this window at (x, y) (window pixels): shows where it would land and returns that
    /// index, or -1 (and hides the marker) when the pointer isn't over the tab bar's band at the top.
    /// </summary>
    public int ShowDropTarget(float x, float y)
    {
        int index = DropIndexAt(x, y);
        if (bar.SetDropIndicator(tabBar.Shown ? index : -1)) window.Redraw();
        return index;
    }

    public void HideDropTarget()
    {
        if (bar.SetDropIndicator(-1)) window.Redraw();
    }

    /// <summary>Insertion index for a tab dropped at (x, y), or -1 outside the bar's band (the top strip, shown or not).</summary>
    public int DropIndexAt(float x, float y)
    {
        var (w, h) = window.WindowPixels();
        var pad = window.ScaledPadding();
        var theme = window.Theme;
        float band = pad.Top + TabBar.Height(theme) + theme.S(8);
        if (x < 0 || x >= w || y < 0 || y >= Math.Min(h, band)) return -1;
        return tabBar.Shown ? bar.InsertionIndex(x) : tabs.Count;
    }

    // ---- The dragged tab's ghost outside this window (App.TearOff.cs) ------------------------------

    public TabGhost? CreateTabGhost(int x, int y) =>
        bar.Ghost is { } g ? TabGhost.Create(window.SdlWindow, window.Fonts, g.W, g.H, window.Density, x, y) : null;

    // ---- Menus ---------------------------------------------------------------------------------

    /// <summary>Right click on a tab.</summary>
    public void OpenTabMenu(Tab tab, float x, float y, long now)
    {
        var items = new List<ContextMenu.Item>
        {
            new("New Tab", Icons.Add, window.ShortcutText("SpawnTab"), tabBar.SpawnTab),
            new("Rename Tab…", Icons.Edit, null, () => { tabBar.ActivateTabIndex(tabs.IndexOf(tab)); tabBar.BeginRename(); }, tabBar.Shown),
            new("Set Tab Color…", Icons.Color, null, () => { tabBar.ActivateTabIndex(tabs.IndexOf(tab)); tabColors.OpenPicker(); }), // Tabs/WindowTabColors.cs
            ContextMenu.Item.Separator,
            new("Move Tab to New Window", Icons.Window, window.ShortcutText("MoveTabToNewWindow"), () => window.MoveTabToNewWindow(tab), window.CanMoveTab(tab)),
        };
        foreach (var other in window.OtherWindows())
        {
            var target = other;
            items.Add(new($"Move Tab to Window {target.Id}: {Ellipsize(target.MenuTitle, 32)}", Icons.Window, null, () => window.MoveTab(tab, target)));
        }
        items.Add(ContextMenu.Item.Separator);
        items.Add(new("Close Tab", Icons.Close, window.ShortcutText("CloseCurrentTab"), () => closeConfirm.RequestCloseTab(tab)));
        menu.Open(items, x, y, window.Theme, now);
    }

    private static string Ellipsize(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Palette hook (BuildCommands): "Move Tab to New Window" and "Move Tab to Window N".</summary>
    public void AddCommands(List<Command> list)
    {
        if (tabs.Active is not { } active) return;
        if (ActionSpec.TryParse("MoveTabToNewWindow", out var spec, out _))
            list.Add(new Command
            {
                Id = "MoveTabToNewWindow", Category = "Window", Title = "Move Tab to New Window", Icon = Icons.Window, Action = spec,
                IsEnabled = () => window.CanMoveTab(active), Shortcut = window.ShortcutFor(spec),
            });
        foreach (var other in window.OtherWindows())
        {
            string action = $"MoveTabToWindow({other.Id})";
            if (!ActionSpec.TryParse(action, out var to, out _)) continue;
            list.Add(new Command
            {
                Id = action, Category = "Window", Title = $"Move Tab to Window {other.Id}: {other.MenuTitle}", Icon = Icons.Window, Action = to,
                Shortcut = window.ShortcutFor(to),
            });
        }
    }
}
