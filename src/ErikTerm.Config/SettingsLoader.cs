using System.Globalization;

namespace ErikTerm.Config;

/// <summary>
/// Finding, loading and parsing the config file: the top-level settings (<see cref="SettingRules"/>), then the tables
/// (<see cref="SettingTables"/>), each value read and checked by a <see cref="ValueReader"/>; plus the helpers the
/// rest of erikterm shares (home expansion, colors, "did you mean").
/// </summary>
public static class SettingsLoader
{
    public const string FileName = "erikterm.toml";

    /// <summary>The final file a symlinked config resolves to, or null when it isn't a link (or the link is broken).</summary>
    public static string? ConfigLinkTarget(string configPath)
    {
        try
        {
            var info = new FileInfo(configPath);
            if (info.LinkTarget is null) return null;
            return info.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true } final ? final.FullName : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// --config-file, then $ERIKTERM_CONFIG_FILE, then $XDG_CONFIG_HOME/erikterm/erikterm.toml, then ~/.erikterm.toml.
    /// On Windows the last two are replaced by %APPDATA%\erikterm\erikterm.toml.
    /// </summary>
    public static string DefaultPath(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath)) return ExpandHome(explicitPath);
        if (Environment.GetEnvironmentVariable("ERIKTERM_CONFIG_FILE") is { Length: > 0 } env) return ExpandHome(env);
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "erikterm", FileName);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(home, ".config");
        string xdg = Path.Combine(configHome, "erikterm", FileName);
        string dot = Path.Combine(home, "." + FileName);
        return !File.Exists(xdg) && File.Exists(dot) ? dot : xdg;
    }

    /// <summary>
    /// The settings a live reload should apply: the new ones, or — when the file has a syntax error (half saved, an
    /// autosave mid-edit) — <paramref name="current"/>, so every window keeps its font, colors and keys. Kept says which.
    /// </summary>
    public static (Settings Settings, bool Kept) Reloaded(Settings current, LoadResult result) =>
        result.SyntaxError ? (current, true) : (result.Settings, false);

    public static LoadResult Load(string path, LoadOptions? options = null)
    {
        var schemeDiagnostics = new List<string>();
        if (options is { SchemeDirectories.Count: > 0 })
            options = options with { Schemes = ColorSchemeFiles.LoadDirectories(options.SchemeDirectories, schemeDiagnostics) };
        LoadResult result;
        if (!File.Exists(path))
            result = Parse("", path, options) with { Path = null };
        else
        {
            try
            {
                result = Parse(File.ReadAllText(path), path, options);
            }
            catch (IOException ex)
            {
                result = Parse("", path, options) with { Diagnostics = [$"{path}: {ex.Message}"] };
            }
        }
        return schemeDiagnostics.Count == 0 ? result : result with { Diagnostics = [.. schemeDiagnostics, .. result.Diagnostics] };
    }

    /// <summary>
    /// Parses settings. A syntax error yields defaults plus the error; a bad individual value is
    /// reported and skipped, so one typo doesn't throw away the rest of the file.
    /// </summary>
    public static LoadResult Parse(string toml, string path = FileName, LoadOptions? options = null)
    {
        var diagnostics = new List<string>();
        var custom = options?.Schemes ?? [];
        TomlTable root;
        bool syntaxError = false;
        try
        {
            root = Toml.Parse(toml);
        }
        catch (TomlException ex)
        {
            // A syntax error throws the file away; command-line overrides still apply over the defaults.
            diagnostics.Add($"{path}: {ex.Message}");
            root = new TomlTable();
            syntaxError = true;
        }
        ConfigOverrides.ApplyTo(root, options?.Overrides);

        var s = SettingRules.Apply(root, new ValueReader(root, path, diagnostics), custom, path, diagnostics);
        s = SettingTables.Read(root, s, path, diagnostics);
        return new LoadResult(s, path, diagnostics) { CustomSchemes = custom, SyntaxError = syntaxError };
    }

    /// <summary>" — did you mean \"color_scheme\"?" for a near miss (a typo or two), else "".</summary>
    public static string DidYouMean(string key, IReadOnlyList<string> known)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        string lower = key.ToLowerInvariant().Replace('-', '_');
        foreach (var candidate in known)
        {
            int d = EditDistance(lower, candidate);
            if (d < bestDistance) { bestDistance = d; best = candidate; }
        }
        int allowed = Math.Max(1, Math.Min(3, key.Length / 4));
        return best is not null && bestDistance <= allowed ? $" — did you mean \"{best}\"?" : "";
    }

    /// <summary>Optimal string alignment distance: insertions, deletions, substitutions and swapped neighbours cost 1.</summary>
    internal static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }

    public static string ExpandHome(string p) =>
        ExpandHome(p, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows());

    /// <summary>"~" and "~/..." → home; on Windows also "~\...". (A backslash is an ordinary filename character elsewhere.)</summary>
    public static string ExpandHome(string p, string home, bool windows) =>
        p == "~" || p.StartsWith("~/", StringComparison.Ordinal) || (windows && p.StartsWith("~\\", StringComparison.Ordinal))
            ? home + p[1..]
            : p;

    /// <summary>Parses #rrggbb / #rgb / rgb:rr/gg/bb.</summary>
    public static bool TryParseColor(string text, out uint rgb)
    {
        rgb = 0;
        string t = text.Trim();
        if (t.StartsWith('#')) return TryParseHashColor(t[1..], out rgb);
        return t.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase) && TryParseRgbColor(t[4..], out rgb);
    }

    private static bool TryParseHashColor(string hex, out uint rgb)
    {
        rgb = 0;
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb);
    }

    private static bool TryParseRgbColor(string channels, out uint rgb)
    {
        rgb = 0;
        var parts = channels.Split('/');
        if (parts.Length != 3) return false;
        uint v = 0;
        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 4 || !uint.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint x)) return false;
            uint scaled = part.Length switch
            {
                1 => x * 17,
                2 => x,
                _ => x >> ((part.Length - 2) * 4),
            };
            v = v << 8 | scaled;
        }
        rgb = v;
        return true;
    }

    /// <summary>Every top-level setting <see cref="Parse"/> understands.</summary>
    public static readonly string[] KnownKeys = SettingRules.KnownKeys;
}
