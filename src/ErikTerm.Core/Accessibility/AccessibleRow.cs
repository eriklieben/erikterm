using System.Text;

namespace ErikTerm.Core.Accessibility;

/// <summary>
/// A position in a terminal's text for screen readers: an absolute line (see <see cref="Terminal.FirstLine"/>, so it
/// stays on its text while output scrolls) and the column a character starts in. The end of the document is
/// (<see cref="IAccessibleRows.EndLine"/>, 0).
/// </summary>
public readonly record struct TextPoint(long Line, int Col) : IComparable<TextPoint>
{
    public int CompareTo(TextPoint other) => Line != other.Line ? Line.CompareTo(other.Line) : Col.CompareTo(other.Col);

    public static bool operator <(TextPoint a, TextPoint b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPoint a, TextPoint b) => a.CompareTo(b) > 0;
    public static bool operator <=(TextPoint a, TextPoint b) => a.CompareTo(b) <= 0;
    public static bool operator >=(TextPoint a, TextPoint b) => a.CompareTo(b) >= 0;

    public static TextPoint Min(TextPoint a, TextPoint b) => a <= b ? a : b;
    public static TextPoint Max(TextPoint a, TextPoint b) => a >= b ? a : b;
}

/// <summary>
/// One terminal row as characters, the way a screen reader counts them: a wide character is one character over two
/// columns, a grapheme cluster (base plus combining marks, an emoji ZWJ sequence) is one character, and every blank
/// cell is a space. Rows are padded or cut to the terminal's width, so every row has a character at every column a
/// character can start in. Immutable: decoded once under the terminal's lock and then read from any thread.
/// </summary>
public sealed class AccessibleRow
{
    public static readonly AccessibleRow Empty = new("", [0], [0], 0, wrapped: false);

    private readonly string _text;
    /// <summary>UTF-16 offset of character i in <see cref="_text"/>; one more entry, the text's length, at the end.</summary>
    private readonly int[] _offsets;
    /// <summary>Column character i starts in; one more entry, the row's width, at the end.</summary>
    private readonly int[] _columns;

    private AccessibleRow(string text, int[] offsets, int[] columns, int width, bool wrapped)
    {
        _text = text;
        _offsets = offsets;
        _columns = columns;
        Width = width;
        Wrapped = wrapped;
        int end = Count;
        while (end > 0 && IsBlank(end - 1)) end--;
        TextEnd = end;
    }

    /// <summary>Columns in the row.</summary>
    public int Width { get; }

    /// <summary>The line continues on the next row (a soft wrap).</summary>
    public bool Wrapped { get; }

    /// <summary>Characters in the row (blank cells included).</summary>
    public int Count => _columns.Length - 1;

    /// <summary>Index of the character after the last one that isn't blank: 0 for a blank row.</summary>
    public int TextEnd { get; }

    public int Column(int index) => _columns[index];

    /// <summary>The text of characters [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public ReadOnlySpan<char> Text(int from, int to) => _text.AsSpan(_offsets[from], _offsets[to] - _offsets[from]);

    public ReadOnlySpan<char> CharText(int index) => Text(index, index + 1);

    public bool IsBlank(int index) => _offsets[index + 1] - _offsets[index] == 1 && _text[_offsets[index]] == ' ';

    /// <summary>The character covering <paramref name="col"/> (the lead of a wide character for its second column); <see cref="Count"/> at or past the row's end.</summary>
    public int CharAt(int col)
    {
        if (col >= Width) return Count;
        if (col <= 0) return 0;
        int i = Array.BinarySearch(_columns, 0, Count, col);
        return i >= 0 ? i : ~i - 1;
    }

    /// <summary>Decodes <paramref name="row"/> at <paramref name="width"/> columns (call under the terminal's lock).</summary>
    public static AccessibleRow From(Row? row, int width)
    {
        if (row is null || width <= 0) return width <= 0 ? Empty : Blank(width, wrapped: false);
        var sb = new StringBuilder(width + 8);
        var offsets = new List<int>(width + 1);
        var columns = new List<int>(width + 1);
        var cells = row.Cells;
        for (int x = 0; x < width; x++)
        {
            if (x < cells.Length && (cells[x].Flags & CellAttributes.WideTrail) != 0 && columns.Count > 0) continue;
            offsets.Add(sb.Length);
            columns.Add(x);
            AppendCell(sb, row, x);
        }
        offsets.Add(sb.Length);
        columns.Add(width);
        return new AccessibleRow(sb.ToString(), [.. offsets], [.. columns], width, row.Wrapped);
    }

    /// <summary>A row of <paramref name="width"/> spaces.</summary>
    public static AccessibleRow Blank(int width, bool wrapped)
    {
        var offsets = new int[width + 1];
        for (int i = 0; i <= width; i++) offsets[i] = i;
        return new AccessibleRow(new string(' ', width), offsets, (int[])offsets.Clone(), width, wrapped);
    }

    /// <summary>A row from plain text, one column per character and two for wide ones (tests and fakes).</summary>
    public static AccessibleRow FromText(string text, int width, bool wrapped = false)
    {
        var row = new Row(width) { Wrapped = wrapped };
        int x = 0;
        foreach (int codepoint in text.EnumerateRunes().Select(rune => rune.Value))
        {
            if (x >= width) break;
            int w = CharWidth.Of(codepoint);
            if (w == 0 && x > 0) continue; // combining marks: the tests that need clusters build real rows
            row.Cells[x].Rune = codepoint;
            if (w == 2 && x + 1 < width)
            {
                row.Cells[x].Flags |= CellAttributes.WideLead;
                row.Cells[x + 1].Flags |= CellAttributes.WideTrail;
                x++;
            }
            x++;
        }
        return From(row, width);
    }

    private static void AppendCell(StringBuilder sb, Row row, int x)
    {
        if (x >= row.Cells.Length) { sb.Append(' '); return; } // the unstored blank tail of a scrollback row
        ref readonly var c = ref row.Cells[x];
        if ((c.Flags & CellAttributes.Cluster) != 0 && row.TryGetCluster(x, out var cluster)) sb.Append(cluster);
        else if (c.Rune <= 0x20 || (c.Flags & CellAttributes.Hidden) != 0) sb.Append(' ');
        else if (Rune.TryCreate(c.Rune, out var r)) sb.Append(r.ToString());
        else sb.Append('?');
    }
}
