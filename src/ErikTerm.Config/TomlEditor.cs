using System.Globalization;
using System.Text;

namespace ErikTerm.Config;

/// <summary>
/// Changes one top-level setting in the text of a TOML file and leaves everything else as it was: comments, blank
/// lines, the order of the keys and the line endings. Used by the settings page, which writes the user's own file.
/// </summary>
public static class TomlEditor
{
    /// <summary>
    /// The text with <paramref name="key"/> set to <paramref name="literal"/> (a TOML value as written, see
    /// <see cref="Literal(bool)"/> and friends). An existing line keeps its comment; a commented-out example
    /// (<c># key = …</c>) is switched on in its place; otherwise the line goes at the end of the top-level keys.
    /// </summary>
    public static string Set(string text, string key, string literal)
    {
        var doc = new Lines(text);
        var region = TopLevel(doc);
        if (region.Find(key) is { } found)
        {
            doc.Replace(found.First, found.Last - found.First + 1, [Rewrite(doc.Text(found.First), literal, found.Last > found.First)]);
            return doc.Join();
        }
        if (region.FindCommented(key) is { } example)
        {
            doc.Replace(example, 1, [Rewrite(Uncomment(doc.Text(example)), literal, multiLine: false)]);
            return doc.Join();
        }
        doc.Insert(region.InsertAt(doc), $"{key} = {literal}");
        return doc.Join();
    }

    /// <summary>
    /// The text with <paramref name="key"/> commented out, so it's back to its default but the line (and its
    /// comment) stays as an example to switch on again. Unchanged when the key isn't set.
    /// </summary>
    public static string Unset(string text, string key)
    {
        var doc = new Lines(text);
        if (TopLevel(doc).Find(key) is not { } found) return text;
        var commented = new List<string>();
        for (int i = found.First; i <= found.Last; i++) commented.Add("# " + doc.Text(i));
        doc.Replace(found.First, found.Last - found.First + 1, commented);
        return doc.Join();
    }

    // ---- Values ------------------------------------------------------------------------------------------------

    public static string Literal(bool value) => value ? "true" : "false";

