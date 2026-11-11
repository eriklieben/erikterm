namespace ErikTerm.Core.Images;

/// <summary>
/// Keeps kitty placements on the text they were placed on while the screen changes: region scrolls move the placements
/// inside the region (ones that leave it are deleted), ED 2 / 3 delete them, and a resize moves each to the new line of
/// the row it was anchored to. Virtual placements, and placements relative to one, aren't on the grid and stay.
/// </summary>
internal sealed class PlacementTracker(ImageStore images)
{
    /// <summary>A region scroll: lines [top, bottom] of the screen at <paramref name="screenTop"/> move up by n (the top ones into the scrollback if allowed).</summary>
    public void ScrollUp(bool alt, long screenTop, int screenRows, int top, int bottom, int n, bool toScrollback)
    {
        var list = images.Placements(alt);
        if (list.Count == 0) return;
        if (toScrollback)
        {
            ShiftPlacementsBelowRegion(list, screenTop, screenRows, bottom, n);
            return;
        }
        images.RemovePlacements(alt, p =>
        {
            if (p.Virtual || p.RelativeToVirtual) return false;
            long row = p.Line - screenTop;
            if (row < top || row > bottom) return false;
            p.Line -= n;
            return p.Line - screenTop < top;
        }, freeImages: false);
    }

    /// <summary>
    /// The scrolled lines keep their absolute numbers; rows below the region didn't move, but the screen top advanced
    /// by n, so their numbers grow.
    /// </summary>
    private static void ShiftPlacementsBelowRegion(List<KittyPlacement> list, long screenTop, int screenRows, int bottom, int n)
    {
        if (bottom >= screenRows - 1) return;
        foreach (var p in list)
            if (!p.Virtual && !p.RelativeToVirtual && p.Line - screenTop > bottom) p.Line += n;
    }

    /// <summary>A region scroll: lines [top, bottom] move down by n.</summary>
    public void ScrollDown(bool alt, long screenTop, int top, int bottom, int n)
    {
        if (images.Placements(alt).Count == 0) return;
        images.RemovePlacements(alt, p =>
        {
            if (p.Virtual || p.RelativeToVirtual) return false;
            long row = p.Line - screenTop;
            if (row < top || row > bottom) return false;
            p.Line += n;
            return p.Line - screenTop > bottom;
        }, freeImages: false);
    }

    /// <summary>ED 2 / 3: kitty deletes the placements on the screen (3: and in the scrollback).</summary>
    public void Erase(bool alt, long screenTop, bool scrollback)
    {
        if (images.HasPlacements) ErasePlacements(alt, screenTop, scrollback); // the lambda's closure would allocate on entry
    }

    private void ErasePlacements(bool alt, long screenTop, bool scrollback) =>
        images.RemovePlacements(alt, p => !p.Virtual && !p.RelativeToVirtual && (scrollback || p.Line + Math.Max(1, p.Rows) > screenTop), freeImages: false);

    /// <summary>Before a resize: remembers the Row each placement is anchored to (null without placements).</summary>
    public List<(KittyPlacement P, Row Row)>? Capture(Scrollback scrollback, Row[] main, Row[] altScreen)
    {
        if (!images.HasPlacements) return null;
        var anchors = new List<(KittyPlacement, Row)>();
        foreach (var alt in (ReadOnlySpan<bool>)[false, true])
            foreach (var p in images.Placements(alt))
                if (!p.Virtual && !p.RelativeToVirtual && LineOf(scrollback, main, altScreen, p.Line, alt) is { } row) anchors.Add((p, row));
        return anchors;
    }

    /// <summary>After a resize: moves each placement to its Row's new line (placements whose row is gone are deleted).</summary>
    public void Restore(List<(KittyPlacement P, Row Row)>? anchors, Scrollback scrollback, Row[] main, Row[] altScreen)
    {
        if (anchors is null) return;
        long screenTop = scrollback.Dropped + scrollback.Count;
        var lineOf = new Dictionary<Row, long>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < scrollback.Count; i++) lineOf[scrollback.Peek(i)] = scrollback.Dropped + i;
        for (int y = 0; y < main.Length; y++) lineOf.TryAdd(main[y], screenTop + y);
        var altLines = new Dictionary<Row, long>(ReferenceEqualityComparer.Instance);
        for (int y = 0; y < altScreen.Length; y++) altLines[altScreen[y]] = screenTop + y;

        var keep = new HashSet<KittyPlacement>(ReferenceEqualityComparer.Instance);
        var onAlt = new HashSet<KittyPlacement>(images.Placements(true), ReferenceEqualityComparer.Instance);
        foreach (var (p, row) in anchors)
        {
            bool alt = onAlt.Contains(p);
            if ((alt ? altLines : lineOf).TryGetValue(row, out long line))
            {
                p.Line = line;
                keep.Add(p);
            }
        }
        var anchored = new HashSet<KittyPlacement>(anchors.Select(a => a.P), ReferenceEqualityComparer.Instance);
        foreach (var alt in (ReadOnlySpan<bool>)[false, true])
            images.RemovePlacements(alt, p => anchored.Contains(p) && !keep.Contains(p), freeImages: false);
    }

    /// <summary>Row at an absolute line on the main or alternate screen.</summary>
    private static Row? LineOf(Scrollback scrollback, Row[] main, Row[] altScreen, long line, bool alt)
    {
        if (!alt)
        {
            long i = line - scrollback.Dropped;
            if (i < 0) return null;
            if (i < scrollback.Count) return scrollback[(int)i];
            i -= scrollback.Count;
            return i < main.Length ? main[(int)i] : null;
        }
        long y = line - (scrollback.Dropped + scrollback.Count);
        return y >= 0 && y < altScreen.Length ? altScreen[(int)y] : null;
    }
}
