using System.Text.RegularExpressions;

namespace ErikTerm.Ui;

/// <summary>
/// A file reference in text: <c>path:line[:col]</c>, MSBuild/dotnet <c>path(line,col)</c>, .NET stack frames
/// (<c>in /p/File.cs:line 42</c>), Python tracebacks (<c>File "x.py", line 3</c>) or a plain path.
/// [Start, Start + Length) is the span to underline; <see cref="Line"/>/<see cref="Column"/> are 0 when absent.
/// <see cref="Strong"/>: unambiguous enough to offer without checking the file exists (quick select);
/// the others (bare <c>name.ext</c>, <c>Console.WriteLine(1)</c>-like) only count once the file is found.
/// </summary>
public readonly record struct FileRefMatch(int Start, int Length, string Path, int Line, int Column, bool Strong)
{
    public int End => Start + Length;
}

/// <summary>
/// Finding file references in text (<see cref="FileRefMatch"/>): stack frames, tracebacks, compiler output, path:line
/// and bare file names. Pure string logic (source-generated regexes); <see cref="TextPatterns"/> is the front door.
/// </summary>
public static partial class FileRefPatterns
{
    // .NET stack frame: "   at Foo.Bar() in /src/App/File.cs:line 42" (paths may contain spaces).
    [GeneratedRegex(@"\bin (?<p>(?:[A-Za-z]:)?[\\/][^\r\n""<>|*?]*?):line (?<l>\d{1,7})\b", RegexOptions.CultureInvariant)]
    private static partial Regex StackFrameRegex();

    // Python traceback: File "/x/y.py", line 12
    [GeneratedRegex(@"\bFile ""(?<p>[^""\r\n]+)"", line (?<l>\d{1,7})\b", RegexOptions.CultureInvariant)]
    private static partial Regex PythonFrameRegex();

    // MSBuild / dotnet / tsc: /p/File.cs(12,5): error CS1002 — also (line) and (line,col,endLine,endCol).
    [GeneratedRegex(@"(?<![\w/.~\\@+=-])(?<p>(?:[A-Za-z]:)?[\w.@+,=~\\/-]*\.[A-Za-z]\w*)\((?<l>\d{1,7})(?:,(?<c>\d{1,5}))?(?:,\d{1,7},\d{1,5})?\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex MsBuildRefRegex();

    // grep/rg/gcc/eslint style: path:line[:col] (a path with a slash, or a bare name.ext).
    [GeneratedRegex(@"(?<![\w/.~@+=-])(?<p>(?:~|\.{1,2})?(?:[\w.@+,=-]+)?(?:/[\w.@+,=-]+)+|[\w@+,=-][\w.@+,=-]*\.[A-Za-z]\w*):(?<l>\d{1,7})(?::(?<c>\d{1,5}))?(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ColonRefRegex();

    // A bare file name with an extension (README.md, Program.cs): only a link when it exists.
    [GeneratedRegex(@"(?<![\w/.~@+=-])[\w@+=-][\w.@+=-]*\.[A-Za-z][A-Za-z0-9]{0,9}(?![\w/@+=-])", RegexOptions.CultureInvariant)]
    private static partial Regex BareFileRegex();

    /// <summary>File references in text order, without overlaps (and not inside URLs).</summary>
    public static List<FileRefMatch> Find(string text)
    {
        var refs = new FileRefCollector(text);
        refs.AddFrames(StackFrameRegex());
        refs.AddFrames(PythonFrameRegex());
        refs.AddMsBuildRefs();
        refs.AddColonRefs();
        refs.AddPaths();
        refs.AddBareFiles();
        return refs.InTextOrder();
    }

    /// <summary>Gathers references, each kept only when it overlaps neither a URL nor an earlier reference.</summary>
    private sealed class FileRefCollector
    {
        private readonly string _text;
        private readonly List<TextMatch> _urls;
        private readonly List<FileRefMatch> _found = [];

        public FileRefCollector(string text)
        {
            _text = text;
            _urls = TextPatterns.FindUrls(text);
        }

        public void AddFrames(Regex regex)
        {
            foreach (var groups in regex.Matches(_text).Select(m => m.Groups))
            {
                var p = groups["p"];
                var l = groups["l"];
                Add(p.Index, l.Index + l.Length - p.Index, p.Value.TrimEnd(), Int(l), 0, strong: true);
            }
        }

        public void AddMsBuildRefs()
        {
            foreach (Match m in MsBuildRefRegex().Matches(_text))
            {
                var p = m.Groups["p"].Value;
                bool strong = p.Contains('/') || p.Contains('\\') || (m.Index + m.Length < _text.Length && _text[m.Index + m.Length] == ':');
                Add(m.Index, m.Length, p, Int(m.Groups["l"]), Int(m.Groups["c"]), strong);
            }
        }

        public void AddColonRefs()
        {
            foreach (Match m in ColonRefRegex().Matches(_text))
            {
                var p = m.Groups["p"].Value;
                Add(m.Index, m.Length, p, Int(m.Groups["l"]), Int(m.Groups["c"]), p.Contains('/'));
            }
        }

        public void AddPaths()
        {
            foreach (var m in TextPatterns.PathRegex().EnumerateMatches(_text))
            {
                int len = TextPatterns.TrimUrl(_text.AsSpan(m.Index, m.Length));
                if (len < 3 || !_text.AsSpan(m.Index, len).ContainsAnyExcept('/', '.')) continue;
                // PathRegex allows a :line[:col] suffix; ColonRefRegex already took those.
                Add(m.Index, len, _text.Substring(m.Index, len), 0, 0, strong: true);
            }
        }

        public void AddBareFiles()
        {
            foreach (var m in BareFileRegex().EnumerateMatches(_text))
                Add(m.Index, m.Length, _text.Substring(m.Index, m.Length), 0, 0, strong: false);
        }

        public List<FileRefMatch> InTextOrder()
        {
            _found.Sort((a, b) => a.Start.CompareTo(b.Start));
            return _found;
        }

        private void Add(int start, int length, string path, int line, int col, bool strong)
        {
            if (path.Length == 0 || length <= 0) return;
            int end = start + length;
            foreach (var u in _urls) if (start < u.End && u.Start < end) return;
            foreach (var f in _found) if (start < f.End && f.Start < end) return;
            _found.Add(new FileRefMatch(start, length, path, line, col, strong));
        }

        private static int Int(Group g) => g.Success && int.TryParse(g.ValueSpan, out int v) ? v : 0;
    }

    /// <summary>The file reference spanning all of <paramref name="text"/> (e.g. a quick select match), or null.</summary>
    public static FileRefMatch? Parse(string text)
    {
        // Stack and traceback spans start at the path: put back the context their patterns need.
        foreach (string prefix in (ReadOnlySpan<string>)["", "in ", "File \""])
        {
            string probe = prefix + text;
            foreach (var m in Find(probe))
                if (m.Start == prefix.Length && m.Length == text.Length) return m with { Start = 0 };
        }
        return null;
    }
}
