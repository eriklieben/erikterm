using System.Text.RegularExpressions;

namespace ErikTerm.Core;

/// <summary>
/// The query of a scrollback search and matching it against one logical line, without any scanning policy: used by
/// the per-pane <see cref="TerminalSearch"/> and by searching every pane at once. Safe to use from one thread at a time.
/// </summary>
public sealed class SearchLineMatcher
{
    /// <summary>A hit in the line's text. Return false to stop looking in this line.</summary>
    public delegate bool MatchFound(int index, int length);

    private Regex? _compiled;

    public string Pattern { get; private set; } = "";
    public bool CaseSensitive { get; private set; }
    public bool UseRegex { get; private set; }

    /// <summary>Invalid regular expression message, or null.</summary>
    public string? Error { get; private set; }

    public bool HasQuery => Pattern.Length > 0 && Error is null && (!UseRegex || _compiled is not null);

    /// <summary>Sets the query; returns true when it changed.</summary>
    public bool SetQuery(string pattern, bool caseSensitive, bool regex)
    {
        if (pattern == Pattern && caseSensitive == CaseSensitive && regex == UseRegex) return false;
        Pattern = pattern;
        CaseSensitive = caseSensitive;
        UseRegex = regex;
        Error = null;
        _compiled = null;
        if (regex && pattern.Length > 0)
        {
            try
            {
                var options = RegexOptions.CultureInvariant | (caseSensitive ? 0 : RegexOptions.IgnoreCase);
                _compiled = new Regex(pattern, options, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                Error = ex.Message;
            }
        }
        return true;
    }

    /// <summary>Reports every match in <paramref name="text"/>. Returns false when the regex timed out on it.</summary>
    public bool Find(ReadOnlySpan<char> text, MatchFound found)
    {
        if (_compiled is { } re)
        {
            try
            {
                foreach (var m in re.EnumerateMatches(text))
                    if (m.Length > 0 && !found(m.Index, m.Length)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
            return true;
        }

        var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var pattern = Pattern.AsSpan();
        int offset = 0;
        while (offset < text.Length)
        {
            int i = text[offset..].IndexOf(pattern, comparison);
            if (i < 0) break;
            if (!found(offset + i, pattern.Length)) return true;
            offset += i + pattern.Length;
        }
        return true;
    }
}

/// <summary>A logical line held by a <see cref="LogicalLineBuffer"/>: where its text is and which absolute line it starts on.</summary>
public readonly record struct LogicalLineSpan(long StartLine, int Offset, int Length);

/// <summary>
/// Text of logical lines (soft-wrapped rows joined) plus, per UTF-16 unit, the row and cells it came from, so a match can
/// be turned back into terminal coordinates. Reading a line needs the terminal's lock; using what was read doesn't.
/// </summary>
public sealed class LogicalLineBuffer
{
    /// <summary>Longest logical line searched as one piece, in rows.</summary>
    public const int MaxRows = 512;

    private char[] _text = new char[1024];
    private int[] _row = new int[1024], _col = new int[1024], _colEnd = new int[1024];
    private int _length;

    public void Clear() => _length = 0;

    /// <summary>The first row of the logical line <paramref name="line"/> is on (looks back at most <see cref="MaxRows"/> rows).</summary>
    public static long LogicalStart(Terminal t, long line)
    {
        for (int i = 0; i < MaxRows && line > t.FirstLine; i++)
        {
            if (t.GetLine(line - 1) is not { Wrapped: true }) break;
            line--;
        }
        return line;
    }

    /// <summary>Appends the logical line starting at <paramref name="start"/> (trailing blanks dropped); returns how many rows it spans.</summary>
    public int AppendLine(Terminal t, long start, long end, out LogicalLineSpan span)
    {
        int offset = _length, rows = 0;
        int lastNonBlank = offset; // text length up to the last non-blank cell
        while (start + rows < end && rows < MaxRows)
        {
            var row = t.GetLine(start + rows);
            rows++;
            if (row is null) break;
            AppendRow(row.Cells, rows - 1, ref lastNonBlank); // a trimmed row just has fewer cells: its tail was blank
            if (!row.Wrapped) break;
        }
        _length = lastNonBlank;
        span = new LogicalLineSpan(start, offset, lastNonBlank - offset);
        return Math.Max(1, rows);
    }

    public ReadOnlySpan<char> TextOf(in LogicalLineSpan span) => _text.AsSpan(span.Offset, span.Length);

    /// <summary>A match at <paramref name="index"/> of the line's text, in absolute line coordinates.</summary>
    public SearchMatch Map(in LogicalLineSpan span, int index, int length)
    {
        int first = span.Offset + index, last = first + length - 1;
        return new SearchMatch(span.StartLine + _row[first], _col[first], span.StartLine + _row[last], _colEnd[last]);
    }

    /// <summary>Where a match sits in the line's text (the inverse of <see cref="Map"/>), or null when no cells line up with it.</summary>
    public (int Index, int Length)? Locate(in LogicalLineSpan span, SearchMatch match)
    {
        int firstRow = (int)(match.StartLine - span.StartLine), lastRow = (int)(match.EndLine - span.StartLine);
        int end = span.Offset + span.Length, first = -1;
        for (int i = span.Offset; i < end; i++)
        {
            if (first < 0)
            {
                if (_row[i] != firstRow || _col[i] != match.StartCol) continue;
                first = i;
            }
            if (_row[i] == lastRow && _colEnd[i] == match.EndCol)
            {
                int lastUnit = i;
                while (lastUnit + 1 < end && _row[lastUnit + 1] == lastRow && _col[lastUnit + 1] == _col[lastUnit]) lastUnit++; // the other half of a surrogate pair
                return (first - span.Offset, lastUnit - first + 1);
            }
        }
        return null;
    }

    /// <summary>Appends a row's text, recording for each UTF-16 unit the cell it came from.</summary>
    private void AppendRow(Cell[] cells, int rowIndex, ref int lastNonBlank)
    {
        for (int x = 0; x < cells.Length; x++)
        {
            ref readonly var c = ref cells[x];
            if ((c.Flags & CellAttributes.WideTrail) != 0) continue;
            int last = (c.Flags & CellAttributes.WideLead) != 0 ? Math.Min(x + 1, cells.Length - 1) : x;
            int rune = c.Rune;
            if (rune is < 0x20 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF)) rune = ' ';
            int units = rune > 0xFFFF ? 2 : 1;
            EnsureCapacity(_length + units);
            StoreRune(rune, units, _length);
            for (int u = 0; u < units; u++)
            {
                _row[_length + u] = rowIndex;
                _col[_length + u] = x;
                _colEnd[_length + u] = last;
            }
            _length += units;
            if (rune != ' ') lastNonBlank = _length;
        }
    }

    private void StoreRune(int rune, int units, int at)
    {
        if (units == 1)
        {
            _text[at] = (char)rune;
            return;
        }
        int v = rune - 0x10000;
        _text[at] = (char)(0xD800 + (v >> 10));
        _text[at + 1] = (char)(0xDC00 + (v & 0x3FF));
    }

    private void EnsureCapacity(int n)
    {
        if (n <= _text.Length) return;
        int size = Math.Max(n, _text.Length * 2);
        Array.Resize(ref _text, size);
        Array.Resize(ref _row, size);
        Array.Resize(ref _col, size);
        Array.Resize(ref _colEnd, size);
    }
}
