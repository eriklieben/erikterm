using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class ReflowTests
{
    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    private static string Screen(Terminal t) => t.GetScreenText();

    /// <summary>All text (scrollback + screen), soft-wrapped rows joined.</summary>
    private static string All(Terminal t) => t.GetText(t.FirstLine, 0, t.EndLine - 1, int.MaxValue);

    private static string Line(Row row)
    {
        var sb = new StringBuilder();
        foreach (var c in row.Cells)
            if ((c.Flags & CellAttributes.WideTrail) == 0) sb.Append(c.Rune == 0 ? " " : char.ConvertFromUtf32(c.Rune));
        return sb.ToString().TrimEnd();
    }

    /// <summary>The grapheme cluster stored at a cell, if any (the span can't live in an async test).</summary>
    private static string? Cluster(Row row, int col) => Terminal.TryGetCluster(row, col, out var text) ? text.ToString() : null;

    [Test]
    public async Task ShrinkRewrapsSoftWrappedLine()
    {
        // Arrange
        var t = Feed(new Terminal(10, 4), "0123456789abcde");
        string before = Screen(t);

        // Act
        t.Resize(5, 4);

        // Assert
        await Assert.That(before).IsEqualTo("0123456789\nabcde\n\n\n");
        await Assert.That(Screen(t)).IsEqualTo("01234\n56789\nabcde\n\n");
        await Assert.That(t.GetScreenRow(0).Wrapped).IsTrue();
        await Assert.That(t.GetScreenRow(1).Wrapped).IsTrue();
        await Assert.That(t.GetScreenRow(2).Wrapped).IsFalse();
    }

    [Test]
    public async Task CursorAtTheEndOfAShrunkFullRowWrapsTheNextCharacter()
    {
        // Arrange
        // The cursor was after 'e'; after the shrink the row is full, so the next character wraps.
        var t = Feed(new Terminal(10, 4), "0123456789abcde");
        t.Resize(5, 4);

        // Act
        Feed(t, "f");

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("01234\n56789\nabcde\nf\n");
        await Assert.That(All(t)).IsEqualTo("0123456789abcdef");
    }

    [Test]
    public async Task GrowJoinsWrappedLines()
    {
        // Arrange
        var t = Feed(new Terminal(5, 4), "0123456789ab");
        string before = Screen(t);

        // Act
        t.Resize(12, 4);

        // Assert
        await Assert.That(before).IsEqualTo("01234\n56789\nab\n\n");
        await Assert.That(Screen(t)).IsEqualTo("0123456789ab\n\n\n\n");
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((11, 0)); // after 'b', which now ends a full row: pending wrap
    }

    [Test]
    public async Task PrintAfterGrowingToAFullRowStartsTheNextRow()
    {
        // Arrange
        var t = Feed(new Terminal(5, 4), "0123456789ab");
        t.Resize(12, 4);

        // Act
        Feed(t, "c");

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("0123456789ab\nc\n\n\n");
    }

    [Test]
    public async Task GrowingAgainJoinsTheCharacterPrintedAfterThePendingWrap()
    {
        // Arrange
        var t = Feed(new Terminal(5, 4), "0123456789ab");
        t.Resize(12, 4);
        Feed(t, "c");

        // Act
        t.Resize(20, 4);

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("0123456789abc\n\n\n\n");
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((13, 0));
    }

    [Test]
    public async Task ShrinkWrapsEachHardLineOnItsOwn()
    {
        // Arrange
        var t = Feed(new Terminal(6, 6), "abc\r\ndef");

        // Act
        t.Resize(2, 6);

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("ab\nc\nde\nf\n\n\n");
    }

    [Test]
    public async Task HardLineBreaksAreNotJoined()
    {
        // Arrange
        var t = Feed(new Terminal(6, 6), "abc\r\ndef");
        t.Resize(2, 6);

        // Act
        t.Resize(6, 6);

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("abc\ndef\n\n\n\n\n");
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((3, 1));
    }

    /// <summary>Two lines, the first soft-wrapped, with the cursor moved onto its 'c' (second row, third column).</summary>
    private static Terminal CursorOnC() => Feed(new Terminal(10, 4), "0123456789abcdefghij\r\nnext\e[2;3H");

    [Test]
    public async Task CursorStaysOnSameCharacterWhenShrinking()
    {
        // Arrange
        var t = CursorOnC();

        // Act
        t.Resize(4, 8);
        Feed(t, "X");

        // Assert
        await Assert.That(All(t).Split('\n')[0]).IsEqualTo("0123456789abXdefghij");
    }

    [Test]
    public async Task CursorStaysOnSameCharacterWhenGrowing()
    {
        // Arrange
        var t = CursorOnC();
        t.Resize(4, 8);
        Feed(t, "X");

        // Act
        t.Resize(25, 8);
        Feed(t, "Y");

        // Assert
        await Assert.That(All(t).Split('\n')[0]).IsEqualTo("0123456789abXYefghij");
    }

    /// <summary>A full row with the cursor in pending-wrap state at its end.</summary>
    private static Terminal PendingWrap() => Feed(new Terminal(10, 4), "abcdefghij");

    [Test]
    public async Task PendingWrapSurvivesShrink()
    {
        // Arrange
        var t = PendingWrap();

        // Act
        t.Resize(5, 4);

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("abcde\nfghij\n\n\n");
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((4, 1));
    }

    [Test]
    public async Task PendingWrapSurvivesGrow()
    {
        // Arrange
        var t = PendingWrap();
        t.Resize(5, 4);

        // Act
        t.Resize(20, 4);

        // Assert
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((10, 0));
    }

    [Test]
    public async Task PrintAfterShrinkAndGrowContinuesTheLine()
    {
        // Arrange
        var t = PendingWrap();
        t.Resize(5, 4);
        t.Resize(20, 4);

        // Act
        Feed(t, "k");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("abcdefghijk");
    }

    [Test]
    public async Task WideCharactersAreNotSplitAcrossRows()
    {
        // Arrange
        var t = Feed(new Terminal(6, 4), "ab日本語");
        string before = Screen(t);

        // Act
        t.Resize(5, 4);
        var row0 = t.GetScreenRow(0);

        // Assert
        await Assert.That(before).IsEqualTo("ab日本\n語\n\n\n");
        await Assert.That(Screen(t)).IsEqualTo("ab日\n本語\n\n\n");
        await Assert.That(row0.Wrapped).IsTrue();
        await Assert.That(row0.Cells[4].Rune).IsEqualTo(0); // padding: the wide char didn't fit in the last column
        await Assert.That(All(t).TrimEnd('\n')).IsEqualTo("ab日本語");
    }

    [Test]
    public async Task GrowingDropsTheWideCharacterPadding()
    {
        // Arrange
        var t = Feed(new Terminal(6, 4), "ab日本語");
        t.Resize(5, 4);

        // Act
        t.Resize(6, 4);

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("ab日本\n語\n\n\n");
    }

    [Test]
    public async Task GrowingPastTheWideLineJoinsIt()
    {
        // Arrange
        var t = Feed(new Terminal(6, 4), "ab日本語");
        t.Resize(5, 4);
        t.Resize(6, 4);

        // Act
        t.Resize(10, 4);

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("ab日本語\n\n\n\n");
        await Assert.That(t.CursorX).IsEqualTo(8);
    }

    [Test]
    public async Task WideCharacterAtNewBoundaryKeepsBothHalves()
    {
        // Arrange
        var t = Feed(new Terminal(10, 4), "abc日defgh");

        // Act
        t.Resize(4, 4);
        bool[] endsInWideLead = [.. Enumerable.Range(0, 3).Select(y => (t.GetScreenRow(y).Cells[^1].Flags & CellAttributes.WideLead) != 0)];

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("abc\n日de\nfgh\n\n");
        for (int y = 0; y < 3; y++)
        {
            await Assert.That(endsInWideLead[y]).IsFalse().Because($"row {y} ends in a wide lead");
        }
    }

    /// <summary>A long line that wrapped into scrollback, then two short ones on the screen.</summary>
    private static Terminal WrappedIntoScrollback() => Feed(new Terminal(10, 2, 100), "aaaaaaaaaaBB\r\nccc\r\nddd");

    [Test]
    public async Task ScrollbackHoldsTheWrappedRowsBeforeAnyResize()
    {
        // Act
        var t = WrappedIntoScrollback();

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(2);
        await Assert.That(Screen(t)).IsEqualTo("ccc\nddd\n");
    }

    [Test]
    public async Task GrowingJoinsTheWrappedScrollbackLine()
    {
        // Arrange
        var t = WrappedIntoScrollback();

        // Act
        t.Resize(20, 2);

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(1);
        await Assert.That(Line(t.GetLine(t.FirstLine)!)).IsEqualTo("aaaaaaaaaaBB");
        await Assert.That(Screen(t)).IsEqualTo("ccc\nddd\n");
    }

    [Test]
    public async Task ScrollbackIsReflowed()
    {
        // Arrange
        var t = WrappedIntoScrollback();
        t.Resize(20, 2);

        // Act
        t.Resize(5, 2);

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(3);
        await Assert.That(Screen(t)).IsEqualTo("ccc\nddd\n");
        await Assert.That(All(t)).IsEqualTo("aaaaaaaaaaBB\nccc\nddd");
        await Assert.That(t.FirstLine + t.ScrollbackCount).IsEqualTo(t.ScreenTopLine);
        await Assert.That(t.ScreenTopLine + t.Rows).IsEqualTo(t.EndLine);
    }

    [Test]
    public async Task LineWrappedAcrossScrollbackAndScreenIsJoined()
    {
        // Arrange
        var t = Feed(new Terminal(4, 2, 100), "abcdefghij"); // "abcd" in scrollback, "efgh" "ij" on screen
        int scrollbackBefore = t.ScrollbackCount;

        // Act
        t.Resize(10, 2);

        // Assert
        await Assert.That(scrollbackBefore).IsEqualTo(1);
        await Assert.That(t.ScrollbackCount).IsEqualTo(0);
        await Assert.That(Screen(t)).IsEqualTo("abcdefghij\n\n");
    }

    [Test]
    public async Task ShrinkingPushesLinesIntoScrollbackKeepingCursorVisible()
    {
        // Arrange
        var t = Feed(new Terminal(10, 3, 100), "1111111111\r\n2\r\n3");

        // Act
        t.Resize(5, 3);

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("11111\n2\n3\n");
        await Assert.That(t.ScrollbackCount).IsEqualTo(1);
        await Assert.That(t.CursorY).IsEqualTo(2);
        await Assert.That(t.CursorX).IsEqualTo(1);
    }

    [Test]
    public async Task EmptyRowsBelowCursorAreNotPushedIntoScrollback()
    {
        // Arrange
        var t = Feed(new Terminal(10, 5, 100), "aaaaaaaaaaaaaaa\e[H");

        // Act
        t.Resize(3, 5);

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(0);
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((0, 0));
        await Assert.That(Screen(t)).IsEqualTo("aaa\naaa\naaa\naaa\naaa\n");
    }

    [Test]
    public async Task ScrollbackCapacityIsRespected()
    {
        // Arrange
        var t = new Terminal(10, 2, scrollbackLines: 3);
        for (int i = 0; i < 6; i++) Feed(t, "0123456789\r\n");
        long first = t.FirstLine;

        // Act
        t.Resize(5, 2);

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(3);
        await Assert.That(t.FirstLine).IsGreaterThan(first); // rows that no longer fit were dropped
        await Assert.That(t.FirstLine + 3).IsEqualTo(t.ScreenTopLine);
    }

    /// <summary>A soft-wrapped line on the main screen, then the alternate screen with a full row.</summary>
    private static Terminal MainAndAlternate() => Feed(new Terminal(10, 3), "0123456789abc\e[?1049h\e[Hxxxxxxxxxx");

    [Test]
    public async Task AlternateScreenIsNotReflowed()
    {
        // Arrange
        var t = MainAndAlternate();

        // Act
        t.Resize(5, 3);

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("xxxxx\n\n\n"); // truncated, not rewrapped
        await Assert.That(t.GetScreenRow(1).Wrapped).IsFalse();
    }

    [Test]
    public async Task MainScreenIsReflowedBehindTheAlternateScreen()
    {
        // Arrange
        var t = MainAndAlternate();
        t.Resize(5, 3);

        // Act
        Feed(t, "\e[?1049l");

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("01234\n56789\nabc\n");
    }

    [Test]
    public async Task SavedCursorFollowsItsCharacter()
    {
        // Arrange
        var t = MainAndAlternate();
        t.Resize(5, 3);
        Feed(t, "\e[?1049l");

        // Act
        Feed(t, "d");

        // Assert
        await Assert.That(t.GetLineText(2)).IsEqualTo("abcd");
    }

    /// <summary>
    /// A cursor saved on the last row, a shorter screen, then DECSET 47, which does not save the cursor: the saved row
    /// is below the last row of the main screen.
    /// </summary>
    private static Terminal AlternateWithSavedRowBelowTheScreen()
    {
        var t = Feed(new Terminal(10, 3), "a\r\nb\r\nc\e7");
        t.Resize(10, 2);
        return Feed(t, "\e[?47h");
    }

    [Test]
    public async Task ShorterScreenWithSavedRowBelowItMovesTheTopRowsToTheScrollback()
    {
        // Arrange
        var t = AlternateWithSavedRowBelowTheScreen();

        // Act
        t.Resize(10, 1);
        Feed(t, "\e[?47l");

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("c\n");
        await Assert.That(t.ScrollbackCount).IsEqualTo(2);
        await Assert.That(All(t).TrimEnd('\n')).IsEqualTo("a\nb\nc");
    }

    [Test]
    public async Task SavedRowBelowAShorterScreenRestoresToTheLastRow()
    {
        // Arrange
        var t = AlternateWithSavedRowBelowTheScreen();
        t.Resize(10, 1);
        Feed(t, "\e[?47l");

        // Act
        Feed(t, "\e8d");

        // Assert
        await Assert.That(t.CursorY).IsEqualTo(0);
        await Assert.That(t.GetLineText(0)).IsEqualTo("cd");
    }

    [Test]
    public async Task SavedRowBelowTheScreenGivesTheSameScreenWithAndWithoutAColumnChange()
    {
        // Arrange
        var t = AlternateWithSavedRowBelowTheScreen();

        // Act
        t.Resize(5, 1);
        Feed(t, "\e[?47l");

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("c\n");
        await Assert.That(All(t).TrimEnd('\n')).IsEqualTo("a\nb\nc");
    }

    [Test]
    public async Task SavedRowSurvivesAShorterScreenThatGrowsBack()
    {
        // Arrange
        var t = Feed(new Terminal(10, 5), "\e[4;1H\e7\e[H");
        t.Resize(10, 2);

        // Act
        t.Resize(10, 5);
        Feed(t, "\e8");

        // Assert
        await Assert.That(t.CursorY).IsEqualTo(3);
    }

    /// <summary>
    /// "e" + U+0301 (combining acute): two code points in one cell. Spelled as an escape so an editor can't normalize
    /// it to the single code point "é", which is not a cluster.
    /// </summary>
    private const string Combined = "e\u0301";

    [Test]
    public async Task ClustersMoveWithTheirCellsWhenShrinking()
    {
        // Arrange
        var t = Feed(new Terminal(10, 4), "abcdefghi" + Combined + "z");

        // Act
        t.Resize(5, 4);

        // Assert
        await Assert.That(Cluster(t.GetScreenRow(1), 4)).IsEqualTo(Combined);
        await Assert.That(All(t).TrimEnd('\n')).IsEqualTo("abcdefghi" + Combined + "z");
    }

    [Test]
    public async Task ClustersMoveWithTheirCellsWhenGrowing()
    {
        // Arrange
        var t = Feed(new Terminal(10, 4), "abcdefghi" + Combined + "z");
        t.Resize(5, 4);

        // Act
        t.Resize(20, 4);

        // Assert
        await Assert.That(Cluster(t.GetScreenRow(0), 9)).IsEqualTo(Combined);
    }

    [Test]
    public async Task ResizingRowsOnlyDoesNotRewrap()
    {
        // Arrange
        var t = Feed(new Terminal(5, 4), "0123456789");
        int reflows = t.ReflowCount;

        // Act
        t.Resize(5, 6);

        // Assert
        await Assert.That(t.ReflowCount).IsEqualTo(reflows);
    }

    [Test]
    public async Task ResizingColumnsRewraps()
    {
        // Arrange
        var t = Feed(new Terminal(5, 4), "0123456789");
        int reflows = t.ReflowCount;
        t.Resize(5, 6);

        // Act
        t.Resize(6, 6);

        // Assert
        await Assert.That(t.ReflowCount).IsEqualTo(reflows + 1);
        await Assert.That(Screen(t)).IsEqualTo("012345\n6789\n\n\n\n\n");
    }

    [Test]
    public async Task ManyResizesRoundTripContent()
    {
        // Arrange
        var t = new Terminal(80, 24, 100_000);
        var rng = new Random(42);
        for (int i = 0; i < 300; i++)
        {
            int len = rng.Next(0, 200);
            var line = new StringBuilder();
            for (int k = 0; k < len; k++) line.Append(rng.Next(10) == 0 ? '日' : (char)('a' + rng.Next(26)));
            Feed(t, line + "\r\n");
        }
        string before = All(t);

        // Act
        foreach (int cols in new[] { 37, 120, 3, 79, 200, 2, 80 }) t.Resize(cols, 24);

        // Assert
        await Assert.That(All(t).TrimEnd('\n')).IsEqualTo(before.TrimEnd('\n'));
    }

    /// <summary>
    /// A performance bound: reflowing 10,000 lines of scrollback stays well under 100 ms per resize. It measures the
    /// CPU time of this thread (a loaded machine stretches wall-clock time, not the work done), runs alone, and takes
    /// the best of a few sweeps, so it catches a slower algorithm without failing because the machine is busy.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task ReflowOfFullScrollbackIsFast()
    {
        // Arrange
        var t = new Terminal(120, 40, 10_000);
        var sb = new StringBuilder();
        for (int i = 0; i < 12_000; i++) sb.Append(new string((char)('a' + i % 26), i % 7 == 0 ? 300 : 10 + i % 100)).Append("\r\n");
        Feed(t, sb.ToString());
        t.Resize(119, 40); // warm up
        const int Rounds = 3;
        var perResizeMs = new List<double>();

        // Act
        for (int round = 0; round < Rounds; round++)
        {
            // Alternately 118 -> 60 and 62 -> 120 columns, 30 resizes each: every round reflows the whole scrollback.
            bool shrink = round % 2 == 0;
            int n = 0;
            var cpu = ThreadCpuTime.Measure(() =>
            {
                for (int cols = shrink ? 118 : 62; shrink ? cols >= 60 : cols <= 120; cols += shrink ? -2 : 2, n++) t.Resize(cols, 40);
            });
            perResizeMs.Add(cpu.TotalMilliseconds / n);
        }
        double best = perResizeMs.Min();

        // Assert
        // Generous bound for slow CI machines and Debug builds; the benchmark (--reflow) has real numbers.
        await Assert.That(best).IsLessThan(100)
            .Because($"{best:F1} ms CPU per resize at best (rounds: {string.Join(", ", perResizeMs.Select(ms => ms.ToString("F1")))})");
    }
}
