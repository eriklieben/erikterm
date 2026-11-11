using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>Reflow keeps per-row metadata (OSC 8 links, OSC 133 marks) on its text; row versions track every change.</summary>
public class ReflowMetadataTests
{
    private const string A = "\e]133;A\e\\", B = "\e]133;B\e\\", C = "\e]133;C\e\\";

    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    private static string? UriAt(Terminal t, int x, int y) => t.GetHyperlink(t.GetScreenRow(y).LinkIdAt(x))?.Uri;

    /// <summary>"ab" + link "cdefgh" + "ij" on a terminal <paramref name="columns"/> wide.</summary>
    private static Terminal LinkedLine(int columns) => Feed(new Terminal(columns, 6), "ab\e]8;;http://x\e\\cdefgh\e]8;;\e\\ij");

    /// <summary>A long prompt that wraps; the command starts on its second row at 10 columns.</summary>
    private static Terminal WrappedPrompt() => Feed(new Terminal(10, 6), A + "0123456789abc" + B + "ls\r\n" + C + "out\r\n");

    [Test]
    public async Task ShrinkingRewrapsTheLinkedText()
    {
        // Arrange
        var t = LinkedLine(20);

        // Act
        t.Resize(4, 6);

        // Assert
        await Assert.That(t.GetScreenText()).IsEqualTo("abcd\nefgh\nij\n\n\n\n");
    }

    [Test]
    [Arguments(0, 0, null)]
    [Arguments(1, 0, null)]
    [Arguments(2, 0, "http://x")]
    [Arguments(3, 0, "http://x")]
    [Arguments(0, 1, "http://x")]
    [Arguments(1, 1, "http://x")]
    [Arguments(2, 1, "http://x")]
    [Arguments(3, 1, "http://x")]
    [Arguments(0, 2, null)]
    [Arguments(1, 2, null)]
    public async Task LinkSpansFollowTheirTextWhenShrinking(int x, int y, string? expected)
    {
        // Arrange
        // 20 columns -> 4 columns: "abcd" / "efgh" / "ij", the link on "cdefgh".
        var t = LinkedLine(20);

        // Act
        t.Resize(4, 6);
        string? uri = UriAt(t, x, y);

        // Assert
        await Assert.That(uri).IsEqualTo(expected);
    }

    [Test]
    public async Task GrowingRejoinsTheLinkedText()
    {
        // Arrange
        var t = LinkedLine(4);

        // Act
        t.Resize(20, 6);

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("abcdefghij");
    }

    [Test]
    public async Task LinkSpansRejoinWhenGrowing()
    {
        // Arrange
        var t = LinkedLine(4);

        // Act
        t.Resize(20, 6);
        var links = t.GetScreenRow(0).Links!;
        string? lastLinked = UriAt(t, 7, 0), afterLink = UriAt(t, 8, 0);

        // Assert
        var link = await Assert.That(links).HasSingleItem();
        await Assert.That((link.Start, link.End)).IsEqualTo((2, 8));
        await Assert.That(lastLinked).IsEqualTo("http://x");
        await Assert.That(afterLink).IsNull();
    }

    [Test]
    public async Task RowsThatHeldTheContinuationDropTheirSpansWhenGrowing()
    {
        // Arrange
        var t = LinkedLine(4);

        // Act
        t.Resize(20, 6);

        // Assert
        // No stale spans on the row that used to hold "efgh".
        await Assert.That(t.GetScreenRow(1).HasLinks).IsFalse();
    }

    [Test]
    public async Task WrappedPromptMarksItsFirstRowAndTheInputRow()
    {
        // Act
        var t = WrappedPrompt();

        // Assert
        await Assert.That(t.GetScreenRow(0).Marks).IsEqualTo(SemanticMarks.Prompt);
        await Assert.That(t.GetScreenRow(1).Marks).IsEqualTo(SemanticMarks.Input);
        await Assert.That(t.GetScreenRow(1).CommandColumn).IsEqualTo(3);
    }

    [Test]
    public async Task GrowingPutsPromptAndInputMarksOnTheLinesFirstRow()
    {
        // Arrange
        var t = WrappedPrompt();

        // Act
        t.Resize(20, 6);
        var block = t.CommandAt(t.ScreenTopLine + 1);

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("0123456789abcls");
        await Assert.That(t.GetScreenRow(0).Marks).IsEqualTo(SemanticMarks.Prompt | SemanticMarks.Input);
        await Assert.That(t.GetScreenRow(0).CommandColumn).IsEqualTo(13);
        await Assert.That(t.GetScreenRow(1).Marks).IsEqualTo(SemanticMarks.Output);
        await Assert.That(block).IsNotNull();
        await Assert.That(block!.Value.Command).IsEqualTo("ls");
    }

