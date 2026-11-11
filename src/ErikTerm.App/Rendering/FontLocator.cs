using System.Collections.Concurrent;
using System.Diagnostics;

namespace ErikTerm.App.Rendering;

/// <summary>Maps font family names to font files. One implementation per platform.</summary>
internal interface IFontLocator
{
    /// <summary>The file for exactly this family in this style, or null if it isn't installed.</summary>
    string? Find(string family, FontStyle style);

    /// <summary>The platform's default monospace font, used when the configured family is missing.</summary>
    string? DefaultMonospace();

    /// <summary>Families tried after the user's font_fallback: symbols, nerd font icons, CJK.</summary>
    IReadOnlyList<string> SymbolFallbackFamilies { get; }

    /// <summary>Further fallback files for <paramref name="family"/>, best first.</summary>
    IEnumerable<string> FallbackFiles(string family);

    /// <summary>
    /// Last resort: any installed font file that covers <paramref name="codepoint"/>. Slow (fc-match: a process);
    /// FontSet only calls it through <see cref="FallbackResolver"/>, off the UI thread.
    /// </summary>
    string? FindForCodepoint(int codepoint);

    /// <summary>Which file covers which code points, read once (slow; off the UI thread). Null if unavailable.</summary>
    FontCoverage? BuildCoverage() => null;
}

internal static class FontLocator
{
    private static IFontLocator? _current;

    public static IFontLocator Current => _current ??= Create();

    /// <summary>
    /// Windows: the registry. macOS: Homebrew's fontconfig when it is installed (its tools by full path — an app started
    /// from the Dock doesn't have Homebrew on PATH), else a FreeType scan of the font directories. Elsewhere: fontconfig.
    /// </summary>
    private static IFontLocator Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsFontLocator();
        if (OperatingSystem.IsMacOS())
            return FontFiles.MacFontconfigDirectory() is { } fc ? new FontconfigLocator(fc, macOS: true) : new MacFontLocator();
        return new FontconfigLocator();
    }
}

/// <summary>
/// Linux (and other fontconfig systems, Homebrew's on macOS): shells out to the `fc-match` CLI (avoids binding
/// fontconfig). On macOS the face index goes with the file (fonts there are mostly .ttc collections: "Menlo.ttc#1" is
/// Menlo Bold), and the fallback families are Apple's.
/// </summary>
internal sealed class FontconfigLocator : IFontLocator
{
    private readonly string _fcMatch, _fcList;
    private readonly bool _macOS;
    private readonly ConcurrentDictionary<string, Task<string?>> _lookups = new();

    /// <param name="toolDirectory">Where fc-match and fc-list are (null: on PATH).</param>
    /// <param name="macOS">Face indexes, Apple fallback families and monospace defaults.</param>
    public FontconfigLocator(string? toolDirectory = null, bool macOS = false)
    {
        _fcMatch = toolDirectory is null ? "fc-match" : Path.Combine(toolDirectory, "fc-match");
        _fcList = toolDirectory is null ? "fc-list" : Path.Combine(toolDirectory, "fc-list");
        _macOS = macOS;
        SymbolFallbackFamilies = macOS ? FontFiles.MacSymbolFallbackFamilies
            : ["Symbols Nerd Font Mono", "Noto Sans Symbols 2", "Noto Sans CJK JP", "DejaVu Sans"];
    }

    public IReadOnlyList<string> SymbolFallbackFamilies { get; }

    /// <summary>The file part of an fc-match format: with the face index on macOS.</summary>
    private string FileFormat => _macOS ? "%{file}#%{index}" : "%{file}";

    public string? Find(string family, FontStyle style) => MatchFamily(family, style switch
    {
        FontStyle.Bold => ":bold",
        FontStyle.Italic => ":italic",
        FontStyle.BoldItalic => ":bold:italic",
        _ => "",
    });

    public string? DefaultMonospace()
    {
        if (_macOS)
            foreach (var family in FontFiles.MacMonospaceFamilies)
                if (Find(family, FontStyle.Regular) is { } path) return path;
        return Match("monospace");
    }

    public IEnumerable<string> FallbackFiles(string family) => MatchSorted(family);

    public string? FindForCodepoint(int codepoint) => Match($":charset={codepoint:x}");

    public FontCoverage? BuildCoverage()
    {
        if (FcRun(_fcList, ["--format", FileFormat + "\t%{charset}\n"]) is not { } output) return null;
        return FontCoverage.Parse(_macOS ? FontFiles.NormalizeFontconfigList(output) : output);
    }

    private string? MatchFamily(string family, string styleSuffix)
    {
        var output = FcMatch(["-f", "%{family}\t" + FileFormat, family + styleSuffix]);
        if (output is null) return null;
        var parts = output.Split('\t');
        if (parts.Length != 2) return null;
        // fc-match always returns *something*; only accept it if it is the family we asked for.
        bool same = parts[0].Split(',').Any(f => f.Trim().Equals(family, StringComparison.OrdinalIgnoreCase));
        string? path = Located(parts[1]);
        return same ? path : null;
    }

    private string? Match(string pattern) => FcMatch(["-f", FileFormat, pattern]) is { } output ? Located(output) : null;

    /// <summary>fc-match's file (macOS: file#index) as a locator path, if the file exists.</summary>
    private string? Located(string output)
    {
        if (!_macOS) return File.Exists(output) ? output : null;
        string path = output.Trim('\n');
        int hash = path.LastIndexOf('#');
        if (hash > 0) path = FontFiles.FromFontconfig(path[..hash], path[(hash + 1)..]);
        return File.Exists(FontFiles.Split(path).File) ? path : null;
    }

    private IEnumerable<string> MatchSorted(string pattern) =>
        (FcMatch(["-s", "-f", FileFormat + "\n", pattern]) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => _macOS ? Located(p) : p)
            .OfType<string>()
            .Where(p => p.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                || (_macOS && FontFiles.IsScalableFontFile(FontFiles.Split(p).File)));

    /// <summary>A lookup's answer stays until <see cref="ForgetLookups"/>: each costs a process, and every new window asks again.</summary>
    private string? FcMatch(string[] args) =>
        _lookups.GetOrAdd(string.Join('\0', args), _ => Task.Run(() => FcRun(_fcMatch, args))).GetAwaiter().GetResult();

    /// <summary>Starts the lookups a first window needs for <paramref name="family"/>, so they run alongside the window's setup.</summary>
    public void Prefetch(string family)
    {
        foreach (var style in (ReadOnlySpan<FontStyle>)[FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.BoldItalic])
        {
            var current = style;
            Task.Run(() => Find(family, current));
        }
        foreach (var fallback in SymbolFallbackFamilies) Task.Run(() => Find(fallback, FontStyle.Regular));
        Task.Run(() => MatchSorted(family).ToList());
    }

    /// <summary>The installed fonts may have changed: ask fontconfig again from now on.</summary>
    public void ForgetLookups() => _lookups.Clear();

    private static string? FcRun(string tool, string[] args)
    {
        try
        {
            if (Integrations.ToolRunner.ProgramPath(tool) is not { } program) return null;
            var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // fontconfig tools not installed
        }
    }
}
