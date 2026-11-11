using System.Collections.Concurrent;

namespace ErikTerm.App.Integrations;

/// <summary>
/// A window's background jobs: <see cref="Run{T}"/> runs work on the thread pool (at most three at a time) and hands
/// its result back to the main thread, where <see cref="Drain"/> (the integrations' tick) runs the continuation. Used by
/// git status, GitHub, worktrees, links and the theme download; nothing here touches the window itself.
/// </summary>
internal sealed class BackgroundJobs(Action wake)
{
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly SemaphoreSlim _gate = new(3);
    private int _inFlight;

    /// <summary>A job runs or a result waits to be collected: the event loop should look again soon.</summary>
    public bool Busy => Volatile.Read(ref _inFlight) > 0 || !_inbox.IsEmpty;

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool and <paramref name="then"/> with its result on the main thread; an
    /// exception becomes a null result. <paramref name="wakeLoop"/>: the user is waiting (a click), so wake the loop at
    /// once instead of at its next poll.
    /// </summary>
    public void Run<T>(Func<T?> work, Action<T?> then, bool wakeLoop = false) where T : class
    {
        Interlocked.Increment(ref _inFlight);
        Task.Run(() =>
        {
            T? result = null;
            _gate.Wait();
            try { result = work(); }
            catch (Exception ex) { Console.Error.WriteLine($"erikterm: background job failed: {ex.Message}"); }
            finally { _gate.Release(); }
            _inbox.Enqueue(() => then(result));
            Interlocked.Decrement(ref _inFlight);
            if (wakeLoop) wake();
        });
    }

    /// <summary>Queues <paramref name="then"/> for the main thread (from any thread); it runs at the next <see cref="Drain"/>.</summary>
    public void Post(Action then) => _inbox.Enqueue(then);

    /// <summary>Main thread: runs the results that came back.</summary>
    public void Drain()
    {
        while (_inbox.TryDequeue(out var then)) then();
    }
}
