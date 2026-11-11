using System.Text;

namespace ErikTerm.Ui.Compare;

/// <summary>Which kinds of difference count as noise. Noise is shown dimmed, never hidden.</summary>
public readonly record struct NoiseRules(bool Whitespace, bool LineEndings, bool Comments)
{
    /// <summary>Whitespace and comments on, line endings off, as on the design board.</summary>
    public static readonly NoiseRules Default = new(Whitespace: true, LineEndings: false, Comments: true);

    public bool Any => Whitespace || LineEndings || Comments;
}

/// <summary>Comment and string syntax of a file type; config-free for now, extended by adding a table entry.</summary>
public sealed record LanguageSyntax(string[] LineComments, (string Open, string Close)[] BlockComments, bool SingleQuoteStrings = true, string[]? MultilineStrings = null)
{
    public string[] MultilineStrings { get; init; } = MultilineStrings ?? [];

    private static readonly (string, string)[] CBlock = [("/*", "*/")];

    private static readonly LanguageSyntax C = new(["//"], CBlock);
    private static readonly LanguageSyntax Rust = new(["//"], CBlock, SingleQuoteStrings: false);
    private static readonly LanguageSyntax CSharp = new(["//"], CBlock) { MultilineStrings = ["\"\"\""] };
    private static readonly LanguageSyntax Js = new(["//"], CBlock) { MultilineStrings = ["`"] };
    private static readonly LanguageSyntax Go = new(["//"], CBlock) { MultilineStrings = ["`"] };
    private static readonly LanguageSyntax Css = new([], CBlock);
    private static readonly LanguageSyntax Hash = new(["#"], []);
    private static readonly LanguageSyntax Python = new(["#"], []) { MultilineStrings = ["\"\"\"", "'''"] };
    private static readonly LanguageSyntax Ruby = new(["#"], [("=begin", "=end")]);
    private static readonly LanguageSyntax Shell = new(["#"], []);
    private static readonly LanguageSyntax Toml = new(["#"], []) { MultilineStrings = ["\"\"\"", "'''"] };
    private static readonly LanguageSyntax Ini = new([";", "#"], []);
    private static readonly LanguageSyntax Sql = new(["--"], CBlock);
    private static readonly LanguageSyntax Lua = new(["--"], [("--[[", "]]")]);
    private static readonly LanguageSyntax Haskell = new(["--"], [("{-", "-}")]);
    private static readonly LanguageSyntax Lisp = new([";"], [("#|", "|#")], SingleQuoteStrings: false);
    private static readonly LanguageSyntax Markup = new([], [("<!--", "-->")], SingleQuoteStrings: false);
    private static readonly LanguageSyntax PowerShell = new(["#"], [("<#", "#>")]);
    private static readonly LanguageSyntax Vim = new(["\""], []);
    private static readonly LanguageSyntax Bat = new(["::", "REM ", "rem "], [], SingleQuoteStrings: false);
    private static readonly LanguageSyntax Fsharp = new(["//"], [("(*", "*)")]);
    private static readonly LanguageSyntax Clojure = new([";"], [], SingleQuoteStrings: false);

    private static readonly Dictionary<string, LanguageSyntax> ByExtension = BuildTable();

    private static Dictionary<string, LanguageSyntax> BuildTable()
    {
        var table = new Dictionary<string, LanguageSyntax>(StringComparer.OrdinalIgnoreCase);
        void Add(LanguageSyntax syntax, params string[] extensions) { foreach (string e in extensions) table[e] = syntax; }
        Add(C, ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".java", ".swift", ".kt", ".kts", ".scala", ".dart", ".m", ".mm", ".proto", ".zig", ".v", ".glsl", ".hlsl", ".php");
        Add(CSharp, ".cs");
        Add(Rust, ".rs");
        Add(Js, ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs", ".json5");
        Add(Go, ".go");
        Add(Css, ".css", ".scss", ".less");
        Add(Hash, ".mk", ".cmake", ".dockerfile", ".cfg", ".conf", ".editorconfig", ".gitignore", ".env", ".r");
        Add(Python, ".py", ".pyi");
        Add(Ruby, ".rb", ".rake");
        Add(Shell, ".sh", ".bash", ".zsh", ".fish", ".yml", ".yaml", ".pl", ".pm", ".tf");
        Add(Toml, ".toml");
        Add(Ini, ".ini");
        Add(Sql, ".sql");
        Add(Lua, ".lua");
        Add(Haskell, ".hs");
        Add(Lisp, ".lisp", ".el", ".scm");
        Add(Clojure, ".clj", ".cljs");
        Add(Markup, ".html", ".htm", ".xml", ".svg", ".xaml", ".vue", ".md", ".markdown", ".csproj", ".props", ".targets");
        Add(PowerShell, ".ps1", ".psm1");
        Add(Vim, ".vim");
        Add(Bat, ".bat", ".cmd");
        Add(Fsharp, ".fs", ".fsx", ".ml", ".mli");
        return table;
    }

    /// <summary>Syntax for a path by its extension (or a well-known file name), or null when unknown.</summary>
    public static LanguageSyntax? For(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        if (name.Equals("Makefile", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)) return Hash;
        string ext = System.IO.Path.GetExtension(name);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var syntax) ? syntax : null;
    }
}

/// <summary>A line's comparison key under the rules, and whether the line is pure noise (a comment or blank line).</summary>
public readonly record struct NormalizedLine(string Key, bool Ignorable);

/// <summary>
/// Reduces lines to what the rules consider significant: walks each line with the file's comment and string syntax
/// (whitespace inside strings stays significant), carrying block-comment and multi-line-string state across lines.
/// </summary>
public sealed class LineNormalizer(NoiseRules rules, LanguageSyntax? syntax)
{
    private readonly StringBuilder _out = new();
    private int _blockOpen = -1;       // index of the block comment we're inside
    private string? _multilineString;  // delimiter of the string we're inside

