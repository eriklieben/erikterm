using System.Text;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class FloodFramesTests
{
    [Test]
    [Arguments(40, 159, false)]
    [Arguments(40, 160, true)]
    [Arguments(2, 7, false)]
    [Arguments(2, 8, true)]
    public async Task A_flood_is_several_screens_per_frame(int rows, long scrolled, bool flood)
    {
        // Act
        bool result = FloodFrames.IsFlood(scrolled, rows);

        // Assert
        await Assert.That(result).IsEqualTo(flood);
    }

    [Test]
    public async Task Paging_a_screen_per_frame_is_not_a_flood()
    {
        // Act
        bool result = FloodFrames.IsFlood(linesScrolled: 40, rows: 40);

        // Assert
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task The_first_frame_is_never_held()
    {
        // Arrange
        var frames = new FloodFrames();

        // Act
        bool mayHold = frames.MayHold(now: 1_000_000);

        // Assert
        await Assert.That(mayHold).IsFalse();
    }

    [Test]
    public async Task A_frame_may_wait_until_the_interval_has_passed()
    {
        // Arrange
        var frames = new FloodFrames();
        frames.Drawn(1000);

        // Act
        bool within = frames.MayHold(1000 + FloodFrames.FrameMs - 1);
        bool after = frames.MayHold(1000 + FloodFrames.FrameMs);

        // Assert
        await Assert.That(within).IsTrue();
        await Assert.That(after).IsFalse();
    }

    [Test]
    public async Task A_held_frame_wakes_the_loop_when_it_is_due()
    {
        // Arrange
        var frames = new FloodFrames();
        frames.Drawn(1000);
        frames.Holding = true;

        // Act
        int soon = frames.WakeMs(1030);
        int late = frames.WakeMs(5000);

        // Assert
        await Assert.That(soon).IsEqualTo(FloodFrames.FrameMs - 30);
        await Assert.That(late).IsEqualTo(1);
    }

    [Test]
    public async Task Nothing_held_asks_for_no_wake()
    {
        // Arrange
        var frames = new FloodFrames();
        frames.Drawn(1000);

        // Act
        int wake = frames.WakeMs(1030);

        // Assert
        await Assert.That(wake).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task Holding_ends_with_the_frame_and_with_the_interval()
    {
        // Arrange
        var frames = new FloodFrames();
        frames.Drawn(1000);
        frames.Holding = true;

        // Act
        bool during = frames.IsHolding(1050);
        bool expired = frames.IsHolding(1000 + FloodFrames.FrameMs);
        frames.Drawn(1200);
        bool afterDraw = frames.IsHolding(1201);

        // Assert
        await Assert.That(during).IsTrue();
        await Assert.That(expired).IsFalse();
        await Assert.That(afterDraw).IsFalse();
    }

    [Test]
    public async Task The_terminal_counts_the_lines_it_scrolls()
    {
        // Arrange
        var term = new Terminal(10, 3);

        // Act
        term.Feed(Encoding.ASCII.GetBytes("a\r\nb\r\nc\r\nd\r\ne\r\n"));

        // Assert: the first two line feeds only move the cursor down
        await Assert.That(term.LinesScrolled).IsEqualTo(3);
    }
}
