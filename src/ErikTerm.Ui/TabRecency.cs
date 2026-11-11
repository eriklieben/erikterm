namespace ErikTerm.Ui;

/// <summary>
/// A window's tabs, most recently used first (Ctrl+Tab's order). <see cref="TabList{T}"/> keeps it up to date: activating
/// a tab moves it to the front, and a closed or torn-off tab leaves. Nothing here allocates once a tab is known.
/// </summary>
public sealed class TabRecency<T> where T : class
{
    private readonly List<T> _order = [];

    public IReadOnlyList<T> Order => _order;

    /// <summary>The tab was used: it goes to the front (a tab seen for the first time included).</summary>
    public void Touch(T tab)
    {
        int at = _order.IndexOf(tab);
        if (at == 0) return;
        if (at > 0) _order.RemoveAt(at);
        _order.Insert(0, tab);
    }

    /// <summary>The tab is gone from this window.</summary>
    public void Remove(T tab) => _order.Remove(tab);

    /// <summary>A tab that was created but never looked at (opened in the background): last in line.</summary>
    public void Demote(T tab)
    {
        if (_order.Remove(tab)) _order.Add(tab);
    }

    /// <summary>Starts over: <paramref name="active"/> first, then the tabs in their bar order (a restored session has no history).</summary>
    public void Reset(IReadOnlyList<T> tabs, T? active)
    {
        _order.Clear();
        if (active is not null) _order.Add(active);
        for (int i = 0; i < tabs.Count; i++)
            if (!ReferenceEquals(tabs[i], active)) _order.Add(tabs[i]);
    }
}