    public static string Literal(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    public static string Literal(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case < ' ' or '\x7f': sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static string Literal(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Literal)) + "]";

    // ---- Lines -------------------------------------------------------------------------------------------------

    /// <summary>The file as lines, remembering its line ending and whether it ended with one.</summary>
    private sealed class Lines
    {
        private readonly List<string> _lines;
        private readonly string _newline;
        private readonly bool _finalNewline;

        public Lines(string text)
        {
            _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            _finalNewline = text.Length == 0 || text.EndsWith('\n');
            string body = text;
            if (_finalNewline && text.Length > 0) body = text[..^(text.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : 1)];
            _lines = text.Length == 0 ? [] : [.. body.Split('\n').Select(line => line.TrimEnd('\r'))];
        }

        public int Count => _lines.Count;
        public string Text(int index) => _lines[index];

        public void Replace(int index, int count, IReadOnlyList<string> with)
        {
            _lines.RemoveRange(index, count);
            _lines.InsertRange(index, with);
        }

        public void Insert(int index, string line) => _lines.Insert(index, line);

        public string Join() => string.Join(_newline, _lines) + (_finalNewline && _lines.Count > 0 ? _newline : "");
    }

    /// <summary>The lines of a key = value pair: one line, or several for a value that spans lines.</summary>
    private readonly record struct Span(int First, int Last);

    /// <summary>What the top of the file (before the first [table]) holds.</summary>
    private sealed class Region(Dictionary<string, Span> keys, Dictionary<string, int> commented, int lastKeyLine, int end)
    {
        public Span? Find(string key) => keys.TryGetValue(key, out var span) ? span : null;
        public int? FindCommented(string key) => commented.TryGetValue(key, out int line) ? line : null;

        /// <summary>After the last top-level key; before the first table when there are none, so the key stays top-level.</summary>
        public int InsertAt(Lines doc) => lastKeyLine >= 0 ? lastKeyLine + 1 : Math.Min(end, doc.Count);
    }

    private static Region TopLevel(Lines doc)
    {
        var keys = new Dictionary<string, Span>(StringComparer.Ordinal);
        var commented = new Dictionary<string, int>(StringComparer.Ordinal);
        int lastKeyLine = -1;
        int i = 0;
        while (i < doc.Count)
        {
            string trimmed = doc.Text(i).TrimStart();
            if (trimmed.StartsWith('[')) break; // the first table: everything after belongs to it
            if (trimmed.StartsWith('#'))
            {
                if (KeyOf(Uncomment(trimmed)) is { } example) commented.TryAdd(example, i);
            }
            else if (KeyOf(trimmed) is { } key)
            {
                int last = EndOfValue(doc, i);
                keys.TryAdd(key, new Span(i, last));
                lastKeyLine = last;
                i = last; // past the value's other lines
            }
            i++;
        }
        return new Region(keys, commented, lastKeyLine, i);
    }

    /// <summary>The bare key a <c>key = value</c> line sets, or null (dotted and quoted keys are left alone).</summary>
    private static string? KeyOf(string line)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0) return null;
        string key = line[..eq].Trim();
        return key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : null;
    }

    private static string Uncomment(string line)
    {
        string rest = line.TrimStart().TrimStart('#');
        return rest.StartsWith(' ') ? rest[1..] : rest;
    }

    /// <summary>The last line of the value that starts on <paramref name="first"/>: arrays and inline tables can span lines, and so can triple-quoted strings.</summary>
    private static int EndOfValue(Lines doc, int first)
    {
        var scan = new ValueScan();
        string line = doc.Text(first);
        scan.Feed(line[(line.IndexOf('=') + 1)..]);
        int i = first;
        while (scan.Open && i + 1 < doc.Count) scan.Feed(doc.Text(++i));
        return i;
    }

    /// <summary>
    /// The line <paramref name="line"/> (a <c>key = value</c>, possibly the first of several) with its value replaced:
    /// the key's spelling and the comment after the value are kept, with the comment where it was when it fits.
    /// </summary>
    private static string Rewrite(string line, string literal, bool multiLine)
    {
        int eq = line.IndexOf('=');
        string head = line[..(eq + 1)];
        string rest = line[(eq + 1)..];
        int lead = rest.Length - rest.TrimStart().Length;
        string spacing = lead > 0 ? rest[..lead] : " ";
        if (multiLine) return head + spacing + literal;
        int comment = CommentStart(rest);
        if (comment < 0) return head + spacing + literal;
        string value = rest[lead..comment].TrimEnd();
        int gap = comment - lead - value.Length;
        int pad = Math.Max(1, gap + value.Length - literal.Length);
        return head + spacing + literal + new string(' ', pad) + rest[comment..];
    }

    /// <summary>Where a <c>#</c> comment starts in a value, outside strings; -1 for none.</summary>
    private static int CommentStart(string value)
    {
        var scan = new ValueScan();
        for (int i = 0; i < value.Length; i++)
            if (scan.Step(value, ref i)) return i;
        return -1;
    }

    /// <summary>Follows strings, brackets and braces through a value, so that a # or ] inside a string doesn't count.</summary>
    private sealed class ValueScan
    {
        private int _depth;
        private string? _multiQuote; // inside """ or ''' across lines

        public bool Open => _depth > 0 || _multiQuote is not null;

        public void Feed(string line)
        {
            for (int i = 0; i < line.Length; i++)
                if (Step(line, ref i)) return; // the rest is a comment
        }

        /// <summary>Takes in the character at <paramref name="i"/> (moving past a whole string); true at a comment.</summary>
        public bool Step(string s, ref int i)
        {
            if (_multiQuote is { } quote)
            {
                int close = s.IndexOf(quote, i, StringComparison.Ordinal);
                if (close < 0) { i = s.Length; return false; }
                _multiQuote = null;
                i = close + 2;
                return false;
            }
            char c = s[i];
            switch (c)
            {
                case '#': return true;
                case '[' or '{': _depth++; return false;
                case ']' or '}': _depth = Math.Max(0, _depth - 1); return false;
                case '"' or '\'': i = SkipString(s, i, c); return false;
                default: return false;
            }
        }

        private int SkipString(string s, int start, char quote)
        {
            string triple = new(quote, 3);
            if (string.CompareOrdinal(s, start, triple, 0, 3) == 0)
            {
                int close = s.IndexOf(triple, start + 3, StringComparison.Ordinal);
                if (close >= 0) return close + 2;
                _multiQuote = triple;
                return s.Length;
            }
            int i = start + 1;
            while (i < s.Length)
            {
                if (s[i] == quote) return i;
                i += quote == '"' && s[i] == '\\' ? 2 : 1; // in a basic string a backslash escapes the next character
            }
            return s.Length;
        }
    }
}
