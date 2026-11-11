namespace ErikTerm.App.Rendering;

/// <summary>
/// Pure string logic for the Windows font registry (HKLM/HKCU\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts).
/// Value names look like "Cascadia Code Bold (TrueType)" or "MS Gothic &amp; MS UI Gothic &amp; MS PGothic (TrueType)";
/// the data is a file name relative to %WINDIR%\Fonts, or a full path for per-user fonts.
/// Kept free of other dependencies so the test project can compile it directly.
/// </summary>
internal static class WindowsFontNames
{
    public readonly record struct Entry(string Family, bool Bold, bool Italic);

    private static readonly string[] ScalableExtensions = [".ttf", ".otf", ".ttc", ".otc"];

    /// <summary>
    /// Parses a registry value name into (family, style) entries. For a font collection (.ttc/.otc)
    /// only the first name is returned: it names face 0, the only face erikterm opens.
    /// </summary>
    public static List<Entry> ParseValueName(string valueName, bool isCollection)
    {
        var result = new List<Entry>();
        string name = StripTypeSuffix(valueName);
        foreach (var part in name.Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(SplitStyle(part));
            if (isCollection) break;
        }
        return result;
    }

    /// <summary>"Arial (TrueType)" → "Arial"; names without a trailing parenthesis are returned trimmed.</summary>
    public static string StripTypeSuffix(string valueName)
    {
        string s = valueName.Trim();
        if (s.EndsWith(')') && s.LastIndexOf('(') is > 0 and var open) s = s[..open];
        return s.Trim();
    }

    /// <summary>
    /// Splits a full face name into family and style by peeling trailing style words:
    /// "Cascadia Code Bold Italic" → ("Cascadia Code", bold, italic). Weights other than bold
    /// ("SemiBold", "Light", ...) stay part of the family, so they never match a plain family request.
    /// </summary>
    public static Entry SplitStyle(string fullName)
    {
        var words = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int end = words.Length;
        bool bold = false, italic = false;
        while (end > 1 && TryStyleWord(words[end - 1], ref bold, ref italic)) end--;
        return new Entry(string.Join(' ', words, 0, end), bold, italic);
    }

    /// <summary>
    /// Parses a style name as FreeType reports it (FT_FaceRec.style_name): "Regular", "Bold",
    /// "Italic", "Bold Italic", "Oblique", ... Returns false for anything else ("SemiBold", "Light").
    /// </summary>
    public static bool TryParseStyleName(string? styleName, out bool bold, out bool italic)
    {
        bold = italic = false;
        if (string.IsNullOrWhiteSpace(styleName)) return true; // unnamed style: treat as regular
        foreach (var word in styleName.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!TryStyleWord(word, ref bold, ref italic)) return false;
        return true;
    }

    private static bool TryStyleWord(string word, ref bool bold, ref bool italic)
    {
        if (word.Equals("Bold", StringComparison.OrdinalIgnoreCase)) bold = true;
        else if (word.Equals("Italic", StringComparison.OrdinalIgnoreCase) || word.Equals("Oblique", StringComparison.OrdinalIgnoreCase)) italic = true;
        else if (!(word.Equals("Regular", StringComparison.OrdinalIgnoreCase) || word.Equals("Normal", StringComparison.OrdinalIgnoreCase)
                   || word.Equals("Roman", StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    /// <summary>Registry data → absolute path. Relative names live in the system fonts directory.</summary>
    public static string ResolvePath(string data, string systemFontsDir)
    {
        string d = data.Trim().Trim('"');
        return IsAbsoluteWindowsPath(d) ? d : systemFontsDir.TrimEnd('\\', '/') + "\\" + d;
    }

    /// <summary>"C:\..." / "C:/..." / "\\server\share" (checked as strings, so it behaves the same on any host OS).</summary>
    public static bool IsAbsoluteWindowsPath(string p) =>
        (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/') || p.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Outline font files FreeType can open (skips .fon raster fonts and the like).</summary>
    public static bool IsScalableFontFile(string path) =>
        ScalableExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static bool IsCollection(string path) =>
        path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otc", StringComparison.OrdinalIgnoreCase);
}
