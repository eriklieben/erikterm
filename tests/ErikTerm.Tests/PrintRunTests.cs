using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

// Every non-ASCII literal is spelled with \u / \U escapes (see ClusterTests).
/// <summary>
/// The whole-sequence paths of the print path (Terminal.PrintUtf8, VtParser.TryDispatchWholeCsi): feeding a stream
/// whole must leave the same screen as feeding it one byte at a time, which takes only the state-machine path.
/// </summary>
public class PrintRunTests
{
    private static string State(Terminal t)
    {
        var s = new StringBuilder();
        s.Append($"cursor {t.CursorX},{t.CursorY} sb {t.ScrollbackCount}\n");
        for (long line = t.FirstLine; line < t.EndLine; line++)
        {
            var row = t.GetLine(line)!;
            s.Append($"{(row.Wrapped ? 'W' : '-')} ");
            for (int x = 0; x < row.Width; x++)
            {
                var c = row.CellAt(x);
                s.Append(c.Rune).Append(':').Append(c.Fg).Append(':').Append(c.Bg).Append(':').Append((int)c.Flags);
                if (Terminal.TryGetCluster(row, x, out var text)) s.Append('[').Append(text).Append(']');
                s.Append(' ');
            }
            s.Append('\n');
        }
        return s.ToString();
    }

    private static Terminal Whole(byte[] bytes, int cols, int rows, int scrollback = 10)
    {
        var t = new Terminal(cols, rows, scrollback);
        t.Feed(bytes);
        return t;
    }

    private static Terminal ByteByByte(byte[] bytes, int cols, int rows, int scrollback = 10)
    {
        var t = new Terminal(cols, rows, scrollback);
        foreach (byte b in bytes) t.Feed([b]);
        return t;
    }

    /// <summary>The whole-buffer feed matches the per-byte state machine for this input.</summary>
    private static async Task AssertSameAsPerByte(byte[] bytes, int cols, int rows)
    {
        // Act
        string whole = State(Whole(bytes, cols, rows));
        string perByte = State(ByteByByte(bytes, cols, rows));

        // Assert
        await Assert.That(whole).IsEqualTo(perByte);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Test]
    public async Task Utf8RunEndingExactlyAtTheLineEndWrapsOnlyOnTheNextCharacter()
    {
        // Arrange: é fits column 4; nothing follows it on the row.
        var t = new Terminal(5, 2);

        // Act
        t.Feed(Utf8("ééééé"));

        // Assert
        await Assert.That(t.CursorX).IsEqualTo(4);
        await Assert.That(t.CellAt(4, 0).Rune).IsEqualTo(0xE9);
        await Assert.That(t.CursorY).IsEqualTo(0);
    }

    [Test]
    public async Task Utf8RunWrapsAtTheMargin() =>
        await AssertSameAsPerByte(Utf8("ééééééx"), 5, 2);

    [Test]
    public async Task WideCharacterAtTheMarginMovesToTheNextRow()
    {
        // Arrange: a narrow character takes the last column before a wide one needs two.
        var t = new Terminal(3, 2);

        // Act
        t.Feed(Utf8("éé日"));

        // Assert: the third column is blanked and 日 starts the next row.
        await Assert.That(t.CellAt(2, 0).Rune).IsEqualTo(0);
        await Assert.That(t.GetScreenRow(0).Wrapped).IsTrue();
        await Assert.That(t.CellAt(0, 1).Rune).IsEqualTo(0x65E5);
        await Assert.That((t.CellAt(1, 1).Flags & CellAttributes.WideTrail) != 0).IsTrue();
    }

    [Test]
    public async Task WideCharactersOfARunFillTheRowThenWrap() =>
        await AssertSameAsPerByte(Utf8("日本語日本語日"), 6, 3);

    [Test]
    public async Task WideCharacterAfterAPendingWrap() =>
        await AssertSameAsPerByte(Utf8("ééé日"), 3, 3);

    [Test]
    public async Task OverwritingHalfAWideCharacterBlanksTheOtherHalf() =>
        await AssertSameAsPerByte(Concat(Utf8("日本\r"), Utf8("é")), 6, 2);

    [Test]
    public async Task RowWithLinksTakesThePerCharacterPath() =>
        await AssertSameAsPerByte(Utf8("\u001b]8;;http://a\u001b\\ab\u001b]8;;\u001b\\é日"), 10, 2);

    [Test]
    public async Task CombiningMarksJoinThePreviousCell()
    {
        // Act
        var t = Whole(Utf8("éé́"), 6, 2);

        // Assert
        await Assert.That(Terminal.TryGetCluster(t.GetScreenRow(0), 0, out _)).IsTrue();
        await Assert.That(Terminal.TryGetCluster(t.GetScreenRow(0), 1, out _)).IsTrue();
        await Assert.That(t.CursorX).IsEqualTo(2);
    }

