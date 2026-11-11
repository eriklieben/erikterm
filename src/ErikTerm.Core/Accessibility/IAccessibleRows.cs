namespace ErikTerm.Core.Accessibility;

/// <summary>
/// The rows a screen reader reads: scrollback and screen, by absolute line. What <see cref="AccessibleText"/>
/// navigates over; <see cref="TerminalRows"/> reads them from a live terminal, tests pass fixed rows.
/// </summary>
public interface IAccessibleRows
{
    /// <summary>The oldest line still kept.</summary>
    long FirstLine { get; }

    /// <summary>The line after the last one with content (the cursor's line counts as content).</summary>
    long EndLine { get; }

    /// <summary>The row of an absolute line; <see cref="AccessibleRow.Empty"/> outside [FirstLine, EndLine).</summary>
    AccessibleRow Row(long line);
}

/// <summary>Fixed rows for tests: line <see cref="FirstLine"/> + i is <c>rows[i]</c>.</summary>
public sealed class FixedRows(IReadOnlyList<AccessibleRow> rows, long firstLine = 0) : IAccessibleRows
{
    public long FirstLine => firstLine;
    public long EndLine => firstLine + rows.Count;

    public AccessibleRow Row(long line) =>
        line >= firstLine && line < EndLine ? rows[(int)(line - firstLine)] : AccessibleRow.Empty;

    /// <summary>Rows from lines of text; a line ending in '\\' is soft-wrapped into the next (the '\\' isn't kept).</summary>
    public static FixedRows Of(int width, params string[] lines) =>
        new([.. lines.Select(l => l.EndsWith('\\') ? AccessibleRow.FromText(l[..^1], width, wrapped: true) : AccessibleRow.FromText(l, width))]);
}
