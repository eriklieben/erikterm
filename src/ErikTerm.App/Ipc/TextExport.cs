using System.Text;
using ErikTerm.Core;

namespace ErikTerm.App.Ipc;

/// <summary>Text of a pane for <c>erikterm cli get-text</c>.</summary>
internal static class TextExport
{
    /// <summary>
    /// Resolves wezterm's line numbers — 0 is the top of the screen, negative numbers reach into the
    /// scrollback, default end is the last screen row — to an absolute range, clamped to what exists.
    /// Null when the range is empty.
    /// </summary>
    public static (long From, long To)? Range(Terminal t, int? startLine, int? endLine)
    {
        long top = t.ScreenTopLine;
        long from = Math.Max(t.FirstLine, top + (startLine ?? 0));
        long to = Math.Min(t.EndLine - 1, top + (endLine ?? t.Rows - 1));
        return from <= to ? (from, to) : null;
    }

    /// <summary>Plain text, one line per row (soft-wrapped rows joined), trailing blanks trimmed, each line ending in '\n'.</summary>
    public static string Plain(Terminal t, int? startLine, int? endLine) =>
        Range(t, startLine, endLine) is var (from, to) ? t.GetText(from, 0, to, t.Columns - 1) + "\n" : "";

    /// <summary>Plain text, one line per screen row (soft-wrapped rows not joined), trailing blanks trimmed — tmux capture-pane.</summary>
    public static string PlainRows(Terminal t, int? startLine, int? endLine)
    {
        if (Range(t, startLine, endLine) is not var (from, to)) return "";
        var sb = new StringBuilder();
        for (long line = from; line <= to; line++)
            sb.Append(t.GetText(line, 0, line, t.Columns - 1).TrimEnd(' ', '\n')).Append('\n');
        return sb.ToString();
    }

    /// <summary>Like <see cref="Plain"/> but one line per row, with SGR sequences for colors and attributes.</summary>
    public static string WithEscapes(Terminal t, int? startLine, int? endLine)
    {
        if (Range(t, startLine, endLine) is not var (from, to)) return "";
        var sb = new StringBuilder();
        for (long line = from; line <= to; line++)
        {
            if (t.GetLine(line) is { } row) AppendRow(sb, row);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, Row row)
    {
        var cells = row.Cells;
        int last = LastVisibleCell(cells);

        uint fg = 0, bg = 0, ul = 0;
        var flags = CellAttributes.None;
        for (int x = 0; x <= last; x++)
        {
            ref readonly var c = ref cells[x];
            if ((c.Flags & CellAttributes.WideTrail) != 0) continue;
            var f = c.Flags & CellAttributes.SgrMask;
            uint u = (f & CellAttributes.Underline) != 0 ? row.UnderlineColorAt(x) : 0;
            if (c.Fg != fg || c.Bg != bg || f != flags || u != ul)
            {
                sb.Append(SgrText.Sequence(c.Fg, c.Bg, f, u));
                (fg, bg, flags, ul) = (c.Fg, c.Bg, f, u);
            }
            AppendCell(sb, row, x, in c);
        }
        if (fg != 0 || bg != 0 || flags != CellAttributes.None) sb.Append("\e[0m");
    }

    /// <summary>The index of the last cell with something to export (blank default-colored cells at the end are not).</summary>
    private static int LastVisibleCell(Cell[] cells)
    {
        int last = cells.Length - 1;
        while (last >= 0 && cells[last].Rune is 0 or ' ' && TermColor.IsDefault(cells[last].Bg)
               && (cells[last].Flags & (CellAttributes.Inverse | CellAttributes.Underline | CellAttributes.Strike | CellAttributes.Overline)) == 0) last--;
        return last;
    }

    private static void AppendCell(StringBuilder sb, Row row, int x, in Cell c)
    {
        if ((c.Flags & CellAttributes.Cluster) != 0 && Terminal.TryGetCluster(row, x, out var cluster)) sb.Append(cluster);
        else if (c.Rune == 0) sb.Append(' ');
        else sb.Append(char.ConvertFromUtf32(c.Rune));
    }
}
