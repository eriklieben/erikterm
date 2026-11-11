namespace ErikTerm.Core;

/// <summary>
/// Runs a piece of work once a pane has had no output for a while. A timer, not the PTY reader's idle step: a read on a
/// Windows pipe can't time out, so the reader may not come back to idle work until the program prints again.
/// </summary>
public sealed class AfterOutputStops : IDisposable
{
    private readonly int _quietMs;
    private readonly Func<bool> _wanted;
    private readonly Action _work;
    private readonly Timer _timer;
    private long _outputAt;
    private int _armed;

    /// <param name="wanted">Whether there is work to wait for; called for every chunk of output, so it must be cheap.</param>
    public AfterOutputStops(int quietMs, Func<bool> wanted, Action work)
    {
        _quietMs = quietMs;
        _wanted = wanted;
        _work = work;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The program printed: the quiet period starts over, if there is work waiting for it.</summary>
    public void NoteOutput()
    {
        if (!_wanted()) return;
        Volatile.Write(ref _outputAt, Environment.TickCount64);
        if (Interlocked.Exchange(ref _armed, 1) == 0) _timer.Change(_quietMs, Timeout.Infinite);
    }

    /// <summary>Does the work if the output has been quiet long enough; else waits for the remainder.</summary>
    internal void Tick()
    {
        long waited = Environment.TickCount64 - Volatile.Read(ref _outputAt);
        try
        {
            if (waited < _quietMs)
            {
                _timer.Change(_quietMs - waited, Timeout.Infinite);
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            return; // the pane closed while this was on its way
        }
        Volatile.Write(ref _armed, 0); // first: output from here on needs the timer again
        _work();
    }

    public void Dispose() => _timer.Dispose();
}
