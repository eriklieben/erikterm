namespace ErikTerm.Core.Accessibility;

/// <summary>
/// A live terminal's rows for a screen reader, read under the lock its output is fed under. Every call takes the lock
/// on its own and only for one row, so a screen reader reading the whole scrollback never holds up a frame for longer
/// than a row takes to decode. Decoded rows are cached: screen rows by the row and its version, scrollback rows by
/// line number (their text never changes until reflow renumbers them). Safe to call from any thread.
/// </summary>
public sealed class TerminalRows : IAccessibleRows
{
    /// <summary>Decoded rows kept before the cache starts over: a few screens' worth of reading.</summary>
    private const int CacheLimit = 4096;

    private readonly Terminal _term;
    private readonly Lock _gate;
    private readonly Dictionary<long, Entry> _cache = [];
    private int _cacheReflow = -1, _cacheColumns = -1;
    private (long Version, int Cursor, int Rows, int Reflow) _contentEndKey = (-1, -1, -1, -1);
    private long _contentEnd;

    private readonly record struct Entry(Row? Row, int Version, AccessibleRow Decoded);

    public TerminalRows(Terminal term, Lock gate)
    {
        _term = term;
        _gate = gate;
    }

    public Terminal Terminal => _term;

    public long FirstLine
    {
        get { lock (_gate) return _term.FirstLine; }
    }

    public long EndLine
    {
        get { lock (_gate) return ContentEnd(); }
    }

    public AccessibleRow Row(long line)
    {
        lock (_gate)
        {
            var t = _term;
            if (line < t.FirstLine || line >= t.EndLine) return AccessibleRow.Empty;
            if (t.ReflowCount != _cacheReflow || t.Columns != _cacheColumns || _cache.Count >= CacheLimit)
            {
                _cache.Clear();
                (_cacheReflow, _cacheColumns) = (t.ReflowCount, t.Columns);
            }
            bool onScreen = line >= t.ScreenTopLine;
            if (!onScreen && _cache.TryGetValue(line, out var kept) && kept.Row is null) return kept.Decoded;
            var row = t.GetLine(line);
            if (onScreen && _cache.TryGetValue(line, out kept) && ReferenceEquals(kept.Row, row) && kept.Version == row!.Version)
                return kept.Decoded;
            var decoded = AccessibleRow.From(row, t.Columns);
            _cache[line] = new Entry(onScreen ? row : null, row?.Version ?? 0, decoded);
            return decoded;
        }
    }

    /// <summary>
    /// The line after the last one with something on it: the empty rows below the prompt are not part of the
    /// document, or reading to the end would read a screenful of blank lines. Worked out once per change of output.
    /// </summary>
    private long ContentEnd()
    {
        var t = _term;
        int cursor = t.CursorY;
        var key = (t.ContentVersion, cursor, t.Rows, t.ReflowCount);
        if (key == _contentEndKey) return _contentEnd;
        int last = cursor;
        for (int y = t.Rows - 1; y > cursor; y--)
        {
            if (!IsBlank(t.GetScreenRow(y))) { last = y; break; }
        }
        (_contentEndKey, _contentEnd) = (key, t.ScreenTopLine + last + 1);
        return _contentEnd;
    }

    private static bool IsBlank(Row row)
    {
        foreach (ref readonly var c in row.Cells.AsSpan())
            if (c.Rune > 0x20) return false;
        return true;
    }
}
