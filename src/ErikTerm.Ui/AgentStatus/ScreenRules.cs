namespace ErikTerm.Ui.AgentStatus;

/// <summary>The visible screen as text rows (top to bottom). Implementations reuse their buffers.</summary>
public interface IScreenText
{
    int RowCount { get; }
    /// <summary>Row <paramref name="y"/> without trailing blanks. Valid until the next call.</summary>
    ReadOnlySpan<char> Row(int y);
}

/// <summary>
/// One condition: some row among the bottom <see cref="BottomRows"/> rows (0 = the whole screen) contains one of
/// <see cref="AnyOf"/> (case-insensitive; empty = any row), and — when set — <see cref="AlsoOnRow"/> too, and
/// starts (after blanks) with one of the characters in <see cref="LeadingGlyphs"/>.
/// </summary>
public sealed record ScreenClause(string[] AnyOf, int BottomRows = 0, string? AlsoOnRow = null, string? LeadingGlyphs = null);

/// <summary>A screen rule: every clause holds ⇒ the agent is in <see cref="State"/>.</summary>
public sealed record ScreenRule(string Name, AgentState State, ScreenClause[] All, string? Message = null, string? Kind = null);

/// <summary>Evaluates an agent's rule table on a screen. The first rule that holds wins (tables list Waiting first).</summary>
public static class ScreenClassifier
{
    public static ScreenRule? Classify(ScreenRule[] rules, IScreenText screen) =>
        Array.Find(rules, rule => Array.TrueForAll(rule.All, clause => Holds(clause, screen)));

    public static bool Holds(ScreenClause c, IScreenText screen)
    {
        int n = screen.RowCount;
        int from = c.BottomRows > 0 ? Math.Max(0, n - c.BottomRows) : 0;
        for (int y = n - 1; y >= from; y--)
            if (RowHolds(c, screen.Row(y))) return true;
        return false;
    }

    private static bool RowHolds(ScreenClause c, ReadOnlySpan<char> row)
    {
        if (row.IsEmpty) return false;
        if (c.LeadingGlyphs is { } glyphs)
        {
            var t = row.TrimStart();
            if (t.IsEmpty || !StartsWithAny(t, glyphs)) return false;
        }
        if (c.AlsoOnRow is { } also && row.IndexOf(also, StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (c.AnyOf.Length == 0) return true;
        foreach (var s in c.AnyOf)
            if (row.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static bool StartsWithAny(ReadOnlySpan<char> t, string glyphs)
    {
        // Glyphs may be surrogate pairs: compare by rune.
        if (System.Text.Rune.DecodeFromUtf16(t, out var first, out _) != System.Buffers.OperationStatus.Done) return false;
        Span<char> utf16 = stackalloc char[2];
        return glyphs.AsSpan().IndexOf(utf16[..first.EncodeToUtf16(utf16)], StringComparison.Ordinal) >= 0;
    }
}

/// <summary>A screen built from strings (tests, tools).</summary>
public sealed class StringScreen(params string[] rows) : IScreenText
{
    public int RowCount => rows.Length;
    public ReadOnlySpan<char> Row(int y) => rows[y].AsSpan().TrimEnd();
}
