namespace ErikTerm.Core;

/// <summary>A search hit in absolute line coordinates (see <see cref="Terminal.FirstLine"/>); the end cell is inclusive.</summary>
public readonly record struct SearchMatch(long StartLine, int StartCol, long EndLine, int EndCol)
{
    /// <summary>True when (line, col) is at or after this match's start.</summary>
    public bool StartsAtOrBefore(long line, int col) => StartLine < line || (StartLine == line && StartCol <= col);
}

/// <summary>
/// Incremental scrollback search. Matches are found per logical line (soft-wrapped rows joined), so a
/// word broken across a wrap is still found, and are kept in absolute line numbers so they stay on
/// their text while output scrolls.
/// <para>
/// <see cref="Step"/> scans a bounded number of rows per call (call it with the terminal locked, once
/// per frame) so a 10k-line history never blocks a frame. Results are cached against
/// <see cref="Terminal.ContentVersion"/>: when output arrives only the live screen (whose rows can
/// still change) and what scrolled in after it is rescanned — scrollback rows are immutable.
/// </para>
/// </summary>
public sealed class TerminalSearch
{
    public const int MaxMatches = 100_000;

    private readonly List<SearchMatch> _matches = [];
    private readonly SearchLineMatcher _matcher = new();
    private readonly LogicalLineBuffer _line = new();
    private readonly SearchLineMatcher.MatchFound _onMatch;
    private LogicalLineSpan _span; // the logical line being matched

    // Scan state.
    private bool _started;
    private long _next;          // next absolute line to scan; matches before it are valid
    private long _version;       // Terminal.ContentVersion at the last step
    private long _screenTop;     // Terminal.ScreenTopLine at the last step: rows from here on may change
    private int _cols, _rows;
    private bool _alt;
    private int _reflow;

    public TerminalSearch() => _onMatch = Add;

    public IReadOnlyList<SearchMatch> Matches => _matches;
    public string Pattern => _matcher.Pattern;
    public bool CaseSensitive => _matcher.CaseSensitive;
    public bool UseRegex => _matcher.UseRegex;

    /// <summary>Invalid regular expression message, or null.</summary>
    public string? Error => _matcher.Error;

    /// <summary>True when every line currently in the terminal has been scanned.</summary>
    public bool Complete { get; private set; } = true;

    /// <summary>More than <see cref="MaxMatches"/> hits, or the pattern took too long on a line; the rest are not listed.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Bumped whenever <see cref="Matches"/> changes.</summary>
    public int Revision { get; private set; }

    /// <summary>Sets the query; returns true (and restarts the scan) when it changed.</summary>
    public bool SetQuery(string pattern, bool caseSensitive, bool regex)
    {
        if (!_matcher.SetQuery(pattern, caseSensitive, regex)) return false;
        Restart();
        return true;
    }

    /// <summary>Forgets all results; the next <see cref="Step"/> scans from the top.</summary>
    public void Restart()
    {
        _started = false;
        Truncated = false;
        Complete = !HasQuery;
        if (_matches.Count > 0) { _matches.Clear(); Revision++; }
    }

    private bool HasQuery => _matcher.HasQuery;

    /// <summary>Scans up to <paramref name="maxRows"/> rows. Call with the terminal locked. Returns true when <see cref="Matches"/> changed.</summary>
    public bool Step(Terminal t, int maxRows = 4000)
    {
        if (!HasQuery) { Complete = true; return false; }
        int before = Revision;

        if (!_started || t.Columns != _cols || t.Rows != _rows || t.IsAlternateScreen != _alt || t.ReflowCount != _reflow)
        {
            if (_matches.Count > 0) { _matches.Clear(); Revision++; }
            Truncated = false;
            _started = true;
            _next = t.FirstLine;
            _cols = t.Columns;
            _rows = t.Rows;
            _alt = t.IsAlternateScreen;
            _reflow = t.ReflowCount; // reflow renumbers lines: old matches point at other text
            _version = t.ContentVersion;
            _screenTop = t.ScreenTopLine;
        }
        else if (t.ContentVersion != _version)
        {
            // Rows from the old screen top on may have changed; everything above is settled history.
            long from = LogicalStart(t, Math.Min(_next, _screenTop));
            DropFrom(from);
            _next = Math.Min(_next, from);
            _version = t.ContentVersion;
        }
        _screenTop = t.ScreenTopLine;

        // Lines that fell off the top of the scrollback.
        long first = t.FirstLine;
        if (_next < first) _next = first;
        int drop = 0;
        while (drop < _matches.Count && _matches[drop].StartLine < first) drop++;
        if (drop > 0) { _matches.RemoveRange(0, drop); Revision++; }

        long end = t.EndLine;
        int scanned = 0;
        while (_next < end && scanned < maxRows)
        {
            int rows = ScanLogicalLine(t, _next, end);
            _next += rows;
            scanned += rows;
        }
        Complete = _next >= end;
        return Revision != before;
    }

    private void DropFrom(long line)
    {
        int i = LowerBound(line);
        if (i < _matches.Count)
        {
            _matches.RemoveRange(i, _matches.Count - i);
            Truncated = false;
            Revision++;
        }
    }

    private static long LogicalStart(Terminal t, long line) => LogicalLineBuffer.LogicalStart(t, line);

    /// <summary>Scans the logical line starting at <paramref name="start"/>; returns how many rows it spans.</summary>
    private int ScanLogicalLine(Terminal t, long start, long end)
    {
        _line.Clear();
        int rows = _line.AppendLine(t, start, end, out _span);
        if (_span.Length > 0 && !Truncated && !_matcher.Find(_line.TextOf(_span), _onMatch))
            Truncated = true; // this line's matches are missing, so the count isn't the whole story
        return rows;
    }

    private bool Add(int index, int length)
    {
        if (_matches.Count >= MaxMatches) { Truncated = true; return false; }
        _matches.Add(_line.Map(_span, index, length));
        Revision++;
        return true;
    }

    // ---- Queries ----------------------------------------------------------------------------

    /// <summary>Index of the first match starting on or after <paramref name="line"/> (Count if none).</summary>
    public int LowerBound(long line)
    {
        int lo = 0, hi = _matches.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_matches[mid].StartLine < line) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Index of the last match starting at or before (line, col), or -1.</summary>
    public int LastAtOrBefore(long line, int col)
    {
        int lo = 0, hi = _matches.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_matches[mid].StartsAtOrBefore(line, col)) lo = mid + 1; else hi = mid;
        }
        return lo - 1;
    }

    /// <summary>Index of an exact match, or -1 (used to keep the current match across rescans).</summary>
    public int IndexOf(SearchMatch m)
    {
        int i = LastAtOrBefore(m.StartLine, m.StartCol);
        return i >= 0 && _matches[i] == m ? i : -1;
    }
}
