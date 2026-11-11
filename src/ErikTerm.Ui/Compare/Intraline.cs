using System.Numerics;

namespace ErikTerm.Ui.Compare;

/// <summary>A run of UTF-16 code units inside a line.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>What changed inside one aligned line pair.</summary>
public sealed record LineChanges(TextSpan[] Left, TextSpan[] Right)
{
    public static readonly LineChanges None = new([], []);
}

/// <summary>Word-level changes inside a line pair (words, whitespace runs and single punctuation characters as tokens).</summary>
public static class Intraline
{
    private const int MaxLength = 4000;

    public static LineChanges Compute(string a, string b)
    {
        if (a == b) return LineChanges.None;
        if (a.Length > MaxLength || b.Length > MaxLength) return new LineChanges([new TextSpan(0, a.Length)], [new TextSpan(0, b.Length)]);

        var interner = new LineInterner();
        var ta = Tokens(a, interner);
        var tb = Tokens(b, interner);
        var ids = new int[ta.Count];
        var idsB = new int[tb.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = ta[i].Id;
        for (int j = 0; j < idsB.Length; j++) idsB[j] = tb[j].Id;

        var hunks = SequenceDiff.Diff(ids, idsB);
        var left = new List<TextSpan>();
        var right = new List<TextSpan>();
        foreach (var h in hunks)
        {
            if (h.ALength > 0) AddSpan(left, ta[h.AStart].Start, ta[h.AEnd - 1].End);
            if (h.BLength > 0) AddSpan(right, tb[h.BStart].Start, tb[h.BEnd - 1].End);
        }
        return new LineChanges([.. left], [.. right]);
    }

    private static void AddSpan(List<TextSpan> spans, int start, int end)
    {
        if (spans.Count > 0 && spans[^1].End == start) { spans[^1] = new TextSpan(spans[^1].Start, end - spans[^1].Start); return; }
        spans.Add(new TextSpan(start, end - start));
    }

    private readonly record struct Token(int Start, int End, int Id);

    private static List<Token> Tokens(string s, LineInterner interner)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            int start = i;
            i = TokenEnd(s, i);
            tokens.Add(new Token(start, i, interner.Id(s[start..i])));
        }
        return tokens;
    }

    private static int TokenEnd(string s, int i)
    {
        char c = s[i];
        if (IsWordChar(c))
        {
            while (i < s.Length && IsWordChar(s[i])) i++;
            return i;
        }
        if (char.IsWhiteSpace(c))
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }
        return i + (char.IsHighSurrogate(c) && i + 1 < s.Length ? 2 : 1);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}

/// <summary>Lines of two ranges that belong on one row: exact matches, then lines similar enough to pair.</summary>
public static class LineAligner
{
    /// <summary>Hunks bigger than this (lines x lines) pair by position instead of by similarity.</summary>
    private const int MaxSimilarityCells = 4096;

    /// <summary>Whole-document budget of similarity comparisons, so a pathological file stays bounded.</summary>
    private const int SimilarityBudget = 4_000_000;

    private const double PairThreshold = 0.5;

    /// <summary>
    /// Aligned pairs (-1 is a gap) over index ranges of <paramref name="a"/> and <paramref name="b"/>: exact matches
    /// by the diff, replaced ranges by similarity (monotonic), the rest by position. Used for merge columns and for
    /// compare rows; <paramref name="budget"/> is shared across a document.
    /// </summary>
    public static List<(int A, int B)> Align(IReadOnlyList<string> a, IReadOnlyList<string> b, ref int budget, LineInterner? interner = null)
    {
        interner ??= new LineInterner();
        var ida = interner.Ids(a);
        var idb = interner.Ids(b);
        var hunks = SequenceDiff.Diff(ida, idb, interner);
        var pairs = new List<(int, int)>(Math.Max(a.Count, b.Count));
        int i = 0, j = 0;
        foreach (var h in hunks)
        {
            while (i < h.AStart && j < h.BStart) pairs.Add((i++, j++));
            PairHunk(a, b, h, pairs, ref budget);
            i = h.AEnd;
            j = h.BEnd;
        }
        while (i < a.Count && j < b.Count) pairs.Add((i++, j++));
        while (i < a.Count) pairs.Add((i++, -1));
        while (j < b.Count) pairs.Add((-1, j++));
        return pairs;
    }

