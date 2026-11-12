namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// Agent worktrees in a throwaway repository: the New Agent Worktree form, erikterm cli new-worktree, the worktree
/// switcher, Finish and Clean Up, with stand-in agents (nothing real is started).
/// </summary>
public class WorktreeTests
{
    [Test]
    public async Task The_form_makes_a_worktree_and_starts_the_agent_in_it()
    {
        // Arrange
        string dir = AppRun.NewDirectory();
        var repo = GitRepo.Simple(Path.Combine(dir, "repo"));
        await using var run = AppRun.Start(new Scenario
        {
            Directory = dir,
            Cwd = repo.Path,
            Config = "git_status = true",
            Script = """
                sleep:600
                action:NewAgentWorktree
                sleep:900
                type:Fix the login redirect loop
                key:down
                type:--model haiku
                key:up
                sleep:300
                shot:form.bmp
                key:enter
                sleep:2500
                shot:agent-tab.bmp
                wait-file:started
                exit:0
                sleep:1000
                action:NewAgentWorktree
                sleep:900
                type:second task
                ckey:enter
                sleep:1500
                key:escape
                sleep:300
                action:NewAgentWorktree
                sleep:900
                type:open in a split
                alt:enter
                sleep:2500
                shot:split.bmp
                wait-file:three
                action:ShowWorktrees
                sleep:2500
                shot:switcher.bmp
                key:down
                sleep:300
                key:f
                sleep:2000
                shot:finish.bmp
                key:escape
                sleep:400
                key:escape
                action:CleanUpWorktrees
                sleep:2500
                shot:cleanup.bmp
                key:escape
                sleep:300
                wait-file:done
                close-window
                """,
        });

        // Act / Assert: the form, then a worktree with the agent running in it.
        var form = await Checks.RenderedAsync(run, "form.bmp");
        var tab = await Checks.RenderedAsync(run, "agent-tab.bmp");
        await Checks.DifferAsync(form, tab, because: "the agent's tab replaced the form");
        var panes = await run.WaitForPanesAsync(p => p.Any(x => x.Str("current_path").Contains(".worktrees", StringComparison.Ordinal)));
        string worktrees = repo.Run("worktree", "list");
        await Assert.That(worktrees.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).IsEqualTo(2);
        await Assert.That(string.Join("\n", run.Calls)).Contains("Fix the login redirect loop");
        await Assert.That(string.Join("\n", run.Calls)).Contains("[--model] [haiku]");
        run.Release("started");

        // A background worktree (Ctrl+Enter) and one in a split (Alt+Enter): four worktrees in all.
        await Checks.RenderedAsync(run, "split.bmp");
        await Wait.UntilAsync(() => repo.Run("worktree", "list").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 4, "four worktrees");
        panes = await run.PanesAsync();
        await Assert.That(panes.Count(p => p.Str("current_path").Contains(".worktrees", StringComparison.Ordinal))).IsGreaterThanOrEqualTo(2);
        run.Release("three");

        // The switcher, the finish dialog and cleanup each draw over the window.
        var switcher = await Checks.RenderedAsync(run, "switcher.bmp");
        var finish = await Checks.RenderedAsync(run, "finish.bmp");
        var cleanup = await Checks.RenderedAsync(run, "cleanup.bmp");
        await Checks.DifferAsync(switcher, finish, because: "the finish dialog");
        await Checks.DifferAsync(finish, cleanup, because: "the cleanup list");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Cli_new_worktree_and_wait_for_state_follow_the_agent()
    {
        // Arrange
        string dir = AppRun.NewDirectory();
        var repo = GitRepo.Simple(Path.Combine(dir, "repo"));
        await using var run = AppRun.Start(new Scenario { Directory = dir, Cwd = repo.Path, Script = "wait-file:done\nclose-window" });

        // Act
        string pane = (await run.CliAsync("new-worktree", "--cwd", repo.Path, "--agent", "claude", "--prompt", "fix the parser",
            "--branch", "fix-parser", "--split")).Trim();
        string state = (await run.CliAsync("wait-for-state", "--pane-id", pane, "--state", "working,done,waiting", "--timeout", "20s")).Trim();
        await run.CliAsync("set-agent-status", "--pane-id", pane, "--state", "waiting", "--message", "needs a decision");
        string agents = await run.CliAsync("agents", "--format", "json");
        string table = await run.CliAsync("agents");
        await run.CliAsync("reload-config");

        // Assert
        await Assert.That(int.TryParse(pane, out _)).IsTrue();
        await Assert.That(state).IsEqualTo("working");
        await Assert.That(repo.Run("branch", "--list", "fix-parser")).Contains("fix-parser");
        await Assert.That(agents).Contains("needs a decision");
        await Assert.That(table).IsNotEmpty();
        await Assert.That(string.Join("\n", run.Calls)).Contains("fix the parser");
        var (code, _, _) = await run.CliRawAsync(["wait-for-state", "--pane-id", "999", "--state", "done", "--timeout", "0"]);
        await Assert.That(code).IsEqualTo(3);
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

}
