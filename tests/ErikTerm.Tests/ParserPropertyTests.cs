using System.Text;
using ErikTerm.Config;
using ErikTerm.Core;
using FsCheck;
using FsCheck.Fluent;

namespace ErikTerm.Tests;

/// <summary>
/// Property-based tests (FsCheck) for the code that reads untrusted input: the terminal's escape-sequence parser and
/// the TOML reader. The generated output is built from the pieces real sequences are made of (introducers, numbers,
/// separators, finals, string terminators, UTF-8), so it reaches deep into CSI/OSC/DCS/APC handling instead of
/// stopping at the first byte. Longer, coverage-guided runs of the same code: fuzz/ErikTerm.Fuzz.
/// </summary>
public class ParserPropertyTests
{
    private const int Runs = 300;

    private static readonly string[] Pieces =
    [
        "\e", "\e[", "\e[?", "\e[>", "\e]", "\eP", "\e_G", "\e\\", "\a", "\eP$q", "\eP+q", "\ePq", "\e#8", "\e(0", "\e(B",
        "\e[?2026h", "\e[?2026l", "\e[?1049h", "\e[?1049l", "\e[?69h", "\e[?6h", "\e[?7l",
        "0", "1", "2", "5", "9", "38", "48", "58", "255", "65535", "99999999999", ";", ":", "?", "$", " ", "\"", "#", "!",
        "m", "H", "J", "K", "r", "s", "u", "S", "T", "@", "P", "X", "L", "M", "b", "c", "n", "q", "t", "h", "l", "p", "~",
        "8;;", "8;id=1;https://x/", "52;c;aGk=", "133;A", "133;D;0", "1337;File=inline=1:", "4;1;rgb:ff/00/00", "10;?",
        "a=T,f=100;", "a=T,f=32,s=1,v=1;AAAAAA==", "m=1;", "a=d,d=A",
        "x", "\r", "\n", "\t", "\b", "一", "é", "\U0001F44D", "‍", "️", "\xc2", "\x9b", "\x9d",
    ];

    /// <summary>Program output: 0 to 60 pieces, as UTF-8 (a lone "\xc2" piece makes broken UTF-8 too).</summary>
    private static Gen<byte[]> Output() =>
        Gen.Elements(Pieces).ListOf().Select(parts => parts.Take(60).SelectMany(p => Latin1OrUtf8(p)).ToArray());

    // "\xc2", "\x9b", "\x9d" stand for those single bytes (C1 controls, a cut UTF-8 sequence); the rest is UTF-8.
    private static byte[] Latin1OrUtf8(string piece) =>
        piece.Length == 1 && piece[0] is >= '\x80' and <= '\xff' ? [(byte)piece[0]] : Encoding.UTF8.GetBytes(piece);

    private static (int Cols, int Rows) Size(int seed) => (1 + (seed & 0x7fff) % 100, 1 + (seed >> 16 & 0x7fff) % 30);

    private static string Screen(Terminal term)
    {
        var text = new StringBuilder();
        for (int y = 0; y < term.Rows; y++) text.Append(term.GetLineText(y)).Append('\n');
        return text.Append(term.CursorX).Append(',').Append(term.CursorY).ToString();
    }

    private static string FeedInPieces(byte[] output, int cols, int rows, IEnumerable<int> cuts)
    {
        var term = new Terminal(cols, rows, scrollbackLines: 100);
        int at = 0;
        foreach (int cut in cuts.Where(c => c > at && c < output.Length).Distinct().Order())
        {
            term.Feed(output.AsSpan(at, cut - at));
            at = cut;
        }
        term.Feed(output.AsSpan(at));
        term.Feed("\e[?2026l"u8);
        return Screen(term);
    }

    [Test]
    public async Task Output_split_into_reads_anywhere_gives_the_same_screen()
    {
        // Arrange
        var property = Prop.ForAll(Output().ToArbitrary(), ArbMap.Default.ArbFor<int>(), ArbMap.Default.ArbFor<int[]>(),
            (output, seed, cuts) =>
            {
                var (cols, rows) = Size(seed);
                var positions = cuts.Select(c => (int)((uint)c % (uint)(output.Length + 1)));
                return FeedInPieces(output, cols, rows, []) == FeedInPieces(output, cols, rows, positions);
            });

        // Act
        var failure = Record(() => Check.One(FsCheck.Config.QuickThrowOnFailure.WithMaxTest(Runs), property));

        // Assert
        await Assert.That(failure).IsNull();
    }

    [Test]
    public async Task Any_output_keeps_the_cursor_on_the_screen_after_a_resize()
    {
        // Arrange
        var property = Prop.ForAll(Output().ToArbitrary(), ArbMap.Default.ArbFor<int>(), ArbMap.Default.ArbFor<int>(),
            (output, seed, resize) =>
            {
                var (cols, rows) = Size(seed);
                var (newCols, newRows) = Size(resize);
                var term = new Terminal(cols, rows, scrollbackLines: 100);
                term.Feed(output);
                term.Feed("\e[?2026l"u8);
                bool before = term.CursorX >= 0 && term.CursorX <= cols && term.CursorY >= 0 && term.CursorY < rows;
                term.Resize(newCols, newRows);
                Screen(term);
                return before && term.CursorX >= 0 && term.CursorX <= newCols && term.CursorY >= 0 && term.CursorY < newRows;
            });

        // Act
        var failure = Record(() => Check.One(FsCheck.Config.QuickThrowOnFailure.WithMaxTest(Runs), property));

        // Assert
        await Assert.That(failure).IsNull();
    }

    private static readonly string[] TomlPieces =
    [
        "[", "]", "[[", "]]", "a", "b.c", "\"k\"", "'k'", " = ", "=", "1", "-0", "1_000", "0x1f", "1e3", "inf", "nan",
        "true", "false", "\"", "'", "\"\"\"", "'''", "\\", "\\u00e9", "\\U0001F44D", "{", "}", ",", "#", "\n", "\r\n",
        " ", "\t", "1979-05-27T07:32:00Z", "07:32:00", "é",
    ];

    [Test]
    public async Task Toml_reports_bad_input_only_as_TomlException()
    {
        // Arrange
        var documents = Gen.Elements(TomlPieces).ListOf().Select(parts => string.Concat(parts.Take(80)));
        var property = Prop.ForAll(documents.ToArbitrary(), text =>
        {
            try { Toml.Parse(text); }
            catch (TomlException) { }
        });

        // Act
        var failure = Record(() => Check.One(FsCheck.Config.QuickThrowOnFailure.WithMaxTest(Runs * 3), property));

        // Assert
        await Assert.That(failure).IsNull();
    }

    private static Exception? Record(Action check)
    {
        try { check(); return null; }
        catch (Exception e) { return e; }
    }
}
