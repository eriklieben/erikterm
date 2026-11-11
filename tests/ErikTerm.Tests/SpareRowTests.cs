using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>A cleared scrollback leaves its pinned rows for the next flood, so that flood doesn't pay for a second scrollback.</summary>
public class SpareRowTests
{
    private const string ClearAll = "\u001b[3J\u001b[2J\u001b[H";

    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    private static string Lines(int count, string prefix = "line")
    {
        var text = new StringBuilder();
        for (int i = 0; i < count; i++) text.Append(prefix).Append(' ').Append(i).Append("\r\n");
        return text.ToString();
    }

    private static Terminal Flooded(int lines = 30, int cols = 20, int rows = 4, int scrollback = 40) =>
        Feed(new Terminal(cols, rows, scrollback), Lines(lines));

    private static HashSet<Row> ScrollbackRows(Terminal t)
    {
        var rows = new HashSet<Row>(ReferenceEqualityComparer.Instance);
        for (long l = t.FirstLine; l < t.ScreenTopLine; l++) rows.Add(t.GetLine(l)!);
        return rows;
    }

    private static string All(Terminal t) => t.GetText(t.FirstLine, 0, t.EndLine - 1, int.MaxValue);

    private static void Compact(Terminal t)
    {
        while (t.CompactScrollback(1000)) { }
    }

    [Test]
    public async Task TheNextFloodUsesTheRowsOfAClearedScrollback()
    {
        // Arrange
        var t = Flooded();
        var before = ScrollbackRows(t);

        // Act
        Feed(t, ClearAll);
        bool spare = t.HasSpareRows;
        Feed(t, Lines(20)); // enough for reused rows to scroll off the top of the screen again

        // Assert
        await Assert.That(spare).IsTrue();
        await Assert.That(ScrollbackRows(t).Overlaps(before)).IsTrue();
    }

    [Test]
    public async Task ReusedRowsHoldNothingOfWhatTheyShowedBefore()
    {
        // Arrange: long, wrapped, colored lines, then only short ones.
        var t = Feed(new Terminal(20, 4, 40), string.Concat(Enumerable.Repeat("\u001b[41m" + new string('x', 25) + "\r\n", 20)));
        string shortLines = Lines(30, "short");

        // Act
        Feed(t, "\u001b[0m" + ClearAll + shortLines);

        // Assert: the same as a terminal that never showed the long lines.
        var fresh = Feed(new Terminal(20, 4, 40), shortLines);
        await Assert.That(All(t)).IsEqualTo(All(fresh));
        for (long l = t.FirstLine; l < t.EndLine; l++)
        {
            await Assert.That(t.GetLine(l)!.Wrapped).IsFalse();
            await Assert.That(t.GetLine(l)!.CellAt(19).Bg).IsEqualTo(fresh.GetLine(fresh.FirstLine)!.CellAt(19).Bg);
        }
    }

    [Test]
    public async Task CompactedRowsDropTheEmptySideTablesOfWhatTheyShowedBefore()
    {
        // Arrange: combining marks give every row a cluster table, and the ASCII after the clear reuses those rows.
        var t = Feed(new Terminal(20, 4, 40), string.Concat(Enumerable.Repeat("é\r\n", 30)));
        Feed(t, ClearAll + Lines(30));

        // Act
        Compact(t);

        // Assert
        for (long l = t.FirstLine; l < t.ScreenTopLine; l++)
            await Assert.That(t.GetLine(l)!.HasSideTables).IsFalse();
    }

    [Test]
    public async Task CompactedRowsKeepTheClustersTheyStillShow()
    {
        // Arrange
        var t = Feed(new Terminal(20, 4, 40), string.Concat(Enumerable.Repeat("é\r\n", 30)));

        // Act
        Compact(t);

        // Assert
        var row = t.GetLine(t.FirstLine)!;
        await Assert.That(row.HasSideTables).IsTrue();
        await Assert.That(Terminal.TryGetCluster(row, 0, out var text) ? text.ToString() : null).IsEqualTo("é");
    }

    [Test]
    public async Task ReleasingLetsGoOfTheSpareRows()
    {
        // Arrange
        var t = Flooded();
        var before = ScrollbackRows(t);
        Feed(t, ClearAll);
        long compacted = t.CompactedBytes;

        // Act
        t.ReleaseSpareRows();
        Feed(t, Lines(20));

        // Assert
        await Assert.That(t.HasSpareRows).IsFalse();
        await Assert.That(t.CompactedBytes).IsGreaterThan(compacted);
        await Assert.That(ScrollbackRows(t).Overlaps(before)).IsFalse();
    }

    [Test]
    public async Task ScreenRowsStayPinnedUntilTheSpareRowsAreGone()
    {
        // Arrange
        var t = Flooded();
        Feed(t, "\u001b[3J");

        // Act
        Compact(t); // nothing to compact, but spare rows can still scroll onto the screen
        bool pinnedWhileSpare = t.GetLine(t.ScreenTopLine)!.CellsPinned;
        t.ReleaseSpareRows();

        // Assert
        await Assert.That(pinnedWhileSpare).IsTrue();
        for (long l = t.ScreenTopLine; l < t.EndLine; l++)
            await Assert.That(t.GetLine(l)!.CellsPinned).IsFalse();
    }

