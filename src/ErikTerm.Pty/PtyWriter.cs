namespace ErikTerm.Pty;

/// <summary>Where a <see cref="PtyWriter"/> sends its bytes: the pty master (Linux) or the ConPTY input pipe (Windows).</summary>
public interface IPtySink
{
    /// <summary>
    /// Writes some of <paramref name="data"/>, waiting at most <paramref name="timeoutMs"/> for room (a backend that can't
    /// time out, like a Windows pipe, may wait longer). Returns the bytes written (0 = no room yet), or -1 once the other
    /// side is gone for good.
    /// </summary>
    int WriteSome(ReadOnlySpan<byte> data, int timeoutMs);
}

/// <summary>
/// Input for a child process, queued and written by a thread of its own, so that a program that doesn't read its input
/// can never block the caller (the UI thread for keys and pastes, a reader thread for terminal replies). The queue is a
/// byte ring that grows up to <see cref="Capacity"/>; what doesn't fit is dropped whole (a paste is never cut in half).
/// Terminal replies (DA, DSR, OSC queries) are only queued while less than <see cref="ReplyLimit"/> is waiting: a program
/// that floods queries without reading the answers gets the first 64 KB and no more.
/// </summary>
public sealed class PtyWriter : IDisposable
{
    public const int DefaultCapacity = 16 * 1024 * 1024;
    public const int DefaultReplyLimit = 64 * 1024;
    /// <summary>Largest single write: big pastes go out in pieces, so closing the pane never waits on one huge write.</summary>
    public const int ChunkSize = 64 * 1024;
    private const int PollMs = 100;

    private readonly IPtySink _sink;
    private readonly string _name;
    private readonly object _sync = new();
    private byte[] _ring = new byte[4096];
    private int _head, _count;
    private bool _closed, _gone, _writing;
    private Thread? _thread;
    private long _droppedBytes, _droppedReplies, _written;

    public PtyWriter(IPtySink sink, string name = "pty-writer", int capacity = DefaultCapacity, int replyLimit = DefaultReplyLimit)
    {
        _sink = sink;
        _name = name;
        Capacity = capacity;
        ReplyLimit = replyLimit;
    }

    public int Capacity { get; }
    public int ReplyLimit { get; }

    /// <summary>Bytes queued and not yet taken by the child.</summary>
    public int Pending { get { lock (_sync) return _count; } }
    /// <summary>Input bytes dropped because the queue was full (the child isn't reading).</summary>
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);
    /// <summary>Terminal replies dropped because <see cref="ReplyLimit"/> was already waiting.</summary>
    public long DroppedReplies => Interlocked.Read(ref _droppedReplies);
    /// <summary>Bytes handed to the child so far.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Queues input (keys, a paste). False when it was dropped: the queue is full, or the child is gone.</summary>
    public bool Write(ReadOnlySpan<byte> data) => Enqueue(data, Capacity, reply: false);

    /// <summary>Queues a terminal reply. False when it was dropped (<see cref="ReplyLimit"/> waiting, or the child is gone).</summary>
    public bool WriteReply(ReadOnlySpan<byte> data) => Enqueue(data, ReplyLimit, reply: true);

    private bool Enqueue(ReadOnlySpan<byte> data, int limit, bool reply)
    {
        if (data.IsEmpty) return true;
        lock (_sync)
        {
            if (_closed || _gone) return false;
            if ((long)_count + data.Length > limit)
            {
                if (reply) _droppedReplies++;
                else _droppedBytes += data.Length;
                return false;
            }
            EnsureRoom(_count + data.Length);
            int tail = (_head + _count) % _ring.Length;
            int first = Math.Min(data.Length, _ring.Length - tail);
            data[..first].CopyTo(_ring.AsSpan(tail));
            data[first..].CopyTo(_ring);
            _count += data.Length;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = _name };
                _thread.Start();
            }
            else Monitor.Pulse(_sync);
        }
        return true;
    }

    /// <summary>Grows the ring (doubling) so <paramref name="needed"/> bytes fit; the queued bytes move to the front.</summary>
    private void EnsureRoom(int needed)
    {
        if (needed <= _ring.Length) return;
        int size = _ring.Length;
        while (size < needed) size = (int)Math.Min((long)size * 2, Math.Max(needed, Capacity));
        var grown = new byte[size];
        int first = Math.Min(_count, _ring.Length - _head);
        _ring.AsSpan(_head, first).CopyTo(grown);
        _ring.AsSpan(0, _count - first).CopyTo(grown.AsSpan(first));
        _ring = grown;
        _head = 0;
    }

    /// <summary>Waits for queued bytes and marks the next chunk as being written; false when the writer is closed.</summary>
    private bool TakeChunk(out byte[] ring, out int start, out int length)
    {
        lock (_sync)
        {
            while (_count == 0 && !_closed) Monitor.Wait(_sync);
            ring = _ring;
            start = _head;
            length = Math.Min(Math.Min(_count, ring.Length - start), ChunkSize);
            if (_closed) return false;
            _writing = true;
            return true;
        }
    }

    /// <summary>Accounts for a write's result (&lt; 0: the child is gone); false when the writer was closed meanwhile.</summary>
    private bool FinishChunk(int n)
    {
        lock (_sync)
        {
            _writing = false;
            if (n < 0)
            {
                _gone = true; // the child is gone: nothing will ever be read again
                _count = 0;
                _head = 0;
            }
            else if (n > 0)
            {
                _head = (_head + n) % _ring.Length;
                _count -= n;
                _written += n;
                if (_count == 0) _head = 0;
            }
            if (_closed) { _count = 0; _head = 0; }
            Monitor.PulseAll(_sync); // WaitUntilFlushed / Close
            return !_closed;
        }
    }

    private void Run()
    {
        while (TakeChunk(out byte[] ring, out int start, out int length))
        {
            // The queued bytes stay put while they are written: producers only append past them, and a grown ring is a
            // copy (this one stays valid; the head advances by what was written in whichever ring is current).
            int n;
            try
            {
                n = _sink.WriteSome(ring.AsSpan(start, length), PollMs);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                n = -1;
            }

            if (!FinishChunk(n)) return;
        }
    }

    /// <summary>Waits until everything queued was taken by the child (or it went away). False on timeout.</summary>
    public bool WaitUntilFlushed(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_sync)
        {
            while (_count > 0 && !_gone)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0) return false;
                Monitor.Wait(_sync, (int)Math.Min(left, int.MaxValue));
            }
            return true;
        }
    }

    /// <summary>
    /// Stops the writer: what the child takes within <paramref name="drain"/> is still written, the rest is dropped.
    /// Returns once the writer thread has left the sink (or after a short grace period on a sink that can't time out),
    /// so the caller can close the descriptor without the thread writing into a reused one.
    /// </summary>
    public void Close(TimeSpan drain)
    {
        Thread? thread;
        lock (_sync)
        {
            if (_closed) return;
            if (drain > TimeSpan.Zero && _thread is not null)
            {
                long deadline = Environment.TickCount64 + (long)drain.TotalMilliseconds;
                while (_count > 0 && !_gone && Environment.TickCount64 < deadline)
                    Monitor.Wait(_sync, (int)Math.Max(1, deadline - Environment.TickCount64));
            }
            _closed = true;
            _droppedBytes += _count;
            // The chunk being written (if any) finishes; the thread drops the rest and exits after it.
            if (!_writing) { _count = 0; _head = 0; }
            thread = _thread;
            Monitor.PulseAll(_sync);
        }
        thread?.Join(PollMs * 3);
    }

    public void Dispose() => Close(TimeSpan.Zero);
}
