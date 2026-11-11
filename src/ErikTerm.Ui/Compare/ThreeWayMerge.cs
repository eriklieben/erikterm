namespace ErikTerm.Ui.Compare;

/// <summary>What one stretch of a 3-way merge is.</summary>
public enum MergeChunkKind
{
    /// <summary>Both sides have the same lines (and agree with the base where one exists).</summary>
    Stable,
    /// <summary>Only ours changed this stretch: ours applied automatically.</summary>
    AutoOurs,
    /// <summary>Only theirs changed this stretch: theirs applied automatically.</summary>
    AutoTheirs,
    /// <summary>Both sides made the same change: applied automatically.</summary>
    AutoBoth,
    /// <summary>The sides differ and only a person can pick.</summary>
    Conflict,
}

/// <summary>One stretch of a 3-way merge, with the raw lines (terminators included) of each side.</summary>
public sealed record MergeChunk(MergeChunkKind Kind, string[] Base, string[] Ours, string[] Theirs, bool BaseKnown = true);

/// <summary>A diff3 merge on <see cref="SequenceDiff"/>, with git's conflict rules (see <see cref="Merge"/>).</summary>
public static class ThreeWayMerge
{
    /// <summary>
    /// Merges <paramref name="ours"/> and <paramref name="theirs"/> against <paramref name="baseLines"/>. Follows git:
    /// lines are compared with their terminators; changes that overlap or touch in the base conflict (a change directly
    /// next to the other side's change conflicts, there is no stable line between them); identical changes on both
    /// sides merge; a conflict's common lines are split out, so only what truly differs needs a decision.
    /// <paramref name="baseKnown"/> false (add/add) merges like an empty base, and the chunks say there is no base.
    /// </summary>
    public static List<MergeChunk> Merge(string[] baseLines, string[] ours, string[] theirs, bool baseKnown = true)
    {
        var interner = new LineInterner();
        int[] b = interner.Ids(baseLines), o = interner.Ids(ours), t = interner.Ids(theirs);
        var hunksO = SequenceDiff.Diff(b, o, interner);
        var hunksT = SequenceDiff.Diff(b, t, interner);

        var chunks = new List<MergeChunk>();
        int cursor = 0, io = 0, it = 0, shiftO = 0, shiftT = 0;
        while (io < hunksO.Count || it < hunksT.Count)
        {
            int start = Math.Min(NextStart(hunksO, io), NextStart(hunksT, it));
            int end = start, firstO = io, firstT = it;

            // Grow the region until neither side has a hunk touching it.
            for (bool grew = true; grew;)
            {
                grew = AbsorbTouching(hunksO, ref io, ref end);
                grew |= AbsorbTouching(hunksT, ref it, ref end);
            }

            AddStable(chunks, baseLines, cursor, start, baseKnown);
            cursor = end;

            var baseRange = Slice(baseLines, start, end);
            var oursRange = SideRange(ours, hunksO, firstO, io, start, end, shiftO);
            var theirsRange = SideRange(theirs, hunksT, firstT, it, start, end, shiftT);
            shiftO += LengthChange(hunksO, firstO, io);
            shiftT += LengthChange(hunksT, firstT, it);

            AddRegion(chunks, io > firstO, it > firstT, baseRange, oursRange, theirsRange, baseKnown, interner);
        }
        AddStable(chunks, baseLines, cursor, baseLines.Length, baseKnown);
        return chunks;
    }

    private static int NextStart(List<DiffHunk> hunks, int index) => index < hunks.Count ? hunks[index].AStart : int.MaxValue;

    /// <summary>Takes the hunks that start at or before <paramref name="end"/>, extending it; true when there were any.</summary>
    private static bool AbsorbTouching(List<DiffHunk> hunks, ref int index, ref int end)
    {
        bool grew = false;
        while (index < hunks.Count && hunks[index].AStart <= end)
        {
            end = Math.Max(end, hunks[index].AEnd);
            index++;
            grew = true;
        }
        return grew;
    }

    private static int LengthChange(List<DiffHunk> hunks, int first, int last)
    {
        int change = 0;
        for (int k = first; k < last; k++) change += hunks[k].BLength - hunks[k].ALength;
        return change;
    }

    private static void AddStable(List<MergeChunk> chunks, string[] baseLines, int start, int end, bool baseKnown)
    {
        if (start < end) chunks.Add(new MergeChunk(MergeChunkKind.Stable, Slice(baseLines, start, end), Slice(baseLines, start, end), Slice(baseLines, start, end), baseKnown));
    }

    private static void AddRegion(List<MergeChunk> chunks, bool oursTouched, bool theirsTouched, string[] baseRange, string[] oursRange, string[] theirsRange, bool baseKnown, LineInterner interner)
    {
        if (oursTouched && !theirsTouched) chunks.Add(new MergeChunk(MergeChunkKind.AutoOurs, baseRange, oursRange, theirsRange, baseKnown));
        else if (theirsTouched && !oursTouched) chunks.Add(new MergeChunk(MergeChunkKind.AutoTheirs, baseRange, oursRange, theirsRange, baseKnown));
        else if (oursRange.SequenceEqual(theirsRange)) chunks.Add(new MergeChunk(MergeChunkKind.AutoBoth, baseRange, oursRange, theirsRange, baseKnown));
        else AddRefinedConflict(chunks, baseRange, oursRange, theirsRange, baseKnown, interner);
    }

    /// <summary>
    /// Lines a side has for the base range [start,end): its changed hunks plus the base context around them, or, with
    /// no hunk there, the base's lines at the base offset shifted by the side's earlier hunks.
    /// </summary>
    private static string[] SideRange(string[] side, List<DiffHunk> hunks, int first, int last, int start, int end, int shift)
    {
        if (first == last) return Slice(side, start + shift, end + shift);
        int sideStart = hunks[first].BStart - (hunks[first].AStart - start);
        int sideEnd = hunks[last - 1].BEnd + (end - hunks[last - 1].AEnd);
        return Slice(side, sideStart, sideEnd);
    }

    private static string[] Slice(string[] lines, int start, int end) => lines[start..end];

    /// <summary>Lines common to both sides inside a conflict aren't conflicts: split them out as stable (git's zealous refinement).</summary>
    private static void AddRefinedConflict(List<MergeChunk> chunks, string[] baseRange, string[] ours, string[] theirs, bool baseKnown, LineInterner interner)
    {
        if (ours.Length == 0 || theirs.Length == 0)
        {
            chunks.Add(new MergeChunk(MergeChunkKind.Conflict, baseRange, ours, theirs, baseKnown));
            return;
        }
        var hunks = SequenceDiff.Diff(interner.Ids(ours), interner.Ids(theirs), interner);
        if (hunks.Count == 1 && hunks[0].ALength == ours.Length && hunks[0].BLength == theirs.Length)
        {
            chunks.Add(new MergeChunk(MergeChunkKind.Conflict, baseRange, ours, theirs, baseKnown));
            return;
        }
        int cursorO = 0;
        foreach (var h in hunks)
        {
            if (h.AStart > cursorO)
            {
                var common = ours[cursorO..h.AStart];
                chunks.Add(new MergeChunk(MergeChunkKind.Stable, common, common, common, false));
            }
            chunks.Add(new MergeChunk(MergeChunkKind.Conflict, [], ours[h.AStart..h.AEnd], theirs[h.BStart..h.BEnd], false));
            cursorO = h.AEnd;
        }
        if (cursorO < ours.Length)
        {
            var common = ours[cursorO..];
            chunks.Add(new MergeChunk(MergeChunkKind.Stable, common, common, common, false));
        }
    }
}
