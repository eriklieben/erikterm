namespace ErikTerm.App.Host;

/// <summary>
/// Which windows draw a frame on a pass of the event loop, and how soon they need the next one. Only the focused window
/// waits for vsync: a window on another workspace may get no frame callback from the compositor, and waiting for it
/// here would hold up the keys typed in the visible one. The others present without waiting, at most every
/// <see cref="UnpacedFrameMs"/>. A frame that throws is dropped; the window and the process carry on (<see cref="WindowErrors"/>).
/// </summary>
internal sealed class FramePacer(ErikTerm.Ui.WindowSet<TermWindow, Tab> windows, WindowErrors errors, TabTearOff tearOff)
{
    private const int UnpacedFrameMs = 16;

    private bool[] _animating = new bool[4];
    private bool _pacedLastFrame;
    private bool _rendering;

    /// <summary>When an unpaced window's next frame is due (it skipped this pass); int.MaxValue when none waits.</summary>
    public int UnpacedWakeMs { get; private set; } = int.MaxValue;

    /// <summary>While animating, how long the loop may sleep: no vsync wait paces it when no window waited for one.</summary>
    public int AnimatingWaitMs => _pacedLastFrame ? 0 : UnpacedFrameMs / 2;

    /// <summary>
    /// Before the loop sleeps: notes which of the first <paramref name="count"/> windows animate (the frame pass uses
    /// that) and returns the soonest wake-up any of them asks for.
    /// </summary>
    public int ScanWindows(int count, long now, out bool animating, out bool sync, out bool screenshot)
    {
        if (_animating.Length < count) _animating = new bool[count * 2];
        animating = false;
        sync = false;
        screenshot = false;
        int timeout = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var w = windows[i];
            _animating[i] = w.IsAnimating(now);
            animating |= _animating[i];
            sync |= w.WaitsForSynchronizedOutput;
            screenshot |= w._screenshotPath is not null;
            timeout = Math.Min(timeout, WindowWakeMs(w, now));
        }
        return timeout;
    }

    private static int WindowWakeMs(TermWindow w, long now)
    {
        int timeout = Math.Min(w.ChromeWakeMs(now), w.PendingKeyWakeMs(now));
        timeout = Math.Min(timeout, w.FloodFrameWakeMs(now)); // Rendering/FramePacing.cs
        timeout = Math.Min(timeout, w.ScrollbarWakeMs(now)); // Rendering/FramePacing.cs
        timeout = Math.Min(timeout, w.AgentWakeMs(now)); // agent polls / working pulse (Agents/WindowAgents.cs)
        timeout = Math.Min(timeout, w.AgentResumeWakeMs(now)); // the "Resume N agents" pill (Agents/WindowAgentResume.cs)
        timeout = Math.Min(timeout, w.ProgressWakeMs(now)); // a gliding indeterminate progress bar (Windowing/WindowProgress.cs)
        timeout = Math.Min(timeout, w.AccessibilityWakeMs(now)); // screen-reader announcements (Accessibility/WindowAccessibility.cs)
        timeout = Math.Min(timeout, w.NotificationWakeMs(now)); // bar auto-hide, "N min ago" (Notifications/WindowNotifications.cs)
        return Math.Min(timeout, w.IntegrationWakeMs(now)); // git/gh results, repo pill, CI spinner, drag (WindowFeatures/WindowIntegrations.cs)
    }

    /// <summary>A held frame has a deadline to wake the loop, so the reader needn't signal it for every chunk meanwhile.</summary>
    public bool AnyWindowHoldsFloodFrame()
    {
        long now = Environment.TickCount64;
        for (int i = 0; i < windows.Count; i++)
            if (windows[i].HoldsFloodFrame(now)) return true;
        return false;
    }

    /// <summary>Draws every window (of the first <paramref name="counted"/>, those the scan saw) that needs a frame.</summary>
    public void RenderWindows(int counted)
    {
        long now = Environment.TickCount64;
        int n = Math.Min(counted, windows.Count), paced = -1;
        var focused = windows.Focused;
        for (int i = 0; i < n; i++)
        {
            var w = windows[i];
            w.ReportFrameStats(now);
            w.CheckScreenshot(now);
            if (!w.WantsFrame(_animating[i])) continue;
            if (ReferenceEquals(w, focused)) paced = i;
        }
        tearOff.DrawGhost(); // the dragged tab's popup outside its window; never waits
        _pacedLastFrame = paced >= 0;
        UnpacedWakeMs = int.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (i == paced) continue;
            var w = windows[i];
            if (!w.WantsFrame(_animating[i])) continue;
            if (!w.UnpacedFrameDue(now, UnpacedFrameMs))
            {
                UnpacedWakeMs = UnpacedFrameMs / 2;
                continue;
            }
            w.SetVSync(false);
            Render(w, _animating[i]);
        }
        if (paced < 0) return;
        windows[paced].SetVSync(true);
        Render(windows[paced], _animating[paced]);
    }

    /// <summary>
    /// One frame. Every frame goes through here, which is what lets the resize event watch tell whether one is already
    /// being built (<see cref="LiveResizeWatch"/>): presenting can pump messages and re-enter the watch, and a
    /// re-entrant frame would reset the draw list under the one already in it.
    /// </summary>
    public void Render(TermWindow w, bool animating)
    {
        if (_rendering) return;
        _rendering = true;
        try { w.Render(animating); }
        catch (Exception ex) when (!WindowErrors.IsFatal(ex)) { errors.Report(w, "frame", ex); }
        finally { _rendering = false; }
    }

    /// <summary>A frame is being built (by the loop or by the resize watch).</summary>
    public bool Rendering => _rendering;
}
