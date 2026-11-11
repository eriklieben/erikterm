using System.Globalization;
using System.Text;

namespace ErikTerm.App.Ui;

/// <summary>
/// Everything the About screen shows, captured once when it opens (<see cref="AboutProbe"/> gathers it).
/// Plain data + formatting with no native dependencies, so the tests compile it directly.
/// </summary>
internal sealed record AboutInfo
{
    public const string Product = "erikterm";
    public const string Tagline = "A fast, native terminal built in C# for me, Erik. And maybe for you?";
    public const string Copyright = "© 2026 Erik Lieben";

    // Build
    public string Version { get; init; } = "0.0.0";
    public string? Commit { get; init; }
    public bool Dirty { get; init; }
    public string? BuildDate { get; init; }
    public bool NativeAot { get; init; }
    public string Runtime { get; init; } = "";

    // System
    public string Os { get; init; } = "";
    public string Arch { get; init; } = "";
    public long WorkingSetBytes { get; init; }
    public TimeSpan Uptime { get; init; }
    public int ProcessId { get; init; }

    // Display
    public string? SdlVersion { get; init; }
    public string? VideoDriver { get; init; }
    public string? Renderer { get; init; }
    public bool? VSync { get; init; }
    public int DisplayWidth { get; init; }
    public int DisplayHeight { get; init; }
    public float RefreshHz { get; init; }
    public float PixelDensity { get; init; } = 1;
    public float DisplayScale { get; init; } = 1;
    public float WindowOpacity { get; init; } = 1;

    // Text
    public string? FreeTypeVersion { get; init; }
    public int FreeTypeYear { get; init; } = 2026; // for the FTL credit line
    public string? HarfBuzzVersion { get; init; }
    public string FontPath { get; init; } = "";
    public double FontPoints { get; init; }
    public float FontPixels { get; init; }
    public int CellWidth { get; init; }
    public int CellHeight { get; init; }
    public double LineHeight { get; init; } = 1;
    public int FallbackFonts { get; init; }
    public int AtlasEntries { get; init; }
    public float AtlasFill { get; init; }
    public string UnicodeVersion { get; init; } = "";

    // Session
    public int Tabs { get; init; } = 1;
    public int Panes { get; init; } = 1;
    public string ColorScheme { get; init; } = "";
    public string ConfigPath { get; init; } = "";
    public bool ConfigExists { get; init; }
    public string? Home { get; init; }
    /// <summary>Config diagnostics of the last (re)load, font warnings included (palette: Show Config Problems).</summary>
    public int ConfigProblems { get; init; }
    /// <summary>The newest crash log (CrashLog.cs), if any, and when it was written.</summary>
    public string? LastCrashLog { get; init; }
    public DateTime LastCrashTime { get; init; }
    /// <summary>For "N ago" (tests pin it).</summary>
    public DateTime Now { get; init; } = DateTime.Now;

    /// <summary>Shared libraries mapped into the process (full paths, load order).</summary>
    public IReadOnlyList<string> Libraries { get; init; } = [];

    public string CommitText => AboutFormat.CommitText(Commit, Dirty);
    public string VersionLine => AboutFormat.VersionLine(Version, Commit, Dirty);
    public string ConfigDisplayPath => AboutFormat.HomeRelative(ConfigPath, Home);
}

internal enum AboutSectionKind { Build, System, Display, Text, Session }

internal enum AboutValueKind { Normal, Link, Warning, Dim }

internal readonly record struct AboutRow(string Label, string Value, AboutValueKind Kind = AboutValueKind.Normal);

internal sealed record AboutSection(AboutSectionKind Kind, string Title, IReadOnlyList<AboutRow> Rows);

/// <summary>Pure formatting for the About screen and the "Copy diagnostics" text.</summary>
internal static class AboutFormat
{
    public const string MetaCommit = "ErikTerm.Commit";
    public const string MetaDirty = "ErikTerm.Dirty";
    public const string MetaBuildDate = "ErikTerm.BuildDate";
    private const string Unknown = "unknown";

    /// <summary>
    /// Version, commit, dirty flag and build date from the assembly's informational version
    /// ("0.1.0+3f2c…" when the SDK appends a source revision) and our [AssemblyMetadata] stamps.
    /// </summary>
    public static (string Version, string? Commit, bool Dirty, string? BuildDate) ParseBuild(
        string? informationalVersion, IReadOnlyDictionary<string, string?> metadata)
    {
        string info = (informationalVersion ?? "").Trim();
        int plus = info.IndexOf('+');
        string version = plus >= 0 ? info[..plus] : info;
        if (version.Length == 0) version = "0.0.0";

        string? commit = metadata.GetValueOrDefault(MetaCommit) is { Length: > 0 } c ? c.Trim() : null;
        if (commit is null && plus >= 0)
        {
            string rev = info[(plus + 1)..];
            if (rev.Length >= 7 && rev.All(Uri.IsHexDigit)) commit = rev[..7];
        }
        bool dirty = commit is not null && string.Equals(metadata.GetValueOrDefault(MetaDirty), "true", StringComparison.OrdinalIgnoreCase);
        string? date = metadata.GetValueOrDefault(MetaBuildDate) is { Length: > 0 } d ? d : null;
        return (version, commit, dirty, date);
    }

