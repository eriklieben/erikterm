using System.Runtime.CompilerServices;
using System.Text;
using ErikTerm.App.Ipc;
using ErikTerm.Core;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.Tests;

/// <summary>
/// Two terminals get the same random output, resizes and clears. One is compacted (and so packed) at random points, the
/// other never: whatever a reader asks, they must answer alike. Set ERIKTERM_PACKING_SEEDS to "first:count" to run more
/// seeds, and ERIKTERM_PACKING_STEPS for longer runs.
/// </summary>
public class PackedScrollbackDifferentialTests
{
    private const int DefaultFirstSeed = 1, DefaultSeeds = 6, DefaultSteps = 110;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_scrollback")]
    private static extern ref Scrollback ScrollbackOf(Terminal t);

    private static readonly string[] Words = ["zebra", "build ok", "src/main.rs:12", "warning: unused", "compile", "a", "?", "error[E0308]", "needle", "-- x --"];
    private static readonly string[] WideText = ["漢字", "かな", "日本語テキスト", "Ａ", "\U0001F600", "\U0001F680"];

    private static readonly string[] ClusterText =
    [
        "\U0001F469‍\U0001F469‍\U0001F467‍\U0001F466",
        "\U0001F1F3\U0001F1F1",
        "\U0001F44D\U0001F3FD",
        "é",
        "❤️",
        "\U0001F3F3️‍\U0001F308",
        "क्ष",
    ];

    private readonly record struct Mix(Random R, Terminal Packed, Terminal Whole, int Seed);

    [Test]
    public async Task A_compacted_terminal_reads_like_one_that_never_compacted()
    {
        // Arrange
        (int first, int count) = Seeds();
        int steps = int.TryParse(Environment.GetEnvironmentVariable("ERIKTERM_PACKING_STEPS"), out int s) ? s : DefaultSteps;

        // Act
        long packed = 0;
        string? failure = null;
        for (int seed = first; seed < first + count && failure is null; seed++)
            failure = Run(seed, steps, ref packed);

        // Assert
        await Assert.That(failure).IsNull();
        await Assert.That(packed).IsGreaterThan(0); // the comparison means nothing if no row was ever packed
    }

    private static (int First, int Count) Seeds()
    {
        var text = Environment.GetEnvironmentVariable("ERIKTERM_PACKING_SEEDS")?.Split(':');
        return text is [var a, var b] && int.TryParse(a, out int first) && int.TryParse(b, out int count) ? (first, count) : (DefaultFirstSeed, DefaultSeeds);
    }

    private static string? Run(int seed, int steps, ref long packedRows)
    {
        var r = new Random(seed);
        int cols = r.Next(12, 130), rows = r.Next(3, 30), capacity = r.Next(5) == 0 ? r.Next(2, 15) : r.Next(20, 400);
        var m = new Mix(r, NewTerminal(cols, rows, capacity), NewTerminal(cols, rows, capacity), seed);
        var searchPacked = new TerminalSearch();
        var searchWhole = new TerminalSearch();
        searchPacked.SetQuery("needle", caseSensitive: false, regex: false);
        searchWhole.SetQuery("needle", caseSensitive: false, regex: false);

        for (int step = 0; step < steps; step++)
        {
            string op = Apply(m);
            var versions = Versions(m.Packed);
            Compact(m);
            packedRows += PackedRows(m.Packed);
            string? diff = SameVersions(m.Packed, versions) ?? Compare(m) ?? CompareSearch(m, searchPacked, searchWhole);
            if (diff is not null) return $"seed {seed}, step {step}, after {op}: {diff}";
            if (r.Next(5) != 0) Compact(m); // usually start the next step from packed rows, which is what leaving the ring is about
        }
        return null;
    }

    private static Terminal NewTerminal(int cols, int rows, int capacity) => new(cols, rows, capacity) { CommandClock = static () => 0 };

    private static void Compact(in Mix m)
    {
        if (m.R.Next(3) == 0)
        {
            m.Packed.CompactScrollback(m.R.Next(1, 300));
            return;
        }
        while (m.Packed.CompactScrollback(m.R.Next(1, 300))) { }
    }

