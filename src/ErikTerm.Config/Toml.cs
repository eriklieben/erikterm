using System.Globalization;
using System.Text;

namespace ErikTerm.Config;

public sealed class TomlException(string message, int line) : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// A small TOML reader covering what a config file needs: comments, [tables], [a.b] / dotted keys,
/// basic and literal strings (incl. multi-line """ ), integers (with _ / 0x), floats, booleans,
/// arrays (multi-line, trailing commas), inline tables and arrays of tables (<c>[[name]]</c>, a list of tables). Not
/// supported: dates.
/// Values come back as string, long, double, bool, List&lt;object&gt; or TomlTable — no reflection.
/// </summary>
public static class Toml
{
    public static TomlTable Parse(string text) => new Reader(text).ReadDocument();

    private sealed class Reader(string s)
    {
        private int _pos;
        private int _line = 1;
        private int _depth;

        /// <summary>Deepest nesting of arrays and inline tables: deeper input is an error, not a stack overflow.</summary>
        public const int MaxDepth = 64;

        private void Enter()
        {
            if (++_depth > MaxDepth) throw Error($"arrays / inline tables nested more than {MaxDepth} deep");
        }

        public TomlTable ReadDocument()
        {
            var root = new TomlTable();
            var current = root;
            while (true)
            {
                SkipWhitespaceCommentsAndNewlines();
                if (_pos >= s.Length) return root;

                if (Peek == '[' && PeekAt(1) == '[')
                {
                    // [[a.b]]: a new table appended to the array a.b (the last table of an array on the way is entered).
                    int line = _line;
                    _pos += 2;
                    SkipInlineWhitespace();
                    var arrayPath = ReadKeyPath();
                    SkipInlineWhitespace();
                    Expect(']');
                    Expect(']');
                    current = root;
                    for (int i = 0; i < arrayPath.Count - 1; i++) current = current.GetOrAddTable(arrayPath[i], line);
                    current = current.AppendArrayTable(arrayPath[^1], line);
                    EndOfLine();
                    continue;
                }
                if (Peek == '[')
                {
                    _pos++;
                    SkipInlineWhitespace();
                    var path = ReadKeyPath();
                    SkipInlineWhitespace();
                    Expect(']');
                    current = root;
                    foreach (var part in path) current = current.GetOrAddTable(part, _line);
                    EndOfLine();
                    continue;
                }

                ReadKeyValue(current);
                EndOfLine();
            }
        }

        private void ReadKeyValue(TomlTable table)
        {
            int line = _line;
            var path = ReadKeyPath();
            SkipInlineWhitespace();
            Expect('=');
            SkipInlineWhitespace();
            var value = ReadValue();
            for (int i = 0; i < path.Count - 1; i++) table = table.GetOrAddTable(path[i], line);
            if (!table.TryAdd(path[^1], value, line))
                throw new TomlException($"duplicate key '{path[^1]}'", line);
        }

        private List<string> ReadKeyPath()
        {
            var parts = new List<string>();
            while (true)
            {
                SkipInlineWhitespace();
                parts.Add(Peek switch
                {
                    '"' => ReadBasicString(),
                    '\'' => ReadLiteralString(),
                    _ => ReadBareKey(),
                });
                SkipInlineWhitespace();
                if (Peek != '.') return parts;
                if (parts.Count >= MaxDepth) throw Error($"dotted key with more than {MaxDepth} parts");
                _pos++;
            }
        }

        private string ReadBareKey()
        {
            int start = _pos;
            while (_pos < s.Length && (char.IsAsciiLetterOrDigit(s[_pos]) || s[_pos] is '_' or '-')) _pos++;
            if (_pos == start) throw Error($"expected a key, found {Describe()}");
            return s[start.._pos];
        }

        private object ReadValue()
        {
            char c = Peek;
            if (c == '"') return StartsWith("\"\"\"") ? ReadMultilineBasicString() : ReadBasicString();
            if (c == '\'') return StartsWith("'''") ? ReadMultilineLiteralString() : ReadLiteralString();
            if (c == '[') return ReadArray();
            if (c == '{') return ReadInlineTable();
            if (StartsWith("true")) { _pos += 4; return true; }
            if (StartsWith("false")) { _pos += 5; return false; }
            if (c is '+' or '-' or '.' || char.IsAsciiDigit(c) || StartsWith("inf") || StartsWith("nan")) return ReadNumber();
            throw Error($"expected a value, found {Describe()}");
        }

