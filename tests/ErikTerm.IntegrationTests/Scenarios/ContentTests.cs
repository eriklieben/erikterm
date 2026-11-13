namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// What programs print, through the pty: styled text, colors, underlines, inline images (kitty, iTerm2, sixel) and
/// links (OSC 8 and plain URLs), with the reference scenes' content (bench/testing/scenes/content).
/// </summary>
public class ContentTests
{
    [Test]
    public async Task Text_colors_underlines_and_images_render()
    {
        // Arrange
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                wait-file:text
                shot:text.bmp
                wait-file:colors
                shot:colors.bmp
                wait-file:underlines
                shot:underlines.bmp
                wait-file:images
                sleep:800
                shot:images.bmp
                wait-file:done
                close-window
                """,
        });
        string[] files = ["text.vt", "colors.vt", "underlines.vt", "images.vt"];
        var paths = files.ToDictionary(f => f, run.WriteContent);
        var shots = new List<Bmp>();

        // Act: cat each file and wait for its heading, then the script takes the frame.
        foreach (var (file, heading) in new[]
                 {
                     ("text.vt", "erikterm reference: text rendering"), ("colors.vt", "erikterm reference: colors"),
                     ("underlines.vt", "erikterm reference: underline styles"), ("images.vt", "erikterm reference: images"),
                 })
        {
            await run.TypeAsync($"clear; cat '{Platform.ShPath(paths[file])}'");
            string text = await run.WaitForTextAsync(heading, ["--start-line", "-500"]);
            string name = Path.GetFileNameWithoutExtension(file);
            run.Release(name);
            shots.Add(await Checks.RenderedAsync(run, name + ".bmp"));
        }

        // Assert: every screen differs from the one before it, and images draw more than the text around them.
        for (int i = 1; i < shots.Count; i++)
            await Checks.DifferAsync(shots[i - 1], shots[i], 0.02, files[i]);
        await Assert.That(shots[^1].DistinctColors).IsGreaterThan(200);
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Links_highlight_on_hover_and_open_with_ctrl_click()
    {
        // Arrange: the links scene, hovered with Ctrl held, then a Ctrl+click on the OSC 8 link.
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                wait-file:links
                sleep:300
                shot:links.bmp
                mouse:24,2
                ctrl:1
                sleep:500
                shot:hyperlink.bmp
                ctrl:0
                mouse:30,3
                sleep:500
                shot:url-hover.bmp
                ctrl:1
                cellclick:24,2
                sleep:300
                cellclick:30,3
                ctrl:0
                sleep:600
                wait-file:done
                close-window
                """,
        });
        string path = run.WriteContent("links.vt");

        // Act
        await run.TypeAsync($"clear; cat '{Platform.ShPath(path)}'");
        await run.WaitForTextAsync("erikterm reference: links");
        run.Release("links");

        // Assert: hovering draws the link underline; Ctrl+click hands the URLs to the opener (a stand-in here).
        var plain = await Checks.RenderedAsync(run, "links.bmp");
        var hyper = await Checks.RenderedAsync(run, "hyperlink.bmp");
        var url = await Checks.RenderedAsync(run, "url-hover.bmp");
        await Checks.DifferAsync(plain, hyper, 0.0002, "the hovered OSC 8 link");
        await Checks.DifferAsync(hyper, url, 0.0002, "the hovered URL");
        await Wait.UntilAsync(() => run.Calls.Length >= 2, "two links opened");
        await Assert.That(run.Calls).Contains($"{Platform.Opener} [https://github.com/erikterm/erikterm]");
        await Assert.That(run.Calls.Any(c => c.StartsWith($"{Platform.Opener} [https://example.com/docs/testing", StringComparison.Ordinal))).IsTrue();
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Ctrl_click_on_a_file_location_opens_the_editor()
    {
        // Arrange: compiler-style output naming a file that exists, under the pane's directory; editor_open = "split"
        // opens $VISUAL in a split (the default prefers a Neovim running in the tab).
        await using var run = AppRun.Start(new Scenario
        {
            Config = "editor_open = \"split\"",
            Script = """
                wait-file:printed
                mouse:2,0
                ctrl:1
                sleep:400
                shot:hover.bmp
                cellclick:2,0
                ctrl:0
                sleep:1200
                wait-file:done
                close-window
                """,
        });
        string src = Path.Combine(run.Directory, "cwd", "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "parser.c"), "int main(void)\n{\n    return 0;\n}\n");

        // Act
        // OSC 7 says where the shell is (what shell integration sends), so the relative path resolves.
        await run.TypeAsync($"clear; printf '\\033]7;file://localhost%s\\033\\\\src/parser.c:3:5: error: expected one\\n' \"{Platform.ShellPwd}\"");
        await run.WaitForTextAsync("expected one");
        run.Release("printed");
        await Checks.RenderedAsync(run, "hover.bmp");
        await Wait.UntilAsync(() => run.Calls.Any(c => c.StartsWith("editor", StringComparison.Ordinal)), "the editor started");

        // Assert: $VISUAL (a stand-in) got the file and the line.
        string call = run.Calls.First(c => c.StartsWith("editor", StringComparison.Ordinal));
        await Assert.That(call).Contains("parser.c");
        await Assert.That(call).Contains("3");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Ctrl_click_on_a_file_location_opens_the_editor_without_osc7()
    {
        // Arrange: as above, but the shell never reports its directory (no shell integration) and git status is off,
        // so the link resolves against the shell's own current directory.
        await using var run = AppRun.Start(new Scenario
        {
            Config = "editor_open = \"split\"",
            Script = """
                wait-file:printed
                mouse:2,0
                ctrl:1
                sleep:400
                cellclick:2,0
                ctrl:0
                sleep:1200
                wait-file:done
                close-window
                """,
        });
        string src = Path.Combine(run.Directory, "cwd", "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "parser.c"), "int main(void)\n{\n    return 0;\n}\n");

        // Act
        await run.TypeAsync("clear; printf 'src/parser.c:3:5: error: expected one\\n'");
        await run.WaitForTextAsync("expected one");
        run.Release("printed");
        await Wait.UntilAsync(() => run.Calls.Any(c => c.StartsWith("editor", StringComparison.Ordinal)), "the editor started");

        // Assert
        string call = run.Calls.First(c => c.StartsWith("editor", StringComparison.Ordinal));
        await Assert.That(call).Contains("parser.c");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

}
