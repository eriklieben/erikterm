namespace ErikTerm.Ui;

/// <summary>
/// Recognises a modifier key tapped twice on its own (Ctrl, Ctrl). Ordinary typing must never count: any other key or a
/// mouse click in between starts over, as does a press held too long, so Ctrl+C, Ctrl+C is two chords and not a gesture.
/// It fires on the second release rather than the second press, so a stray tap just before a Ctrl+key chord does nothing.
/// </summary>
public sealed class DoubleTapDetector
{
    /// <summary>A longer press isn't a tap.</summary>
    public const int MaxHoldMs = 250;
    /// <summary>The most time between the first release and the second press.</summary>
    public const int MaxGapMs = 350;

    private long _downAt = -1, _firstUpAt = -1;

    /// <summary>The modifier went down, with no other modifier held.</summary>
    public void Down(long now)
    {
        if (_firstUpAt >= 0 && now - _firstUpAt > MaxGapMs) _firstUpAt = -1;
        _downAt = now;
    }

    /// <summary>The modifier came up; true when that completes a double tap.</summary>
    public bool Up(long now)
    {
        if (_downAt < 0) return false;
        bool tap = now - _downAt <= MaxHoldMs;
        _downAt = -1;
        if (!tap) { _firstUpAt = -1; return false; }
        if (_firstUpAt >= 0) { _firstUpAt = -1; return true; }
        _firstUpAt = now;
        return false;
    }

    /// <summary>Anything else happened (another key, a click, focus lost): start over.</summary>
    public void Reset() => _downAt = _firstUpAt = -1;
}
