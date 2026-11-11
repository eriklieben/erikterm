using System.Text;

namespace ErikTerm.Core;

/// <summary>A cell position in absolute line coordinates.</summary>
public readonly record struct CellPos(long Line, int Col);

/// <summary>
/// Text of a range of lines with a map from every UTF-16 index back to its cell, so pattern matches
/// (URLs, quick select) can be drawn on the grid. Soft-wrapped rows join without a separator; hard
/// line ends become '\n'. Wide characters' trailing halves are skipped.
/// </summary>
public sealed class LineText
{
    private readonly StringBuilder _sb = new();
    private readonly List<CellPos> _map = [];

    public string Text { get; private set; } = "";

    /// <summary>Cell of the character at a UTF-16 index (for '\n', the column after the line's end).</summary>
    public CellPos this[int index] => _map[index];

    /// <summary>Last cell covered by the match [start, start + length).</summary>
    public CellPos LastCell(int start, int length) => _map[Math.Max(start, start + length - 1)];

    public void Build(Terminal t, long fromLine, long toLine)
    {
        _sb.Clear();
        _map.Clear();
        for (long line = fromLine; line <= toLine; line++)
        {
            var row = t.GetLine(line);
            if (row is null) continue;
            int lineStart = _sb.Length;
            AppendCells(row.Cells, line);
            if (!row.Wrapped || line == toLine) EndLine(line, lineStart);
        }
        Text = _sb.ToString();
    }

    /// <summary>Appends a row's characters, mapping each UTF-16 unit to its cell.</summary>
    private void AppendCells(Cell[] cells, long line)
    {
        for (int x = 0; x < cells.Length; x++)
        {
            ref readonly var c = ref cells[x];
            if ((c.Flags & CellAttributes.WideTrail) != 0) continue;
            var pos = new CellPos(line, x);
            if (c.Rune <= 0x20 || c.Rune > 0x10FFFF) { _sb.Append(' '); _map.Add(pos); }
            else if (c.Rune < 0x10000) { _sb.Append((char)c.Rune); _map.Add(pos); }
            else
            {
                _sb.Append(char.ConvertFromUtf32(c.Rune));
                _map.Add(pos);
                _map.Add(pos);
            }
        }
    }

    /// <summary>Trims the trailing spaces of the line that started at <paramref name="lineStart"/> and ends it with '\n'.</summary>
    private void EndLine(long line, int lineStart)
    {
        int end = _sb.Length;
        while (end > lineStart && _sb[end - 1] == ' ') end--;
        _sb.Length = end;
        _map.RemoveRange(end, _map.Count - end);
        _sb.Append('\n');
        _map.Add(new CellPos(line, end - lineStart));
    }

    /// <summary>Builds the logical line (soft wraps joined) containing <paramref name="line"/>.</summary>
    public void BuildLogicalLine(Terminal t, long line, int maxRows = 32)
    {
        long first = line, last = line;
        while (first > t.FirstLine && line - first < maxRows && t.GetLine(first - 1)?.Wrapped == true) first--;
        while (last + 1 < t.EndLine && last - line < maxRows && t.GetLine(last)?.Wrapped == true) last++;
        Build(t, first, last);
    }

    /// <summary>Index of the first character at <paramref name="pos"/>, or -1.</summary>
    public int IndexOf(CellPos pos)
    {
        for (int i = 0; i < _map.Count; i++)
            if (_map[i] == pos && Text[i] != '\n') return i;
        return -1;
    }
}
