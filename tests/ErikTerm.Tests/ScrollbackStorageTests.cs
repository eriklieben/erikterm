using System.Text;
using ErikTerm.Core;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.Tests;

/// <summary>
/// Scrollback compaction (ScrollbackCompactor, Row.Trim): lines enter the scrollback whole and later keep
/// only their text, the default-blank tail dropped; everything that reads rows still sees the blank cells.
/// </summary>
public class ScrollbackStorageTests
{
    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    /// <summary>Compacts the whole scrollback, as the PTY reader does when the output stops.</summary>
    private static Terminal Compacted(Terminal t)
    {
        while (t.CompactScrollback(1000)) { }
        return t;
    }

    /// <summary>All text (scrollback + screen), soft-wrapped rows joined.</summary>
    private static string All(Terminal t) => t.GetText(t.FirstLine, 0, t.EndLine - 1, int.MaxValue);

    /// <summary>A terminal with <paramref name="text"/> pushed into the scrollback (its oldest line), compacted.</summary>
    private static Terminal WithScrolledOff(string text, int cols = 20, int rows = 3, int scrollback = 100) =>
        Compacted(Feed(new Terminal(cols, rows, scrollback), text + "\r\n" + new string('\n', rows)));

    private static string? Cluster(Row row, int col) => Terminal.TryGetCluster(row, col, out var text) ? text.ToString() : null;

