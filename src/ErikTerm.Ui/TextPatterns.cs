using System.Text.RegularExpressions;

namespace ErikTerm.Ui;

public enum MatchKind : byte { Url, Path, Uuid, Ipv6, Ipv4, Hash, Number }

/// <summary>A match in a string: [Start, Start + Length).</summary>
public readonly record struct TextMatch(int Start, int Length, MatchKind Kind)
{
    public int End => Start + Length;
    public string Of(string text) => text.Substring(Start, Length);
}

/// <summary>
/// Pattern matching for implicit hyperlinks and quick select. Pure string logic (source-generated,
/// AOT-friendly regexes), unit-tested; the app maps match offsets back to cells.
/// </summary>
public static partial class TextPatterns
{
    // Box drawing (U+2500–U+259F) and the usual delimiters end a URL: TUIs draw tables around them.
    private const string UrlChars = @"[^\s<>""'`─-▟ ]";

    [GeneratedRegex(@"\b(?:(?:https?|ftps?|file|sftp|ssh|git)://|mailto:|www\.)" + UrlChars + "+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    // A path needs a slash: ~/x, ./x, ../x, /abs/x, rel/x.ext. Optional :line[:col] suffix (compiler output).
    [GeneratedRegex(@"(?<![\w/.~-])(?:~|\.{1,2}|[\w.@+-]+)?(?:/[\w.@+,=-]+)+/?(?::\d+(?::\d+)?)?", RegexOptions.CultureInvariant)]
    internal static partial Regex PathRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex UuidRegex();

    // Git hashes: 7–40 lowercase hex with at least one digit and one letter (so words and plain numbers don't count).
    [GeneratedRegex(@"\b(?=[0-9a-f]*[0-9])(?=[0-9a-f]*[a-f])[0-9a-f]{7,40}\b", RegexOptions.CultureInvariant)]
    private static partial Regex HashRegex();

    [GeneratedRegex(@"(?<![\w.])(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}(?:/\d{1,2}|:\d{1,5})?(?![\w.])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();

    // Full or ::-compressed IPv6, optional /prefix (needs 7 colons or a "::", so clock times like 12:34:56 don't match).
    [GeneratedRegex(
        @"(?<![\w:])(?:" +
        @"(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}" +
        @"|(?:[0-9a-fA-F]{1,4}:){1,6}:[0-9a-fA-F]{1,4}(?::[0-9a-fA-F]{1,4}){0,5}" +
        @"|(?:[0-9a-fA-F]{1,4}:){1,7}:" +
        @"|::(?:[0-9a-fA-F]{1,4}:){0,6}[0-9a-fA-F]{1,4}" +
        @")(?:/\d{1,3})?(?![\w:])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6Regex();

    [GeneratedRegex(@"(?<![\w.])\d{4,}(?![\w])", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    /// <summary>URLs (http, https, ftp, file, mailto, www.…), with trailing punctuation and unbalanced brackets trimmed.</summary>
    public static List<TextMatch> FindUrls(string text)
    {
        var list = new List<TextMatch>();
        AddUrls(text, list);
        return list;
    }

    private static void AddUrls(string text, List<TextMatch> list)
    {
        foreach (var m in UrlRegex().EnumerateMatches(text))
        {
            int len = TrimUrl(text.AsSpan(m.Index, m.Length));
            int minimum = text.AsSpan(m.Index).StartsWith("www.") ? 5 : text.AsSpan(m.Index, m.Length).IndexOf(':') + 2;
            if (len >= minimum) list.Add(new TextMatch(m.Index, len, MatchKind.Url));
        }
    }

    /// <summary>Length of a URL candidate without trailing sentence punctuation or closing brackets it didn't open.</summary>
    public static int TrimUrl(ReadOnlySpan<char> url)
    {
        int len = url.Length;
        while (len > 0)
        {
            char c = url[len - 1];
            if (c is '.' or ',' or ':' or ';' or '!' or '?' or '*' or '\'' or '"') { len--; continue; }
            char open = c switch { ')' => '(', ']' => '[', '}' => '{', '>' => '<', _ => '\0' };
            if (open != '\0' && url[..len].Count(open) < url[..len].Count(c)) { len--; continue; }
            break;
        }
        return len;
    }

    /// <summary>Everything quick select offers, without overlaps (URL, UUID, IPv6, IPv4, path, hash, number win in that order), in text order.</summary>
    public static List<TextMatch> FindQuickSelect(string text)
    {
        var all = new List<TextMatch>();
        AddUrls(text, all);
        Add(UuidRegex(), MatchKind.Uuid);
        Add(Ipv6Regex(), MatchKind.Ipv6); // addresses before paths: 10.0.0.0/8 is a network, not a file
        Add(Ipv4Regex(), MatchKind.Ipv4);
        AddFileRefs(text, all); // MSBuild path(line,col), stack frames, path:line (FileRefPatterns.cs)
        Add(PathRegex(), MatchKind.Path);
        Add(HashRegex(), MatchKind.Hash);
        Add(NumberRegex(), MatchKind.Number);
        all.Sort((a, b) => a.Start.CompareTo(b.Start));
        return all;

        void Add(Regex regex, MatchKind kind)
        {
            foreach (var m in regex.EnumerateMatches(text))
            {
                int start = m.Index, len = QuickSelectLength(text, start, m.Length, kind);
                if (len >= 0 && !Overlaps(all, start, start + len)) all.Add(new TextMatch(start, len, kind));
            }
        }
    }

    /// <summary>The length a candidate keeps, or -1 when it isn't a match of that kind after all.</summary>
    private static int QuickSelectLength(string text, int start, int length, MatchKind kind)
    {
        if (kind == MatchKind.Path)
        {
            int trimmed = TrimUrl(text.AsSpan(start, length));
            return trimmed < 3 || !text.AsSpan(start, trimmed).ContainsAnyExcept('/', '.') ? -1 : trimmed;
        }
        if (kind == MatchKind.Ipv6 && !text.AsSpan(start, length).ContainsAnyExcept(':')) return -1;
        return length;
    }

    private static bool Overlaps(List<TextMatch> list, int start, int end)
    {
        foreach (var m in list)
            if (start < m.End && m.Start < end) return true;
        return false;
    }

    // ---- File references (FileRefPatterns.cs) ------------------------------------------------------------------

    /// <summary>File references in text order, without overlaps (and not inside URLs).</summary>
    public static List<FileRefMatch> FindFileRefs(string text) => FileRefPatterns.Find(text);

    /// <summary>The file reference spanning all of <paramref name="text"/> (e.g. a quick select match), or null.</summary>
    public static FileRefMatch? ParseFileRef(string text) => FileRefPatterns.Parse(text);

    /// <summary>Quick select hook: the unambiguous file references (MSBuild, stack frames, path:line) as paths.</summary>
    private static void AddFileRefs(string text, List<TextMatch> all)
    {
        foreach (var f in FileRefPatterns.Find(text))
            if (f.Strong && !Overlaps(all, f.Start, f.End)) all.Add(new TextMatch(f.Start, f.Length, MatchKind.Path));
    }

    // ---- Commit hashes and issue numbers ----------------------------------------------------------

    [GeneratedRegex(@"^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitHashRegex();

    // #123 (not &#123; HTML entities, not color codes like #fff, not url#fragment).
    [GeneratedRegex(@"(?<![\w&#/])#(?<n>[1-9]\d{0,6})\b(?!-)", RegexOptions.CultureInvariant)]
    private static partial Regex IssueRegex();

    /// <summary>True for an abbreviated or full lowercase git object name (7–40 hex digits).</summary>
    public static bool IsCommitHash(string s) => s.Length is >= 7 and <= 40 && CommitHashRegex().IsMatch(s);

    /// <summary>Hash-looking words (7–40 hex, at least one digit and one letter) in text order.</summary>
    public static List<TextMatch> FindHashes(string text)
    {
        var list = new List<TextMatch>();
        foreach (var m in HashRegex().EnumerateMatches(text)) list.Add(new TextMatch(m.Index, m.Length, MatchKind.Hash));
        return list;
    }

    /// <summary><c>#123</c> issue/PR references; <see cref="TextMatch.Length"/> includes the '#'.</summary>
    public static List<TextMatch> FindIssueRefs(string text)
    {
        var list = new List<TextMatch>();
        foreach (var m in IssueRegex().EnumerateMatches(text)) list.Add(new TextMatch(m.Index, m.Length, MatchKind.Number));
        return list;
    }

    /// <summary>The issue number in "#123" or "123" (1–9999999), or 0.</summary>
    public static int ParseIssueNumber(string s)
    {
        var span = s.AsSpan().TrimStart('#');
        if (span.Length is 0 or > 7 || span[0] == '0') return 0;
        foreach (char c in span) if (c is < '0' or > '9') return 0;
        return int.Parse(span);
    }
}

/// <summary>Quick select hint labels (wezterm's alphabet: home row first).</summary>
public static class HintLabels
{
    public const string DefaultAlphabet = "asdfqwerzxcvjklmiuopghtybn";

    /// <summary>
    /// <paramref name="count"/> prefix-free labels, shortest possible: single letters while they
    /// suffice, then the remaining letters are used as prefixes for two-letter labels.
    /// </summary>
    public static List<string> Generate(int count, string alphabet = DefaultAlphabet)
    {
        var labels = new List<string>(count);
        int n = alphabet.Length;
        if (count <= 0 || n == 0) return labels;
        if (count <= n)
        {
            for (int i = 0; i < count; i++) labels.Add(alphabet[i].ToString());
            return labels;
        }
        // s single letters + (n - s) prefixes × n second letters ≥ count.
        count = Math.Min(count, n * n);
        int singles = n > 1 ? Math.Clamp((n * n - count) / (n - 1), 0, n) : 0;
        for (int i = 0; i < singles && labels.Count < count; i++) labels.Add(alphabet[i].ToString());
        for (int p = singles; p < n && labels.Count < count; p++)
            for (int q = 0; q < n && labels.Count < count; q++)
                labels.Add(string.Concat(alphabet[p], alphabet[q]));
        return labels;
    }
}