        private object ReadNumber()
        {
            int start = _pos;
            while (_pos < s.Length && (char.IsAsciiLetterOrDigit(s[_pos]) || s[_pos] is '_' or '+' or '-' or '.')) _pos++;
            string raw = s[start.._pos].Replace("_", "");
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex)) return hex;
            if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l)) return l;
            if (raw is "inf" or "+inf") return double.PositiveInfinity;
            if (raw == "-inf") return double.NegativeInfinity;
            if (raw is "nan" or "+nan" or "-nan") return double.NaN;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            throw Error($"invalid number '{raw}'");
        }

        private List<object> ReadArray()
        {
            Expect('[');
            Enter();
            var list = new List<object>();
            while (true)
            {
                SkipWhitespaceCommentsAndNewlines();
                if (Peek == ']') { _pos++; _depth--; return list; }
                list.Add(ReadValue());
                SkipWhitespaceCommentsAndNewlines();
                if (Peek == ',') { _pos++; continue; }
                if (Peek == ']') { _pos++; _depth--; return list; }
                throw Error($"expected ',' or ']' in array, found {Describe()}");
            }
        }

        private TomlTable ReadInlineTable()
        {
            Expect('{');
            Enter();
            var table = new TomlTable { IsInline = true };
            SkipInlineWhitespace();
            if (Peek == '}') { _pos++; _depth--; return table; }
            while (true)
            {
                ReadKeyValue(table);
                SkipInlineWhitespace();
                if (Peek == ',') { _pos++; SkipInlineWhitespace(); continue; }
                if (Peek == '}') { _pos++; _depth--; return table; }
                throw Error($"expected ',' or '}}' in inline table, found {Describe()}");
            }
        }

        private string ReadBasicString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= s.Length || s[_pos] == '\n') throw Error("unterminated string");
                char c = s[_pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\') ReadEscape(sb);
                else sb.Append(c);
            }
        }

        private string ReadMultilineBasicString()
        {
            _pos += 3;
            SkipOneNewline();
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= s.Length) throw Error("unterminated \"\"\" string");
                if (StartsWith("\"\"\"")) { _pos += 3; return sb.ToString(); }
                char c = s[_pos++];
                if (c == '\n') _line++;
                if (c != '\\') { sb.Append(c); continue; }
                if (Peek is '\n' or '\r' or ' ' or '\t') SkipEscapedLineEnd();
                else ReadEscape(sb);
            }
        }

        /// <summary>A line-ending backslash: trims the newline and the leading whitespace of the next line.</summary>
        private void SkipEscapedLineEnd()
        {
            while (_pos < s.Length && s[_pos] is ' ' or '\t' or '\r' or '\n')
            {
                if (s[_pos] == '\n') _line++;
                _pos++;
            }
        }

        private void ReadEscape(StringBuilder sb)
        {
            if (_pos >= s.Length) throw Error("unterminated escape");
            char e = s[_pos++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'e': sb.Append('\e'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u': sb.Append(ReadUnicode(4)); break;
                case 'U': sb.Append(ReadUnicode(8)); break;
                default: throw Error($"invalid escape '\\{e}'");
            }
        }

        private string ReadUnicode(int digits)
        {
            if (_pos + digits > s.Length ||
                !int.TryParse(s.AsSpan(_pos, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp) ||
                cp > 0x10FFFF || cp is >= 0xD800 and <= 0xDFFF)
                throw Error("invalid unicode escape");
            _pos += digits;
            return char.ConvertFromUtf32(cp);
        }

        private string ReadLiteralString()
        {
            Expect('\'');
            int start = _pos;
            while (_pos < s.Length && s[_pos] != '\'' && s[_pos] != '\n') _pos++;
            if (_pos >= s.Length || s[_pos] != '\'') throw Error("unterminated string");
            return s[start.._pos++];
        }

        private string ReadMultilineLiteralString()
        {
            _pos += 3;
            SkipOneNewline();
            int start = _pos;
            int end = s.IndexOf("'''", _pos, StringComparison.Ordinal);
            if (end < 0) throw Error("unterminated ''' string");
            string value = s[start..end];
            _line += value.Count(ch => ch == '\n');
            _pos = end + 3;
            return value;
        }

        private void SkipOneNewline()
        {
            if (StartsWith("\r\n")) { _pos += 2; _line++; }
            else if (Peek == '\n') { _pos++; _line++; }
        }

        private void EndOfLine()
        {
            SkipInlineWhitespace();
            if (Peek == '#') while (_pos < s.Length && s[_pos] != '\n') _pos++;
            if (_pos < s.Length && s[_pos] == '\r') _pos++;
            if (_pos < s.Length && s[_pos] != '\n') throw Error($"expected end of line, found {Describe()}");
        }

        private void SkipInlineWhitespace()
        {
            while (_pos < s.Length && s[_pos] is ' ' or '\t') _pos++;
        }

        private void SkipWhitespaceCommentsAndNewlines()
        {
            while (_pos < s.Length)
            {
                char c = s[_pos];
                if (c is ' ' or '\t' or '\r') _pos++;
                else if (c == '\n') { _pos++; _line++; }
                else if (c == '#') { while (_pos < s.Length && s[_pos] != '\n') _pos++; }
                else break;
            }
        }

        private char Peek => _pos < s.Length ? s[_pos] : '\0';
        private char PeekAt(int offset) => _pos + offset < s.Length ? s[_pos + offset] : '\0';
        private bool StartsWith(string token) => string.CompareOrdinal(s, _pos, token, 0, token.Length) == 0;

        private void Expect(char c)
        {
            if (Peek != c) throw Error($"expected '{c}', found {Describe()}");
            _pos++;
        }

        private string Describe()
        {
            if (_pos >= s.Length) return "end of file";
            return s[_pos] == '\n' ? "end of line" : $"'{s[_pos]}'";
        }

        private TomlException Error(string message) => new(message, _line);
    }
}

