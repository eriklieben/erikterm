namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// Coding agents in panes (stand-ins reporting with OSC 7501, as Claude Code does): their status on the tabs, the
/// Agents overview, sending to an agent, closing an agent's pane, and recent commands from OSC 133.
/// </summary>
public class AgentTests
{
    [Test]
    public async Task The_agents_overview_lists_the_agents_and_jumps_to_one()
    {
        // Arrange: two agents in splits next to the shell, one waiting for permission.
        await using var run = AppRun.Start(new Scenario
        {
            Config = "window_close_confirmation = \"AlwaysPrompt\"",
            Script = $"""
                wait-file:agents
                {AppRun.FeedFile("agent.vt")}
                sleep:800
                shot:status.bmp
                action:AgentsOverview
                sleep:600
                shot:overview.bmp
                key:down
                key:up
                key:down
                key:enter
                sleep:500
                wait-file:jumped
                action:AgentsOverview
                sleep:400
                type:claude
                key:escape
                action:SendToAgent
                sleep:400
                key:escape
                action:ActivatePaneDirection(Right)
                action:CloseCurrentPane
                sleep:500
                shot:close-agent.bmp
                key:escape
                sleep:300
                wait-file:done
                close-window
                sleep:500
                key:enter
                """,
        });
        string a = (await run.CliAsync("split-pane", "--right", "--", "claude", "--resume")).Trim();
        string b = (await run.CliAsync("split-pane", "--bottom", "--pane-id", a, "--", "codex")).Trim();
        await run.CliAsync("wait-for-state", "--pane-id", b, "--state", "working", "--timeout", "20s");
        await run.CliAsync("activate-pane-direction", "Left");
        run.Release("agents");

        // Act / Assert 1: the overview draws over the window and lists the agents.
        var status = await Checks.RenderedAsync(run, "status.bmp");
        var overview = await Checks.RenderedAsync(run, "overview.bmp");
        await Checks.DifferAsync(status, overview, because: "the agents overview");
        string agents = await run.CliAsync("agents", "--format", "json");
        await Assert.That(agents).Contains("\"pane_id\": " + a);
        await Assert.That(agents).Contains("\"pane_id\": " + b);

        // Assert 2: Enter on an entry focuses that agent's pane.
        var panes = await run.WaitForPanesAsync(p => p.Single(x => x.Bool("is_active")).Int("pane_id") != 1);
        run.Release("jumped");

        // Assert 3: closing an agent's pane asks first (Escape keeps it).
        await Checks.RenderedAsync(run, "close-agent.bmp");
        panes = await run.PanesAsync();
        await Assert.That(panes.Length).IsEqualTo(3);
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Recent_commands_from_shell_marks_run_again_from_the_palette()
    {
        // Arrange: a command with OSC 133 marks (cmdline_url), finished; then the palette's Run again entry.
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                wait-file:ran
                action:ActivateCommandPalette
                type:echo recent-
                sleep:500
                shot:recent.bmp
                key:enter
                sleep:800
                wait-file:done
                close-window
                """,
        });
        string script = Path.Combine(run.Directory, "marks.sh");
        File.WriteAllText(script, """
            printf '\033]7;file://localhost%s\033\\' "$PWD"
            printf '\033]133;A\033\\$ \033]133;B\033\\echo recent-$((20+3))\r\n\033]133;C;cmdline_url=echo%%20recent-%%24((20%%2B3))\033\\'
            echo recent-$((20+3))
            printf '\033]133;D;0\033\\'
            """);

        // Act
        await run.TypeAsync($"sh '{script}'");
        await run.WaitForTextAsync("recent-23");
        await Task.Delay(500);
        run.Release("ran");
        await Checks.RenderedAsync(run, "recent.bmp");

        // Assert: the palette's entry puts the command line at the prompt (to check before running it); Enter runs it.
        await WaitForCountAsync(run, "echo recent-$((20+3))", 2);
        await run.TypeAsync("");
        string text = await WaitForCountAsync(run, "recent-23", 2);
        await Assert.That(text).DoesNotContain("not found");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    private static async Task<string> WaitForCountAsync(AppRun run, string needle, int count, int timeoutMs = 20_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string text = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            text = await run.CliAsync("get-text");
            int n = 0;
            for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + 1, StringComparison.Ordinal)) n++;
            if (n >= count) return text;
            await Task.Delay(200);
        }
        throw new TimeoutException($"\"{needle}\" never appeared {count} times:\n{text}");
    }
}
