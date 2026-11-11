namespace ErikTerm.Core;

/// <summary>
/// One string object per distinct cluster text. A direct-mapped table: an entry is replaced by the next text hashing to
/// its slot, which bounds it at <see cref="Slots"/> strings of up to <see cref="Row.MaxClusterCodePoints"/> code points.
/// Identical clusters on many rows then share a string, so filling the scrollback doesn't create a string per cell.
/// Owned by one Terminal; not thread-safe.
/// </summary>
internal sealed class ClusterStringPool
{
    private const int Slots = 1 << 10;
    private readonly string?[] _slots = new string?[Slots];

    public string Get(ReadOnlySpan<char> text)
    {
        ref var slot = ref _slots[string.GetHashCode(text) & (Slots - 1)];
        if (slot is { } s && text.SequenceEqual(s)) return s;
        return slot = new string(text);
    }
}

/// <summary>
/// A row's grapheme clusters sorted by column: the full cluster text (base included) for cells flagged
/// <see cref="CellAttributes.Cluster"/>. The flag is authoritative: overwriting a cell drops the flag, so stale entries
/// here are never observed (and are overwritten or cleared later). The array is made the first time a row needs one.
/// One flat array rather than a dictionary: every scrollback row is an object the GC has to walk while output floods in.
/// A struct the row holds by value (a field of <see cref="Row"/>, never copied), so a row has no extra object and
/// is no larger than with the fields inline (a reference and a count fill the struct). Bumping the row's version is the row's job.
/// </summary>
internal struct RowClusters
{
    private struct ClusterEntry
    {
        public int Col;
        public string Text;
    }

    private ClusterEntry[]? _entries;

    private int _count;

    public readonly int Count => _count;

    /// <summary>The entry array exists, in use or not (tests, diagnostics).</summary>
    public readonly bool Allocated => _entries is not null;

    /// <summary>The cluster text stored for <paramref name="col"/>, or null.</summary>
    public readonly string? Get(int col) => Find(col) is var i and >= 0 ? _entries![i].Text : null;

    /// <summary>Lets go of an entry array with nothing in it.</summary>
    public void ReleaseIfEmpty()
    {
        if (_count == 0) _entries = null;
    }

    /// <summary>Index of the entry for <paramref name="col"/>, or the complement of where it belongs (binary search).</summary>
    private readonly int Find(int col)
    {
        var entries = _entries;
        int lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = entries![mid].Col;
            if (c == col) return mid;
            if (c < col) lo = mid + 1;
            else hi = mid - 1;
        }
        return ~lo;
    }

    public void Store(int col, string text)
    {
        // Text is written left to right, so the usual case appends.
        if (_count > 0 && _entries![_count - 1].Col < col)
        {
            Append(col, text);
            return;
        }
        int i = Find(col);
        if (i >= 0)
        {
            _entries![i].Text = text;
            return;
        }
        i = ~i;
        Append(col, text);
        var entries = _entries!;
        Array.Copy(entries, i, entries, i + 1, _count - 1 - i);
        entries[i] = new ClusterEntry { Col = col, Text = text };
    }

    private void Append(int col, string text)
    {
        if (_entries is null) _entries = new ClusterEntry[4];
        else if (_count == _entries.Length) Array.Resize(ref _entries, _entries.Length * 2);
        _entries[_count++] = new ClusterEntry { Col = col, Text = text };
    }

    /// <summary>Drops every entry (the array is kept for reuse).</summary>
    public void Clear()
    {
        if (_count == 0) return;
        _entries.AsSpan(0, _count).Clear();
        _count = 0;
    }

    /// <summary>
    /// Moves entries for columns at or after <paramref name="from"/> by <paramref name="delta"/>; entries that end up
    /// outside [0, <paramref name="columns"/>) are dropped.
    /// </summary>
    public void Shift(int from, int delta, int columns)
    {
        int start = Find(from);
        if (start < 0) start = ~start;
        var moved = _entries.AsSpan(start, _count - start).ToArray();
        _entries.AsSpan(start, _count - start).Clear();
        _count = start;
        foreach (ref readonly var e in moved.AsSpan())
        {
            int col = e.Col + delta;
            if (col >= 0 && col < columns) Store(col, e.Text);
        }
    }
}