    [Test]
    public async Task ShrinkingKeepsThePromptMarkOnTheFirstRowAndMovesTheInputMarkWithItsColumn()
    {
        // Arrange
        // Grown to 20 columns first (the line is one row there), then shrunk.
        var t = WrappedPrompt();
        t.Resize(20, 6);

        // Act
        t.Resize(5, 6);
        long prompt = t.FindPrompt(t.EndLine, -1)!.Value;
        var promptRow = t.GetLine(prompt)!;
        var secondRow = t.GetLine(prompt + 1)!;
        var inputRow = t.GetLine(prompt + 2)!;
        var block = t.CommandAt(prompt + 3);

        // Assert
        // 0123456789abcls -> 3 rows; the command column 13 is on the third row at 3.
        await Assert.That(promptRow.Marks).IsEqualTo(SemanticMarks.Prompt);
        await Assert.That(secondRow.Marks).IsEqualTo(SemanticMarks.None);
        await Assert.That(inputRow.Marks).IsEqualTo(SemanticMarks.Input);
        await Assert.That(inputRow.CommandColumn).IsEqualTo(3);
        await Assert.That(block!.Value.Command).IsEqualTo("ls");
    }

    [Test]
    public async Task ExitCodeLandsOnThePromptAfterReflow()
    {
        // Arrange
        // The prompt line moves (an earlier long line re-wraps); OSC 133;D must still find it.
        var t = Feed(new Terminal(10, 8), "0123456789abcdefghij\r\n" + A + "$ " + B + "false\r\n" + C);
        t.Resize(5, 8);

        // Act
        Feed(t, "\e]133;D;1\e\\");
        long prompt = t.FindPrompt(t.EndLine, -1)!.Value;

        // Assert
        await Assert.That(t.GetLine(prompt)!.CommandFailed).IsTrue();
        await Assert.That(t.HasPromptMarks).IsTrue();
    }

    /// <summary>Every kind of write, in order (each builds on what the previous one left), as (what, input) pairs.</summary>
    private static readonly (string What, string Input)[] EveryKindOfWrite =
    [
        ("ascii", "abc"),
        ("print", "é"),
        ("combining mark (cluster)", "́"),
        ("insert chars", "\e[1G\e[2@"),
        ("delete chars", "\e[P"),
        ("erase chars", "\e[X"),
        ("erase line", "\e[K"),
        ("link", "\e]8;;http://x\e\\l\e]8;;\e\\"),
        ("prompt mark", A),
        ("erase display", "\e[2J"),
    ];

    /// <summary>Feeds every write and returns the writes that left row 0's version unchanged.</summary>
    private static List<string> WritesThatKeptTheVersion(Terminal t, IEnumerable<(string What, string Input)> writes)
    {
        var row = t.GetScreenRow(0);
        var unchanged = new List<string>();
        foreach (var (what, input) in writes)
        {
            int before = row.Version;
            Feed(t, input);
            if (row.Version == before) unchanged.Add(what);
        }
        return unchanged;
    }

    [Test]
    public async Task RowVersionBumpsOnEveryKindOfWrite()
    {
        // Arrange
        var t = new Terminal(10, 4);

        // Act
        var unchanged = WritesThatKeptTheVersion(t, [.. EveryKindOfWrite, ("overwrite", "\e[1;1Hxyz")]);

        // Assert
        await Assert.That(unchanged).IsEmpty();
    }

    [Test]
    public async Task RowsThatDidNotChangeKeepTheirVersion()
    {
        // Arrange
        var t = new Terminal(10, 4);
        WritesThatKeptTheVersion(t, EveryKindOfWrite);
        var other = t.GetScreenRow(3);
        int before = other.Version;

        // Act
        Feed(t, "\e[1;1Hxyz");

        // Assert
        await Assert.That(other.Version).IsEqualTo(before);
    }

    [Test]
    public async Task RecycledScrollRowsGetANewVersion()
    {
        // Arrange
        var t = new Terminal(10, 3, scrollbackLines: 2);
        Feed(t, "a\r\nb\r\nc\r\nd\r\ne\r\nf\r\n");
        // Rows are recycled from the evicted scrollback: each must look changed to a cache keyed by (row, version).
        var seen = new Dictionary<Row, int>(ReferenceEqualityComparer.Instance);
        for (int y = 0; y < 3; y++) seen[t.GetScreenRow(y)] = t.GetScreenRow(y).Version;

        // Act
        Feed(t, "g\r\nh\r\n");
        var stale = new List<int>();
        for (int y = 0; y < 3; y++)
        {
            var row = t.GetScreenRow(y);
            if (seen.TryGetValue(row, out int version) && t.GetLineText(y) != "" && version == row.Version) stale.Add(y);
        }

        // Assert
        await Assert.That(stale).IsEmpty();
    }

    [Test]
    public async Task PaletteVersionBumpsWhenTheForegroundChanges()
    {
        // Arrange
        var p = new Palette();
        int before = p.Version;

        // Act
        p.Foreground = 0x123456;

        // Assert
        await Assert.That(p.Version).IsNotEqualTo(before);
    }

    [Test]
    public async Task PaletteVersionBumpsWhenAnIndexedColorChanges()
    {
        // Arrange
        var p = new Palette();
        p.Foreground = 0x123456;
        int before = p.Version;

        // Act
        p[3] = 0xFF0000;

        // Assert
        await Assert.That(p.Version).IsNotEqualTo(before);
    }

    [Test]
    public async Task ReflowBumpsRowVersions()
    {
        // Arrange
        var t = Feed(new Terminal(10, 4), "0123456789abcde");
        int before = t.GetScreenRow(0).Version;

        // Act
        t.Resize(5, 4);

        // Assert
        await Assert.That(t.GetScreenRow(0).Version).IsNotEqualTo(before);
    }
}
