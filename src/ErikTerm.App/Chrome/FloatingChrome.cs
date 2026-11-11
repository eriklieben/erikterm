using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.App.Windowing;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.Chrome;

/// <summary>
/// The chrome a borderless window has to provide itself: caption buttons, and the rule that brings them and the
/// tab bar out of hiding. With no title bar there is nowhere to put either permanently without eating a row of
/// the grid, so both hide above the window's top edge and slide down when the pointer reaches it — the same
/// gesture a hidden taskbar or a full-screen browser's tab strip answers to. Nothing here applies to a decorated
/// window: it keeps the system's title bar and the tab bar's own show/hide rule.
/// <para>
/// The chrome never covers the grid and never makes it reflow. It asks the window for a strip of its own: the
/// window grows upwards by the strip's height and moves up by the same amount, so the panes stay exactly where
/// they were on screen and keep exactly the rows and columns they had — <see cref="Core.Terminal.Resize"/> is never
/// reached, so the shell is not told anything happened. Only where the window cannot grow (maximized, full
/// screen, hard against the top of the display, or a compositor that places windows itself) does it fall back to
/// sliding the panes down inside the window, which still covers nothing and still keeps the grid's size.
/// </para>
/// </summary>
internal sealed class FloatingChrome
{
    /// <summary>How the window is making room for the floating chrome.</summary>
    private enum ChromeRoom
    {
        /// <summary>None taken: the chrome is away (or on its way out).</summary>
        None,
        /// <summary>The window grew upwards for it; the panes never moved.</summary>
        Grown,
        /// <summary>Nowhere to grow, so the panes slid down inside the window instead.</summary>
        Lifted,
    }

    private readonly IWindowHost _window;
    private readonly WindowControls _controls = new();
    private Tween _fade = new(0);
    private bool _revealed;
    /// <summary>The chrome is out because <see cref="PointerInWindow"/> says so, SDL having stopped reporting it.</summary>
    private bool _edgeHold;
    private ChromeRoom _room;
    /// <summary>When the pointer came to rest in the reveal band (0: it isn't resting there), and where.</summary>
    private long _armedAt;
    private float _armX, _armY;
    private volatile bool _captionDoubleClick;
    private nint _appIcon;
    private bool _appIconTried;

    /// <summary>How long the pointer has to stay at the top before the bar comes in: a sweep past it shows nothing.</summary>
    private const int DwellMs = 180;
    /// <summary>The room actually taken, in window pixels. Pixels, measured, not asked for: see <see cref="TryGrowWindow"/>.</summary>
    private int _roomPx;

    public FloatingChrome(IWindowHost window)
    {
        _window = window;
        _controls.Minimize = () => Sdl.MinimizeWindow(_window.SdlWindow);
        _controls.ToggleMaximize = ToggleMaximize;
        // The same path as the compositor's close, so it still asks when something is running in a pane.
        _controls.Close = () => _window.RequestCloseWindow();
    }

    private Settings Settings => _window.Settings;
    private Theme Theme => _window.Theme;

    /// <summary>
    /// The window draws its own chrome — it floats over the panes and hides itself, caption buttons, a drag and resize
    /// hit test — exactly when it is borderless on a platform where that leaves the window with no frame at all
    /// (Windows, macOS). Not on Linux: there window_decorations = "NONE" is the usual setup under a tiling Wayland
    /// compositor, which places and sizes windows itself, so the tab bar stays where it is and nothing grows or drags.
    /// </summary>
    public bool Overlay => WindowControlsLayout.SelfDrawnChrome(Settings.WindowDecorations, OperatingSystem.IsLinux());

    /// <summary>
    /// Caption buttons are drawn only for a borderless window, and by default only where the platform puts them
    /// on the right and leaves them to the app. macOS keeps its own on the left, so Auto stays out of the way.
    /// </summary>
    public bool ControlsShown => Overlay && Settings.WindowControls switch
    {
        WindowControlsMode.On => true,
        WindowControlsMode.Off => false,
        _ => !OperatingSystem.IsMacOS(),
    };

