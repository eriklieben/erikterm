namespace ErikTerm.App.Host;

/// <summary>What <see cref="DebugScript"/> needs from the process besides its windows.</summary>
internal interface IDebugScriptOwner : IWindowOwner
{
    /// <summary>SpawnWindow: a new window (toasts in <paramref name="from"/> when it can't).</summary>
    TermWindow? SpawnWindow(string? cwd, TermWindow? from, string[]? command = null);
    void CloseWindow(TermWindow w);
}

/// <summary>
/// Debug and screenshot steps for tests and the demos. ERIKTERM_DEBUG_SCRIPT="action:SplitHorizontal;wait;type:spl;key:down"
/// (or ERIKTERM_DEBUG_SCRIPT_FILE, one step per line) runs one step every 350 ms (ERIKTERM_SCRIPT_STEP_MS); steps go to
/// <see cref="Target"/> (the newest window, or the one picked with window:ID). ERIKTERM_SCREENSHOT=/path/frame.bmp (a
/// {window} in it: one file per window) saves a frame ERIKTERM_SCREENSHOT_DELAY ms after each window opens; without
/// {window} only the first window is captured.
/// </summary>
internal sealed class DebugScript(IDebugScriptOwner owner, TabTearOff tearOff)
{
    private const string WindowPlaceholder = "{window}";

    private static readonly int ScriptStepMs = int.TryParse(Environment.GetEnvironmentVariable("ERIKTERM_SCRIPT_STEP_MS"), out int sm) ? sm : 350;

    private readonly string? _screenshotTemplate = Environment.GetEnvironmentVariable("ERIKTERM_SCREENSHOT");
    private readonly int _screenshotDelay =
        int.TryParse(Environment.GetEnvironmentVariable("ERIKTERM_SCREENSHOT_DELAY"), out int d) ? d : 1500;
    private bool _screenshotAssigned;

    private readonly Queue<string> _script = new(
        (Environment.GetEnvironmentVariable("ERIKTERM_DEBUG_SCRIPT") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    private long _nextAt = Environment.TickCount64 + 700;

    private ErikTerm.Ui.WindowSet<TermWindow, Tab> Windows => owner.Windows;

    /// <summary>The window the next steps go to.</summary>
    public TermWindow? Target { get; set; }

    /// <summary>Steps are queued: the loop wakes for them.</summary>
    public bool HasSteps => _script.Count > 0;

    /// <summary>The loop's longest sleep while steps are queued.</summary>
    public static int WaitMs => Math.Min(30, ScriptStepMs);

    /// <summary>ERIKTERM_DEBUG_SCRIPT_FILE: the same steps as ERIKTERM_DEBUG_SCRIPT, one per line.</summary>
    public void LoadFile()
    {
        if (Environment.GetEnvironmentVariable("ERIKTERM_DEBUG_SCRIPT_FILE") is not { Length: > 0 } path || !File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
            if (line.Trim() is { Length: > 0 } step && !step.StartsWith('#')) _script.Enqueue(step);
    }

    /// <summary>A window opened: it gets the ERIKTERM_SCREENSHOT capture (when that's for it) and the next steps.</summary>
    public void OnWindowOpened(TermWindow w)
    {
        if (_screenshotTemplate is { } template && (template.Contains(WindowPlaceholder) || !_screenshotAssigned))
        {
            _screenshotAssigned = true;
            w._screenshotPath = template.Replace(WindowPlaceholder, w.Id.ToString());
            w._screenshotAt = Environment.TickCount64 + _screenshotDelay;
        }
        Target = w;
    }

    /// <summary>A window closed: when the steps went there, they go to the focused one now.</summary>
    public void OnWindowClosed(TermWindow w)
    {
        if (ReferenceEquals(Target, w)) Target = Windows.Focused;
    }

    /// <summary>
    /// Runs the next step. Window steps: window:ID (later steps go to that window) · new-window · close-window ·
    /// shot:PATH (a {window} in it: every window) · move-tab:ID (the active tab to window ID; 0 = new) ·
    /// drop-on:ID,X,Y (the next tear-off drop outside its window lands on window ID at X,Y pixels — what the pointer
    /// entering that window right after the release does on Wayland) · sleep:ms.
    /// Everything else goes to the window (TermWindow.RunScript).
    /// </summary>
    public void Run(long now)
    {
        if (_script.Count == 0 || now < _nextAt) return;
        _nextAt = now + ScriptStepMs;
        string step = _script.Dequeue();
        int colon = step.IndexOf(':');
        string kind = colon < 0 ? step : step[..colon], arg = colon < 0 ? "" : step[(colon + 1)..];
        var w = Target is { CloseRequested: false } sw && Windows.Contains(sw) ? sw : Windows.Focused;
        if (w is null) return;
        switch (kind)
        {
            case "sleep": _nextAt = now + (int.TryParse(arg, out int ms) ? ms : 350); break;
            case "window": PickWindow(arg); break;
            case "new-window": owner.SpawnWindow(null, w); break;
            case "close-window": CloseWindow(w); break;
            case "shot": Shot(arg, w); break;
            case "move-tab": MoveTab(w, arg); break;
            case "drop-on": DropOn(arg); break;
            default: w.RunScript(kind, arg, now); break;
        }
    }

    private void PickWindow(string arg)
    {
        if (uint.TryParse(arg, out uint id) && owner.WindowById(id) is { } picked) Target = picked;
        else Console.Error.WriteLine($"erikterm: script: no window {arg}");
    }

    /// <summary>Like the compositor's close: may ask first.</summary>
    private void CloseWindow(TermWindow w)
    {
        if (w.RequestCloseWindow()) owner.CloseWindow(w);
    }

    private void Shot(string arg, TermWindow w)
    {
        for (int i = 0; i < Windows.Count; i++)
            if (arg.Contains(WindowPlaceholder) || ReferenceEquals(Windows[i], w)) Windows[i].ScheduleScreenshot(arg.Replace(WindowPlaceholder, Windows[i].Id.ToString()));
        if (arg.Contains(WindowPlaceholder)) tearOff.CaptureGhost(arg.Replace(WindowPlaceholder, "ghost"));
    }

    private void MoveTab(TermWindow w, string arg)
    {
        if (w.Tabs.Active is not { } tab) return;
        if (arg is "0" or "") tearOff.MoveTabToNewWindow(w, tab);
        else tearOff.MoveTabToWindow(w, tab, int.TryParse(arg, out int to) ? to : -1);
    }

    private void DropOn(string arg)
    {
        var parts = arg.Split(',');
        if (parts.Length == 3 && uint.TryParse(parts[0], out uint over) && float.TryParse(parts[1], out float x) && float.TryParse(parts[2], out float y))
            tearOff.ScriptDropOn(over, x, y); // the next drop that has to wait for the pointer lands there
    }
}
