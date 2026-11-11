using System.Text;

namespace ErikTerm.Core;

/// <summary>Text taken off the grid: selections (soft-wrapped rows joined), words, rows and cells.</summary>
internal static class ScreenText
{
    /// <summary>Text between two absolute positions (inclusive), joining soft-wrapped rows and trimming line ends.</summary>
    public static string Between(ITerminalLines lines, long startLine, int startCol, long endLine, int endCol, bool block)
    {
        if (startLine > endLine || (startLine == endLine && startCol > endCol))
            (startLine, startCol, endLine, endCol) = (endLine, endCol, startLine, startCol);
        var sb = new StringBuilder();
        for (long line = startLine; line <= endLine; line++)
        {
            var row = lines.GetLine(line);
            if (row is null) continue;
            int from = block || line == startLine ? startCol : 0;
            int to = block || line == endLine ? endCol : row.Width - 1;
            int lineStart = sb.Length;
            bool continues = !block && row.Wrapped && line != endLine && to >= row.Width - 1;
            AppendCells(sb, row, from, LastTextColumn(lines, row, line, to, continues));
            if (!continues) EndTextLine(sb, lineStart, newline: line != endLine);
        }
        return sb.ToString();
    }

    /// <summary>The last column of a row that <see cref="Between"/> copies; the blank padding before a wide character that wrapped doesn't count.</summary>
    private static int LastTextColumn(ITerminalLines lines, Row row, long line, int to, bool continues)
    {
        int last = Math.Min(to, row.Cells.Length - 1);
        if (continues && last >= 0 && lines.GetLine(line + 1) is { } next && RowContent.IsWidePadding(row, next)) last--;
        return last;
    }

    private static void AppendCells(StringBuilder sb, Row row, int from, int last)
    {
        for (int x = Math.Max(0, from); x <= last; x++) AppendCell(sb, row, x);
    }

    /// <summary>Removes the trailing spaces of the text line that started at <paramref name="lineStart"/>, then ends it.</summary>
    private static void EndTextLine(StringBuilder sb, int lineStart, bool newline)
    {
        int end = sb.Length;
        while (end > lineStart && sb[end - 1] == ' ') end--;
        sb.Length = end;
        if (newline) sb.Append('\n');
    }

    /// <summary>Column range of the "word" under a position in <paramref name="row"/> (for double-click selection).</summary>
    public static (int Start, int End) WordAt(Row? row, int col)
    {
        if (row is null || row.Width == 0) return (col, col);
        col = Math.Clamp(col, 0, row.Width - 1);
        if ((row.CellAt(col).Flags & CellAttributes.WideTrail) != 0 && col > 0) col--;
        bool word = IsWordChar(row.CellAt(col).Rune);
        int start = col, end = col;
        while (start > 0 && SameClass(row.CellAt(start - 1))) start--;
        if (!word && end >= row.Cells.Length) end = row.Width - 1; // the unstored blank tail of a scrollback row
        while (end < row.Width - 1 && SameClass(row.CellAt(end + 1))) end++;
        return (start, end);

        bool SameClass(in Cell c) => (c.Flags & CellAttributes.WideTrail) != 0 || IsWordChar(c.Rune) == word && (word || c.Rune is 0 or ' ');
    }

    private static bool IsWordChar(int rune) =>
        rune > 0x20 && (rune > 0x7F || char.IsLetterOrDigit((char)rune) || "_-./~:@#%+=?&".Contains((char)rune));

    /// <summary>A row's text, trailing blanks trimmed.</summary>
    public static string Row(Row row, int capacity)
    {
        var sb = new StringBuilder(capacity);
        for (int x = 0; x < row.Cells.Length; x++) AppendCell(sb, row, x);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Appends a cell's text: its grapheme cluster, its code point, or a space if empty (wide trails add nothing).</summary>
    private static void AppendCell(StringBuilder sb, Row row, int x)
    {
        ref readonly var c = ref row.Cells[x];
        if ((c.Flags & CellAttributes.WideTrail) != 0) return;
        if ((c.Flags & CellAttributes.Cluster) != 0 && row.TryGetCluster(x, out var cluster)) sb.Append(cluster);
        else if (c.Rune == 0) sb.Append(' ');
        else if (c.Rune < 0x10000) sb.Append((char)c.Rune);
        else sb.Append(char.ConvertFromUtf32(c.Rune));
    }

    /// <summary>Text of a cell (cluster, code point, or "" when empty / a wide trail).</summary>
    public static string Cell(Row row, int x)
    {
        ref readonly var c = ref row.Cells[x];
        if ((c.Flags & CellAttributes.WideTrail) != 0 || c.Rune == 0) return "";
        return row.TryGetCluster(x, out var cluster) ? cluster.ToString() : char.ConvertFromUtf32(c.Rune);
    }
}