    private static long PackedRows(Terminal t)
    {
        var scrollback = ScrollbackOf(t);
        long n = 0;
        for (int i = 0; i < scrollback.Count; i++) if (scrollback.Peek(i).IsPacked) n++;
        return n;
    }

    // ---- What happens to both terminals ------------------------------------------------------------

    private static string Apply(in Mix m)
    {
        var r = m.R;
        int pick = r.Next(100);
        if (pick < 50) return Output(m, Lines(r, m.Packed.Columns, r.Next(1, 8)), "lines");
        if (pick < 60) return Output(m, Lines(r, m.Packed.Columns, r.Next(40, m.Packed.Rows + 120)), "flood");
        if (pick < 67) return Prompt(m);
        if (pick < 73) return Reflow(m);
        if (pick < 76) return Resize(m, m.Packed.Columns, r.Next(2, 40), "rows");
        if (pick < 81) return Output(m, Editing(r, m.Packed), "editing");
        if (pick < 85) return Output(m, ClearSequence(r), "clear");
        if (pick < 88) return AltScreen(m);
        if (pick < 91) return Capacity(m);
        if (pick < 95) return ScrollView(m);
        if (pick < 98)
        {
            m.Packed.ReleaseSpareRows();
            m.Whole.ReleaseSpareRows();
            return "release spare rows";
        }
        return Output(m, "\ePq#0;2;100;0;0#1;2;0;100;0#0!12~-#1!12~\e\\\r\n", "sixel");
    }

    private static string Output(in Mix m, string text, string what)
    {
        var data = Encoding.UTF8.GetBytes(text);
        for (int at = 0; at < data.Length;)
        {
            int n = Math.Min(data.Length - at, 1 + m.R.Next(m.R.Next(3) == 0 ? 8 : 4096));
            var chunk = data.AsSpan(at, n);
            m.Packed.Feed(chunk);
            m.Whole.Feed(chunk);
            at += n;
            if (m.R.Next(4) == 0) m.Packed.CompactScrollback(m.R.Next(1, 300));
        }
        return what;
    }

    private static string Prompt(in Mix m)
    {
        int exit = m.R.Next(3) == 0 ? 2 : 0;
        string text = $"\e]133;A\e\\$ \e]133;B\e\\{Word(m.R)}\r\n\e]133;C;cmdline_url={Word(m.R)}\e\\{Lines(m.R, m.Packed.Columns, m.R.Next(0, 6))}\e]133;D;{exit}\e\\";
        return Output(m, text, "prompt");
    }

    private static string Reflow(in Mix m)
    {
        int cols = m.R.Next(8, 140);
        return Resize(m, cols, m.R.Next(2, 40), "reflow");
    }

    private static string Resize(in Mix m, int cols, int rows, string what)
    {
        m.Packed.Resize(cols, rows);
        m.Whole.Resize(cols, rows);
        return $"{what} {cols}x{rows}";
    }

    private static string AltScreen(in Mix m) =>
        Output(m, m.Packed.IsAlternateScreen ? "\e[?1049l" : $"\e[?1049h\e[2J\e[H{Lines(m.R, m.Packed.Columns, 3)}", "alternate screen");

    private static string Capacity(in Mix m)
    {
        int capacity = m.R.Next(4) == 0 ? m.R.Next(0, 6) : m.R.Next(10, 500);
        m.Packed.ScrollbackCapacity = capacity;
        m.Whole.ScrollbackCapacity = capacity;
        return $"capacity {capacity}";
    }

    private static string ScrollView(in Mix m)
    {
        int delta = m.R.Next(-300, 300);
        m.Packed.ScrollView(delta);
        m.Whole.ScrollView(delta);
        return $"scroll view {delta}";
    }

    private static string ClearSequence(Random r) => r.Next(7) switch
    {
        0 => "\e[2J",
        1 => "\e[3J",
        2 => "\e[H\e[J",
        3 => "\ec",
        4 => "\e[1J",
        5 => "\e[K",
        _ => "\e[2J\e[3J\e[H",
    };

