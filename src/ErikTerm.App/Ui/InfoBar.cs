using ErikTerm.App.Rendering;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Ui;

/// <summary>The accent of an <see cref="InfoBarMessage"/>: the tint, the border, the icon and the primary button.</summary>
internal enum InfoBarTone { Accent, Info, Success, Warning, Danger }

/// <summary>A button on an info bar. The click runs <see cref="Run"/>; the bar doesn't close by itself.</summary>
internal sealed class InfoBarButton(string label, Action run, bool primary = false)
{
    public string Label = label;
    public Action Run = run;
    /// <summary>Drawn in the message's tone (the suggested answer); the others are neutral.</summary>
    public bool Primary = primary;
    internal RectF Rect;
}

/// <summary>
/// One message for an <see cref="InfoBar"/>. Texts are drawn as plain text (no markup), one line, ellipsized —
/// pass program-supplied text through a sanitiser first anyway (Notifications/NotificationText.Clean).
/// Changing a field of the shown message: call <see cref="InfoBar.Invalidate"/>.
/// </summary>
internal sealed class InfoBarMessage
{
    /// <summary>Identity: <see cref="InfoBar.Show"/> with a message of the same key replaces it in place.</summary>
    public required string Key;
    /// <summary>The bar shows the message with the highest priority (the newest of equals); the rest wait their turn.</summary>
    public int Priority;
    public InfoBarTone Tone;
    public int Icon = Icons.Info;
    /// <summary>Not None: an agent status dot (AgentBadge colors) instead of <see cref="Icon"/>.</summary>
    public AgentState Dot;
    /// <summary>A small muted chip before the text ("Tab 2 · ~/src").</summary>
    public string? Label;
    /// <summary>The main text, bold.</summary>
    public string Text = "";
    /// <summary>Muted, after the text.</summary>
    public string? Detail;
    /// <summary>Right-aligned muted text before the buttons ("2 min ago").</summary>
    public string? Meta;
    /// <summary>A clickable counter pill after the buttons ("3 unread").</summary>
    public string? Chip;
    public Action? ChipClick;
    public InfoBarButton[] Buttons = [];
    /// <summary>Shows ✕; clicking it calls <see cref="Dismissed"/> (the owner decides whether to remove the message).</summary>
    public bool Dismissible = true;
    public Action? Dismissed;
}

/// <summary>
/// A slim in-window bar (warning, question or status) that sits in the layout — the window reserves its height so it
/// never covers terminal text — with an icon, text, buttons and ✕. Several features can post messages; one shows at a
/// time (by priority). It slides and fades in, cross-fades between messages and fades out when the last one goes.
/// Drawing is allocation-free once texts are fitted (fits are cached per width).
/// <para>Use: keep an <see cref="InfoBar"/> per window, <see cref="Show"/> / <see cref="Remove"/> messages, reserve
/// <see cref="Reserve"/> pixels while <see cref="IsShown"/> (re-layout when it flips), call <see cref="Draw"/> in the
/// reserved slot and route the mouse to <see cref="HandleMouseMove"/> / <see cref="HandleMouseDown"/> first.</para>
/// </summary>
internal sealed class InfoBar
{
    private readonly List<InfoBarMessage> _messages = [];
    private InfoBarMessage? _drawn;          // what is on screen (kept while fading out)
    private Tween _visible = new(0), _content = new(1);
    private float _animMs = 160;
    private RectF _rect, _chipRect, _closeRect;
    private int _hover = -1;                 // button index, ChipHit, CloseHit
    private const int ChipHit = 1000, CloseHit = 1001;
    private FitCache _textFit, _detailFit, _labelFit;

    /// <summary>Whether a message is up (the layout reserves room while true).</summary>
    public bool IsShown => _messages.Count > 0;
    public InfoBarMessage? Current => Pick();
    public bool Contains(string key) => IndexOf(key) >= 0;

    public static float Height(Theme th) => MathF.Round(th.UiPx * 2.35f);
    /// <summary>Height plus the gap to the panes.</summary>
    public static int Reserve(Theme th) => (int)MathF.Round(Height(th) + th.S(6));

    public bool IsAnimating(long now) => _visible.IsAnimating(now) || _content.IsAnimating(now);
    public bool IsVisible(long now) => IsShown || _visible.Value(now) > 0.001f;

