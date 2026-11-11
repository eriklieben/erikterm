namespace ErikTerm.Config;

public sealed record ColorScheme(
    string Name,
    uint Foreground,
    uint Background,
    uint CursorBg,
    uint CursorFg,
    uint SelectionBg,
    uint[] Ansi,
    uint[] Brights,
    uint Split,
    uint Accent)
{
    /// <summary>Selected text color from a scheme file (null: keep the text's own color).</summary>
    public uint? SelectionFg { get; init; }
    /// <summary>Cursor outline color from a scheme file.</summary>
    public uint? CursorBorder { get; init; }
    /// <summary>The scheme file it came from; null for a built-in scheme.</summary>
    public string? SourcePath { get; init; }
}

/// <summary>Built-in schemes, selectable with <c>color_scheme = "..."</c> (wezterm-style names).</summary>
public static class ColorSchemes
{
    public static readonly ColorScheme Erik = new(
        "erik", 0xD4D7DE, 0x16181D, 0xF5C26B, 0x16181D, 0x3A4150,
        [0x23262E, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xC8CCD4],
        [0x5C6370, 0xEF7B85, 0xA9D48A, 0xF0CF8F, 0x7CC0F5, 0xD391EA, 0x6CCAD6, 0xFFFFFF],
        Split: 0x3A4150, Accent: 0x61AFEF);

    public static readonly ColorScheme CatppuccinMacchiato = new(
        "Catppuccin Macchiato", 0xCAD3F5, 0x24273A, 0xF4DBD6, 0x24273A, 0x5B6078,
        [0x494D64, 0xED8796, 0xA6DA95, 0xEED49F, 0x8AADF4, 0xF5BDE6, 0x8BD5CA, 0xB8C0E0],
        [0x5B6078, 0xED8796, 0xA6DA95, 0xEED49F, 0x8AADF4, 0xF5BDE6, 0x8BD5CA, 0xA5ADCB],
        Split: 0x494D64, Accent: 0xB7BDF8);

    public static readonly ColorScheme CatppuccinMocha = new(
        "Catppuccin Mocha", 0xCDD6F4, 0x1E1E2E, 0xF5E0DC, 0x1E1E2E, 0x585B70,
        [0x45475A, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xBAC2DE],
        [0x585B70, 0xF38BA8, 0xA6E3A1, 0xF9E2AF, 0x89B4FA, 0xF5C2E7, 0x94E2D5, 0xA6ADC8],
        Split: 0x45475A, Accent: 0xB4BEFE);

    public static readonly ColorScheme TokyoNight = new(
        "Tokyo Night", 0xC0CAF5, 0x1A1B26, 0xC0CAF5, 0x1A1B26, 0x33467C,
        [0x15161E, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xA9B1D6],
        [0x414868, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xC0CAF5],
        Split: 0x3B4261, Accent: 0x7AA2F7);

    public static readonly ColorScheme[] All = [Erik, CatppuccinMacchiato, CatppuccinMocha, TokyoNight];

    /// <summary>Case-insensitive; also accepts wezterm's "(Gogh)" / "(base16)" suffixed variants.</summary>
    public static ColorScheme? Find(string name)
    {
        string n = name.Trim();
        int paren = n.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) n = n[..paren];
        return All.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Schemes from files (<see cref="ColorSchemeFiles"/>) first — exact name, case-insensitive — then the built-ins.</summary>
    public static ColorScheme? Find(string name, IReadOnlyList<ColorScheme> custom) =>
        custom.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Find(name);

    /// <summary>Custom schemes followed by the built-ins they don't shadow.</summary>
    public static List<ColorScheme> WithCustom(IReadOnlyList<ColorScheme> custom) =>
        [.. custom, .. All.Where(b => !custom.Any(c => c.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase)))];
}
