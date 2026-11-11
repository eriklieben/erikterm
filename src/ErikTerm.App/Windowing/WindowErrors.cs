using ErikTerm.App.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>An internal error in this window's event, tick or frame that the main loop survived (App.Errors.cs).</summary>
internal sealed class WindowErrors(IWindowHost window)
{
    private long _toastAt = long.MinValue / 2, _streakAt = long.MinValue / 2; // / 2: "now - it" mustn't overflow
    private int _streak;

    /// <summary>Debug script <c>throw:N</c>: the next N frames throw mid-frame, with the terminal lock held.</summary>
    public int DebugThrowFrames { get; set; }

    /// <summary>
    /// Says so (a toast at most every 10 s, so an error that repeats every frame doesn't flood the window). When errors
    /// keep coming (3 within 2 s), closes the overlays and popups: the likeliest culprits, and what the user can reopen.
    /// </summary>
    public void OnInternalError(string? logPath, long now)
    {
        _streak = now - _streakAt < 2000 ? _streak + 1 : 1;
        _streakAt = now;
        if (_streak == 3) window.CloseOverlaysAfterError(now);
        window.Redraw();
        if (now - _toastAt < 10_000) return;
        _toastAt = now;
        window.Toast(ToastKind.Error, "Internal error (logged)", logPath is not null ? Path.GetFileName(logPath) + " — About shows where" : "details on stderr");
    }

    /// <summary>Hook from Render while <see cref="DebugThrowFrames"/> is set.</summary>
    public void DebugThrow()
    {
        DebugThrowFrames--;
        throw new InvalidOperationException("debug script: throw");
    }
}
