namespace ErikTerm.App.Ipc;

/// <summary>
/// Hands requests from socket threads to the main thread. <see cref="Post"/> (any thread) queues a request,
/// wakes the event loop and returns a task that completes when <see cref="Drain"/> (main thread) has run
/// it. Requests run in the order they were posted. After <see cref="Close"/>, pending and new requests
/// fail with <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed class RequestQueue<TRequest, TResponse>(Action wake)
{
    private readonly Lock _gate = new();
    private readonly Queue<(TRequest Request, TaskCompletionSource<TResponse> Done)> _pending = new();
    private bool _closed;

    public int Count { get { lock (_gate) return _pending.Count; } }

    public Task<TResponse> Post(TRequest request)
    {
        // Continuations (writing the reply) run on the pool, never inline on the main thread.
        var done = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed) return Task.FromException<TResponse>(new ObjectDisposedException(nameof(RequestQueue<TRequest, TResponse>)));
            _pending.Enqueue((request, done));
        }
        wake();
        return done.Task;
    }

    /// <summary>Runs every queued request with <paramref name="handler"/>; an exception fails just that request. Returns how many ran.</summary>
    public int Drain(Func<TRequest, TResponse> handler)
    {
        int n = 0;
        while (true)
        {
            (TRequest Request, TaskCompletionSource<TResponse> Done) item;
            lock (_gate)
            {
                if (!_pending.TryDequeue(out item)) return n;
            }
            n++;
            try
            {
                item.Done.TrySetResult(handler(item.Request));
            }
            catch (Exception ex)
            {
                item.Done.TrySetException(ex);
            }
        }
    }

    public void Close()
    {
        List<TaskCompletionSource<TResponse>> orphans;
        lock (_gate)
        {
            _closed = true;
            orphans = _pending.Select(p => p.Done).ToList();
            _pending.Clear();
        }
        foreach (var o in orphans) o.TrySetException(new ObjectDisposedException(nameof(RequestQueue<TRequest, TResponse>)));
    }
}
