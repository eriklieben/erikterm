using System.Diagnostics;
using System.Text;
using ErikTerm.Pty;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>Runs a call that could block forever on a thread of its own, so that a hang fails one test instead of the whole run.</summary>
internal static class Bounded
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    public static Task<T> Run<T>(Func<T> call) => Task.Run(call).WaitAsync(Limit);
}

/// <summary>A real pty with a sleeping child; the tests open its terminal side themselves.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class ConsoleTerminalTests
{
    private static Pty.Pty Sleeper() => Pty.Pty.Spawn(new PtyOptions
    {
        Program = "/bin/sh",
        Arguments = ["sh", "-c", "exec sleep 30"],
    });

    private static unsafe int OpenFd(string path)
    {
        byte[] name = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* p = name) return Native.open(p, Native.O_RDWR | Native.O_NOCTTY);
    }

    private static (Pty.Pty Pty, UnixConsoleTerminal Terminal, int Fd) Open()
    {
        var pty = Sleeper();
        int fd = OpenFd(pty.SlavePath!);
        return (pty, new UnixConsoleTerminal(fd, fd), fd);
    }

    private static void Close(Pty.Pty pty, UnixConsoleTerminal terminal, int fd)
    {
        terminal.Dispose();
        pty.Dispose(); // first: a tty's close can wait for output nobody is left to read otherwise
        Task.Run(() => Native.close(fd)).Wait(TimeSpan.FromSeconds(2));
    }

    private static string ReadText(UnixConsoleTerminal terminal)
    {
        var buffer = new byte[64];
        int n = terminal.Read(buffer, 5000);
        return n > 0 ? Encoding.UTF8.GetString(buffer, 0, n) : "";
    }

    [Test]
    public async Task ReportsTheTerminalSize()
    {
        // Arrange
        var (pty, terminal, fd) = Open();
        try
        {
            pty.Resize(101, 33);

            // Act
            var size = terminal.Size();

            // Assert
            await Assert.That(terminal.IsTerminal).IsTrue();
            await Assert.That(size).IsEqualTo((101, 33));
        }
        finally { Close(pty, terminal, fd); }
    }

    [Test]
    public async Task RawModeDeliversInputWithoutANewline()
    {
        // Arrange
        var (pty, terminal, fd) = Open();
        try
        {
            terminal.EnterRawMode();
            terminal.EnterRawMode(); // a second call changes nothing

            // Act
            pty.Write("abc"u8);
            string text = await Bounded.Run(() => ReadText(terminal));

            // Assert
            await Assert.That(text).IsEqualTo("abc");
        }
        finally { Close(pty, terminal, fd); }
    }

    [Test]
    public async Task RestorePutsLineInputBack()
    {
        // Arrange
        var (pty, terminal, fd) = Open();
        try
        {
            terminal.EnterRawMode();
            terminal.Restore();
            terminal.Restore(); // idempotent
            var buffer = new byte[64];

            // Act: in line mode, half a line isn't readable yet
            pty.Write("x"u8);
            int n = await Bounded.Run(() => terminal.Read(buffer, 150));

            // Assert
            await Assert.That(n).IsEqualTo(0);
        }
        finally { Close(pty, terminal, fd); }
    }

    [Test]
    public async Task ReadTimesOutWithZero()
    {
        // Arrange
        var (pty, terminal, fd) = Open();
        try
        {
            var buffer = new byte[16];

            // Act
            int n = await Bounded.Run(() => terminal.Read(buffer, 20));

            // Assert
            await Assert.That(n).IsEqualTo(0);
        }
        finally { Close(pty, terminal, fd); }
    }

    [Test]
    public async Task WakeEndsABlockedRead()
    {
        // Arrange
        var (pty, terminal, fd) = Open();
        try
        {
            var buffer = new byte[16];
            terminal.Wake();
            var clock = Stopwatch.StartNew();

            // Act
            int n = await Bounded.Run(() => terminal.Read(buffer, 10_000));

            // Assert
            await Assert.That(n).IsEqualTo(0);
            await Assert.That(clock.ElapsedMilliseconds).IsLessThan(5000);
        }
        finally { Close(pty, terminal, fd); }
    }

    [Test]
    public async Task WriteReachesTheOtherSide()
    {
        // Arrange
        var (pty, terminal, fd) = Open();
        try
        {
            terminal.EnterRawMode();
            var buffer = new byte[64];

            // Act
            terminal.Write("hello"u8);
            int n = await Bounded.Run(() => pty.Read(buffer));

            // Assert
            await Assert.That(Encoding.UTF8.GetString(buffer, 0, n)).IsEqualTo("hello");
        }
        finally { Close(pty, terminal, fd); }
    }

    [Test]
    public async Task ReadReportsAHangUp()
    {
        // Arrange
        var (pty, terminal, fd) = Open();
        var buffer = new byte[16];
        pty.Hangup();
        await Bounded.Run(pty.WaitForExit);
        pty.Dispose();

        // Act
        int n = await Bounded.Run(() => terminal.Read(buffer, 2000));
        terminal.Dispose();
        await Bounded.Run(() => Native.close(fd));

        // Assert
        await Assert.That(n).IsEqualTo(-1);
    }

    [Test]
    public async Task IsNotATerminalWhenRedirected()
    {
        // Arrange
        int fd = OpenFd("/dev/null");

        // Act
        var terminal = new UnixConsoleTerminal(fd, fd);
        bool isTerminal = terminal.IsTerminal;
        var size = terminal.Size();
        terminal.Wake(); // nothing to wake: no self-pipe
        terminal.Dispose();
        _ = Native.close(fd);

        // Assert
        await Assert.That(isTerminal).IsFalse();
        await Assert.That(size).IsEqualTo((80, 24));
    }

    [Test]
    public async Task TryOpenExplainsWhyItCannot()
    {
        // Act: the outcome depends on how the tests were started, the contract doesn't
        using var terminal = ConsoleTerminal.TryOpen(out string? error);

        // Assert
        await Assert.That(terminal is null).IsEqualTo(error is not null);
    }

    [Test]
    public async Task NothingResizedUntilToldSo()
    {
        // Arrange
        var (pty, terminal, fd) = Open();
        try
        {
            // Act
            bool resized = terminal.TakeResized();

            // Assert
            await Assert.That(resized).IsFalse();
        }
        finally { Close(pty, terminal, fd); }
    }
}

