using System.Runtime.InteropServices;

namespace ErikTerm.App.Host;

/// <summary>
/// SIGTERM / SIGHUP (logout, `kill`) close every window normally, so the session is saved. The main loop saves the
/// session and exits within moments of a quit signal. If it doesn't (a bug blocks the UI thread), the process ends
/// anyway after 5 s rather than being left to `kill -9`.
/// </summary>
/// <param name="quit">Ends the event loop (called on the signal's thread).</param>
internal sealed class QuitSignals(Action quit) : IDisposable
{
    private static readonly int QuitGraceMs = 5000;

    private readonly List<PosixSignalRegistration> _signals = [];
    private int _watchdog;

    public void Register()
    {
        foreach (var signal in (ReadOnlySpan<PosixSignal>)[PosixSignal.SIGTERM, PosixSignal.SIGHUP])
        {
            try
            {
                _signals.Add(PosixSignalRegistration.Create(signal, ctx =>
                {
                    ctx.Cancel = true;
                    quit();
                    StartWatchdog(signal.ToString());
                }));
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or IOException) { /* this signal can't be watched here: it keeps its default handling */ }
        }
    }

    private void StartWatchdog(string signal)
    {
        if (Interlocked.Exchange(ref _watchdog, 1) != 0) return;
        var thread = new Thread(() =>
        {
            Thread.Sleep(QuitGraceMs);
            Console.Error.WriteLine($"erikterm: {signal}: the main loop didn't finish within {QuitGraceMs / 1000} s; exiting without saving the session");
            CrashLog.Write($"{signal}: the main loop didn't finish within {QuitGraceMs / 1000} s; exited without saving the session", null);
            Environment.Exit(128 + 15);
        })
        { IsBackground = true, Name = "quit-watchdog" };
        thread.Start();
    }

    public void Dispose()
    {
        foreach (var s in _signals) s.Dispose();
    }
}
