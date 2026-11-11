namespace ErikTerm.Ui.Compare;

/// <summary>A changed region: lines [AStart, AStart+ALength) of a replaced by [BStart, BStart+BLength) of b.</summary>
public readonly record struct DiffHunk(int AStart, int ALength, int BStart, int BLength)
{
    public int AEnd => AStart + ALength;
    public int BEnd => BStart + BLength;
}

/// <summary>Maps strings to small ints so diffs compare integers, not text.</summary>
public sealed class LineInterner
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<bool> _blank = [];

    public int Count => _ids.Count;

    public int Id(string line)
    {
        if (_ids.TryGetValue(line, out int id)) return id;
        id = _ids.Count;
        _ids.Add(line, id);
        _blank.Add(string.IsNullOrWhiteSpace(line));
        return id;
    }

    public int[] Ids(IReadOnlyList<string> lines)
    {
        var ids = new int[lines.Count];
        for (int i = 0; i < ids.Length; i++) ids[i] = Id(lines[i]);
        return ids;
    }

    public bool IsBlank(int id) => _blank[id];
}

/// <summary>
/// Bounded Myers diff over integer sequences (the linear-space middle-snake form, as in GNU diff). Lines that occur on
/// only one side are removed first (they can never be part of a common subsequence), which makes the usual case very
/// fast; a cost limit gives up on a perfect answer on pathological inputs so a 100k-line file stays O(N * limit).
/// </summary>
public static class SequenceDiff
{
    private const int MinCostLimit = 1 << 10;

    /// <summary>The hunks turning <paramref name="a"/> into <paramref name="b"/>, in order, none empty.</summary>
    public static List<DiffHunk> Diff(ReadOnlySpan<int> a, ReadOnlySpan<int> b, LineInterner? interner = null)
    {
        int n = a.Length, m = b.Length;
        var changedA = new bool[n];
        var changedB = new bool[m];
        if (n == 0 || m == 0)
        {
            Array.Fill(changedA, true);
            Array.Fill(changedB, true);
            return Hunks(a, b, changedA, changedB, interner);
        }

        // Lines absent from the other side are changes by definition; drop them from the search.
        int maxId = LargestId(b, LargestId(a, 0));
        var inA = PresenceOf(a, maxId);
        var inB = PresenceOf(b, maxId);
        int[] keptA = KeepPresent(a, inB, changedA, out int nk);
        int[] keptB = KeepPresent(b, inA, changedB, out int mk);

        var xm = new bool[nk];
        var ym = new bool[mk];
        new Myers(ValuesAt(a, keptA, nk), ValuesAt(b, keptB, mk), xm, ym).Run();
        for (int i = 0; i < nk; i++) changedA[keptA[i]] = xm[i];
        for (int j = 0; j < mk; j++) changedB[keptB[j]] = ym[j];
        return Hunks(a, b, changedA, changedB, interner);
    }

    private static int LargestId(ReadOnlySpan<int> ids, int atLeast)
    {
        int largest = atLeast;
        foreach (int id in ids) if (id > largest) largest = id;
        return largest;
    }

    private static bool[] PresenceOf(ReadOnlySpan<int> ids, int maxId)
    {
        var present = new bool[maxId + 1];
        foreach (int id in ids) present[id] = true;
        return present;
    }

    /// <summary>The positions whose id the other side also has (<paramref name="count"/> of them); the rest are marked changed.</summary>
    private static int[] KeepPresent(ReadOnlySpan<int> ids, bool[] inOther, bool[] changed, out int count)
    {
        var kept = new int[ids.Length];
        count = 0;
        for (int i = 0; i < ids.Length; i++) { if (inOther[ids[i]]) kept[count++] = i; else changed[i] = true; }
        return kept;
    }

    private static int[] ValuesAt(ReadOnlySpan<int> ids, int[] positions, int count)
    {
        var values = new int[count];
        for (int i = 0; i < count; i++) values[i] = ids[positions[i]];
        return values;
    }

    /// <summary>The Myers search with its work arrays; compares ranges of <c>xs</c> and <c>ys</c>, marking changes.</summary>
    private sealed class Myers(int[] xs, int[] ys, bool[] xChanged, bool[] yChanged)
    {
        private readonly int[] _fd = new int[xs.Length + ys.Length + 3];
        private readonly int[] _bd = new int[xs.Length + ys.Length + 3];
        private readonly int _offset = ys.Length + 1;
        private readonly int _costLimit = CostLimit(xs.Length + ys.Length);

