using System.Text;
using ErikTerm.App.Ipc;
using ErikTerm.App.Rendering;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>Styled underlines (SGR 4:n, 21), underline color (SGR 58 / 59) and overline (SGR 53 / 55).</summary>
public class UnderlineTests
{
    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    private static UnderlineStyle StyleAt(Terminal t, int x, int y = 0) => CellStyle.Underline(t.CellAt(x, y).Flags);

    [Test]
    [Arguments("\e[4mx", UnderlineStyle.Single)]
    [Arguments("\e[4:1mx", UnderlineStyle.Single)]
    [Arguments("\e[4:2mx", UnderlineStyle.Double)]
    [Arguments("\e[4:3mx", UnderlineStyle.Curly)]
    [Arguments("\e[4:4mx", UnderlineStyle.Dotted)]
    [Arguments("\e[4:5mx", UnderlineStyle.Dashed)]
    [Arguments("\e[21mx", UnderlineStyle.Double)]
    [Arguments("\e[4:3m\e[4:0mx", UnderlineStyle.None)]
    [Arguments("\e[4:3m\e[24mx", UnderlineStyle.None)]
    [Arguments("\e[4:3m\e[0mx", UnderlineStyle.None)]
    [Arguments("\e[4:3m\e[4:9mx", UnderlineStyle.Curly)] // unknown style: ignored
    public async Task SgrSetsTheUnderlineStyle(string input, UnderlineStyle expected)
    {
        // Arrange
        var t = new Terminal(10, 2);

        // Act
        Feed(t, input);

        // Assert
        await Assert.That(StyleAt(t, 0)).IsEqualTo(expected);
    }

    [Test]
    public async Task StyleChangesKeepOtherAttributes()
    {
        // Arrange
        var t = new Terminal(10, 2);

        // Act
        Feed(t, "\e[1;3;9m\e[4:3m\e[4:4mx");

        // Assert
        var f = t.CellAt(0, 0).Flags;
        await Assert.That((f & (CellAttributes.Bold | CellAttributes.Italic | CellAttributes.Strike))).IsEqualTo(CellAttributes.Bold | CellAttributes.Italic | CellAttributes.Strike);
        await Assert.That(CellStyle.Underline(f)).IsEqualTo(UnderlineStyle.Dotted);
    }

    [Test]
    [Arguments("\e[53mx", true)]
    [Arguments("\e[53m\e[55mx", false)]
    [Arguments("\e[53m\e[0mx", false)]
    public async Task OverlineIsSetAndCleared(string input, bool expected)
    {
        // Arrange
        var t = new Terminal(10, 2);

        // Act
        Feed(t, input);

        // Assert
        await Assert.That((t.CellAt(0, 0).Flags & CellAttributes.Overline) != 0).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\e[4;58;2;255;0;16mx", 0x02FF0010u)]
    [Arguments("\e[4;58:2::255:0:16mx", 0x02FF0010u)]
    [Arguments("\e[4;58:2:255:0:16mx", 0x02FF0010u)]
    [Arguments("\e[4;58;5;196mx", 0x010000C4u)]
    [Arguments("\e[4;58:5:196mx", 0x010000C4u)]
    [Arguments("\e[4;58:5:196m\e[59mx", 0u)]
    [Arguments("\e[4;58:5:196m\e[0;4mx", 0u)]
    public async Task SgrSetsTheUnderlineColor(string input, uint expected)
    {
        // Arrange
        var t = new Terminal(10, 2);

        // Act
        Feed(t, input);

        // Assert
        await Assert.That(t.GetScreenRow(0).UnderlineColorAt(0)).IsEqualTo(expected);
    }

    [Test]
    public async Task UnderlineColorDoesNotChangeTheTextColor()
    {
        // Arrange
        var t = new Terminal(10, 2);

        // Act
        Feed(t, "\e[31;4:3;58:2::1:2:3mx");

        // Assert
        await Assert.That(t.CellAt(0, 0).Fg).IsEqualTo(TermColor.Indexed(1));
    }

    [Test]
    public async Task OverwritingWithADefaultColoredUnderlineDropsTheOldColor()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2), "\e[4;58;5;1mabc");

        // Act
        Feed(t, "\r\e[59mX");

