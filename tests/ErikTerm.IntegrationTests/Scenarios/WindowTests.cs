namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// The window as a whole: session save and restore, configuration problems and reloads, themes, font size, full
/// screen and transparency, a borderless window's own chrome, and the close confirmation.
/// </summary>
public class WindowTests
{
    [Test]
    public async Task The_session_is_saved_on_close_and_restored_at_the_next_start()
    {
        // Arrange: a first run with two tabs, a split and a zoom, closed by the script.
        string dir = AppRun.NewDirectory();
        string work = Path.Combine(dir, "work");
        Directory.CreateDirectory(work);
        await using (var first = AppRun.Start(new Scenario
        {
            Directory = dir,
            KeepDirectory = true,
            Restore = true,
            Config = "restore_session = true",
            Script = """
                action:SplitHorizontal
                sleep:500
                action:SpawnTab
                sleep:500
                wait-file:ready
                close-window
                """,
        }))
        {
            await first.TypeAsync($"cd '{Platform.ShPath(work)}'");
            await first.WaitForPanesAsync(p => p.Length == 3 && p.Any(x => Platform.SamePath(x.Str("current_path"), work)));
            // Saved ~2 s after the last change (Session/SessionKeeper.cs), before the window closes.
            await first.WaitForFileAsync(AppRun.SessionFileOf(dir));
            await Task.Delay(2500);
            first.Release("ready");
            await Checks.CleanExitAsync(first);
        }
        string saved = AppRun.SessionFileOf(dir);
        await Assert.That(File.Exists(saved)).IsTrue();
        await Assert.That(File.ReadAllText(saved)).Contains(System.Text.Json.JsonSerializer.Serialize(work).Trim('"'));

        // Act: the second run restores it.
        await using var second = AppRun.Start(new Scenario
        {
            Directory = dir,
            Restore = true,
            Config = "restore_session = true",
            Script = "wait-file:checked\nclose-window",
        });
        var panes = await second.WaitForPanesAsync(p => p.Length == 3);

        // Assert
        await Assert.That(panes.Tabs()).IsEqualTo(2);
        await Assert.That(panes.Single(p => p.Bool("tab_active")).Int("tab_index")).IsEqualTo(1);
        await Assert.That(panes.Any(p => Platform.SamePath(p.Str("current_path"), work))).IsTrue();
        second.Release("checked");
        await Checks.CleanExitAsync(second);
    }

