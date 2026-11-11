namespace ErikTerm.Core;

/// <summary>Fixed-capacity ring of rows that scrolled off the top. Index 0 is the oldest line.</summary>
public sealed class Scrollback
{
    private Row[] _ring;
    private int _start;

    public Scrollback(int capacity) => _ring = new Row[Math.Max(0, capacity)];

    public int Capacity => _ring.Length;
    public int Count { get; private set; }

    /// <summary>Lines ever dropped off the top (evicted or cleared). Monotonic: lets callers number lines stably.</summary>
    public long Dropped { get; private set; }

    /// <summary>
    /// How many of the newest rows haven't been looked at by <see cref="Terminal.CompactScrollback"/> yet (they may still
    /// hold full-width cell arrays). Written under the terminal's lock; other threads may read it as a hint.
    /// </summary>
    public int Uncompacted => _uncompacted;
    private int _uncompacted;

    /// <summary>The oldest row not compacted yet (see <see cref="Uncompacted"/>), which then counts as looked at.</summary>
    private Row TakeUncompacted()
    {
        Row row = Peek(Count - _uncompacted);
        _uncompacted--;
        return row;
    }

    /// <summary>Packed rows were read and should be packed again by the next compaction. A hint for other threads.</summary>
    public bool HasRowsToRepack => _hasUnpackedRows || _repackNext < _repackEnd;
    private bool _hasUnpackedRows;

    // Lines read (so unpacked) since the last compaction, then the lines a repack pass still has to look at. Absolute
    // line numbers, so lines evicted meanwhile simply fall off the front. Only these rows can have been unpacked, so a
    // read doesn't cost a walk over the whole history.
    private long _readFrom = long.MaxValue, _readTo = long.MinValue;
    private long _repackNext, _repackEnd;
    private bool _repackIsLarge;

    /// <summary>A pass over at least this many lines is a whole search or copy; below that it's what is on screen, unpacked again by every redraw.</summary>
    private const long LargeRepackRows = 2048;

    /// <summary>Starts a pass over the lines read since the last one (joining a pass still under way).</summary>
    public void RepackAfterReads()
    {
        if (!_hasUnpackedRows) return;
        _hasUnpackedRows = false;
        long from = _readFrom, to = _readTo;
        _readFrom = long.MaxValue;
        _readTo = long.MinValue;
        if (_repackNext < _repackEnd)
        {
            from = Math.Min(from, _repackNext);
            to = Math.Max(to, _repackEnd);
        }
        _repackNext = Math.Max(from, Dropped);
        _repackEnd = Math.Min(to, Dropped + Count);
        _repackIsLarge = _repackEnd - _repackNext >= LargeRepackRows;
    }

    /// <summary>
    /// The next row <see cref="Terminal.CompactScrollback"/> should look at, or null when there is none: rows read since
    /// the last compaction first, then the rows not yet looked at. <paramref name="repacked"/> says the row came from a
    /// repack pass too small to matter for what the process holds on to.
    /// </summary>
    public Row? NextToCompact(out bool repacked)
    {
        long first = Dropped;
        _repackNext = Math.Max(_repackNext, first);
        _repackEnd = Math.Min(_repackEnd, first + Count);
        if (_repackNext < _repackEnd)
        {
            repacked = !_repackIsLarge;
            return Peek((int)(_repackNext++ - first));
        }
        repacked = false;
        return _uncompacted > 0 ? TakeUncompacted() : null;
    }

    private Row Unpacked(Row row, int index)
    {
        if (row.IsPacked)
        {
            row.Unpack();
            _hasUnpackedRows = true;
            long line = Dropped + index;
            _readFrom = Math.Min(_readFrom, line);
            _readTo = Math.Max(_readTo, line + 1);
        }
        return row;
    }

    /// <summary>A row that may be packed, for looking at what is kept beside its cells: links, marks, its identity.</summary>
    public Row Peek(int index) => _ring[(_start + index) % _ring.Length];

    private void SetUncompacted(int n) => _uncompacted = Math.Clamp(n, 0, Count);

    /// <summary>
    /// Rows left in the free slots by <see cref="ClearKeepingPinnedRows"/>, which <see cref="Push"/> hands out again.
    /// Written under the terminal's lock; other threads may read it as a hint.
    /// </summary>
    public int SpareRows => _spareRows;
    private int _spareRows;

    public Row this[int index] => Unpacked(Peek(index), index);

