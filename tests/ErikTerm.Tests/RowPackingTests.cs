using System.Runtime.CompilerServices;
using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class RowPackingTests
{
    private static Terminal Fed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    private static void Compact(Terminal t)
    {
        while (t.CompactScrollback(1000)) { }
    }

    private static Cell[] CellsOf(Row row) => row.Cells.ToArray();

    private static Row RowOf(params Cell[] cells) => new(cells);

    private static Cell C(int rune, uint fg = 0, uint bg = 0, CellAttributes flags = 0) =>
        new() { Rune = rune, Fg = fg, Bg = bg, Flags = flags };

    [Test]
    public async Task Plain_text_is_packed_and_comes_back_the_same()
    {
        // Arrange
        var cells = new[] { C('h'), C('i'), default, default, default, default, default, default };
        var row = RowOf(cells);
        var before = cells.ToArray();

        // Act
        var freed = row.Pack();
        bool packed = row.IsPacked;
        int stored = row.Cells.Length;
        row.Unpack();

        // Assert
        await Assert.That(freed).IsNotNull();
        await Assert.That(packed).IsTrue();
        await Assert.That(stored).IsEqualTo(0);
        await Assert.That(row.IsPacked).IsFalse();
        await Assert.That(row.Cells.SequenceEqual(before)).IsTrue();
    }

    [Test]
    public async Task Colors_attributes_and_non_ascii_text_survive()
    {
        // Arrange
        var cells = new[]
        {
            C('a', TermColor.Indexed(1)), C('b', TermColor.Indexed(1)), C(0x65E5, TermColor.Rgb(1, 2, 3), 7, CellAttributes.WideLead),
            C(0, TermColor.Rgb(1, 2, 3), 7, CellAttributes.WideTrail), C(0x1F600, 0, 0, CellAttributes.Bold), C('z'), C('y'), C('x'),
            C('w'), C('v'), C('u'), C('t'), C('s'), C('r'), C('q'), C('p'),
        };
        var row = RowOf(cells);
        var before = cells.ToArray();

        // Act
        row.Pack();
        row.Unpack();

        // Assert
        await Assert.That(row.Cells.SequenceEqual(before)).IsTrue();
    }

    [Test]
    public async Task A_row_that_would_not_get_smaller_stays_as_it_is()
    {
        // Arrange: two cells of different colors take more room packed than as cells
        var cells = new[] { C('a', 1), C('b', 2) };
        var row = RowOf(cells);

        // Act
        var freed = row.Pack();

        // Assert
        await Assert.That(freed).IsNull();
        await Assert.That(row.IsPacked).IsFalse();
        await Assert.That(ReferenceEquals(row.Cells, cells)).IsTrue();
    }

    [Test]
    public async Task A_blank_row_is_left_to_trimming()
    {
        // Arrange
        var row = new Row(8);

        // Act
        var freed = row.Pack();

        // Assert
        await Assert.That(freed).IsNull();
        await Assert.That(row.IsPacked).IsFalse();
    }

    [Test]
    public async Task A_wrapped_row_keeps_its_full_length()
    {
        // Arrange
        var row = new Row(32) { Wrapped = true };
        row.Cells[0] = C('a');

        // Act
        row.Pack();
        row.Unpack();

        // Assert
        await Assert.That(row.Cells.Length).IsEqualTo(32);
        await Assert.That(row.Width).IsEqualTo(32);
    }

    [Test]
    public async Task Replacing_the_cells_drops_what_was_packed()
    {
        // Arrange
        var row = RowOf(C('a'), default, default, default);
        row.Pack();
        var cells = new Cell[4];

        // Act
        row.SetCells(cells, 4);

        // Assert
        await Assert.That(row.IsPacked).IsFalse();
        await Assert.That(ReferenceEquals(row.Cells, cells)).IsTrue();
    }

    [Test]
    public async Task Reading_a_packed_line_shows_the_original_cells()
    {
        // Arrange
        var t = Fed(new Terminal(20, 3, 100), "\e[1;31mred\e[0m \e[44mblue\e[0m plain\r\n\n\n\n");
        var before = CellsOf(t.GetLine(t.FirstLine)!).Take(20).ToArray();

        // Act
        Compact(t);
        var after = CellsOf(t.GetLine(t.FirstLine)!);

        // Assert
        await Assert.That(after.Take(14).SequenceEqual(before.Take(14))).IsTrue();
        await Assert.That(t.GetText(t.FirstLine, 0, t.FirstLine, 19)).IsEqualTo("red blue plain");
    }

    [Test]
    public async Task A_line_read_again_is_packed_at_the_next_compaction()
    {
        // Arrange
        var t = Fed(new Terminal(20, 3, 100), "hello\r\n\n\n\n");
        Compact(t);
        bool before = t.NeedsCompaction;

        // Act
        _ = t.GetLine(t.FirstLine);
        bool afterRead = t.NeedsCompaction;
        Compact(t);
        bool afterCompaction = t.NeedsCompaction;

        // Assert
        await Assert.That(before).IsFalse();
        await Assert.That(afterRead).IsTrue();
        await Assert.That(afterCompaction).IsFalse();
    }

    [Test]
    public async Task Packed_lines_that_are_pushed_out_become_usable_screen_rows()
    {
        // Arrange: a scrollback of two that is full of packed lines
        var t = Fed(new Terminal(10, 2, 2), "one\r\ntwo\r\nthree\r\nfour\r\n");
        Compact(t);

        // Act
        Fed(t, "five\r\nsix\r\nseven");

        // Assert
        await Assert.That(t.GetText(t.FirstLine, 0, t.EndLine - 1, int.MaxValue)).Contains("seven");
        await Assert.That(t.GetLine(t.ScreenTopLine)!.Cells.Length).IsEqualTo(10);
    }

    [Test]
    public async Task A_line_comes_back_when_the_screen_grows()
    {
        // Arrange
        var t = Fed(new Terminal(20, 2, 100), "first\r\nsecond\r\nthird\r\nfourth");
        Compact(t);

        // Act
        t.Resize(20, 6);

        // Assert
        await Assert.That(t.GetText(t.FirstLine, 0, t.EndLine - 1, int.MaxValue)).Contains("first");
        await Assert.That(t.GetLine(t.ScreenTopLine)!.Cells.Length).IsEqualTo(20);
    }

    [Test]
    public async Task Reflow_keeps_the_text_of_packed_lines()
    {
        // Arrange
        var t = Fed(new Terminal(20, 2, 100), "abcdefghijklmnop\r\nsecond line\r\nthird\r\nfourth");
        string before = t.GetText(t.FirstLine, 0, t.EndLine - 1, int.MaxValue);
        Compact(t);

        // Act
        t.Resize(8, 2);
        t.Resize(20, 2);

        // Assert
        await Assert.That(t.GetText(t.FirstLine, 0, t.EndLine - 1, int.MaxValue)).IsEqualTo(before);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_scrollback")]
    private static extern ref Scrollback ScrollbackOf(Terminal t);

    private static Terminal LongHistory(int lines) =>
        Fed(new Terminal(20, 3, lines + 10), string.Concat(Enumerable.Repeat("a quiet line\r\n", lines)));

    [Test]
    public async Task Packing_a_read_line_again_only_looks_at_what_was_read()
    {
        // Arrange: a long packed history and a step far too small to walk all of it
        var t = LongHistory(4000);
        Compact(t);
        _ = t.GetLine(t.FirstLine + 100);

        // Act
        bool more = t.CompactScrollback(2);

        // Assert
        await Assert.That(more).IsFalse();
        await Assert.That(t.NeedsCompaction).IsFalse();
        await Assert.That(ScrollbackOf(t).Peek(100).IsPacked).IsTrue();
    }

    [Test]
    public async Task The_lines_a_redraw_reads_are_not_counted_as_memory_handed_back()
    {
        // Arrange
        var t = LongHistory(4000);
        Compact(t);
        long before = t.CompactedBytes;

        // Act: a screenful of the history is read and packed again, as every frame of a scrolled-back pane does
        for (int y = 0; y < 50; y++) _ = t.GetLine(t.FirstLine + 200 + y);
        Compact(t);

        // Assert
        await Assert.That(t.CompactedBytes).IsEqualTo(before);
    }

    [Test]
    public async Task A_whole_history_read_counts_as_memory_handed_back()
    {
        // Arrange
        var t = LongHistory(4000);
        Compact(t);
        long before = t.CompactedBytes;

        // Act
        for (long line = t.FirstLine; line < t.EndLine; line++) _ = t.GetLine(line);
        Compact(t);

        // Assert
        await Assert.That(t.CompactedBytes).IsGreaterThan(before);
    }

    [Test]
    public async Task Lines_read_and_then_evicted_are_forgotten()
    {
        // Arrange: lines read and still waiting to be packed again when they leave the ring
        var t = LongHistory(200);
        Compact(t);
        _ = t.GetLine(t.FirstLine + 10);

        // Act
        Fed(t, string.Concat(Enumerable.Repeat("newer output\r\n", 500)));
        Compact(t);

        // Assert
        await Assert.That(t.NeedsCompaction).IsFalse();
        await Assert.That(t.GetText(t.FirstLine, 0, t.FirstLine, int.MaxValue)).IsEqualTo("newer output");
    }
}
