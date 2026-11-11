using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Search;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.App.Windowing;
using ErikTerm.Config;

namespace ErikTerm.App.Chrome;

/// <summary>
/// The window's chrome as one piece for the event loop: the tab bar, the search bar and the floating window bar
/// (<see cref="FloatingChrome"/>) take input before the panes, tick, draw above the panes (below the palette, menus
/// and toasts), and tell the loop when a blink, caret or poll needs the next frame.
/// </summary>
internal sealed class WindowChrome(
    IWindowHost window, FloatingChrome floating, TabBarController tabs, WindowSearch search, TabBar tabBar,
    WindowTabSwitcher<Tab> switcher, WindowPaneSearch<Pane> paneSearch, PaneRenderer renderer)
{
    private long _wakeAt;
    private bool _overChrome;

    // ---- Input ------------------------------------------------------------------------------

    /// <summary>Hook from OnKeyDown, after the modal overlays: tab rename and the search bar take keys first.</summary>
    public bool KeyDown(uint key, ushort mod, long now)
    {
        if (tabBar.IsEditing) { tabBar.HandleKey(key, mod, now); return true; }
        return search.KeyDown(key, mod, now);
    }

    /// <summary>Hook from OnTextInput.</summary>
    public bool TextInput(string text, long now)
    {
        if (tabBar.IsEditing) { tabBar.HandleText(text, now); return true; }
        return search.TextInput(text, now);
    }

    /// <summary>Hook from OnMouseMove: hover and tab dragging. True when the pointer is over the tab or search bar.</summary>
    public bool MouseMove(float x, float y, long now)
    {
        if (window.PointerBusy) return false;
        floating.UpdateReveal(x, y, pointerInside: true, now); // floating chrome follows the pointer (FloatingChrome)
        bool over = floating.ControlsMouseMove(x, y, now);
        over |= tabBar.HandleMouseMove(x, y);
        var bar = search.Bar;
        if (bar.IsOpen) over |= bar.HandleMouseMove(x, y);
        if (over) window.SetCursor(tabBar.IsDragging ? window.CursorPointer : window.CursorDefault);
        if (over || _overChrome) window.Redraw(); // hover highlights change on enter, inside and on leave
        _overChrome = over;
        return over;
    }

    /// <summary>Hook from OnMouseDown. Clicking outside the search bar closes it (the view stays put).</summary>
    public bool MouseDown(byte button, byte clicks, float x, float y, long now)
    {
        if (floating.ControlsMouseDown(button, x, y)) return true;
        if (tabBar.HandleMouseDown(button, clicks, x, y, now)) return true;
        return search.MouseDown(x, y, now);
    }

    public bool MouseUp(float x, float y, long now)
    {
        bool controls = floating.ControlsMouseUp(x, y);
        bool tabsUp = tabBar.HandleMouseUp(x, y, now); // always: it ends a tab drag
        return controls || tabsUp;
    }

    // ---- Frame ------------------------------------------------------------------------------

    /// <summary>Hook at the end of Tick.</summary>
    public void Tick(long now)
    {
        var settings = window.Settings;
        renderer.CursorAnimation = settings.CursorAnimation && window.AnimationMs > 0;
        renderer.CursorBlinkRate = settings.CursorBlinkRate;
        renderer.CursorBlinkPhase = settings.CursorBlinkStyle == CursorBlinkStyle.Phase;
        renderer.CursorThicknessPoints = (float)settings.CursorThickness.Points;
        renderer.CursorThicknessCells = (float)settings.CursorThickness.Cells;
        renderer.CursorHeightEm = (float)settings.CursorHeight;
        renderer.Scale = window.Scale;
        renderer.SmoothScrolling = window.SmoothScrolling;
        search.Tick(now);
        switcher.Tick(now); // Tabs/WindowTabSwitcher.cs
        tabs.TickOutgoing(now);
        if (tabs.Shown) tabs.RefreshTitles(now);
        floating.Tick(now); // the pointer parked on or resting at the top, the strip handed back, the caption double click (FloatingChrome)
        if (_wakeAt != 0 && now >= _wakeAt) { _wakeAt = 0; window.Redraw(); }
    }

    /// <summary>Hook from IsAnimating.</summary>
    public bool IsAnimating(long now)
    {
        var settings = window.Settings;
        return tabBar.IsAnimating(now) || search.Bar.IsAnimating(now) || paneSearch.IsAnimating(now) || search.IsScrolling || tabs.Outgoing is not null
            || floating.IsAnimating(now) // the reveal and its buttons (FloatingChrome)
            || CursorRenderer.MsUntilNextFrame(window.Focused, true, window.IsWindowFocused, settings.CursorBlinkRate, now, settings.CursorBlinkStyle == CursorBlinkStyle.Phase) == 0;
    }

    /// <summary>Hook from the event loop: how long it may sleep before a blink or caret needs a frame.</summary>
    public int WakeMs(long now)
    {
        var settings = window.Settings;
        int ms = CursorRenderer.MsUntilNextFrame(window.Focused, true, window.IsWindowFocused, settings.CursorBlinkRate, now, settings.CursorBlinkStyle == CursorBlinkStyle.Phase);
        ms = Math.Min(ms, search.Bar.MsUntilCaretChange(now));
        ms = Math.Min(ms, ImageAnimationWakeMs(now));
        ms = Math.Min(ms, window.Osc52WakeMs(now)); // a clipboard request's expiry (WindowFeatures/WindowOsc52.cs)
        ms = Math.Min(ms, switcher.WakeMs(now)); // the hold's quick-tap delay (Tabs/WindowTabSwitcher.cs)
        if (ms != int.MaxValue)
        {
            ms = Math.Max(1, ms);
            _wakeAt = now + ms; // a blink/caret transition: draw a frame then
        }
        // Tab titles are polled, but only redraw when one changed (RefreshTitles).
        if (tabBar.IsVisible(now)) ms = Math.Min(ms, 500);
        // Likewise the pointer resting on the floating chrome: polled (FloatingChrome), drawn only when it moves on.
        if (floating.RevealPolling) ms = Math.Min(ms, 90);
        ms = Math.Min(ms, floating.ArmWakeMs(now)); // waiting to see whether the pointer rests at the top
        return ms;
    }

    /// <summary>
    /// Kitty animations: a timed redraw when a frame that was on screen in the last frame is due to change. Only
    /// animations the renderer drew count (see ImageRenderer.NextAnimationAt), so a hidden pane, a background tab or
    /// an image scrolled out of view stops costing frames after one more frame; stopped animations and ones waiting
    /// for frames cost nothing.
    /// </summary>
    private int ImageAnimationWakeMs(long now)
    {
        long at = renderer.NextAnimationAt;
        if (at == long.MaxValue) return int.MaxValue;
        return (int)Math.Clamp(at - now, 1, int.MaxValue - 1);
    }

    /// <summary>Hook from Render, above the panes and below the palette/menus/toasts.</summary>
    public void Draw(DrawList dl, long now)
    {
        floating.DrawPanel(dl, now); // the floating strip the next two ride in on (FloatingChrome)
        tabs.Draw(dl, now);
        floating.DrawControls(dl, now); // FloatingChrome
        var bar = search.Bar;
        bar.Draw(dl, window.PaneOverlayTheme(bar.Pane, now), now);
    }
}