    /// <summary>
    /// Whether there is a window bar to slide in: a borderless window that draws its own caption buttons. The tab
    /// bar is not part of it; it stays where it is, below the window bar, and moves down with the panes when the
    /// window bar comes in. Without caption buttons (macOS by default) there is nothing to reveal.
    /// </summary>
    private bool Floats => Overlay && ControlsShown;

    /// <summary>
    /// The strip the chrome wants: the bar, the padding above it that it already sits in, and a little breathing
    /// room below so the tab isn't pressed against the panel's bottom edge — deliberately much less than the room
    /// above, which belongs to the window's rounded corner. Everything else measures the strip from here, so this
    /// is the one place to change it: the panel drawn, the reserve the window grew, and the push the panes take
    /// when it couldn't all read it, and they have to agree to the pixel or the grid loses a row.
    /// </summary>
    private float RoomHeight() => TabBar.Height(Theme) + Theme.S(4);

    /// <summary>
    /// How far the window bar has pushed the tab bar down: all of its strip once the window grew for it, otherwise
    /// as far as the bar has slid, which is the same motion and timing the pushed panes follow (ApplyLayout).
    /// </summary>
    public float Push(long now)
    {
        if (!Floats) return 0;
        return _room == ChromeRoom.Grown ? _roomPx : Out(now) * RoomHeight();
    }

    /// <summary>The window bar's own strip, for the hit test: the empty part of it drags the window.</summary>
    public bool InWindowBar(float x, float y) =>
        Floats && _revealed && y >= 0 && y < PanelHeight() && !_controls.Contains(x, y);

    /// <summary>Whether (x, y) is on a caption button (the hit test leaves those their clicks).</summary>
    public bool ControlsContain(float x, float y) => _controls.Contains(x, y);

    /// <summary>The strip as it actually is — what the window gave, once it has given it.</summary>
    private float PanelHeight() => _room == ChromeRoom.None ? RoomHeight() : _roomPx;

    /// <summary>
    /// Pixels the panes are pushed down when the window could not grow for the chrome. Read by the window's
    /// ContentBounds, which adds it to the panes' top without taking it off their height: same rows and columns
    /// either way, so no terminal is resized and nothing reflows.
    /// </summary>
    public int Lift => _room == ChromeRoom.Lifted ? _roomPx : 0;

    /// <summary>
    /// Pixels the window grew at the top for the chrome, which the panes then leave to it. Read by
    /// <see cref="TabBarController.Reserve"/>. It must stay exactly equal to what the window gained, or the grid loses rows.
    /// </summary>
    public int StripReserve => _room == ChromeRoom.Grown ? _roomPx : 0;

    /// <summary>How far out the floating chrome is: 0 tucked above the window's top edge, 1 in place.</summary>
    private float Out(long now) => Floats ? _fade.Value(now) : 1;

    /// <summary>How far the chrome is drawn above the strip it belongs in — the whole strip, at 0.</summary>
    private float Slide(long now) => Floats ? -(1 - Out(now)) * PanelHeight() : 0;

    /// <summary>
    /// Coming in takes a little longer than going away, so the bar eases into place and leaves without lingering.
    /// The pushed panes and the tab bar are given the same duration, and the same curve, so all three move as one.
    /// </summary>
    private float InMs => Theme.AnimationMs * 1.7f;

    private float OutMs => Theme.AnimationMs * 1.3f;

    /// <summary>
    /// How far the bar's contents still have to settle, on top of the panel's slide: they ride in with the panel and
    /// drop the last few pixels into place just behind it, so the bar lands softly rather than all at once. 0 when
    /// the chrome is fully out or tucked away.
    /// </summary>
    private float ContentSettle(long now) => -(1 - ContentOut(now)) * Theme.S(6);

