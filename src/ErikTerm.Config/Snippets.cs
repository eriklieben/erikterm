using System.Text;

namespace ErikTerm.Config;

/// <summary>A piece of text from the config's <c>snippets</c>, inserted with the palette ("Snippet: name") or SendSnippet(name).</summary>
public sealed record Snippet(string Name, string Text, string? Description = null);

/// <summary>
/// The <c>snippets</c> setting, in either TOML form:
/// <code>
/// [snippets]
/// gs = "git status"
/// deploy = { text = "make deploy DIR={{cwd}}", description = "Deploy from here" }
/// </code>
/// or an array of tables:
/// <code>
/// [[snippets]]
/// name = "gs"
/// text = "git status"
/// description = "…"
/// </code>
/// Text may use <c>{{cwd}}</c> (the pane's directory) and <c>{{selection}}</c> (the selected text); see <see cref="Expand"/>.
/// </summary>
public static class Snippets
{
    public const int MaxNameLength = 100;
    public const int MaxTextLength = 64 * 1024;

    /// <summary>Reads the setting's value; problems are reported through <paramref name="warn"/> and the entry is skipped.</summary>
    public static List<Snippet> Read(object value, Action<string> warn)
    {
        var reader = new SnippetReader(warn);
        switch (value)
        {
            case TomlTable table: reader.ReadTable(table); break;
            case List<object> array: reader.ReadArray(array); break;
            default:
                warn("snippets: expected a table ([snippets] name = \"text\") or an array of tables ([[snippets]] name/text)");
                break;
        }
        return reader.Result;
    }

    private sealed class SnippetReader(Action<string> warn)
    {
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

        public List<Snippet> Result { get; } = [];

        public void ReadTable(TomlTable table)
        {
            foreach (string key in table.Keys)
            {
                table.TryGet(key, out var v, out _);
                switch (v)
                {
                    case string text: Add(key, text, null, "snippets"); break;
                    case TomlTable entry: ReadTableEntry(key, entry); break;
                    default: warn($"snippets.{key}: expected a string or {{ text = \"…\", description = \"…\" }}"); break;
                }
            }
        }

        private void ReadTableEntry(string key, TomlTable entry)
        {
            foreach (string k in entry.Keys.Where(field => field is not ("text" or "description")))
                warn($"snippets.{key}: unknown field '{k}' (text, description)");
            entry.TryGet("text", out var text, out _);
            entry.TryGet("description", out var description, out _);
            Add(key, text, description, "snippets");
        }

        public void ReadArray(List<object> array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is TomlTable entry) ReadArrayEntry(entry, i + 1);
                else warn($"snippets[{i + 1}]: expected a table with name and text");
            }
        }

        private void ReadArrayEntry(TomlTable entry, int number)
        {
            foreach (string k in entry.Keys.Where(field => field is not ("name" or "text" or "description")))
                warn($"snippets[{number}]: unknown field '{k}' (name, text, description)");
            entry.TryGet("name", out var n, out _);
            entry.TryGet("text", out var text, out _);
            entry.TryGet("description", out var description, out _);
            if (n is not string name) { warn($"snippets[{number}]: needs a name (a string)"); return; }
            Add(name, text, description, $"snippets[{number}]");
        }

        private void Add(string name, object? text, object? description, string where)
        {
            name = name.Trim();
            if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl))
            {
                warn($"{where}: a name must be 1–{MaxNameLength} characters without control characters");
                return;
            }
            if (text is not string t || t.Length == 0) { warn($"{where}: \"{name}\" needs a text (a non-empty string)"); return; }
            if (t.Length > MaxTextLength) { warn($"{where}: \"{name}\" is longer than {MaxTextLength / 1024} KiB"); return; }
            if (description is not (null or string)) { warn($"{where}: \"{name}\": description must be a string"); return; }
            if (!_names.Add(name)) { warn($"{where}: \"{name}\" is defined twice (the first one counts)"); return; }
            Result.Add(new Snippet(name, t, (description as string)?.Trim() is { Length: > 0 } d ? d : null));
        }
    }

    /// <summary>
    /// Fills in the placeholders: <c>{{cwd}}</c> becomes <paramref name="cwd"/> and <c>{{selection}}</c>
    /// <paramref name="selection"/> (empty when there is none), as they are — no quoting; <c>{{{{</c> is a literal
    /// <c>{{</c>. Other <c>{{…}}</c> stay as written. The result still goes through the paste sanitizer.
    /// </summary>
    public static string Expand(string text, string? cwd, string? selection)
    {
        if (!text.Contains("{{", StringComparison.Ordinal)) return text;
        var sb = new StringBuilder(text.Length + 64);
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, open - i);
            if (string.CompareOrdinal(text, open, "{{{{", 0, 4) == 0) { sb.Append("{{"); i = open + 4; continue; }
            int close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { sb.Append(text, open, text.Length - open); break; }
            string name = text[(open + 2)..close].Trim();
            switch (name)
            {
                case "cwd": sb.Append(cwd ?? ""); break;
                case "selection": sb.Append(selection ?? ""); break;
                default: sb.Append(text, open, close + 2 - open); break;
            }
            i = close + 2;
        }
        return sb.ToString();
    }
}