    [Test]
    public async Task LinesEnterTheScrollbackWhole()
    {
        // Act
        var t = Feed(new Terminal(20, 3, 100), "hello\r\n\n\n\n");
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.Cells.Length).IsEqualTo(20);
        await Assert.That(t.NeedsCompaction).IsTrue();
    }

    [Test]
    public async Task ShortLineIsStoredWithoutItsBlankTail()
    {
        // Act
        var t = WithScrolledOff("hello");
        bool needed = t.NeedsCompaction;
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.Cells.Length).IsEqualTo(8); // "hello" rounded up to the storage granularity
        await Assert.That(row.Width).IsEqualTo(20);
        await Assert.That(row.IsTrimmed).IsTrue();
        await Assert.That(needed).IsFalse();
        await Assert.That(t.GetText(t.FirstLine, 0, t.FirstLine, 19)).IsEqualTo("hello");
    }

    [Test]
    public async Task CompactionLeavesWhatTheRowShowsUnchanged()
    {
        // Arrange
        var t = Feed(new Terminal(20, 3, 100), "\e[1;31mred\e[0m text\r\n\n\n\n");
        var row = t.GetLine(t.FirstLine)!;
        int version = row.Version;
        var before = row.Cells.Take(8).ToArray();

        // Act
        Compacted(t);

        // Assert
        var after = t.GetLine(t.FirstLine)!;
        await Assert.That(ReferenceEquals(after, row)).IsTrue();
        await Assert.That(after.Version).IsEqualTo(version); // cached renderings stay valid
        await Assert.That(after.Cells.Take(8).ToArray()).IsEquivalentTo(before, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task CompactionWorksOldestFirstWithinItsBudget()
    {
        // Arrange
        var t = Feed(new Terminal(20, 2, 100), "a\r\nb\r\nc\r\n\n");

        // Act
        bool more = t.CompactScrollback(1);
        bool oldest = t.GetLine(t.FirstLine)!.IsTrimmed, next = t.GetLine(t.FirstLine + 1)!.IsTrimmed;

        // Assert
        await Assert.That(more).IsTrue();
        await Assert.That(oldest).IsTrue();
        await Assert.That(next).IsFalse();
    }

    [Test]
    public async Task BlankLineStoresNoCells()
    {
        // Act
        var t = WithScrolledOff("");
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.Cells.Length).IsEqualTo(0);
        await Assert.That(row.Width).IsEqualTo(20);
    }

    [Test]
    public async Task ColoredBackgroundTailIsKept()
    {
        // Act
        // EL with a background color paints the rest of the row: those cells aren't blank.
        var t = WithScrolledOff("\e[44mdiff\e[K\e[0m");
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.Cells.Length).IsEqualTo(20);
        await Assert.That(row.Cells[19].Bg).IsEqualTo(TermColor.Indexed(4));
    }

    [Test]
    public async Task ColoredBlankCellsAtTheEndOfTheTextAreKept()
    {
        // Act
        var t = WithScrolledOff("a\e[41m   \e[0m");
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.IsTrimmed).IsTrue();
        await Assert.That(row.Cells[3].Bg).IsEqualTo(TermColor.Indexed(1));
    }

    [Test]
    public async Task UnderlinedSpacesWithAnUnderlineColorAreKept()
    {
        // Act
        var t = WithScrolledOff("ab\e[4:3;58;5;196m   \e[0m");
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.IsTrimmed).IsTrue();
        await Assert.That(row.Cells.Length).IsGreaterThanOrEqualTo(5);
        await Assert.That(CellStyle.Underline(row.Cells[4].Flags)).IsEqualTo(UnderlineStyle.Curly);
        await Assert.That(row.UnderlineColorAt(4)).IsEqualTo(TermColor.Indexed(196));
    }

    [Test]
    public async Task OverlinedBlankCellIsKept()
    {
        // Act
        var t = WithScrolledOff("x\e[53m \e[0m");
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.Cells[1].Flags & CellAttributes.Overline).IsEqualTo(CellAttributes.Overline);
    }

    [Test]
    public async Task WideCharacterAtTheTextEndKeepsBothHalves()
    {
        // Act
        var t = WithScrolledOff("ab日");
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.IsTrimmed).IsTrue();
        await Assert.That(row.Cells[2].Flags & CellAttributes.WideLead).IsEqualTo(CellAttributes.WideLead);
        await Assert.That(row.Cells[3].Flags & CellAttributes.WideTrail).IsEqualTo(CellAttributes.WideTrail);
        await Assert.That(t.GetText(t.FirstLine, 0, t.FirstLine, 19)).IsEqualTo("ab日");
    }

    [Test]
    public async Task WideCharacterAtTheRightEdgeKeepsTheRowWhole()
    {
        // Act
        var t = WithScrolledOff("\e[19G日", cols: 20);
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.Cells.Length).IsEqualTo(20);
        await Assert.That(row.Cells[19].Flags & CellAttributes.WideTrail).IsEqualTo(CellAttributes.WideTrail);
    }

    [Test]
    public async Task GraphemeClusterSurvivesTrimming()
    {
        // Act
        var t = WithScrolledOff("ok é 👍🏽");
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.IsTrimmed).IsTrue();
        await Assert.That(Cluster(row, 3)).IsEqualTo("é");
        await Assert.That(Cluster(row, 5)).IsEqualTo("👍🏽");
        await Assert.That(t.GetText(t.FirstLine, 0, t.FirstLine, 19)).IsEqualTo("ok é 👍🏽");
    }

    [Test]
    public async Task SoftWrappedRowsAreKeptWhole()
    {
        // Act
        var t = WithScrolledOff(new string('x', 25), cols: 20, rows: 3);
        var first = t.GetLine(t.FirstLine)!;
        var last = t.GetLine(t.FirstLine + 1)!;

        // Assert
        await Assert.That(first.Wrapped).IsTrue();
        await Assert.That(first.Cells.Length).IsEqualTo(20);
        await Assert.That(last.IsTrimmed).IsTrue();
        await Assert.That(t.GetText(t.FirstLine, 0, t.FirstLine + 1, 19)).IsEqualTo(new string('x', 25));
    }

    [Test]
    public async Task NarrowingRewrapsATrimmedLine()
    {
        // Arrange
        var t = WithScrolledOff("0123456789abcdef", cols: 20, rows: 3);

        // Act
        t.Resize(10, 3);

        // Assert
        await Assert.That(t.GetLine(t.FirstLine)!.Wrapped).IsTrue();
        await Assert.That(t.GetText(t.FirstLine, 0, t.FirstLine, 9)).IsEqualTo("0123456789");
        await Assert.That(t.GetLine(t.FirstLine + 1)!.Width).IsEqualTo(10);
        await Assert.That(All(t).TrimEnd('\n')).IsEqualTo("0123456789abcdef");
    }

    [Test]
    public async Task WideningJoinsAWrappedLineIntoOneTrimmedRow()
    {
        // Arrange
        var t = WithScrolledOff(new string('x', 25), cols: 20, rows: 3);

        // Act
        t.Resize(40, 3);
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(row.Wrapped).IsFalse();
        await Assert.That(row.Width).IsEqualTo(40);
        await Assert.That(row.IsTrimmed).IsTrue();
        await Assert.That(All(t).TrimEnd('\n')).IsEqualTo(new string('x', 25));
    }

    [Test]
    public async Task RoundTripThroughReflowKeepsTextColorsClustersAndUnderlineColors()
    {
        // Arrange
        string line = "\e[31mred\e[0m é 日本 \e[4;58;5;33mul\e[0m " + new string('z', 12);
        var t = WithScrolledOff(line, cols: 30, rows: 3);
        string before = All(t);

        // Act
        t.Resize(8, 3);
        Compacted(t);
        t.Resize(30, 3);
        Compacted(t);
        var row = t.GetLine(t.FirstLine)!;

        // Assert
        await Assert.That(All(t)).IsEqualTo(before);
        await Assert.That(row.Cells[0].Fg).IsEqualTo(TermColor.Indexed(1));
        await Assert.That(Cluster(row, 4)).IsEqualTo("é");
        await Assert.That(row.UnderlineColorAt(11)).IsEqualTo(TermColor.Indexed(33));
    }

    [Test]
    public async Task ReflowMarksTheScrollbackForCompactionAgain()
    {
        // Arrange
        var t = WithScrolledOff("some text that wraps", cols: 30, rows: 3);

        // Act
        t.Resize(10, 3);

        // Assert
        await Assert.That(t.NeedsCompaction).IsTrue();
    }

    [Test]
    public async Task RowPulledBackOntoTheScreenIsFullWidthAgain()
    {
        // Arrange
        var t = WithScrolledOff("hi", cols: 20, rows: 3);

        // Act
        t.Resize(20, 10); // taller: scrollback rows come back onto the screen
        var row = t.GetScreenRow(0);
        Feed(t, "\e[1;20HZ");

        // Assert
        await Assert.That(row.Cells.Length).IsEqualTo(20);
        await Assert.That(row.IsTrimmed).IsFalse();
        await Assert.That(t.GetLineText(0)).IsEqualTo("hi" + new string(' ', 17) + "Z");
    }

    [Test]
    public async Task SelectionAcrossTrimmedRowsCopiesTheirText()
    {
        // Arrange
        var t = Compacted(Feed(new Terminal(20, 3, 100), "one\r\n  two\r\nthree\r\n\n\n\n"));

        // Act
        string text = t.GetText(t.FirstLine, 0, t.FirstLine + 2, 19);

        // Assert
        await Assert.That(text).IsEqualTo("one\n  two\nthree");
    }

    [Test]
    public async Task BlockSelectionPastTheTextOfTrimmedRows()
    {
        // Arrange
        var t = Compacted(Feed(new Terminal(20, 3, 100), "abcdef\r\nab\r\n\n\n\n"));

        // Act
        string text = t.GetText(t.FirstLine, 1, t.FirstLine + 1, 15, block: true);

        // Assert
        await Assert.That(text).IsEqualTo("bcdef\nb");
    }

    [Test]
    public async Task WordSelectionInTheBlankTailCoversTheRestOfTheRow()
    {
        // Arrange
        var t = WithScrolledOff("word", cols: 20);

        // Act
        var range = t.WordAt(t.FirstLine, 15);

        // Assert
        await Assert.That(range).IsEqualTo((4, 19));
    }

    [Test]
    public async Task WordSelectionOnTheTextOfATrimmedRow()
    {
        // Arrange
        var t = WithScrolledOff("say hello", cols: 20);

        // Act
        var range = t.WordAt(t.FirstLine, 6);

        // Assert
        await Assert.That(range).IsEqualTo((4, 8));
    }

    [Test]
    public async Task SearchFindsTextInTrimmedRows()
    {
        // Arrange
        var t = Compacted(Feed(new Terminal(20, 3, 100), "alpha\r\nneedle here\r\nomega\r\n\n\n\n"));
        var search = new TerminalSearch();
        search.SetQuery("needle", false, false);

        // Act
        while (true) { search.Step(t, 100); if (search.Complete) break; }

        // Assert
        await Assert.That(search.Matches).IsEquivalentTo([new SearchMatch(t.FirstLine + 1, 0, t.FirstLine + 1, 5)], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task VimSeesTheRowsFullWidth()
    {
        // Arrange
        var t = WithScrolledOff("vim", cols: 20);

        // Act
        int width = VimBuffer.Width(t, t.FirstLine);
        int next = VimBuffer.NextCol(t, t.FirstLine, 10);

        // Assert
        await Assert.That(width).IsEqualTo(20);
        await Assert.That(next).IsEqualTo(11);
    }

    [Test]
    public async Task InlineImageTilesInScrollbackAreKept()
    {
        // Arrange
        string png = Convert.ToBase64String(PngTests.EncodeRgba(6, 4, (x, y) => 0xFF0000FF));

        // Act
        var t = Compacted(Feed(new Terminal(20, 3, 100), "\e]1337;File=inline=1;width=3;height=1:" + png + "\a\r\n\n\n\n"));
        var row = t.GetLine(t.FirstLine)!;
        int tiles = row.Cells.Count(c => (c.Flags & CellAttributes.Image) != 0);

        // Assert
        await Assert.That(row.IsTrimmed).IsTrue();
        await Assert.That(tiles).IsEqualTo(3);
    }

    [Test]
    public async Task KittyPlacementStaysOnItsLineWhenTheRowIsCompacted()
    {
        // Arrange
        var t = Feed(new Terminal(20, 3, 100), "\e_Ga=T,f=32,s=2,v=2,i=7,q=2;" + Convert.ToBase64String(new byte[16]) + "\e\\\r\n\n\n\n");
        long line = t.Images.Placements(false).Single().Line;

        // Act
        Compacted(t);
        long after = t.Images.Placements(false).Single().Line;

        // Assert
        await Assert.That(after).IsEqualTo(line);
    }

    [Test]
    public async Task ExportedScrollbackKeepsColorsOfTrimmedRows()
    {
        // Arrange
        var t = Feed(new Terminal(20, 3, 100), "\e[32mok\e[0m\r\n\n\n\n");
        string before = t.ExportStyledLines(10)[0];

        // Act
        string after = Compacted(t).ExportStyledLines(10)[0];

        // Assert
        await Assert.That(after).IsEqualTo(before);
    }

    [Test]
    public async Task EvictionKeepsTheNewestLines()
    {
        // Arrange
        var t = new Terminal(20, 2, scrollbackLines: 3);
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++) sb.Append("line ").Append(i).Append(i % 3 == 0 ? new string('-', 12) : "").Append("\r\n");

        // Act
        foreach (string line in sb.ToString().Split("\r\n")[..^1])
        {
            Feed(t, line + "\r\n");
            t.CompactScrollback(1); // evicted rows are compacted ones from here on
        }
        string text = t.GetText(t.FirstLine, 0, t.ScreenTopLine - 1, 19);

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(3);
        await Assert.That(t.FirstLine).IsEqualTo(46L);
        await Assert.That(text).IsEqualTo("line 46\nline 47\nline 48" + new string('-', 12));
    }

    [Test]
    public async Task EvictedCompactedRowsComeBackAsBlankFullWidthRows()
    {
        // Arrange
        var t = Compacted(Feed(new Terminal(20, 3, scrollbackLines: 2), "#####\r\n\e[45m\e[K\e[0m\r\nx\r\n\n\n"));

        // Act
        Feed(t, "\n\n\n"); // evicts the compacted rows; their Row objects become screen rows again
        var rows = Enumerable.Range(0, 3).Select(t.GetScreenRow).ToArray();

        // Assert
        await Assert.That(t.GetScreenText()).IsEqualTo("\n\n\n");
        await Assert.That(rows.All(r => r.Cells.Length == 20 && r.Width == 20)).IsTrue();
        await Assert.That(rows.All(r => r.Cells.All(c => c.Bg == TermColor.Default && c.Rune == 0))).IsTrue();
    }

    [Test]
    public async Task TricklingOutputReusesStorage()
    {
        // Arrange
        // An agent or a shell session: a line at a time, compacted between lines, over a full scrollback.
        var t = new Terminal(80, 10, scrollbackLines: 100);
        string[] lines = Enumerable.Range(0, 300).Select(i => "entry " + i % 10 + new string('.', i % 7 * 9) + "\r\n").ToArray();
        foreach (string line in lines) { Feed(t, line); Compacted(t); }
        long missesBefore = t.CellPoolMisses;

        // Act
        for (int round = 0; round < 3; round++)
            foreach (string line in lines) { Feed(t, line); Compacted(t); }
        long misses = t.CellPoolMisses - missesBefore;

        // Assert
        await Assert.That(misses).IsLessThanOrEqualTo(10L);
    }
}
