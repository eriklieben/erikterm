namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// GitHub status on the repository badge, from a stand-in gh that answers like the real one: signed in, a pull
/// request with a failing check, a workflow run to watch.
/// </summary>
public class GitHubTests
{
    private const string Gh = """
        #!/bin/sh
        printf 'gh' >> "$FAKE_CALLS"; for a in "$@"; do printf ' [%s]' "$a" >> "$FAKE_CALLS"; done; printf '\n' >> "$FAKE_CALLS"
        case "$1 $2" in
          "auth status") exit 0 ;;
          "pr view") printf '%s\n' '{"number":42,"title":"Fix the parser","state":"OPEN","url":"https://github.com/example/shop/pull/42","isDraft":false,"reviewDecision":"APPROVED","statusCheckRollup":[{"__typename":"CheckRun","name":"build","status":"COMPLETED","conclusion":"FAILURE","detailsUrl":"https://github.com/example/shop/actions/runs/7"},{"__typename":"CheckRun","name":"test","status":"IN_PROGRESS","conclusion":""}]}' ;;
          "run list") printf '%s\n' '[{"status":"completed","conclusion":"failure","url":"https://github.com/example/shop/actions/runs/7","name":"build","databaseId":7}]' ;;
          "run watch") sleep 1; exit 1 ;;
          "pr list") printf '[]\n' ;;
          *) exit 1 ;;
        esac
        """;

    [Test]
    public async Task The_badge_shows_the_pull_request_and_its_checks()
    {
        // Arrange: a repository whose origin is on GitHub, on a branch with a pull request.
        string dir = AppRun.NewDirectory();
        var repo = GitRepo.Simple(Path.Combine(dir, "shop"));
        repo.Run("remote", "add", "origin", "https://github.com/example/shop.git");
        repo.Run("switch", "-q", "-c", "fix-parser");
        await using var run = AppRun.Start(new Scenario
        {
            Directory = dir,
            Cwd = repo.Path,
            Config = "git_status = true\ngithub_status = true",
            Fakes = { ["gh"] = Gh },
            Script = """
                sleep:300
                shot:early.bmp
                wait-file:fetched
                sleep:1500
                shot:pr.bmp
                ghfake:pass
                sleep:800
                shot:pass.bmp
                ghfake:fail
                sleep:600
                ghfake:pending
                sleep:600
                action:ActivateCommandPalette
                type:GitHub
                sleep:500
                shot:palette.bmp
                key:escape
                hovertab:0
                sleep:600
                shot:hover.bmp
                wait-file:done
                close-window
                """,
        });

        // Act 1: gh is asked about the branch.
        await Checks.RenderedAsync(run, "early.bmp");
        await WaitForCallAsync(run, "gh [pr] [view]");
        run.Release("fetched");

        // Assert 1: signed-in check first, then the pull request; the badge changes with the checks' state.
        await Assert.That(run.Calls.First(c => c.StartsWith("gh", StringComparison.Ordinal))).Contains("[auth] [status]");
        var pr = await Checks.RenderedAsync(run, "pr.bmp");
        var pass = await Checks.RenderedAsync(run, "pass.bmp");
        await Checks.DifferAsync(pr, pass, 0.0001, "the checks went from failing to passing");
        await Checks.RenderedAsync(run, "palette.bmp");
        await Checks.RenderedAsync(run, "hover.bmp");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    private static async Task WaitForCallAsync(AppRun run, string call, int timeoutMs = 30_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!run.Calls.Any(c => c.StartsWith(call, StringComparison.Ordinal)))
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"no {call}; calls:\n{string.Join("\n", run.Calls)}\nlog:\n{run.Output}");
            await Task.Delay(200);
        }
    }
}
