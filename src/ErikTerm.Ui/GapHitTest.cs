namespace ErikTerm.Ui;

/// <summary>
/// Who owns a point in or near the gap between two panes. The gap left of a pane is also that pane's prompt
/// gutter (click it to select a command's output), so the two targets overlap. The rule:
/// <list type="bullet">
/// <item>The divider owns its gap plus <c>slop</c> on every side — unless, for a side-by-side split, the pane to the
/// right has a clickable gutter on that row.</item>
/// <item>Then the divider keeps everything up to and including the drawn hairline (and the slop into the left pane),
/// and the rest of the gap — from the hairline to the right pane's edge — is the gutter.</item>
/// </list>
/// So the divider can always be grabbed on or left of its hairline, and the hover affordance (resize cursor vs
/// gutter highlight) follows the same function.
/// </summary>
public static class GapHitTest
{
    /// <summary>Right edge of the hairline drawn for a vertical divider in <paramref name="gap"/> (matches the renderer).</summary>
    public static float HairlineEnd(RectI gap, float line) => MathF.Round(gap.X + (gap.W - line) / 2) + line;

    /// <summary>The slop-extended hit area of a divider, before the gutter rule.</summary>
    public static bool NearDivider(RectI gap, float slop, float x, float y) =>
        x >= gap.X - slop && x < gap.Right + slop && y >= gap.Y - slop && y < gap.Bottom + slop;

    /// <summary>
    /// True when (x, y) grabs the divider. <paramref name="rightGutterLive"/>: the pane right of a
    /// <see cref="SplitAxis.Horizontal"/> split has a clickable prompt gutter at row y.
    /// </summary>
    public static bool HitsDivider(RectI gap, SplitAxis axis, float line, float slop, float x, float y, bool rightGutterLive)
    {
        if (!NearDivider(gap, slop, x, y)) return false;
        if (axis != SplitAxis.Horizontal || !rightGutterLive) return true;
        return x < HairlineEnd(gap, line);
    }
}
