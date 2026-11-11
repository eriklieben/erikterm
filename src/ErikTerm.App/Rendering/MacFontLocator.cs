using ErikTerm.App.Native;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Font discovery on macOS without fontconfig (when Homebrew's isn't installed) and without CoreText: the font
/// directories (<see cref="FontFiles.MacFontDirectories"/>, .ttc collections included) are read with FreeType, which is
/// loaded anyway. A family lookup first opens only the files whose name suggests the family ("JetBrainsMono-Bold.ttf"
/// for "JetBrains Mono"); only a family that isn't found that way — and isn't one of the built-in fallbacks — costs a
/// pass over every file. The coverage index (<see cref="BuildCoverage"/>, off the UI thread) reads every face's charmap.
/// Faces past the first of a collection are named <c>file.ttc#N</c> (<see cref="FontFiles"/>).
/// </summary>
#pragma warning disable S6640 // faces are opened and read through FreeType's pointer API
internal sealed unsafe class MacFontLocator : IFontLocator
#pragma warning restore S6640
{
    private readonly string[] _directories;
    private readonly Func<string, IEnumerable<string>> _listFiles;
    private readonly Lock _sync = new();
    private List<string>? _files;
    private readonly HashSet<string> _scanned = new(StringComparer.Ordinal);
    // family + '\0' + style → path; first writer wins (the user's fonts are listed first).
    private readonly Dictionary<string, string> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _builtIn = new(StringComparer.OrdinalIgnoreCase);
    private bool _scannedAll;
    private FontCoverage? _coverage;

    /// <param name="directories">Tests: where to look (default: the macOS font directories).</param>
    public MacFontLocator(IReadOnlyList<string>? directories = null)
    {
        _directories = directories?.ToArray()
            ?? [.. FontFiles.MacFontDirectories(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                "/System/Applications/Utilities/Terminal.app/Contents/Resources/Fonts"]; // SF Mono lives here
        _listFiles = ListFiles;
        foreach (var f in FontFiles.MacMonospaceFamilies.Concat(FontFiles.MacSymbolFallbackFamilies).Concat(FontFiles.MacBroadFallbackFamilies))
            _builtIn.Add(f);
        _builtIn.Add(FontFiles.DefaultFamily); // the default font setting: its files are always named after it
    }

    public IReadOnlyList<string> SymbolFallbackFamilies => FontFiles.MacSymbolFallbackFamilies;

    public string? Find(string family, FontStyle style)
    {
        family = family.Trim();
        if (family.Length == 0) return null;
        lock (_sync)
        {
            string key = Key(family, style);
            if (_index.TryGetValue(key, out var path)) return path;
            if (_scannedAll) return null;
            ScanNames(Files().Where(f => FontFiles.NameSuggests(f, family)));
            if (_index.TryGetValue(key, out path)) return path;
            // The family is there without this style (no Bold file): FontSet falls back to the regular face.
            if (_families.Contains(family) || _builtIn.Contains(family)) return null;
            ScanNames(Files());
            _scannedAll = true;
            return _index.GetValueOrDefault(key);
        }
    }

    public string? DefaultMonospace()
    {
        foreach (var family in FontFiles.MacMonospaceFamilies)
            if (Find(family, FontStyle.Regular) is { } path) return path;
        lock (_sync) return Files().FirstOrDefault(); // anything beats no window
    }

    public IEnumerable<string> FallbackFiles(string family)
    {
        foreach (var f in FontFiles.MacBroadFallbackFamilies)
            if (Find(f, FontStyle.Regular) is { } path) yield return path;
    }

    /// <summary>The first face (preferred families first) whose charmap has the code point; needs <see cref="BuildCoverage"/>.</summary>
    public string? FindForCodepoint(int codepoint) => Volatile.Read(ref _coverage)?.FirstCovering(codepoint);

    /// <summary>Reads every face's charmap (seconds on a cold disk; FallbackResolver calls it on its own thread).</summary>
    public FontCoverage? BuildCoverage()
    {
        List<string> files;
        lock (_sync) files = [.. Files()];
        var faces = new List<(string Path, string Family, List<(int, int)> Ranges)>();
        nint lib;
        if (FreeType.InitFreeType(&lib) != 0) return null;
        try
        {
            foreach (var file in files)
                ForEachFace(lib, file, (path, face, family) => faces.Add((path, family, FreeType.CharRanges(face))));
        }
        finally
        {
            FreeType.DoneFreeType(lib);
        }
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in FontFiles.MacMonospaceFamilies.Concat(FontFiles.MacSymbolFallbackFamilies).Concat(FontFiles.MacBroadFallbackFamilies))
            rank.TryAdd(f, rank.Count);
        var ordered = faces
            .Select((f, i) => (f, i))
            .OrderBy(x => rank.GetValueOrDefault(x.f.Family, int.MaxValue))
            .ThenBy(x => x.i)
            .Select(x => (x.f.Path, x.f.Ranges));
        var coverage = FontCoverage.FromRanges(ordered);
        Volatile.Write(ref _coverage, coverage);
        return coverage;
    }

    private static string Key(string family, FontStyle style) => family + "\0" + (int)style;

    private List<string> Files() => _files ??= FontFiles.MacFontFiles(_directories, _listFiles);

    private static IEnumerable<string> ListFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
            : [];

    /// <summary>Indexes the family/style names of every face in <paramref name="files"/> not read before.</summary>
    private void ScanNames(IEnumerable<string> files)
    {
        var todo = files.Where(f => !_scanned.Contains(f)).ToList();
        if (todo.Count == 0) return;
        nint lib;
        if (FreeType.InitFreeType(&lib) != 0) return;
        try
        {
            foreach (var file in todo)
            {
                _scanned.Add(file);
                ForEachFace(lib, file, (path, face, family) =>
                {
                    string? styleName = FreeType.StyleName(face);
                    if (WindowsFontNames.TryParseStyleName(styleName, out bool bold, out bool italic))
                        IndexFace(family, bold, italic, path);

                    // The typographic family (name ID 16) is the name fontconfig reports, so it is what a config written
                    // on Linux asks for — and family_name (ID 1) can differ: a Nerd Font's files call themselves
                    // "JetBrainsMono NF" there. Its own subfamily (ID 17) decides the style when present; without it the
                    // Light file, whose style_name is "Regular", would claim the family's regular face.
                    var (typo, typoStyle) = FreeType.TypographicNames(face);
                    if (typo is { Length: > 0 } && WindowsFontNames.TryParseStyleName(typoStyle ?? styleName, out bool tb, out bool ti))
                        IndexFace(typo, tb, ti, path);
                });
            }
        }
        finally
        {
            FreeType.DoneFreeType(lib);
        }
    }

    private void IndexFace(string family, bool bold, bool italic, string path)
    {
        var style = FontStyles.Of(bold, italic);
        _index.TryAdd(Key(family, style), path);
        _families.Add(family);
    }

    /// <summary>Opens each face of <paramref name="file"/> in turn (skipping hidden system faces, named ".SF…").</summary>
    private static void ForEachFace(nint lib, string file, Action<string, nint, string> visit)
    {
        long count = 1;
        for (int i = 0; i < count && i < 256; i++)
        {
            nint face;
            if (FreeType.NewFace(lib, file, i, &face) != 0) return;
            try
            {
                if (i == 0) count = Math.Max(1, FreeType.NumFaces(face));
                if (FreeType.FamilyName(face) is { Length: > 0 } family && family[0] != '.')
                    visit(FontFiles.Join(file, i), face, family);
            }
            finally
            {
                FreeType.DoneFace(face);
            }
        }
    }
}
