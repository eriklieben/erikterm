using System.Text;
using ErikTerm.App.Notifications;
using ErikTerm.Config;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>notify_command_duration: OSC 133 command timing in the terminal, and the notification's text.</summary>
public class CommandNotifyTests
{
    private static (Terminal Term, Func<long, long> Advance) New()
    {
        long now = 1_000;
        var t = new Terminal(40, 5) { CommandClock = () => now };
        return (t, ms => now += ms);
    }

    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    // ---- The terminal -------------------------------------------------------------------------

    [Test]
    public async Task FinishedCommandRecordsItsExitStatus()
    {
        // Arrange
        var (t, advance) = New();
        Feed(t, "\e]133;A\e\\$ \e]133;B\e\\make\r\n\e]133;C;cmdline_url=make%20all\e\\");
        advance(12_345);

        // Act
        Feed(t, "\e]133;D;2\e\\");

        // Assert
        await Assert.That(t.CommandsFinished).IsEqualTo(1);
        await Assert.That(t.LastFinishedCommand.ExitCode).IsEqualTo(2);
    }

    [Test]
    public async Task FinishedCommandRecordsHowLongItRan()
    {
        // Arrange
        var (t, advance) = New();
        Feed(t, "\e]133;A\e\\$ \e]133;B\e\\sleep 12\r\n\e]133;C\e\\");
        advance(12_345);

        // Act
        Feed(t, "\e]133;D;0\e\\");

        // Assert
        await Assert.That(t.LastFinishedCommand.DurationMs).IsEqualTo(12_345);
    }

    [Test]
    public async Task FinishedCommandKeepsTheCommandLineTheShellSent()
    {
        // Arrange
        var (t, _) = New();
        Feed(t, "\e]133;A\e\\$ \e]133;B\e\\make\r\n\e]133;C;cmdline_url=make%20all\e\\");

        // Act
        Feed(t, "\e]133;D;0\e\\");
        string text = t.CommandText(t.LastFinishedCommand);

        // Assert
        await Assert.That(text).IsEqualTo("make all");
    }

    [Test]
    public async Task FinishedCommandWithoutACommandLineReadsTheTypedText()
    {
        // Arrange
        var (t, _) = New();
        Feed(t, "\e]133;A\e\\$ \e]133;B\e\\cargo build\r\n\e]133;C\e\\Compiling\r\n");

        // Act
        Feed(t, "\e]133;D;0\e\\");
        string text = t.CommandText(t.LastFinishedCommand);

        // Assert
        await Assert.That(text).IsEqualTo("cargo build");
    }

    [Test]
    public async Task EndMarkWithoutAStartedCommandFinishesNothing()
    {
        // Arrange: an empty command line — the shell reports D without C.
        var (t, _) = New();
        Feed(t, "\e]133;A\e\\$ \e]133;B\e\\\r\n");

        // Act
        Feed(t, "\e]133;D;0\e\\");

        // Assert
        await Assert.That(t.CommandsFinished).IsEqualTo(0);
    }

    // ---- When and what --------------------------------------------------------------------------

    [Test]
    [Arguments(9_999L, 10.0, false)]
    [Arguments(10_000L, 10.0, true)]
    [Arguments(600_000L, 0.0, false)] // 0 turns it off
    [Arguments(2_500L, 2.5, true)]
    public async Task OnlyCommandsThatRanLongEnoughNotify(long durationMs, double threshold, bool expected)
    {
        // Act
        bool notify = CommandNotice.ShouldNotify(durationMs, threshold);

        // Assert
        await Assert.That(notify).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0L, "0s")]
    [Arguments(12_900L, "12s")]
    [Arguments(72_000L, "1m 12s")]
    [Arguments(3_780_000L, "1h 3m")]
    [Arguments(187_200_000L, "2d 4h")]
    public async Task DurationsReadLikeAClock(long ms, string expected)
    {
        // Act
        string text = CommandNotice.Duration(ms);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task SuccessSaysHowLongItTook()
    {
        // Act
        var (title, body) = CommandNotice.Text("cargo build", 0, 72_000);

        // Assert
        await Assert.That(title).IsEqualTo("✓ cargo build finished in 1m 12s");
        await Assert.That(body).IsEqualTo("");
    }

    [Test]
    public async Task FailureSaysTheExitStatus()
    {
        // Act
        var (title, _) = CommandNotice.Text("make", 2, 15_000);

        // Assert
        await Assert.That(title).IsEqualTo("✗ make exited 2 after 15s");
    }

    [Test]
    public async Task LongCommandLineIsShortenedInTheTitleAndWholeInTheBody()
    {
        // Arrange
        string command = "docker compose run --rm builder make -j8 all test install DESTDIR=/tmp/out";

        // Act
        var (title, body) = CommandNotice.Text(command, 0, 20_000);

        // Assert
        await Assert.That(title).IsEqualTo("✓ docker compose run --rm builder make -j8 all te… finished in 20s");
        await Assert.That(body).IsEqualTo(command);
    }

    [Test]
    public async Task UnknownCommandLineReadsAsCommand()
    {
        // Act
        var (title, _) = CommandNotice.Text("", 0, 12_000);

        // Assert
        await Assert.That(title).IsEqualTo("✓ Command finished in 12s");
    }

    [Test]
    public async Task ControlCharactersInTheCommandLineAreRemoved()
    {
        // Act: a command line comes from the terminal's output, so it's cleaned like any notification text.
        var (title, _) = CommandNotice.Text("echo \e[31mhi‮", 0, 12_000);

        // Assert
        await Assert.That(title).DoesNotContain("\e");
        await Assert.That(title).DoesNotContain("‮");
    }

    // ---- Settings -----------------------------------------------------------------------------

    [Test]
    public async Task NotifyCommandDurationDefaultsToTenSeconds()
    {
        // Act
        var s = SettingsLoader.Parse("").Settings;

        // Assert
        await Assert.That(s.NotifyCommandDuration).IsEqualTo(10.0);
    }

    [Test]
    [Arguments("notify_command_duration = 0", 0.0)]
    [Arguments("notify_command_duration = 2.5", 2.5)]
    public async Task NotifyCommandDurationIsRead(string toml, double expected)
    {
        // Act
        var s = SettingsLoader.Parse(toml).Settings;

        // Assert
        await Assert.That(s.NotifyCommandDuration).IsEqualTo(expected);
    }

    [Test]
    public async Task NegativeNotifyCommandDurationIsReported()
    {
        // Act
        var result = SettingsLoader.Parse("notify_command_duration = -1");

        // Assert
        await Assert.That(result.Diagnostics).Contains(d => d.Contains("notify_command_duration", StringComparison.Ordinal));
        await Assert.That(result.Settings.NotifyCommandDuration).IsEqualTo(10.0);
    }
}
