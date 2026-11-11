using System.Text;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>Scrollbar marks: prompts and failed commands on absolute lines, pixel buckets, the mark under a click.</summary>
public class ScrollMarksTests
{
    private static readonly IReadOnlyList<SearchMatch> NoMatches = [];

    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    /// <summary>A whole command: prompt, typed line, output, exit status.</summary>
    private static void Command(Terminal t, string cmd, int status, int outputLines = 3)
    {
        Feed(t, $"\e]133;A\e\\$ \e]133;B\e\\{cmd}\r\n\e]133;C\e\\");
        for (int i = 0; i < outputLines; i++) Feed(t, $"output {i}\r\n");
        Feed(t, $"\e]133;D;{status}\e\\");
    }

    private static List<long> PromptLines(Terminal t)
    {
        var lines = new List<long>();
        for (long l = t.FirstLine; l < t.EndLine; l++)
            if (t.GetLine(l) is { } row && (row.Marks & SemanticMarks.Prompt) != 0) lines.Add(l);
        return lines;
    }

    private static List<long> Lines(ScrollMarks m) => Enumerable.Range(0, m.Count).Select(m.LineAt).ToList();

    [Test]
    public async Task FindsThePromptsAsAbsoluteLines()
    {
        // Arrange
        var t = new Terminal(40, 5, 100);
        for (int i = 0; i < 6; i++) Command(t, $"cmd{i}", 0);
        var marks = new ScrollMarks();

        // Act
        marks.Update(t);

        // Assert
        await Assert.That(Lines(marks)).IsEquivalentTo(PromptLines(t), CollectionOrdering.Matching);
        await Assert.That(marks.Count).IsEqualTo(6);
    }

    [Test]
    public async Task LinesStayPutWhileMoreOutputArrives()
    {
        // Arrange
        var t = new Terminal(40, 5, 100);
        Command(t, "first", 0);
        var marks = new ScrollMarks();
        marks.Update(t);
        long line = marks.LineAt(0);

        // Act
        for (int i = 0; i < 20; i++) Feed(t, "more output\r\n");
        marks.Update(t);

        // Assert
        await Assert.That(marks.LineAt(0)).IsEqualTo(line);
    }

    [Test]
    public async Task AFailedCommandIsMarked()
    {
        // Arrange
        var t = new Terminal(40, 5, 100);
        Command(t, "ok", 0);
        Command(t, "bad", 2);
        var marks = new ScrollMarks();

        // Act
        marks.Update(t);

        // Assert
        await Assert.That(marks.FailedAt(0)).IsFalse();
        await Assert.That(marks.FailedAt(1)).IsTrue();
    }

    [Test]
    public async Task AFailureArrivingAfterThePromptEnteredTheHistoryIsSeen()
    {
        // Arrange
        var t = new Terminal(40, 3, 100);
        Feed(t, "\e]133;A\e\\$ \e]133;B\e\\slow\r\n\e]133;C\e\\");
        for (int i = 0; i < 10; i++) Feed(t, "line\r\n");
        var marks = new ScrollMarks();
        marks.Update(t); // the prompt row is in the history by now, and only looked at once

        // Act
        Feed(t, "\e]133;D;1\e\\");
        marks.Update(t);

        // Assert
        await Assert.That(marks.FailedAt(0)).IsTrue();
    }

    [Test]
    public async Task SeveralFailuresBetweenUpdatesAreAllSeen()
    {
        // Arrange
        var t = new Terminal(40, 3, 100);
        for (int i = 0; i < 2; i++)
        {
            Feed(t, "\e]133;A\e\\$ \e]133;B\e\\x\r\n\e]133;C\e\\");
            for (int k = 0; k < 6; k++) Feed(t, "line\r\n");
        }
        var marks = new ScrollMarks();
        marks.Update(t);

        // Act
        Feed(t, "\e]133;D;1\e\\\e]133;A\e\\$ \e]133;B\e\\y\r\n\e]133;C\e\\\e]133;D;3\e\\");
        marks.Update(t);

        // Assert
        await Assert.That(marks.FailedAt(1)).IsTrue();
        await Assert.That(marks.FailedAt(2)).IsTrue();
    }

    [Test]
    public async Task PromptsDroppedOffTheHistoryAreForgotten()
    {
        // Arrange
        var t = new Terminal(40, 4, 10);
        var marks = new ScrollMarks();
        Command(t, "old", 0);
        marks.Update(t);

        // Act
        for (int i = 0; i < 6; i++) { Command(t, $"new{i}", 0); marks.Update(t); }

        // Assert
        await Assert.That(Lines(marks)).IsEquivalentTo(PromptLines(t), CollectionOrdering.Matching);
        await Assert.That(marks.LineAt(0)).IsGreaterThanOrEqualTo(t.FirstLine);
    }