    /// <summary>The contents' own progress: they start once the panel is a third of the way out.</summary>
    private float ContentOut(long now) => Floats ? Math.Clamp((Out(now) - 0.35f) / 0.65f, 0, 1) : 1;

    /// <summary>Where the bar's contents are drawn: the panel's slide plus their own settle.</summary>
    private float ContentSlide(long now) => Slide(now) + ContentSettle(now);

    /// <summary>The reveal and its buttons are moving (the window's IsAnimating).</summary>
    public bool IsAnimating(long now) => _fade.IsAnimating(now) || _controls.IsAnimating(now);

    private void ToggleMaximize()
    {
        nint window = _window.SdlWindow;
        if ((Sdl.GetWindowFlags(window) & Sdl.WINDOW_MAXIMIZED) != 0) Sdl.RestoreWindow(window);
        else Sdl.MaximizeWindow(window);
    }

    // ---- Double-click on the bar ------------------------------------------------------------

    /// <summary>
    /// A double click on the bar maximizes and restores, the way a title bar's does — and the bar is standing in
    /// for one. It has to be taken from the raw message stream: the empty strip is given to the system as a
    /// caption region so the window can be dragged by it (<see cref="WindowHitTest"/>), and SDL turns the click there
    /// into a window drag of its own, so neither Windows' own maximize nor an ordinary mouse event ever arrives.
    /// </summary>
    public void InstallCaptionHook()
    {
        if (!OperatingSystem.IsWindows()) return;
        nint hwnd = Sdl.Hwnd(_window.SdlWindow);
        if (hwnd == 0) return;
        CaptionHook.Add(hwnd, this);
    }

    public void ReleaseCaptionHook()
    {
        nint window = _window.SdlWindow;
        nint hwnd = window != 0 ? Sdl.Hwnd(window) : 0;
        if (hwnd != 0) CaptionHook.Remove(hwnd);
    }

    /// <summary>From the message hook, inside SDL's message pump: only notes the click (<see cref="TickCaptionClick"/> acts on it).</summary>
    public void NoteCaptionDoubleClick() => _captionDoubleClick = true;

    /// <summary>Hook from the chrome's tick: act on a double click the message hook saw, off the message pump.</summary>
    private void TickCaptionClick()
    {
        if (!_captionDoubleClick) return;
        _captionDoubleClick = false;
        ToggleMaximize();
        _window.Redraw();
    }

    /// <summary>
    /// The band that reveals the chrome: the strip it will occupy, plus a margin of the content below it so the
    /// pointer brings it out before it has to land on it.
    /// <para>
    /// The room already taken is added back, and that is load-bearing rather than generosity. Growing the window
    /// upwards slides every point of it down by the strip's height in window coordinates, so the pointer that
    /// asked for the chrome would land outside a fixed band the instant it arrived — hide, shrink, reveal, grow,
    /// for ever. Widening the band by exactly what was taken makes the test read the same before and after.
    /// </para>
    /// </summary>
    private float RevealBand() => RoomHeight() + Theme.S(10) + _roomPx;

    /// <summary>
    /// Where the pointer really is, in this window's pixels, asked of the system rather than taken from the event
    /// stream — null when the window's position is unknown.
    /// <para>
    /// Needed because SDL reports no motion over anything the hit test claims (<see cref="WindowHitTest"/>): the resize
    /// border along the top edge and the empty strip of the tab bar that drags the window are non-client area to
    /// the compositor, so arriving on either of them reads as the pointer leaving the window. Both lie inside the
    /// reveal band, so believing that would put the chrome away at the exact moment the pointer reached it.
    /// </para>
    /// </summary>
#pragma warning disable S6640 // Native interop: SDL returns the global mouse and window position through pointers.
    private unsafe (float X, float Y)? PointerInWindow()
#pragma warning restore S6640
    {
        float gx, gy;
        Sdl.GetGlobalMouseState(&gx, &gy);
        int wx, wy;
        if (!Sdl.GetWindowPosition(_window.SdlWindow, &wx, &wy)) return null;
        float density = _window.Density;
        return ((gx - wx) * density, (gy - wy) * density);
    }

