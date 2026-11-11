namespace ErikTerm.Ui;

public readonly record struct RectI(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public bool Contains(float px, float py) => px >= X && px < Right && py >= Y && py < Bottom;
    public RectI Inset(int l, int t, int r, int b) => new(X + l, Y + t, Math.Max(0, W - l - r), Math.Max(0, H - t - b));
}

public readonly record struct RectF(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public bool Contains(float px, float py) => px >= X && px < Right && py >= Y && py < Bottom;
    public RectF Inset(float d) => new(X + d, Y + d, Math.Max(0, W - 2 * d), Math.Max(0, H - 2 * d));
    public RectF Offset(float dx, float dy) => this with { X = X + dx, Y = Y + dy };
    public static implicit operator RectF(RectI r) => new(r.X, r.Y, r.W, r.H);

    public static RectF Lerp(RectF a, RectF b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.W + (b.W - a.W) * t, a.H + (b.H - a.H) * t);
}

public enum Direction { Left, Right, Up, Down }

/// <summary>Horizontal = panes side by side (a vertical divider); Vertical = stacked.</summary>
public enum SplitAxis { Horizontal, Vertical }
