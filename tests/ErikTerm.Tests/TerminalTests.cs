using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class TerminalTests
{
    private static Terminal Term(int cols = 10, int rows = 4, int scrollback = 100) => new(cols, rows, scrollback);

    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    private static string Screen(Terminal t) => t.GetScreenText();

    private static string RowText(Row row)
    {
        var sb = new StringBuilder();
        foreach (var c in row.Cells) sb.Append(c.Rune == 0 ? ' ' : (char)c.Rune);
        return sb.ToString().TrimEnd();
    }

    [Test]
    public async Task PrintsAndMovesCursor()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "hello");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("hello");
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((5, 0));
    }

    [Test]
    public async Task AutowrapUsesPendingWrap()
    {
        // Arrange
        var t = Term(cols: 5);

        // Act
        Feed(t, "abcde");

        // Assert
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((4, 0)); // stays on last column until the next print
    }

    [Test]
    public async Task PrintAfterPendingWrapGoesToTheNextLine()
    {
        // Arrange
        var t = Feed(Term(cols: 5), "abcde");

        // Act
        Feed(t, "f");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("abcde");
        await Assert.That(t.GetLineText(1)).IsEqualTo("f");
        await Assert.That(t.GetScreenRow(0).Wrapped).IsTrue();
    }

    [Test]
    public async Task CarriageReturnClearsPendingWrap()
    {
        // Arrange
        var t = Term(cols: 5);

        // Act
        Feed(t, "abcde\rX");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("Xbcde");
        await Assert.That(t.GetLineText(1)).IsEqualTo("");
    }

    [Test]
    public async Task NoAutowrapOverwritesLastColumn()
    {
        // Arrange
        var t = Term(cols: 5);

        // Act
        Feed(t, "\e[?7labcdefg");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("abcdg");
        await Assert.That(t.GetLineText(1)).IsEqualTo("");
    }

    [Test]
    public async Task ScrollsIntoScrollback()
    {
        // Arrange
        var t = Term(rows: 2);

        // Act
        Feed(t, "one\r\ntwo\r\nthree");

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("two\nthree\n");
        await Assert.That(t.ScrollbackCount).IsEqualTo(1);
    }

    [Test]
    public async Task ScrollingTheViewShowsTheScrolledOffLine()
    {
        // Arrange
        var t = Feed(Term(rows: 2), "one\r\ntwo\r\nthree");

        // Act
        t.ScrollView(1);

        // Assert
        await Assert.That(RowText(t.GetVisibleRow(0))).IsEqualTo("one");
    }

    [Test]
    public async Task CursorPositionAndErase()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "aaaaaaaaaa\r\nbbbbbbbbbb\e[1;4H\e[K\e[2;3H\e[1K");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("aaa");
        await Assert.That(t.GetLineText(1)).IsEqualTo("   bbbbbbb");
    }

    [Test]
    public async Task EraseDisplay()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "aa\r\nbb\r\ncc\e[2;2H\e[J");

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("aa\nb\n\n\n");
    }

    [Test]
    public async Task ScrollRegionKeepsOutsideLines()
    {
        // Arrange
        var t = Term(rows: 4);

        // Act
        Feed(t, "top\r\n1\r\n2\r\nbot\e[2;3r\e[3;1H\nnew");

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("top\n2\nnew\nbot\n");
        await Assert.That(t.ScrollbackCount).IsEqualTo(0); // region scrolls never feed history
    }

    [Test]
    public async Task InsertLinePushesLinesDown()
    {
        // Arrange
        var t = Term(rows: 4);

        // Act
        Feed(t, "a\r\nb\r\nc\r\nd\e[2;1H\e[L");

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("a\n\nb\nc\n");
    }

    [Test]
    public async Task DeleteLinesPullLinesUp()
    {
        // Arrange
        var t = Feed(Term(rows: 4), "a\r\nb\r\nc\r\nd\e[2;1H\e[L");

        // Act
        Feed(t, "\e[M\e[M");

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("a\nc\n\n\n");
    }

    [Test]
    public async Task InsertCharsShiftsTheRestRight()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "abcdef\e[1;2H\e[2@");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("a  bcdef");
    }

    [Test]
    public async Task DeleteCharsShiftsTheRestLeft()
    {
        // Arrange
        var t = Feed(Term(), "abcdef\e[1;2H\e[2@");

        // Act
        Feed(t, "\e[3P");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("acdef");
    }

    [Test]
    public async Task SgrColorsAndAttributes()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "\e[1;31mA\e[38;5;200mB\e[38;2;1;2;3mC\e[48:2::4:5:6mD\e[0mE");

        // Assert
        await Assert.That(t.CellAt(0, 0).Fg).IsEqualTo(TermColor.Indexed(1));
        await Assert.That(t.CellAt(0, 0).Flags.HasFlag(CellAttributes.Bold)).IsTrue();
        await Assert.That(t.CellAt(1, 0).Fg).IsEqualTo(TermColor.Indexed(200));
        await Assert.That(t.CellAt(2, 0).Fg).IsEqualTo(TermColor.Rgb(1, 2, 3));
        await Assert.That(t.CellAt(3, 0).Bg).IsEqualTo(TermColor.Rgb(4, 5, 6));
        await Assert.That(t.CellAt(4, 0).Fg).IsEqualTo(TermColor.Default);
        await Assert.That(t.CellAt(4, 0).Flags).IsEqualTo(CellAttributes.None);
    }

    [Test]
    public async Task WideCharactersTakeTwoCells()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "a日b");

        // Assert
        await Assert.That(t.CellAt(1, 0).Flags.HasFlag(CellAttributes.WideLead)).IsTrue();
        await Assert.That(t.CellAt(2, 0).Flags.HasFlag(CellAttributes.WideTrail)).IsTrue();
        await Assert.That(t.CellAt(3, 0).Rune).IsEqualTo('b');
        await Assert.That(t.CursorX).IsEqualTo(4);
    }

    [Test]
    public async Task WideCharacterWrapsAtLastColumn()
    {
        // Arrange
        var t = Term(cols: 4);

        // Act
        Feed(t, "abc日");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("abc");
        await Assert.That(t.GetLineText(1)).IsEqualTo("日");
    }

    [Test]
    public async Task OverwritingHalfAWideCharClearsIt()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "日\e[1;2Hx");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo(" x");
    }

    [Test]
    public async Task AlternateScreenShowsItsOwnContent()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "main\e[?1049h\e[Halt");

        // Assert
        await Assert.That(t.IsAlternateScreen).IsTrue();
        await Assert.That(t.GetLineText(0)).IsEqualTo("alt");
    }

    [Test]
    public async Task AlternateScreenRestoresMainAndCursor()
    {
        // Arrange
        var t = Feed(Term(), "main\e[?1049h\e[Halt");

        // Act
        Feed(t, "\e[?1049l");

        // Assert
        await Assert.That(t.IsAlternateScreen).IsFalse();
        await Assert.That(t.GetLineText(0)).IsEqualTo("main");
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((4, 0));
    }

    [Test]
    public async Task DecLineDrawing()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "\e(0lqk\e(Bq");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("┌─┐q");
    }

    [Test]
    public async Task TabStops()
    {
        // Arrange
        var t = Term(cols: 20);

        // Act
        Feed(t, "a\tb");

        // Assert
        await Assert.That(t.CursorX).IsEqualTo(9);
    }

    [Test]
    public async Task RepeatsLastCharacter()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "x\e[3b");

        // Assert
        await Assert.That(t.GetLineText(0)).IsEqualTo("xxxx");
    }

    [Test]
    public async Task RespondsToDeviceQueries()
    {
        // Arrange
        var t = Term();
        var replies = new List<string>();
        t.Response = data => replies.Add(Encoding.ASCII.GetString(data));

        // Act
        Feed(t, "\e[3;5H\e[6n\e[c\e[?2004$p");

        // Assert
        await Assert.That(replies).IsEquivalentTo(["\e[3;5R", "\e[?62;4;22c", "\e[?2004;2$y"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SetsTitleFromOsc()
    {
        // Arrange
        var t = Term();

        // Act
        Feed(t, "\e]2;my title\a");

        // Assert
        await Assert.That(t.Title).IsEqualTo("my title");
    }

    [Test]
    public async Task OriginModeIsRelativeToMargins()
    {
        // Arrange
        var t = Term(rows: 6);

        // Act
        Feed(t, "\e[2;5r\e[?6h\e[1;1HX");

        // Assert
        await Assert.That(t.GetLineText(1)).IsEqualTo("X");
    }

    [Test]
    public async Task ResizeKeepsCursorLineVisible()
    {
        // Arrange
        var t = Feed(Term(cols: 10, rows: 4), "1\r\n2\r\n3\r\n4");

        // Act
        t.Resize(10, 2);

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("3\n4\n");
        await Assert.That(t.ScrollbackCount).IsEqualTo(2);
    }

    [Test]
    public async Task GrowingRowsPullsLinesBackOutOfScrollback()
    {
        // Arrange
        var t = Feed(Term(cols: 10, rows: 4), "1\r\n2\r\n3\r\n4");
        t.Resize(10, 2);

        // Act
        t.Resize(10, 4);

        // Assert
        await Assert.That(Screen(t)).IsEqualTo("1\n2\n3\n4\n");
        await Assert.That(t.CursorY).IsEqualTo(3);
    }

    /// <summary>Ten lines through a 2-row screen with room for 3 lines of history.</summary>
    private static Terminal OverflowedScrollback()
    {
        var t = Term(cols: 4, rows: 2, scrollback: 3);
        for (int i = 0; i < 10; i++) Feed(t, $"{i}\r\n");
        return t;
    }

    [Test]
    public async Task ScrollbackRecyclesRowsWhenFull()
    {
        // Act
        var t = OverflowedScrollback();

        // Assert
        await Assert.That(t.ScrollbackCount).IsEqualTo(3);
    }

    [Test]
    public async Task FullScrollbackKeepsTheNewestLines()
    {
        // Arrange
        var t = OverflowedScrollback();

        // Act
        t.ScrollView(3);

        // Assert
        await Assert.That(RowText(t.GetVisibleRow(0))).IsEqualTo("6");
    }
}
