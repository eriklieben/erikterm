using System.Runtime.Versioning;
using ErikTerm.App.Native;
using Microsoft.Win32;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Windows font discovery, no fontconfig. Primary source is the registry font list (HKCU, then HKLM,
/// under SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts), which maps "Family Style (TrueType)" to a
/// file. On the first miss, every font file in %WINDIR%\Fonts and %LOCALAPPDATA%\Microsoft\Windows\Fonts is
/// opened once with FreeType to read its own family/style names, which the registry's name for it can differ
/// from; those become aliases for the same file, so a family is found under either name.
/// </summary>
[SupportedOSPlatform("windows")]
#pragma warning disable S6640 // faces are opened and read through FreeType's pointer API
internal sealed unsafe class WindowsFontLocator : IFontLocator
#pragma warning restore S6640
{
    private const string FontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    private static readonly string[] MonospaceFamilies = ["Cascadia Mono", "Cascadia Code", "Consolas", "Courier New"];

    // Coverage-ordered: wide symbol/emoji sets first, then CJK, then scripts the above don't cover.
    private static readonly string[] BroadFallbackFamilies =
    [
        "Segoe UI", "Segoe UI Historic", "Nirmala UI", "Ebrima", "Gadugi", "Leelawadee UI", "Microsoft Himalaya",
        "Myanmar Text", "Mongolian Baiti", "Microsoft Yi Baiti", "Microsoft JhengHei", "SimSun", "Sylfaen",
        "Javanese Text", "Arial", "Times New Roman",
    ];

    public IReadOnlyList<string> SymbolFallbackFamilies { get; } =
    [
        "Symbols Nerd Font Mono", "Segoe UI Symbol", "Segoe UI Emoji", "MS Gothic", "Yu Gothic", "Malgun Gothic", "Microsoft YaHei",
    ];

    // Key: family + '\0' + style. First writer wins (HKCU is read before HKLM).
    private readonly Dictionary<string, string> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _systemFonts;
    private readonly string _userFonts;
    private bool _scannedDirectories;

    public WindowsFontLocator()
    {
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        _systemFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts) is { Length: > 0 } f ? f : Path.Combine(windir, "Fonts");
        _userFonts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        ReadRegistry(Registry.CurrentUser);
        ReadRegistry(Registry.LocalMachine);
    }

    public string? Find(string family, FontStyle style)
    {
        string key = Key(family.Trim(), style);
        if (_index.TryGetValue(key, out var path)) return path;
        if (_scannedDirectories) return null;
        ScanDirectories();
        return _index.GetValueOrDefault(key);
    }

    public string? DefaultMonospace()
    {
        foreach (var family in MonospaceFamilies)
            if (Find(family, FontStyle.Regular) is { } path) return path;
        return null;
    }

    public IEnumerable<string> FallbackFiles(string family)
    {
        foreach (var f in MonospaceFamilies.Concat(BroadFallbackFamilies))
            if (Find(f, FontStyle.Regular) is { } path) yield return path;
    }

    /// <summary>No charset query on Windows; the fallback lists above (walked by FontSet) stand in for it.</summary>
    public string? FindForCodepoint(int codepoint) => null;

    private static string Key(string family, FontStyle style) => family + "\0" + (int)style;

    private static FontStyle StyleOf(bool bold, bool italic) => FontStyles.Of(bold, italic);

    private void Add(string family, bool bold, bool italic, string path)
    {
        if (family.Length == 0) return;
        _index.TryAdd(Key(family, StyleOf(bold, italic)), path);
    }

    private void ReadRegistry(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(FontsKey);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string data || data.Length == 0) continue;
                string path = WindowsFontNames.ResolvePath(Environment.ExpandEnvironmentVariables(data), _systemFonts);
                if (!WindowsFontNames.IsScalableFontFile(path) || !File.Exists(path)) continue;
                foreach (var e in WindowsFontNames.ParseValueName(name, WindowsFontNames.IsCollection(path)))
                    Add(e.Family, e.Bold, e.Italic, path);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"erikterm: cannot read font registry ({root.Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// Indexes every font file in the two directories by the family name inside it, read from face 0 with FreeType.
    /// Files the registry already listed are read too, because the two names can differ: the Nerd Font installer
    /// registers "JetBrainsMono NF" while the file calls itself "JetBrainsMono Nerd Font", which is the name
    /// fontconfig reports and therefore the one a config written on Linux asks for. Registry names still win
    /// (<see cref="Add"/> only fills gaps), so this adds aliases rather than replacing anything.
    /// </summary>
    private void ScanDirectories()
    {
        _scannedDirectories = true;
        var files = FontFilesInDirectories();
        if (files.Count == 0) return;

        nint lib;
        if (FreeType.InitFreeType(&lib) != 0) return;
        try
        {
            foreach (var file in files) IndexFontFile(lib, file);
        }
        finally
        {
            FreeType.DoneFreeType(lib);
        }
    }

    private List<string> FontFilesInDirectories()
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { _userFonts, _systemFonts })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                files.AddRange(Directory.EnumerateFiles(dir).Where(file => WindowsFontNames.IsScalableFontFile(file) && seen.Add(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable folder: use the other one */ }
        }
        return files;
    }

    private void IndexFontFile(nint lib, string file)
    {
        nint face;
        if (FreeType.NewFace(lib, file, 0, &face) != 0) return;
        try
        {
            string? style = FreeType.StyleName(face);
            if (FreeType.FamilyName(face) is { } family &&
                WindowsFontNames.TryParseStyleName(style, out bool bold, out bool italic))
                Add(family, bold, italic, file);

            // The typographic family (name ID 16) is the name fontconfig reports, so it is the one a
            // config written on Linux asks for -- and neither the registry nor family_name mentions it.
            // Its own subfamily decides the style when present: without it the Light file, whose
            // style_name is "Regular", would claim the family's regular face.
            var (typo, typoStyle) = FreeType.TypographicNames(face);
            if (typo is { Length: > 0 } &&
                WindowsFontNames.TryParseStyleName(typoStyle ?? style, out bool tb, out bool ti))
                Add(typo, tb, ti, file);
        }
        finally
        {
            FreeType.DoneFace(face);
        }
    }
}
