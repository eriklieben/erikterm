namespace ErikTerm.Core.Images;

/// <summary>
/// Frames and playback state of a kitty animation (a=f / a=a / a=c). Frame 0 is the image's root frame
/// (<see cref="TerminalImage.Rgba"/>); the others are full canvases (the protocol composes each new frame onto a base
/// frame or a background color when it arrives, so drawing a frame is a texture upload, not a composition).
/// Playback is evaluated lazily from timestamps: <see cref="Update"/> (renderer, each drawn frame) advances to the
/// frame due at a time, and <see cref="MsUntilNextFrame"/> says when the next change is due, so an animation costs a
/// wake-up per frame change only while it is drawn, and nothing when it is stopped, waiting for frames or hidden.
/// </summary>
public sealed class ImageAnimation
{
    /// <summary>Default gap of frames sent with a=f (the root frame defaults to 0, see <see cref="Gap"/>).</summary>
    public const int DefaultGapMs = 40;
    public const int MaxFrames = 1024;

    public enum PlayState { Stopped = 1, Loading = 2, Running = 3 }

    private readonly TerminalImage _image;
    private readonly List<byte[]> _frames = []; // frames 1.. (frame 0 is the image's own pixels)
    private readonly List<int> _gaps = [0];      // per frame, ms; <= 0: gapless (skipped while playing)

    internal ImageAnimation(TerminalImage image) => _image = image;

    /// <summary>Frames including the root frame.</summary>
    public int FrameCount => _gaps.Count;

    public PlayState State { get; private set; } = PlayState.Stopped;

    /// <summary>0-based index of the frame shown.</summary>
    public int Current { get; private set; }

    /// <summary>Bumped when frame pixels change (renderers re-upload the texture).</summary>
    public int PixelsVersion { get; private set; }

    /// <summary>v=: 0 = loop forever, else the number of times to play (v - 1 extra loops).</summary>
    private int _loops;
    private int _loopsLeft;
    private long _shownAt;

    public byte[]? Frame(int index)
    {
        if (index == 0) return _image.Rgba;
        return (uint)(index - 1) < (uint)_frames.Count ? _frames[index - 1] : null;
    }

    public int Gap(int index) => (uint)index < (uint)_gaps.Count ? _gaps[index] : 0;

    internal long ExtraBytes => (long)_frames.Count * _image.Width * _image.Height * 4;

    internal void AddFrame(byte[] rgba, int gap)
    {
        _frames.Add(rgba);
        _gaps.Add(gap);
        PixelsVersion++;
    }

    internal void SetGap(int index, int gap)
    {
        if ((uint)index < (uint)_gaps.Count) _gaps[index] = gap;
    }

    internal void Touched() => PixelsVersion++;

    internal void Release() => _frames.Clear();

    /// <summary>a=a s=: stopping resets the loop counter; (re)starting starts the current frame's gap now.</summary>
    internal void SetState(PlayState state, long now)
    {
        if (state == PlayState.Stopped) _loopsLeft = _loops;
        else if (State == PlayState.Stopped) { _shownAt = now; _loopsLeft = _loops; }
        State = state;
    }

    /// <summary>a=a v=: 1 loops forever, n plays n - 1 times; 0 is ignored by the caller.</summary>
    internal void SetLoops(int v)
    {
        _loops = v <= 1 ? 0 : v - 1;
        _loopsLeft = _loops;
    }

    internal void SetCurrent(int index, long now)
    {
        Current = Math.Clamp(index, 0, FrameCount - 1);
        _shownAt = now;
    }

    private bool HasTimedFrame() => _gaps.Exists(g => g > 0);

    /// <summary>Advances to the frame due at <paramref name="now"/>; returns true when the shown frame changed.</summary>
    public bool Update(long now)
    {
        if (State == PlayState.Stopped || FrameCount < 2 || !HasTimedFrame()) return false;
        int start = Current;
        long cycle = 0;
        foreach (int g in _gaps) cycle += Math.Max(g, 0);
        // Hidden for a long time (or the clock jumped): resume from where it was rather than replaying every frame.
        if (now - _shownAt > Math.Max(cycle * 2, 2000)) _shownAt = now - Math.Max(0, Gap(Current));
        for (int guard = 0; guard < 4 * MaxFrames; guard++)
        {
            int gap = Math.Max(0, Gap(Current));
            if (now - _shownAt < gap) break;
            int next = Current + 1;
            if (next >= FrameCount)
            {
                if (!LoopAgain()) break;
                next = 0;
            }
            _shownAt += gap;
            Current = next;
        }
        return Current != start;
    }

    /// <summary>At the last frame: true to wrap to the first, false to stay (waiting for frames, or out of loops).</summary>
    private bool LoopAgain()
    {
        if (State == PlayState.Loading) return false; // wait at the last frame for more
        if (_loops > 0)
        {
            if (_loopsLeft <= 0) { State = PlayState.Stopped; _loopsLeft = _loops; return false; }
            _loopsLeft--;
        }
        return true;
    }

    /// <summary>Milliseconds until the shown frame changes (0: due now), or int.MaxValue when nothing will change by itself.</summary>
    public int MsUntilNextFrame(long now)
    {
        if (State == PlayState.Stopped || FrameCount < 2 || !HasTimedFrame()) return int.MaxValue;
        if (State == PlayState.Loading && Current == FrameCount - 1) return int.MaxValue;
        long due = _shownAt + Math.Max(0, Gap(Current)) - now;
        return (int)Math.Clamp(due, 0, int.MaxValue - 1);
    }
}
