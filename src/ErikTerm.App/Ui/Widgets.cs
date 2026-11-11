using ErikTerm.App.Rendering;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>Modifiers held when a palette entry was chosen (Shift+Enter, Ctrl+Enter, Alt+Enter, or with a click).</summary>
[Flags]
internal enum RunModifiers { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }

/// <summary>Something the user can run from the command palette or a menu.</summary>
internal sealed class Command
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public int Icon { get; init; } = Icons.Terminal;
    /// <summary>The icon's color (a swatch: Tab Color entries); null = the palette's own.</summary>
    public uint? IconColor { get; init; }
    public ActionSpec? Action { get; init; }
    public Action? Run { get; init; }
    /// <summary>Instead of <see cref="Run"/>: told which modifiers were held (e.g. Shift+Enter = run it too).</summary>
    public Action<RunModifiers>? RunWith { get; init; }
    /// <summary>Called while the item is highlighted (live preview, e.g. color schemes).</summary>
    public Action? Preview { get; init; }
    public Func<bool>? IsEnabled { get; init; }
    public string[] Shortcut { get; set; } = [];

    /// <summary>Muted text after the title (a snippet's description, a command's directory); searched too.</summary>
    public string? Detail { get; init; }

    public string SearchText => Detail is null ? $"{Category}: {Title}" : $"{Category}: {Title}  {Detail}";
    public bool Enabled => IsEnabled?.Invoke() ?? true;
}

internal static class Widgets
{
    /// <summary>A panel's width: what it wants, at least its minimum, but never wider than the window allows (unlike Math.Clamp, fine when they cross).</summary>
    public static float FitWidth(float want, float min, float max) => MathF.Max(1, MathF.Min(MathF.Max(want, min), max));

    /// <summary>Splits a chord like "Ctrl+Shift+P" into keycap labels.</summary>
    public static string[] KeycapLabels(KeyChord chord) => chord.ToString().Split('+');

    /// <summary>Draws keycaps right-aligned ending at <paramref name="right"/>, vertically centred on <paramref name="centerY"/>. Returns the total width.</summary>
    public static float Keycaps(DrawList dl, Theme th, string[] keys, float right, float centerY, float alpha, bool emphasized = false)
    {
        if (keys.Length == 0) return 0;
        float px = th.UiPx * 0.78f;
        var m = dl.MetricsFor(px);
        float padX = th.S(5), h = MathF.Round(m.Height + th.S(6)), gap = th.S(3);

        float total = 0;
        Span<float> widths = stackalloc float[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            widths[i] = MathF.Max(h, dl.Measure(keys[i], px) + 2 * padX);
            total += widths[i] + (i > 0 ? gap : 0);
        }

        float x = right - total;
        float top = MathF.Round(centerY - h / 2);
        uint fg = emphasized ? th.PanelFg : th.Muted;
        for (int i = 0; i < keys.Length; i++)
        {
            var r = new RectF(x, top, widths[i], h);
            dl.RoundRect(r.Offset(0, th.S(1)), th.S(4), 0x000000, 0.25f * alpha);          // bottom edge
            dl.Panel(r, th.S(4), Colors.Mix(th.PanelBg, th.PanelFg, 0.10f), alpha, th.Border, alpha);
            float tw = dl.Measure(keys[i], px);
            dl.Text(keys[i], x + (widths[i] - tw) / 2, top + (h - m.Height) / 2 + m.Ascender, px, fg, alpha);
            x += widths[i] + gap;
        }
        return total;
    }

    /// <summary>Floating panel chrome: drop shadow, 1px border, fill.</summary>
    public static void FloatingPanel(DrawList dl, Theme th, RectF r, float radius, float alpha)
    {
        dl.Shadow(r.Offset(0, th.S(10)), radius, th.S(28), 0.50f * alpha);
        dl.Shadow(r.Offset(0, th.S(2)), radius, th.S(6), 0.30f * alpha);
        dl.Panel(r, radius, th.PanelBg, 0.97f * alpha, Colors.Mix(th.PanelBg, th.PanelFg, 0.16f), alpha);
    }

    /// <summary>Text vertically centred in a row.</summary>
    public static float Baseline(DrawList dl, float px, float top, float height)
    {
        var m = dl.MetricsFor(px);
        return MathF.Round(top + (height - m.Height) / 2 + m.Ascender);
    }
}
