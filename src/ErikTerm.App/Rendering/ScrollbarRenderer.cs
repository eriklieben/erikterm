using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.Rendering;

/// <summary>
/// A pane's overlay scrollbar: the thumb, and ticks along it for prompts, failed commands, search matches and vim marks.
/// Drawn once per pane per frame, after the grid (<see cref="PaneRenderer"/>).
/// </summary>
internal static class ScrollbarRenderer
{
    private static readonly IReadOnlyList<SearchMatch> NoMatches = [];

    /// <summary>Vim marks fit in here: a–z and A–Z.</summary>
    internal const int MarkCapacity = 52;

    public static void Draw(DrawList dl, Pane pane, RectF rect, Theme theme, long now, SearchBar? search)
    {
        var t = pane.Term;
        float alpha = pane.Scrollbar.Value(now);
        if (alpha <= 0.01f || t.ScrollbackCount == 0 || t.IsAlternateScreen) return;

        var g = ScrollbarGeometry.Of(rect, t.ScrollbackCount, t.Rows, t.ViewOffset, theme.Scale);
        DrawScrollMarks(dl, pane, g, theme, alpha, search);
        dl.RoundRect(new RectF(g.X, g.ThumbY, g.Width, g.ThumbH), g.Width / 2, theme.Foreground, 0.35f * alpha);
    }

    /// <summary>What the pane's ticks are built from right now (<paramref name="search"/>'s matches, vim's marks). Marks go in <paramref name="marks"/>.</summary>
    internal static (IReadOnlyList<SearchMatch> Matches, long Signature, int Marks) TickSources(Pane pane, Span<long> marks, SearchBar? search)
    {
        var matches = NoMatches;
        long signature = 0;
        if (search is { IsActive: true } s && ReferenceEquals(s.Pane, pane))
        {
            matches = s.Engine.Matches;
            signature = s.Engine.Revision;
        }
        return (matches, signature, pane.Vim?.CopyMarkLines(marks) ?? 0);
    }

    private static void DrawScrollMarks(DrawList dl, Pane pane, in ScrollbarGeometry g, Theme theme, float alpha, SearchBar? search)
    {
        var t = pane.Term;
        pane.ScrollMarks.Update(t);
        Span<long> marks = stackalloc long[MarkCapacity];
        var (matches, signature, markCount) = TickSources(pane, marks, search);
        var ticks = pane.ScrollMarks.Ticks(t.FirstLine, t.EndLine - t.FirstLine, g.TickRows, matches, signature, marks[..markCount]);
        if (ticks.IsEmpty) return;

        float x = g.X - 2 * theme.Scale, w = g.Width + 2 * theme.Scale;
        float thin = MathF.Max(1, MathF.Round(theme.Scale)), bold = MathF.Max(2, MathF.Round(theme.Scale * 2));
        DrawTicks(dl, ticks, ScrollMarkKinds.Prompt, g.TrackY, new TickLane(x + w * 0.45f, w * 0.55f, thin, theme.Foreground, 0.55f * alpha));
        DrawTicks(dl, ticks, ScrollMarkKinds.Mark, g.TrackY, new TickLane(x, w, bold, theme.Info, 0.95f * alpha));
        DrawTicks(dl, ticks, ScrollMarkKinds.Search, g.TrackY, new TickLane(x, w, bold, theme.Accent, 0.9f * alpha));
        DrawTicks(dl, ticks, ScrollMarkKinds.Failed, g.TrackY, new TickLane(x, w, bold, theme.Danger, alpha));

        // The search's current match, a little larger.
        if (search is { IsActive: true, Current: { } current } s && ReferenceEquals(s.Pane, pane)
            && ScrollMarks.Bucket(current.StartLine, t.FirstLine, t.EndLine - t.FirstLine, g.TickRows) is var row and >= 0)
            dl.RoundRect(new RectF(x - theme.S(1), MathF.Round(g.TrackY + row - bold), w + theme.S(1), bold * 2), bold, theme.Foreground, alpha);
    }

    /// <summary>Where and how one kind of tick is drawn: from X, W wide, at least MinH high, in Rgb at opacity A.</summary>
    private readonly record struct TickLane(float X, float W, float MinH, uint Rgb, float A);

    /// <summary>One quad per run of pixel rows that carry <paramref name="kind"/>; a lone row still gets a visible height.</summary>
    private static void DrawTicks(DrawList dl, ReadOnlySpan<byte> ticks, ScrollMarkKinds kind, float top, in TickLane lane)
    {
        float x = lane.X, w = lane.W, minH = lane.MinH, a = lane.A;
        uint rgb = lane.Rgb;
        byte bit = (byte)kind;
        for (int y = 0; y < ticks.Length; y++)
        {
            if ((ticks[y] & bit) == 0) continue;
            int end = y + 1;
            while (end < ticks.Length && (ticks[end] & bit) != 0) end++;
            float h = MathF.Max(minH, end - y);
            dl.Rect(new RectF(x, MathF.Round(top + y), w, h), rgb, a);
            #pragma warning disable S127 // the run just drawn is skipped: moving the counter is how this per-frame loop merges rows into one quad
            y = end - 1;
            #pragma warning restore S127
        }
    }
}