    public static string CommitText(string? commit, bool dirty)
    {
        if (commit is null) return Unknown;
        return dirty ? commit + " (modified)" : commit;
    }

    /// <summary>"0.1.0 (354fb54)", "0.1.0 (354fb54, modified)" or just "0.1.0".</summary>
    public static string VersionLine(string version, string? commit, bool dirty)
    {
        if (commit is null) return version;
        string modified = dirty ? ", modified" : "";
        return $"{version} ({commit}{modified})";
    }

    /// <summary>`erikterm --version`: "erikterm 0.1.0 (354fb54), built 2026-11-07 21:40 UTC".</summary>
    public static string VersionText(string version, string? commit, bool dirty, string? buildDate) =>
        $"erikterm {VersionLine(version, commit, dirty)}" + (buildDate is { Length: > 0 } ? $", built {buildDate}" : "");

    /// <summary>SDL_GetVersion's packed major*1000000 + minor*1000 + micro.</summary>
    public static string SdlVersion(int packed) =>
        packed <= 0 ? Unknown : $"{packed / 1000000}.{packed / 1000 % 1000}.{packed % 1000}";

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        if (u == 0) return $"{bytes} B";
        string format = v >= 100 ? "0" : "0.0";
        return v.ToString(format, CultureInfo.InvariantCulture) + " " + units[u];
    }

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}s";
        if (t.TotalHours < 1) return $"{t.Minutes}m {t.Seconds:00}s";
        if (t.TotalDays < 1) return $"{t.Hours}h {t.Minutes:00}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }

    public static string Refresh(float hz)
    {
        if (hz <= 0) return "";
        return MathF.Abs(hz - MathF.Round(hz)) < 0.05f ? $"{MathF.Round(hz):0} Hz" : hz.ToString("0.##", CultureInfo.InvariantCulture) + " Hz";
    }

    public static string Number(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>"~/.config/…" when the path is under the home directory.</summary>
    public static string HomeRelative(string path, string? home)
    {
        if (string.IsNullOrEmpty(home) || path.Length <= home.Length) return path;
        home = home.TrimEnd('/', '\\');
        if (path.StartsWith(home, StringComparison.Ordinal) && path[home.Length] is '/' or '\\') return "~" + path[home.Length..];
        return path;
    }

    /// <summary>
    /// The &lt;year&gt; for FreeType's required credit line: the release year of the loaded version
    /// (newer than this table: the build year).
    /// </summary>
    public static int FreeTypeYear(int major, int minor, int patch, int fallbackYear) => (major, minor, patch) switch
    {
        (2, 15, _) => 2026,
        (2, 14, _) => 2025,
        (2, 13, >= 3) => 2024,
        (2, 13, _) => 2023,
        (2, 12, _) => 2022,
        (2, 11, _) => 2021,
        (2, 10, >= 3) => 2020,
        (2, 10, _) => 2019,
        (2, < 10, _) => 2018,
        _ => Math.Max(2026, fallbackYear),
    };

    public static string FreeTypeCredit(int year) =>
        $"Portions of this software are copyright © {year} The FreeType Project (www.freetype.org). All rights reserved.";

    // ---- Sections ----------------------------------------------------------------------------

    public static List<AboutSection> Sections(AboutInfo i)
    {
        string fontFile = i.FontPath.Length > 0 ? Path.GetFileName(i.FontPath) : Unknown;
        string display = Unknown;
        if (i.DisplayWidth > 0) display = $"{i.DisplayWidth} × {i.DisplayHeight}" + (i.RefreshHz > 0 ? " @ " + Refresh(i.RefreshHz) : "");
        string vsync = i.VSync switch
        {
            true => " · vsync",
            false => " · no vsync",
            null => "",
        };
        string renderer = (i.Renderer ?? Unknown) + vsync;
        string density = Number(i.PixelDensity) + "×" + (MathF.Abs(i.DisplayScale - i.PixelDensity) > 0.01f ? " · scale " + Number(i.DisplayScale) : "");

        return
        [
            new(AboutSectionKind.Build, "Build",
            [
                new("Version", i.Version),
                new("Commit", i.CommitText, i.Dirty ? AboutValueKind.Warning : AboutValueKind.Normal),
                new("Built", i.BuildDate ?? Unknown),
                new("Compiled", i.NativeAot ? "Native AOT" : "JIT (dotnet)"),
                new("Runtime", i.Runtime),
            ]),
            new(AboutSectionKind.Display, "Display",
            [
                new("SDL", i.SdlVersion ?? Unknown),
                new("Video", i.VideoDriver ?? Unknown),
                new("Renderer", renderer),
                new("Display", display),
                new("Density", density),
                new("Opacity", $"{MathF.Round(i.WindowOpacity * 100):0}%"),
            ]),
            new(AboutSectionKind.System, "System",
            [
                new("OS", i.Os),
                new("Arch", i.Arch),
                new("Memory", Bytes(i.WorkingSetBytes) + " working set"),
                new("Uptime", Duration(i.Uptime)),
                new("Process", $"PID {i.ProcessId}"),
            ]),
            new(AboutSectionKind.Text, "Text",
            [
                new("FreeType", i.FreeTypeVersion ?? Unknown),
                new("HarfBuzz", i.HarfBuzzVersion ?? "not available (no ligatures)", i.HarfBuzzVersion is null ? AboutValueKind.Warning : AboutValueKind.Normal),
                new("Font", fontFile),
                new("Size", $"{Number(i.FontPoints)} pt · {MathF.Round(i.FontPixels):0} px"),
                new("Cell", $"{i.CellWidth} × {i.CellHeight} px · line height {Number(i.LineHeight)}"),
                new("Fallbacks", i.FallbackFonts == 1 ? "1 font" : $"{i.FallbackFonts} fonts"),
                new("Atlas", $"{i.AtlasEntries} entries · {MathF.Round(i.AtlasFill * 100):0}% full"),
                new("Unicode", i.UnicodeVersion),
            ]),
            new(AboutSectionKind.Session, "Session",
            [
                new("Tabs", i.Tabs.ToString(CultureInfo.InvariantCulture)),
                new("Panes", i.Panes.ToString(CultureInfo.InvariantCulture)),
                new("Scheme", i.ColorScheme),
                new("Config", i.ConfigDisplayPath + (i.ConfigExists ? "" : " (not created yet)"), AboutValueKind.Link),
                .. ProblemRows(i),
            ]),
        ];
    }

    /// <summary>Config problems and the last crash log, only when there are any.</summary>
    private static IEnumerable<AboutRow> ProblemRows(AboutInfo i)
    {
        if (i.ConfigProblems > 0)
            yield return new("Problems", (i.ConfigProblems == 1 ? "1 config problem" : $"{i.ConfigProblems} config problems") + " · palette: Show Config Problems", AboutValueKind.Warning);
        if (i.LastCrashLog is { } log)
        {
            var age = i.Now - i.LastCrashTime;
            yield return new("Crash log", HomeRelative(log, i.Home) + " · " + (age < TimeSpan.FromMinutes(1) ? "just now" : Duration(age) + " ago"), AboutValueKind.Warning);
        }
    }

    /// <summary>The plain-text block "Copy diagnostics" puts on the clipboard.</summary>
    public static string Diagnostics(AboutInfo info)
    {
        var sb = new StringBuilder();
        sb.Append(AboutInfo.Product).Append(' ').Append(info.VersionLine).Append('\n');
        var sections = Sections(info);
        int labelW = LabelWidth(sections);
        foreach (var s in sections) AppendSection(sb, info, s, labelW);
        if (info.FontPath.Length > 0) sb.Append("\nFont file\n  ").Append(info.FontPath).Append('\n');
        if (info.Libraries.Count > 0)
        {
            sb.Append("\nLoaded libraries\n");
            foreach (var lib in info.Libraries) sb.Append("  ").Append(lib).Append('\n');
        }
        return sb.ToString();
    }

    private static int LabelWidth(List<AboutSection> sections)
    {
        int labelW = 0;
        foreach (var s in sections) foreach (var r in s.Rows) labelW = Math.Max(labelW, r.Label.Length);
        return labelW;
    }

    private static void AppendSection(StringBuilder sb, AboutInfo info, AboutSection s, int labelW)
    {
        sb.Append('\n').Append(s.Title).Append('\n');
        foreach (var r in s.Rows)
        {
            string value = r.Value;
            if (r.Kind == AboutValueKind.Link && r.Label == "Config") value = info.ConfigPath + (info.ConfigExists ? "" : " (not created yet)");
            sb.Append("  ").Append(r.Label.PadRight(labelW + 2)).Append(value).Append('\n');
        }
    }
}