    [Test]
    public async Task CompactedRowsAreNotKept()
    {
        // Arrange
        var t = Flooded();
        Compact(t);

        // Act
        Feed(t, "\u001b[3J");

        // Assert
        await Assert.That(t.HasSpareRows).IsFalse();
    }

    [Test]
    public async Task UsingAllTheSpareRowsLeavesNone()
    {
        // Arrange
        var t = Flooded();
        Feed(t, ClearAll);

        // Act: more lines than there were rows to reuse.
        Feed(t, Lines(60));

        // Assert
        await Assert.That(t.HasSpareRows).IsFalse();
    }

    [Test]
    public async Task AFullResetKeepsNoSpareRows()
    {
        // Arrange
        var t = Flooded();
        Feed(t, ClearAll);

        // Act
        Feed(t, "\u001bc");

        // Assert
        await Assert.That(t.HasSpareRows).IsFalse();
    }

    [Test]
    public async Task ChangingTheScrollbackSizeKeepsNoSpareRows()
    {
        // Arrange
        var t = Flooded();
        Feed(t, ClearAll);

        // Act
        t.ScrollbackCapacity = 10;

        // Assert
        await Assert.That(t.HasSpareRows).IsFalse();
    }

    [Test]
    public async Task ANewWidthDoesNotUseRowsOfTheOldOne()
    {
        // Arrange
        var t = Flooded();
        Feed(t, ClearAll);
        string more = Lines(30, "after");

        // Act
        t.Resize(32, 4);
        Feed(t, more);

        // Assert
        var fresh = new Terminal(20, 4, 40);
        fresh.Resize(32, 4);
        Feed(fresh, more);
        await Assert.That(All(t)).IsEqualTo(All(fresh));
        for (long l = t.ScreenTopLine; l < t.EndLine; l++)
            await Assert.That(t.GetLine(l)!.Cells.Length).IsEqualTo(32);
    }

    [Test]
    public async Task GrowingTheScreenWhileRowsAreSpareKeepsTheText()
    {
        // Arrange: a few rows reused, then the screen pulls some back out of the scrollback.
        var t = Flooded();
        string first = Lines(12, "first"), second = Lines(12, "second");
        Feed(t, ClearAll + first);

        // Act
        t.Resize(20, 8);
        Feed(t, second);

        // Assert
        var fresh = Feed(new Terminal(20, 4, 40), first);
        fresh.Resize(20, 8);
        Feed(fresh, second);
        await Assert.That(All(t)).IsEqualTo(All(fresh));
    }

    // ---- SpareRowRelease -------------------------------------------------------------------------------------------

    private static Terminal WithSpareRows()
    {
        var t = Flooded();
        Feed(t, ClearAll);
        return t;
    }

    [Test]
    public async Task TheRowsAreReleasedOnceTheOutputHasStopped()
    {
        // Arrange
        var t = WithSpareRows();
        int released = 0;
        using var release = new SpareRowRelease(t, new Lock(), 10, () => Interlocked.Increment(ref released));

        // Act
        release.NoteOutput();

        // Assert
        await Assert.That(await Eventually.Value(() => Volatile.Read(ref released), n => n == 1)).IsEqualTo(1);
        await Assert.That(t.HasSpareRows).IsFalse();
    }

    [Test]
    public async Task TheRowsStayWhileTheQuietPeriodIsRunning()
    {
        // Arrange
        var t = WithSpareRows();
        int released = 0;
        using var release = new SpareRowRelease(t, new Lock(), 600_000, () => released++);
        release.NoteOutput();

        // Act
        release.Tick();

        // Assert
        await Assert.That(released).IsEqualTo(0);
        await Assert.That(t.HasSpareRows).IsTrue();
    }

    [Test]
    public async Task TheRowsAreReleasedWhenTheQuietPeriodHasPassed()
    {
        // Arrange
        var t = WithSpareRows();
        int released = 0;
        using var release = new SpareRowRelease(t, new Lock(), 0, () => released++);

        // Act
        release.Tick();

        // Assert
        await Assert.That(released).IsEqualTo(1);
        await Assert.That(t.HasSpareRows).IsFalse();
    }

    [Test]
    public async Task ATickAfterThePaneClosedDoesNothing()
    {
        // Arrange
        var t = WithSpareRows();
        var release = new SpareRowRelease(t, new Lock(), 600_000, () => { });
        release.NoteOutput();
        release.Dispose();

        // Act
        release.Tick();

        // Assert
        await Assert.That(t.HasSpareRows).IsTrue();
    }

    [Test]
    public async Task WaitingCompactionRunsOnceOutputStops()
    {
        // Arrange: the way a Windows pane uses it, where the reader's idle step doesn't run.
        var t = Flooded();
        using var compaction = new AfterOutputStops(10, () => t.NeedsCompaction, () => Compact(t));

        // Act
        compaction.NoteOutput();

        // Assert
        await Assert.That(await Eventually.True(() => !t.NeedsCompaction)).IsTrue();
    }

    [Test]
    public async Task OutputWithoutSpareRowsNeverArmsTheTimer()
    {
        // Arrange
        var t = Flooded();
        int released = 0;
        using var release = new SpareRowRelease(t, new Lock(), 1, () => Interlocked.Increment(ref released));

        // Act
        release.NoteOutput();
        await Task.Delay(100);

        // Assert
        await Assert.That(Volatile.Read(ref released)).IsEqualTo(0);
    }
}
