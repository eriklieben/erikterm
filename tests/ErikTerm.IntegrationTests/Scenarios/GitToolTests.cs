namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// The git tools in a pane: the merge/rebase state bar and repository badge, the conflict resolver (erikterm
/// mergetool), side-by-side compare (erikterm compare) and the local review of a worktree (Compare with main).
/// </summary>
public class GitToolTests
{
    [Test]
    public async Task A_stopped_rebase_shows_the_state_bar_and_badge()
    {
        // Arrange: start outside the repository, then cd into one stopped on a rebase conflict.
        string dir = AppRun.NewDirectory();
        var repo = GitRepo.RebaseConflict(Path.Combine(dir, "rebasing"));
        await using var run = AppRun.Start(new Scenario
        {
            Directory = dir,
            Config = "git_status = true",
            Script = """
                sleep:800
                shot:outside.bmp
                wait-file:inside
                sleep:4000
                shot:rebasing.bmp
                infobar:9
                wait-file:done
                close-window
                """,
        });

        // Act
        var outside = await Checks.RenderedAsync(run, "outside.bmp");
        await run.TypeAsync($"cd '{Platform.ShPath(repo.Path)}' && clear && git status --short");
        await run.WaitForTextAsync("a.txt");
        run.Release("inside");

        // Assert: the bar (and the branch badge) appear once git status has run.
        var inside = await Checks.RenderedAsync(run, "rebasing.bmp");
        await Checks.DifferAsync(outside, inside, 0.01, "the rebase bar and the repository badge");
        var panes = await run.PanesAsync();
        await Assert.That(Platform.SamePath(panes[0].Str("current_path"), repo.Path)).IsTrue().Because(panes[0].Str("current_path"));
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Mergetool_lists_the_conflicts_and_takes_decisions()
    {
        // Arrange: erikterm mergetool as the pane's program, in a repository stopped on a merge.
        string dir = AppRun.NewDirectory();
        var repo = GitRepo.MergeConflict(Path.Combine(dir, "mergerepo"));
        await using var run = AppRun.Start(new Scenario
        {
            Directory = dir,
            Cwd = repo.Path,
            Config = $"default_prog = {Platform.ErikTermInPane("mergetool")}\nfont_size = 9",
            Script = """
                sleep:2500
                shot:merge.bmp
                type:o
                sleep:600
                type:n
                type:t
                sleep:800
                shot:decided.bmp
                wait-file:done
                close-window
                """,
        });

        // Act
        string text = await run.WaitForTextAsync("auth.ts");

        // Assert
        await Assert.That(text).Contains("cart.ts");
        var merge = await Checks.RenderedAsync(run, "merge.bmp");
        var decided = await Checks.RenderedAsync(run, "decided.bmp");
        await Checks.DifferAsync(merge, decided, because: "the decisions mark the conflicts");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Compare_shows_two_files_side_by_side()
    {
        // Arrange
        string dir = AppRun.NewDirectory();
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        File.WriteAllText(Path.Combine(dir, "files", "a.ts"), "// cart helpers\nexport const total = (items) =>\n  items.reduce((a, i) => a + i.price, 0);\n\nexport const count = (items) =>\n  items.length;\n");
        File.WriteAllText(Path.Combine(dir, "files", "b.ts"), "// helpers for the shopping cart\nexport const total = (items) =>\n  items.reduce((a, i) => a + i.price * i.qty, 0);\n  // qty defaults to 1\n\nexport const count = (items) =>\n    items.length;\n");
        await using var run = AppRun.Start(new Scenario
        {
            Directory = dir,
            Cwd = Path.Combine(dir, "files"),
            Config = $"default_prog = {Platform.ErikTermInPane("compare", "a.ts", "b.ts")}\nfont_size = 9",
            Script = """
                sleep:2000
                shot:compare.bmp
                type:i
                sleep:1000
                shot:rules.bmp
                type:i
                key:down
                key:down
                sleep:500
                wait-file:done
                close-window
                """,
        });

        // Act
        string text = await run.WaitForTextAsync("qty");

        // Assert: both versions are on screen.
        await Assert.That(text).Contains("shopping cart");
        await Assert.That(text).Contains("cart helpers");
        var compare = await Checks.RenderedAsync(run, "compare.bmp");
        var rules = await Checks.RenderedAsync(run, "rules.bmp");
        await Checks.DifferAsync(compare, rules, because: "the noise rules panel");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Review_compares_a_worktree_with_main()
    {
        // Arrange
        string dir = AppRun.NewDirectory();
        var (_, worktree) = GitRepo.WithReviewWorktree(Path.Combine(dir, "reviewrepo"));
        await using var run = AppRun.Start(new Scenario
        {
            Directory = dir,
            Cwd = worktree,
            Config = "font_size = 9",
            Script = """
                sleep:800
                action:ActivateCommandPalette
                type:compare with main
                sleep:500
                key:enter
                sleep:3000
                shot:review.bmp
                type:]
                sleep:800
                type:c
                sleep:800
                shot:commits.bmp
                key:escape
                sleep:400
                wait-file:reviewed
                action:ActivateTab(0)
                sleep:500
                action:CompareWithMain
                sleep:1500
                type:f
                sleep:1500
                shot:finish.bmp
                key:escape
                wait-file:done
                close-window
                """,
        });

        // Act / Assert: a review tab opened next to the shell, listing the changed files.
        var panes = await run.WaitForPanesAsync(p => p.Tabs() == 2);
        string text = await run.WaitForTextAsync("cart.ts", ["--pane-id", panes.Single(p => p.Int("tab_index") == 1).Int("pane_id").ToString()]);
        await Assert.That(text).Contains("notes.txt");
        var review = await Checks.RenderedAsync(run, "review.bmp");
        var commits = await Checks.RenderedAsync(run, "commits.bmp");
        await Checks.DifferAsync(review, commits, because: "the commit list");
        run.Release("reviewed");
        await Checks.RenderedAsync(run, "finish.bmp");
        panes = await run.PanesAsync();
        await Assert.That(panes.Tabs()).IsEqualTo(2).Because("Compare with main reuses the open review tab");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }
}
