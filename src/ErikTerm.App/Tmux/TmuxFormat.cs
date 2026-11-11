using System.Globalization;
using System.Text;

namespace ErikTerm.App.Tmux;

/// <summary>
/// tmux format strings (<c>-F</c>, <c>display-message -p</c>): <c>#{name}</c>, the one-letter aliases (<c>#D</c>
/// <c>#P</c> <c>#S</c> <c>#I</c> <c>#W</c> <c>#T</c> <c>#F</c> <c>#H</c> <c>#h</c>), conditionals
/// <c>#{?cond,then,else}</c> (also chained <c>#{?c1,a,c2,b,else}</c>), comparisons <c>#{==:a,b}</c> <c>!=</c>
/// <c>&lt;</c> <c>&gt;</c> <c>&lt;=</c> <c>&gt;=</c> <c>||</c> <c>&amp;&amp;</c>, and the modifiers <c>=N:</c> / <c>=-N:</c>
/// (truncate), <c>b:</c> / <c>d:</c> (basename / dirname), <c>l:</c> (literal), <c>n:</c> (length).
/// Escapes: <c>##</c>, <c>#,</c>, <c>#}</c>. Unknown variables expand to nothing, as in tmux; style markup
/// (<c>#[fg=red]</c>) is kept as is.
/// </summary>
internal static class TmuxFormat
{
    private static readonly Dictionary<char, string> Aliases = new()
    {
        ['D'] = "pane_id", ['F'] = "window_flags", ['H'] = "host", ['h'] = "host_short", ['I'] = "window_index",
        ['P'] = "pane_index", ['S'] = "session_name", ['T'] = "pane_title", ['W'] = "window_name",
    };

    public static string Expand(string format, Func<string, string?> lookup)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < format.Length)
        {
            char c = format[i];
            if (c != '#' || i + 1 >= format.Length)
            {
                sb.Append(c);
                i++;
                continue;
            }
            char n = format[i + 1];
            switch (n)
            {
                case '#' or ',' or '}':
                    sb.Append(n);
                    i += 2;
                    break;
                case '{':
                    int end = MatchBrace(format, i + 1);
                    if (end < 0)
                    {
                        sb.Append(format, i, format.Length - i);
                        return sb.ToString();
                    }
                    sb.Append(ExpandInner(format[(i + 2)..end], lookup));
                    i = end + 1;
                    break;
                default:
                    if (Aliases.TryGetValue(n, out string? name)) sb.Append(lookup(name) ?? "");
                    else sb.Append('#').Append(n);
                    i += 2;
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Index of the '}' closing the '{' at <paramref name="open"/>, or -1.</summary>
    private static int MatchBrace(string s, int open)
    {
        int depth = 0;
        int j = open;
        while (j < s.Length)
        {
            if (s[j] == '#' && j + 1 < s.Length)
            {
                if (s[j + 1] == '{') depth++;
                j += 2; // "#x" is one unit: escapes don't count as braces
                continue;
            }
            if (s[j] == '{') depth++;
            else if (s[j] == '}' && --depth == 0) return j;
            j++;
        }
        return -1;
    }

    /// <summary>Splits on commas outside nested <c>#{…}</c>.</summary>
    private static List<string> SplitTop(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        int j = 0;
        while (j < s.Length)
        {
            if (s[j] == '#' && j + 1 < s.Length)
            {
                if (s[j + 1] == '{') depth++;
                j += 2;
                continue;
            }
            if (s[j] == '{') depth++;
            else if (s[j] == '}') depth--;
            else if (s[j] == ',' && depth == 0)
            {
                parts.Add(s[start..j]);
                start = j + 1;
            }
            j++;
        }
        parts.Add(s[start..]);
        return parts;
    }

    private static bool Truthy(string v) => v.Length > 0 && v != "0";

    /// <summary>A variable's value, or the text expanded as a format when there is no such variable.</summary>
    private static string Value(string s, Func<string, string?> lookup) =>
        !s.Contains('#') && lookup(s) is { } v ? v : Expand(s, lookup);

    private static string ExpandInner(string inner, Func<string, string?> lookup)
    {
        if (inner.StartsWith('?')) return ExpandConditional(inner[1..], lookup);
        int colon = inner.IndexOf(':');
        if (colon > 0 && !inner[..colon].Contains('{')) return ExpandModified(inner[..colon], inner[(colon + 1)..], lookup);
        return lookup(inner) ?? "";
    }

    private static string ExpandConditional(string body, Func<string, string?> lookup)
    {
        var parts = SplitTop(body);
        int k = 0;
        while (k + 1 < parts.Count)
        {
            if (Truthy(Value(parts[k], lookup))) return Expand(parts[k + 1], lookup);
            k += 2;
        }
        return k < parts.Count ? Expand(parts[k], lookup) : "";
    }

    private static string ExpandModified(string mod, string rest, Func<string, string?> lookup)
    {
        switch (mod)
        {
            case "==" or "!=" or "<" or ">" or "<=" or ">=" or "||" or "&&": return ExpandComparison(mod, rest, lookup);
            case "l": return rest;
            case "b": return Path.GetFileName(Value(rest, lookup).TrimEnd('/'));
            case "d": return Path.GetDirectoryName(Value(rest, lookup)) ?? "";
            case "n": return Value(rest, lookup).Length.ToString(CultureInfo.InvariantCulture);
        }
        if (mod.Length > 1 && mod[0] == '=' && int.TryParse(mod.AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int width))
        {
            string v = Value(rest, lookup);
            int n = Math.Min(Math.Abs(width), v.Length);
            return width >= 0 ? v[..n] : v[^n..];
        }
        return ""; // an unsupported modifier
    }

    private static string ExpandComparison(string mod, string rest, Func<string, string?> lookup)
    {
        var args = SplitTop(rest);
        if (args.Count != 2) return "";
        string a = Expand(args[0], lookup), b = Expand(args[1], lookup);
        int cmp = string.CompareOrdinal(a, b);
        bool r = mod switch
        {
            "==" => cmp == 0, "!=" => cmp != 0, "<" => cmp < 0, ">" => cmp > 0, "<=" => cmp <= 0, ">=" => cmp >= 0,
            "||" => Truthy(Value(args[0], lookup)) || Truthy(Value(args[1], lookup)),
            _ => Truthy(Value(args[0], lookup)) && Truthy(Value(args[1], lookup)),
        };
        return r ? "1" : "0";
    }
}
