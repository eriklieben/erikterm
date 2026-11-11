using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Search;
using ErikTerm.App.Ui;
using ErikTerm.App.Vim;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// Find in the focused pane (the search bar, Ui/SearchBar.cs): opening it, stepping through matches, and the eased
/// scroll that brings a match (or the live screen) into view. Also the window's half of the search across all panes
/// (<see cref="WindowPaneSearch{TPane}"/>): the panes it offers and showing a hit.
/// </summary>
internal sealed class WindowSearch(IWindowHost window, Lock gate, SearchBar bar, WindowVim<Pane> vim, WindowPaneSearch<Pane> paneSearch,
    ContextMenu menu, CommandPalette palette)
{
    // Animated scrolling toward a match: the view's top line eases from _scrollFrom by _scrollAnim lines.
    private Tween _scrollAnim = new(0);
    private long _scrollFrom;
    private Pane? _scrollPane;
    private bool _scrollToBottom;

    private static long Now => Environment.TickCount64;

    public SearchBar Bar => bar;

    /// <summary>A scroll toward a match is under way (the window keeps drawing).</summary>
    public bool IsScrolling => _scrollPane is not null;

    /// <summary>The Search and SearchAllPanes actions.</summary>
    public void Execute(string name, string arg)
    {
        if (name == "SearchAllPanes") paneSearch.Open(Now); // Search/WindowPaneSearch.cs
        else Open(arg);
    }

    private void Open(string arg)
    {
        if (vim.OpenSearchPrompt()) return; // in vim mode, Search is vim's / prompt (Vim/WindowVim.cs)
        long now = Now;
        menu.Close(now);
        palette.Close(now);
        string? initial = null;
        long anchorLine;
        int anchorCol;
        var focused = window.Focused;
        lock (gate)
        {
            var t = focused.Term;
            // Prefill with a one-line selection, like most editors.
            if (focused.Selection?.Text(t) is { Length: > 0 and <= 200 } sel && !sel.Contains('\n')) initial = sel;
            else if (arg == "CurrentSelectionOrEmptyString") initial = "";
            anchorLine = t.ViewTopLine + t.Rows - 1;
            anchorCol = t.Columns;
        }
        bar.Open(focused, initial, window.Theme, anchorLine, anchorCol, now);
    }

    public void Close(bool resetView)
    {
        if (!bar.IsOpen) return;
        var pane = bar.Pane;
        bar.Close(Now);
        if (resetView && pane is not null) AnimateViewTo(pane, null);
    }

    private void Navigate(int delta)
    {
        if (bar.Step(delta, Now) is { } m && bar.Pane is { } pane) AnimateViewTo(pane, m);
    }

    /// <summary>Scrolls so <paramref name="match"/> is centred (or back to the live screen when null), easing over a few frames.</summary>
    public void AnimateViewTo(Pane pane, SearchMatch? match)
    {
        long now = Now;
        long current, target;
        lock (gate)
        {
            var t = pane.Term;
            current = t.ViewTopLine;
            if (match is { } m)
            {
                long mid = m.StartLine + (m.EndLine - m.StartLine) / 2;
                target = Math.Clamp(mid - (t.Rows - 1) / 2, t.IsAlternateScreen ? t.ScreenTopLine : t.FirstLine, t.ScreenTopLine);
            }
            else target = t.ScreenTopLine;
        }
        _scrollToBottom = match is null;
        _scrollPane = pane;
        _scrollFrom = current;
        _scrollAnim.Snap(0);
        long distance = Math.Abs(target - current);
        if (distance == 0) { FinishScroll(); return; }
        // With smooth scrolling the jump itself is eased by the pane (SmoothScroll); otherwise step there over a few frames.
        float anim = window.AnimationMs;
        float duration = anim <= 0 || window.SmoothScrolling ? 0 : Math.Clamp(120 + distance * 2, 140, 260);
        _scrollAnim.Set(target - current, now, duration);
        pane.ScrollActivityAt = now;
        TickScroll(now);
    }

    private void TickScroll(long now)
    {
        if (_scrollPane is not { } pane) return;
        lock (gate)
        {
            var t = pane.Term;
            long top = _scrollFrom + (long)MathF.Round(_scrollAnim.Value(now));
            if (_scrollToBottom && !_scrollAnim.IsAnimating(now)) t.ResetView();
            else t.ScrollView((int)Math.Clamp(t.ViewTopLine - top, int.MinValue / 2, int.MaxValue / 2));
        }
        pane.ScrollActivityAt = now;
        if (!_scrollAnim.IsAnimating(now)) FinishScroll();
    }

    private void FinishScroll()
    {
        if (_scrollToBottom && _scrollPane is { } pane) lock (gate) pane.Term.ResetView();
        _scrollPane = null;
        _scrollToBottom = false;
    }

    /// <summary>Per frame: advance the incremental search and any scroll animation.</summary>
    public void Tick(long now)
    {
        if (_scrollPane is not null) TickScroll(now);
        if (bar.Pane is not { } pane) return;
        if (!window.Layout.Contains(pane)) { bar.Detach(pane); return; }
        if (!bar.IsActive) return;
        SearchMatch? reveal;
        lock (gate) reveal = bar.Update(pane.Term, now);
        if (reveal is { } m) AnimateViewTo(pane, m);
    }

    /// <summary>Keys while the search bar is open. Returns false when it isn't.</summary>
    public bool KeyDown(uint key, ushort mod, long now)
    {
        if (!bar.IsOpen) return false;
        switch (bar.HandleKey(key, mod, now))
        {
            case SearchKeyResult.Handled: window.SuppressText = true; return true;
            case SearchKeyResult.Close: Close(resetView: true); window.SuppressText = true; return true;
            case SearchKeyResult.Previous: Navigate(-1); window.SuppressText = true; return true;
            case SearchKeyResult.Next: Navigate(+1); window.SuppressText = true; return true;
        }
        // Bound shortcuts still work (new tab, palette, scrolling…); paste goes into the query.
        if (KeyNames.Chord(key, mod) is { } chord && window.Keys.TryGetValue(chord, out var action))
        {
            window.SuppressText = true;
            if (action.Name == "PasteFrom")
                bar.Paste(action.Arg == "PrimarySelection" ? Sdl.GetPrimarySelectionText() : Sdl.GetClipboardText(), now);
            else if (action.Name != "Search") window.Execute(action);
            return true;
        }
        return true; // printable keys arrive as text input; everything else stays out of the terminal
    }

    /// <summary>Text while the bar is open (the query). False when it isn't.</summary>
    public bool TextInput(string text, long now)
    {
        if (!bar.IsOpen) return false;
        bar.HandleText(text, now);
        return true;
    }

    /// <summary>A press while the bar is open: its buttons, or outside it, which closes it (the view stays put). False: not taken.</summary>
    public bool MouseDown(float x, float y, long now)
    {
        if (!bar.IsOpen) return false;
        switch (bar.HandleMouseDown(x, y, now))
        {
            case null: Close(resetView: false); return false;
            case SearchKeyResult.Previous: Navigate(-1); break;
            case SearchKeyResult.Next: Navigate(+1); break;
            case SearchKeyResult.Close: Close(resetView: true); break;
        }
        return true;
    }

    /// <summary>The pane's lines were renumbered by a reflow: the bar re-runs its search, a scroll toward a match stops.</summary>
    public void Reflowed(Pane pane)
    {
        if (ReferenceEquals(bar.Pane, pane)) bar.OnReflow();
        if (_scrollPane == pane) { _scrollPane = null; _scrollToBottom = false; }
    }

    /// <summary>The pane left this window or closed: a scroll toward a match in it stops.</summary>
    public void ForgetScroll(Pane pane)
    {
        if (_scrollPane == pane) { _scrollPane = null; _scrollToBottom = false; }
    }

    // ---- Search across panes ---------------------------------------------------------------------

    /// <summary>This window's panes for the search, the focused one first, then its tab, then the other tabs.</summary>
    public void CollectPaneSearchTargets(List<PaneSearchTarget<Pane>> into, string windowPrefix)
    {
        window.SyncTabs();
        var tabs = window.Tabs;
        var focused = window.Focused;
        Add(focused, tabs.ActiveIndex);
        for (int i = 0; i < tabs.Count; i++)
        {
            int tab = (i + tabs.ActiveIndex) % tabs.Count; // the active tab first
            var layout = tabs.Items[tab].Layout;
            foreach (var pane in layout.LeafList.Where(p => !ReferenceEquals(p, focused)))
                Add(pane, tab);
        }

        void Add(Pane pane, int tab)
        {
            if (!pane.LayoutHidden) into.Add(new PaneSearchTarget<Pane>(pane, pane.Term, pane.Label, $"{windowPrefix}Tab {tab + 1}"));
        }
    }

    /// <summary>Shows <paramref name="pane"/> if it is in this window, with the search bar on the match; false when it isn't.</summary>
    public bool RevealSearchHit(Pane pane, PaneSearchQuery query, SearchMatch match)
    {
        if (!window.JumpToPane(pane)) return false;
        bar.OpenAt(pane, query.Pattern, query.CaseSensitive, query.Regex, match, window.Theme, Now);
        AnimateViewTo(pane, match);
        return true;
    }
}
