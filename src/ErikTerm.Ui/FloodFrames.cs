namespace ErikTerm.Ui;

/// <summary>
/// Paces a window's frames while its output scrolls faster than anyone can read. A frame is built with the terminal's
/// lock held, which stops the PTY reader, and text that is gone before the next frame isn't worth stopping it for.
/// A screenful per frame is what paging through a file does, so a flood is several times that.
/// </summary>
public sealed class FloodFrames
{
    public const int FrameMs = 100;
    public const int ScreensPerFrame = 4;

    private long _drawnAt = long.MinValue / 2;

    /// <summary>A frame is being held back; the event loop must wake for it, however quiet the output has gone.</summary>
    public bool Holding { get; set; }

    public static bool IsFlood(long linesScrolled, int rows) => linesScrolled >= (long)rows * ScreensPerFrame;

    /// <summary>The last frame is recent enough for the next to wait; after an idle spell the first frame is never held.</summary>
    public bool MayHold(long now) => now - _drawnAt < FrameMs;

    public void Drawn(long now)
    {
        _drawnAt = now;
        Holding = false;
    }

    public bool IsHolding(long now) => Holding && MayHold(now);

    public int WakeMs(long now) => Holding ? (int)Math.Max(1, _drawnAt + FrameMs - now) : int.MaxValue;
}
