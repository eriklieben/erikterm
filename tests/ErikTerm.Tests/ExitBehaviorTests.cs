using System.Text;
using ErikTerm.App.Panes;
using ErikTerm.App.Session;
using ErikTerm.Config;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>exit_behavior (hold or close when a pane's program exits) and Restart Pane's terminal reset.</summary>
public class ExitBehaviorTests
{
    // ---- The setting --------------------------------------------------------------------------

    [Test]
    public async Task ExitBehaviorDefaultsToClose()
    {
        // Act
        var s = SettingsLoader.Parse("").Settings;

        // Assert
        await Assert.That(s.ExitBehavior).IsEqualTo(ExitBehavior.Close);
    }

    [Test]
    [Arguments("exit_behavior = \"Hold\"", ExitBehavior.Hold)]
    [Arguments("exit_behavior = \"closeoncleanexit\"", ExitBehavior.CloseOnCleanExit)]
    [Arguments("exit_behavior = \"Close\"", ExitBehavior.Close)]
    public async Task ExitBehaviorTakesWeztermsNames(string toml, ExitBehavior expected)
    {
        // Act
        var s = SettingsLoader.Parse(toml).Settings;

        // Assert
        await Assert.That(s.ExitBehavior).IsEqualTo(expected);
    }

    [Test]
    [Arguments("exit_behavior = \"Linger\"")]
    [Arguments("exit_behavior = \"1\"")]
    public async Task UnknownExitBehaviorIsReported(string toml)
    {
        // Act
        var result = SettingsLoader.Parse(toml);

        // Assert
        await Assert.That(result.Diagnostics).Contains(d => d.Contains("CloseOnCleanExit", StringComparison.Ordinal));
    }

    // ---- Hold or close ------------------------------------------------------------------------

    [Test]
    [Arguments(ExitBehavior.Close, false, 1, false)]
    [Arguments(ExitBehavior.Hold, false, 0, true)]
    [Arguments(ExitBehavior.CloseOnCleanExit, false, 0, false)]
    [Arguments(ExitBehavior.CloseOnCleanExit, false, 3, true)]
    [Arguments(ExitBehavior.Hold, true, 1, false)] // tmux panes, tool splits, kill-pane
    public async Task ExitBehaviorDecidesWhetherAPaneStays(ExitBehavior behavior, bool mustClose, int code, bool expected)
    {
        // Act
        bool hold = ExitHold.ShouldHold(behavior, mustClose, code);

        // Assert
        await Assert.That(hold).IsEqualTo(expected);
    }

    [Test]
    public async Task CloseOnCleanExitHoldsWhenTheStatusIsUnknown()
    {
        // Act
        bool hold = ExitHold.ShouldHold(ExitBehavior.CloseOnCleanExit, mustClose: false, exitCode: null);

        // Assert
        await Assert.That(hold).IsTrue();
    }

    [Test]
    [Arguments("make", 3, "make exited with code 3")]
    [Arguments("zsh", 0, "zsh exited with code 0")]
    [Arguments("", 1, "Process exited with code 1")]
    [Arguments("sleep", null, "sleep exited")]
    public async Task ExitBarSaysHowTheProgramEnded(string program, int? code, string expected)
    {
        // Act
        string text = ExitHold.Message(program, code);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments(new[] { "/usr/bin/make", "all" }, "make")]
    [Arguments(new[] { "zsh" }, "zsh")]
    [Arguments(new[] { "C:\\Program Files\\PowerShell\\7\\pwsh.exe" }, "pwsh")]
    [Arguments(new string[0], "")]
    public async Task ExitBarNamesTheProgram(string[] argv, string expected)
    {
        // Act
        string name = ExitHold.ProgramName(argv);

        // Assert
        await Assert.That(name).IsEqualTo(expected);
    }

    [Test]
    [ExcludeOn(OS.Windows)] // No /bin/sh to wrap with: RestoreLaunch.Wrap returns null and programs aren't re-run on Windows
    public async Task RestoredProgramsWrapperIsNamedAfterTheShellItBecomes()
    {
        // Arrange: a restored nvim runs through the wrapper, which execs the shell when nvim quits.
        var argv = RestoreLaunch.Wrap(["nvim", "notes.md"], ["/bin/zsh", "-l"])!;

        // Act
        string name = ExitHold.ProgramName(argv);

        // Assert
        await Assert.That(name).IsEqualTo("zsh");
    }

    [Test]
    public async Task RestartRunsWhereTheShellLastWas()
    {
        // Arrange
        string reported = Path.GetTempPath(), launched = Environment.CurrentDirectory;

        // Act
        string? dir = ExitHold.RestartDirectory(reported, launched, null);

        // Assert
        await Assert.That(dir).IsEqualTo(reported);
    }

    [Test]
    public async Task RestartFallsBackToWhereThePaneStarted()
    {
        // Arrange
        string missing = Path.Combine(Path.GetTempPath(), "erikterm-no-such-dir-" + Guid.NewGuid().ToString("N"));
        string launched = Environment.CurrentDirectory;

        // Act
        string? dir = ExitHold.RestartDirectory(missing, launched, null);

        // Assert
        await Assert.That(dir).IsEqualTo(launched);
    }

    [Test]
    public async Task RestartOfARunningProgramAsksFirst()
    {
        // Act
        string text = CloseConfirmation.Text(CloseScope.Restart, ["nvim"]);
        string button = CloseConfirmation.ConfirmLabel(CloseScope.Restart);

        // Assert
        await Assert.That(text).IsEqualTo("Restart this pane? nvim is still running");
        await Assert.That(button).IsEqualTo("Restart");
    }

    [Test]
    public async Task RestartPaneIsAnAction()
    {
        // Act
        bool ok = ActionSpec.TryParse("RestartPane", out var action, out _);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(action.Name).IsEqualTo("RestartPane");
    }

    // ---- The terminal across a restart --------------------------------------------------------

    private static string Row(Terminal t, int y) => t.GetText(t.ScreenTopLine + y, 0, t.ScreenTopLine + y, t.Columns - 1);

    [Test]
    public async Task RestartKeepsTheOldOutput()
    {
        // Arrange
        var t = new Terminal(20, 4);
        t.Feed(Encoding.UTF8.GetBytes("build failed"));

        // Act
        t.ResetForNewProgram();

        // Assert
        await Assert.That(Row(t, 0)).IsEqualTo("build failed");
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((0, 1)); // the new program starts on a line of its own
    }

    [Test]
    public async Task RestartLeavesTheAlternateScreen()
    {
        // Arrange
        var t = new Terminal(20, 4);
        t.Feed("main\e[?1049h\e[Halt screen"u8);

        // Act
        t.ResetForNewProgram();

        // Assert
        await Assert.That(t.IsAlternateScreen).IsFalse();
        await Assert.That(Row(t, 0)).IsEqualTo("main");
    }

    [Test]
    public async Task RestartResetsTheModesTheOldProgramSet()
    {
        // Arrange
        var t = new Terminal(20, 4);
        t.Feed("\e[?2004h\e[?1000h\e[?1004h\e[>1u\e]2;vim\a\e[?25l"u8);

        // Act
        t.ResetForNewProgram();

        // Assert
        await Assert.That((t.BracketedPaste, t.MouseTracking, t.FocusReporting)).IsEqualTo((false, MouseTracking.None, false));
        await Assert.That(t.KeyboardFlags).IsEqualTo(KittyKeyboardModes.None);
        await Assert.That((t.Title, t.CursorVisible)).IsEqualTo(("erikterm", true));
    }

    [Test]
    public async Task RestartForgetsTheOldProgramsProgress()
    {
        // Arrange
        var t = new Terminal(20, 4);
        t.Feed("\e]9;4;1;30\a"u8);

        // Act
        t.ResetForNewProgram();

        // Assert
        await Assert.That(t.Progress).IsEqualTo((0, 0));
    }
}
