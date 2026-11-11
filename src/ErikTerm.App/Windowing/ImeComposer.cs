using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>Input methods: the text being composed is drawn at the cursor, and the candidate list is placed next to it.</summary>
#pragma warning disable S6640 // Native interop: SDL takes the text input rectangle by pointer.
internal sealed unsafe class ImeComposer
#pragma warning restore S6640
{
    private string _preedit = "";
    private Sdl.Rect _area;

    /// <summary>An input method is composing: Enter commits, Escape cancels, nothing reaches the program.</summary>
    public bool Composing => _preedit.Length > 0;

    /// <summary>SDL_EVENT_TEXT_EDITING (and "" when the composition ends). True when the text changed (a frame is due).</summary>
    public bool SetText(string text)
    {
        if (text == _preedit) return false;
        _preedit = text;
        return true;
    }

    private static (float X, float Y) CursorCellPixels(Pane focused, DrawList dl, long now)
    {
        var rect = focused.Rect.Value(now);
        var term = focused.Term;
        return (MathF.Round(rect.X) + term.CursorX * dl.Fonts.CellWidth,
            MathF.Round(rect.Y) + (term.CursorY + term.ViewOffset) * dl.Fonts.CellHeight);
    }

    /// <summary>Tells the system where the cursor is, when it moved. The area is in window points, the rest of the frame in pixels.</summary>
    public void UpdateArea(nint window, Pane focused, DrawList dl, long now)
    {
        var (x, y) = CursorCellPixels(focused, dl, now);
        float density = MathF.Max(0.1f, Sdl.GetWindowPixelDensity(window));
        var area = new Sdl.Rect
        {
            X = (int)(x / density),
            Y = (int)(y / density),
            W = (int)MathF.Ceiling(dl.Fonts.CellWidth / density),
            H = (int)MathF.Ceiling(dl.Fonts.CellHeight / density),
        };
        if (area.X == _area.X && area.Y == _area.Y && area.W == _area.W && area.H == _area.H) return;
        _area = area;
        Sdl.SetTextInputArea(window, &area, 0);
    }

    public void Draw(DrawList dl, Theme theme, Pane focused, long now)
    {
        if (_preedit.Length == 0) return;
        var fonts = dl.Fonts;
        float px = fonts.GridPx;
        var pane = focused.Rect.Value(now);
        var (cursorX, y) = CursorCellPixels(focused, dl, now);
        float width = dl.Measure(_preedit, px);
        float x = MathF.Round(ImePlacement.Left(cursorX, width, pane.X, pane.Right));
        dl.Rect(new RectF(x, y, width, fonts.CellHeight), theme.PanelBg);
        dl.Text(_preedit, x, y + fonts.Baseline, px, theme.PanelFg, 1);
        dl.Rect(new RectF(x, fonts.UnderlinePosition + y, width, fonts.LineThickness), theme.Accent);
    }
}
