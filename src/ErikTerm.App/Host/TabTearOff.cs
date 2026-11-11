using ErikTerm.App.Native;
using ErikTerm.App.Ui;

namespace ErikTerm.App.Host;

/// <summary>What <see cref="TabTearOff"/> needs from the process besides its windows.</summary>
internal interface ITearOffOwner : IWindowOwner
{
    /// <summary>A window that isn't open yet, for <see cref="IWindowOwner.CreateWindow"/>'s <c>prepared</c>.</summary>
    TermWindow NewWindow();
    /// <summary>A tab moved into <paramref name="w"/>: the debug script's next steps go there.</summary>
    void RetargetScript(TermWindow w);
}

/// <summary>
/// Moving tabs between windows: "Move Tab to New Window" / "Move Tab to Window N" (palette, tab context menu,
/// MoveTabToNewWindow / MoveTabToWindow(N) actions) and dragging a tab out of the tab bar. The live panes move —
/// same PTYs, scrollback and state; nothing restarts.
///
/// Dragging: past a threshold away from the bar the tab detaches (TabBar). A ghost follows the pointer — drawn by the
/// source window's TabBar inside the window, and by a tooltip popup of that window outside it (TabGhost). Letting go
/// over another window's tab bar puts the tab there (where the insertion marker showed); anywhere else it becomes a
/// window of its own, at the drop position where the platform allows placing windows.
///
/// Wayland: while the button is held the pointer stays with the source window (implicit grab), so we get its position
/// relative to that window even outside it, but not where our other windows are — no insertion marker on them during the
/// drag. On release outside the source window the decision waits up to <see cref="DropWaitMs"/> ms for the pointer
/// to enter one of our windows (the compositor sends that right after the grab ends): over its tab bar the tab goes
/// there, elsewhere (or nowhere) it gets a new window, which the compositor places (Hyprland tiles it).
/// </summary>
#pragma warning disable S6640 // Native interop: SDL returns the mouse position through pointers.
internal sealed unsafe class TabTearOff(ITearOffOwner owner)
#pragma warning restore S6640
{
    private const int DropWaitMs = 250;

    private TermWindow? _dropHover;          // window showing an insertion marker (not on Wayland)
    private PendingDrop? _pendingDrop;
    private TabGhost? _ghost;
    private TermWindow? _ghostOwner;
    private (string Number, string Title) _ghostLabel;
    private bool _ghostDirty;
    private (uint Window, float X, float Y)? _scriptDropOn;

    private sealed record PendingDrop(TermWindow From, Tab Tab, long Deadline);

    private static long Now => Environment.TickCount64;

    private ErikTerm.Ui.WindowSet<TermWindow, Tab> Windows => owner.Windows;

    /// <summary>The compositor places windows and doesn't tell their positions (no global pointer between windows). Set once SDL is up.</summary>
    public bool IsWayland { get; set; }

    // ==== Moving ===============================================================================

    public bool CanMoveTab(Tab tab, TermWindow? to) => Windows.CanMove(tab, to);

    /// <summary>TabBar.CanDetach: there is somewhere to go — another window, or a new one (more than one tab here).</summary>
    public bool CanDetachTab(TermWindow from) => Windows.Count > 1 || from.Tabs.Count > 1;

    /// <summary>Moves a tab (and its live panes) into another window, at <paramref name="index"/> (negative = the end).</summary>
    public void MoveTab(Tab tab, TermWindow to, int index)
    {
        if (!Windows.CanMove(tab, to) || to.CloseRequested) return;
        var from = Windows.WindowOf(tab)!;
        bool barWasShown = from.PrepareTabLeave(tab); // Windowing/TabBarController.cs
        to.PrepareTabArrive();
        var move = Windows.MoveTab(tab, to, index);
        from.FinishTabLeave(move.WasActive, move.SourceActive, barWasShown); // an emptied window closes after this iteration
        to.FinishTabArrive(tab, move.TargetPrevious);
        Windows.Focus(to);
        owner.RetargetScript(to);
    }

    public void MoveTabToWindow(TermWindow from, Tab tab, int windowId)
    {
        if (windowId <= 0 || owner.WindowById((uint)windowId) is not { CloseRequested: false } to) { from.Toast(ToastKind.Info, $"No window {windowId}"); return; }
        if (ReferenceEquals(to, from)) return;
        MoveTab(tab, to, -1);
    }

    /// <summary>Tear-off: the tab gets a window of its own, the size of the one it left (at <paramref name="position"/>, desktop points, where allowed).</summary>
    public void MoveTabToNewWindow(TermWindow from, Tab tab, (int X, int Y)? position = null)
    {
        if (!Windows.CanMove(tab, null))
        {
            from.Toast(ToastKind.Info, "It's the only tab in this window", "Drag it onto another window's tab bar to move it there");
            return;
        }
        int oldIndex = from.Tabs.IndexOf(tab);
        var previousActive = from.Tabs.Active;
        bool barWasShown = from.PrepareTabLeave(tab);
        var w = owner.NewWindow();
        var move = Windows.MoveTabToNewWindow(tab, w, w.Tabs);
        try
        {
            owner.CreateWindow(new TermWindow.OpenOptions { Adopt = tab, Size = from.SizePoints, Position = position, Quiet = true }, w);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Windows.MoveTab(tab, from, oldIndex); // put it back where it was (and show it)
            Windows.Remove(w);
            from.FinishTabArrive(tab, previousActive);
            from.Toast(ToastKind.Error, "Couldn't open a new window", ex.Message);
            return;
        }
        from.FinishTabLeave(move.WasActive, move.SourceActive, barWasShown);
    }

    /// <summary>Hook from CloseWindow: forget drag state that involves the window.</summary>
    public void Cancel(TermWindow w)
    {
        if (ReferenceEquals(_pendingDrop?.From, w)) _pendingDrop = null;
        if (ReferenceEquals(_dropHover, w)) _dropHover = null;
        if (ReferenceEquals(_ghostOwner, w)) HideGhost();
    }

    // ==== Dragging =============================================================================

    /// <summary>TabBar.DragOutside: a detached tab moved (pointer in <paramref name="from"/>'s pixels, maybe outside it).</summary>
    public void OnTabDragged(TermWindow from, float x, float y)
    {
        UpdateGhost(from, x, y);
        if (IsWayland) return; // no positions for our other windows: the target is found after the drop
        var (target, lx, ly) = WindowUnder(from, x, y);
        SetDropHover(target, lx, ly);
    }

    /// <summary>TabBar.DropOutside: a detached tab was let go.</summary>
    public void OnTabDropped(TermWindow from, Tab tab, float x, float y)
    {
        HideGhost();
        SetDropHover(null, 0, 0);
        if (!IsWayland)
        {
            var (target, lx, ly) = WindowUnder(from, x, y);
            if (target is not null && target.DropIndexAt(lx, ly) is >= 0 and var index) { MoveTab(tab, target, index); return; }
            if (!Windows.CanMove(tab, null)) return;
            var (gx, gy) = DesktopPoint(from, x, y);
            var grab = from.TabGrabOffset;
            MoveTabToNewWindow(from, tab, ((int)(gx - grab.X / from.Scale), (int)(gy - grab.Y / from.Scale)));
            return;
        }
        // Wayland: over the source window itself we know; elsewhere wait for the pointer to show up in another window.
        if (from.ContainsPixel(x, y))
        {
            if (Windows.CanMove(tab, null)) MoveTabToNewWindow(from, tab);
            return;
        }
        _pendingDrop = new PendingDrop(from, tab, Now + DropWaitMs);
        if (_scriptDropOn is { } scripted) // debug script drop-on:
        {
            _scriptDropOn = null;
            ResolvePendingDrop(owner.WindowById(scripted.Window), scripted.X, scripted.Y);
        }
    }

    /// <summary>Debug script drop-on:ID,X,Y: the next drop that has to wait for the pointer lands on window ID at X,Y pixels.</summary>
    public void ScriptDropOn(uint window, float x, float y) => _scriptDropOn = (window, x, y);

    /// <summary>Dispatch hook: during a pending (Wayland) drop, the pointer entering another of our windows decides it.</summary>
    public void OnEvent(ref Sdl.Event ev)
    {
        if (_pendingDrop is not { } p) return;
        if (ev.Type is not (Sdl.EVENT_WINDOW_MOUSE_ENTER or Sdl.EVENT_MOUSE_MOTION)) return;
        if (owner.WindowById(ev.WindowID) is not { CloseRequested: false } w || ReferenceEquals(w, p.From)) return;
        float x, y;
        if (ev.Type == Sdl.EVENT_MOUSE_MOTION) (x, y) = (ev.MouseX, ev.MouseY);
        else Sdl.GetMouseState(&x, &y);
        ResolvePendingDrop(w, x * w.Scale, y * w.Scale);
    }

    /// <summary>Lands a pending drop: on <paramref name="over"/>'s tab bar at (x, y) (its pixels), else in a new window.</summary>
    private void ResolvePendingDrop(TermWindow? over, float x, float y)
    {
        if (_pendingDrop is not { } p) return;
        _pendingDrop = null;
        if (!Windows.Contains(p.From) || !ReferenceEquals(Windows.WindowOf(p.Tab), p.From)) return;
        if (over is not null && !ReferenceEquals(over, p.From) && over.DropIndexAt(x, y) is >= 0 and var index) MoveTab(p.Tab, over, index);
        else if (Windows.CanMove(p.Tab, null)) MoveTabToNewWindow(p.From, p.Tab);
    }

    /// <summary>Hook from the event loop: nobody claimed a pending drop in time — it's a new window.</summary>
    public void Tick(long now)
    {
        if (_pendingDrop is { } p && now >= p.Deadline) ResolvePendingDrop(null, 0, 0);
    }

    /// <summary>When a pending drop's wait ends.</summary>
    public int WakeMs(long now) =>
        _pendingDrop is { } p ? (int)Math.Clamp(p.Deadline - now, 0, int.MaxValue) : int.MaxValue;

    private void SetDropHover(TermWindow? target, float x, float y)
    {
        if (_dropHover is { } old && !ReferenceEquals(old, target)) old.HideDropTarget();
        _dropHover = target;
        target?.ShowDropTarget(x, y);
    }

    /// <summary>The pointer in desktop points (not Wayland): the window's position plus the window-relative pointer.</summary>
    private static (float X, float Y) DesktopPoint(TermWindow from, float x, float y)
    {
        var pos = from.Position ?? (0, 0);
        return (pos.X + x / from.Scale, pos.Y + y / from.Scale);
    }

    /// <summary>Our other window under the pointer and the pointer in its pixels (not Wayland).</summary>
    private (TermWindow? Window, float X, float Y) WindowUnder(TermWindow from, float x, float y)
    {
        var (gx, gy) = DesktopPoint(from, x, y);
        // Most recently focused first: that's the one on top where windows overlap.
        foreach (var w in Windows.ByRecency)
        {
            if (ReferenceEquals(w, from) || w.CloseRequested || w.Position is not { } pos) continue;
            var (ww, wh) = w.SizePoints;
            if (gx < pos.X || gy < pos.Y || gx >= pos.X + ww || gy >= pos.Y + wh) continue;
            return (w, (gx - pos.X) * w.Scale, (gy - pos.Y) * w.Scale);
        }
        return (null, 0, 0);
    }

    // ==== The ghost outside the window =========================================================

    /// <summary>Inside its window the TabBar draws the ghost; outside, a tooltip popup of the window follows the pointer.</summary>
    private void UpdateGhost(TermWindow from, float x, float y)
    {
        if (from.ContainsPixel(x, y) || from.TabGhostLabel is not { } label) { HideGhost(); return; }
        float scale = from.Scale;
        var grab = from.TabGrabOffset;
        int gx = (int)MathF.Round((x - grab.X) / scale) - TabGhost.MarginPoints, gy = (int)MathF.Round((y - grab.Y) / scale) - TabGhost.MarginPoints;
        if (_ghost is not null && !ReferenceEquals(_ghostOwner, from)) HideGhost();
        if (_ghost is null)
        {
            _ghost = from.CreateTabGhost(gx, gy); // null when popups aren't available: the in-window ghost is all there is
            _ghostOwner = from;
            _ghostDirty = true;
            if (_ghost is null) return;
        }
        else _ghost.Move(gx, gy);
        if (_ghostLabel != (label.Number, label.Title)) { _ghostLabel = (label.Number, label.Title); _ghostDirty = true; }
    }

    /// <summary>Hook from the frame pass: draws the popup when it changed; never waits.</summary>
    public void DrawGhost()
    {
        if (_ghost is null || !_ghostDirty || _ghostOwner is null) return;
        _ghostDirty = false;
        _ghostOwner.DrawTabGhost(_ghost, _ghostLabel.Number, _ghostLabel.Title);
    }

    /// <summary>Debug script shot: with a {window} in the path, the ghost popup is saved too.</summary>
    public void CaptureGhost(string path)
    {
        if (_ghost is null) return;
        _ghost.Capture(path);
        _ghostDirty = true;
    }

    public void HideGhost()
    {
        _ghost?.Dispose();
        _ghost = null;
        _ghostOwner = null;
        _ghostLabel = default;
    }
}
