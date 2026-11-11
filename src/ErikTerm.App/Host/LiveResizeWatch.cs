using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ErikTerm.App.Native;

namespace ErikTerm.App.Host;

/// <summary>
/// Smooth resizing on Windows. Dragging a window edge (or the title bar) puts Windows into its own modal message
/// loop inside <c>DefWindowProc</c>: it does not return until the mouse button comes up, so the event loop is
/// blocked for the whole drag and <see cref="FramePacer.RenderWindows"/> never runs. The window then shows one stale
/// frame stretched over the new size until the drag ends.
/// <para>
/// SDL's answer is <c>SDL_AddEventWatch</c>: a watch is called the moment an event is created, straight from SDL's
/// window procedure, so it runs <em>inside</em> that modal loop — the one place we get control there. Drawing the
/// window from the watch on resize/expose keeps the content live for the whole drag.
/// </para>
/// <para>
/// Windows only. Linux is the primary platform and does not have the problem: neither Wayland nor X11 resizes in a
/// modal loop, the configure/resize events arrive in the normal queue, and adding a second path that draws frames
/// there is risk without a benefit. macOS <em>does</em> run a modal loop while resizing and would likely want the
/// same, but nobody can test it here, so it is left out rather than guessed at.
/// </para>
/// <para>
/// Three things make this safe. The watch is called on whatever thread creates the event, and PTY reader threads
/// create one every time they wake the loop after feeding output, so anything but the main thread returns at
/// once — SDL renderers are main-thread only. A frame may be built only when no other frame is being built:
/// <see cref="FramePacer.Rendering"/> guards every render, because presenting a frame can itself pump messages and
/// re-enter the watch, and a re-entrant <see cref="TermWindow.Render"/> would reset the draw list underneath the frame
/// already in it. And the work done here is deliberately only "refit the grid, draw": no <see cref="TermWindow.Tick"/>,
/// no config reload, no IPC, so the watch cannot block the drag on anything slow. The lock is taken the same way a
/// normal frame takes it (reader threads hold it only to feed, never across a wake), so there is nothing new to
/// deadlock against.
/// </para>
/// </summary>
#pragma warning disable S6640 // Native interop: registers a function pointer with SDL (AddEventWatch) and reads native SDL event structs.
internal sealed unsafe class LiveResizeWatch(IWindowOwner owner, FramePacer pacer)
#pragma warning restore S6640
{
    /// <summary>This watch, for the static callback SDL calls (its userdata); 0 while not watching.</summary>
    private GCHandle _self;

    /// <summary>The thread that owns SDL. Events created on any other thread are not ours to draw from.</summary>
    private int _thread;

    /// <summary>Starts drawing from inside the platform's modal resize loop. No-op off Windows.</summary>
    public void Start()
    {
        if (!OperatingSystem.IsWindows()) return;
        _thread = Environment.CurrentManagedThreadId;
        _self = GCHandle.Alloc(this);
        if (!Sdl.AddEventWatch(&OnWatchedEvent, GCHandle.ToIntPtr(_self)))
            Console.Error.WriteLine($"erikterm: no live resize: SDL_AddEventWatch: {Sdl.GetError()}");
    }

    /// <summary>Removes the watch before the windows go, so SDL cannot call back into a half-torn-down process.</summary>
    public void Stop()
    {
        if (!OperatingSystem.IsWindows() || !_self.IsAllocated) return;
        Sdl.RemoveEventWatch(&OnWatchedEvent, GCHandle.ToIntPtr(_self));
        _self.Free();
    }

    /// <summary>
    /// SDL_EventFilter, called for every event as it is created — here only to catch the resizes and exposes that
    /// arrive while the event loop is blocked. Always returns true: a watch must not swallow the event, which is
    /// still queued and handled normally afterwards.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnWatchedEvent(nint userdata, Sdl.Event* ev)
    {
        if (ev->Type is not (Sdl.EVENT_WINDOW_RESIZED or Sdl.EVENT_WINDOW_PIXEL_SIZE_CHANGED or Sdl.EVENT_WINDOW_EXPOSED))
            return 1;
        if (userdata == 0 || GCHandle.FromIntPtr(userdata).Target is not LiveResizeWatch watch || Environment.CurrentManagedThreadId != watch._thread) return 1;
        // Nothing may escape into SDL's window procedure: an exception thrown through native code would be fatal,
        // and this frame is the one we can most afford to lose (the loop draws the window again straight after).
        try { watch.DrawWhileBlocked(ev->Type, ev->WindowID); }
        catch (Exception ex) when (!WindowErrors.IsFatal(ex)) { /* a lost frame: the loop draws the window again straight after */ }
        return 1;
    }

    /// <summary>One frame for a window being resized, from inside the modal loop.</summary>
    private void DrawWhileBlocked(uint type, uint windowId)
    {
        if (pacer.Rendering || owner.Quitting) return; // a frame is already being built: see the class remarks
        if (owner.WindowById(windowId) is not { CloseRequested: false } w) return;
        // The event has not been dequeued yet, so Handle has not refitted the grid for the new size: do it here.
        // Doing it again when the queued event is handled is harmless — ApplyLayout recomputes from the window size
        // and SizeTerminal only resizes a terminal whose columns or rows actually changed.
        if (type != Sdl.EVENT_WINDOW_EXPOSED) w.ResizeToWindow();
        else w.RequestRedraw(); // the backbuffer was resized under us; draw even if nothing is dirty
        pacer.Render(w, animating: false);
    }
}