    /// <summary>
    /// Re-evaluates whether the floating chrome should be out, from the pointer and from whatever is mid-gesture:
    /// a tab being dragged or renamed, or an open menu, holds it out wherever the pointer went.
    /// </summary>
    public void UpdateReveal(float x, float y, bool pointerInside, long now)
    {
        if (!Floats)
        {
            _edgeHold = false;
            if (!_revealed) return;
            _revealed = false;
            _fade.Snap(0);
            TickRoom(now); // nothing left to slide: hand the strip straight back
            return;
        }
        bool sticky = _window.ChromeHeldOut;
        bool asked = false;
        // "The pointer left" may only mean it reached the chrome itself; ask where it is before believing it.
        if (!pointerInside && !sticky && PointerInWindow() is { } p) (x, y, pointerInside, asked) = (p.X, p.Y, true, true);
        var (w, _) = _window.WindowPixels();
        bool inBand = pointerInside && WindowControlsLayout.InRevealBand(w, RevealBand(), x, y);
        // Out already: it stays while the pointer is in the band. Not yet: the pointer has to stop there first.
        bool reveal = sticky || (inBand && (_revealed || Rested(x, y, now)));
        if (!inBand || reveal) _armedAt = 0; // left the band, or it is out: nothing is waiting any more
        _edgeHold = reveal && asked; // only this poll will notice when the pointer moves on
        SetRevealed(reveal, now);
    }

    /// <summary>The pointer left the window: the caption buttons lose their hover and the chrome goes back in.</summary>
    public void MouseLeave(long now)
    {
        _controls.HandleMouseLeave(now);
        UpdateReveal(-1, -1, pointerInside: false, now);
    }

    /// <summary>
    /// Whether the pointer has rested in the reveal band long enough to mean it. Moving more than a few pixels starts
    /// the wait again, so sliding along the top or passing through on the way out of the window doesn't count.
    /// </summary>
    private bool Rested(float x, float y, long now)
    {
        float slack = Theme.S(12);
        if (_armedAt == 0 || MathF.Abs(x - _armX) > slack || MathF.Abs(y - _armY) > slack)
        {
            (_armedAt, _armX, _armY) = (now, x, y);
            return false;
        }
        return now - _armedAt >= DwellMs;
    }

    /// <summary>
    /// Hook from the chrome's tick: a pointer that stopped in the band sends no more events, so look again once it has rested,
    /// at the place it stopped. Had it moved on, that move would have restarted or cancelled the wait already.
    /// </summary>
    private void TickArm(long now)
    {
        if (_armedAt == 0 || _revealed || now - _armedAt < DwellMs) return;
        UpdateReveal(_armX, _armY, pointerInside: true, now);
    }

    /// <summary>When the event loop has to wake for <see cref="TickArm"/>; int.MaxValue when nothing is waiting.</summary>
    public int ArmWakeMs(long now) =>
        _armedAt == 0 || _revealed ? int.MaxValue : (int)Math.Max(1, _armedAt + DwellMs - now);

    /// <summary>
    /// Hook from the chrome's tick, while the chrome is out and SDL has stopped reporting the pointer: it is sitting on the
    /// bar or the resize border, which are non-client area (see <see cref="PointerInWindow"/>), so no event will say
    /// it moved on. Look for ourselves, a few times a second, until it has.
    /// </summary>
    private void PollEdge(long now)
    {
        if (RevealPolling) UpdateReveal(-1, -1, pointerInside: false, now);
    }

    /// <summary>Whether the pointer has to be polled this frame (and the event loop kept awake for it).</summary>
    public bool RevealPolling => Floats && _edgeHold;