    public NormalizedLine Normalize(string rawLine)
    {
        var content = TextDocument.Content(rawLine);
        string eol = rules.LineEndings ? "" : rawLine[content.Length..];
        if (!rules.Whitespace && !rules.Comments) return new NormalizedLine(string.Concat(content, eol), false);

        _out.Clear();
        bool hadComment = Scan(content);
        if (rules.Comments && hadComment) TrimTrailingWhitespace(); // "x; // note" and "x;" are the same code
        string key = _out.ToString();
        bool blankWhitespace = rules.Whitespace && key.Length == 0 && content.Trim().IsEmpty;
        bool commentOnly = rules.Comments && hadComment && key.Length == 0;
        return new NormalizedLine(key + eol, blankWhitespace || commentOnly);
    }

    private void TrimTrailingWhitespace()
    {
        int length = _out.Length;
        while (length > 0 && char.IsWhiteSpace(_out[length - 1])) length--;
        _out.Length = length;
    }

    /// <summary>Copies the significant characters of the line into the buffer; returns whether a comment was seen.</summary>
    private bool Scan(ReadOnlySpan<char> line)
    {
        bool comment = false;
        int i = 0;
        while (i < line.Length)
        {
            var step = ScanStructure(line, ref i, ref comment);
            if (step == ScanStep.EndOfLine) return comment;
            if (step == ScanStep.Consumed) continue;

            char c = line[i];
            if (rules.Whitespace && char.IsWhiteSpace(c)) { i++; continue; }
            _out.Append(c);
            i++;
        }
        return comment;
    }

    private enum ScanStep { Consumed, EndOfLine, PlainCharacter }

    /// <summary>Handles what the syntax gives meaning to at <paramref name="i"/>: an open comment or string, or the start of one.</summary>
    private ScanStep ScanStructure(ReadOnlySpan<char> line, ref int i, ref bool comment)
    {
        if (_blockOpen >= 0)
        {
            comment = true;
            return CloseBlockComment(line, ref i) ? ScanStep.Consumed : ScanStep.EndOfLine;
        }
        if (_multilineString is { } delimiter) return CloseMultilineString(line, ref i, delimiter) ? ScanStep.Consumed : ScanStep.EndOfLine;
        if (syntax is null) return ScanStep.PlainCharacter;
        if (TryBlockComment(line, ref i, ref comment)) return ScanStep.Consumed;
        if (TryLineComment(line, i, ref comment)) return ScanStep.EndOfLine;
        if (TryMultilineString(line, ref i)) return ScanStep.Consumed;
        return TryString(line, ref i) ? ScanStep.Consumed : ScanStep.PlainCharacter;
    }

    /// <summary>Scans the open block comment to its end; false when it goes past this line.</summary>
    private bool CloseBlockComment(ReadOnlySpan<char> line, ref int i)
    {
        string close = syntax!.BlockComments[_blockOpen].Close;
        int end = line[i..].IndexOf(close.AsSpan(), StringComparison.Ordinal);
        if (end < 0)
        {
            if (!rules.Comments) _out.Append(line[i..]);
            return false;
        }
        if (!rules.Comments) _out.Append(line.Slice(i, end + close.Length));
        i += end + close.Length;
        _blockOpen = -1;
        return true;
    }

    /// <summary>Scans the open string to its delimiter; false when it goes past this line.</summary>
    private bool CloseMultilineString(ReadOnlySpan<char> line, ref int i, string delimiter)
    {
        int end = line[i..].IndexOf(delimiter.AsSpan(), StringComparison.Ordinal);
        if (end < 0)
        {
            _out.Append(line[i..]);
            return false;
        }
        _out.Append(line.Slice(i, end + delimiter.Length));
        i += end + delimiter.Length;
        _multilineString = null;
        return true;
    }

    private bool TryBlockComment(ReadOnlySpan<char> line, ref int i, ref bool comment)
    {
        var blocks = syntax!.BlockComments;
        for (int k = 0; k < blocks.Length; k++)
        {
            if (!line[i..].StartsWith(blocks[k].Open, StringComparison.Ordinal)) continue;
            comment = true;
            if (!rules.Comments) _out.Append(blocks[k].Open);
            i += blocks[k].Open.Length;
            _blockOpen = k;
            return true;
        }
        return false;
    }

    private bool TryLineComment(ReadOnlySpan<char> line, int i, ref bool comment)
    {
        foreach (string token in syntax!.LineComments)
        {
            if (!line[i..].StartsWith(token, StringComparison.Ordinal)) continue;
            comment = true;
            if (!rules.Comments) _out.Append(line[i..]);
            return true;
        }
        return false;
    }

    private bool TryMultilineString(ReadOnlySpan<char> line, ref int i)
    {
        foreach (string delimiter in syntax!.MultilineStrings)
        {
            if (!line[i..].StartsWith(delimiter, StringComparison.Ordinal)) continue;
            _out.Append(delimiter);
            i += delimiter.Length;
            _multilineString = delimiter;
            return true;
        }
        return false;
    }

    private bool TryString(ReadOnlySpan<char> line, ref int i)
    {
        char c = line[i];
        if (c != '"' && !(c == '\'' && syntax!.SingleQuoteStrings)) return false;
        int start = i++;
        while (i < line.Length)
        {
            if (line[i] == '\\') { i += 2; continue; }
            if (line[i++] == c) break;
        }
        _out.Append(line[start..Math.Min(i, line.Length)]); // whitespace in a string is significant
        return true;
    }
}
