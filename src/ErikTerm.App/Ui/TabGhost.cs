using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>
/// The dragged tab outside its window: a small transparent tooltip popup of the source window that follows the pointer.
/// A popup is positioned relative to its parent, which is what Wayland allows (toplevels can't be placed), and the
/// compositor keeps it on screen. It has its own renderer, so its own glyph atlas, over the source window's fonts.
/// </summary>
internal sealed class TabGhost : IDisposable
{
    /// <summary>Room around the pill for its shadow, in points.</summary>
    public const int MarginPoints = 14;

    private nint _window, _renderer;
    private GlyphAtlas? _atlas;
    private DrawList? _dl;
    private readonly float _scale;
    private readonly float _w, _h; // the pill, pixels

    private TabGhost(nint window, nint renderer, GlyphAtlas atlas, DrawList dl, float scale, float w, float h)
    {
        (_window, _renderer, _atlas, _dl, _scale, _w, _h) = (window, renderer, atlas, dl, scale, w, h);
    }

    /// <summary>A ghost for a pill of <paramref name="w"/>×<paramref name="h"/> pixels at (x, y) points relative to <paramref name="parent"/>; null when popups aren't available.</summary>
    public static TabGhost? Create(nint parent, FontSet fonts, float w, float h, float scale, int x, int y)
    {
        int wPts = (int)MathF.Ceiling(w / scale) + 2 * MarginPoints, hPts = (int)MathF.Ceiling(h / scale) + 2 * MarginPoints;
        nint window = Sdl.CreatePopupWindow(parent, x, y, wPts, hPts, Sdl.WINDOW_TOOLTIP | Sdl.WINDOW_TRANSPARENT | Sdl.WINDOW_HIGH_PIXEL_DENSITY);
        if (window == 0) return null;
        nint renderer = Sdl.CreateRenderer(window, null);
        if (renderer == 0)
        {
            Sdl.DestroyWindow(window);
            return null;
        }
        Sdl.SetRenderVSync(renderer, 0); // never wait on the ghost
        var atlas = new GlyphAtlas(renderer, fonts);
        float density = Sdl.GetWindowPixelDensity(window);
        return new TabGhost(window, renderer, atlas, new DrawList(renderer, atlas, fonts), density > 0 ? density : scale, w, h);
    }

    /// <summary>Moves the popup to (x, y) points relative to its parent window.</summary>
    public void Move(int x, int y) => Sdl.SetWindowPosition(_window, x, y);

    /// <summary>Debug (script shot:): the next drawn frame is also saved to this .bmp path.</summary>
    public void Capture(string path)
    {
        if (_dl is not null) _dl.CapturePath = path;
    }

    public void Draw(Theme theme, string number, string title)
    {
        if (_dl is null) return;
        _dl.Begin();
        float m = MarginPoints * _scale, px = theme.UiPx * 0.88f;
        TabBar.DrawPill(_dl, theme, new RectF(m, m, _w, _h), number, title, px, _dl.MetricsFor(px), 1);
        _dl.Present(0, 0);
    }

    public void Dispose()
    {
        _atlas?.Dispose();
        _atlas = null;
        _dl = null;
        if (_renderer != 0) Sdl.DestroyRenderer(_renderer);
        if (_window != 0) Sdl.DestroyWindow(_window);
        _renderer = _window = 0;
    }
}
