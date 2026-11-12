using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>
/// Control sequences whose other outcomes the main terminal tests don't reach: back tabs and tab clears, reverse index
/// below the margin, the save-cursor mode, DECRQM and DECRQSS answers, cursor styles and the title stack.
/// </summary>
public class TerminalReplyTests
{
    private static (Terminal T, List<string> Replies) Term(int cols = 40, int rows = 10)
    {
        var t = new Terminal(cols, rows);
        var replies = new List<string>();
        t.Response = d => replies.Add(Encoding.ASCII.GetString(d));
        return (t, replies);
    }

    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    [Test]
    [Arguments("\e[30G\e[Z", 24)]      // back to the tab stop before
    [Arguments("\e[30G\e[2Z", 16)]
    [Arguments("\e[3G\e[5Z", 0)]       // stops at the left edge
    [Arguments("\e[Z", 0)]             // already there
    public async Task BackTabMovesToEarlierStops(string input, int col)
    {
        // Arrange
        var (t, _) = Term();

        // Act
        Feed(t, input);

        // Assert
        await Assert.That(t.CursorX).IsEqualTo(col);
    }

    [Test]
    public async Task TabClearRemovesTheStopUnderTheCursor()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "\e[9G\e[g");   // column 9 is the stop at 8

        // Act
        Feed(t, "\r\t");

        // Assert
        await Assert.That(t.CursorX).IsEqualTo(16);
    }

    [Test]
    public async Task TabClearAllLeavesNoStops()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "\e[3g");

        // Act
        Feed(t, "\r\t");

        // Assert: to the last column
        await Assert.That(t.CursorX).IsEqualTo(39);
    }

    [Test]
    public async Task TabClearWithAnotherModeKeepsTheStops()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "\e[9G\e[1g");

        // Act
        Feed(t, "\r\t");

        // Assert
        await Assert.That(t.CursorX).IsEqualTo(8);
    }

    [Test]
    public async Task ReverseIndexBelowTheTopMarginMovesUp()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "\e[3;8r\e[6;1H");

        // Act
        Feed(t, "\eM");

        // Assert
        await Assert.That(t.CursorY).IsEqualTo(4);
    }

    [Test]
    public async Task ReverseIndexAboveTheRegionStopsAtTheTopRow()
    {
        // Arrange: the cursor on row 1, outside a region that starts at row 3
        var (t, _) = Term();
        Feed(t, "\e[3;8r\e[1;1H");

        // Act
        Feed(t, "\eM");

        // Assert
        await Assert.That(t.CursorY).IsEqualTo(0);
    }

    [Test]
    public async Task Mode1048SavesAndRestoresTheCursor()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "\e[4;7H\e[?1048h\e[1;1H");

        // Act
        Feed(t, "\e[?1048l");

        // Assert
        await Assert.That((t.CursorY, t.CursorX)).IsEqualTo((3, 6));
    }

    [Test]
    public async Task Mode1049TwiceStaysOnTheAlternateScreen()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "main\e[?1049h");

        // Act
        Feed(t, "alt\e[?1049h");

        // Assert: the second one is ignored, so leaving once returns to the main screen
        Feed(t, "\e[?1049l");
        await Assert.That(t.GetLineText(0).TrimEnd()).IsEqualTo("main");
    }

    [Test]
    public async Task Mode1047ClearsTheAlternateScreenOnLeaving()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "\e[?1047hgone\e[?1047l");

        // Act: back on the alternate screen without clearing it
        Feed(t, "\e[?47h");

        // Assert
        await Assert.That(t.GetLineText(0).Trim()).IsEqualTo("");
    }

    [Test]
    [Arguments("\e[?6$p", "\e[?6;2$y")]
    [Arguments("\e[?6h\e[?6$p", "\e[?6;1$y")]
    [Arguments("\e[?7$p", "\e[?7;1$y")]
    [Arguments("\e[?25$p", "\e[?25;1$y")]
    [Arguments("\e[?1049$p", "\e[?1049;2$y")]
    [Arguments("\e[?47h\e[?47$p", "\e[?47;1$y")]
    [Arguments("\e[?2026$p", "\e[?2026;2$y")]
    [Arguments("\e[?2027$p", "\e[?2027;3$y")]
    [Arguments("\e[?2031$p", "\e[?2031;2$y")]
    [Arguments("\e[?2048$p", "\e[?2048;2$y")]
    [Arguments("\e[?1000h\e[?1000$p", "\e[?1000;1$y")]
    [Arguments("\e[?2004$p", "\e[?2004;2$y")]
    [Arguments("\e[?9999$p", "\e[?9999;0$y")]
    [Arguments("\e[4h\e[4$p", "\e[4;1$y")]
    [Arguments("\e[4$p", "\e[4;2$y")]
    [Arguments("\e[20h\e[20$p", "\e[20;1$y")]
    [Arguments("\e[2$p", "\e[2;4$y")]
    [Arguments("\e[12$p", "\e[12;4$y")]
    [Arguments("\e[99$p", "\e[99;0$y")]
    public async Task ModeReports(string input, string reply)
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, input);

        // Assert
        await Assert.That(replies.Last()).IsEqualTo(reply);
    }

    [Test]
    [Arguments("r", "\eP1$r1;10r\e\\")]
    [Arguments(" q", "\eP1$r2 q\e\\")]   // a steady block by default
    [Arguments("\"p", "\eP1$r62;1\"p\e\\")]
    [Arguments("\"q", "\eP1$r0\"q\e\\")]
    [Arguments("t", "\eP1$r10t\e\\")]
    [Arguments("*|", "\eP1$r10*|\e\\")]
    [Arguments("s", "\eP0$r\e\\")]   // no left/right margins
    public async Task StatusStringRequests(string setting, string reply)
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, "\eP$q" + setting + "\e\\");

        // Assert
        await Assert.That(replies.Last()).IsEqualTo(reply);
    }

    [Test]
    [Arguments(3, "\eP1$r3 q\e\\")]
    [Arguments(4, "\eP1$r4 q\e\\")]
    [Arguments(5, "\eP1$r5 q\e\\")]
    [Arguments(6, "\eP1$r6 q\e\\")]
    [Arguments(2, "\eP1$r2 q\e\\")]
    public async Task CursorStyleIsReportedAsSet(int style, string reply)
    {
        // Arrange
        var (t, replies) = Term();
        Feed(t, $"\e[{style} q");

        // Act
        Feed(t, "\eP$q q\e\\");

        // Assert
        await Assert.That(replies.Last()).IsEqualTo(reply);
    }

    [Test]
    public async Task CursorStyleZeroIsTheDefault()
    {
        // Arrange
        var (t, _) = Term();
        t.DefaultCursorShape = CursorShape.Bar;
        Feed(t, "\e[4 q");

        // Act
        Feed(t, "\e[0 q");

        // Assert
        await Assert.That(t.CursorShape).IsEqualTo(CursorShape.Bar);
    }

    [Test]
    [Arguments("\e[6n", "\e[3;5R")]
    [Arguments("\e[?6n", "\e[?3;5;1R")]
    [Arguments("\e[?15n", "\e[?13n")]
    public async Task CursorAndPrinterReports(string query, string reply)
    {
        // Arrange
        var (t, replies) = Term();
        Feed(t, "\e[3;5H");

        // Act
        Feed(t, query);

        // Assert
        await Assert.That(replies.Last()).IsEqualTo(reply);
    }

    [Test]
    public async Task CursorReportIsRelativeToTheRegionInOriginMode()
    {
        // Arrange
        var (t, replies) = Term();
        Feed(t, "\e[3;8r\e[?6h\e[2;4H");

        // Act
        Feed(t, "\e[?6n");

        // Assert
        await Assert.That(replies.Last()).IsEqualTo("\e[?2;4;1R");
    }

    [Test]
    public async Task UnknownStatusReportsGetNoAnswer()
    {
        // Arrange
        var (t, replies) = Term();

        // Act
        Feed(t, "\e[7n\e[?99n");

        // Assert
        await Assert.That(replies).IsEmpty();
    }

    [Test]
    public async Task TitleStackRestoresPushedTitles()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "\e]2;first\a\e[22;2t\e]2;second\a");

        // Act
        Feed(t, "\e[23;2t");

        // Assert
        await Assert.That(t.Title).IsEqualTo("first");
    }

    [Test]
    public async Task TitleStackIgnoresIconTitleOnlyPushAndPop()
    {
        // Arrange
        var (t, _) = Term();
        Feed(t, "\e]2;first\a\e[22;1t\e]2;second\a");

        // Act
        Feed(t, "\e[23;1t\e[23;0t");

        // Assert: nothing was pushed, so nothing to pop
        await Assert.That(t.Title).IsEqualTo("second");
    }

    [Test]
    public async Task TitleStackHoldsAtMostTen()
    {
        // Arrange: push eleven titles; the eleventh push is dropped
        var (t, _) = Term();
        for (int i = 0; i < 11; i++) Feed(t, $"\e]2;t{i}\a\e[22t");
        Feed(t, "\e]2;last\a");

        // Act: pop once
        Feed(t, "\e[23t");

        // Assert
        await Assert.That(t.Title).IsEqualTo("t9");
    }
}