    /// <summary>The chrome's per-tick work: the pointer parked on it, the dwell at the top, the strip handed back, the caption double click.</summary>
    public void Tick(long now)
    {
        PollEdge(now); // the pointer parked on the chrome, where SDL reports nothing
        TickArm(now); // the pointer stopped at the top: bring the window bar in
        TickRoom(now); // the chrome has finished sliding away: give the window's strip back
        TickCaptionClick(); // a double click on the bar, taken off the raw message stream
    }

    /// <summary>
    /// Brings the chrome out for something other than the pointer — an inline rename started from the command
    /// palette, where the bar has to be on screen to be typed into. Polling puts it away again afterwards.
    /// </summary>
    public void HoldOut(long now)
    {
        if (Floats) SetRevealed(true, now);
    }

    /// <summary>
    /// Hook from the chrome's draw, under the bar and the caption buttons: the surface they ride in on. It fills the strip
    /// the window made for them and nothing else, so it is opaque and casts a shadow — there is no text under it to
    /// show through, only the gap the panes were never in.
    /// </summary>
    public void DrawPanel(DrawList dl, long now)
    {
        if (!Floats || Out(now) <= 0.001f) return;
        var theme = Theme;
        var (w, _) = _window.WindowPixels();
        var panel = new RectF(0, Slide(now), w, PanelHeight());
        float line = MathF.Max(1, theme.S(1));
        dl.Shadow(panel, 0, theme.S(10), 0.30f);
        dl.Rect(panel, theme.PanelBg, 1);
        dl.Rect(new RectF(0, panel.Bottom - line, w, line), theme.Border, 0.9f);
    }

    private void SetRevealed(bool reveal, long now)
    {
        if (reveal == _revealed) return;
        _revealed = reveal;
        _fade.Set(reveal ? 1 : 0, now, reveal ? InMs : OutMs);
        // Pushed panes go back up with the bar, in the same motion. Only a grown window waits for the slide to end.
        if (!reveal && _room == ChromeRoom.Lifted) ReleaseRoom();
        // Coming out, the room is taken first and the chrome slides down into it. Going away, the room is given
        // back only once the slide has finished (TickRoom), so the window changes size once per gesture
        // rather than once per frame — a window that resizes at 60 Hz judders on Windows.
        if (reveal) TakeRoom();
        _window.Redraw();
    }

    // ---- The strip the chrome lives in ------------------------------------------------------

    /// <summary>
    /// Takes the room the chrome needs, preferring to grow the window upwards so the panes never move. Whichever
    /// way it goes, the panes keep their pixel size and so their rows and columns: nothing reaches the PTY.
    /// </summary>
    private void TakeRoom()
    {
        if (_room != ChromeRoom.None || !Floats) return;
        int room = (int)MathF.Round(RoomHeight());
        if (room <= 0) return;
        _room = ChromeRoom.Grown; // set before the window changes: the resize event must see the reserve
        _roomPx = room;
        if (Settings.ChromeReveal == ChromeRevealMode.Push || !TryGrowWindow(room))
        {
            _room = ChromeRoom.Lifted;
            _roomPx = room;
        }
        // Grown: the panes are already where they belong, so no animation — there is nothing to animate.
        _window.ApplyLayout(animate: _room == ChromeRoom.Lifted, durationMs: InMs);
    }

    /// <summary>Hook from the chrome's tick: the chrome has finished sliding away, so the window can have its strip back.</summary>
    private void TickRoom(long now)
    {
        if (_room == ChromeRoom.None || _revealed || _fade.IsAnimating(now)) return;
        ReleaseRoom();
    }

    /// <summary>Gives the strip back: the window shrinks if it grew, the panes slide back up if they were pushed.</summary>
    private void ReleaseRoom()
    {
        if (_room == ChromeRoom.None) return;
        bool grown = _room == ChromeRoom.Grown;
        int room = _roomPx;
        _room = ChromeRoom.None;
        _roomPx = 0;
        if (grown) ShrinkWindow(room);
        _window.ApplyLayout(animate: !grown, durationMs: OutMs);
        _window.Redraw();
    }