    /// <summary>Adds the message, or replaces the one with the same key.</summary>
    public void Show(InfoBarMessage m, Theme th, long now)
    {
        _animMs = th.AnimationMs;
        int i = IndexOf(m.Key);
        if (i >= 0) _messages[i] = m; else _messages.Add(m);
        Update(now);
    }

    public void Remove(string key, long now)
    {
        int i = IndexOf(key);
        if (i < 0) return;
        _messages.RemoveAt(i);
        Update(now);
    }

    /// <summary>The shown message's texts changed: fit them again.</summary>
    public void Invalidate() { _textFit = default; _detailFit = default; _labelFit = default; }

    private void Update(long now)
    {
        var pick = Pick();
        if (pick is null) { _visible.Set(0, now, _animMs * 0.9f); return; }
        if (!ReferenceEquals(pick, _drawn))
        {
            // Another message: cross-fade. The same one updated (its age, its count): just redraw.
            bool other = _drawn is null || _drawn.Key != pick.Key || _drawn.Text != pick.Text;
            if (other && _drawn is not null && _visible.Value(now) > 0.05f) { _content.Snap(0); _content.Set(1, now, _animMs); }
            if (other) _hover = -1;
            _drawn = pick;
        }
        Invalidate();
        _visible.Set(1, now, _animMs * 1.2f);
    }

    private InfoBarMessage? Pick()
    {
        InfoBarMessage? best = null;
        foreach (var m in _messages) if (best is null || m.Priority >= best.Priority) best = m;
        return best;
    }

    private int IndexOf(string key)
    {
        for (int i = 0; i < _messages.Count; i++) if (_messages[i].Key == key) return i;
        return -1;
    }

    // ---- Input ------------------------------------------------------------------------------

    /// <summary>True when the pointer is over the bar. <paramref name="changed"/>: the hovered control changed (redraw).</summary>
    public bool HandleMouseMove(float x, float y, out bool changed)
    {
        changed = false;
        if (!IsShown || _drawn is null || !_rect.Contains(x, y))
        {
            if (_hover != -1) { _hover = -1; changed = true; }
            return false;
        }
        int hover = HitTest(x, y);
        changed = hover != _hover;
        _hover = hover;
        return true;
    }

    /// <summary>Whether the pointer is over something clickable (for the pointer cursor).</summary>
    public bool OverControl => _hover >= 0;

    /// <summary>A click on the bar: runs its button / chip / ✕. True when the click was on the bar (consumed).</summary>
    public bool HandleMouseDown(float x, float y)
    {
        if (!IsShown || _drawn is not { } m || !_rect.Contains(x, y)) return false;
        int hit = HitTest(x, y);
        if (hit == CloseHit) m.Dismissed?.Invoke();
        else if (hit == ChipHit) m.ChipClick?.Invoke();
        else if (hit >= 0 && hit < m.Buttons.Length) m.Buttons[hit].Run();
        return true;
    }

    private int HitTest(float x, float y)
    {
        if (_drawn is not { } m) return -1;
        if (m.Dismissible && _closeRect.Contains(x, y)) return CloseHit;
        if (m.Chip is not null && m.ChipClick is not null && _chipRect.Contains(x, y)) return ChipHit;
        for (int i = 0; i < m.Buttons.Length; i++) if (m.Buttons[i].Rect.Contains(x, y)) return i;
        return -1;
    }

    // ---- Drawing ----------------------------------------------------------------------------

    /// <summary>The sizes the bar's controls share this frame.</summary>
    private readonly record struct BarFrame(float Px, float Small, float CtrlH, float CtrlY, uint Accent, float Alpha);

    /// <summary>Draws into <paramref name="slot"/> (the reserved area, <see cref="Height"/> tall). <paramref name="fromTop"/>: slides down into place (else up).</summary>
    public void Draw(DrawList dl, Theme th, RectF slot, bool fromTop, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f || _drawn is not { } m)
        {
            if (!IsShown) _drawn = null;
            _rect = default;
            return;
        }
        float slide = (1 - Ease.OutCubic(v)) * th.S(fromTop ? -8 : 8);
        var r = new RectF(slot.X, MathF.Round(slot.Y + slide), slot.W, slot.H);
        _rect = r;
        float ca = v * _content.Value(now); // content alpha (cross-fade between messages)