    [Test]
    public async Task Config_problems_reloads_and_themes_change_the_window()
    {
        // Arrange: two bad overrides give the startup toast; then the file changes and is reloaded.
        await using var run = AppRun.Start(new Scenario
        {
            Args = ["--set", "font_size=huge", "--set", "no_such_option=1"],
            Config = "color_scheme = \"Catppuccin Macchiato\"",
            Script = """
                sleep:1200
                shot:toast.bmp
                action:ActivateCommandPalette
                type:Show Config Problems
                key:enter
                sleep:500
                shot:problems.bmp
                key:escape
                sleep:300
                shot:macchiato.bmp
                wait-file:edited
                sleep:2500
                shot:reloaded.bmp
                action:ReloadConfiguration
                sleep:500
                action:BrowseThemes
                type:mocha
                sleep:400
                key:enter
                sleep:600
                shot:mocha.bmp
                vim::theme erik<CR>
                sleep:600
                key:escape
                shot:erik.bmp
                toast
                sleep:200
                wait-file:done
                close-window
                """,
        });

        // Act
        var toast = await Checks.RenderedAsync(run, "toast.bmp");
        var problems = await Checks.RenderedAsync(run, "problems.bmp");
        var macchiato = await Checks.RenderedAsync(run, "macchiato.bmp");
        string config = Path.Combine(run.Directory, "erikterm.toml");
        File.WriteAllText(config, File.ReadAllText(config).Replace("Catppuccin Macchiato", "Tokyo Night"));
        run.Release("edited");

        // Assert: problems listed, the edited file applied by itself (the config watch), themes applied.
        await Checks.DifferAsync(toast, problems, because: "the config problems list");
        var reloaded = await Checks.RenderedAsync(run, "reloaded.bmp");
        await Checks.DifferAsync(macchiato, reloaded, 0.3, "Tokyo Night after the file changed");
        var mocha = await Checks.RenderedAsync(run, "mocha.bmp");
        var erik = await Checks.RenderedAsync(run, "erik.bmp");
        await Checks.DifferAsync(reloaded, mocha, 0.3, "the theme browser applied Catppuccin Mocha");
        await Checks.DifferAsync(mocha, erik, 0.3, "vim's :theme applied erik");
        await Assert.That(run.Output).Contains("font_size");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Font_size_full_screen_and_transparency_resize_the_grid()
    {
        // Arrange
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                wait-file:start
                action:IncreaseFontSize
                action:IncreaseFontSize
                sleep:500
                shot:bigger.bmp
                wait-file:bigger
                action:DecreaseFontSize
                action:DecreaseFontSize
                action:DecreaseFontSize
                sleep:500
                wait-file:smaller
                action:ResetFontSize
                action:ToggleFullScreen
                sleep:800
                wait-file:full
                action:ToggleFullScreen
                action:ToggleTransparency
                sleep:400
                shot:transparent.bmp
                action:ToggleTransparency
                wait-file:done
                close-window
                """,
        });
        var start = (await run.PanesAsync())[0];
        run.Release("start");

        // Act / Assert: a bigger font means fewer rows and columns, a smaller one more; stty agrees with the list.
        await Checks.RenderedAsync(run, "bigger.bmp");
        var bigger = await run.WaitForPanesAsync(p => p[0].Cols() < start.Cols());
        await Assert.That(bigger[0].Rows()).IsLessThan(start.Rows());
        await run.TypeAsync("clear; stty size");
        bigger = await run.PanesAsync();
        await run.WaitForTextAsync($"{bigger[0].Rows()} {bigger[0].Cols()}");
        run.Release("bigger");

        var smaller = await run.WaitForPanesAsync(p => p[0].Cols() > start.Cols());
        await Assert.That(smaller[0].Rows()).IsGreaterThan(start.Rows());
        run.Release("smaller");

        await run.WaitForPanesAsync(p => p[0].Cols() != smaller[0].Cols());
        run.Release("full");
        await Checks.RenderedAsync(run, "transparent.bmp");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task A_borderless_translucent_window_keeps_its_alpha()
    {
        // Arrange: window_decorations = "NONE" with the window buttons on and a translucent background; the pointer
        // goes over the top edge, the buttons' corner and the tab bar.
        await using var run = AppRun.Start(new Scenario
        {
            Config = "window_decorations = \"NONE\"\nwindow_controls = \"on\"\nwindow_background_opacity = 0.85",
            Script = """
                sleep:800
                shot:plain.bmp
                move:500,3
                sleep:500
                move:980,15
                sleep:400
                shot:hover.bmp
                move:500,300
                hovertab:0
                sleep:300
                dclick:500,10
                sleep:300
                action:ToggleTransparency
                sleep:400
                shot:opaque.bmp
                wait-file:done
                close-window
                """,
        });

        // Act
        var plain = await Checks.RenderedAsync(run, "plain.bmp");
        await Checks.RenderedAsync(run, "hover.bmp");
        var opaque = await Checks.RenderedAsync(run, "opaque.bmp");

        // Assert: the background is translucent (the frame's alpha is what the compositor blends) until toggled off.
        // Only Linux's offscreen window has an alpha channel to read back (its EGL framebuffer); on macOS and Windows
        // the offscreen surface is XRGB8888 and drops it. The premultiplied color stays: the translucent background is
        // darker there, so most of the frame changes when it turns opaque.
        if (OperatingSystem.IsLinux())
        {
            await Assert.That(plain.TranslucentShare).IsGreaterThan(0.5);
            await Assert.That(opaque.TranslucentShare).IsLessThan(0.05);
        }
        else await Checks.DifferAsync(plain, opaque, 0.5, "Toggle Transparency drew the background opaque");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Closing_a_busy_pane_and_window_asks_first()
    {
        // Arrange: AlwaysPrompt, and a program running in the pane.
        await using var run = AppRun.Start(new Scenario
        {
            Config = "window_close_confirmation = \"AlwaysPrompt\"",
            Script = """
                wait-file:busy
                action:SplitHorizontal
                sleep:500
                action:ActivatePaneDirection(Left)
                action:CloseCurrentPane
                sleep:500
                shot:pane-prompt.bmp
                key:escape
                sleep:300
                wait-file:kept
                action:CloseCurrentTab
                sleep:400
                key:escape
                close-window
                sleep:500
                shot:window-prompt.bmp
                key:enter
                """,
        });
        // Windows: the shell replaces itself with sleep. A program Git for Windows' sh starts the usual way (fork, then
        // exec) isn't in the pane's process tree: MSYS2's exec starts the program as a new Windows process and the forked
        // sh exits, so the program's parent is gone. Only a top-level MSYS2 process (the pane's own sh) stays as its
        // program's parent, which erikterm's foreground lookup follows.
        await run.TypeAsync(OperatingSystem.IsWindows() ? "exec sleep 600" : "sleep 600");
        await run.WaitForPanesAsync(p => p[0].Str("foreground_process_name") == "sleep");
        run.Release("busy");

        // Act / Assert: Escape keeps the pane; Enter on the window's prompt closes everything.
        await Checks.RenderedAsync(run, "pane-prompt.bmp");
        var panes = await run.WaitForPanesAsync(p => p.Length == 2);
        await Assert.That(panes.Any(p => p.Str("foreground_process_name") == "sleep")).IsTrue();
        run.Release("kept");
        await Checks.RenderedAsync(run, "window-prompt.bmp");
        await Checks.CleanExitAsync(run);
    }
}
