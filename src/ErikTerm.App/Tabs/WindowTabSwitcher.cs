using ErikTerm.App.Native;
using ErikTerm.App.Notifications;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.Tabs;

/// <summary>
/// The Ctrl+Tab list (Ui/TabSwitcherOverlay.cs, the rules in ErikTerm.Ui/TabSwitcher.cs): most recently used tabs first,
/// per window. Holding the chord's modifier and pressing Tab opens it with the previous tab selected, Tab steps, releasing
/// the modifier switches; a tap faster than the quick-tap delay just flips to the previous tab, with no popup.
/// ShowTabSwitcher and the bar's "⌄ N more" open the same list and leave it up until Enter, a click or Esc. Keys go here
/// first while it is open: the letters typed filter the list, and with Ctrl held no text event follows them, so they are
/// taken from the key press.
/// </summary>
internal sealed class WindowTabSwitcher<TTab>(ITabSwitcherWindow<TTab> window, ITabSwitcherProcess process)
    where TTab : class, ITabView
{
    private const ushort HoldMods = Sdl.KMOD_CTRL | Sdl.KMOD_LALT | Sdl.KMOD_GUI;
    private const uint LeftCtrl = 0x400000E0, RightCtrl = 0x400000E4;

    private readonly TabSwitcherOverlay _overlay = new();
    private readonly DoubleTapDetector _ctrlDoubleTap = new();
    private ushort _hold;   // the modifiers that keep a hold open; releasing them all switches

    private static long Now => Environment.TickCount64;

    public bool IsOpen => _overlay.IsOpen;
    public bool IsAnimating(long now) => _overlay.IsAnimating(now);
    public void Draw(DrawList dl, Theme theme, RectF area, long now) => _overlay.Draw(dl, theme, area, now);
    public int WakeMs(long now) => _overlay.WakeMs(now);
    public void Close() => _overlay.Close();
    /// <summary>The double tap of Ctrl starts over: focus left the window, or a mouse button went down.</summary>
    public void ResetDoubleTap() => _ctrlDoubleTap.Reset();
    public void HandleText(string text, long now) => _overlay.HandleText(text, now);
    public void HandleMouseMove(float x, float y, long now) => _overlay.HandleMouseMove(x, y, now);
    public void HandleWheel(int lines, long now) => _overlay.HandleWheel(lines, now);

    /// <summary>Hook from OnKeyDown, before anything else: a Ctrl press may start a double tap, any other key ends one.</summary>
    public void NoteKeyForDoubleCtrl(uint key, ushort mod, bool repeat, long now)
    {
        if (key is not (LeftCtrl or RightCtrl)) { _ctrlDoubleTap.Reset(); return; }
        if (repeat) return;
        if ((mod & (Sdl.KMOD_SHIFT | Sdl.KMOD_LALT | Sdl.KMOD_GUI)) != 0) _ctrlDoubleTap.Reset();
        else _ctrlDoubleTap.Down(now);
    }

    /// <summary>Hook from key-up: Ctrl tapped twice on its own opens the list and leaves it up; again closes it.</summary>
    public void DoubleCtrlKeyUp(uint key, long now)
    {
        if (key is not (LeftCtrl or RightCtrl) || !_ctrlDoubleTap.Up(now) || !process.Settings.TabSwitcherDoubleCtrl) return;
        if (_overlay.IsOpen) { _overlay.Close(); window.Redraw(); return; }
        if (!window.OverlayOpen && window.Tabs.Count >= 2) Open(TabSwitcherMode.Sticky, +1, null, now);
    }

    /// <summary>The bound Ctrl+Tab action. With ctrl_tab_order = "position" it steps through the bar like ActivateTabRelative, with no list.</summary>
    public void RunAction(string arg, ushort mod, long now)
    {
        int delta = ParseInt(arg, 1) < 0 ? -1 : 1;
        if (process.Settings.CtrlTabOrder == "position")
        {
            window.ActivateTabRelative(delta);
            return;
        }
        if (window.Tabs.Count < 2) return;
        // A binding without a modifier (or from the palette) can't be held: it stays up instead.
        ushort hold = (ushort)(mod & HoldMods);
        _hold = hold;
        Open(hold != 0 ? TabSwitcherMode.Hold : TabSwitcherMode.Sticky, delta, null, now);
    }

    /// <summary>The bar's "⌄ N more": the list dropping down from the button, for the mouse.</summary>
    public void OpenFromBar(RectF anchor)
    {
        if (_overlay.IsOpen) return;
        Open(TabSwitcherMode.Sticky, +1, anchor, Now);
    }

    /// <summary>ShowTabSwitcher: the list, left up until Enter, a click or Esc.</summary>
    public void OpenSticky(long now) => Open(TabSwitcherMode.Sticky, +1, null, now);

    private void Open(TabSwitcherMode mode, int delta, RectF? anchor, long now)
    {
        var tabs = window.Tabs;
        if (_overlay.IsOpen || tabs.Count == 0) return;
        window.SyncTabs();
        window.CloseOverlaysForTabSwitcher(now);

        bool position = process.Settings.CtrlTabOrder == "position"; // the list without history, in bar order
        var rows = new List<TabSwitcherOverlay.Row>(tabs.Count);
        var order = position ? tabs.Items : tabs.Recent;
        for (int i = 0; i < order.Count; i++) rows.Add(RowFor(order[i]));
        int selected;
        if (position) selected = Math.Max(0, tabs.ActiveIndex);
        else if (rows.Count <= 1) selected = 0;
        else
        {
            int wrapped = delta < 0 ? rows.Count - 1 : delta;
            selected = (wrapped % rows.Count + rows.Count) % rows.Count;
        }

        _overlay.AnimateProgress = window.AnimateProgress;
        _overlay.Open(rows, selected, mode, anchor, window.Theme, now);
        window.Redraw();
    }

    /// <summary>A snapshot of one tab for the list. Allocates; runs while the list is open, not otherwise.</summary>
    private TabSwitcherOverlay.Row RowFor(TTab tab)
    {
        var badge = window.TabRepoBadge(tab);
        return new TabSwitcherOverlay.Row
        {
            Id = tab.Id,
            Number = (window.Tabs.IndexOf(tab) + 1).ToString(),
            Title = tab.Title,
            // Searchable, never highlighted: the repository and branch, and "container" for a tab in one.
            SearchExtra = (badge is null ? "" : badge.RepoName + " " + badge.BranchShort) + (tab.InContainer ? " container" : ""),
            Color = window.TabColorOf(tab),
            Badge = badge,
            Agent = tab.AgentState,
            Active = ReferenceEquals(tab, window.Tabs.Active),
            Activity = tab.Activity,
            Unread = HasUnread(tab),
            Progress = tab.Progress,
            Container = tab.InContainer,
        };
    }

    /// <summary>Hook at the end of Tick while the list is open: live rows, the popup after its delay, tabs gone or torn off.</summary>
    public void Tick(long now)
    {
        if (!_overlay.IsOpen) return;
        Sync();
        if (_overlay.Tick(now)) window.Redraw(); // the hold's delay is over
    }

    private void Sync()
    {
        var tabs = window.Tabs.Items;
        var rows = new List<TabSwitcherOverlay.Row>(tabs.Count);
        for (int i = 0; i < tabs.Count; i++) rows.Add(RowFor(tabs[i]));
        _overlay.Sync(rows);
    }

    /// <summary>Hook from OnKeyDown, before anything else: the list takes every key while it is open.</summary>
    public bool KeyDown(uint key, ushort mod, long now)
    {
        if (!_overlay.IsOpen) return false;
        Sync(); // a tab that left since the last tick must not be switched to
        window.SuppressText = true; // keep it unless the key is text for the filter
        switch (_overlay.HandleKey(key, mod, now))
        {
            case TabSwitcherOverlay.KeyResult.Accept: Accept(); return true;
            case TabSwitcherOverlay.KeyResult.Cancel: _overlay.Close(); window.Redraw(); return true;
            case TabSwitcherOverlay.KeyResult.Handled: window.Redraw(); return true;
        }

        // The chord itself again (a rebound Tab), without or with Shift.
        if (KeyNames.Chord(key, mod) is { } chord && process.Keys.TryGetValue(chord, out var action) && action.Name is "TabSwitcher" or "ShowTabSwitcher")
        {
            _overlay.Step(ParseInt(action.Arg ?? "1", 1) < 0 ? -1 : 1, now);
            window.Redraw();
            return true;
        }

        // Typing filters. With Ctrl or Alt held SDL sends no text event, so take the character from the key.
        // Without them the text event follows: let it through to OnTextInput.
        if ((mod & (Sdl.KMOD_CTRL | Sdl.KMOD_LALT | Sdl.KMOD_GUI)) == 0)
        {
            if (IsPrintableKey(key)) window.SuppressText = false;
            return true;
        }
        if (IsPrintableKey(key))
        {
            _overlay.TypeFromKey(char.ConvertFromUtf32((int)key), now);
            window.Redraw();
        }
        return true;
    }

    private static bool IsPrintableKey(uint key) =>
        key is >= 0x20 and < 0xD800 or > 0xDFFF and < 0x110000 && key != Sdl.K_DELETE && key != Sdl.K_TAB;

    /// <summary>Hook from the key-up event: the last held modifier went up, so the hold switches to the selected tab.</summary>
    public void ModifierUp(ushort mod)
    {
        if (!_overlay.IsOpen || _overlay.Mode != TabSwitcherMode.Hold || (mod & _hold) != 0) return;
        Sync();
        Accept();
    }

    /// <summary>Hook from OnMouseDown: the list takes the press while it is open.</summary>
    public bool MouseDown(float x, float y, long now)
    {
        if (!_overlay.IsOpen) return false;
        Sync();
        switch (_overlay.HandleMouseDown(x, y, now))
        {
            case TabSwitcherOverlay.KeyResult.Accept: Accept(); break;
            case TabSwitcherOverlay.KeyResult.Cancel: _overlay.Close(); break;
        }
        window.Redraw();
        return true;
    }

    private void Accept()
    {
        if (_overlay.Accept() is { } id && window.Tabs.ById(id) is { } tab) window.ActivateTab(window.Tabs.IndexOf(tab));
        window.Redraw();
    }

    /// <summary>Unread notifications belonging to the tab: its panes', teammates going with their lead (polled, so this stays off the frame path).</summary>
    public bool HasUnread(TTab tab)
    {
        var center = process.Notifications;
        if (center.UnreadCount == 0) return false;
        var items = center.Items;
        for (int i = 0; i < items.Count; i++)
            if (!items[i].Read && NotificationScope.HomeTab(process, items[i].PaneId) == tab.Id) return true;
        return false;
    }

    private static int ParseInt(string s, int fallback) => int.TryParse(s.Trim(), out int v) ? v : fallback;
}