    public static int NewBudget() => SimilarityBudget;

    private static void PairHunk(IReadOnlyList<string> a, IReadOnlyList<string> b, DiffHunk h, List<(int A, int B)> pairs, ref int budget)
    {
        int m = h.ALength, n = h.BLength;
        if (m == 0 || n == 0 || m * n > MaxSimilarityCells || m * n > budget || m == 1 && n == 1)
        {
            Positional(h.AStart, m, h.BStart, n, pairs);
            return;
        }
        budget -= m * n;

        var score = new double[m + 1, n + 1];
        var take = new bool[m + 1, n + 1];
        FillPairingTable(Signatures(a, h.AStart, m), Signatures(b, h.BStart, n), score, take);
        EmitPairing(h, score, take, pairs);
    }

    private static Signature[] Signatures(IReadOnlyList<string> lines, int start, int count)
    {
        var signatures = new Signature[count];
        for (int i = 0; i < count; i++) signatures[i] = Signature.Of(lines[start + i]);
        return signatures;
    }

    /// <summary>Maximises the summed similarity of a monotonic pairing: the best score from each (i, j), and where taking the pair achieves it.</summary>
    private static void FillPairingTable(Signature[] sigA, Signature[] sigB, double[,] score, bool[,] take)
    {
        for (int i = sigA.Length - 1; i >= 0; i--)
        {
            for (int j = sigB.Length - 1; j >= 0; j--)
            {
                double sim = Signature.Similarity(sigA[i], sigB[j]);
                double best = Math.Max(score[i + 1, j], score[i, j + 1]);
                if (sim >= PairThreshold && score[i + 1, j + 1] + sim >= best) { take[i, j] = true; best = score[i + 1, j + 1] + sim; }
                score[i, j] = best;
            }
        }
    }

    private static void EmitPairing(DiffHunk h, double[,] score, bool[,] take, List<(int A, int B)> pairs)
    {
        int m = h.ALength, n = h.BLength;
        int ri = 0, rj = 0, startA = 0, startB = 0;
        while (ri < m && rj < n)
        {
            if (take[ri, rj])
            {
                Positional(h.AStart + startA, ri - startA, h.BStart + startB, rj - startB, pairs);
                pairs.Add((h.AStart + ri, h.BStart + rj));
                ri++; rj++;
                startA = ri; startB = rj;
            }
#pragma warning disable S1244 // traceback: score[ri, rj] was assigned from one of these cells, so equality is exact and says which one it came from
            else if (score[ri, rj] == score[ri + 1, rj]) ri++;
#pragma warning restore S1244
            else rj++;
        }
        Positional(h.AStart + startA, m - startA, h.BStart + startB, n - startB, pairs);
    }

    private static void Positional(int aStart, int m, int bStart, int n, List<(int A, int B)> pairs)
    {
        int common = Math.Min(m, n);
        for (int k = 0; k < common; k++) pairs.Add((aStart + k, bStart + k));
        for (int k = common; k < m; k++) pairs.Add((aStart + k, -1));
        for (int k = common; k < n; k++) pairs.Add((-1, bStart + k));
    }

    /// <summary>A 256-bit fingerprint of a line's character pairs (whitespace-trimmed): cheap, good enough to pair lines.</summary>
    private readonly record struct Signature(ulong A, ulong B, ulong C, ulong D)
    {
        public static Signature Of(string raw)
        {
            var text = raw.AsSpan().Trim();
            Span<ulong> words = stackalloc ulong[4];
            for (int i = 0; i + 1 < text.Length; i++)
            {
                uint hash = (uint)(text[i] * 31 + text[i + 1]) * 2654435761u;
                int bit = (int)(hash >> 24);
                words[bit >> 6] |= 1ul << (bit & 63);
            }
            return new Signature(words[0], words[1], words[2], words[3]);
        }

        private int Count => BitOperations.PopCount(A) + BitOperations.PopCount(B) + BitOperations.PopCount(C) + BitOperations.PopCount(D);

        public static double Similarity(in Signature x, in Signature y)
        {
            int total = x.Count + y.Count;
            if (total == 0) return 0;
            int common = BitOperations.PopCount(x.A & y.A) + BitOperations.PopCount(x.B & y.B)
                + BitOperations.PopCount(x.C & y.C) + BitOperations.PopCount(x.D & y.D);
            return 2.0 * common / total;
        }
    }
}