        private static int CostLimit(int total)
        {
            int limit = 1;
            for (int d = total; d != 0; d >>= 2) limit <<= 1;
            return Math.Max(MinCostLimit, limit);
        }

        public void Run()
        {
            var stack = new Stack<(int xl, int xh, int yl, int yh)>();
            stack.Push((0, xs.Length, 0, ys.Length));
            while (stack.Count > 0)
            {
                var (xl, xh, yl, yh) = stack.Pop();
                TrimEqualEnds(ref xl, ref xh, ref yl, ref yh);
                if (xl == xh) { MarkChanged(yChanged, yl, yh); continue; }
                if (yl == yh) { MarkChanged(xChanged, xl, xh); continue; }
                var (xm, ym) = Split(xl, xh, yl, yh);
                if ((xm == xl && ym == yl) || (xm == xh && ym == yh))
                {
                    // No progress possible (the cost limit hit the very first step): the whole range changed.
                    MarkChanged(xChanged, xl, xh);
                    MarkChanged(yChanged, yl, yh);
                    continue;
                }
                stack.Push((xm, xh, ym, yh));
                stack.Push((xl, xm, yl, ym));
            }
        }

        private void TrimEqualEnds(ref int xl, ref int xh, ref int yl, ref int yh)
        {
            while (xl < xh && yl < yh && xs[xl] == ys[yl]) { xl++; yl++; }
            while (xh > xl && yh > yl && xs[xh - 1] == ys[yh - 1]) { xh--; yh--; }
        }

        private static void MarkChanged(bool[] changed, int from, int to)
        {
            for (int i = from; i < to; i++) changed[i] = true;
        }

        /// <summary>The diagonals one wave has reached.</summary>
        private struct Diagonals(int mid)
        {
            public int Min = mid, Max = mid;
        }

        private (int X, int Y) Split(int xl, int xh, int yl, int yh)
        {
            int dmin = xl - yh, dmax = xh - yl;
            int fmid = xl - yl, bmid = xh - yh;
            var forward = new Diagonals(fmid);
            var backward = new Diagonals(bmid);
            bool odd = ((fmid - bmid) & 1) != 0;
            _fd[_offset + fmid] = xl;
            _bd[_offset + bmid] = xh;

            for (int c = 1; c <= _costLimit; c++)
            {
                Widen(_fd, ref forward, dmin, dmax, -1);
                if (AdvanceForward(forward, backward, odd, xh, yh, out var meet)) return meet;

                Widen(_bd, ref backward, dmin, dmax, int.MaxValue);
                if (AdvanceBackward(backward, forward, !odd, xl, yl, out meet)) return meet;
            }

            // Too expensive: split at the point that got furthest, forward or backward.
            return FurthestPoint(forward, backward, xl, xh, yl, yh);
        }

        /// <summary>Moves both ends of a wave one diagonal, staying in [dmin, dmax]; a new end gets <paramref name="sentinel"/> beyond it.</summary>
        private void Widen(int[] wave, ref Diagonals range, int dmin, int dmax, int sentinel)
        {
            if (range.Min > dmin) { --range.Min; if (_offset + range.Min - 1 >= 0) wave[_offset + range.Min - 1] = sentinel; } else ++range.Min;
            if (range.Max < dmax) { ++range.Max; if (_offset + range.Max + 1 < wave.Length) wave[_offset + range.Max + 1] = sentinel; } else --range.Max;
        }

        private bool AdvanceForward(Diagonals wave, Diagonals other, bool checkOverlap, int xh, int yh, out (int X, int Y) meet)
        {
            meet = default;
            for (int d = wave.Max; d >= wave.Min; d -= 2)
            {
                int tlo = _fd[_offset + d - 1], thi = _fd[_offset + d + 1];
                int x = tlo < thi ? thi : tlo + 1;
                int y = x - d;
                while (x < xh && y < yh && xs[x] == ys[y]) { x++; y++; }
                _fd[_offset + d] = x;
                if (checkOverlap && other.Min <= d && d <= other.Max && _bd[_offset + d] <= x) { meet = (x, y); return true; }
            }
            return false;
        }

        private bool AdvanceBackward(Diagonals wave, Diagonals other, bool checkOverlap, int xl, int yl, out (int X, int Y) meet)
        {
            meet = default;
            for (int d = wave.Max; d >= wave.Min; d -= 2)
            {
                int tlo = _bd[_offset + d - 1], thi = _bd[_offset + d + 1];
                int x = tlo < thi ? tlo : thi - 1;
                int y = x - d;
                while (xl < x && yl < y && xs[x - 1] == ys[y - 1]) { x--; y--; }
                _bd[_offset + d] = x;
                if (checkOverlap && other.Min <= d && d <= other.Max && x <= _fd[_offset + d]) { meet = (x, y); return true; }
            }
            return false;
        }