/// <summary>A TOML table; remembers the line each key was defined on for error messages.</summary>
public sealed class TomlTable
{
    private readonly Dictionary<string, (object Value, int Line)> _entries = new(StringComparer.Ordinal);

    public IEnumerable<string> Keys => _entries.Keys;
    public int Count => _entries.Count;

    public bool TryGet(string key, out object value, out int line)
    {
        if (_entries.TryGetValue(key, out var e)) { value = e.Value; line = e.Line; return true; }
        value = null!;
        line = 0;
        return false;
    }

    public int LineOf(string key) => _entries.TryGetValue(key, out var e) ? e.Line : 0;

    /// <summary>One table of an array of tables (<c>[[name]]</c>).</summary>
    public bool IsArrayElement { get; private init; }

    /// <summary>[[key]]: appends a new table to the array at <paramref name="key"/> (creating it) and returns it.</summary>
    internal TomlTable AppendArrayTable(string key, int line)
    {
        var table = new TomlTable { IsArrayElement = true };
        if (_entries.TryGetValue(key, out var e))
        {
            if (e.Value is not List<object> list || (list.Count > 0 && list[0] is not TomlTable { IsArrayElement: true }))
                throw new TomlException($"'{key}' is already defined as a value or a table", line);
            list.Add(table);
        }
        else _entries[key] = (new List<object> { table }, line);
        return table;
    }

    /// <summary>Written as an inline table (<c>{ a = 1 }</c>): a whole value, replaced rather than merged by overrides.</summary>
    public bool IsInline { get; internal set; }

    /// <summary>Adds or replaces a key (used by command-line overrides).</summary>
    internal void Set(string key, object value, int line) => _entries[key] = (value, line);

    internal bool TryAdd(string key, object value, int line) => _entries.TryAdd(key, (value, line));

    internal TomlTable GetOrAddTable(string key, int line)
    {
        if (_entries.TryGetValue(key, out var e))
        {
            // [a.b] after [[a]]: b goes into the array's last table.
            if (e.Value is List<object> { Count: > 0 } list && list[^1] is TomlTable { IsArrayElement: true } last) return last;
            return e.Value as TomlTable ?? throw new TomlException($"'{key}' is already defined as a value", line);
        }
        var t = new TomlTable();
        _entries[key] = (t, line);
        return t;
    }
}