    private static string Editing(Random r, Terminal t)
    {
        int y = r.Next(1, Math.Max(2, t.Rows + 1)), x = r.Next(1, Math.Max(2, t.Columns + 1));
        return r.Next(8) switch
        {
            0 => $"\e[{y};{x}H{Word(r)}",
            1 => $"\e[{y};{x}H\e[{r.Next(1, 5)}@",
            2 => $"\e[{y};{x}H\e[{r.Next(1, 5)}P",
            3 => $"\e[{y};{x}H\e[{r.Next(1, 5)}X",
            4 => $"\e[{y};{x}H\e[{r.Next(1, 5)}L",
            5 => $"\e[{y};{x}H\e[{r.Next(1, 5)}M",
            6 => $"\e[{r.Next(1, 8)}S",
            _ => $"\e[1;{Math.Max(1, t.Rows - 1)}r\e[{y};1H\n\n\n\e[r",
        };
    }

    private static string Lines(Random r, int cols, int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) AppendLine(sb, r, cols);
        return sb.ToString();
    }

    private static string Word(Random r) => Words[r.Next(Words.Length)];

    private static void AppendLine(StringBuilder sb, Random r, int cols)
    {
        int parts = r.Next(6) == 0 ? r.Next(8, 40) : r.Next(1, 6);
        for (int i = 0; i < parts; i++)
        {
            sb.Append(Style(r));
            bool link = r.Next(6) == 0;
            if (link) sb.Append("\e]8;;https://example.com/").Append(r.Next(5)).Append("\e\\");
            AppendText(sb, r, cols);
            if (link) sb.Append("\e]8;;\e\\");
        }
        if (r.Next(4) != 0) sb.Append("\e[0m");
        if (r.Next(8) == 0) sb.Append("\e[41m\e[K\e[0m"); // erased with a background, which blank trimming has to keep
        switch (r.Next(10))
        {
            case 0: break;                      // run into the next line
            case 1: sb.Append('\n'); break;
            default: sb.Append("\r\n"); break;
        }
    }

    private static void AppendText(StringBuilder sb, Random r, int cols)
    {
        switch (r.Next(10))
        {
            case 0: sb.Append('x', r.Next(1, cols * 3)); break;    // long enough to soft-wrap
            case 1: sb.Append(WideText[r.Next(WideText.Length)]); break;
            case 2: sb.Append(ClusterText[r.Next(ClusterText.Length)]); break;
            case 3: sb.Append('\t'); break;
            case 4: sb.Append('é').Append('α'); break;   // non-ASCII but narrow
            case 5: sb.Append(string.Concat(Enumerable.Repeat(WideText[r.Next(WideText.Length)], r.Next(1, cols)))); break; // wide runs reaching the edge
            default: sb.Append(Word(r)).Append(' '); break;
        }
    }

    private static string Style(Random r) => r.Next(14) switch
    {
        0 => "\e[0m",
        1 => $"\e[{31 + r.Next(6)}m",
        2 => "\e[1;32m",
        3 => $"\e[38;5;{r.Next(256)}m",
        4 => $"\e[48;5;{r.Next(256)}m",
        5 => $"\e[38;2;{r.Next(256)};{r.Next(256)};{r.Next(256)}m",
        6 => $"\e[48;2;{r.Next(256)};{r.Next(256)};{r.Next(256)}m",
        7 => $"\e[4:{1 + r.Next(5)}m\e[58;5;{r.Next(256)}m",
        8 => $"\e[4m\e[58:2::{r.Next(256)}:{r.Next(256)}:{r.Next(256)}m",
        9 => "\e[7m",
        10 => "\e[9;53m",
        11 => "\e[2;3m",
        _ => "",
    };

    // ---- What every reader must agree on -----------------------------------------------------------

    private static List<(long Line, int Version)> Versions(Terminal t)
    {
        var versions = new List<(long, int)>();
        for (long line = t.FirstLine; line < t.EndLine; line++)
            if (t.GetLine(line) is { } row) versions.Add((line, row.Version));
        return versions;
    }

    private static string? SameVersions(Terminal t, List<(long Line, int Version)> before)
    {
        foreach (var (line, version) in before)
            if (t.GetLine(line) is { } row && row.Version != version) return $"compaction changed the version of line {line}";
        return null;
    }

    private static string? Compare(in Mix m)
    {
        var (r, a, b, _) = m;
        return Field("columns", a.Columns, b.Columns) ?? Field("rows", a.Rows, b.Rows)
            ?? Field("first line", a.FirstLine, b.FirstLine) ?? Field("scrollback", a.ScrollbackCount, b.ScrollbackCount)
            ?? Field("end line", a.EndLine, b.EndLine) ?? Field("view", a.ViewOffset, b.ViewOffset)
            ?? Field("cursor x", a.CursorX, b.CursorX) ?? Field("cursor y", a.CursorY, b.CursorY)
            ?? Field("reflows", a.ReflowCount, b.ReflowCount) ?? Field("content", a.ContentVersion, b.ContentVersion)
            ?? EveryRow(a, b) ?? VisibleRows(a, b) ?? Text(r, a, b) ?? Shell(r, a, b) ?? Vim(r, a, b);
    }

    private static string? Field<T>(string name, T a, T b) => EqualityComparer<T>.Default.Equals(a, b) ? null : $"{name}: {a} against {b}";

    private static string? EveryRow(Terminal a, Terminal b)
    {
        for (long line = a.FirstLine; line < a.EndLine; line++)
        {
            string? diff = SameRow(a, b, line, a.GetLine(line), b.GetLine(line));
            if (diff is not null) return $"line {line}: {diff}";
        }
        return null;
    }

    private static string? VisibleRows(Terminal a, Terminal b)
    {
        for (int y = 0; y < a.Rows; y++)
        {
            string? diff = SameRow(a, b, y, a.GetVisibleRow(y), b.GetVisibleRow(y));
            if (diff is not null) return $"visible row {y}: {diff}";
        }
        return null;
    }

    private static string? SameRow(Terminal ta, Terminal tb, long line, Row? a, Row? b)
    {
        if (a is null || b is null) return a is null && b is null ? null : "missing on one side";
        string? head = Field("wrapped", a.Wrapped, b.Wrapped) ?? Field("width", a.Width, b.Width) ?? Field("marks", a.Marks, b.Marks)
            ?? Field("command column", a.CommandColumn, b.CommandColumn) ?? Field("exit code", a.ExitCode, b.ExitCode)
            ?? Field("command line", a.CommandLine, b.CommandLine) ?? Field("links", LinksOf(a), LinksOf(b));
        if (head is not null) return head;
        for (int x = 0; x <= a.Width + 1; x++)
        {
            string? cell = SameCell(ta, tb, a, b, x);
            if (cell is not null) return $"col {x}: {cell}";
        }
        return null;
    }

    private static string LinksOf(Row row) => row.Links is { } links ? string.Join(' ', links.Select(s => $"{s.Start}-{s.End}:{s.Id}")) : "";

    private static string? SameCell(Terminal ta, Terminal tb, Row a, Row b, int x)
    {
        var ca = a.CellAt(x);
        var cb = b.CellAt(x);
        if (ca.Rune != cb.Rune || ca.Fg != cb.Fg || ca.Bg != cb.Bg || ca.Flags != cb.Flags)
            return $"cell {ca.Rune:X}/{ca.Fg:X}/{ca.Bg:X}/{ca.Flags} against {cb.Rune:X}/{cb.Fg:X}/{cb.Bg:X}/{cb.Flags}";
        bool isA = a.TryGetCluster(x, out var textA), isB = b.TryGetCluster(x, out var textB);
        if (isA != isB || !textA.SequenceEqual(textB)) return $"cluster {textA} against {textB}";
        if (a.LinkIdAt(x) != b.LinkIdAt(x)) return $"link id {a.LinkIdAt(x)} against {b.LinkIdAt(x)}";
        if (ta.GetHyperlink(a.LinkIdAt(x))?.Uri != tb.GetHyperlink(b.LinkIdAt(x))?.Uri) return "link uri";
        return (ca.Flags & CellAttributes.Underline) != 0 && a.UnderlineColorAt(x) != b.UnderlineColorAt(x)
            ? $"underline color {a.UnderlineColorAt(x):X} against {b.UnderlineColorAt(x):X}"
            : null;
    }

    private static string? Text(Random r, Terminal a, Terminal b)
    {
        long first = a.FirstLine, last = a.EndLine - 1;
        long l1 = first + r.NextInt64(Math.Max(1, last - first + 1)), l2 = l1 + r.Next(40);
        int c1 = r.Next(a.Columns), c2 = r.Next(a.Columns);
        var built = (new LineText(), new LineText());
        built.Item1.Build(a, first, last);
        built.Item2.Build(b, first, last);
        var logical = (new LineText(), new LineText());
        logical.Item1.BuildLogicalLine(a, l1);
        logical.Item2.BuildLogicalLine(b, l1);
        return Field("all text", a.GetText(first, 0, last, int.MaxValue), b.GetText(first, 0, last, int.MaxValue))
            ?? Field("range text", a.GetText(l1, c1, l2, c2), b.GetText(l1, c1, l2, c2))
            ?? Field("block text", a.GetText(l1, c1, l2, c2, block: true), b.GetText(l1, c1, l2, c2, block: true))
            ?? Field("word", a.WordAt(l1, c1), b.WordAt(l1, c1))
            ?? Field("line text", built.Item1.Text, built.Item2.Text)
            ?? Field("logical line", logical.Item1.Text, logical.Item2.Text)
            ?? Field("export", string.Join('\n', a.ExportStyledLines(100_000)), string.Join('\n', b.ExportStyledLines(100_000)))
            ?? Field("plain", TextExport.Plain(a, -100_000, null), TextExport.Plain(b, -100_000, null))
            ?? Field("rows", TextExport.PlainRows(a, -100_000, null), TextExport.PlainRows(b, -100_000, null))
            ?? Field("escapes", TextExport.WithEscapes(a, -100_000, null), TextExport.WithEscapes(b, -100_000, null));
    }

    private static string? Shell(Random r, Terminal a, Terminal b)
    {
        long line = a.FirstLine + r.NextInt64(Math.Max(1, a.EndLine - a.FirstLine));
        return Field("prompt up", a.FindPrompt(line, -1), b.FindPrompt(line, -1))
            ?? Field("prompt down", a.FindPrompt(line, 1), b.FindPrompt(line, 1))
            ?? Field("command", a.CommandAt(line), b.CommandAt(line))
            ?? Field("has prompts", a.HasPromptMarks, b.HasPromptMarks);
    }

    private static string? Vim(Random r, Terminal a, Terminal b)
    {
        long line = a.FirstLine + r.NextInt64(Math.Max(1, a.EndLine - a.FirstLine));
        int col = r.Next(a.Columns);
        return Field("vim last line", VimBuffer.LastLine(a), VimBuffer.LastLine(b))
            ?? Field("vim last col", VimBuffer.LastContentCol(a, line), VimBuffer.LastContentCol(b, line))
            ?? Field("vim first", VimBuffer.FirstNonBlank(a, line), VimBuffer.FirstNonBlank(b, line))
            ?? Field("vim width", VimBuffer.Width(a, line), VimBuffer.Width(b, line))
            ?? Field("vim wrapped", VimBuffer.Wrapped(a, line), VimBuffer.Wrapped(b, line))
            ?? Field("vim lead", VimBuffer.Lead(a, line, col), VimBuffer.Lead(b, line, col))
            ?? Field("vim cell width", VimBuffer.CellWidth(a, line, col), VimBuffer.CellWidth(b, line, col))
            ?? Field("vim rune", VimBuffer.Rune(a, line, col), VimBuffer.Rune(b, line, col))
            ?? Field("vim next", VimBuffer.NextCol(a, line, col), VimBuffer.NextCol(b, line, col));
    }

    private static string? CompareSearch(in Mix m, TerminalSearch a, TerminalSearch b)
    {
        // Both keep their scan between steps, as the find bar does while output scrolls.
        do { a.Step(m.Packed, m.R.Next(8, 2000)); } while (!a.Complete);
        do { b.Step(m.Whole, 4000); } while (!b.Complete);
        return Field("search", string.Join(' ', a.Matches), string.Join(' ', b.Matches));
    }
}
