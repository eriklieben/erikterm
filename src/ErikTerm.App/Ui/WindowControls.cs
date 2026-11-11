using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>
/// The minimize / maximize / close buttons a borderless window has to draw for itself, in the top-right corner
/// next to the tab bar. They fade in with the rest of the chrome (Chrome/WindowChrome.cs) and follow the system's
/// shape: flat squares rather than pills, close lighting up red and the other two a neutral wash, and the middle
/// one swapping between the maximize and restore glyphs with the window's state.
/// </summary>
internal sealed class WindowControls
{
    private RectF _strip;
    private WindowControl _hover = WindowControl.None, _pressed = WindowControl.None;
    private readonly Tween[] _hot = [new(0), new(0), new(0)];

    public Action? Minimize, ToggleMaximize, Close;

    /// <summary>Set by the caller each frame: the middle button shows "restore" when the window is maximized.</summary>
    public bool Maximized;

    /// <summary>Button height in pixels, matched to the tab bar so the chrome reads as one strip.</summary>
    public static float Height(Theme th) => TabBar.Height(th);

    public static float ButtonWidth(Theme th) => MathF.Round(th.S(30));

    /// <summary>The strip as last laid out; empty before the first frame.</summary>
    public RectF Bounds => _strip;

    public bool Contains(float x, float y) => _strip.Contains(x, y);

    public void Layout(Theme th, float windowWidth, float top, float inset)
    {
        _strip = WindowControlsLayout.Strip(windowWidth, top, ButtonWidth(th), Height(th), inset);
    }

    public void Draw(DrawList dl, Theme th, long now, float alpha)
    {
        if (alpha <= 0.001f || _strip.W <= 0) return;
        float px = th.UiPx * 0.78f;
        for (int i = 0; i < WindowControlsLayout.Count; i++)
            DrawButton(dl, th, i, px, alpha, now);
    }

    private void DrawButton(DrawList dl, Theme th, int i, float px, float alpha, long now)
    {
        var r = WindowControlsLayout.Button(_strip, i);
        var which = (WindowControl)i;
        float hot = _hot[i].Value(now);
        if (hot > 0.001f)
        {
            // Close goes red the way every platform's does; the others take a neutral wash.
            uint wash = which == WindowControl.Close ? th.Danger : th.Foreground;
            float strength = which == WindowControl.Close ? 0.85f : 0.14f;
            dl.RoundRect(r.Inset(th.S(2)), th.S(5), wash, strength * hot * alpha);
        }
        uint fg = which == WindowControl.Close && hot > 0.5f ? 0xFFFFFF : th.PanelFg;
        int icon = which switch
        {
            WindowControl.Minimize => Icons.ChromeMinimize,
            WindowControl.Maximize => Maximized ? Icons.ChromeRestore : Icons.ChromeMaximize,
            _ => Icons.ChromeClose,
        };
        float w = dl.Measure(char.ConvertFromUtf32(icon), px);
        float baseline = Widgets.Baseline(dl, px, r.Y, r.H);
        dl.Glyph(icon, MathF.Round(r.X + (r.W - w) / 2), baseline, px, fg, alpha * (0.7f + 0.3f * hot));
    }

    /// <summary>True when the pointer is over the strip (the caller then keeps it away from the panes).</summary>
    public bool HandleMouseMove(float x, float y, long now)
    {
        var hit = WindowControlsLayout.HitTest(_strip, x, y);
        if (hit == _hover) return hit != WindowControl.None;
        _hover = hit;
        for (int i = 0; i < _hot.Length; i++)
            _hot[i].Set((WindowControl)i == hit ? 1 : 0, now, 110);
        return hit != WindowControl.None;
    }

    public void HandleMouseLeave(long now)
    {
        _hover = _pressed = WindowControl.None;
        foreach (ref var t in _hot.AsSpan()) t.Set(0, now, 110);
    }

    public bool HandleMouseDown(byte button, float x, float y)
    {
        if (button != 1) return false;
        _pressed = WindowControlsLayout.HitTest(_strip, x, y);
        return _pressed != WindowControl.None;
    }

    /// <summary>Acts only when the release lands on the button the press started on, as a button should.</summary>
    public bool HandleMouseUp(float x, float y)
    {
        var pressed = _pressed;
        _pressed = WindowControl.None;
        if (pressed == WindowControl.None) return false;
        if (WindowControlsLayout.HitTest(_strip, x, y) != pressed) return true;
        switch (pressed)
        {
            case WindowControl.Minimize: Minimize?.Invoke(); break;
            case WindowControl.Maximize: ToggleMaximize?.Invoke(); break;
            case WindowControl.Close: Close?.Invoke(); break;
        }
        return true;
    }

    public bool IsAnimating(long now)
    {
        foreach (ref readonly var t in _hot.AsSpan())
            if (t.IsAnimating(now)) return true;
        return false;
    }
}
