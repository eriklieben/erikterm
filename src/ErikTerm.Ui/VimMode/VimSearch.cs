using System.Text.RegularExpressions;
using ErikTerm.Core;

namespace ErikTerm.Ui.VimMode;

/// <summary>Search movement over <see cref="TerminalSearch"/> results (sorted by start), and vim's pattern rules.</summary>
public static class VimSearch
{
    /// <summary>
    /// The match n (forward) or N (backward) goes to from <paramref name="from"/>: the first one starting after it
    /// (at it too when <paramref name="inclusive"/>, as incremental search does), wrapping around the ends. -1: none.
    /// </summary>
    public static int Next(IReadOnlyList<SearchMatch> matches, VimPos from, bool forward, bool inclusive = false)
    {
        int n = matches.Count;
        if (n == 0) return -1;
        // Binary search for the first match starting at or after `from`.
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            var m = matches[mid];
            if (new VimPos(m.StartLine, m.StartCol) < from) lo = mid + 1;
            else hi = mid;
        }
        bool atFrom = lo < n && matches[lo].StartLine == from.Line && matches[lo].StartCol == from.Col;
        if (forward)
        {
            int i = !inclusive && atFrom ? lo + 1 : lo;
            return i < n ? i : 0;
        }
        int j = inclusive && atFrom ? lo : lo - 1;
        return j >= 0 ? j : n - 1;
    }

    /// <summary>True when going from <paramref name="from"/> to match <paramref name="index"/> wrapped around an end.</summary>
    public static bool Wrapped(IReadOnlyList<SearchMatch> matches, int index, VimPos from, bool forward)
    {
        if (index < 0) return false;
        var start = new VimPos(matches[index].StartLine, matches[index].StartCol);
        return forward ? start < from : start > from;
    }

    /// <summary>Vim's 'smartcase': case-sensitive only when the pattern has an upper-case letter.</summary>
    public static bool CaseSensitive(string pattern)
    {
        bool escaped = false;
        foreach (char c in pattern)
        {
            if (escaped) { escaped = false; continue; } // \S, \W are classes, not letters
            if (c == '\\') { escaped = true; continue; }
            if (char.IsUpper(c)) return true;
        }
        return false;
    }

    /// <summary>
    /// A vim pattern as a .NET regex: \&lt; and \&gt; are word boundaries, \{n,m} a counted repeat; the rest is already
    /// close enough (vim's "very magic" mode is plain regex syntax).
    /// </summary>
    public static string ToRegex(string pattern) =>
        pattern.Replace(@"\<", @"\b", StringComparison.Ordinal).Replace(@"\>", @"\b", StringComparison.Ordinal).Replace(@"\{", "{", StringComparison.Ordinal);

    /// <summary>* and #: the whole word under the cursor, as a pattern.</summary>
    public static string WordPattern(string word)
    {
        string escaped = Regex.Escape(word);
        bool wordStart = word.Length > 0 && (char.IsLetterOrDigit(word[0]) || word[0] == '_');
        bool wordEnd = word.Length > 0 && (char.IsLetterOrDigit(word[^1]) || word[^1] == '_');
        return (wordStart ? @"\b" : "") + escaped + (wordEnd ? @"\b" : "");
    }
}
