using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.Panes;

/// <summary>
/// Smooth scrolling for one pane. <see cref="Terminal.ViewOffset"/> stays the logical, whole-line position; this
/// is a purely visual offset in rows added to every displayed row: an eased remainder after the view jumped
/// (wheel notches, page keys, prompt and search jumps, scroll to top/bottom), plus a trackpad's sub-line
/// fraction. Following live output (view offset 0 before and after a change) never eases, and nothing here
/// runs or animates while the view is still. Call everything under the terminal lock.
/// </summary>
internal sealed class SmoothScroll(Terminal term)
{
    public const float EaseMs = 150;
    /// <summary>A trackpad gesture that pauses this long settles on a whole line.</summary>
    public const int SettleMs = 120;
    /// <summary>Lines per wheel unit (a notch, or a trackpad's equivalent distance).</summary>
    public const int LinesPerNotch = 3;

    private Tween _ease = new(0);
    private long _seenTop = long.MinValue;
    private int _seenOffset, _seenReflow, _seenRows;
    private bool _seenAlt, _snap;
    private long _preciseUntil;

    /// <summary>Trackpad scrolling not yet applied as whole lines, in rows (+ = toward older lines).</summary>
    public float Fraction { get; private set; }
    public long FractionAt { get; private set; }

    public bool IsAnimating(long now) => _ease.IsAnimating(now) || Fraction != 0;

    /// <summary>The next frame shows the view where it is, without easing (typing returns to the live screen).</summary>
    public void Snap()
    {
        _snap = true;
        Fraction = 0;
    }

    /// <summary>Adds a visual offset (rows) that eases back to zero.</summary>
    public void Ease(float rows, long now)
    {
        float cur = Math.Clamp(_ease.Value(now) + rows, -term.Rows, term.Rows);
        _ease.Snap(cur);
        _ease.Set(0, now, EaseMs);
    }

    /// <summary>
    /// Per frame: turns a view jump since the last frame into an eased offset. Returns the offset in rows
    /// (positive: rows are drawn lower, revealing the line above the view).
    /// </summary>
    public float Update(bool smooth, long now)
    {
        var t = term;
        long top = t.ViewTopLine;
        bool reset = !smooth || _snap || _seenTop == long.MinValue || t.ReflowCount != _seenReflow
            || t.Rows != _seenRows || t.IsAlternateScreen != _seenAlt;
        if (reset)
        {
            _ease.Snap(0);
            if (!smooth || t.IsAlternateScreen) Fraction = 0;
        }
        else if (top != _seenTop && (t.ViewOffset != 0 || _seenOffset != 0))
            Ease(top - _seenTop, now); // the old rows start where they were and glide to their new place
        _snap = false;
        Remember();
        return _ease.Value(now) + Fraction;
    }

    private void Remember()
    {
        var t = term;
        _seenTop = t.ViewTopLine;
        _seenOffset = t.ViewOffset;
        _seenReflow = t.ReflowCount;
        _seenRows = t.Rows;
        _seenAlt = t.IsAlternateScreen;
    }

    /// <summary>
    /// A wheel event over the history. Notched wheels scroll <see cref="LinesPerNotch"/> lines per notch (eased by
    /// <see cref="Update"/>); fractional deltas (trackpads, hi-res wheels) follow the fingers pixel by pixel.
    /// </summary>
    public void Wheel(int notches, float delta, bool smooth, long now)
    {
#pragma warning disable S1244 // Whether the wheel delta is exactly zero or a whole number is what tells a notched wheel from a precise one.
        bool precise = smooth && delta != 0 && (delta != MathF.Round(delta) || now < _preciseUntil);
#pragma warning restore S1244
        if (!precise)
        {
            term.ScrollView(notches * LinesPerNotch);
            return;
        }
        _preciseUntil = now + 250; // a gesture that started fractional stays precise
        float f = Fraction + delta * LinesPerNotch;
        int whole = (int)MathF.Truncate(f);
        if (whole != 0)
        {
            int before = term.ViewOffset;
            term.ScrollView(whole);
            int moved = term.ViewOffset - before;
            f -= moved;
            if (moved != whole) f = 0; // hit the top or the bottom
        }
        // No partial row beyond the history or below the live screen.
        if (f > 0 && term.ViewOffset >= (term.IsAlternateScreen ? 0 : term.ScrollbackCount)) f = 0;
        if (f < 0 && term.ViewOffset == 0) f = 0;
        Fraction = f;
        FractionAt = now;
        if (_seenTop != long.MinValue) Remember(); // already displayed through Fraction: don't ease it again
    }

    /// <summary>Per tick: a trackpad gesture that paused settles on the nearest whole line, easing the rest.</summary>
    public void Tick(long now)
    {
        if (Fraction == 0 || now - FractionAt < SettleMs) return;
        float f = Fraction;
        int step = MathF.Abs(f) >= 0.5f ? Math.Sign(f) : 0;
        int before = term.ViewOffset;
        if (step != 0) term.ScrollView(step);
        int moved = term.ViewOffset - before;
        Fraction = 0;
        if (_seenTop != long.MinValue) Remember();
        Ease(f - moved, now);
    }
}
