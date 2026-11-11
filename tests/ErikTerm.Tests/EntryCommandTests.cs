using ErikTerm.App;

namespace ErikTerm.Tests;

/// <summary>Program.Main's dispatch order: what one executable was started as.</summary>
public class EntryCommandTests
{
    private const string Helper = "--erikterm-pty-exec";

    [Test]
    public async Task TmuxDashCapitalVAnswersAsTmuxNotErikterm()
    {
        // Act — a pane runs `tmux -V` through the shim link; Claude Code reads the answer to pick tmux features
        var entry = EntryCommand.Classify(["-V"], invokedAsTmux: true, Helper);

        // Assert
        await Assert.That(entry).IsEqualTo(EntryKind.TmuxShim);
    }

    [Test]
    public async Task ErikTermDashCapitalVIsItsOwnVersion()
    {
        // Act
        var entry = EntryCommand.Classify(["-V"], invokedAsTmux: false, Helper);

        // Assert
        await Assert.That(entry).IsEqualTo(EntryKind.Version);
    }

    [Test]
    public async Task TmuxShimSubcommandIsTheShim()
    {
        // Act
        var entry = EntryCommand.Classify(["tmux-shim", "-V"], invokedAsTmux: false, Helper);

        // Assert
        await Assert.That(entry).IsEqualTo(EntryKind.TmuxShim);
    }

    [Test]
    public async Task SpawnHelperComesBeforeEverything()
    {
        // Act — even when the process name looks like tmux
        var entry = EntryCommand.Classify([Helper, "", "/bin/zsh"], invokedAsTmux: true, Helper);

        // Assert
        await Assert.That(entry).IsEqualTo(EntryKind.SpawnHelper);
    }

    [Test]
    [Arguments(new[] { "cli", "--version" }, "Version")]
    [Arguments(new[] { "cli", "list" }, "Cli")]
    [Arguments(new[] { "--terminfo" }, "Terminfo")]
    [Arguments(new[] { "--no-restore" }, "Window")]
    [Arguments(new string[0], "Window")]
    public async Task OtherArgumentsPickTheirCommand(string[] args, string expected)
    {
        // Act
        var entry = EntryCommand.Classify(args, invokedAsTmux: false, Helper);

        // Assert
        await Assert.That(entry.ToString()).IsEqualTo(expected);
    }
}
