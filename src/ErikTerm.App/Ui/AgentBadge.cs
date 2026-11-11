using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Ui;

/// <summary>
/// Drawing for agent status: the colored dot (tabs, pane picker, overview), the pill in a pane's top-right corner
/// and the thin rim around a pane that waits. Allocation-free: texts are cached on <see cref="AgentPane"/>.
/// </summary>
internal static class AgentBadge
{
    /// <summary>Period of the "working" pulse.</summary>
    public const int PulseMs = 1600;

    public static uint Color(Theme th, AgentState s) => s switch
    {
        AgentState.Waiting => th.Danger,
        AgentState.Working => th.Warning,
        AgentState.Done => th.Info,
        _ => th.Muted,
    };

    /// <summary>0.45–1: a soft breathing alpha for working dots.</summary>
    public static float Pulse(long now) => 0.725f + 0.275f * MathF.Sin(now % PulseMs / (float)PulseMs * MathF.Tau);

    /// <summary>Where a status dot goes: centred at (<see cref="CenterX"/>, <see cref="CenterY"/>), <see cref="Size"/> across.</summary>
    public readonly record struct DotSpot(float CenterX, float CenterY, float Size);

    /// <summary>A status dot at <paramref name="at"/>. Working pulses; waiting gets a faint halo ring; idle is an empty outline, no color.</summary>
    public static void Dot(DrawList dl, Theme th, AgentState s, in DotSpot at, float alpha, long now)
    {
        if (s == AgentState.None || alpha <= 0.002f) return;
        float cx = at.CenterX, cy = at.CenterY, d = at.Size;
        uint c = Color(th, s);
        if (s == AgentState.Idle)
        {
            Ring(dl, MathF.Round(cx - d / 2), MathF.Round(cy - d / 2), d, MathF.Max(1, MathF.Round(th.S(1))), c, alpha);
            return;
        }
        float a = alpha * (s == AgentState.Working ? Pulse(now) : 1);
        if (s == AgentState.Waiting)
        {
            float ring = MathF.Round(d * 1.9f);
            dl.RoundRect(new RectF(cx - ring / 2, cy - ring / 2, ring, ring), ring / 2, c, 0.22f * alpha);
        }
        dl.RoundRect(new RectF(MathF.Round(cx - d / 2), MathF.Round(cy - d / 2), d, d), d / 2, c, a);
    }

    /// <summary>
    /// An antialiased circle outline, pixel by pixel: the rounded shapes in the atlas are all filled, and painting a smaller
    /// circle inside one would need to know the color behind it, which on a translucent or tinted tab isn't a single color.
    /// </summary>
    private static void Ring(DrawList dl, float left, float top, float d, float width, uint color, float alpha)
    {
        float centre = d / 2, radius = centre - width / 2;
        int size = (int)MathF.Ceiling(d);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - centre, dy = y + 0.5f - centre;
                float coverage = Math.Clamp(width / 2 + 0.5f - MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - radius), 0, 1);
                if (coverage > 0.02f) dl.Rect(left + x, top + y, left + x + 1, top + y + 1, color, alpha * coverage);
            }
    }

    /// <summary>Where <see cref="Pill"/> draws (null while it is hidden): other top-right pane pills (the repo pill) sit left of it.</summary>
    public static RectF? PillBounds(DrawList dl, Theme th, AgentPane a, RectF pane, bool zoomBadge, long now)
    {
        float v = a.Pill.Value(now);
        if (v <= 0.002f || pane.W < th.S(160) || pane.H < th.S(40)) return null;
        float px = th.UiPx * 0.8f;
        var m = dl.MetricsFor(px);
        bool bar = a.Tracker.Progress is not null || a.Usage?.ContextFraction is not null;
        float h = MathF.Round(m.Height + th.S(bar ? 13 : 10));
        float dot = MathF.Round(th.S(7));
        float textW = dl.Measure(a.PillText, px);
        float usageW = a.UsageText.Length > 0 ? dl.Measure(a.UsageText, px * 0.92f) + th.S(10) : 0;
        float w = MathF.Round(th.S(10) + dot + th.S(7) + textW + usageW + th.S(11));
        w = MathF.Min(w, pane.W - th.S(16));
        float top = pane.Y + th.S(8) + (zoomBadge ? h + th.S(6) : 0) - (1 - v) * th.S(4);
        return new RectF(MathF.Round(pane.Right - w - th.S(8)), MathF.Round(top), w, h);
    }

    /// <summary>The pill in the pane's top-right corner: dot, "Claude · waiting", usage, and a thin progress/context bar.</summary>
    public static void Pill(DrawList dl, Theme th, AgentPane a, RectF pane, bool zoomBadge, long now)
    {
        if (PillBounds(dl, th, a, pane, zoomBadge, now) is not { } r) return;
        float v = a.Pill.Value(now);
        var s = a.State == AgentState.None ? a.PillState : a.State;
        float px = th.UiPx * 0.8f;
        var m = dl.MetricsFor(px);
        float? fill = a.Tracker.Progress is { } p ? p / 100f : a.Usage?.ContextFraction;
        float h = r.H;
        float dot = MathF.Round(th.S(7));
        uint c = Color(th, s);

        dl.Shadow(r.Offset(0, th.S(3)), h / 2, th.S(8), 0.30f * v);
        dl.Panel(r, h / 2, Colors.Mix(th.PanelBg, c, 0.10f), 0.90f * v, Colors.Mix(th.PanelBg, c, s == AgentState.Waiting ? 0.65f : 0.35f), v);
        dl.PushClip(r);
        float x = r.X + th.S(10);
        Dot(dl, th, s, new DotSpot(x + dot / 2, r.Y + h / 2, dot), v, now);
        x += dot + th.S(7);
        float b = MathF.Round(r.Y + (h - m.Height) / 2 + m.Ascender - (fill is null ? 0 : th.S(1.5f)));
        x += dl.Text(a.PillText, x, b, px, th.PanelFg, v);
        if (a.UsageText.Length > 0)
        {
            x += th.S(10);
            dl.Text(a.UsageText, x, b, px * 0.92f, th.Muted, v);
        }

        // Progress (OSC 7501) or context-window use (status line) as a hairline along the bottom.
        if (fill is { } f)
        {
            float bw = r.W - h, bh = MathF.Max(1, MathF.Round(th.S(1.5f)));
            var track = new RectF(r.X + h / 2, r.Bottom - bh - th.S(2), bw, bh);
            dl.Rect(track, th.Muted, 0.25f * v);
            uint fc;
            if (a.Tracker.Progress is not null) fc = c;
            else if (f > 0.8f) fc = th.Danger;
            else fc = f > 0.6f ? th.Warning : th.Accent;
            dl.Rect(track with { W = MathF.Round(bw * Math.Clamp(f, 0, 1)) }, fc, 0.85f * v);
        }
        dl.PopClip();
    }

    /// <summary>Corner radius of a pane's rim, in UI points: it sits just inside a window with rounded corners.</summary>
    public const float RimRadius = 8;

    /// <summary>A thin rim around a waiting pane (no halo: a 1.5 px line just inside its edge).</summary>
    public static void Rim(DrawList dl, Theme th, AgentPane a, RectF pane, long now)
    {
        float v = a.Rim.Value(now);
        if (v <= 0.002f) return;
        float t = MathF.Max(1, MathF.Round(th.S(1.5f)));
        dl.RoundedOutline(pane, th.S(RimRadius), t, th.Danger, 0.75f * v);
    }
}
