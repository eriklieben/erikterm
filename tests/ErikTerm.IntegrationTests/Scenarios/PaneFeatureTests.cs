namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// What a pane does with its program: notifications (bar, panel, desktop), progress, long commands, a program that
/// exits (Hold, Restart), the OSC 52 prompt, drag and drop, mouse selection with copy and paste, shell prompts
/// (OSC 133) with the scrollbar, and the internal-error handling.
/// </summary>
public class PaneFeatureTests
{
    [Test]
    public async Task Notifications_reach_the_bar_the_panel_and_the_desktop()
    {
        // Arrange: the window counts as unfocused, so a pane's notification is shown, not read at once. Unfocused before
        // the first shot: the test types the printf as soon as plain.bmp exists, and a step after the shot ran a step
        // interval later, so on a slow runner the notification could arrive while the window still had focus (then
        // erikterm rightly raises no desktop notification).
        await using var run = AppRun.Start(new Scenario
        {
            Config = "os_notifications = \"on\"\nnotify_command_duration = 1",
            Script = """
                sleep:600
                unfocus
                shot:plain.bmp
                wait-file:sent
                sleep:800
                shot:bar.bmp
                action:SpawnTab
                sleep:400
                unfocus
                feed:\e]777;notify;Tests failed;3 failures in the parser\a
                sleep:300
                action:ActivateTab(0)
                sleep:500
                action:NotificationsPanel
                sleep:500
                shot:panel.bmp
                key:down
                key:escape
                infobar:0
                sleep:300
                unfocus
                feed:\e]133;A\e\\$ \e]133;B\e\\make test\r\n\e]133;C;cmdline_url=make%20test\e\\
                sleep:1300
                feed:\e]133;D;0\e\\
                sleep:700
                shot:finished.bmp
                action:ClearNotifications
                action:ToggleDesktopNotifications
                action:ToggleDesktopNotifications
                wait-file:done
                close-window
                """,
        });

        // Act: OSC 777 and OSC 9 from the shell, as a program would send them.
        var plain = await Checks.RenderedAsync(run, "plain.bmp");
        await run.TypeAsync(@"printf '\033]777;notify;Build finished;All tests passed\a\033]9;Plain OSC 9 message\a\033]9;4;1;65\a'");
        await run.WaitForTextAsync("printf");
        run.Release("sent");

        // Assert: the bar, the panel, and the desktop notification handed to notify-send (a stand-in).
        var bar = await Checks.RenderedAsync(run, "bar.bmp");
        await Checks.DifferAsync(plain, bar, because: "the notification bar and progress");
        var panel = await Checks.RenderedAsync(run, "panel.bmp");
        await Checks.DifferAsync(bar, panel, because: "the notifications panel");
        await Checks.RenderedAsync(run, "finished.bmp");
        // Windows has no notify-send: erikterm raises none there (Host/ProcessNotifications.cs).
        if (!OperatingSystem.IsWindows())
            await Assert.That(run.Calls.Any(c => c.StartsWith("notify-send", StringComparison.Ordinal) && c.Contains("Build finished"))).IsTrue()
                .Because(string.Join("\n", run.Calls));
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task A_held_pane_shows_its_exit_and_restarts()
    {
        // Arrange: exit_behavior = "Hold": the pane stays with its status.
        await using var run = AppRun.Start(new Scenario
        {
            Config = "exit_behavior = \"Hold\"",
            Script = """
                sleep:600
                shot:running.bmp
                exit:3
                sleep:900
                shot:held.bmp
                wait-file:held
                action:RestartPane
                sleep:800
                wait-file:restarted
                action:SplitHorizontal
                sleep:500
                exit:0
                sleep:800
                key:enter
                sleep:500
                wait-file:done
                close-window
                """,
        });

        // Act / Assert: the held pane shows how its program ended (a banner with Restart and Close).
        var running = await Checks.RenderedAsync(run, "running.bmp");
        var held = await Checks.RenderedAsync(run, "held.bmp");
        await Checks.DifferAsync(running, held, because: "the exit banner");
        var before = await run.PanesAsync();
        run.Release("held");

        // RestartPane: a new shell in the same pane.
        await Task.Delay(1500);
        await run.TypeAsync("echo restarted-$((40+2))");
        await run.WaitForTextAsync("restarted-42");
        var after = await run.PanesAsync();
        await Assert.That(after[0].Int("pid")).IsNotEqualTo(before[0].Int("pid"));
        run.Release("restarted");
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Osc52_asks_and_drop_and_selection_paste_into_the_shell()
    {
        // Arrange
        string dropped = Path.Combine(AppRun.NewDirectory(), "a dropped file.txt");
        File.WriteAllText(dropped, "x");
        await using var run = AppRun.Start(new Scenario
        {
            Config = "osc52 = \"ask\"",
            Script = $"""
                wait-file:osc52
                sleep:600
                shot:osc52.bmp
                infobar:0
                sleep:300
                action:PasteFrom(Clipboard)
                sleep:300
                wait-file:pasted
                dragover:5,5
                sleep:200
                shot:dragover.bmp
                drop:5,5:{dropped}
                sleep:400
                droptext:5,5:dropped-text
                sleep:300
                dragover:3,3
                dragleave
                wait-file:dropped
                select
                action:CopyTo(Clipboard)
                down:30,40
                drag:200,60
                up:200,60
                dclick:60,45
                sleep:300
                action:CopyTo(ClipboardAndPrimarySelection)
                mclick:60,45
                sleep:300
                wait-file:done
                close-window
                """,
        });

        // Act: a program asks to set the clipboard; allowing it lets PasteFrom paste it back.
        await run.TypeAsync(@"printf '\033]52;c;%s\a' $(printf 'from-osc52' | base64)");
        await run.WaitForTextAsync("printf");
        run.Release("osc52");
        await Checks.RenderedAsync(run, "osc52.bmp");
        string text = await run.WaitForTextAsync("from-osc52");
        run.Release("pasted");

        // Dropped files arrive quoted; dropped text as it is.
        await Checks.RenderedAsync(run, "dragover.bmp");
        text = await run.WaitForTextAsync("dropped-text");
        await Assert.That(text).Contains("a dropped file.txt");
        run.Release("dropped");
        run.Release("done");
        await Checks.CleanExitAsync(run);
        Directory.Delete(Path.GetDirectoryName(dropped)!, true);
    }

    [Test]
    public async Task Shell_prompts_mark_the_scrollbar_and_jump_between_commands()
    {
        // Arrange: OSC 133 prompts and a failing command, then the prompt and output actions.
        await using var run = AppRun.Start(new Scenario
        {
            Script = $"""
                sleep:600
                {AppRun.FeedFile("marks.vt")}
                sleep:400
                scroll:40
                sleep:400
                shot:marks.bmp
                action:ScrollToPrompt(-1)
                action:ScrollToPrompt(1)
                action:SelectCommandOutput
                action:CopyCommandOutput
                action:ScrollByPage(-1)
                action:ScrollByPage(1)
                action:ScrollToTop
                sleep:300
                shot:top.bmp
                action:ScrollToBottom
                action:Search
                type:output 3
                key:enter
                key:enter
                skey:enter
                sleep:500
                shot:search.bmp
                key:escape
                click:995,300
                sleep:300
                action:ClearScrollback
                sleep:300
                wait-file:done
                close-window
                """,
        });

        // Act
        var marks = await Checks.RenderedAsync(run, "marks.bmp");
        var top = await Checks.RenderedAsync(run, "top.bmp");
        var search = await Checks.RenderedAsync(run, "search.bmp");

        // Assert
        await Checks.DifferAsync(marks, top, because: "scrolled to the top");
        await Checks.DifferAsync(top, search, because: "the search bar and its matches");
        await run.WaitForPanesAsync(p => p[0].Int("history_size") == 0); // ClearScrollback emptied it
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task An_internal_error_is_logged_and_the_window_keeps_running()
    {
        // Arrange: throw:N makes the next frames fail (Windowing/WindowErrors.cs).
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                sleep:600
                throw:2
                sleep:800
                shot:error.bmp
                action:ShowAbout
                sleep:500
                shot:about.bmp
                key:escape
                wait-file:done
                close-window
                """,
        });

        // Act
        await Checks.RenderedAsync(run, "error.bmp");
        await Checks.RenderedAsync(run, "about.bmp");
        await run.TypeAsync("echo still-alive");
        await run.WaitForTextAsync("still-alive");
        run.Release("done");

        // Assert: it exits cleanly and the failure is in a crash log, not lost.
        int code = await run.WaitForExitAsync();
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(run.CrashLogs.Length).IsGreaterThanOrEqualTo(1);
        await Assert.That(run.CrashLogs[0]).Contains("debug");
    }
}
