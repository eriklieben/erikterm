namespace ErikTerm.Ui.Compare;

/// <summary>What a compare row is: the same, a real change, or a change the noise rules ignore.</summary>
public enum RowKind : byte { Equal, Important, Unimportant }

/// <summary>One aligned row; a side of -1 is a gap, <see cref="Block"/> is the difference block it belongs to or -1.</summary>
public readonly record struct CompareRow(int Left, int Right, RowKind Kind, int Block);

/// <summary>A run of consecutive differing rows; unimportant when every row in it is noise.</summary>
public readonly record struct DiffBlock(int FirstRow, int RowCount, bool Important);

/// <summary>The aligned result of a compare, and its counts as words.</summary>
public sealed record CompareResult(CompareRow[] Rows, DiffBlock[] Blocks)
{
    public int Differences => Blocks.Length;
    public int Unimportant => Blocks.Count(b => !b.Important);

    public string Summary
    {
        get
        {
            if (Differences == 0) return "No differences";
            string text = MergeSession.Counted(Differences, "difference");
            return Unimportant > 0 ? $"{text}, {Unimportant} unimportant" : text;
        }
    }
}

/// <summary>
/// Aligns two documents under the noise rules: an exact-matching diff over each side's significant lines (comment-only
/// and blank lines are dropped first, so they can't disturb the alignment), then the gaps between matched lines are
/// aligned line by line and every row classified. Ignored differences stay on screen, marked unimportant.
/// </summary>
public static class CompareEngine
{
    public static CompareResult Compute(TextDocument a, TextDocument b, NoiseRules rules, LanguageSyntax? syntaxA, LanguageSyntax? syntaxB)
    {
        var normA = Normalize(a.RawLines, rules, syntaxA);
        var normB = Normalize(b.RawLines, rules, syntaxB);

        var interner = new LineInterner();
        var (idsA, mapA) = Significant(normA, interner);
        var (idsB, mapB) = Significant(normB, interner);
        var hunks = SequenceDiff.Diff(idsA, idsB, interner);

        var rows = new RowBuilder(a.RawLines, b.RawLines, normA, normB, interner);
        int ka = 0, kb = 0;
        foreach (var h in hunks)
        {
            while (ka < h.AStart && kb < h.BStart) rows.Anchor(mapA[ka++], mapB[kb++]);
            ka = h.AEnd;
            kb = h.BEnd;
        }
        while (ka < mapA.Length && kb < mapB.Length) rows.Anchor(mapA[ka++], mapB[kb++]);
        rows.Finish();

        return Blocked(rows.Rows);
    }

    private static NormalizedLine[] Normalize(string[] lines, NoiseRules rules, LanguageSyntax? syntax)
    {
        var normalizer = new LineNormalizer(rules, syntax);
        var result = new NormalizedLine[lines.Length];
        for (int i = 0; i < lines.Length; i++) result[i] = normalizer.Normalize(lines[i]);
        return result;
    }

    /// <summary>Ids of the lines that take part in the diff, with each one's original index.</summary>
    private static (int[] Ids, int[] Map) Significant(NormalizedLine[] lines, LineInterner interner)
    {
        var ids = new List<int>(lines.Length);
        var map = new List<int>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Ignorable) continue;
            ids.Add(interner.Id(lines[i].Key));
            map.Add(i);
        }
        return ([.. ids], [.. map]);
    }

    /// <summary>
    /// The rows of a comparison, built front to back: each anchor (a matched significant line) after the unmatched
    /// lines between it and the previous one, aligned line by line and classified.
    /// </summary>
    private sealed class RowBuilder(string[] a, string[] b, NormalizedLine[] normA, NormalizedLine[] normB, LineInterner interner)
    {
        private int _budget = LineAligner.NewBudget();
        private int _prevA = -1, _prevB = -1;

        public List<CompareRow> Rows { get; } = new(Math.Max(a.Length, b.Length));

        /// <summary>The lines since the previous anchor, then <paramref name="ia"/> and <paramref name="ib"/> on one row.</summary>
        public void Anchor(int ia, int ib)
        {
            AddRegion(ia, ib);
            Rows.Add(new CompareRow(ia, ib, a[ia] == b[ib] ? RowKind.Equal : RowKind.Unimportant, -1));
            _prevA = ia;
            _prevB = ib;
        }

        /// <summary>The lines after the last anchor.</summary>
        public void Finish() => AddRegion(a.Length, b.Length);

        /// <summary>Aligns the unmatched lines between the previous anchor and <paramref name="aTo"/>/<paramref name="bTo"/> and classifies each row.</summary>
        private void AddRegion(int aTo, int bTo)
        {
            int aFrom = _prevA + 1, bFrom = _prevB + 1;
            if (aFrom >= aTo && bFrom >= bTo) return;
            string[] left = a[aFrom..aTo], right = b[bFrom..bTo];
            foreach (var (l, r) in LineAligner.Align(left, right, ref _budget, interner))
            {
                int ia = l < 0 ? -1 : aFrom + l, ib = r < 0 ? -1 : bFrom + r;
                Rows.Add(new CompareRow(ia, ib, Classify(ia, ib), -1));
            }
        }

        private RowKind Classify(int ia, int ib)
        {
            if (ia >= 0 && ib >= 0)
            {
                if (a[ia] == b[ib]) return RowKind.Equal;
                if (normA[ia].Key == normB[ib].Key) return RowKind.Unimportant;
                return normA[ia].Ignorable && normB[ib].Ignorable ? RowKind.Unimportant : RowKind.Important;
            }
            var present = ia >= 0 ? normA[ia] : normB[ib];
            return present.Ignorable ? RowKind.Unimportant : RowKind.Important;
        }
    }

    private static CompareResult Blocked(List<CompareRow> list)
    {
        var rows = list.ToArray();
        var blocks = new List<DiffBlock>();
        int i = 0;
        while (i < rows.Length)
        {
            if (rows[i].Kind == RowKind.Equal) { i++; continue; }
            int first = i;
            bool important = false;
            while (i < rows.Length && rows[i].Kind != RowKind.Equal)
            {
                important |= rows[i].Kind == RowKind.Important;
                rows[i] = rows[i] with { Block = blocks.Count };
                i++;
            }
            blocks.Add(new DiffBlock(first, i - first, important));
        }
        return new CompareResult(rows, [.. blocks]);
    }
}