    [Test]
    public async Task EmojiSequencesMatchThePerBytePath() =>
        await AssertSameAsPerByte(Utf8("\U0001F469‍\U0001F4BB\U0001F44D\U0001F3FD\U0001F1F3\U0001F1F1☁️⚡️"), 9, 3);

    [Test]
    public async Task ZeroWidthCharacterInARunJoinsAcrossThePendingWrap() =>
        await AssertSameAsPerByte(Utf8("ééé́é"), 3, 2);

    [Test]
    [Arguments(new byte[] { 0xC0, 0x80 })] // overlong
    [Arguments(new byte[] { 0xED, 0xA0, 0x80 })] // surrogate
    [Arguments(new byte[] { 0xF4, 0x90, 0x80, 0x80 })] // past U+10FFFF
    [Arguments(new byte[] { 0xFF })]
    [Arguments(new byte[] { 0x80 })]
    [Arguments(new byte[] { 0xE2, 0x41 })] // bad continuation
    [Arguments(new byte[] { 0xC3 })] // stream ends inside a sequence
    [Arguments(new byte[] { 0xE2, 0x82 })]
    public async Task InvalidUtf8BetweenValidTextMatchesThePerBytePath(byte[] bad)
    {
        // Arrange
        byte[] input = Concat(Utf8("éa"), bad, Utf8("éb日"));

        // Act + Assert
        await AssertSameAsPerByte(input, 8, 3);
    }

    [Test]
    public async Task SequenceSplitAcrossFeedsCompletes()
    {
        // Arrange: 日 (E6 97 A5) cut after its first byte, then after its second.
        var t = new Terminal(5, 2);

        // Act
        t.Feed(new byte[] { 0xC3, 0xA9, 0xE6 });
        t.Feed(new byte[] { 0x97 });
        t.Feed(new byte[] { 0xA5, (byte)'x' });

        // Assert
        await Assert.That(t.CellAt(1, 0).Rune).IsEqualTo(0x65E5);
        await Assert.That(t.CellAt(3, 0).Rune).IsEqualTo('x');
    }

    [Test]
    public async Task InsertModeAndCharacterSetsGoThroughThePerCharacterPath() =>
        await AssertSameAsPerByte(Utf8("\u001b[4habc\u001b[Dé日\u001b[4l\u001b(0qqé\u001b(Bé"), 8, 2);

    [Test]
    public async Task UnderlinedRunGoesThroughThePerCharacterPath()
    {
        // Act: colored underline on a run with wide characters.
        var t = Whole(Utf8("\u001b[4;58:5:3mé日"), 8, 2);

        // Assert
        await Assert.That(t.GetScreenRow(0).UnderlineColorAt(0)).IsEqualTo(TermColor.Indexed(3));
        await Assert.That(t.GetScreenRow(0).UnderlineColorAt(2)).IsEqualTo(TermColor.Indexed(3));
    }

    [Test]
    public async Task RepeatRepeatsTheLastCharacterOfARun()
    {
        // Act
        var t = Whole(Utf8("é\u001b[2b"), 8, 2);

        // Assert
        await Assert.That(t.CursorX).IsEqualTo(3);
        await Assert.That(t.CellAt(2, 0).Rune).IsEqualTo(0xE9);
    }

    [Test]
    [Arguments("\u001b[38;2;10;20;30mX")]
    [Arguments("\u001b[38:2::1:2:3mX")]
    [Arguments("\u001b[;;mX")]
    [Arguments("\u001b[;1mX")]
    [Arguments("\u001b[mX")]
    [Arguments("\u001b[999999;2HX")] // values clamp
    [Arguments("\u001b[?25lX")] // private marker: the state machine's job
    [Arguments("\u001b[2$pX")] // intermediate
    [Arguments("\u001b[1\u0007mX")] // a control byte inside the sequence
    public async Task CsiSequencesMatchThePerBytePath(string input) =>
        await AssertSameAsPerByte(Utf8(input), 10, 3);

    [Test]
    public async Task CsiWithMoreParametersThanFitMatchesThePerBytePath()
    {
        // Arrange: 40 parameters, more than MaxParams.
        string seq = "\u001b[" + string.Join(';', Enumerable.Range(1, 40)) + "mX";

        // Act + Assert
        await AssertSameAsPerByte(Utf8(seq), 10, 2);
    }

    [Test]
    public async Task CsiSplitAcrossFeedsIsHandledByTheStateMachine()
    {
        // Arrange
        var whole = new Terminal(10, 2);
        var split = new Terminal(10, 2);

        // Act
        whole.Feed(Utf8("\u001b[1;31mX"));
        split.Feed(Utf8("\u001b[1;3"));
        split.Feed(Utf8("1mX"));

        // Assert
        await Assert.That(State(split)).IsEqualTo(State(whole));
        await Assert.That(whole.CellAt(0, 0).Flags & CellAttributes.Bold).IsEqualTo(CellAttributes.Bold);
    }

