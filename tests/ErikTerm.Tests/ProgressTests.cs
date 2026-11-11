using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>OSC 9;4 progress: when a pane's report goes away, and how a tab combines its panes' reports.</summary>
public class ProgressTests
{
    [Test]
    public async Task ShellPromptClearsProgressTheProgramLeftBehind()
    {
        // Arrange
        var t = new Terminal(40, 5);
        t.Feed("\e]9;4;1;60\e\\"u8);

        // Act
        t.Feed("\r\n\e]133;A\e\\$ "u8);

        // Assert
        await Assert.That(t.Progress).IsEqualTo((0, 0));
    }

    [Test]
    public async Task ResetClearsProgress()
    {
        // Arrange
        var t = new Terminal(40, 5);
        t.Feed("\e]9;4;3;0\e\\"u8);

        // Act
        t.Feed("\ec"u8);

        // Assert
        await Assert.That(t.Progress).IsEqualTo((0, 0));
    }

    [Test]
    public async Task RemoveStateClearsProgress()
    {
        // Arrange
        var t = new Terminal(40, 5);
        t.Feed("\e]9;4;2;80\e\\"u8);

        // Act
        t.Feed("\e]9;4;0\e\\"u8);

        // Assert
        await Assert.That(t.Progress).IsEqualTo((0, 0));
    }

    [Test]
    [Arguments(1, 150, ProgressState.Normal, 100)]
    [Arguments(2, 40, ProgressState.Error, 40)]
    [Arguments(4, -5, ProgressState.Paused, 0)]
    [Arguments(0, 50, ProgressState.None, 0)]
    [Arguments(7, 50, ProgressState.None, 0)]
    public async Task ReportsComeFromTheTerminalsStateAndValue(int state, int value, ProgressState expectedState, int expectedValue)
    {
        // Act
        var r = ProgressReport.From(state, value);

        // Assert
        await Assert.That(r.State).IsEqualTo(expectedState);
        await Assert.That(r.Value).IsEqualTo(expectedValue);
    }

    private static ProgressReport Combine(params ProgressReport[] reports)
    {
        var a = new ProgressAggregate();
        foreach (var r in reports) a.Add(r);
        return a.Result;
    }

    [Test]
    public async Task NoReportsCombineToNone()
    {
        // Act
        var r = Combine(default, default);

        // Assert
        await Assert.That(r.IsActive).IsFalse();
    }

    [Test]
    public async Task DeterminatePanesCombineToTheirMean()
    {
        // Act
        var r = Combine(new(ProgressState.Normal, 20), new(ProgressState.Normal, 62), default);

        // Assert
        await Assert.That(r).IsEqualTo(new ProgressReport(ProgressState.Normal, 41));
    }

    [Test]
    public async Task ErrorWinsOverEveryOtherState()
    {
        // Act
        var r = Combine(new(ProgressState.Paused, 10), new(ProgressState.Error, 90), new(ProgressState.Normal, 50));

        // Assert
        await Assert.That(r.State).IsEqualTo(ProgressState.Error);
    }

    [Test]
    public async Task PausedWinsOverNormal()
    {
        // Act
        var r = Combine(new(ProgressState.Normal, 10), new(ProgressState.Paused, 30));

        // Assert
        await Assert.That(r.State).IsEqualTo(ProgressState.Paused);
    }

    [Test]
    public async Task DeterminateWinsOverIndeterminate()
    {
        // Act
        var r = Combine(new(ProgressState.Indeterminate, 0), new(ProgressState.Normal, 70));

        // Assert
        await Assert.That(r).IsEqualTo(new ProgressReport(ProgressState.Normal, 70));
    }

    [Test]
    public async Task OnlyIndeterminatePanesCombineToIndeterminate()
    {
        // Act
        var r = Combine(new(ProgressState.Indeterminate, 0), default);

        // Assert
        await Assert.That(r.State).IsEqualTo(ProgressState.Indeterminate);
    }
}
