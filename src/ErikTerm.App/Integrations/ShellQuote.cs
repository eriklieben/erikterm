using System.Text;

namespace ErikTerm.App.Integrations;

/// <summary>POSIX shell quoting for dropped paths, and turning drop payloads (paths, file:// URI lists) into text.</summary>
internal static class ShellQuote
{
    /// <summary>The word as is when it only has safe characters, else in single quotes (' becomes '\'').</summary>
    public static string Quote(string s)
    {
        if (s.Length == 0) return "''";
        bool safe = true;
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+' or ',' or '@' or '%' or '=' or ':')) { safe = false; break; }
        if (safe && s[0] != '-' && s[0] != '=') return s;
        return "'" + s.Replace("'", @"'\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Paths quoted and joined by spaces, with a trailing space so the next drop or word doesn't stick to it.</summary>
    public static string JoinPaths(IEnumerable<string> paths)
    {
        var sb = new StringBuilder();
        foreach (var p in paths)
        {
            // A newline would run the command, other controls would be stripped from the paste (a different path).
            if (p.Length == 0 || p.Any(Security.PasteSanitizer.IsControl)) continue;
            sb.Append(Quote(p)).Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Text dropped on the window: a text/uri-list of file:// URIs becomes local paths; anything else is returned as
    /// <paramref name="paths"/> = null with the text flattened to one line (a dropped newline would press Enter).
    /// </summary>
    public static (List<string>? Paths, string Text) FromDroppedText(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#')).ToList();
        if (lines.Count > 0 && lines.All(l => l.StartsWith("file://", StringComparison.OrdinalIgnoreCase)))
        {
            var paths = LocalPaths(lines);
            if (paths.Count > 0) return (paths, "");
        }
        return (null, FlattenToOneLine(text));
    }

    private static List<string> LocalPaths(List<string> fileUris)
    {
        var paths = new List<string>();
        foreach (var l in fileUris)
            if (Uri.TryCreate(l, UriKind.Absolute, out var uri) && uri.IsFile && uri.LocalPath.Length > 0) paths.Add(uri.LocalPath);
        return paths;
    }

    private static string FlattenToOneLine(string text)
    {
        var flat = new StringBuilder(text.Length);
        foreach (char c in text.Trim())
        {
            if (c is '\n' or '\r' or '\t') flat.Append(' ');
            else flat.Append(char.IsControl(c) ? '�' : c);
        }
        return flat.ToString();
    }
}
