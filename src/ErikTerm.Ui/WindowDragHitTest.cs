namespace ErikTerm.Ui;

/// <summary>What a point on a borderless window grabs: nothing, the window itself, or one of its resize borders.</summary>
public enum WindowGrab
{
    None,
    Move,
    ResizeTopLeft,
    ResizeTop,
    ResizeTopRight,
    ResizeRight,
    ResizeBottomRight,
    ResizeBottom,
    ResizeBottomLeft,
    ResizeLeft,
}

/// <summary>
/// Hit testing for a window that draws its own chrome (<c>window_decorations = "NONE"</c>). Such a window has no
/// system frame, so nothing moves or resizes it unless the app names those regions itself:
/// <list type="bullet">
/// <item>a <c>margin</c>-wide border resizes, like the frame a decorated window would have;</item>
/// <item>the chrome the caller reports as empty — the strip of tab bar no tab or button sits on — moves the window,
/// the way a title bar does.</item>
/// </list>
/// Borders win over the movable surface, so the corners of the tab bar still resize rather than drag. The margin is
/// clamped to a third of the window so the two borders can't meet and swallow the middle on a tiny window.
/// </summary>
public static class WindowDragHitTest
{
    public static WindowGrab Classify(float width, float height, float margin, float x, float y, bool onEmptyChrome, bool resizable = true)
    {
        if (width <= 0 || height <= 0) return WindowGrab.None;
        // A point outside the window is not a border: it belongs to whatever is actually there.
        bool inside = x >= 0 && y >= 0 && x < width && y < height;
        if (resizable && margin > 0 && inside && Border(width, height, margin, x, y) is var border && border != WindowGrab.None) return border;
        return onEmptyChrome ? WindowGrab.Move : WindowGrab.None;
    }

    private static WindowGrab Border(float width, float height, float margin, float x, float y)
    {
        float m = MathF.Min(margin, MathF.Min(width, height) / 3);
        bool left = x < m, right = x >= width - m;
        if (y < m) return EdgeOrCorner(left, right, WindowGrab.ResizeTopLeft, WindowGrab.ResizeTopRight, WindowGrab.ResizeTop);
        if (y >= height - m) return EdgeOrCorner(left, right, WindowGrab.ResizeBottomLeft, WindowGrab.ResizeBottomRight, WindowGrab.ResizeBottom);
        if (left) return WindowGrab.ResizeLeft;
        return right ? WindowGrab.ResizeRight : WindowGrab.None;
    }

    private static WindowGrab EdgeOrCorner(bool left, bool right, WindowGrab leftCorner, WindowGrab rightCorner, WindowGrab edge)
    {
        if (left) return leftCorner;
        return right ? rightCorner : edge;
    }
}
