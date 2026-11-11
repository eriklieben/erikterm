namespace ErikTerm.Ui;

/// <summary>How the tab list was opened.</summary>
public enum TabSwitcherMode
{
    /// <summary>Ctrl+Tab held: Tab steps, releasing the modifier switches. A quick tap never shows the list.</summary>
    Hold,
    /// <summary>ShowTabSwitcher and the bar's "⌄ N more": stays until Enter, a click or Esc.</summary>
    Sticky,
}

/// <summary>One entry of the list: a tab (by id, ids never change), its title, and what else is searchable.</summary>
public readonly record struct TabSwitcherEntry(int Id, string Title, string Extra = "")
{
    /// <summary>What the fuzzy matcher sees: the title first, so highlight positions are positions in the title.</summary>
    public string SearchText => Extra.Length == 0 ? Title : Title + "  " + Extra;
}

/// <summary>An entry that passed the filter, and which of its title's characters matched.</summary>
public sealed record TabSwitcherMatch(TabSwitcherEntry Entry, int Score, List<int> Positions);

/// <summary>
/// The Ctrl+Tab list without the drawing: which tabs are in it, the filter, which one is selected, and when the popup
/// may appear. Selection is by position in the filtered list; it follows the selected tab (by id) when the list
/// changes underneath it (a tab closed or torn off while the list is open), so the right tab is switched to.
/// </summary>
public sealed class TabSwitcherModel
{
    /// <summary>A press and release faster than this flips to the previous tab without showing the list.</summary>
    public const int QuickTapMs = 150;

    /// <summary>The list may show once the modifier was held this long (always, when it wasn't opened by a hold).</summary>
    public static bool PopupDue(TabSwitcherMode mode, long openedAt, long now) =>
        mode != TabSwitcherMode.Hold || now - openedAt >= QuickTapMs;

    /// <summary>True when the release came before the popup could show: the tab changes with no flash of the list.</summary>
    public static bool IsQuickTap(long openedAt, long releasedAt) => releasedAt - openedAt < QuickTapMs;

    private readonly List<TabSwitcherEntry> _entries = [];
    private readonly List<TabSwitcherMatch> _matches = [];
    private int _initialIndex;
    private int _selected;

    public bool IsOpen { get; private set; }
    public TabSwitcherMode Mode { get; private set; }
    public long OpenedAt { get; private set; }
    public string Query { get; private set; } = "";
    public IReadOnlyList<TabSwitcherMatch> Matches => _matches;
    public int EntryCount => _entries.Count;
    public int SelectedIndex => _selected;

    /// <summary>The selected tab's id, or null when the filter left nothing.</summary>
    public int? SelectedId => _selected >= 0 && _selected < _matches.Count ? _matches[_selected].Entry.Id : null;

    /// <summary>Opens the list on <paramref name="selected"/> (index into <paramref name="entries"/>).</summary>
    public void Open(IEnumerable<TabSwitcherEntry> entries, int selected, TabSwitcherMode mode, long now)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        Mode = mode;
        OpenedAt = now;
        Query = "";
        IsOpen = true;
        _initialIndex = Math.Clamp(selected, 0, Math.Max(0, _entries.Count - 1));
        Refilter();
    }

    public void Close()
    {
        IsOpen = false;
        _entries.Clear();
        _matches.Clear();
        Query = "";
        _selected = 0;
    }

    /// <summary>Whether the popup is showing at <paramref name="now"/>.</summary>
    public bool PopupVisible(long now) => IsOpen && PopupDue(Mode, OpenedAt, now);

    /// <summary>Milliseconds until the popup is due (0 once it is, int.MaxValue when closed).</summary>
    public int PopupWakeMs(long now)
    {
        if (!IsOpen) return int.MaxValue;
        if (Mode != TabSwitcherMode.Hold) return 0;
        long left = QuickTapMs - (now - OpenedAt);
        return left <= 0 ? 0 : (int)left;
    }

    /// <summary>A step through the filtered list, wrapping around; pass <paramref name="clamp"/> for page keys, which stop at the ends.</summary>
    public void Move(int delta, bool clamp = false)
    {
        if (_matches.Count == 0) return;
        int next = _selected + delta;
        _selected = clamp ? Math.Clamp(next, 0, _matches.Count - 1) : Wrap(next, _matches.Count);
    }

    public void Select(int index)
    {
        if (_matches.Count > 0) _selected = Math.Clamp(index, 0, _matches.Count - 1);
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;

    /// <summary>Text typed into the filter.</summary>
    public void Type(string text)
    {
        if (text.Length == 0) return;
        Query += text;
        Refilter();
    }

    /// <summary>Removes the last character of the filter.</summary>
    public void Backspace()
    {
        if (Query.Length == 0) return;
        Query = Query[..^(char.IsLowSurrogate(Query[^1]) && Query.Length > 1 ? 2 : 1)];
        Refilter();
    }

    /// <summary>Returns the id to switch to and closes the list (null: nothing was selected).</summary>
    public int? Accept()
    {
        int? id = SelectedId;
        Close();
        return id;
    }

    /// <summary>
    /// The tabs as they are now, in list order. Gone tabs drop out, new ones (a background tab) go last, and a changed
    /// title is re-matched. The selection stays on its tab; when that tab left, on the entry that took its place.
    /// Returns false when nothing is left, and closes.
    /// </summary>
    public bool Sync(IReadOnlyList<TabSwitcherEntry> live)
    {
        if (!IsOpen) return false;
        int? selectedId = SelectedId;
        bool changed = ApplyLiveEntries(live);
        changed |= AppendNewEntries(live);
        if (_entries.Count == 0) { Close(); return false; }
        if (!changed) return true;

        _initialIndex = Math.Clamp(_initialIndex, 0, _entries.Count - 1);
        int keep = _selected;
        Refilter(keepSelection: true);
        int found = selectedId is { } id ? IndexOfMatch(id) : -1;
        if (found >= 0) _selected = found;
        else _selected = _matches.Count == 0 ? 0 : Math.Clamp(keep, 0, _matches.Count - 1);
        return true;
    }

    /// <summary>Drops the entries of tabs that left and refreshes changed ones; true when anything changed.</summary>
    private bool ApplyLiveEntries(IReadOnlyList<TabSwitcherEntry> live)
    {
        bool changed = false;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            int at = IndexOf(live, _entries[i].Id);
            if (at < 0)
            {
                _entries.RemoveAt(i);
                if (_initialIndex > i) _initialIndex--;
                changed = true;
            }
            else if (live[at] != _entries[i])
            {
                _entries[i] = live[at];
                changed = true;
            }
        }
        return changed;
    }

    private bool AppendNewEntries(IReadOnlyList<TabSwitcherEntry> live)
    {
        bool changed = false;
        for (int i = 0; i < live.Count; i++)
        {
            if (IndexOf(_entries, live[i].Id) >= 0) continue;
            _entries.Add(live[i]);
            changed = true;
        }
        return changed;
    }

    private static int IndexOf(IReadOnlyList<TabSwitcherEntry> list, int id)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == id) return i;
        return -1;
    }

    private int IndexOfMatch(int id)
    {
        for (int i = 0; i < _matches.Count; i++)
            if (_matches[i].Entry.Id == id) return i;
        return -1;
    }

    private void Refilter(bool keepSelection = false)
    {
        _matches.Clear();
        if (Query.Trim().Length == 0)
        {
            foreach (var e in _entries) _matches.Add(new TabSwitcherMatch(e, 0, []));
            if (!keepSelection) _selected = Math.Min(_initialIndex, Math.Max(0, _matches.Count - 1));
            return;
        }

        foreach (var e in _entries)
        {
            var positions = new List<int>();
            if (!Fuzzy.Match(Query, e.SearchText, out int score, positions)) continue;
            int titleLength = e.Title.Length; // highlights are drawn on the title only
            positions.RemoveAll(p => p >= titleLength);
            _matches.Add(new TabSwitcherMatch(e, score, positions));
        }
        // Best match first; equal scores keep the list's own (recency) order.
        var order = new Dictionary<int, int>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++) order[_entries[i].Id] = i;
        _matches.Sort((a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : order[a.Entry.Id].CompareTo(order[b.Entry.Id]);
        });
        if (!keepSelection) _selected = 0;
    }
}

