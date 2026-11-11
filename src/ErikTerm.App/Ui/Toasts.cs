using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

internal enum ToastKind { Info, Success, Error }

/// <summary>Transient notifications in the bottom-right corner (config reloaded, copied, errors).</summary>
internal sealed class Toasts
{
    private sealed class Toast
    {
        public required ToastKind Kind;
        public required string Title;
        public string? Detail;
        public long Shown, Expires;
        public Tween Visible = new(0);
        public Tween Y = new(0);
        public bool Placed;
        // Optional click action ("Open", …): the whole toast is the button.
        public string? ActionLabel;
        public Action? Action;
        public RectF Rect;
    }

    private readonly List<Toast> _toasts = [];

    /// <summary>A click on a toast with an action runs it (and dismisses the toast). False when no such toast is there.</summary>
    public bool HandleMouseDown(float x, float y, Theme th, long now)
    {
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var t = _toasts[i];
            if (t.Action is null || t.Visible.Target == 0 || !t.Rect.Contains(x, y)) continue;
            t.Visible.Set(0, now, th.AnimationMs * 1.2f);
            t.Action();
            return true;
        }
        return false;
    }

    public void Show(ToastKind kind, string title, string? detail, Theme th, long now, string? actionLabel, Action? action)
    {
        Show(kind, title, detail, th, now);
        var t = _toasts[^1];
        t.Action = action;
        t.ActionLabel = action is null ? null : actionLabel ?? "Open";
        if (action is not null) t.Expires = now + 9000;
    }

    public void Show(ToastKind kind, string title, string? detail, Theme th, long now)
    {
        // Replace an identical toast instead of stacking duplicates.
        _toasts.RemoveAll(t => t.Title == title && t.Detail == detail);
        var toast = new Toast
        {
            Kind = kind,
            Title = title,
            Detail = detail,
            Shown = now,
            Expires = now + (kind == ToastKind.Error ? 8000 : 2600),
        };
        toast.Visible.Set(1, now, th.AnimationMs * 1.4f);
        _toasts.Add(toast);
        while (_toasts.Count > 4) _toasts.RemoveAt(0);
    }

    public bool IsAnimating() => _toasts.Count > 0;

    /// <summary>The sizes every toast shares.</summary>
    private readonly record struct ToastFrame(float Px, float Margin, float Gap, float Pad, FontSet.Metrics Metrics, float LineH, float IconW);

    /// <summary>A toast's measured text and size this frame.</summary>
    private readonly record struct ToastSize(string? Detail, float ActionW, float W, float H);

    /// <summary>Draws the stack up from the bottom-right corner; a toast that would cover an <paramref name="avoid"/> rect goes above it.</summary>
    public void Draw(DrawList dl, Theme th, RectF window, long now, List<RectF>? avoid = null)
    {
        if (_toasts.Count == 0) return;
        RetireExpired(th, now);

        float px = th.UiPx * 0.95f;
        var lm = dl.MetricsFor(px);
        var f = new ToastFrame(px, th.S(18), th.S(10), th.S(14), lm, lm.Height * 1.35f, px * 2.0f);
        float bottom = window.Bottom - f.Margin;

        for (int i = _toasts.Count - 1; i >= 0; i--)
            bottom = DrawToast(dl, th, _toasts[i], window, bottom, f, now, avoid);
    }

    private void RetireExpired(Theme th, long now)
    {
        foreach (var t in _toasts)
            if (now >= t.Expires && t.Visible.Target > 0) t.Visible.Set(0, now, th.AnimationMs * 2f);
        for (int i = _toasts.Count - 1; i >= 0; i--) // no closure: this runs every frame
            if (_toasts[i].Visible.Target == 0 && !_toasts[i].Visible.IsAnimating(now)) _toasts.RemoveAt(i);
    }

    /// <summary>Places and draws one toast; returns the bottom edge for the next one up.</summary>
    private static float DrawToast(DrawList dl, Theme th, Toast t, RectF window, float bottom, in ToastFrame f, long now, List<RectF>? avoid)
    {
        float v = t.Visible.Value(now);
        var size = Measure(dl, th, t, f);
        float h = size.H;

        float targetY = bottom - h, left = window.Right - f.Margin - size.W;
        if (avoid is not null) targetY = MoveAboveAvoided(avoid, targetY, left, h, window.Right - f.Margin, f.Gap);
        if (!t.Placed) { t.Y.Snap(targetY); t.Placed = true; } else t.Y.Set(targetY, now, th.AnimationMs);

        float slide = (1 - Ease.OutCubic(v)) * th.S(24);
        var r = new RectF(MathF.Round(left + slide), MathF.Round(t.Y.Value(now)), size.W, h);
        Widgets.FloatingPanel(dl, th, r, th.S(10), v);
        Paint(dl, th, t, r, size, f, v);
        t.Rect = r;
        return targetY + h - (h + f.Gap) * MathF.Min(1, v * 1.5f);
    }

    private static ToastSize Measure(DrawList dl, Theme th, Toast t, in ToastFrame f)
    {
        float px = f.Px;
        float titleW = dl.Measure(t.Title, px, FontStyle.Bold);
        string? detail = t.Detail is null ? null : dl.Ellipsize(t.Detail, px * 0.9f, th.S(460));
        float detailW = detail is null ? 0 : dl.Measure(detail, px * 0.9f);
        float actionW = t.ActionLabel is null ? 0 : dl.Measure(t.ActionLabel, px * 0.9f, FontStyle.Bold) + th.S(20);
        float w = MathF.Round(f.Pad * 2 + f.IconW + MathF.Max(titleW, detailW) + (actionW > 0 ? th.S(14) + actionW : 0));
        float h = MathF.Round(f.Pad * 2 + f.LineH * (detail is null ? 1 : 2));
        return new ToastSize(detail, actionW, w, h);
    }

    /// <summary>The highest <paramref name="targetY"/> needs to go so the toast overlaps none of the rects.</summary>
    private static float MoveAboveAvoided(List<RectF> avoid, float targetY, float left, float h, float right, float gap)
    {
        for (int k = 0; k < avoid.Count; k++)
        {
            var o = avoid[k];
            if (o.X < right && o.Right > left && o.Y < targetY + h && o.Bottom > targetY)
            {
                targetY = o.Y - gap - h;
#pragma warning disable S127 // Restarting the scan after each move is the algorithm: the new position must clear every rect again.
                k = -1; // moved up: check every rect again
#pragma warning restore S127
            }
        }
        return targetY;
    }

    private static void Paint(DrawList dl, Theme th, Toast t, RectF r, in ToastSize size, in ToastFrame f, float v)
    {
        float px = f.Px, pad = f.Pad;
        var lm = f.Metrics;
        uint accent = t.Kind switch { ToastKind.Error => th.Danger, ToastKind.Success => th.Success, _ => th.Accent };
        int icon = t.Kind switch { ToastKind.Error => Icons.Error, ToastKind.Success => Icons.Check, _ => Icons.Info };
        dl.RoundRect(new RectF(r.X + th.S(1), r.Y + th.S(8), th.S(3), r.H - th.S(16)), th.S(1.5f), accent, v);

        float b1 = MathF.Round(r.Y + pad + (f.LineH - lm.Height) / 2 + lm.Ascender);
        dl.Glyph(icon, r.X + pad, b1, px * 1.05f, accent, v);
        dl.Text(t.Title, r.X + pad + f.IconW, b1, px, th.PanelFg, v, FontStyle.Bold);
        if (size.Detail is { } detail) dl.Text(detail, r.X + pad + f.IconW, MathF.Round(b1 + f.LineH), px * 0.9f, th.Muted, v);
        if (t.ActionLabel is not null)
        {
            // A small accent button on the right; the whole toast takes the click.
            float bh = MathF.Round(lm.Height * 1.5f);
            var br = new RectF(r.Right - pad - size.ActionW, MathF.Round(r.Y + (r.H - bh) / 2), size.ActionW, bh);
            dl.Panel(br, bh / 2, Colors.Mix(th.PanelBg, accent, 0.22f), v, accent, 0.6f * v);
            dl.Text(t.ActionLabel, br.X + th.S(10), Widgets.Baseline(dl, px * 0.9f, br.Y, br.H), px * 0.9f, th.PanelFg, v, FontStyle.Bold);
        }
    }
}
