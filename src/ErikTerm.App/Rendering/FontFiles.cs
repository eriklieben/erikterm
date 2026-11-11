using System.Globalization;
using System.Text;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Font file names as the locators hand them to <see cref="FontSet"/>. A face of a collection other than the first is
/// written <c>file.ttc#N</c> (macOS ships Menlo, Apple Color Emoji, PingFang … as .ttc files with several faces); a
/// path that exists as it is is always taken literally. Plus the pure parts of the macOS font scan (MacFontLocator).
/// </summary>
internal static class FontFiles
{
    private static readonly string[] ScalableExtensions = [".ttf", ".otf", ".ttc", ".otc", ".dfont"];

    /// <summary>"file#N" for face N &gt; 0 of a collection, else the file.</summary>
    public static string Join(string file, int faceIndex) =>
        faceIndex > 0 ? file + "#" + faceIndex.ToString(CultureInfo.InvariantCulture) : file;

    /// <summary>The file and face index of a locator path ("Menlo.ttc#1" → Menlo.ttc, 1); a path that exists is face 0.</summary>
    public static (string File, int FaceIndex) Split(string path, Func<string, bool>? exists = null)
    {
        int hash = path.LastIndexOf('#');
        if (hash <= 0 || hash == path.Length - 1) return (path, 0);
        var digits = path.AsSpan(hash + 1);
        foreach (char c in digits)
            if (!char.IsAsciiDigit(c)) return (path, 0);
        if ((exists ?? File.Exists)(path)) return (path, 0); // a real file name with '#' in it
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < 0x10000
            ? (path[..hash], index)
            : (path, 0);
    }

    /// <summary>
    /// A fontconfig (file, index) pair as a locator path. fontconfig puts a variable font's named instance in the upper
    /// 16 bits of the index; FontSet picks instances itself, so only the face (lower 16 bits) is kept.
    /// </summary>
    public static string FromFontconfig(string file, string index) =>
        int.TryParse(index.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? Join(file, i & 0xFFFF) : file;

    /// <summary>
    /// fc-list output in the form <c>FILE#INDEX\tCHARSET</c> rewritten for <see cref="FontCoverage.Parse"/>: the key
    /// becomes the locator path, so a collection's faces (and a variable font's instances, merged) stay apart.
    /// </summary>
    public static string NormalizeFontconfigList(string fcList)
    {
        var sb = new StringBuilder(fcList.Length);
        foreach (var line in fcList.Split('\n'))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            string key = line[..tab];
            int hash = key.LastIndexOf('#');
            string path = hash > 0 ? FromFontconfig(key[..hash], key[(hash + 1)..]) : key;
            sb.Append(path).Append(line, tab, line.Length - tab).Append('\n');
        }
        return sb.ToString();
    }

    public static bool IsScalableFontFile(string path) =>
        ScalableExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    // ---- macOS ------------------------------------------------------------------------------------------

    /// <summary>Where macOS keeps fonts, user's first (so a font installed twice resolves to the user's copy).</summary>
    public static string[] MacFontDirectories(string home) =>
    [
        Path.Combine(home, "Library", "Fonts"),
        "/Library/Fonts",
        "/System/Library/Fonts",            // includes Supplemental/ (scanned recursively)
        "/Network/Library/Fonts",
    ];

    /// <summary>Homebrew's fontconfig tools, if installed (Apple silicon prefix first): the directory, or null.</summary>
    public static string? MacFontconfigDirectory(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        return new[] { "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin" }
            .FirstOrDefault(dir => exists(Path.Combine(dir, "fc-match")) && exists(Path.Combine(dir, "fc-list")));
    }

    /// <summary>
    /// The scalable font files under <paramref name="directories"/>, each once, in directory order.
    /// <paramref name="listFiles"/> lists a directory recursively (a fake in tests).
    /// </summary>
    public static List<string> MacFontFiles(IEnumerable<string> directories, Func<string, IEnumerable<string>> listFiles)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<string>();
        foreach (var dir in directories)
        {
            IEnumerable<string> list;
            try { list = listFiles(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            files.AddRange(list.Where(file => IsScalableFontFile(file) && seen.Add(file)));
        }
        return files;
    }

    /// <summary>Lowercase letters and digits only: "JetBrains Mono" and "JetBrainsMono-Bold.ttf" compare by this.</summary>
    public static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Where(char.IsLetterOrDigit)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Whether <paramref name="file"/>'s name suggests it holds <paramref name="family"/> (its squashed name starts with
    /// the squashed family: "SF-Mono-Bold.otf" for "SF Mono"), so a lookup can read those files before all the others.
    /// </summary>
    public static bool NameSuggests(string file, string family)
    {
        string f = Squash(family);
        return f.Length > 0 && Squash(Path.GetFileNameWithoutExtension(file)).StartsWith(f, StringComparison.Ordinal);
    }

    /// <summary>The <c>font</c> setting's default (Settings.Font); not installed on a stock Mac.</summary>
    public const string DefaultFamily = "JetBrains Mono";

    /// <summary>Monospace families tried, in order, when the configured font is missing (macOS).</summary>
    public static readonly string[] MacMonospaceFamilies = ["SF Mono", "Menlo", "Monaco", "Courier New"];

    /// <summary>Tried after the user's font_fallback on macOS: nerd font icons, symbols, emoji, CJK.</summary>
    public static readonly string[] MacSymbolFallbackFamilies =
        ["Symbols Nerd Font Mono", "Apple Symbols", "Apple Color Emoji", "Hiragino Sans", "PingFang SC", "Apple SD Gothic Neo"];

    /// <summary>Broad-coverage system fonts walked after the above (macOS), and preferred when several cover a code point.</summary>
    public static readonly string[] MacBroadFallbackFamilies =
    [
        "Menlo", "Apple Symbols", "STIX Two Math", "Arial Unicode MS", "Hiragino Sans", "PingFang SC", "PingFang TC",
        "Apple SD Gothic Neo", "Kohinoor Devanagari", "Noto Sans Kannada", "Geeza Pro", "Arial Hebrew", "Thonburi",
        "Lucida Grande", "Helvetica", "Times New Roman",
    ];
}