[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class ProcessInfoTests
{
    private const int NoSuchPid = int.MaxValue - 1;

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task InvalidPidsKnowNothing(int pid)
    {
        // Assert
        await Assert.That(ProcessInfo.IsRunning(pid)).IsFalse();
        await Assert.That(ProcessInfo.CommandLine(pid)).IsNull();
        await Assert.That(ProcessInfo.ExecutablePath(pid)).IsNull();
        await Assert.That(ProcessInfo.WorkingDirectory(pid)).IsNull();
    }

    [Test]
    public async Task AGoneProcessKnowsNothing()
    {
        // Assert
        await Assert.That(ProcessInfo.IsRunning(NoSuchPid)).IsFalse();
        await Assert.That(ProcessInfo.CommandLine(NoSuchPid)).IsNull();
        await Assert.That(ProcessInfo.ExecutablePath(NoSuchPid)).IsNull();
        await Assert.That(ProcessInfo.WorkingDirectory(NoSuchPid)).IsNull();
    }

    [Test]
    public async Task DescribesThisProcess()
    {
        // Arrange
        int pid = Environment.ProcessId;

        // Act
        bool running = ProcessInfo.IsRunning(pid);
        string[]? argv = ProcessInfo.CommandLine(pid);
        string? exe = ProcessInfo.ExecutablePath(pid);
        string? cwd = ProcessInfo.WorkingDirectory(pid);

        // Assert
        await Assert.That(running).IsTrue();
        await Assert.That(argv).IsNotNull();
        await Assert.That(exe).IsNotNull();
        await Assert.That(cwd).IsNotNull();
    }

    [Test]
    public async Task ReadsTheForegroundProgramsArguments()
    {
        // Arrange
        using var pty = Pty.Pty.Spawn(new PtyOptions { Program = "/bin/sh", Arguments = ["sh", "-c", "exec sleep 30"] });
        await Eventually.Value(() => pty.ForegroundProcessName, n => n == "sleep");

        // Act
        string[]? argv = pty.ForegroundArguments();

        // Assert
        await Assert.That(argv).IsEquivalentTo(["sleep", "30"], CollectionOrdering.Matching);
    }
}

[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class PtyProcessTests
{
    private static Pty.Pty Shell(string script) => Pty.Pty.Spawn(new PtyOptions { Program = "/bin/sh", Arguments = ["sh", "-c", script] });

    [Test]
    public async Task ReadEndsWhenTheChildExitsButSomethingHoldsTheTerminal()
    {
        // Arrange: the background job ignores the hangup and keeps the terminal open after the shell has gone
        using var pty = Shell("trap '' HUP; sleep 5 & exit 0");
        var buffer = new byte[256];
        var clock = Stopwatch.StartNew();

        // Act
        await Bounded.Run(() =>
        {
            while (pty.Read(buffer) > 0) { }
            return 0;
        });

        // Assert
        await Assert.That(clock.ElapsedMilliseconds).IsLessThan(4000);
    }

    [Test]
    public async Task ExposesTheProcessAndItsExitCode()
    {
        // Arrange: macOS throws away input sent before the child has opened its side, so wait until it says it's there
        using var pty = Shell("printf ready; read x; exit 7");
        var buffer = new byte[64];
        await Bounded.Run(() => pty.Read(buffer));
        bool earlyExit = pty.TryGetExitCode(out _);

        // Act
        pty.Write("go\n"u8);
        await Bounded.Run(() =>
        {
            while (pty.Read(buffer) > 0) { } // macOS: the child can't finish exiting while its echoed output is unread
            return 0;
        });
        int code = await Bounded.Run(pty.WaitForExit);
        bool known = pty.TryGetExitCode(out int reported);

        // Assert
        await Assert.That(pty.ProcessId).IsGreaterThan(0);
        await Assert.That(pty.MasterFd).IsGreaterThanOrEqualTo(0);
        await Assert.That(earlyExit).IsFalse();
        await Assert.That(code).IsEqualTo(7);
        await Assert.That(known).IsTrue();
        await Assert.That(reported).IsEqualTo(7);
    }

    [Test]
    [RunOn(OS.Linux)] // on macOS the spawn doesn't report it
    public async Task ReportsAProgramThatCannotStart()
    {
        // Act
        Action spawn = () => Pty.Pty.Spawn(new PtyOptions { Program = "/nonexistent/erikterm-test", Arguments = ["x"] }).Dispose();

        // Assert
        await Assert.That(spawn).ThrowsException();
    }

    [Test]
    public async Task ExecReplaceReturnsTheErrorWhenItFails()
    {
        // Act
        string error = Pty.Pty.ExecReplace("/nonexistent/erikterm-test", ["x"], new Dictionary<string, string> { ["A"] = "b" });

        // Assert
        await Assert.That(error).IsNotEmpty();
    }

    [Test]
    public async Task DefaultEnvironmentHidesTheParentTerminal()
    {
        // Arrange
        Environment.SetEnvironmentVariable("WEZTERM_PANE_TEST", "1");

        // Act
        var env = Pty.Pty.DefaultEnvironment();
        Environment.SetEnvironmentVariable("WEZTERM_PANE_TEST", null);

        // Assert
        await Assert.That(env.ContainsKey("WEZTERM_PANE_TEST")).IsFalse();
    }

    [Test]
    public async Task DefaultShellCommandStartsWithTheShell()
    {
        // Act
        string[] command = Pty.Pty.DefaultShellCommand();

        // Assert
        await Assert.That(command[0]).IsEqualTo(Pty.Pty.DefaultShell());
    }
}

public class KeyChordTextTests
{
    [Test]
    public async Task ADefaultChordHasEmptyText()
    {
        // Act
        string text = default(ErikTerm.Config.KeyChord).ToString();

        // Assert
        await Assert.That(text).IsEqualTo("");
    }
}

public class IdleTickTests
{
    [Test]
    [Arguments(0L, IdleTick.ActiveMs)]
    [Arguments(IdleTick.DeepIdleAfterMs - 1, IdleTick.ActiveMs)]
    [Arguments((long)IdleTick.DeepIdleAfterMs, IdleTick.DeepIdleMs)]
    [Arguments(600_000L, IdleTick.DeepIdleMs)]
    public async Task TheLoopSleepsLongerOnceEventsHaveStopped(long sinceLastEvent, int expected)
    {
        // Act
        int ms = IdleTick.Ms(1_000_000 + sinceLastEvent, 1_000_000);

        // Assert
        await Assert.That(ms).IsEqualTo(expected);
    }
}

public class QuietCollectionTests
{
    [Test]
    public async Task WaitsForAQuietKeyboard()
    {
        // Arrange
        var collection = new QuietCollection();
        collection.NoteInput(1000);
        collection.Request();

        // Act
        bool whileTyping = collection.TryTake(1000 + QuietCollection.QuietMs - 1);
        bool afterwards = collection.TryTake(1000 + QuietCollection.QuietMs);

        // Assert
        await Assert.That(whileTyping).IsFalse();
        await Assert.That(afterwards).IsTrue();
    }

    [Test]
    public async Task RunsOncePerRequest()
    {
        // Arrange
        var collection = new QuietCollection();
        collection.Request();

        // Act
        bool first = collection.TryTake(100);
        bool second = collection.TryTake(200);

        // Assert
        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
    }

    [Test]
    public async Task DoesNothingUnasked()
    {
        // Act
        bool taken = new QuietCollection().TryTake(100_000);

        // Assert
        await Assert.That(taken).IsFalse();
    }
}

public class ImePlacementTests
{
    [Test]
    [Arguments(100f, 50f, 0f, 400f, 100f)]   // fits at the cursor
    [Arguments(380f, 50f, 0f, 400f, 350f)]   // moved left to stay in the pane
    [Arguments(380f, 500f, 10f, 400f, 10f)]  // wider than the pane: its start shows
    public async Task StaysInsideThePane(float cursor, float width, float left, float right, float expected)
    {
        // Act
        float x = ImePlacement.Left(cursor, width, left, right);

        // Assert
        await Assert.That(x).IsEqualTo(expected);
    }
}
