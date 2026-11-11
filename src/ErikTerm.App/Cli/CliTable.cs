using System.Text;

namespace ErikTerm.App.Cli;

/// <summary>The fixed-width text tables <c>erikterm cli</c> prints.</summary>
internal static class CliTable
{
    /// <summary>
    /// Appends the rows, one per line: the first <paramref name="rightAligned"/> columns pad on the left, the others on
    /// the right, except the last column, which isn't padded. Trailing spaces are removed.
    /// </summary>
    public static void Append(StringBuilder sb, IReadOnlyList<string[]> rows, int rightAligned)
    {
        var widths = new int[rows[0].Length];
        foreach (var r in rows)
            for (int i = 0; i < r.Length; i++) widths[i] = Math.Max(widths[i], r[i].Length);

        foreach (var r in rows)
        {
            AppendRow(sb, r, widths, rightAligned);
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
            sb.Append('\n');
        }
    }

    private static void AppendRow(StringBuilder sb, string[] row, int[] widths, int rightAligned)
    {
        for (int i = 0; i < row.Length; i++)
        {
            bool last = i == row.Length - 1;
            if (i < rightAligned) sb.Append(row[i].PadLeft(widths[i]));
            else if (last) sb.Append(row[i]);
            else sb.Append(row[i].PadRight(widths[i]));
            if (!last) sb.Append(' ');
        }
    }
}