        // Assert
        await Assert.That(t.GetScreenRow(0).UnderlineColorAt(0)).IsEqualTo(0u);
        await Assert.That(t.GetScreenRow(0).UnderlineColorAt(1)).IsEqualTo(TermColor.Indexed(1));
    }

    [Test]
    public async Task ARowWithoutColoredUnderlinesHasNoTable()
    {
        // Arrange
        var t = new Terminal(10, 2);

        // Act
        Feed(t, "\e[4:3mplain curly");

        // Assert
        await Assert.That(t.GetScreenRow(0).HasUnderlineColors).IsFalse();
    }

    [Test]
    public async Task WideCharactersColorBothHalves()
    {
        // Arrange
        var t = new Terminal(10, 2);

        // Act
        Feed(t, "\e[4;58;5;2m世");

        // Assert
        var row = t.GetScreenRow(0);
        await Assert.That((row.UnderlineColorAt(0), row.UnderlineColorAt(1))).IsEqualTo((TermColor.Indexed(2), TermColor.Indexed(2)));
    }

    [Test]
    public async Task InsertCharactersMovesUnderlineColorsWithTheCells()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2), "\e[4;58;5;3mab\e[0m");

        // Act
        Feed(t, "\e[1G\e[2@");

        // Assert
        var row = t.GetScreenRow(0);
        await Assert.That((row.UnderlineColorAt(2), row.UnderlineColorAt(3))).IsEqualTo((TermColor.Indexed(3), TermColor.Indexed(3)));
    }

    [Test]
    public async Task DeleteCharactersMovesUnderlineColorsWithTheCells()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2), "xy\e[4;58;5;3mab\e[0m");

        // Act
        Feed(t, "\e[1G\e[2P");

        // Assert
        await Assert.That(t.GetScreenRow(0).UnderlineColorAt(0)).IsEqualTo(TermColor.Indexed(3));
    }

    [Test]
    public async Task ScrolledOffRowsKeepTheirStyleAndColor()
    {
        // Arrange
        var t = new Terminal(10, 2, 100);

        // Act
        Feed(t, "\e[4:3;58;5;5mcurly\e[0m\r\nb\r\nc");

        // Assert
        var row = t.GetLine(t.FirstLine)!;
        await Assert.That(CellStyle.Underline(row.Cells[0].Flags)).IsEqualTo(UnderlineStyle.Curly);
        await Assert.That(row.UnderlineColorAt(0)).IsEqualTo(TermColor.Indexed(5));
    }

    [Test]
    public async Task ARecycledScrollbackRowStartsWithoutColors()
    {
        // Arrange: a one-line scrollback, so the next scroll reuses the evicted row
        var t = new Terminal(10, 1, 1);
        Feed(t, "\e[4;58;5;5mcolor\e[0m\r\nnext\r\n");

        // Act
        Feed(t, "\e[4mfresh");

        // Assert
        await Assert.That(t.GetScreenRow(0).UnderlineColorAt(0)).IsEqualTo(0u);
    }

    [Test]
    public async Task ReflowCarriesUnderlineColorsToTheirNewRows()
    {
        // Arrange: "abcdefgh" wraps at 4; "efgh" has a red curly underline
        var t = Feed(new Terminal(4, 4, 100), "abcd\e[4:3;58;2;255;0;0mefgh\e[0m");

        // Act
        t.Resize(8, 4);

        // Assert
        var row = t.GetScreenRow(0);
        await Assert.That(t.GetLineText(0)).IsEqualTo("abcdefgh");
        await Assert.That(row.UnderlineColorAt(3)).IsEqualTo(0u);
        await Assert.That(row.UnderlineColorAt(4)).IsEqualTo(TermColor.Rgb(255, 0, 0));
        await Assert.That(CellStyle.Underline(row.Cells[7].Flags)).IsEqualTo(UnderlineStyle.Curly);
    }

    [Test]
    public async Task ReflowToANarrowerScreenSplitsTheColors()
    {
        // Arrange
        var t = Feed(new Terminal(8, 4, 100), "abcd\e[4;58;5;4mefgh\e[0m");

        // Act
        t.Resize(4, 4);

        // Assert
        var second = t.GetScreenRow(1);
        await Assert.That(t.GetLineText(1)).IsEqualTo("efgh");
        await Assert.That(second.UnderlineColorAt(0)).IsEqualTo(TermColor.Indexed(4));
        await Assert.That(t.GetScreenRow(0).UnderlineColorAt(0)).IsEqualTo(0u);
    }

    [Test]
    public async Task SessionExportWritesStylesColorAndOverline()
    {
        // Arrange
        var t = Feed(new Terminal(20, 2, 100), "\e[4:3;58:2::1:2:3mab\e[0;53mc");

        // Act
        var lines = t.ExportStyledLines(1);

        // Assert
        await Assert.That(lines[0]).IsEqualTo("\e[0;4:3;58:2::1:2:3mab\e[0;53mc\e[0m");
    }

    [Test]
    public async Task SessionExportRoundTripsThroughFeed()
    {
        // Arrange
        var source = Feed(new Terminal(20, 2, 100), "\e[4:4;58;5;9mdots\e[0m \e[21mdouble");
        var restored = new Terminal(20, 2, 100);

        // Act
        Feed(restored, source.ExportStyledLines(1)[0]);

        // Assert
        await Assert.That(StyleAt(restored, 0)).IsEqualTo(UnderlineStyle.Dotted);
        await Assert.That(restored.GetScreenRow(0).UnderlineColorAt(0)).IsEqualTo(TermColor.Indexed(9));
        await Assert.That(StyleAt(restored, 5)).IsEqualTo(UnderlineStyle.Double);
    }

    [Test]
    public async Task GetTextWithEscapesWritesUnderlineStyleAndColor()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2, 100), "\e[4:5;58;5;2mx");

        // Act
        string s = TextExport.WithEscapes(t, 0, 0);

        // Assert
        await Assert.That(s).IsEqualTo("\e[0;4:5;58:5:2mx\e[0m\n");
    }

    [Test]
    public async Task SaveAndRestoreCursorKeepTheUnderlineColor()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2), "\e[4;58;5;6m\e7\e[0m");

        // Act
        Feed(t, "\e8x");

        // Assert
        await Assert.That(t.GetScreenRow(0).UnderlineColorAt(0)).IsEqualTo(TermColor.Indexed(6));
    }

    // ---- Rendering geometry -----------------------------------------------------------------

    [Test]
    [Arguments(UnderlineStyle.Curly)]
    [Arguments(UnderlineStyle.Dotted)]
    [Arguments(UnderlineStyle.Dashed)]
    public async Task PatternedStylesRenderSomething(UnderlineStyle style)
    {
        // Arrange
        var (top, height) = Underlines.Strip(style, cellHeight: 24, underlineY: 20, thickness: 2, baseline: 18);
        var alpha = new byte[12 * height];

        // Act
        bool drawn = Underlines.Render(style, 12, height, 2, alpha);

        // Assert
        await Assert.That(drawn).IsTrue();
        await Assert.That(alpha.Any(a => a > 0)).IsTrue();
        await Assert.That(top + height).IsLessThanOrEqualTo(24);
    }

    [Test]
    [Arguments(UnderlineStyle.Single)]
    [Arguments(UnderlineStyle.Double)]
    public async Task RectangleStylesAreNotRenderedAsStrips(UnderlineStyle style)
    {
        // Arrange
        var alpha = new byte[12 * 2];

        // Act
        bool drawn = Underlines.Render(style, 12, 2, 2, alpha);

        // Assert
        await Assert.That(drawn).IsFalse();
    }

    [Test]
    public async Task CurlyIsTallerThanAStrokeAndJoinsAcrossCells()
    {
        // Arrange
        var (_, height) = Underlines.Strip(UnderlineStyle.Curly, cellHeight: 30, underlineY: 24, thickness: 2, baseline: 22);
        var alpha = new byte[16 * height];

        // Act
        Underlines.Render(UnderlineStyle.Curly, 16, height, 2, alpha);

        // Assert: taller than the stroke, and the first and last columns match (the wave continues in the next cell)
        await Assert.That(height).IsGreaterThanOrEqualTo(6);
        var first = Enumerable.Range(0, height).Select(y => alpha[y * 16]).ToArray();
        var last = Enumerable.Range(0, height).Select(y => alpha[y * 16 + 15]).ToArray();
        await Assert.That(first).IsEquivalentTo(last, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CurlyScalesWithHiDpiCells()
    {
        // Arrange: the same font at 1x and 2x
        var low = Underlines.Strip(UnderlineStyle.Curly, cellHeight: 20, underlineY: 17, thickness: 1, baseline: 15);

        // Act
        var high = Underlines.Strip(UnderlineStyle.Curly, cellHeight: 40, underlineY: 34, thickness: 2, baseline: 30);

        // Assert
        await Assert.That(high.Height).IsGreaterThanOrEqualTo(2 * low.Height - 1);
    }

    [Test]
    public async Task DoubleUnderlineStaysInsideTheCell()
    {
        // Arrange: the underline sits near the bottom of a short cell

        // Act
        var (first, second) = Underlines.DoubleLines(cellHeight: 16, underlineY: 14, thickness: 1);

        // Assert
        await Assert.That(second + 1).IsLessThanOrEqualTo(16);
        await Assert.That(second - first).IsEqualTo(2);
    }
}