/// <summary>What the tab bar shows: how wide tabs are, how many fit, and which is first when they don't all fit.</summary>
public readonly record struct TabStripLayout(float TabWidth, int FitCount, int First, bool Overflowing, float ViewportWidth)
{
    /// <summary>How many tabs are out of view.</summary>
    public int Hidden(int count) => Overflowing ? Math.Max(0, count - FitCount) : 0;
}

/// <summary>
/// The tab bar when not all tabs fit: tabs keep their minimum width and the bar becomes a window onto them, by index.
/// How many fit, which is first, and keeping a tab in view. Pure, so the bar's tweens just follow.
/// </summary>
public static class TabStrip
{
    /// <summary>
    /// Lays out <paramref name="count"/> tabs in <paramref name="avail"/> pixels (the bar minus the "+" and its gap). Tabs
    /// shrink evenly down to <paramref name="minW"/>; past that the bar overflows, the "⌄ N more" button
    /// (<paramref name="moreW"/>) takes room, and as many whole tabs as fit are shown, starting at <paramref name="first"/>.
    /// </summary>
    public static TabStripLayout Layout(int count, float avail, float gap, float minW, float maxW, float moreW, int first)
    {
        int n = Math.Max(1, count);
        if (n * minW + (n - 1) * gap <= avail || n == 1)
        {
            float even = MathF.Round(Math.Clamp((avail - gap * (n - 1)) / n, minW, maxW));
            return new TabStripLayout(even, count, 0, false, avail);
        }

        float view = MathF.Max(minW, avail - moreW - gap);
        int fit = Math.Min(Math.Max(1, (int)MathF.Floor((view + gap) / (minW + gap))), n);
        float width = MathF.Floor(Math.Clamp((view - gap * (fit - 1)) / fit, minW, maxW));
        return new TabStripLayout(width, fit, ClampFirst(first, count, fit), true, view);
    }

    /// <summary>The first shown index, kept so that the window stays over the tabs.</summary>
    public static int ClampFirst(int first, int count, int fit) => Math.Clamp(first, 0, Math.Max(0, count - fit));

    /// <summary>The smallest shift of the window that shows <paramref name="index"/>.</summary>
    public static int Reveal(int first, int index, int count, int fit)
    {
        if (index < first) first = index;
        else if (index >= first + fit) first = index - fit + 1;
        return ClampFirst(first, count, fit);
    }

    /// <summary>Whether tab <paramref name="index"/> is outside the window.</summary>
    public static bool IsHidden(int index, int first, int fit) => index < first || index >= first + fit;

    /// <summary>How many tabs outside the window need attention (<paramref name="attention"/>: one flag per tab, in order).</summary>
    public static int HiddenAttention(ReadOnlySpan<bool> attention, int first, int fit)
    {
        int count = 0;
        for (int i = 0; i < attention.Length; i++)
            if (IsHidden(i, first, fit) && attention[i]) count++;
        return count;
    }

    /// <summary>The button's label for <paramref name="hidden"/> tabs out of view.</summary>
    public static string MoreLabel(int hidden) => hidden + " more";
}