    /// <summary>Appends a row. Returns a row the caller can reuse: the evicted oldest one when full, or a spare one.</summary>
    public Row? Push(Row row)
    {
        if (_ring.Length == 0) { Dropped++; return row; }
        if (Count < _ring.Length)
        {
            int slot = (_start + Count) % _ring.Length;
            Row? spare = _ring[slot];
            _ring[slot] = row;
            Count++;
            _uncompacted++; // ≤ Count
            if (spare is not null) _spareRows--;
            return spare;
        }
        Row evicted = _ring[_start];
        Dropped++;
        _ring[_start] = row;
        _start = (_start + 1) % _ring.Length;
        if (_uncompacted < Count) _uncompacted++;
        return evicted;
    }

    /// <summary>Removes and returns the newest row (used when the screen grows taller).</summary>
    public Row? PopNewest()
    {
        if (Count == 0) return null;
        Count--;
        SetUncompacted(_uncompacted - 1);
        int i = (_start + Count) % _ring.Length;
        Row row = _ring[i];
        _ring[i] = null!;
        row.Unpack(); // it leaves the ring for the screen
        return row;
    }

    /// <summary>
    /// Appends all rows, oldest first, to <paramref name="destination"/>, for reflow. A row that is a whole line and fits
    /// <paramref name="moveOnlyColumns"/> stays packed, since reflow only moves those: unpacking a long history for each
    /// step of a drag is what would make resizing slow. The others are unpacked; Reset marks them all for compaction.
    /// </summary>
    public void CopyTo(List<Row> destination, int moveOnlyColumns)
    {
        if (_ring.Length == 0) return;
        bool continuesLine = false;
        for (int i = 0; i < Count; i++)
        {
            var row = Peek(i);
            if (continuesLine || !row.PackedFits(moveOnlyColumns)) row.Unpack();
            continuesLine = row.Wrapped;
            destination.Add(row);
        }
    }

    /// <summary>
    /// Replaces the contents with the first <paramref name="count"/> rows of <paramref name="rows"/> (oldest first),
    /// used by reflow. Rows beyond the capacity are dropped from the old end and counted in <see cref="Dropped"/>.
    /// </summary>
    public void Reset(List<Row> rows, int count)
    {
        Array.Clear(_ring);
        _start = 0;
        _spareRows = 0;
        int skip = Math.Max(0, count - _ring.Length);
        Dropped += skip;
        for (int i = skip; i < count; i++) _ring[i - skip] = rows[i];
        Count = count - skip;
        SetUncompacted(Count); // re-wrapped rows may be full width again
        _hasUnpackedRows = false; // covered by that: every row gets looked at, and line numbers changed
        _readFrom = long.MaxValue;
        _readTo = long.MinValue;
        _repackEnd = 0;
    }

    /// <summary>
    /// Changes the capacity in place (config reload), keeping the newest rows. Rows that no longer fit are
    /// dropped from the old end and counted in <see cref="Dropped"/>, so absolute line numbers stay coherent.
    /// </summary>
    public void SetCapacity(int capacity)
    {
        capacity = Math.Max(0, capacity);
        if (capacity == _ring.Length) return;
        int keep = Math.Min(Count, capacity);
        var ring = new Row[capacity];
        for (int i = 0; i < keep; i++) ring[i] = Peek(Count - keep + i);
        Dropped += Count - keep;
        _ring = ring;
        _start = 0;
        _spareRows = 0;
        Count = keep;
        SetUncompacted(_uncompacted);
    }

    public void Clear()
    {
        Dropped += Count;
        Array.Clear(_ring);
        _start = 0;
        _spareRows = 0;
        Count = 0;
        SetUncompacted(0);
    }

    /// <summary>
    /// Empties the scrollback but leaves its full-width pinned rows in their slots, for <see cref="Push"/> to reuse. Their
    /// arrays only go back to the system in a full GC, so without this a flood after a clear would pay for a second
    /// scrollback on top of the first. Other rows are let go as usual.
    /// </summary>
    public void ClearKeepingPinnedRows()
    {
        Dropped += Count;
        for (int i = 0; i < Count; i++)
        {
            int slot = (_start + i) % _ring.Length;
            Row row = _ring[slot];
            if (row.CellsPinned && !row.IsTrimmed) _spareRows++;
            else _ring[slot] = null!;
        }
        Count = 0;
        SetUncompacted(0);
    }

    /// <summary>Lets go of the spare rows and returns how many there were.</summary>
    public int ReleaseSpareRows()
    {
        int released = _spareRows;
        if (released == 0) return 0;
        for (int i = Count; i < _ring.Length; i++) _ring[(_start + i) % _ring.Length] = null!;
        _spareRows = 0;
        return released;
    }
}
