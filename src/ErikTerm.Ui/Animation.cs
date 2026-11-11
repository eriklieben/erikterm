namespace ErikTerm.Ui;

public static class Ease
{
    public static float OutCubic(float t) => 1 - MathF.Pow(1 - Math.Clamp(t, 0, 1), 3);
    public static float InOutCubic(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2;
    }
    /// <summary>Slight overshoot — for things that "pop" in.</summary>
    public static float OutBack(float t)
    {
        t = Math.Clamp(t, 0, 1);
        const float c1 = 1.4f, c3 = c1 + 1;
        return 1 + c3 * MathF.Pow(t - 1, 3) + c1 * MathF.Pow(t - 1, 2);
    }
}

/// <summary>
/// A float that animates toward a target. Retargeting mid-flight starts from the current value,
/// so interrupted animations never jump. Time is in milliseconds (Environment.TickCount64).
/// </summary>
public struct Tween
{
    private float _from, _to;
    private long _start;
    private float _duration;

    public Tween(float value)
    {
        _from = _to = value;
        _start = 0;
        _duration = 0;
    }

    public readonly float Target => _to;

    public void Set(float target, long now, float durationMs)
    {
#pragma warning disable S1244 // _to is exactly the value the last Set stored: this asks "the same target again?", and a tolerance would drop small real changes
        if (target == _to && !IsAnimating(now)) return;
#pragma warning restore S1244
        _from = Value(now);
        _to = target;
        _start = now;
        _duration = durationMs;
    }

    public void Snap(float value)
    {
        _from = _to = value;
        _duration = 0;
    }

    public readonly float Value(long now)
    {
        if (_duration <= 0) return _to;
        float t = (now - _start) / _duration;
        if (t >= 1) return _to;
        return _from + (_to - _from) * Ease.OutCubic(t);
    }

    public readonly bool IsAnimating(long now) => _duration > 0 && now - _start < _duration;
}

/// <summary>A rectangle that eases toward its target (used for pane layout transitions).</summary>
public struct RectTween
{
    private RectF _from, _to;
    private long _start;
    private float _duration;
    private bool _initialized;

    public readonly RectF Target => _to;

    public void Set(RectF target, long now, float durationMs)
    {
        if (!_initialized) { _from = _to = target; _initialized = true; return; }
        if (target == _to) return;
        _from = Value(now);
        _to = target;
        _start = now;
        _duration = durationMs;
    }

    public void Snap(RectF target)
    {
        _from = _to = target;
        _duration = 0;
        _initialized = true;
    }

    public readonly RectF Value(long now)
    {
        if (_duration <= 0) return _to;
        float t = (now - _start) / _duration;
        return t >= 1 ? _to : RectF.Lerp(_from, _to, Ease.OutCubic(t));
    }

    public readonly bool IsAnimating(long now) => _duration > 0 && now - _start < _duration;
}
