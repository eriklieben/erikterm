namespace ErikTerm.Ui;

/// <summary>Timing for the text cursor: a blink that fades instead of flicking, and when a move should glide.</summary>
public static class CursorMotion
{
    /// <summary>After typing or cursor movement the cursor stays solid this long before blinking resumes.</summary>
    public const int BlinkPauseMs = 1200;

    /// <summary>Length of the fade at each end of a blink transition for a given rate.</summary>
    public static float FadeMs(int rateMs) => MathF.Min(rateMs * 0.45f, 260);

    /// <summary>
    /// Cursor opacity <paramref name="elapsedMs"/> after the last activity, blinking every
    /// <paramref name="rateMs"/> (wezterm's cursor_blink_rate: time between visible and invisible).
    /// </summary>
    public static float BlinkOpacity(long elapsedMs, int rateMs, bool pulse = false)
    {
        if (rateMs <= 0) return 1;
        long t = elapsedMs - BlinkPauseMs;
        if (t < 0) return 1;
        if (pulse) return PhaseOpacity(t, rateMs);
        float fade = FadeMs(rateMs);
        float phase = t % (2L * rateMs);
        if (phase < rateMs - fade) return 1;
        if (phase < rateMs) return 1 - Ease.InOutCubic((phase - (rateMs - fade)) / fade);
        if (phase < 2 * rateMs - fade) return 0;
        return Ease.InOutCubic((phase - (2 * rateMs - fade)) / fade);
    }

    /// <summary>Milliseconds until the blink opacity next starts changing (0 while it is fading).</summary>
    public static int MsUntilChange(long elapsedMs, int rateMs, bool pulse = false)
    {
        if (rateMs <= 0) return int.MaxValue;
        long t = elapsedMs - BlinkPauseMs;
        if (pulse) return t < 0 ? (int)-t : 0; // the pulse never rests
        float fade = FadeMs(rateMs);
        if (t < 0) return (int)Math.Ceiling(-t + rateMs - fade);
        float phase = t % (2L * rateMs);
        if (phase < rateMs - fade) return (int)Math.Ceiling(rateMs - fade - phase);
        if (phase < rateMs) return 0;
        if (phase < 2 * rateMs - fade) return (int)Math.Ceiling(2 * rateMs - fade - phase);
        return 0;
    }

    /// <summary>
    /// The "phase" blink: a continuous cosine pulse from fully visible down to <see cref="PhaseFloor"/> and back,
    /// one full cycle every 2 × <paramref name="rateMs"/> (so the rate means the same as for the on/off blink).
    /// </summary>
    public static float PhaseOpacity(long t, int rateMs)
    {
        float wave = 0.5f + 0.5f * MathF.Cos(MathF.PI * (t % (2L * rateMs)) / rateMs);
        return PhaseFloor + (1 - PhaseFloor) * wave;
    }

    /// <summary>Lowest opacity of the phase pulse: a thin bar has to stay easy to see at the bottom of it.</summary>
    public const float PhaseFloor = 0.5f;

    /// <summary>
    /// How long a hidden cursor keeps being drawn. Programs hide it around every redraw; a frame that lands inside one
    /// must not make it flicker.
    /// </summary>
    public const long HideGraceMs = 50;

    public static bool HideTakesEffect(long msSinceHidden) => msSinceHidden >= HideGraceMs;

    /// <summary>
    /// Whether a move of (dx, dy) cells should glide: only short hops (typing, word jumps, the next line),
    /// and never when the cursor is moving fast (output streaming, key repeat) so it can't trail behind.
    /// </summary>
    public static bool ShouldGlide(int dx, int dy, long msSinceLastMove) =>
        msSinceLastMove >= 45 && Math.Abs(dy) <= 1 && Math.Abs(dx) <= 16;

    /// <summary>Default bar height, in multiples of the font size: from the top of tall letters to just below the baseline.</summary>
    public const float DefaultBarHeightEm = 1.19f;

    /// <summary>
    /// Top and bottom of a bar cursor within its cell. A positive <paramref name="heightEm"/> hugs the text around the
    /// baseline, independent of line_height; 0 fills the cell.
    /// </summary>
    public static (float Top, float Bottom) BarSpan(int cellHeight, int baseline, float pixelSize, float heightEm)
    {
        if (heightEm <= 0) return (0, cellHeight);
        float height = MathF.Max(1, MathF.Round(heightEm * pixelSize));
        float top = Math.Clamp(MathF.Round(baseline - height * 0.78f), 0, MathF.Max(0, cellHeight - 1));
        return (top, MathF.Min(cellHeight, top + height));
    }

    /// <summary>How long a bar stays thicker after a typed character.</summary>
    public const long TypingSettleMs = 110;

    /// <summary>Milliseconds until the settle ends, or int.MaxValue when it is over and needs no frame.</summary>
    public static int MsUntilSettled(long now, long settleUntil) =>
        now < settleUntil ? (int)(settleUntil - now) : int.MaxValue;

    /// <summary>
    /// Whether a move put new text behind the cursor: it went right on its row and the cell it left has changed.
    /// Moving over existing text (arrow keys, word jumps) leaves that cell as it was.
    /// </summary>
    public static bool IsTyping(int dx, int dy, bool leftCellChanged) => dy == 0 && dx > 0 && leftCellChanged;
}

/// <summary>How a rounded outline covers the pixels of its corners.</summary>
public static class RoundedOutline
{
    /// <summary>
    /// Coverage (0..1) of a pixel whose centre is (<paramref name="dx"/>, <paramref name="dy"/>) from the centre of a
    /// corner arc of outer <paramref name="radius"/>, for a line <paramref name="width"/> wide.
    /// </summary>
    public static float Coverage(float dx, float dy, float radius, float width)
    {
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        float toEdge = MathF.Min(radius - distance, distance - (radius - width));
        return Math.Clamp(toEdge + 0.5f, 0, 1);
    }
}

/// <summary>When a costly full GC may run: asked for by background work, held back until the keyboard has been quiet.</summary>
public sealed class QuietCollection
{
    public const long QuietMs = 3000;

    private long _lastInput = long.MinValue / 2;
    private int _wanted;

    public void NoteInput(long now) => Volatile.Write(ref _lastInput, now);

    public void Request() => Volatile.Write(ref _wanted, 1);

    /// <summary>True once per request, when the keyboard has been quiet long enough.</summary>
    public bool TryTake(long now) =>
        Volatile.Read(ref _wanted) != 0 && now - Volatile.Read(ref _lastInput) >= QuietMs && Interlocked.Exchange(ref _wanted, 0) != 0;
}

/// <summary>How long the event loop may sleep with nothing scheduled. Whatever needs it sooner wakes it or asks for a timeout.</summary>
public static class IdleTick
{
    public const int ActiveMs = 500, DeepIdleMs = 10_000, DeepIdleAfterMs = 2000;

    public static int Ms(long now, long lastEventAt) => now - lastEventAt < DeepIdleAfterMs ? ActiveMs : DeepIdleMs;
}

/// <summary>Where text being composed in an input method is drawn.</summary>
public static class ImePlacement
{
    /// <summary>At the cursor, moved left as far as it takes to stay inside the pane; a pane too narrow shows its start.</summary>
    public static float Left(float cursorX, float textWidth, float paneLeft, float paneRight) =>
        MathF.Max(paneLeft, MathF.Min(cursorX, paneRight - textWidth));
}
