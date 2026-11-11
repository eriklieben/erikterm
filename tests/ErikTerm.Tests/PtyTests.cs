using System.Diagnostics;
using System.Text;
using ErikTerm.Core;
using ErikTerm.Pty;

namespace ErikTerm.Tests;

/// <summary>Drives /bin/sh through a Unix98 pty: Linux and macOS (skipped on Windows).</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class PtyTests
{
    private static Pty.Pty Shell(string script, int columns = 80, int rows = 24) => Pty.Pty.Spawn(new PtyOptions
    {
        Program = "/bin/sh",
        Arguments = ["sh", "-c", script],
        Columns = columns,
        Rows = rows,
    });

    private static string RunToEnd(Pty.Pty pty)
    {
        var output = new StringBuilder();
        var buf = new byte[4096];
        int n;
        while ((n = pty.Read(buf)) > 0) output.Append(Encoding.UTF8.GetString(buf, 0, n));
        return output.ToString();
    }

    // Long enough to outlive any polling deadline; disposing the pty hangs it up (SIGHUP) when the test is done.
    private const string Sleeper = "exec sleep 60";

    /// <summary>Polls until the shell has exec'd sleep — some time after the spawn, longer on a loaded machine.</summary>
    private static Task<string?> ForegroundOnceSleeping(Pty.Pty pty) =>
        Eventually.Value(() => pty.ForegroundProcessName, n => n == "sleep");

    /// <summary>A pty whose child is already running sleep.</summary>
    private static async Task<Pty.Pty> SleepingChild()
    {
        var pty = Shell(Sleeper);
        await ForegroundOnceSleeping(pty);
        return pty;
    }

    [Test]
    public async Task ChildOwnsTheTerminalAsControllingTty()
    {
        // Arrange
        using var pty = Shell("tty; ps -o stat= -p $$");

        // Act
        var lines = RunToEnd(pty).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        int exitCode = pty.WaitForExit();

        // Assert
        // 's' = session leader, '+' = in the tty's foreground process group: job control will work.
        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(lines[0]).IsEqualTo(pty.SlavePath);
        await Assert.That(lines[1]).Contains('s');
        await Assert.That(lines[1]).Contains('+');
    }

    /// <summary>Reads everything, calling <paramref name="idle"/> whenever no output is waiting.</summary>
    private static string RunToEnd(Pty.Pty pty, Func<bool> idle)
    {
        var output = new StringBuilder();
        var buf = new byte[4096];
        int n;
        while ((n = pty.Read(buf, idle)) > 0) output.Append(Encoding.UTF8.GetString(buf, 0, n));
        return output.ToString();
    }

    [Test]
    public async Task IdleWorkRunsWhileTheChildIsQuiet()
    {
        // Arrange
        using var pty = Shell("printf a; sleep 0.3; printf b");
        int calls = 0;

        // Act
        string output = RunToEnd(pty, () => { calls++; return false; });

        // Assert
        await Assert.That(output).IsEqualTo("ab");
        await Assert.That(calls).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task IdleWorkWaitsUntilTheChildHasBeenQuiet()
    {
        // Arrange
        using var pty = Shell("printf a; sleep 0.3; printf b");
        var buf = new byte[64];
        var clock = Stopwatch.StartNew();

        // Act
        long outputAt = -1, idleAt = -1;
        int n;
        while ((n = pty.Read(buf, () => { if (outputAt >= 0 && idleAt < 0) idleAt = clock.ElapsedMilliseconds; return false; })) > 0)
            if (outputAt < 0) outputAt = clock.ElapsedMilliseconds;

        // Assert
        await Assert.That(idleAt).IsGreaterThanOrEqualTo(0L);
        await Assert.That(idleAt - outputAt).IsGreaterThanOrEqualTo((long)Pty.Pty.IdleAfterMs - 5); // clock granularity
    }

    [Test]
    public async Task OutputIsReadBetweenIdleSteps()
    {
        // Arrange
        // Idle work that never runs out (a step a millisecond): output must still come through.
        using var pty = Shell("printf a; sleep 0.2; printf b");
        int steps = 0;

        // Act
        string output = RunToEnd(pty, () => { steps++; Thread.Sleep(1); return true; });

        // Assert
        await Assert.That(output).IsEqualTo("ab");
        await Assert.That(steps).IsGreaterThan(1);
    }

    [Test]
    public async Task ReportsWindowSizeAndTerm()
    {
        // Arrange
        using var pty = Shell("stty size; echo $TERM", columns: 132, rows: 43);

        // Act
        string output = RunToEnd(pty);

        // Assert
        await Assert.That(output).IsEqualTo("43 132\r\nxterm-256color\r\n");
    }

    [Test]
    public async Task ResizeIsVisibleToChild()
    {
        // Arrange
        using var pty = Shell("read x; stty size");

        // Act
        pty.Resize(100, 30);
        pty.Write("go\r"u8);
        string output = RunToEnd(pty);

        // Assert
        await Assert.That(output).EndsWith("30 100\r\n");
    }

    [Test]
    public async Task ExitCodeIsPropagated()
    {
        // Arrange
        using var pty = Shell("exit 7");

        // Act
        RunToEnd(pty);
        int exitCode = pty.WaitForExit();

        // Assert
        await Assert.That(exitCode).IsEqualTo(7);
    }

    [Test]
    public async Task OutputDrivesTerminal()
    {
        // Arrange
        using var pty = Shell("printf '\\033[2;3Hhi\\033[1;31m!'");
        var term = new Terminal(20, 5);
        var buf = new byte[4096];

        // Act
        int n;
        while ((n = pty.Read(buf)) > 0) term.Feed(buf.AsSpan(0, n));

        // Assert
        await Assert.That(term.GetLineText(1)).IsEqualTo("  hi!");
        await Assert.That(term.CellAt(4, 1).Fg).IsEqualTo(TermColor.Indexed(1));
    }

    [Test]
    public async Task ReportsForegroundProcessName()
    {
        // Arrange
        using var pty = Shell(Sleeper);

        // Act
        string? foreground = await ForegroundOnceSleeping(pty);

        // Assert
        await Assert.That(foreground).IsEqualTo("sleep");
    }

    [Test]
    public async Task HangupEndsTheChildWithSighup()
    {
        // Arrange
        using var pty = await SleepingChild();

        // Act
        pty.Hangup();
        int exitCode = pty.WaitForExit();

        // Assert
        await Assert.That(exitCode).IsEqualTo(129); // 128 + SIGHUP
    }

    [Test]
    public async Task ForegroundProcessNameIsNullAfterDispose()
    {
        // Arrange
        var pty = await SleepingChild();
        pty.Hangup();
        pty.WaitForExit();

        // Act
        pty.Dispose();

        // Assert
        await Assert.That(pty.ForegroundProcessName).IsNull();
    }

    [Test]
    [Arguments(0, 1000)]
    [Arguments(4, 1000)]
    [Arguments(5, 5000)]
    [Arguments(10_000, 5000)]
    public async Task AQuietChildIsCheckedOnLessOften(int checks, int expectedMs)
    {
        // Act
        int ms = UnixPty.ChildCheckDelay(checks);

        // Assert
        await Assert.That(ms).IsEqualTo(expectedMs);
    }

    [Test]
    public async Task DefaultShellIsNotEmpty()
    {
        // Act
        string shell = Pty.Pty.DefaultShell();

        // Assert
        await Assert.That(string.IsNullOrEmpty(shell)).IsFalse();
    }

    [Test]
    [Arguments("TERM", "xterm-256color")]
    [Arguments("TERM_PROGRAM", "erikterm")]
    [Arguments("COLORTERM", "truecolor")]
    public async Task DefaultEnvironmentDescribesTheTerminal(string variable, string expected)
    {
        // Act
        var env = Pty.Pty.DefaultEnvironment();

        // Assert
        await Assert.That(env[variable]).IsEqualTo(expected);
    }

    // ---- Input backpressure: a child that doesn't read its input -----------------------------------------------

    /// <summary>Reads until <paramref name="marker"/> shows up in the output (feeding <paramref name="term"/> if given). False on timeout.</summary>
    private static Task<bool> ReadUntil(Pty.Pty pty, string marker, Terminal? term = null) => Task.Run(() =>
    {
        var seen = new StringBuilder();
        var buf = new byte[64 * 1024];
        int n;
        while ((n = pty.Read(buf)) > 0)
        {
            term?.Feed(buf.AsSpan(0, n));
            seen.Append(Encoding.Latin1.GetString(buf, 0, n));
            if (seen.ToString().Contains(marker, StringComparison.Ordinal)) return true;
            if (seen.Length > 1 << 20) seen.Remove(0, seen.Length - marker.Length);
        }
        return false;
    });

    [Test]
    public async Task LargeWriteToANonReadingChildReturnsAtOnce()
    {
        // Arrange
        using var pty = await SleepingChild(); // sleep never reads its input
        var paste = new byte[3 * 1024 * 1024];
        paste.AsSpan().Fill((byte)'x');

        // Act
        var sw = Stopwatch.StartNew();
        bool accepted = pty.Write(paste);
        sw.Stop();

        // Assert
        await Assert.That(accepted).IsTrue();
        // Generous on purpose: the bug this guards against is blocking forever, and CI machines stall for hundreds of ms.
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5000);
        await Assert.That(pty.Input.Pending).IsGreaterThan(0); // the kernel took a little; the rest waits in the queue
    }

    [Test]
    public async Task DisposeIsPromptWhileANonReadingChildHasInputQueued()
    {
        // Arrange
        var pty = await SleepingChild();
        pty.Write(new byte[1024 * 1024]);

        // Act
        var sw = Stopwatch.StartNew();
        pty.Hangup();
        pty.Dispose();
        sw.Stop();

        // Assert
        // Generous on purpose: the bug this guards against is blocking forever, and CI machines stall for hundreds of ms.
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(10_000);
    }

    [Test]
    public async Task QueryFloodFromANonReadingChildDoesNotDeadlock()
    {
        // Arrange: a raw-mode program sends 40 000 DA queries, then sleeps without reading a single answer.
        using var pty = Shell(FloodScript);
        var term = new Terminal(80, 24);
        term.Response = data => pty.WriteReply(data);

        // Act
        var reading = ReadUntil(pty, "FLOODDONE", term);
        bool finished = await reading.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert: the reader kept up (no deadlock) and the queued answers stayed bounded.
        await Assert.That(finished).IsTrue();
        await Assert.That(pty.Input.Pending).IsLessThanOrEqualTo(PtyWriter.DefaultReplyLimit);
        await Assert.That(pty.Input.DroppedReplies).IsGreaterThan(0);
    }

    // printf repeats its format for each argument: 40 000 times ESC [ c. The marker is split so the echo can't match.
    private const string FloodScript = "stty raw -echo; printf '\\033[c%.0s' $(seq 40000); printf 'FLOOD''DONE'; exec sleep 60";

    [Test]
    public async Task QueuedInputReachesAReadingChildComplete()
    {
        // Arrange: raw mode first (a canonical-mode line holds 4 KB at most), then count every byte.
        using var pty = Shell("stty raw -echo; printf 'RE''ADY'; head -c 300000 | wc -c");
        await ReadUntil(pty, "READY").WaitAsync(TimeSpan.FromSeconds(30));
        var data = new byte[300_000];
        data.AsSpan().Fill((byte)'a');

        // Act
        pty.Write(data);
        string output = RunToEnd(pty);

        // Assert
        await Assert.That(output.Trim()).IsEqualTo("300000");
    }
}
