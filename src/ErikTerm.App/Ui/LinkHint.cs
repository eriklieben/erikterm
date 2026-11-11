using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>Codicons for links and shell integration (JetBrainsMono Nerd Font).</summary>
internal static class ShellIcons
{
    public const int Link = 0xEB15;
    public const int LinkExternal = 0xEB14;
    public const int Zap = 0xEA86;
    public const int Output = 0xEB9D;
    public const int Error = 0xEA87;
    public const int Pass = 0xEBA4;
    public const int Clipboard = 0xEAC0;
}

/// <summary>
/// A browser-style status pill at the bottom of a pane: the URL under the pointer (and how to open
/// it), or what a gutter click would do. Fades and slides in; moves to the top when the pointer is
/// near the bottom so it never covers what it describes.
/// </summary>
internal sealed class LinkHint
{
    private Pane? _pane;
    public Pane? Pane => _pane;
    private string _text = "", _action = "";
    private int _icon;
    private uint? _iconColor;
    private bool _atTop;
    private Tween _visible = new(0);
    private Tween _armedFade = new(0);
    private float _animMs = 160;

    public bool IsAnimating(long now) => _visible.IsAnimating(now) || _armedFade.IsAnimating(now);

    public void Show(Pane pane, int icon, string text, string action, bool armed, bool atTop, Theme th, long now, uint? iconColor = null)
    {
        _iconColor = iconColor;
        _animMs = th.AnimationMs;
        _pane = pane;
        _icon = icon;
        _text = text;
        _action = action;
        _atTop = atTop;
        _visible.Set(1, now, _animMs * 1.2f);
        _armedFade.Set(armed ? 1 : 0, now, _animMs * 0.8f);
    }

    public void Hide(long now)
    {
        if (_visible.Target != 0) _visible.Set(0, now, _animMs * 1.4f);
    }

    public void Forget(Pane pane)
    {
        if (_pane == pane) { _pane = null; _visible.Snap(0); }
    }

    public void Draw(DrawList dl, Theme th, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f || _pane is null) return;
        RectF pr = _pane.Rect.Value(now);
        float armed = _armedFade.Value(now);

        float px = th.UiPx * 0.86f;
        float smallPx = px * 0.92f;
        float h = MathF.Round(px * 2.05f);
        float padX = th.S(11), iconW = px * 1.7f, gap = th.S(12);
        float maxW = MathF.Max(th.S(120), pr.W - th.S(24));

        float actionW = _action.Length > 0 ? dl.Measure(_action, smallPx) : 0;
        float textMax = MathF.Max(th.S(60), maxW - padX * 2 - iconW - (actionW > 0 ? actionW + gap : 0));
        string text = dl.Ellipsize(_text, px, textMax);
        float textW = dl.Measure(text, px);
        float w = MathF.Round(padX * 2 + iconW + textW + (actionW > 0 ? gap + actionW : 0));

        float slide = (1 - Ease.OutCubic(v)) * th.S(6);
        float margin = th.S(10);
        float y = _atTop ? pr.Y + margin - slide : pr.Bottom - margin - h + slide;
        var r = new RectF(MathF.Round(pr.X + margin), MathF.Round(y), w, h);

        dl.Shadow(r.Offset(0, th.S(6)), h / 2, th.S(16), 0.45f * v);
        uint border = Colors.Mix(Colors.Mix(th.PanelBg, th.PanelFg, 0.16f), th.Accent, armed);
        dl.Panel(r, h / 2, th.PanelBg, 0.96f * v, border, v, MathF.Max(1, th.S(1)));

        float b = Widgets.Baseline(dl, px, r.Y, h);
        float x = r.X + padX;
        dl.Glyph(_icon, x, b, px * 1.05f, _iconColor ?? Colors.Mix(th.Muted, th.Accent, 0.5f + 0.5f * armed), v);
        x += iconW;
        dl.Text(text, x, b, px, th.PanelFg, v);
        x += textW + gap;
        if (actionW > 0)
            dl.Text(_action, x, Widgets.Baseline(dl, smallPx, r.Y, h), smallPx, Colors.Mix(th.Muted, th.Accent, armed), v);
    }
}
