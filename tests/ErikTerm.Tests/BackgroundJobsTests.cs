using ErikTerm.App.Integrations;
using ErikTerm.App.Tmux;

namespace ErikTerm.Tests;

public class BackgroundJobsTests
{
    /// <summary>Drains until <paramref name="done"/> or 5 s passed (the job runs on the thread pool).</summary>
    /// <returns>The thread of the last <see cref="BackgroundJobs.Drain"/> call.</returns>
    private static async Task<int> DrainUntil(BackgroundJobs jobs, Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        int thread = -1;
        while (!done() && DateTime.UtcNow < until)
        {
            thread = Environment.CurrentManagedThreadId;
            jobs.Drain();
            await Task.Delay(5);
        }
        return thread;
    }

    [Test]
    public async Task TheResultComesBackAtDrain()
    {
        // Arrange
        var jobs = new BackgroundJobs(() => { });
        string? got = null;
        int threadOfThen = -1;

        // Act
        jobs.Run(() => "done", r => { got = r; threadOfThen = Environment.CurrentManagedThreadId; });
        int drainThread = await DrainUntil(jobs, () => got is not null);

        // Assert
        await Assert.That(got).IsEqualTo("done");
        await Assert.That(threadOfThen).IsEqualTo(drainThread); // the continuation runs inside Drain, not on the pool
        await Assert.That(jobs.Busy).IsFalse();
    }

    [Test]
    public async Task AThrowingJobGivesNull()
    {
        // Arrange
        var jobs = new BackgroundJobs(() => { });
        bool called = false;
        string? got = "not yet";

        // Act
        jobs.Run<string>(() => throw new InvalidOperationException("boom"), r => { called = true; got = r; });
        await DrainUntil(jobs, () => called);

        // Assert
        await Assert.That(called).IsTrue();
        await Assert.That(got).IsNull();
    }

    [Test]
    public async Task WakeLoopWakesTheLoop()
    {
        // Arrange
        int wakes = 0;
        var jobs = new BackgroundJobs(() => Interlocked.Increment(ref wakes));
        bool called = false;

        // Act
        jobs.Run(() => "x", _ => called = true, wakeLoop: true);
        await DrainUntil(jobs, () => called);

        // Assert
        await Assert.That(Volatile.Read(ref wakes)).IsEqualTo(1);
    }

    [Test]
    public async Task PostedWorkWaitsForDrain()
    {
        // Arrange
        var jobs = new BackgroundJobs(() => { });
        bool ran = false;

        // Act
        jobs.Post(() => ran = true);
        bool busyBefore = jobs.Busy, ranBefore = ran;
        jobs.Drain();

        // Assert
        await Assert.That(busyBefore).IsTrue();
        await Assert.That(ranBefore).IsFalse();
        await Assert.That(ran).IsTrue();
        await Assert.That(jobs.Busy).IsFalse();
    }
}

public class TmuxShimExecutableTests
{
    [Test]
    public async Task OwnExecutableIsTheShim()
    {
        // Act
        bool shim = TmuxSession.IsShimExecutable("/usr/bin/../bin/erikterm", "/usr/bin/erikterm");

        // Assert
        await Assert.That(shim).IsTrue();
    }

    [Test]
    public async Task AnotherExecutableIsRealTmux()
    {
        // Act
        bool shim = TmuxSession.IsShimExecutable("/usr/bin/tmux", "/usr/bin/erikterm");

        // Assert
        await Assert.That(shim).IsFalse();
    }

    [Test]
    public async Task UnknownSelfIsNotTheShim()
    {
        // Act
        bool shim = TmuxSession.IsShimExecutable("/usr/bin/erikterm", null);

        // Assert
        await Assert.That(shim).IsFalse();
    }
}