    [Test]
    public async Task ClearingTheHistoryForgetsItsPrompts()
    {
        // Arrange
        var t = new Terminal(40, 4, 100);
        for (int i = 0; i < 5; i++) Command(t, $"cmd{i}", 0);
        var marks = new ScrollMarks();
        marks.Update(t);

        // Act
        Feed(t, "\e[3J");
        marks.Update(t);

        // Assert
        await Assert.That(Lines(marks)).IsEquivalentTo(PromptLines(t), CollectionOrdering.Matching);
    }

    [Test]
    public async Task AReflowLooksAtEverythingAgain()
    {
        // Arrange
        var t = new Terminal(40, 5, 100);
        for (int i = 0; i < 4; i++) { Feed(t, new string('x', 35) + "\r\n"); Command(t, $"cmd{i}", 0); }
        var marks = new ScrollMarks();
        marks.Update(t);

        // Act
        t.Resize(20, 5);
        marks.Update(t);

        // Assert
        await Assert.That(Lines(marks)).IsEquivalentTo(PromptLines(t), CollectionOrdering.Matching);
    }

    [Test]
    public async Task ASignatureChangesWithTheList()
    {
        // Arrange
        var t = new Terminal(40, 5, 100);
        Command(t, "a", 0);
        var marks = new ScrollMarks();
        marks.Update(t);
        long before = marks.Signature;

        // Act
        Command(t, "b", 1);
        marks.Update(t);

        // Assert
        await Assert.That(marks.Signature).IsNotEqualTo(before);
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(50, 25)]
    [Arguments(99, 49)]
    public async Task ALineMapsProportionallyOntoTheTrack(long offset, int expected)
    {
        // Act
        int row = ScrollMarks.Bucket(1000 + offset, 1000, 100, 50);

        // Assert
        await Assert.That(row).IsEqualTo(expected);
    }

    [Test]
    [Arguments(999L)]
    [Arguments(1100L)]
    public async Task ALineOutsideTheRangeHasNoRow(long line)
    {
        // Act
        int row = ScrollMarks.Bucket(line, 1000, 100, 50);

        // Assert
        await Assert.That(row).IsEqualTo(-1);
    }

    [Test]
    public async Task TicksCarryPromptsAndFailuresInTheirRows()
    {
        // Arrange
        var t = new Terminal(40, 5, 100);
        Command(t, "ok", 0);
        Command(t, "bad", 1);
        var marks = new ScrollMarks();
        marks.Update(t);
        long total = t.EndLine - t.FirstLine;

        // Act
        var ticks = marks.Ticks(t.FirstLine, total, 200, NoMatches, 0, []).ToArray();

        // Assert
        await Assert.That((ScrollMarkKind)ticks[ScrollMarks.Bucket(marks.LineAt(0), t.FirstLine, total, 200)]).IsEqualTo(ScrollMarkKind.Prompt);
        await Assert.That((ScrollMarkKind)ticks[ScrollMarks.Bucket(marks.LineAt(1), t.FirstLine, total, 200)]).IsEqualTo(ScrollMarkKind.Failed);
    }

    [Test]
    public async Task TicksCarrySearchMatches()
    {
        // Arrange
        var marks = new ScrollMarks();
        var search = new List<SearchMatch> { new(1020, 0, 1020, 3), new(1090, 0, 1090, 3) };

        // Act
        var ticks = marks.Ticks(1000, 100, 100, search, 1, []).ToArray();

        // Assert
        await Assert.That(ticks[20]).IsEqualTo((byte)ScrollMarkKind.Search);
        await Assert.That(ticks[90]).IsEqualTo((byte)ScrollMarkKind.Search);
        await Assert.That(ticks.Count(b => b != 0)).IsEqualTo(2);
    }

