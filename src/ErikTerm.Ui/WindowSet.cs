namespace ErikTerm.Ui;

/// <summary>
/// The windows of one process and who owns which tab: each window has its own <see cref="TabList{T}"/>, a tab
/// belongs to exactly one window, and tabs move between windows (tear-off, "Move Tab to Window N"). Focus is
/// kept most-recently-used first, so closing a window hands focus (keyboard, `erikterm cli` without a target) to
/// the one used before it. Closing semantics: a window whose last tab leaves or closes closes too, and the
/// process exits when the last window is gone (<see cref="Count"/> == 0). Pure bookkeeping, like TabList.
/// </summary>
public sealed class WindowSet<TWindow, TTab> where TWindow : class where TTab : class
{
    private readonly List<(TWindow Window, TabList<TTab> Tabs)> _windows = [];
    private readonly List<TWindow> _recent = [];

    /// <summary>The result of <see cref="MoveTab"/>.</summary>
    /// <param name="From">The window the tab came from.</param>
    /// <param name="Index">Where it went in the target window.</param>
    /// <param name="WasActive">It was the source window's active tab (the source switched to <paramref name="SourceActive"/>).</param>
    /// <param name="SourceActive">The source window's active tab now; null when it has none left and should close.</param>
    /// <param name="TargetPrevious">The tab that was active in the target before.</param>
    public readonly record struct TabMove(TWindow From, TWindow To, int Index, bool WasActive, TTab? SourceActive, TTab? TargetPrevious)
    {
        public bool SourceEmpty => SourceActive is null;
    }

    public int Count => _windows.Count;

    /// <summary>Windows in the order they were opened.</summary>
    public IEnumerable<TWindow> Windows
    {
        get
        {
            foreach (var (w, _) in _windows) yield return w;
        }
    }

    /// <summary>Most recently focused first.</summary>
    public IReadOnlyList<TWindow> ByRecency => _recent;

    /// <summary>The window that has (or last had) focus.</summary>
    public TWindow? Focused => _recent.Count > 0 ? _recent[0] : null;

    public TWindow this[int index] => _windows[index].Window;

    public void Add(TWindow window, TabList<TTab> tabs, bool focus = true)
    {
        if (Contains(window)) throw new InvalidOperationException("window added twice");
        _windows.Add((window, tabs));
        if (focus) _recent.Insert(0, window);
        else _recent.Add(window);
    }

    public bool Contains(TWindow window) => IndexOf(window) >= 0;

    public int IndexOf(TWindow window)
    {
        for (int i = 0; i < _windows.Count; i++)
            if (ReferenceEquals(_windows[i].Window, window)) return i;
        return -1;
    }

    public TabList<TTab> TabsOf(TWindow window)
    {
        int i = IndexOf(window);
        return i >= 0 ? _windows[i].Tabs : throw new ArgumentException("not one of this set's windows", nameof(window));
    }

    /// <summary>The window holding <paramref name="tab"/>, or null.</summary>
    public TWindow? WindowOf(TTab tab)
    {
        foreach (var (w, tabs) in _windows)
            if (tabs.IndexOf(tab) >= 0) return w;
        return null;
    }

    /// <summary>The window gained focus: it moves to the front of <see cref="ByRecency"/>.</summary>
    public void Focus(TWindow window)
    {
        int i = _recent.FindIndex(w => ReferenceEquals(w, window));
        if (i < 0) return;
        if (i == 0) return;
        _recent.RemoveAt(i);
        _recent.Insert(0, window);
    }

    /// <summary>Removes a window. Returns the window that takes over focus (the most recently used other one), or null when none are left.</summary>
    public TWindow? Remove(TWindow window)
    {
        int i = IndexOf(window);
        if (i < 0) return Focused;
        _windows.RemoveAt(i);
        _recent.RemoveAll(w => ReferenceEquals(w, window));
        return Focused;
    }

    /// <summary>
    /// Whether <paramref name="tab"/> can move to <paramref name="to"/> (null = a new window): not to its own window,
    /// and not the only tab of a window into a window of its own (that would just replace the window).
    /// </summary>
    public bool CanMove(TTab tab, TWindow? to)
    {
        if (WindowOf(tab) is not { } from) return false;
        if (to is null) return TabsOf(from).Count > 1;
        return !ReferenceEquals(from, to) && Contains(to);
    }

    /// <summary>
    /// Tear-off: moves <paramref name="tab"/> out of its window into <paramref name="window"/>, a new window registered
    /// here (focused) with <paramref name="tabs"/> (empty) as its tab list.
    /// </summary>
    public TabMove MoveTabToNewWindow(TTab tab, TWindow window, TabList<TTab> tabs)
    {
        var from = WindowOf(tab) ?? throw new ArgumentException("the tab isn't in any window", nameof(tab));
        if (Contains(window)) throw new InvalidOperationException("not a new window");
        var source = TabsOf(from);
        bool wasActive = ReferenceEquals(source.Active, tab);
        var sourceActive = source.Remove(tab);
        tabs.Add(tab);
        Add(window, tabs);
        return new TabMove(from, window, 0, wasActive, sourceActive, null);
    }

    /// <summary>
    /// Moves <paramref name="tab"/> into <paramref name="to"/> at <paramref name="index"/> (clamped; negative = the end), where
    /// it becomes the active tab. In the source window the neighbour takes over as after a close. Within the same window it's a reorder.
    /// </summary>
    public TabMove MoveTab(TTab tab, TWindow to, int index)
    {
        var from = WindowOf(tab) ?? throw new ArgumentException("the tab isn't in any window", nameof(tab));
        var target = TabsOf(to);
        var previous = target.Active;
        if (ReferenceEquals(from, to))
        {
            int at = target.IndexOf(tab);
            target.Move(at, index < 0 ? target.Count - 1 : index);
            target.Activate(tab);
            return new TabMove(from, to, target.IndexOf(tab), true, tab, previous);
        }
        var source = TabsOf(from);
        bool wasActive = ReferenceEquals(source.Active, tab);
        var sourceActive = source.Remove(tab);
        int placed = target.Insert(index, tab);
        return new TabMove(from, to, placed, wasActive, sourceActive, previous);
    }
}