    /// <summary>
    /// Moves the window up by <paramref name="roomPx"/> and makes it that much taller, so the strip appears above
    /// everything that was on screen and nothing that was on screen moves.
    /// <para>
    /// False when the window cannot have it: maximized or full screen (its size is not ours to choose), already
    /// against the top of the display it is on (work area, not zero — a second screen above or left of the primary
    /// has negative coordinates), or a compositor that ignores where we ask to be, which is Wayland. Anything it
    /// did manage is put back before returning, so the caller only has to choose the other way.
    /// </para>
    /// </summary>
#pragma warning disable S6640 // Native interop: SDL returns the window geometry through pointers.
    private unsafe bool TryGrowWindow(int roomPx)
#pragma warning restore S6640
    {
        nint window = _window.SdlWindow;
        if ((Sdl.GetWindowFlags(window) & (Sdl.WINDOW_MAXIMIZED | Sdl.WINDOW_FULLSCREEN)) != 0) return false;
        int d = Math.Max(1, (int)MathF.Round(roomPx / MathF.Max(0.01f, _window.Density)));
        int x, y, w, h;
        if (!Sdl.GetWindowPosition(window, &x, &y) || !Sdl.GetWindowSize(window, &w, &h)) return false;
        var usable = default(Sdl.Rect);
        if (!Sdl.GetDisplayUsableBounds(Sdl.GetDisplayForWindow(window), &usable) || y - d < usable.Y) return false;

        var (_, before) = _window.WindowPixels();
        Sdl.SetWindowPosition(window, x, y - d);
        Sdl.SetWindowSize(window, w, h + d);
        int nx, ny;
        var (_, after) = _window.WindowPixels();
        bool moved = Sdl.GetWindowPosition(window, &nx, &ny) && ny == y - d;
        if (!moved || after - before <= 0)
        {
            Sdl.SetWindowPosition(window, x, y);
            Sdl.SetWindowSize(window, w, h);
            return false;
        }
        // What the window actually gained, not what was asked for: the reserve has to match it to the pixel.
        _roomPx = after - before;
        return true;
    }

#pragma warning disable S6640 // Native interop: SDL returns the window geometry through pointers.
    private unsafe void ShrinkWindow(int roomPx)
#pragma warning restore S6640
    {
        nint window = _window.SdlWindow;
        // Maximized meanwhile: its size stopped being ours, and restoring it is the user's business now.
        if ((Sdl.GetWindowFlags(window) & (Sdl.WINDOW_MAXIMIZED | Sdl.WINDOW_FULLSCREEN)) != 0) return;
        int d = Math.Max(1, (int)MathF.Round(roomPx / MathF.Max(0.01f, _window.Density)));
        int x, y, w, h;
        if (!Sdl.GetWindowPosition(window, &x, &y) || !Sdl.GetWindowSize(window, &w, &h)) return;
        Sdl.SetWindowPosition(window, x, y + d); // down first, so the panes never leave the place they are drawn in
        Sdl.SetWindowSize(window, w, Math.Max(1, h - d));
    }

    /// <summary>
    /// The strip, in window points, for whoever has to discount it — the session records the window's size once a
    /// second and would otherwise save it mid-hover and reopen a little taller every time (<see cref="WindowSession"/>).
    /// </summary>
    public int RoomPoints =>
        _room == ChromeRoom.Grown ? (int)MathF.Round(_roomPx / MathF.Max(0.01f, _window.Density)) : 0;

    /// <summary>Hook from the chrome mouse handlers: true when the pointer is on a caption button.</summary>
    public bool ControlsMouseMove(float x, float y, long now) =>
        ControlsShown && _revealed && _controls.HandleMouseMove(x, y, now);

