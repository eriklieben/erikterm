using ErikTerm.Config;

namespace ErikTerm.App.Tabs;

/// <summary>
/// What a tab's color resolved to against a scheme. Named colors follow the theme, so the result is kept until the color
/// or the scheme changes: the tab bar asks every frame and this doesn't allocate. Read on the main thread only.
/// </summary>
internal sealed class TabColorCache
{
    private string? _name;
    private ColorScheme? _scheme;
    private uint? _resolved;

    /// <summary>The color the tab shows: its own, else its program's (OSC 6), else none.</summary>
    public uint? Shown(string? color, uint? programColor, ColorScheme scheme)
    {
        if (color is null) return programColor;
        if (!ReferenceEquals(_name, color) || !ReferenceEquals(_scheme, scheme))
        {
            _name = color;
            _scheme = scheme;
            _resolved = TabColors.Resolve(color, scheme);
        }
        return _resolved ?? programColor;
    }
}
