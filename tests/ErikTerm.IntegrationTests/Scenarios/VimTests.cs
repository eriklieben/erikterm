namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>Vim mode over the scrollback: motions, visual modes, search, yank, marks, the cheat sheet and commands.</summary>
public class VimTests
{
    [Test]
    public async Task Vim_mode_moves_selects_searches_and_yanks()
    {
        // Arrange: the prose scene, then vim keys (Vim/WindowVim.cs vim notation).
        await using var run = AppRun.Start(new Scenario
        {
            Script = $"""
                sleep:600
                {AppRun.FeedFile("prose.vt")}
                sleep:400
                shot:plain.bmp
                vim:gg7jwwve
                sleep:400
                shot:visual.bmp
                vim:<Esc>V2j
                sleep:300
                shot:lines.bmp
                vim:<Esc><C-v>jjll
                sleep:300
                vim:y
                vim:/greeting<CR>
                sleep:300
                shot:search.bmp
                vim:nNma0$G'a%bBeEW
                vim:?vim<CR>
                vim:*#
                vim:yy
                vim:fwtd;,
                vim:H
                vim:M
                vim:L
                vim:<C-u><C-d><C-b><C-f>
                vim:vipy
                vim:viwy
                vim:g?
                sleep:400
                shot:help.bmp
                vim:q
                vim::noh<CR>
                vim::copy<CR>
                vim::q<CR>
                sleep:300
                action:ActivateCopyMode
                vim:k
                sleep:300
                action:ActivateCopyMode
                action:PasteFrom(Clipboard)
                sleep:300
                shot:after.bmp
                wait-file:done
                close-window
                """,
        });

        // Act
        var plain = await Checks.RenderedAsync(run, "plain.bmp");
        var visual = await Checks.RenderedAsync(run, "visual.bmp");
        var lines = await Checks.RenderedAsync(run, "lines.bmp");
        var search = await Checks.RenderedAsync(run, "search.bmp");
        var help = await Checks.RenderedAsync(run, "help.bmp");

        // Assert: each mode draws its own selection, pill or sheet; the pane text is untouched by vim mode.
        await Checks.DifferAsync(plain, visual, because: "a visual selection and the mode pill");
        await Checks.DifferAsync(visual, lines, because: "a line selection");
        await Checks.DifferAsync(lines, search, because: "search matches");
        await Checks.DifferAsync(search, help, 0.05, "the cheat sheet");
        await Checks.RenderedAsync(run, "after.bmp");
        string text = await run.CliAsync("get-text");
        await Assert.That(text).Contains("vim keys");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }
}
