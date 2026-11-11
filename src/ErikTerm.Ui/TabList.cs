namespace ErikTerm.Ui;

/// <summary>
/// Ordered tabs with one active, and wezterm's tab operations (ActivateTab, ActivateTabRelative,
/// MoveTabRelative, close). Pure bookkeeping: what a tab holds is up to the caller.
/// </summary>
public sealed class TabList<T> where T : class
{
    private readonly List<T> _items = [];
    private readonly TabRecency<T> _recency = new();

    public IReadOnlyList<T> Items => _items;

    /// <summary>Most recently used first: the active tab, then the one before it, and so on. Ctrl+Tab's order.</summary>
    public IReadOnlyList<T> Recent => _recency.Order;
    public int Count => _items.Count;
    public int ActiveIndex { get; private set; } = -1;
    public T? Active => ActiveIndex >= 0 ? _items[ActiveIndex] : null;

    public int IndexOf(T tab) => _items.IndexOf(tab);

    /// <summary>Adds a tab right after the active one (or at the end) and activates it. Returns its index.</summary>
    public int Add(T tab, bool afterActive = false)
    {
        int index = afterActive && ActiveIndex >= 0 ? ActiveIndex + 1 : _items.Count;
        _items.Insert(index, tab);
        ActiveIndex = index;
        _recency.Touch(tab);
        return index;
    }

    /// <summary>Inserts a tab at <paramref name="index"/> (clamped; negative = the end) and activates it. Returns its index.</summary>
    public int Insert(int index, T tab)
    {
        index = index < 0 || index > _items.Count ? _items.Count : index;
        _items.Insert(index, tab);
        ActiveIndex = index;
        _recency.Touch(tab);
        return index;
    }

    /// <summary>
    /// Resolves wezterm's ActivateTab argument: 0-based, negative counts from the end (-1 = last).
    /// Returns -1 when out of range.
    /// </summary>
    public int Resolve(int n)
    {
        int i = n < 0 ? _items.Count + n : n;
        return i >= 0 && i < _items.Count ? i : -1;
    }

    /// <summary>Activates a tab by index; returns false when out of range or already active.</summary>
    public bool Activate(int index)
    {
        if (index < 0 || index >= _items.Count || index == ActiveIndex) return false;
        ActiveIndex = index;
        _recency.Touch(_items[index]);
        return true;
    }

    public bool Activate(T tab) => Activate(_items.IndexOf(tab));

    /// <summary>Activates the tab <paramref name="delta"/> positions away, wrapping around (wezterm's ActivateTabRelative).</summary>
    public bool ActivateRelative(int delta, bool wrap = true)
    {
        if (_items.Count < 2 || delta == 0) return false;
        int next = ActiveIndex + delta;
        next = wrap ? ((next % _items.Count) + _items.Count) % _items.Count : Math.Clamp(next, 0, _items.Count - 1);
        return Activate(next);
    }

    /// <summary>Moves a tab to <paramref name="to"/> (clamped), keeping the same tab active. Returns false if nothing moved.</summary>
    public bool Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count) return false;
        to = Math.Clamp(to, 0, _items.Count - 1);
        if (to == from) return false;
        var active = Active;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);
        ActiveIndex = active is null ? -1 : _items.IndexOf(active);
        return true;
    }

    /// <summary>Moves the active tab by <paramref name="delta"/> positions, clamped at the ends (wezterm's MoveTabRelative).</summary>
    public bool MoveRelative(int delta) => ActiveIndex >= 0 && Move(ActiveIndex, ActiveIndex + delta);

    /// <summary>
    /// Removes a tab. When it was active, the tab to its right takes over (or its left neighbour when
    /// it was last), like browsers. Returns the new active tab, or null when none are left.
    /// </summary>
    public T? Remove(T tab)
    {
        int index = _items.IndexOf(tab);
        if (index < 0) return Active;
        var active = Active;
        _items.RemoveAt(index);
        _recency.Remove(tab);
        if (_items.Count == 0) { ActiveIndex = -1; return null; }
        if (ReferenceEquals(active, tab))
        {
            ActiveIndex = Math.Min(index, _items.Count - 1);
            _recency.Touch(_items[ActiveIndex]);
        }
        else ActiveIndex = _items.IndexOf(active!);
        return Active;
    }

    /// <summary>A tab that opened in the background: last in recency, so Ctrl+Tab still flips to the tab you came from.</summary>
    public void Demote(T tab) => _recency.Demote(tab);

    /// <summary>A restored session has no history: the active tab first, then bar order.</summary>
    public void ResetRecency() => _recency.Reset(_items, Active);
}
