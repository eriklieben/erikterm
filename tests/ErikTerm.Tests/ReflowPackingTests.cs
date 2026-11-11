using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class ReflowPackingTests
{
    private static Row Packed(int stored, bool wrapped = false)
    {
        var cells = new Cell[stored * 2];
        for (int i = 0; i < stored; i++) cells[i].Rune = 'a';
        var row = new Row(cells) { Wrapped = wrapped };
        row.Pack();
        return row;
    }

    private static List<Row> Copied(int moveOnlyColumns, params Row[] rows)
    {
        var scrollback = new Scrollback(10);
        foreach (var row in rows) scrollback.Push(row);
        var copy = new List<Row>();
        scrollback.CopyTo(copy, moveOnlyColumns);
        return copy;
    }

    [Test]
    public async Task A_whole_line_that_fits_stays_packed()
    {
        // Act
        var copy = Copied(20, Packed(8));

        // Assert
        await Assert.That(copy[0].IsPacked).IsTrue();
    }

    [Test]
    public async Task A_line_too_wide_for_the_new_width_is_unpacked()
    {
        // Act
        var copy = Copied(4, Packed(8));

        // Assert
        await Assert.That(copy[0].IsPacked).IsFalse();
        await Assert.That(copy[0].Cells[7].Rune).IsEqualTo('a');
    }

    [Test]
    public async Task Both_halves_of_a_wrapped_line_are_unpacked()
    {
        // Act: the second row fits and isn't marked wrapped, but it is the end of the first one's line
        var copy = Copied(20, Packed(8, wrapped: true), Packed(4), Packed(4));

        // Assert
        await Assert.That(copy[0].IsPacked).IsFalse();
        await Assert.That(copy[1].IsPacked).IsFalse();
        await Assert.That(copy[2].IsPacked).IsTrue();
    }

    [Test]
    public async Task No_room_given_unpacks_every_row()
    {
        // Act: what reflow asks for when inline images need their cells looked at
        var copy = Copied(0, Packed(1));

        // Assert
        await Assert.That(copy[0].IsPacked).IsFalse();
    }

    [Test]
    public async Task Packed_history_comes_back_onto_a_screen_that_grows_while_the_width_changes()
    {
        // Arrange
        var t = new Terminal(20, 2, 100);
        t.Feed(Encoding.ASCII.GetBytes("first\r\nsecond\r\nthird\r\nfourth\r\nfifth"));
        while (t.CompactScrollback(1000)) { }

        // Act
        t.Resize(30, 8);

        // Assert
        string text = t.GetText(t.FirstLine, 0, t.EndLine - 1, int.MaxValue);
        await Assert.That(text).Contains("first");
        await Assert.That(t.GetScreenRow(0).Cells.Length).IsEqualTo(30);
        await Assert.That(t.GetScreenRow(0).IsPacked).IsFalse();
    }
}
