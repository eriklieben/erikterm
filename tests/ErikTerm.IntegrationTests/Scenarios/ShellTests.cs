namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>A shell in a pane: input through erikterm cli, the text read back, a frame, a clean exit.</summary>
public class ShellTests
{
    [Test]
    public async Task Shell_output_reaches_the_screen_and_the_window_closes_cleanly()
    {
        // Arrange
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                wait-file:go
                shot:shell.bmp
                sleep:400
                close-window
                """,
        });

        // Act
        await run.CliAsync("send-text", "--no-paste", "echo hello-$((6*7))\r");
        string text = await run.WaitForTextAsync("hello-42");
        run.Release("go");

        // Assert
        await Assert.That(text).Contains("echo hello-$((6*7))");
        await Checks.RenderedAsync(run, "shell.bmp");
        await Checks.CleanExitAsync(run);
    }
}
