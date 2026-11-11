namespace ErikTerm.Config;

/// <summary>
/// Color schemes from wezterm-format TOML files: <c>[colors]</c> foreground / background / cursor_bg /
/// cursor_fg / cursor_border / selection_bg / selection_fg / ansi / brights (+ optional split), and
/// <c>[metadata] name</c> (else the file name). Other keys wezterm's files carry (tab_bar, indexed,
/// author, …) are ignored.
/// </summary>
public static class ColorSchemeFiles
{
    private const string Colors = "colors";

    /// <summary>
    /// Where scheme files are looked for, most important first: the config file's directory's
    /// <c>colors/</c>, erikterm's default config directory's <c>colors/</c>
    /// (<c>$XDG_CONFIG_HOME/erikterm/colors</c>; Windows: <c>%APPDATA%\erikterm\colors</c>), and
    /// wezterm's <c>~/.config/wezterm/colors</c> (or <c>$XDG_CONFIG_HOME/wezterm/colors</c>).
    /// </summary>
    public static List<string> DefaultDirectories(string? configPath = null)
    {
        var dirs = new List<string>();
        void Add(string? d)
        {
            if (string.IsNullOrEmpty(d)) return;
            d = Path.GetFullPath(d);
            if (!dirs.Contains(d)) dirs.Add(d);
        }
        if (configPath is not null)
        {
            Add(ColorsNextTo(configPath));
            Add(CatalogDirectory(configPath)); // downloaded (Browse Themes… › Download): after your own files, so those win
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        Add(ErikColorsDirectory());
        Add(Path.Combine(ErikColorsDirectory(), CatalogFolderName));
        Add(Path.Combine(configHome, "wezterm", Colors));
        if (!OperatingSystem.IsWindows()) Add(Path.Combine(home, ".config", "wezterm", Colors));
        return dirs;
    }

    /// <summary>The folder (under a colors directory) the downloaded scheme catalogue goes into.</summary>
    public const string CatalogFolderName = "iTerm2-Color-Schemes";

    /// <summary>The <c>colors/</c> directory next to a config file.</summary>
    private static string ColorsNextTo(string configPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? ".", Colors);

    /// <summary>erikterm's own colors directory: <c>$XDG_CONFIG_HOME/erikterm/colors</c> (Windows: <c>%APPDATA%\erikterm\colors</c>).</summary>
    public static string ErikColorsDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "erikterm", Colors);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        return Path.GetFullPath(Path.Combine(configHome, "erikterm", Colors));
    }

    /// <summary>
    /// Where Browse Themes… › Download puts the iTerm2-Color-Schemes catalogue: its own folder (easy to update or remove)
    /// in the <c>colors/</c> directory next to the config file, normally <c>~/.config/erikterm/colors</c>.
    /// </summary>
    public static string CatalogDirectory(string configPath) => Path.Combine(ColorsNextTo(configPath), CatalogFolderName);

    /// <summary>
    /// Loads every <c>*.toml</c> in the directories that exist. Earlier directories win when two files
    /// use the same name; within a directory files load in name order. Problems go to <paramref name="diagnostics"/>.
    /// </summary>
    public static List<ColorScheme> LoadDirectories(IEnumerable<string> directories, List<string> diagnostics)
    {
        var result = new List<ColorScheme>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string dir in directories)
        {
            if (ListSchemeFiles(dir, diagnostics) is not { } files) continue;
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files)
            {
                if (ReadSchemeFile(file, diagnostics) is { } text && Parse(text, file, diagnostics) is { } scheme && seen.Add(scheme.Name))
                    result.Add(scheme);
            }
        }
        return result;
    }

    /// <summary>The <c>*.toml</c> files of a directory; null when it doesn't exist or can't be read (the latter with a diagnostic).</summary>
    private static string[]? ListSchemeFiles(string dir, List<string> diagnostics)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.toml") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"{dir}: {ex.Message}");
            return null;
        }
    }

    private static string? ReadSchemeFile(string file, List<string> diagnostics)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"{file}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parses one scheme file; null (with a diagnostic) when it isn't usable.</summary>
    public static ColorScheme? Parse(string toml, string path, List<string> diagnostics)
    {
        if (ParseDocument(toml, path, diagnostics) is not { } root) return null;

        string name = SchemeName(root, path, diagnostics);

        if (!root.TryGet(Colors, out var colorsValue, out int colorsLine) || colorsValue is not TomlTable colors)
        {
            diagnostics.Add(colorsLine > 0 ? $"{path}:{colorsLine}: colors: expected a table" : $"{path}: expected a [colors] table");
            return null;
        }

        int before = diagnostics.Count;
        var read = new SchemeColors(colors, path, diagnostics);
        uint? fg = read.Color("foreground"), bg = read.Color("background");
        uint? cursorBg = read.Color("cursor_bg"), cursorFg = read.Color("cursor_fg"), cursorBorder = read.Color("cursor_border");
        uint? selBg = read.Color("selection_bg"), selFg = read.Color("selection_fg"), split = read.Color("split");
        uint[]? ansi = read.List("ansi"), brights = read.List("brights");
        if (diagnostics.Count > before) return null;
        if (fg is null || bg is null)
        {
            diagnostics.Add($"{path}:{colorsLine}: colors: foreground and background are required");
            return null;
        }

        var fallback = ColorSchemes.Erik;
        ansi ??= (uint[])fallback.Ansi.Clone();
        brights ??= (uint[])fallback.Brights.Clone();
        return new ColorScheme(
            name, fg.Value, bg.Value,
            CursorBg: cursorBg ?? cursorBorder ?? fg.Value,
            CursorFg: cursorFg ?? bg.Value,
            SelectionBg: selBg ?? Mix(bg.Value, fg.Value, 0.25f),
            Ansi: ansi, Brights: brights,
            Split: split ?? Mix(bg.Value, fg.Value, 0.2f),
            Accent: ansi[4])
        {
            SelectionFg = selFg,
            CursorBorder = cursorBorder,
            SourcePath = path,
        };
    }

    private static TomlTable? ParseDocument(string toml, string path, List<string> diagnostics)
    {
        try
        {
            return Toml.Parse(toml);
        }
        catch (TomlException ex)
        {
            diagnostics.Add($"{path}: {ex.Message}");
            return null;
        }
    }

    /// <summary><c>metadata.name</c> when it is usable, otherwise the file's name.</summary>
    private static string SchemeName(TomlTable root, string path, List<string> diagnostics)
    {
        if (!root.TryGet("metadata", out var meta, out _) || meta is not TomlTable m || !m.TryGet("name", out var n, out int nameLine))
            return Path.GetFileNameWithoutExtension(path);
        if (n is string s && s.Trim().Length > 0) return s.Trim();
        diagnostics.Add($"{path}:{nameLine}: metadata.name: expected a non-empty string");
        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Reads values from a scheme's <c>[colors]</c> table; problems go to the diagnostics and the value counts as missing.</summary>
    private sealed class SchemeColors(TomlTable colors, string path, List<string> diagnostics)
    {
        public uint? Color(string key)
        {
            if (!colors.TryGet(key, out var v, out int line)) return null;
            if (v is string text && SettingsLoader.TryParseColor(text, out uint rgb)) return rgb;
            diagnostics.Add($"{path}:{line}: colors.{key}: expected a color like \"#rrggbb\"");
            return null;
        }

        public uint[]? List(string key)
        {
            if (!colors.TryGet(key, out var v, out int line)) return null;
            if (v is not List<object> { Count: 8 } list)
            {
                diagnostics.Add($"{path}:{line}: colors.{key}: expected an array of 8 colors");
                return null;
            }
            var result = new uint[8];
            for (int i = 0; i < 8; i++)
            {
                if (list[i] is string text && SettingsLoader.TryParseColor(text, out result[i])) continue;
                diagnostics.Add($"{path}:{line}: colors.{key}: entry {i + 1} is not a color like \"#rrggbb\"");
                return null;
            }
            return result;
        }
    }

    private static uint Mix(uint a, uint b, float t)
    {
        uint Channel(int shift)
        {
            float x = (a >> shift & 0xFF) * (1 - t) + (b >> shift & 0xFF) * t;
            return (uint)Math.Clamp((int)MathF.Round(x), 0, 255) << shift;
        }
        return Channel(16) | Channel(8) | Channel(0);
    }
}
