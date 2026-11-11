using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>
/// An overlay footer's key hints ("↵ open   esc close"), laid out left to right from <paramref name="x"/>: keycaps centred on
/// <paramref name="centerY"/>, a muted label on <paramref name="baseline"/>, then <paramref name="spacing"/> before the next.
/// A value on the stack, built once per footer per frame; allocation-free.
/// </summary>
internal struct FooterHints(DrawList dl, Theme th, float x, float centerY, float baseline, float alpha, float spacing)
{
    private float _pen = x;

    public void Add(string[] keys, string label)
    {
        float keysWidth = Widgets.KeycapsWidth(dl, th, keys);
        Widgets.Keycaps(dl, th, keys, _pen + keysWidth, centerY, alpha);
        _pen += keysWidth + th.S(6);
        _pen += dl.Text(label, _pen, baseline, th.UiPx * 0.82f, th.Muted, alpha);
        _pen += spacing;
    }
}
