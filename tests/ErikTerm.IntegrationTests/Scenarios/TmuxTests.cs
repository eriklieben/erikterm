namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// The tmux emulation from inside a pane (the shim on PATH, $TMUX set): the commands tools use, and Claude Code's agent
/// teams as pills.
/// </summary>
public class TmuxTests
{
    [Test]
    public async Task Tmux_commands_in_a_pane_open_and_drive_erikterm_panes()
    {
        // Arrange
        await using var run = AppRun.Start(new Scenario
        {
            Env = { ["ERIKTERM_TMUX_LOG"] = "{dir}/tmux.log" },
            Script = "wait-file:done\nclose-window",
        });
        string script = run.WriteScript("tmux.sh", """
            set -e
            echo "TMUX set: ${TMUX:+yes}"
            new=$(tmux split-window -h -P -F '#{pane_id}' -- sh)
            echo "split: $new"
            tmux send-keys -t "$new" 'echo from-the-split' Enter
            sleep 1
            tmux capture-pane -p -t "$new" | grep -c from-the-split | sed 's/^/captured: /'
            tmux list-panes -F '#{pane_id} #{pane_width}x#{pane_height}' | wc -l | sed 's/^ */panes: /'
            tmux display-message -p 'session=#{session_name} window=#{window_index}'
            tmux select-pane -t "$new" -T named
            tmux resize-pane -t "$new" -x 30
            tmux select-layout tiled
            tmux select-layout even-horizontal
            tmux new-window -d -- sh
            tmux list-windows | wc -l | sed 's/^ */windows: /'
            tmux kill-pane -t "$new"
            tmux no-such-command 2>&1 | head -1 | sed 's/^/unknown: /'
            tmux has-session && echo has-session
            echo tmux-script-done
            """);

        // Act
        await run.TypeAsync($"sh '{Platform.ShPath(script)}'");
        string text = await run.WaitForTextAsync("tmux-script-done", ["--start-line", "-200"]);

        // Assert
        await Assert.That(text).Contains("TMUX set: yes");
        await Assert.That(text).Contains("captured: 2");
        await Assert.That(text).Contains("panes: 2");
        await Assert.That(text).Contains("session=");
        await Assert.That(text).Contains("windows: 2");
        await Assert.That(text).Contains("unknown: ");
        var panes = await run.WaitForPanesAsync(p => p.Tabs() == 2 && p.Length == 2);
        await Assert.That(panes.Any(p => p.Bool("created_by_tmux"))).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(run.Directory, "tmux.log"))).Contains("split-window");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Claude_code_teammates_become_pills_that_open_and_park()
    {
        // Arrange: what Claude Code 2.1 sends for a teammate (Tmux/TmuxTeammates.cs), from its environment.
        await using var run = AppRun.Start(new Scenario
        {
            Config = "tmux_agent_panes = \"pills\"",
            Script = """
                wait-file:teammate
                sleep:500
                shot:pill.bmp
                action:ToggleAgentPane(1)
                sleep:600
                shot:open.bmp
                wait-file:opened
                action:ToggleAgentPane
                sleep:600
                action:ActivateCommandPalette
                type:teammate
                sleep:400
                shot:palette.bmp
                key:escape
                wait-file:done
                close-window
                """,
        });
        string script = run.WriteScript("team.sh", """
            export CLAUDECODE=1 AI_AGENT=claude-code_2.1.0_test CLAUDE_CODE_ENTRYPOINT=cli
            lead=$TMUX_PANE
            new=$(tmux split-window -d -t "$lead" -h -l 70% -P -F '#{pane_id}' -- cat)
            tmux select-pane -t "$new" -T alpha
            tmux set-option -p -t "$new" pane-border-style fg=blue
            tmux respawn-pane -k -t "$new" -- "sh -c 'printf \"teammate alpha\\n\"; exec sleep 600' x --agent-id alpha@team --agent-name alpha --team-name team --agent-color blue"
            echo "teammate: $new"
            """);

        // Act
        await run.TypeAsync($"sh '{Platform.ShPath(script)}'");
        await run.WaitForTextAsync("teammate: %");
        var panes = await run.WaitForPanesAsync(p => p.Any(x => x.Bool("parked")));
        run.Release("teammate");

        // Assert: parked, the lead keeps the whole tab; ToggleAgentPane opens it as a split.
        await Assert.That(panes.Count(p => !p.Bool("parked"))).IsEqualTo(1);
        var pill = await Checks.RenderedAsync(run, "pill.bmp");
        var open = await Checks.RenderedAsync(run, "open.bmp");
        await Checks.DifferAsync(pill, open, because: "the teammate opened as a split");
        panes = await run.WaitForPanesAsync(p => p.All(x => !x.Bool("parked")));
        run.Release("opened");
        await Checks.RenderedAsync(run, "palette.bmp");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }
}
