using System.Diagnostics;
using ErikTerm.Pty;

namespace ErikTerm.Tests;

/// <summary>The input queue between the UI (keys, pastes) / the reader thread (terminal replies) and the child.</summary>
public class PtyWriterTests
{
    /// <summary>A child that never reads: no room, ever (waits out the timeout like a pty would).</summary>
    private sealed class StalledSink : IPtySink
    {
        public int WriteSome(ReadOnlySpan<byte> data, int timeoutMs)
        {
            Thread.Sleep(Math.Clamp(timeoutMs, 1, 10));
            return 0;
        }
    }

    /// <summary>A child that reads at most <paramref name="perCall"/> bytes at a time and keeps them.</summary>
    private sealed class RecordingSink(int perCall) : IPtySink
    {
        private readonly MemoryStream _received = new();

        public byte[] Received { get { lock (_received) return _received.ToArray(); } }

        public int WriteSome(ReadOnlySpan<byte> data, int timeoutMs)
        {
            int n = Math.Min(perCall, data.Length);
            lock (_received) _received.Write(data[..n]);
            return n;
        }
    }

    /// <summary>A child that has exited.</summary>
    private sealed class GoneSink : IPtySink
    {
        public int WriteSome(ReadOnlySpan<byte> data, int timeoutMs) => -1;
    }

    [Test]
    public async Task WriteReturnsAtOnceWhenTheChildDoesNotRead()
    {
        // Arrange
        using var writer = new PtyWriter(new StalledSink());
        var paste = new byte[3 * 1024 * 1024];

        // Act
        var sw = Stopwatch.StartNew();
        bool accepted = writer.Write(paste);
        sw.Stop();

        // Assert
        await Assert.That(accepted).IsTrue();
        // Generous on purpose: the bug this guards against is blocking forever, and CI machines stall for hundreds of ms.
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5000);
        await Assert.That(writer.Pending).IsEqualTo(paste.Length);
    }

    [Test]
    public async Task InputBeyondCapacityIsDroppedWhole()
    {
        // Arrange
        using var writer = new PtyWriter(new StalledSink(), capacity: 1000);
        writer.Write(new byte[600]);

        // Act
        bool accepted = writer.Write(new byte[600]);

        // Assert
        await Assert.That(accepted).IsFalse();
        await Assert.That(writer.Pending).IsEqualTo(600);
        await Assert.That(writer.DroppedBytes).IsEqualTo(600);
    }

    [Test]
    public async Task RepliesAreCappedWhileTheChildDoesNotRead()
    {
        // Arrange
        using var writer = new PtyWriter(new StalledSink());
        var reply = "\e[?62;22c"u8.ToArray();

        // Act
        for (int i = 0; i < 20_000; i++) writer.WriteReply(reply);

        // Assert
        await Assert.That(writer.Pending).IsLessThanOrEqualTo(PtyWriter.DefaultReplyLimit);
        await Assert.That(writer.DroppedReplies).IsGreaterThan(0);
    }

    [Test]
    public async Task InputIsStillAcceptedWhileRepliesAreCapped()
    {
        // Arrange
        using var writer = new PtyWriter(new StalledSink());
        for (int i = 0; i < 20_000; i++) writer.WriteReply("\e[?62;22c"u8);

        // Act
        bool accepted = writer.Write("typed"u8);

        // Assert
        await Assert.That(accepted).IsTrue();
    }

    [Test]
    public async Task QueuedBytesArriveCompleteAndInOrder()
    {
        // Arrange
        var sink = new RecordingSink(perCall: 7);
        using var writer = new PtyWriter(sink);
        var expected = new List<byte>();
        for (int i = 0; i < 300; i++)
        {
            var chunk = Enumerable.Range(0, i % 50 + 1).Select(j => (byte)(i + j)).ToArray();
            expected.AddRange(chunk);
            writer.Write(chunk);
        }

        // Act
        bool flushed = writer.WaitUntilFlushed(TimeSpan.FromSeconds(30));

        // Assert
        await Assert.That(flushed).IsTrue();
        await Assert.That(sink.Received.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task WritesAfterTheChildIsGoneAreRefused()
    {
        // Arrange
        using var writer = new PtyWriter(new GoneSink());
        writer.Write("first"u8);
        writer.WaitUntilFlushed(TimeSpan.FromSeconds(30));

        // Act
        bool accepted = writer.Write("second"u8);

        // Assert
        await Assert.That(accepted).IsFalse();
    }

    [Test]
    public async Task CloseReturnsPromptlyWhileTheChildDoesNotRead()
    {
        // Arrange
        var writer = new PtyWriter(new StalledSink());
        writer.Write(new byte[1024 * 1024]);

        // Act
        var sw = Stopwatch.StartNew();
        writer.Close(TimeSpan.FromMilliseconds(50));
        sw.Stop();

        // Assert
        // Generous on purpose: the bug this guards against is blocking forever, and CI machines stall for hundreds of ms.
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5000);
        await Assert.That(writer.Write("late"u8)).IsFalse();
    }
}