    [Test]
    public async Task CsiFastPathSetsColorsFromTheSameBufferAsText()
    {
        // Arrange
        var t = new Terminal(10, 2);

        // Act
        t.Feed(Utf8("\u001b[38;2;1;2;3mab\u001b[48;5;9mc"));

        // Assert
        await Assert.That(t.CellAt(0, 0).Fg).IsEqualTo(TermColor.Rgb(1, 2, 3));
        await Assert.That(t.CellAt(2, 0).Bg).IsEqualTo(TermColor.Indexed(9));
    }

    // ---- Rows made while the scrollback fills ------------------------------------------------------

    private static Terminal Flooded(int lines, int cols = 8, int rows = 2, int scrollback = 50)
    {
        var t = new Terminal(cols, rows, scrollback);
        for (int i = 0; i < lines; i++) t.Feed(Utf8($"line {i}\r\n"));
        return t;
    }

    [Test]
    public async Task RowsMadeWhileTheScrollbackFillsArePinned()
    {
        // Act
        var t = Flooded(10);

        // Assert: the rows that scrolled in after the first ones were made here (the first ones are the initial screen rows).
        await Assert.That(t.GetLine(t.ScreenTopLine - 1)!.CellsPinned).IsTrue();
    }

    [Test]
    public async Task CompactionMovesPinnedRowsOffThePinnedHeap()
    {
        // Arrange: enough short lines to fill the scrollback and leave pinned rows on screen.
        var t = Flooded(60);
        string before = State(t);

        // Act
        while (t.CompactScrollback(1000)) { }

        // Assert: nothing visible changed and no row keeps a pinned array.
        await Assert.That(State(t)).IsEqualTo(before);
        for (long l = t.FirstLine; l < t.EndLine; l++)
            await Assert.That(t.GetLine(l)!.CellsPinned).IsFalse();
    }

    [Test]
    public async Task CompactionMovesPinnedRowsOffEvenWhenTheyCannotBeTrimmed()
    {
        // Arrange: a line that fills the row can't be trimmed, so its array has to be replaced.
        var t = new Terminal(4, 2, 20);
        t.Feed(Utf8("abcd\r\nwxyz\r\nabcd\r\nwxyz\r\n"));
        var row = t.GetLine(t.ScreenTopLine - 1)!;
        var pinned = row.CellsPinned;

        // Act
        while (t.CompactScrollback(1000)) { }

        // Assert
        var after = t.GetLine(t.ScreenTopLine - 1)!;
        await Assert.That(pinned).IsTrue();
        await Assert.That(after.CellsPinned).IsFalse();
        await Assert.That(after.Cells.Length).IsEqualTo(4);
        await Assert.That(after.CellAt(0).Rune).IsEqualTo('a');
    }

    [Test]
    public async Task CompactedBytesCountsPinnedArraysLetGo()
    {
        // Arrange
        var t = new Terminal(4, 2, 20);
        t.Feed(Utf8("abcd\r\nwxyz\r\nabcd\r\nwxyz\r\n"));

        // Act
        while (t.CompactScrollback(1000)) { }

        // Assert: both full rows' arrays were replaced, whole or trimmed.
        await Assert.That(t.CompactedBytes).IsGreaterThan(0);
    }

    [Test]
    public async Task ARunEndingOnTheFirstHalfOfAWideCharacterKeepsItsLastLetter()
    {
        // Arrange: x, a wide character in columns 1 and 2, y
        var t = new Terminal(10, 2);
        t.Feed("x\u4e16y"u8);

        // Act: the run ends on the wide character's first half
        t.Feed("\e[1;1Hab"u8);

        // Assert
        var cells = t.GetScreenRow(0).Cells;
        await Assert.That((char)cells[0].Rune).IsEqualTo('a');
        await Assert.That((char)cells[1].Rune).IsEqualTo('b');
        await Assert.That(cells[1].Flags & CellAttributes.WideLead).IsEqualTo(default(CellAttributes));
        await Assert.That(cells[2].Flags & CellAttributes.WideTrail).IsEqualTo(default(CellAttributes));
        await Assert.That((char)cells[3].Rune).IsEqualTo('y');
    }

    [Test]
    public async Task ARunEndingJustBeforeAWideCharacterLeavesItWhole()
    {
        // Arrange: a wide character in columns 2 and 3
        var t = new Terminal(10, 2);
        t.Feed("\e[1;3H\u4e16"u8);

        // Act: the run stops right in front of it
        t.Feed("\e[1;1Hab"u8);

        // Assert
        var cells = t.GetScreenRow(0).Cells;
        await Assert.That((char)cells[1].Rune).IsEqualTo('b');
        await Assert.That(cells[2].Rune).IsEqualTo(0x4e16);
        await Assert.That(cells[2].Flags & CellAttributes.WideLead).IsEqualTo(CellAttributes.WideLead);
        await Assert.That(cells[3].Flags & CellAttributes.WideTrail).IsEqualTo(CellAttributes.WideTrail);
    }
}
