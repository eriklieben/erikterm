namespace ErikTerm.Ui;

/// <summary>One of the caption buttons a borderless window draws for itself.</summary>
public enum WindowControl
{
    None = -1,
    Minimize = 0,
    Maximize = 1,
    Close = 2,
}

/// <summary>
/// Where the minimize / maximize / close buttons sit, and which one a point is on. A borderless window has no
/// caption bar, so the strip is pinned to the window's top-right corner — the corner the platforms that put
/// window buttons on the right have trained the pointer to go to — and the buttons run left to right in the
/// order the system would draw them, with close last.
/// </summary>
public static class WindowControlsLayout
{
    /// <summary>
    /// Whether a window draws its own chrome (floating tab bar, caption buttons, drag/resize hit test): when it is
    /// borderless on a platform where that leaves it with no frame at all. Not on Linux, where "no decorations" is
    /// the normal setup under a tiling Wayland compositor that places and sizes windows itself.
    /// </summary>
    public static bool SelfDrawnChrome(bool windowDecorations, bool linux) => !windowDecorations && !linux;

    public const int Count = 3;

    /// <summary>
    /// The strip of <see cref="Count"/> buttons, right-aligned in <paramref name="windowWidth"/>.
    /// <paramref name="inset"/> keeps it clear of the window's rounded corner and border.
    /// </summary>
    public static RectF Strip(float windowWidth, float top, float buttonWidth, float height, float inset) =>
        new(windowWidth - inset - buttonWidth * Count, top, buttonWidth * Count, height);

    public static RectF Button(RectF strip, int index) =>
        new(strip.X + index * (strip.W / Count), strip.Y, strip.W / Count, strip.H);

    public static WindowControl HitTest(RectF strip, float x, float y)
    {
        if (strip.W <= 0 || strip.H <= 0 || !strip.Contains(x, y)) return WindowControl.None;
        int index = (int)((x - strip.X) / (strip.W / Count));
        return (WindowControl)Math.Clamp(index, 0, Count - 1);
    }

    /// <summary>
    /// The band along the top of the window that reveals the chrome. It spans the full width so the tab bar is
    /// reachable too, and is deliberately taller than the chrome itself: the pointer should bring the bar out
    /// before it has to land on it, and small overshoots past the bar shouldn't put it away again.
    /// </summary>
    public static bool InRevealBand(float windowWidth, float bandHeight, float x, float y) =>
        x >= 0 && x < windowWidth && y >= 0 && y < bandHeight;
}
