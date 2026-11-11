using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ErikTerm.App.Native;
using ErikTerm.App.Ui;
using ErikTerm.App.Windowing;
using ErikTerm.Ui;

namespace ErikTerm.App.Chrome;

/// <summary>
/// Moving and resizing a window that draws its own chrome. With <c>window_decorations = "NONE"</c> there is no
/// system frame to grab, so SDL asks us which regions are special: the empty strip of the tab bar drags the
/// window, its border resizes it (see <see cref="WindowDragHitTest"/>).
/// <para>
/// Installed only while the window is borderless. A decorated window already gets both from its frame, and a hit
/// test would take them away — SDL answers every point the callback doesn't claim with HITTEST_NORMAL, so the
/// frame's own resize borders would stop working. The callback is also how the platforms differ: SDL maps it to
/// WM_NCHITTEST on Windows, _NET_WM_MOVERESIZE on X11, xdg_toplevel move/resize on Wayland and window dragging on
/// macOS, and returns false where it has no implementation, which only costs us the feature.
/// </para>
/// </summary>
#pragma warning disable S6640 // Native interop: the hit test is an unmanaged SDL callback that receives pointers.
internal sealed unsafe class WindowHitTest(IWindowSurface window, FloatingChrome chrome, TabBar tabBar)
#pragma warning restore S6640
{
    /// <summary>Keeps this object reachable from the unmanaged callback; freed with the window.</summary>
    private GCHandle _handle;

    /// <summary>Grab border, in points — matched to what a system frame offers.</summary>
    private const float ResizeMargin = 5;

    /// <summary>Installs or removes the hit test to match window_decorations and the platform (called at startup and on reload).</summary>
    public void Apply()
    {
        bool want = chrome.Overlay; // borderless on Windows / macOS (FloatingChrome)
        nint sdlWindow = window.SdlWindow;
        if (sdlWindow == 0 || want == _handle.IsAllocated) return;
        if (want)
        {
            var handle = GCHandle.Alloc(this);
            if (Sdl.SetWindowHitTest(sdlWindow, &Callback, GCHandle.ToIntPtr(handle))) _handle = handle;
            else handle.Free(); // unsupported platform: the window stays put, everything else works
            // Clicks on the regions we just gave away stop arriving as mouse events (FloatingChrome).
            chrome.InstallCaptionHook();
        }
        else
        {
            Sdl.SetWindowHitTest(sdlWindow, null, 0);
            _handle.Free();
            _handle = default;
            chrome.ReleaseCaptionHook();
        }
    }

    public void Release()
    {
        chrome.ReleaseCaptionHook();
        if (!_handle.IsAllocated) return;
        nint sdlWindow = window.SdlWindow;
        if (sdlWindow != 0) Sdl.SetWindowHitTest(sdlWindow, null, 0);
        _handle.Free();
        _handle = default;
    }

    /// <summary>
    /// SDL's hit-test entry point. It fires during event processing, at times unrelated to where the pointer
    /// actually is, so it stays allocation-free and never lets an exception reach native code.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Callback(nint sdlWindow, Sdl.Point* area, nint data)
    {
        try
        {
            if (area is null || GCHandle.FromIntPtr(data).Target is not WindowHitTest self) return Sdl.HITTEST_NORMAL;
            return self.HitTest(area->X, area->Y);
        }
        catch
        {
            return Sdl.HITTEST_NORMAL;
        }
    }

    /// <summary>The area arrives in window points; the tab bar is laid out in pixels, so scale before asking it.</summary>
    private int HitTest(int pointX, int pointY)
    {
        var (w, h) = window.WindowPixels();
        float density = window.Density;
        float x = pointX * density, y = pointY * density;
        // The caption buttons sit in the same strip: they take their clicks, they don't drag the window.
        bool chromeArea = chrome.InWindowBar(x, y) || (tabBar.IsEmptyArea(x, y) && !(chrome.ControlsShown && chrome.ControlsContain(x, y)));
        var grab = WindowDragHitTest.Classify(w, h, MathF.Max(1, ResizeMargin * window.Scale), x, y, chromeArea);
        return grab switch
        {
            WindowGrab.Move => Sdl.HITTEST_DRAGGABLE,
            WindowGrab.ResizeTopLeft => Sdl.HITTEST_RESIZE_TOPLEFT,
            WindowGrab.ResizeTop => Sdl.HITTEST_RESIZE_TOP,
            WindowGrab.ResizeTopRight => Sdl.HITTEST_RESIZE_TOPRIGHT,
            WindowGrab.ResizeRight => Sdl.HITTEST_RESIZE_RIGHT,
            WindowGrab.ResizeBottomRight => Sdl.HITTEST_RESIZE_BOTTOMRIGHT,
            WindowGrab.ResizeBottom => Sdl.HITTEST_RESIZE_BOTTOM,
            WindowGrab.ResizeBottomLeft => Sdl.HITTEST_RESIZE_BOTTOMLEFT,
            WindowGrab.ResizeLeft => Sdl.HITTEST_RESIZE_LEFT,
            _ => Sdl.HITTEST_NORMAL,
        };
    }
}
