namespace ErikTerm.Core;

/// <summary>What <see cref="SynchronizedOutputGate"/> needs from the terminal it feeds.</summary>
internal interface ISynchronizedOutputTarget
{
    /// <summary>DEC mode 2026, as the parsed output left it; the gate turns it off when it gives up waiting.</summary>
    bool SynchronizedOutput { get; set; }

    /// <summary>Parses output (the parser and the screen update).</summary>
    void Parse(ReadOnlySpan<byte> data);
}

/// <summary>
/// Synchronized output (DEC mode 2026). Between the begin and end sequences the program's bytes are held back, not
/// parsed, so the grid always shows the last complete update: every pane can be drawn at any moment, focused or not.
/// A program that never ends its update is released after <see cref="Terminal.SynchronizedOutputTimeoutMs"/> or when
/// <see cref="Terminal.SynchronizedOutputMaxHeld"/> bytes wait. Called once per chunk of output, not per byte.
/// </summary>
internal sealed class SynchronizedOutputGate(ISynchronizedOutputTarget target)
{
    private static ReadOnlySpan<byte> SyncBegin => "\e[?2026h"u8;
    private static ReadOnlySpan<byte> SyncEnd => "\e[?2026l"u8;

    private byte[] _held = new byte[4096], _heldSpare = new byte[4096];
    private int _heldCount;
    private long _heldSince;

    /// <summary>Whether output is waiting for the end of a synchronized update. Safe to read without the lock.</summary>
    public bool Holds => Volatile.Read(ref _heldCount) > 0;

    public void Feed(ReadOnlySpan<byte> data, long now)
    {
        ReleaseExpired(now);
        while (!data.IsEmpty)
        {
            if (target.SynchronizedOutput)
            {
                int searchFrom = Math.Max(0, _heldCount - (SyncEnd.Length - 1));
                Hold(data, now);
                int end = _held.AsSpan(searchFrom, _heldCount - searchFrom).IndexOf(SyncEnd);
                if (end < 0)
                {
                    if (_heldCount >= Terminal.SynchronizedOutputMaxHeld) Release(now);
                    return;
                }
                int complete = searchFrom + end + SyncEnd.Length;
                var held = TakeHeld();
                target.Parse(held[..complete]); // turns the mode off
                data = held[complete..];
                continue;
            }
            int begin = data.IndexOf(SyncBegin);
            if (begin < 0)
            {
                target.Parse(data);
                return;
            }
            int upToBegin = begin + SyncBegin.Length;
            target.Parse(data[..upToBegin]);
            data = data[upToBegin..];
        }
    }

    /// <summary>Parses held output whose update has taken too long. True when it did.</summary>
    public bool ReleaseExpired(long now)
    {
        if (_heldCount == 0 || now - _heldSince < Terminal.SynchronizedOutputTimeoutMs) return false;
        Release(now);
        return true;
    }

    private void Release(long now)
    {
        target.SynchronizedOutput = false;
        Feed(TakeHeld(), now);
    }

    private void Hold(ReadOnlySpan<byte> data, long now)
    {
        if (_heldCount == 0) _heldSince = now;
        int needed = _heldCount + data.Length;
        if (needed > _held.Length) Array.Resize(ref _held, Math.Max(needed, _held.Length * 2));
        data.CopyTo(_held.AsSpan(_heldCount));
        Volatile.Write(ref _heldCount, needed);
    }

    /// <summary>The held bytes; valid until the next-but-one call (the two buffers swap, and <see cref="Hold"/> copies out of this one).</summary>
    private ReadOnlySpan<byte> TakeHeld()
    {
        var taken = _held.AsSpan(0, _heldCount);
        (_held, _heldSpare) = (_heldSpare, _held);
        Volatile.Write(ref _heldCount, 0);
        return taken;
    }
}
