using System.Text;

namespace ErikTerm.Core;

/// <summary>A styled text export of the main screen and scrollback, for session restore (<see cref="Terminal.ExportStyledLines"/>).</summary>
internal static class StyledTextExport
{
    /// <summary>
    /// The last <paramref name="maxLines"/> logical lines of the main screen and its scrollback (soft-wrapped rows
    /// joined, trailing blank rows skipped), each as text with SGR sequences for colors and attributes. Each line
    /// starts from default attributes. Hyperlinks and other metadata are not included.
    /// </summary>
    public static List<string> Export(Scrollback scrollback, Row[] main, int maxLines)
    {
        var result = new List<string>();
        if (maxLines <= 0) return result;

        int screenRows = main.Length;
        while (screenRows > 0 && RowContent.IsBlankRow(main[screenRows - 1])) screenRows--;
        int total = scrollback.Count + screenRows;
        var starts = LogicalLineStarts(scrollback, main, total, maxLines);

        var sb = new StringBuilder();
        for (int k = 0; k < starts.Count; k++)
        {
            int from = starts[k], to = k + 1 < starts.Count ? starts[k + 1] : total;
            sb.Clear();
            var style = default(Style);
            for (int i = from; i < to; i++) AppendStyledRow(sb, ExportRow(scrollback, main, i), last: i == to - 1, ref style);
            if (style != default) sb.Append("\e[0m");
            result.Add(sb.ToString());
        }
        return result;
    }

    /// <summary>Row <paramref name="i"/> of the export: the scrollback from the oldest line, then the main screen.</summary>
    private static Row ExportRow(Scrollback scrollback, Row[] main, int i) => i < scrollback.Count ? scrollback[i] : main[i - scrollback.Count];

    /// <summary>The first rows of the last <paramref name="maxLines"/> logical lines, found from the bottom up and returned oldest first.</summary>
    private static List<int> LogicalLineStarts(Scrollback scrollback, Row[] main, int total, int maxLines)
    {
        var starts = new List<int>();
        int end = total;
        while (end > 0 && starts.Count < maxLines)
        {
            int start = end - 1;
            while (start > 0 && ExportRow(scrollback, main, start - 1).Wrapped) start--;
            starts.Add(start);
            end = start;
        }
        starts.Reverse();
        return starts;
    }

    /// <summary>Appends a row's cells as text, with an SGR sequence wherever the style changes; a line's last row stops at its content.</summary>
    private static void AppendStyledRow(StringBuilder sb, Row row, bool last, ref Style style)
    {
        int width = last ? RowContent.ContentWidth(row) : row.Cells.Length;
        for (int x = 0; x < width; x++)
        {
            ref readonly var c = ref row.Cells[x];
            if ((c.Flags & CellAttributes.WideTrail) != 0) continue;
            var attr = c.Flags & CellAttributes.SgrMask;
            var s = new Style(c.Fg, c.Bg, attr, (attr & CellAttributes.Underline) != 0 ? row.UnderlineColorAt(x) : 0);
            if (s != style) { AppendSgr(sb, s); style = s; }
            AppendCellText(sb, row, x, in c);
        }
    }

    /// <summary>A cell's text for the export: its cluster or code point, with controls and empty cells as spaces.</summary>
    private static void AppendCellText(StringBuilder sb, Row row, int x, in Cell c)
    {
        if ((c.Flags & CellAttributes.Cluster) != 0 && row.TryGetCluster(x, out var cluster)) AppendPrintable(sb, cluster);
        else if (c.Rune is 0 or < 0x20 or (>= 0x7F and < 0xA0)) sb.Append(' ');
        else if (c.Rune < 0x10000) sb.Append((char)c.Rune);
        else sb.Append(char.ConvertFromUtf32(c.Rune));
    }

    private readonly record struct Style(uint Fg, uint Bg, CellAttributes Attr, uint UnderlineColor);

    private static void AppendPrintable(StringBuilder sb, ReadOnlySpan<char> text)
    {
        foreach (char ch in text) sb.Append(ch < 0x20 || ch is >= '\x7f' and < '\xa0' ? ' ' : ch);
    }

    private static void AppendSgr(StringBuilder sb, Style s)
    {
        sb.Append("\e[");
        SgrText.AppendParams(sb, s.Fg, s.Bg, s.Attr, s.UnderlineColor, colon: false);
        sb.Append('m');
    }
}
