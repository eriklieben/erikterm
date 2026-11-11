namespace ErikTerm.App.Host;

/// <summary>
/// One window's event, tick or frame throwing doesn't end the process (every window and tab with it): the loop drops
/// that work, logs the error with its stack once per distinct place (CrashLog.cs), tells the window, and carries on.
/// </summary>
internal sealed class WindowErrors
{
    private readonly HashSet<string> _logged = [];

    private static long Now => Environment.TickCount64;

    /// <summary>Errors not worth surviving: the process is in no state to go on.</summary>
    public static bool IsFatal(Exception ex) => ex is OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException;

    /// <summary>The <paramref name="where"/> ("event", "tick", "frame") of window <paramref name="w"/> threw and was dropped.</summary>
    public void Report(TermWindow w, string where, Exception ex)
    {
        string? log = null;
        // The same bug every frame: one log (and one stderr line) per place it was thrown from.
        string site = $"{where}|{ex.GetType().FullName}|{FirstFrame(ex)}";
        if (_logged.Count < 64 && _logged.Add(site))
        {
            log = CrashLog.Write($"window {w.Id}: {where} (recovered: the {where} was dropped)", ex);
            Console.Error.WriteLine($"erikterm: window {w.Id}: internal error in {where}: {ex.GetType().Name}: {ex.Message}"
                + (log is not null ? $" (logged to {log})" : ""));
        }
        try
        {
            w.OnInternalError(log ?? CrashLog.Latest()?.Path, Now);
        }
        catch (Exception again) when (!IsFatal(again))
        {
            Console.Error.WriteLine($"erikterm: window {w.Id}: {again.Message}");
        }
    }

    private static string FirstFrame(Exception ex)
    {
        string trace = ex.StackTrace ?? "";
        int nl = trace.IndexOf('\n');
        return (nl < 0 ? trace : trace[..nl]).Trim();
    }
}