    public bool ControlsMouseDown(byte button, float x, float y) =>
        ControlsShown && _revealed && _controls.HandleMouseDown(button, x, y);

    public bool ControlsMouseUp(float x, float y) =>
        ControlsShown && _controls.HandleMouseUp(x, y);

    public void DrawControls(DrawList dl, long now)
    {
        if (!ControlsShown) return;
        var theme = Theme;
        var (w, _) = _window.WindowPixels();
        var pad = _window.ScaledPadding();
        _controls.Maximized = (Sdl.GetWindowFlags(_window.SdlWindow) & Sdl.WINDOW_MAXIMIZED) != 0;
        // Laid out every frame, slide and all: the hit test asks where the buttons are, and while the chrome is
        // tucked away they have to answer "above the window" rather than with where they last were.
        _controls.Layout(theme, w, ContentSlide(now) + theme.S(2), pad.Right);
        if (Out(now) > 0.001f) _controls.Draw(dl, theme, now, ContentOut(now)); // they fade in as they settle
        DrawWindowBarTitle(dl, theme, pad, now);
    }

    /// <summary>
    /// The window bar's left side: erikterm's icon and name, where a title bar shows them. They arrive and settle
    /// with the caption buttons. The strip around them drags the window (<see cref="WindowHitTest"/>).
    /// </summary>
    private void DrawWindowBarTitle(DrawList dl, Theme theme, Padding pad, long now)
    {
        float alpha = ContentOut(now);
        if (!Floats || alpha <= 0.001f) return;
        float top = ContentSlide(now) + theme.S(2), height = TabBar.Height(theme);
        float x = pad.Left + theme.S(4);
        float size = MathF.Round(theme.S(16));
        if (AppIconTexture() is var icon && icon != 0)
        {
            float y = MathF.Round(top + (height - size) / 2);
            dl.Image(icon, new QuadCorners(x, y, x + size, y + size), new QuadUv(0, 0, 1, 1), 0xFFFFFF, alpha, Sdl.SCALEMODE_LINEAR);
            x += size + theme.S(8);
        }
        float px = theme.UiPx * 0.8f;
        dl.Text("Erik's Terminal", x, Widgets.Baseline(dl, px, top, height), px, theme.PanelFg, alpha * 0.85f);
    }

    /// <summary>The embedded window icon as a texture, made once; 0 when it can't be (the name is drawn alone).</summary>
#pragma warning disable S6640 // Native interop: SDL copies the icon's pixels from a pinned pointer.
    private unsafe nint AppIconTexture()
#pragma warning restore S6640
    {
        if (_appIconTried) return _appIcon;
        _appIconTried = true;
        if (WindowIcon.Load() is not { } image) return 0;
        nint texture = Sdl.CreateTexture(_window.Renderer, Sdl.PIXELFORMAT_RGBA32, Sdl.TEXTUREACCESS_STATIC, image.Width, image.Height);
        if (texture == 0) return 0;
        Sdl.SetTextureBlendMode(texture, Sdl.BLENDMODE_BLEND);
        fixed (byte* pixels = image.Rgba) Sdl.UpdateTexture(texture, null, pixels, image.Width * 4);
        _appIcon = texture; // freed with the renderer
        return texture;
    }

    /// <summary>
    /// Rounded corners and a hairline border, asked of the compositor (Native/WindowsDwm.cs). Only a borderless
    /// window gets them: a decorated one already wears the system's frame. The border follows the color scheme,
    /// so it reads as part of the theme rather than a stripe around it.
    /// </summary>
    public void ApplyFrame()
    {
        nint hwnd = Sdl.Hwnd(_window.SdlWindow);
        if (hwnd == 0) return;
        bool borderless = Overlay;
        WindowsDwm.SetRoundedCorners(hwnd, borderless);
        WindowsDwm.SetBorderColor(hwnd, borderless ? Theme.Border : null);
    }
}