        private (int X, int Y) FurthestPoint(Diagonals forward, Diagonals backward, int xl, int xh, int yl, int yh)
        {
            int fxBest = xl, fxyBest = -1;
            for (int d = forward.Max; d >= forward.Min; d -= 2)
            {
                int x = Math.Min(_fd[_offset + d], xh), y = x - d;
                if (yh < y) { x = yh + d; y = yh; }
                if (fxyBest < x + y) { fxyBest = x + y; fxBest = x; }
            }
            int bxBest = xh, bxyBest = int.MaxValue;
            for (int d = backward.Max; d >= backward.Min; d -= 2)
            {
                int x = Math.Max(xl, _bd[_offset + d]), y = x - d;
                if (y < yl) { x = yl + d; y = yl; }
                if (x + y < bxyBest) { bxyBest = x + y; bxBest = x; }
            }
            return (xh + yh) - bxyBest < fxyBest - (xl + yl)
                ? (fxBest, fxyBest - fxBest)
                : (bxBest, bxyBest - bxBest);
        }
    }

    private static List<DiffHunk> Hunks(ReadOnlySpan<int> a, ReadOnlySpan<int> b, bool[] changedA, bool[] changedB, LineInterner? interner)
    {
        var hunks = new List<DiffHunk>();
        int i = 0, j = 0, n = a.Length, m = b.Length;
        while (i < n || j < m)
        {
            if (i < n && j < m && !changedA[i] && !changedB[j]) { i++; j++; continue; }
            int ia = i, ib = j;
            i = SkipChanged(changedA, i);
            j = SkipChanged(changedB, j);
            if (i == ia && j == ib) // unequal unchanged lines can't happen; stay safe rather than loop
            {
                if (i < n) i++;
                if (j < m) j++;
            }
            hunks.Add(new DiffHunk(ia, i - ia, ib, j - ib));
        }
        if (interner is not null) Slide(a, b, hunks, interner);
        return hunks;
    }

    private static int SkipChanged(bool[] changed, int from)
    {
        int i = from;
        while (i < changed.Length && changed[i]) i++;
        return i;
    }

    /// <summary>
    /// Pure insertions and deletions can usually slide along equal lines. Choose the position that reads best: the
    /// lowest one, or if sliding up lets the block end on a blank line, that one.
    /// </summary>
    private static void Slide(ReadOnlySpan<int> a, ReadOnlySpan<int> b, List<DiffHunk> hunks, LineInterner interner)
    {
        for (int h = 0; h < hunks.Count; h++) hunks[h] = Slid(a, b, hunks, h, interner);
    }

    private static DiffHunk Slid(ReadOnlySpan<int> a, ReadOnlySpan<int> b, List<DiffHunk> hunks, int h, LineInterner interner)
    {
        var hunk = hunks[h];
        bool insertion = hunk.ALength == 0;
        if (!insertion && hunk.BLength != 0) return hunk;
        ReadOnlySpan<int> seq = insertion ? b : a;
        int start = insertion ? hunk.BStart : hunk.AStart;
        int len = insertion ? hunk.BLength : hunk.ALength;
        int otherStart = insertion ? hunk.AStart : hunk.BStart;
        int minStart = 0;
        if (h != 0) minStart = insertion ? hunks[h - 1].BEnd : hunks[h - 1].AEnd;
        int maxEnd = seq.Length;
        if (h != hunks.Count - 1) maxEnd = insertion ? hunks[h + 1].BStart : hunks[h + 1].AStart;

        int best = BestStart(seq, start, len, minStart, maxEnd, interner);
        if (best == start) return hunk;
        int shift = best - start;
        return insertion
            ? new DiffHunk(otherStart + shift, 0, best, len)
            : new DiffHunk(best, len, otherStart + shift, 0);
    }

    /// <summary>The start a block of <paramref name="len"/> lines settles on within [minStart, maxEnd).</summary>
    private static int BestStart(ReadOnlySpan<int> seq, int start, int len, int minStart, int maxEnd, LineInterner interner)
    {
        int s = start;
        while (s + len < maxEnd && seq[s] == seq[s + len]) s++; // lowest position
        int best = s;
        for (int t = s; t > minStart && seq[t - 1] == seq[t + len - 1]; )
        {
            t--;
            if (interner.IsBlank(seq[t + len - 1])) { best = t; break; }
        }
        return best;
    }
}
