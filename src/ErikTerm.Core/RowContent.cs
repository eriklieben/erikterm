namespace ErikTerm.Core;

/// <summary>How much of a row holds something, by the different measures resize, reflow, export and copy need.</summary>
internal static class RowContent
{
    /// <summary>Cells up to and including the last non-empty one (a printed space counts; erased cells do not).</summary>
    public static int ContentLength(Row row)
    {
        var cells = row.Cells;
        int i = cells.Length - 1;
        while (i >= 0 && cells[i].Rune == 0 && (cells[i].Flags & (CellAttributes.WideTrail | CellAttributes.Image)) == 0) i--;
        return i + 1;
    }

    /// <summary>A wrapped row ending in an empty cell before a wide character: the blank left when it didn't fit.</summary>
    public static bool IsWidePadding(Row row, Row next)
    {
        var last = row.CellAt(row.Width - 1);
        return last.Rune == 0 && (last.Flags & CellAttributes.WideTrail) == 0
            && (next.CellAt(0).Flags & CellAttributes.WideLead) != 0;
    }

    /// <summary>Cells up to the last one that shows something (a glyph, a background, inverse video).</summary>
    public static int ContentWidth(Row row)
    {
        int w = row.Cells.Length;
        while (w > 0)
        {
            ref readonly var c = ref row.Cells[w - 1];
            bool blank = c.Rune is 0 or ' ' && c.Bg == TermColor.Default && (c.Flags & CellAttributes.Inverse) == 0
                && (c.Flags & CellAttributes.WideTrail) == 0;
            if (!blank) break;
            w--;
        }
        return w;
    }

    /// <summary>A row that shows nothing and doesn't continue on the next one.</summary>
    public static bool IsBlankRow(Row row) => !row.Wrapped && ContentWidth(row) == 0;

    /// <summary>No glyph in the row's cells (spaces and controls don't count); a missing row is blank.</summary>
    public static bool HasNoText(Row? row)
    {
        if (row is null) return true;
        foreach (ref readonly var c in row.Cells.AsSpan())
            if (c.Rune > 0x20) return false;
        return true;
    }
}
