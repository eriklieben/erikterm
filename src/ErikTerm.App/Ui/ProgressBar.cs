using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>
/// The hairline OSC 9;4 progress bar of tabs and panes: a faint track with the fill in the accent color (or the tab's
/// color), red for error, yellow for paused; indeterminate is a segment gliding to and fro — a still one when
/// animations are off or the window isn't focused, so the frame then doesn't depend on time. Allocation-free.
/// </summary>
internal static class ProgressBar
{
    /// <summary>Frame interval while an indeterminate bar glides (only while one is on screen).</summary>
    public const int FrameMs = 50;
    /// <summary>One sweep there and back.</summary>
    public const int PeriodMs = 1800;

    public static uint Color(Theme th, ProgressState state, uint? accent) => state switch
    {
        ProgressState.Error => th.Danger,
        ProgressState.Paused => th.Warning,
        _ => accent ?? th.Accent,
    };

    /// <summary>Where the indeterminate segment's left edge is, 0–1 of the free track (0.5: still, centred).</summary>
    public static float Sweep(long now, bool animate)
    {
        if (!animate) return 0.5f;
        float t = now % PeriodMs / (float)PeriodMs;
        return Ease.InOutCubic(t < 0.5f ? t * 2 : 2 - t * 2);
    }

    public static void Draw(DrawList dl, Theme th, RectF track, ProgressReport p, float alpha, long now, bool animate, uint? accent = null)
    {
        if (!p.IsActive || alpha <= 0.002f || track.W < 4 || track.H <= 0) return;
        float radius = track.H / 2;
        dl.RoundRect(track, radius, th.Muted, 0.30f * alpha);
        uint c = Color(th, p.State, accent);
        if (p.State == ProgressState.Indeterminate)
        {
            float seg = MathF.Round(MathF.Max(track.H * 2, track.W * 0.3f));
            float x = track.X + (track.W - seg) * Sweep(now, animate);
            dl.RoundRect(new RectF(MathF.Round(x), track.Y, seg, track.H), radius, c, 0.95f * alpha);
            return;
        }
        float w = MathF.Round(track.W * p.Value / 100f);
        if (w > 0) dl.RoundRect(track with { W = MathF.Max(w, track.H) }, radius, c, 0.95f * alpha);
    }
}
