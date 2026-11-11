using ErikTerm.App.Rendering;

namespace ErikTerm.App.Ui;

/// <summary>The container icon: a tab whose pane's shell runs in a container (WindowFeatures/WindowContainers.cs).</summary>
internal static class ContainerIcon
{
    /// <summary>
    /// The cube before the title of a tab whose shell runs in a container (the tab bar, the Ctrl+Tab list), in the
    /// scheme's blue so it follows the theme, a little dimmer when not active. Returns the width it covers.
    /// </summary>
    public static float Draw(DrawList dl, Theme th, float x, float baseline, float px, float lit, float hover, float a)
    {
        float size = MathF.Round(px * 1.05f);
        var g = dl.Atlas.Get(Icons.Container, FontStyle.Regular, size);
        dl.Glyph(Icons.Container, x, baseline, size, th.Info, (0.8f + 0.2f * MathF.Max(lit, hover)) * a);
        return MathF.Max(g.Advance, g.Left + g.Width); // a Nerd Font icon may draw wider than its advance
    }
}
