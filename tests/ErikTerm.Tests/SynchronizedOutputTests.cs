using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class SynchronizedOutputTests
{
    private const string Begin = "\e[?2026h", End = "\e[?2026l";
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static Terminal New() => new(40, 5);

    [Test]
    public async Task AnUpdateIsNotVisibleUntilItEnds()
    {
        // Arrange
        var term = New();
        term.Feed(B("before"), 0);

        // Act
        term.Feed(B(Begin + "\rduring"), 10);

        // Assert
        await Assert.That(term.GetScreenText()).Contains("before");
        await Assert.That(term.HoldsSynchronizedOutput).IsTrue();
    }

    [Test]
    public async Task EndingTheUpdateShowsItWhole()
    {
        // Arrange
        var term = New();
        term.Feed(B(Begin + "one"), 0);

        // Act
        term.Feed(B("two" + End), 10);

        // Assert
        await Assert.That(term.GetScreenText()).Contains("onetwo");
        await Assert.That(term.HoldsSynchronizedOutput).IsFalse();
        await Assert.That(term.SynchronizedOutput).IsFalse();
    }

    [Test]
    public async Task TheEndSequenceMaySplitAcrossReads()
    {
        // Arrange
        var term = New();
        term.Feed(B(Begin + "text\e[?20"), 0);

        // Act
        term.Feed(B("26l"), 10);

        // Assert
        await Assert.That(term.GetScreenText()).Contains("text");
        await Assert.That(term.HoldsSynchronizedOutput).IsFalse();
    }

    [Test]
    public async Task TheBeginSequenceMaySplitAcrossReads()
    {
        // Arrange
        var term = New();
        term.Feed(B("\e[?20"), 0);

        // Act
        term.Feed(B("26h"), 10);
        term.Feed(B("held"), 20);

        // Assert
        await Assert.That(term.GetScreenText()).DoesNotContain("held");
    }

    [Test]
    public async Task OutputAfterTheEndIsNotHeld()
    {
        // Arrange
        var term = New();

        // Act
        term.Feed(B(Begin + "a" + End + "b"), 0);

        // Assert
        await Assert.That(term.GetScreenText()).Contains("ab");
        await Assert.That(term.HoldsSynchronizedOutput).IsFalse();
    }

    [Test]
    public async Task TwoUpdatesInOneReadBothApply()
    {
        // Arrange
        var term = New();

        // Act
        term.Feed(B(Begin + "a" + End + Begin + "b" + End), 0);

        // Assert
        await Assert.That(term.GetScreenText()).Contains("ab");
        await Assert.That(term.HoldsSynchronizedOutput).IsFalse();
    }

    [Test]
    public async Task ASecondUpdateAfterOneInTheSameReadIsHeld()
    {
        // Arrange
        var term = New();

        // Act
        term.Feed(B(Begin + "a" + End + Begin + "b"), 0);

        // Assert
        await Assert.That(term.GetScreenText()).Contains("a");
        await Assert.That(term.GetScreenText()).DoesNotContain("b");
    }

    [Test]
    public async Task AnUpdateThatNeverEndsIsReleasedAfterTheTimeout()
    {
        // Arrange
        var term = New();
        term.Feed(B(Begin + "stuck"), 1000);

        // Act
        bool early = term.ReleaseExpiredSynchronizedOutput(1000 + Terminal.SynchronizedOutputTimeoutMs - 1);
        bool due = term.ReleaseExpiredSynchronizedOutput(1000 + Terminal.SynchronizedOutputTimeoutMs);

        // Assert
        await Assert.That(early).IsFalse();
        await Assert.That(due).IsTrue();
        await Assert.That(term.GetScreenText()).Contains("stuck");
        await Assert.That(term.SynchronizedOutput).IsFalse();
    }

    [Test]
    public async Task NewOutputAlsoReleasesAnExpiredUpdate()
    {
        // Arrange
        var term = New();
        term.Feed(B(Begin + "old"), 1000);

        // Act
        term.Feed(B("new"), 1000 + Terminal.SynchronizedOutputTimeoutMs);

        // Assert
        await Assert.That(term.GetScreenText()).Contains("oldnew");
    }

    [Test]
    public async Task TooMuchHeldOutputIsReleased()
    {
        // Arrange
        var term = New();
        term.Feed(B(Begin), 0);
        var flood = new byte[Terminal.SynchronizedOutputMaxHeld];
        flood.AsSpan().Fill((byte)'x');

        // Act
        term.Feed(flood, 1);

        // Assert
        await Assert.That(term.HoldsSynchronizedOutput).IsFalse();
        await Assert.That(term.GetScreenText()).Contains("x");
    }

    [Test]
    public async Task HeldOutputDoesNotMarkTheTerminalDirty()
    {
        // Arrange
        var term = New();
        term.Feed(B(Begin), 0);
        term.Dirty = false;

        // Act
        term.Feed(B("held"), 1);

        // Assert
        await Assert.That(term.Dirty).IsFalse();
    }
}
