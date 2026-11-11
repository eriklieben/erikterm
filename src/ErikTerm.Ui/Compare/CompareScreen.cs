namespace ErikTerm.Ui.Compare;

/// <summary>One side of a compare: what to call it and its content, or why there's nothing to show.
/// <see cref="Absent"/>: the file doesn't exist on this side (added or deleted), so an empty document, not an empty file.</summary>
public sealed record CompareSide(string Title, string Subtitle, TextDocument? Document, string? Error = null, bool Absent = false)
{
    public static CompareSide Failed(string title, string error) => new(title, "", null, error);
}

public enum CompareFilter { All, Differences, Context }

/// <summary>
/// Side-by-side compare of two files: a toolbar and footer around a <see cref="ComparePane"/>. Pure: loading and
/// recomputation run through an <see cref="IBackground"/> so input stays responsive for the largest files.
/// </summary>
public sealed class CompareScreen : ITuiScreen
{
    private readonly IBackground _background;
    private readonly Func<CompareSide> _loadLeft, _loadRight;
    private readonly ComparePane _pane;
    private TuiTheme _theme = TuiTheme.Dark;

    public CompareScreen(IBackground background, Func<CompareSide> loadLeft, Func<CompareSide> loadRight)
    {
        _background = background;
        _loadLeft = loadLeft;
        _loadRight = loadRight;
        _pane = new ComparePane(background);
    }

    public bool Done { get; private set; }
    public int ExitCode => 0;
    public NoiseRules Rules => _pane.Rules;
    public CompareFilter Filter => _pane.Filter;
    public CompareResult? Result => _pane.Result;

    public void OnThemeColor(uint backgroundColor)
    {
        _theme = TuiTheme.ForBackground(backgroundColor);
        _pane.Theme = _theme;
    }

    /// <summary>Starts loading; the result arrives through the background runner.</summary>
    public void Start() =>
        _background.Run(() => (Left: _loadLeft(), Right: _loadRight()), sides => _pane.Show(sides.Left, sides.Right));

    public void OnKey(KeyEvent key)
    {
        if (_pane.OnKey(key)) return;
        if (key.Key == TuiKey.Esc || key.Is('q') || (key.Key == TuiKey.Char && key.Ctrl && key.Rune == 'c')) Done = true;
    }

    public void OnMouse(MouseEvent mouse, Frame? lastFrame)
    {
        if (lastFrame is null) return;
        switch (mouse.Kind)
        {
            case MouseKind.WheelUp: _pane.Scroll(-3); return;
            case MouseKind.WheelDown: _pane.Scroll(3); return;
            case MouseKind.Press when mouse.Button == 0:
                if (lastFrame.HitAt(mouse.X, mouse.Y) is { } hit) _pane.OnHit(hit, mouse);
                return;
        }
    }

    public void Render(Frame frame)
    {
        frame.Clear(_theme.Text, _theme.Base);
        _pane.DrawToolbar(frame, 0, 0, frame.Width);
        DrawFooter(frame);
        _pane.Render(frame, 0, 1, frame.Width, frame.Height - 2);
        _pane.RenderPopups(frame, 1);
    }

    private void DrawFooter(Frame f)
    {
        int y = f.Height - 1;
        f.Fill(0, y, f.Width, 1, _theme.Subtext, _theme.Crust);
        int x = 1;
        foreach (string item in new[] { "] / [ next / previous difference", "d view", "i ignore rules", "h/l pan", "q close" })
        {
            if (x + item.Length + 2 >= f.Width) break;
            x = f.Text(x, y, f.Width - x, item, _theme.Subtext, _theme.Crust) + 2;
        }
    }
}