        uint accent = m.Dot != AgentState.None ? AgentBadge.Color(th, m.Dot) : ToneColor(th, m.Tone);
        dl.Panel(r, th.S(8), Colors.Mix(th.PanelBg, accent, 0.07f), 0.97f * v, Colors.Mix(th.PanelBg, accent, 0.32f), v);

        float px = th.UiPx * 0.92f, small = th.UiPx * 0.84f;
        var lm = dl.MetricsFor(px);
        float cy = r.Y + r.H / 2;
        float ctrlH = MathF.Round(lm.Height * 1.45f);
        var f = new BarFrame(px, small, ctrlH, MathF.Round(cy - ctrlH / 2), accent, ca);

        // Right side, right to left: ✕, chip, buttons, meta.
        float right = r.Right - th.S(8);
        right = DrawClose(dl, th, m, right, f);
        right = DrawChip(dl, th, m, right, f);
        right = DrawButtons(dl, th, m, right, f);
        right = DrawMeta(dl, th, m, r, right, f);

        DrawLeft(dl, th, m, r, right, f, now);
    }

    private float DrawClose(DrawList dl, Theme th, InfoBarMessage m, float right, in BarFrame f)
    {
        if (!m.Dismissible)
        {
            _closeRect = default;
            return right;
        }
        float cs = f.CtrlH;
        _closeRect = new RectF(right - cs, f.CtrlY, cs, cs);
        if (_hover == CloseHit) dl.RoundRect(_closeRect, th.S(6), Colors.Mix(th.PanelBg, th.PanelFg, 0.14f), f.Alpha);
        float gw = dl.Measure("", f.Small);
        dl.Glyph(Icons.Close, _closeRect.X + (cs - gw) / 2, Widgets.Baseline(dl, f.Small, f.CtrlY, cs), f.Small, _hover == CloseHit ? th.PanelFg : th.Muted, f.Alpha);
        return _closeRect.X - th.S(4);
    }

    private float DrawChip(DrawList dl, Theme th, InfoBarMessage m, float right, in BarFrame f)
    {
        if (m.Chip is not { } chip)
        {
            _chipRect = default;
            return right;
        }
        float cw = MathF.Round(dl.Measure(chip, f.Small, FontStyle.Bold) + th.S(18));
        _chipRect = new RectF(right - cw, f.CtrlY, cw, f.CtrlH);
        float fill = _hover == ChipHit ? 0.34f : 0.22f;
        dl.Panel(_chipRect, f.CtrlH / 2, Colors.Mix(th.PanelBg, f.Accent, fill), f.Alpha, Colors.Mix(th.PanelBg, f.Accent, 0.5f), f.Alpha);
        dl.Text(chip, _chipRect.X + th.S(9), Widgets.Baseline(dl, f.Small, f.CtrlY, f.CtrlH), f.Small, th.PanelFg, f.Alpha, FontStyle.Bold);
        return _chipRect.X - th.S(8);
    }

    private float DrawButtons(DrawList dl, Theme th, InfoBarMessage m, float right, in BarFrame f)
    {
        for (int i = m.Buttons.Length - 1; i >= 0; i--)
        {
            var btn = m.Buttons[i];
            var style = btn.Primary ? FontStyle.Bold : FontStyle.Regular;
            float bw = MathF.Round(dl.Measure(btn.Label, f.Small, style) + th.S(22));
            btn.Rect = new RectF(right - bw, f.CtrlY, bw, f.CtrlH);
            bool hot = _hover == i;
            if (btn.Primary)
                dl.Panel(btn.Rect, th.S(6), Colors.Mix(th.PanelBg, f.Accent, hot ? 0.40f : 0.26f), f.Alpha, Colors.Mix(th.PanelBg, f.Accent, 0.65f), f.Alpha);
            else
                dl.Panel(btn.Rect, th.S(6), Colors.Mix(th.PanelBg, th.PanelFg, hot ? 0.15f : 0.07f), f.Alpha, th.Border, f.Alpha);
            dl.Text(btn.Label, btn.Rect.X + th.S(11), Widgets.Baseline(dl, f.Small, f.CtrlY, f.CtrlH), f.Small, th.PanelFg, f.Alpha, style);
            right = btn.Rect.X - th.S(6);
        }
        return right;
    }

    private static float DrawMeta(DrawList dl, Theme th, InfoBarMessage m, RectF r, float right, in BarFrame f)
    {
        if (m.Meta is not { Length: > 0 } meta) return right;
        float mw = dl.Measure(meta, f.Small);
        if (right - mw <= r.X + r.W * 0.45f) return right;
        dl.Text(meta, right - th.S(6) - mw, Widgets.Baseline(dl, f.Small, r.Y, r.H), f.Small, th.Muted, f.Alpha);
        return right - (mw + th.S(6) + th.S(8));
    }

    /// <summary>Left side: icon or dot, label chip, text, detail.</summary>
    private void DrawLeft(DrawList dl, Theme th, InfoBarMessage m, RectF r, float right, in BarFrame f, long now)
    {
        float gap = th.S(8);
        float b = Widgets.Baseline(dl, f.Px, r.Y, r.H);
        float cy = r.Y + r.H / 2;
        float x = r.X + th.S(14);
        if (m.Dot != AgentState.None)
        {
            float d = MathF.Round(th.S(9));
            AgentBadge.Dot(dl, th, m.Dot, x + d / 2 + th.S(2), cy, d, f.Alpha, now);
            x += d + th.S(14);
        }
        else x += dl.Glyph(m.Icon, x, b, f.Px * 1.05f, f.Accent, f.Alpha) + th.S(10);

        float avail = right - x - gap;
        if (m.Label is { Length: > 0 } label && avail > th.S(160))
        {
            x += DrawLabelChip(dl, th, label, x, cy, avail, f) + gap;
            avail = right - x - gap;
        }
        if (avail <= th.S(20)) return;
        string text = _textFit.Get(dl, m.Text, f.Px, avail, FontStyle.Bold);
        float tw = dl.Text(text, x, b, f.Px, th.PanelFg, f.Alpha, FontStyle.Bold);
        if (m.Detail is { Length: > 0 } detail && avail - tw > th.S(60))
        {
            float dx = x + tw + th.S(10);
            dl.Text(_detailFit.Get(dl, detail, f.Px, right - gap - dx, FontStyle.Regular), dx, b, f.Px, Colors.Mix(th.PanelFg, th.Muted, 0.45f), f.Alpha);
        }
    }

    /// <summary>Returns the chip's width.</summary>
    private float DrawLabelChip(DrawList dl, Theme th, string label, float x, float cy, float avail, in BarFrame f)
    {
        string shown = _labelFit.Get(dl, label, f.Small, MathF.Min(avail * 0.35f, th.S(260)), FontStyle.Regular);
        float lw = MathF.Round(dl.Measure(shown, f.Small) + th.S(14));
        var lr = new RectF(x, MathF.Round(cy - (f.CtrlH - th.S(4)) / 2), lw, f.CtrlH - th.S(4));
        dl.RoundRect(lr, th.S(5), Colors.Mix(th.PanelBg, th.PanelFg, 0.09f), f.Alpha);
        dl.Text(shown, lr.X + th.S(7), Widgets.Baseline(dl, f.Small, lr.Y, lr.H), f.Small, Colors.Mix(th.PanelFg, th.Muted, 0.5f), f.Alpha);
        return lw;
    }

    public static uint ToneColor(Theme th, InfoBarTone tone) => tone switch
    {
        InfoBarTone.Info => th.Info,
        InfoBarTone.Success => th.Success,
        InfoBarTone.Warning => th.Warning,
        InfoBarTone.Danger => th.Danger,
        _ => th.Accent,
    };

    /// <summary>An ellipsized text, recomputed only when the source string or the width changes.</summary>
    private struct FitCache
    {
        private string? _source, _shown;
        private float _width, _px;

        public string Get(DrawList dl, string source, float px, float width, FontStyle style)
        {
#pragma warning disable S1244 // Cache key: _px is exactly the last px, and any change means ellipsize again.
            if (_shown is not null && ReferenceEquals(source, _source) && MathF.Abs(width - _width) < 0.5f && px == _px) return _shown;
#pragma warning restore S1244
            _source = source;
            _width = width;
            _px = px;
            return _shown = dl.Ellipsize(source, px, MathF.Max(0, width), style);
        }
    }
}