    [Test]
    public async Task ManySearchMatchesStillFillTheRightRows()
    {
        // Arrange
        var marks = new ScrollMarks();
        var search = Enumerable.Range(0, 5000).Select(i => new SearchMatch(2000 + i, 0, 2000 + i, 1)).ToList(); // the second half of the lines

        // Act
        var ticks = marks.Ticks(0, 10_000, 100, search, 1, []).ToArray();

        // Assert
        await Assert.That(ticks[10]).IsEqualTo((byte)0);
        await Assert.That(ticks[30]).IsEqualTo((byte)ScrollMarkKind.Search);
        await Assert.That(ticks[80]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task TicksCarryVimMarks()
    {
        // Arrange
        var marks = new ScrollMarks();

        // Act
        var ticks = marks.Ticks(0, 100, 100, NoMatches, 0, [42]).ToArray();

        // Assert
        await Assert.That(ticks[42]).IsEqualTo((byte)ScrollMarkKind.Mark);
    }

    [Test]
    public async Task TheBucketsFollowTheSearch()
    {
        // Arrange
        var marks = new ScrollMarks();
        marks.Ticks(0, 100, 100, new List<SearchMatch> { new(10, 0, 10, 1) }, 1, []);

        // Act
        var ticks = marks.Ticks(0, 100, 100, new List<SearchMatch> { new(70, 0, 70, 1) }, 2, []).ToArray();

        // Assert
        await Assert.That(ticks[10]).IsEqualTo((byte)0);
        await Assert.That(ticks[70]).IsEqualTo((byte)ScrollMarkKind.Search);
    }

    [Test]
    public async Task AClickNearAFailedCommandFindsIt()
    {
        // Arrange
        var t = new Terminal(40, 5, 100);
        Command(t, "ok", 0);
        Command(t, "bad", 1);
        var marks = new ScrollMarks();
        marks.Update(t);
        long total = t.EndLine - t.FirstLine;
        marks.Ticks(t.FirstLine, total, 200, NoMatches, 0, []);
        int row = ScrollMarks.Bucket(marks.LineAt(1), t.FirstLine, total, 200);

        // Act
        bool found = marks.Find(row + 3, 4, t.FirstLine, total, NoMatches, [], out long line, out var kind);

        // Assert
        await Assert.That(found).IsTrue();
        await Assert.That(line).IsEqualTo(marks.LineAt(1));
        await Assert.That(kind).IsEqualTo(ScrollMarkKind.Failed);
    }

    [Test]
    public async Task AClickFarFromAnyTickFindsNothing()
    {
        // Arrange
        var marks = new ScrollMarks();
        var search = new List<SearchMatch> { new(10, 0, 10, 1) };
        marks.Ticks(0, 100, 100, search, 1, []);

        // Act
        bool found = marks.Find(60, 4, 0, 100, search, [], out _, out _);

        // Assert
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task AFailureWinsOverASearchMatchOnTheSameRow()
    {
        // Arrange
        var t = new Terminal(40, 5, 100);
        Command(t, "bad", 1);
        var marks = new ScrollMarks();
        marks.Update(t);
        long total = t.EndLine - t.FirstLine, line = marks.LineAt(0);
        var search = new List<SearchMatch> { new(line, 0, line, 1) };
        marks.Ticks(t.FirstLine, total, 200, search, 1, []);

        // Act
        marks.Find(ScrollMarks.Bucket(line, t.FirstLine, total, 200), 0, t.FirstLine, total, search, [], out _, out var kind);

        // Assert
        await Assert.That(kind).IsEqualTo(ScrollMarkKind.Failed);
    }

    [Test]
    [NotInParallel]
    public async Task UpdatingAndBucketingDoesNotAllocateOnceWarm()
    {
        // Arrange
        var t = new Terminal(40, 5, 1000);
        for (int i = 0; i < 30; i++) Command(t, $"cmd{i}", i % 3);
        var marks = new ScrollMarks();
        var search = new List<SearchMatch> { new(t.FirstLine + 3, 0, t.FirstLine + 3, 1) };
        void Frame(long height)
        {
            marks.Update(t);
            marks.Ticks(t.FirstLine, t.EndLine - t.FirstLine, (int)height, search, 1, []);
        }
        for (int i = 0; i < 2000; i++) Frame(120 + i % 3); // warm-up: tiers, and every array at its size

        // Act: the best of a few rounds, as a concurrent GC can shift the thread's byte count a little.
        long allocated = long.MaxValue;
        for (int round = 0; round < 5 && allocated > 0; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++) Frame(120 + i % 3);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Assert
        await Assert.That(allocated).IsEqualTo(0);
    }

    // ---- The thumb -------------------------------------------------------------------------------

    private static readonly RectF Pane = new(0, 0, 800, 400);

    [Test]
    [Arguments(0)]
    [Arguments(30)]
    [Arguments(990)]
    public async Task ATickLiesUnderTheThumbWhileItsLineIsInView(int offset)
    {
        // Arrange
        const int scrollback = 1000, rows = 30;
        long total = scrollback + rows;
        var g = ScrollbarGeometry.Of(Pane, scrollback, rows, offset, 1);
        long top = scrollback - offset;

        // Act
        int first = ScrollMarks.Bucket(top, 0, total, g.TickRows);
        int last = ScrollMarks.Bucket(top + rows - 1, 0, total, g.TickRows);

        // Assert
        await Assert.That(first).IsGreaterThanOrEqualTo((int)(g.ThumbY - g.TrackY) - 1);
        await Assert.That(last).IsLessThanOrEqualTo((int)(g.ThumbY - g.TrackY + g.ThumbH) + 1);
    }

    [Test]
    public async Task TickRowsNeverExceedTheTrack()
    {
        // Act
        var g = ScrollbarGeometry.Of(Pane, 100_000, 30, 0, 1);

        // Assert
        await Assert.That(g.TickRows).IsLessThanOrEqualTo((int)g.TrackH);
        await Assert.That(g.ThumbH).IsGreaterThanOrEqualTo(24f);
    }

    [Test]
    [Arguments(0)]
    [Arguments(250)]
    [Arguments(1000)]
    public async Task TheThumbPositionGivesBackItsOffset(int offset)
    {
        // Arrange
        var g = ScrollbarGeometry.Of(Pane, 1000, 30, offset, 1);

        // Act
        int back = g.OffsetForThumbAt(g.ThumbY, 1000);

        // Assert
        await Assert.That(back).IsEqualTo(offset);
    }
}
