using ErikTerm.App.Chrome;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// A window's tabs: the tab bar (Ui/TabBar.cs) and what its buttons do, opening, switching (the old tab slides out
/// under the new one), closing and reaping tabs, their titles, and the Tab entries in the palette. The active tab's
/// layout, focus and zoom are the window's live state (<see cref="IWindowHost.EnterTab"/>); background tabs are kept in
/// step by <see cref="BackgroundTabs"/>.
/// </summary>
#pragma warning disable S107 // TermWindow's wiring: each part takes the peers it calls, so its dependencies stay visible; a bundle of them would only hide that
internal sealed class TabBarController(
    IWindowHost window, TabList<Tab> tabs, TabBar bar, FloatingChrome chrome, WindowTabSwitcher<Tab> switcher,
    WindowTabColors<Tab> tabColors, PaneRenderer renderer, BackgroundTabs background, WindowProgress progress, WindowSearch search,
    CloseConfirmFlow closeConfirm, ExitHoldFlow exitHold, ContextMenu menu, PaneSelect paneSelect)
#pragma warning restore S107
{
    private static readonly string HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private Tab? _outgoing;            // the tab sliding out during a switch
    private long _outgoingUntil;

    private static long Now => Environment.TickCount64;

    /// <summary>The tab sliding out during a switch (its panes are left where the animation puts them).</summary>
    public Tab? Outgoing => _outgoing;

    /// <summary>The tab bar this controls, the floating window bar it rides in, and the Ctrl+Tab switcher.</summary>
    public TabBar Bar => bar;
    public FloatingChrome Floating => chrome;
    public WindowTabSwitcher<Tab> Switcher => switcher;

    /// <summary>
    /// Whether the bar has a place in the layout. A floating bar always does: it is hidden by sliding above the
    /// window's top edge (<see cref="FloatingChrome"/>), not by leaving the model, so its tabs are already laid
    /// out and in the right order the moment it comes down, and the hit test can ask where they are.
    /// </summary>
    public bool Shown
    {
        get
        {
            var settings = window.Settings;
            return settings.EnableTabBar && (chrome.Overlay || tabs.Count > 1 || !settings.HideTabBarIfOnlyOneTab);
        }
    }

    /// <summary>
    /// Pixels reserved above the panes for the tab bar. A floating bar reserves only the strip the window grew for
    /// it, and exactly that: the window is taller by the same number of pixels, so the panes come out the size they
    /// already were and no terminal is resized (<see cref="FloatingChrome"/>).
    /// </summary>
    public int Reserve()
    {
        var theme = window.Theme;
        int row = Shown ? (int)MathF.Round(TabBar.Height(theme) + theme.S(4)) : 0;
        // A grown window's strip holds the window bar; the tab row comes below it (FloatingChrome).
        return chrome.Overlay ? row + chrome.StripReserve : row;
    }

    /// <summary>Open hook: the first tab (unless restored or adopted) and the bar's buttons. <paramref name="tearOff"/> has the tab menu.</summary>
    public void Init(TabTearOff tearOff)
    {
        if (tabs.Count == 0) tabs.Add(new Tab { Layout = window.Layout, Focused = window.Focused }); // else restored (App.Session.cs)
        bar.Activate = id => { if (TabById(id) is { } t) ActivateTabIndex(tabs.IndexOf(t)); };
        bar.Close = id => { if (TabById(id) is { } t) closeConfirm.RequestCloseTab(t); };
        bar.New = () => SpawnTab();
        bar.Move = (id, to) =>
        {
            if (TabById(id) is { } t) tabs.Move(tabs.IndexOf(t), to);
            window.Redraw();
        };
        bar.Rename = (id, title) => { TabById(id)?.Rename(title); window.Redraw(); };
        // Tear-off: drag a tab out of the bar into a window of its own, or onto another window's bar (App.TearOff.cs).
        bar.CanDetach = window.CanDetachTab;
        bar.DragOutside = (id, x, y) => { if (TabById(id) is not null) window.OnTabDragged(x, y); };
        bar.DropOutside = (id, x, y) => { if (TabById(id) is { } t) window.OnTabDropped(t, x, y); };
        bar.Menu = (id, x, y) => { if (TabById(id) is { } t) tearOff.OpenTabMenu(t, x, y, Now); };
        bar.More = switcher.OpenFromBar; // the "⌄ N more" button (Tabs/WindowTabSwitcher.cs)
    }

    public Tab? TabById(int id)
    {
        foreach (var t in tabs.Items) if (t.Id == id) return t;
        return null;
    }

    /// <summary>Copies the live layout state back into the active tab.</summary>
    public void SyncActive()
    {
        if (tabs.Active is not { } tab) return;
        tab.Layout = window.Layout;
        tab.Focused = window.Focused;
        tab.Zoomed = window.Zoomed;
    }

    /// <summary>Takes the pane out of its split into a tab of its own (vim's Ctrl-w T); the program keeps running.</summary>
    public bool MovePaneToNewTab(Pane pane)
    {
        var layout = window.Layout;
        if (!layout.Contains(pane)) return false;
        if (layout.Count < 2) { window.Toast(ToastKind.Info, "Already alone in its tab"); return false; }
        window.TakePaneOut(pane);
        SyncActive();
        var from = tabs.Active!;
        var tab = new Tab { Layout = new LayoutTree<Pane>(pane), Focused = pane };
        tabs.Add(tab, afterActive: true);
        SwitchTo(from, tab, +1);
        return true;
    }

    /// <summary>A new tab where the focused pane is: in its container too, when it is in one (WindowFeatures/WindowContainers.cs).</summary>
    public void SpawnTab()
    {
        var focused = window.Focused;
        SpawnTabIn(focused.StartDirectory(window.Settings.DefaultCwd), window.ContainerShellFor(focused));
    }

    /// <summary>A new tab whose shell starts in <paramref name="cwd"/> (or runs <paramref name="command"/>: a shell in a container).</summary>
    public void SpawnTabIn(string? cwd, string[]? command = null)
    {
        SyncActive();
        var from = tabs.Active!;
        var tab = new Tab();
        tabs.Add(tab); // before sizing the pane: the bar may appear and take room
        Pane pane;
        try
        {
            pane = window.SpawnPane(command, cwd, window.ContentBounds());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            tabs.Remove(tab);
            tabs.Activate(from);
            window.Toast(ToastKind.Error, "Couldn't open a new tab", ex.Message);
            return;
        }
        tab.Layout = new LayoutTree<Pane>(pane);
        tab.Focused = pane;
        SwitchTo(from, tab, +1);
    }

    public void ActivateTabIndex(int index)
    {
        if (index < 0 || index >= tabs.Count || index == tabs.ActiveIndex) return;
        SyncActive();
        var from = tabs.Active!;
        int dir = Math.Sign(index - tabs.ActiveIndex);
        tabs.Activate(index);
        SwitchTo(from, tabs.Active!, dir);
    }

    public void ActivateTabRelative(int delta)
    {
        if (tabs.Count <= 1) return;
        int n = tabs.Count;
        ActivateTabIndex(((tabs.ActiveIndex + delta) % n + n) % n);
    }

    public void CloseTab(Tab tab)
    {
        SyncActive();
        bool barWasShown = Shown;
        foreach (var pane in tab.Layout.LeafList)
        {
            window.ForgetPane(pane);
            pane.Dispose();
        }
        RemoveTab(tab);
        if (barWasShown != Shown && tabs.Active is not null) window.ApplyLayout(animate: true);
    }

    /// <summary>Removes a tab whose panes are already disposed, switching away if it was active.</summary>
    private void RemoveTab(Tab tab)
    {
        bool wasActive = ReferenceEquals(tab, tabs.Active);
        int oldIndex = tabs.IndexOf(tab);
        var next = tabs.Remove(tab);
        if (next is null) { window.LastTabClosed(); return; }
        if (wasActive) SwitchTo(tab, next, tabs.IndexOf(next) >= oldIndex ? +1 : -1);
        window.Redraw();
    }

    /// <summary>Hook from ClosePane: the active tab's last pane closed. Returns false when it was the last tab (quit).</summary>
    public bool OnLastPaneClosed()
    {
        if (tabs.Count <= 1) { window.LastPaneClosed(); return false; }
        bool barWasShown = Shown;
        RemoveTab(tabs.Active!);
        if (barWasShown != Shown) window.ApplyLayout(animate: true);
        return true;
    }

    /// <summary>The pane closed or left the window: the outgoing tab's slide stops if it was in it.</summary>
    public void ForgetOutgoing(Pane pane)
    {
        if (_outgoing is not null && _outgoing.Layout.Contains(pane)) _outgoing = null;
    }

    /// <summary>Makes <paramref name="to"/> the live tab; the old one slides out while the new one slides in from <paramref name="dir"/>.</summary>
    public void SwitchTo(Tab from, Tab to, int dir)
    {
        long now = Now;
        search.Close(resetView: false);
        menu.Close(now);
        paneSelect.Close(now);
        window.ResetPointer();
        window.EnterTab(to);

        float duration = window.AnimationMs * 1.25f;
        if (duration > 0 && !ReferenceEquals(from, to))
        {
            float slide = window.Theme.S(40) * dir;
            foreach (var p in window.Layout.LeafList)
            {
                var target = p.Rect.Target;
                p.Rect.Snap(target.Offset(slide, 0));
                p.Rect.Set(target, now, duration);
                p.Appear.Snap(0);
                p.Appear.Set(1, now, duration);
            }
            RestoreOutgoing();
            _outgoing = from;
            _outgoingUntil = now + (long)duration;
            foreach (var p in from.Layout.LeafList)
            {
                p.Rect.Set(p.Rect.Value(now).Offset(-slide, 0), now, duration);
                p.Appear.Set(0, now, duration * 0.55f);
            }
        }
        background.MarkSeen(to);
        background.MarkSeen(from);
        to.Activity = false;
        to.TitleAt = 0;
        window.Redraw();
    }

    private void RestoreOutgoing()
    {
        if (_outgoing is not { } tab) return;
        _outgoing = null;
        foreach (var p in tab.Layout.LeafList)
        {
            p.Appear.Snap(1);
            p.Rect.Snap(p.Target);
        }
    }

    /// <summary>Hook from the chrome's tick: the outgoing tab's slide is over.</summary>
    public void TickOutgoing(long now)
    {
        if (_outgoing is not null && now >= _outgoingUntil) { RestoreOutgoing(); window.Redraw(); }
    }

    /// <summary>Hook at the start of DrawPanes: the outgoing tab, fading and sliding away under the new one.</summary>
    public void DrawOutgoing(DrawList dl, Theme theme, bool windowFocused, long now)
    {
        if (_outgoing is not { } tab || now >= _outgoingUntil) return;
        foreach (var p in tab.Layout.LeafList)
        {
            if (tab.Zoomed is not null && p != tab.Zoomed) continue;
            renderer.Draw(dl, p, p.Rect.Value(now), theme, p == tab.Focused, windowFocused, now);
        }
    }

    /// <summary>Hook from ReapExitedPanes: panes that exited in background tabs.</summary>
    public void ReapBackgroundTabs()
    {
        for (int i = tabs.Count - 1; i >= 0; i--)
        {
            var tab = tabs.Items[i];
            if (ReferenceEquals(tab, tabs.Active)) continue;
            bool any = false;
            foreach (var p in tab.Layout.LeafList) any |= p.Exited && !p.Held;
            if (any) ReapExitedIn(tab);
        }
    }

    private void ReapExitedIn(Tab tab)
    {
        foreach (var pane in tab.Layout.LeafList.Where(p => p.Exited && !p.Held).ToList())
        {
            if (exitHold.HoldIfWanted(pane)) continue; // exit_behavior keeps it (Windowing/ExitHoldFlow.cs)
            window.ForgetPane(pane);
            var next = tab.Layout.Remove(pane);
            pane.Dispose();
            if (next is null)
            {
                // It was the tab's only pane: the tab goes too.
                bool barWasShown = Shown;
                RemoveTab(tab);
                if (barWasShown != Shown) window.ApplyLayout(animate: true);
                break;
            }
            if (next.LayoutHidden) next = TeamRoster.FirstShown(tab.Layout) ?? next; // not a parked teammate
            if (tab.Focused == pane) tab.Focused = next;
            if (tab.Zoomed == pane) tab.Zoomed = null;
        }
    }

    public void DisposeBackgroundTabs()
    {
        var live = window.Layout;
        foreach (var layout in tabs.Items.Select(tab => tab.Layout))
        {
            if (ReferenceEquals(layout, live)) continue;
            foreach (var p in layout.LeafList) p.Dispose();
        }
    }

    /// <summary>Title for the tab bar: the custom name, else the program (or, for a shell, its directory).</summary>
    public void RefreshTitles(long now)
    {
        var items = tabs.Items;
        for (int i = 0; i < items.Count; i++) // runs every tick: no boxed enumerator
        {
            var tab = items[i];
            if (now - tab.TitleAt < 500) continue;
            tab.TitleAt = now;
            RefreshTitle(tab, now);
        }
    }

    private void RefreshTitle(Tab tab, long now)
    {
        var pane = ReferenceEquals(tab, tabs.Active) ? window.Focused : tab.Focused;
        string label = window.AgentTabLabel(pane, pane.Label ?? ""); // agent name instead of "claude" (Agents/WindowAgents.cs)
        if (pane.Container is { } container && label == (pane.Label ?? "")) label = window.ContainerTitle(container, pane); // WindowFeatures/WindowContainers.cs
        else if (TabTitles.IsShell(label)) label = TabTitles.Auto(label, pane.Term.Title, pane.WorkingDirectory, HomeDirectory); // only shells: WorkingDirectory checks the disk
        bool inContainer = window.TabInContainer(tab);
        if (inContainer != tab.InContainer) { tab.InContainer = inContainer; window.Redraw(); }
        if (label != tab.AutoTitle) { tab.AutoTitle = label; window.Redraw(); }
        bool unread = switcher.HasUnread(tab); // Tabs/WindowTabSwitcher.cs
        if (unread != tab.Unread) { tab.Unread = unread; window.Redraw(); }
        var programColor = pane.Term.ProgramTabColor; // OSC 6 (Tabs/WindowTabColors.cs)
        if (programColor != tab.ProgramColor) { tab.ProgramColor = programColor; window.Redraw(); }
        if (!ReferenceEquals(tab, tabs.Active))
        {
            bool hasActivity = background.HasActivity(tab, now);
            bool activity = tab.Activity || hasActivity;
            if (activity != tab.Activity) { tab.Activity = activity; window.Redraw(); }
        }
        else background.MarkSeen(tab);
    }

    /// <summary>The bar's "more" button shows a dot for a hidden tab like this: an agent waiting or finished unseen, or something unread.</summary>
    private static bool NeedsAttention(Tab tab) =>
        tab.AgentState is ErikTerm.Ui.AgentStatus.AgentState.Waiting or ErikTerm.Ui.AgentStatus.AgentState.Done || tab.Unread;

    public void Draw(DrawList dl, long now)
    {
        bool shown = Shown;
        if (!shown && !bar.IsVisible(now)) return;
        var theme = window.Theme;
        bar.BeginSync(shown, theme, now);
        if (shown)
        {
            bar.AnimateProgress = progress.AnimateProgress; // Windowing/WindowProgress.cs
            var items = tabs.Items;
            for (int i = 0; i < items.Count; i++) // by index: foreach over the IReadOnlyList would box its enumerator every frame
            {
                var tab = items[i];
                bar.Set(tab.Id, new TabBar.TabState
                {
                    Title = tab.Title, Active = ReferenceEquals(tab, tabs.Active), Activity = tab.Activity, Agent = tab.AgentState,
                    Badge = window.TabRepoBadge(tab), // git/GitHub badge (WindowFeatures/WindowIntegrations.cs)
                    Color = tabColors.ColorOf(tab), Progress = tab.Progress, Attention = NeedsAttention(tab), Container = tab.InContainer, // Tabs/WindowTabColors.cs, Windowing/WindowProgress.cs, Tabs/WindowTabSwitcher.cs
                }, now);
            }
        }
        bar.EndSync(now);

        var (w, _) = window.WindowPixels();
        var pad = window.ScaledPadding();
        // A floating bar rides down with its panel (FloatingChrome.DrawPanel): 0 once it is in place, negative on the way.
        // Below the window bar when it is out, pushed down with the panes (FloatingChrome).
        float top = pad.Top + chrome.Push(now);
        var rect = new RectF(pad.Left, top, Math.Max(1, w - pad.Left - pad.Right), TabBar.Height(theme));
        bar.Draw(dl, theme, rect, now);
    }

    public void BeginRename()
    {
        if (tabs.Active is not { } tab) return;
        if (!Shown) { window.Toast(ToastKind.Info, "The tab bar is hidden", "Set enable_tab_bar = true (and hide_tab_bar_if_only_one_tab = false) to rename tabs"); return; }
        chrome.HoldOut(Now); // a floating bar has to come down before it can be typed into (FloatingChrome)
        bar.BeginRename(tab.Id, tab.Title, Now);
    }

    public void AddCommands(List<Command> list)
    {
        void Add(string category, string title, int icon, string action, Func<bool>? enabled = null)
        {
            if (!ActionSpec.TryParse(action, out var spec, out _)) return;
            list.Add(new Command { Id = action, Category = category, Title = title, Icon = icon, Action = spec, IsEnabled = enabled, Shortcut = window.ShortcutFor(spec) });
        }
        bool multi() => tabs.Count > 1;
        Add("Edit", "Find…", Icons.Search, "Search");
        Add("Tab", "New Tab", Icons.Add, "SpawnTab");
        Add("Tab", "Close Tab", Icons.Close, "CloseCurrentTab");
        Add("Tab", "Next Tab", Icons.ArrowRight, "ActivateTabRelative(1)", multi);
        Add("Tab", "Previous Tab", Icons.ArrowLeft, "ActivateTabRelative(-1)", multi);
        Add("Tab", "Switch Tab…", Icons.Search, "ShowTabSwitcher", multi);
        Add("Tab", "Move Tab Right", Icons.ArrowRight, "MoveTabRelative(1)", multi);
        Add("Tab", "Move Tab Left", Icons.ArrowLeft, "MoveTabRelative(-1)", multi);
        list.Add(new Command { Id = "rename-tab", Category = "Tab", Title = "Rename Tab…", Icon = Icons.Edit, Run = BeginRename, IsEnabled = () => Shown });
        if (tabs.Count > 1)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                var tab = tabs.Items[i];
                int index = i;
                string tabArgument = i == 8 ? "-1" : i.ToString();
                list.Add(new Command
                {
                    Id = "tab:" + tab.Id, Category = "Tab", Title = $"Switch to {i + 1}: {tab.Title}", Icon = Icons.Terminal,
                    Run = () => ActivateTabIndex(index), IsEnabled = () => tabs.ActiveIndex != index,
                    Shortcut = i < 9 ? window.ShortcutFor(new ActionSpec("ActivateTab", tabArgument)) : [],
                });
            }
        }
    }
}
