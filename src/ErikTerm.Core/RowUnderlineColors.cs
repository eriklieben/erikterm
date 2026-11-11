namespace ErikTerm.Core;

/// <summary>
/// A row's SGR 58 underline colors, kept beside the cells (<see cref="Cell"/> stays 16 bytes): one packed
/// <see cref="TermColor"/> per column, made the first time a row shows a colored underline. Like clusters, the
/// cell's flag is authoritative: an entry only means something for a cell with <see cref="CellAttributes.Underline"/>, and
/// every writer of an underlined cell writes its entry (0 = the text color), so entries left behind by erased or
/// overwritten cells are never observed. The table is independent of the row's cells' length (rows can be
/// resized or trimmed), and lookups past its end read 0.
/// <para>
/// The row keeps the table and whether it is in use as two fields of its own and these functions work on them: held in
/// a struct, the flag's padding made every row 8 bytes larger (measured: 0.8 MB per 100,000 scrollback lines).
/// Bumping the row's version is the row's job.
/// </para>
/// </summary>
internal static class RowUnderlineColors
{
    /// <summary>The color at <paramref name="col"/>; 0 when the table isn't <paramref name="active"/> or is shorter.</summary>
    public static uint At(uint[]? table, bool active, int col) =>
        active && table is not null && (uint)col < (uint)table.Length ? table[col] : 0;

    /// <summary>
    /// Records the color of cells [col, col + count) on a row of <paramref name="cells"/> stored cells, making or
    /// reusing the table (an inactive one is cleared first).
    /// </summary>
    public static void Set(ref uint[]? table, ref bool active, int col, int count, uint color, int cells)
    {
        if (!active)
        {
            int size = Math.Max(cells, col + count);
            if (table is null || table.Length < size) table = new uint[size];
            else Array.Clear(table);
            active = true;
        }
        else if (table!.Length < col + count)
        {
            Array.Resize(ref table, Math.Max(col + count, cells));
        }
        table.AsSpan(col, count).Fill(color);
    }

    /// <summary>The same Array.Copy as the cells got (insert/delete characters).</summary>
    public static void Copy(uint[]? table, bool active, int from, int to, int count)
    {
        if (!active || table is null || count <= 0) return;
        int limit = table.Length;
        count = Math.Min(count, Math.Min(limit - from, limit - to));
        if (count > 0) Array.Copy(table, from, table, to, count);
    }
}
