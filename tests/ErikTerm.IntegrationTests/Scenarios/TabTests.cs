namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>Tabs: new tabs, renaming through the palette, colors, the Ctrl+Tab switcher, an overflowing bar, moves.</summary>
public class TabTests
{
    [Test]
    public async Task Tabs_are_renamed_colored_switched_and_overflow()
    {
        // Arrange
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                shot:one-tab.bmp
                feed:\e[H\e[2J tab one
                action:SetTabColor(blue)
                action:SpawnTab
                sleep:400
                feed:\e[H\e[2J tab two\e]9;4;1;40\e\\
                action:SetTabColor(green)
                action:SpawnTab
                sleep:400
                action:ActivateCommandPalette
                type:Rename Tab
                key:enter
                type:renamed-third
                key:enter
                sleep:300
                shot:tabs.bmp
                wait-file:named
                ckey:tab
                sleep:500
                shot:switcher.bmp
                ctrl-up
                sleep:300
                wait-file:switched
                ctrl-tap
                action:ActivateTabRelative(1)
                action:MoveTabRelative(-1)
                action:ShowTabSwitcher
                sleep:300
                key:escape
                hovertab:0
                sleep:200
                menutab:0
                sleep:300
                shot:tab-menu.bmp
                key:escape
                action:SpawnTab
                action:SpawnTab
                action:SpawnTab
                action:SpawnTab
                action:SpawnTab
                action:SpawnTab
                action:SpawnTab
                action:SpawnTab
                action:SpawnTab
                action:SpawnTab
                action:SpawnTab
                action:SpawnTab
                sleep:800
                shot:overflow.bmp
                wheelbar:2
                clickmore
                sleep:400
                shot:overflow-list.bmp
                key:escape
                wait-file:many
                action:CloseCurrentTab
                sleep:300
                wait-file:closed
                close-window
                """,
        });

        // Act / Assert: the third tab got its title from the palette's Rename Tab.
        var one = await Checks.RenderedAsync(run, "one-tab.bmp");
        var tabs = await Checks.RenderedAsync(run, "tabs.bmp");
        await Checks.DifferAsync(one, tabs, because: "three tabs on the bar");
        var panes = await run.WaitForPanesAsync(p => p.Tabs() == 3 && p.Any(x => x.TabTitle() == "renamed-third"));
        await Assert.That(panes.Single(p => p.Bool("tab_active")).TabTitle()).IsEqualTo("renamed-third");
        run.Release("named");

        // The switcher (most recently used first): releasing Ctrl picks the tab used before this one, the second.
        var switcher = await Checks.RenderedAsync(run, "switcher.bmp");
        await Checks.DifferAsync(tabs, switcher, because: "the Ctrl+Tab list");
        panes = await run.WaitForPanesAsync(p => p.Single(x => x.Bool("tab_active")).Int("tab_index") == 1);
        run.Release("switched");

        var menu = await Checks.RenderedAsync(run, "tab-menu.bmp");
        var overflow = await Checks.RenderedAsync(run, "overflow.bmp");
        var list = await Checks.RenderedAsync(run, "overflow-list.bmp");
        await Checks.DifferAsync(overflow, list, because: "the list of tabs that don't fit");
        await Checks.DifferAsync(tabs, menu, because: "the tab's context menu");
        panes = await run.WaitForPanesAsync(p => p.Tabs() == 15);
        run.Release("many");

        panes = await run.WaitForPanesAsync(p => p.Tabs() == 14);
        run.Release("closed");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Cli_spawns_titles_colors_and_activates_tabs()
    {
        // Arrange
        await using var run = AppRun.Start(new Scenario { Script = "wait-file:done\nclose-window" });

        // Act
        await run.CliAsync("spawn");
        await run.CliAsync("spawn", "--cwd", run.Directory);
        await run.CliAsync("set-tab-title", "logs");
        await run.CliAsync("set-tab-color", "red");
        await run.CliAsync("activate-tab", "--tab-index", "0");
        await run.CliAsync("set-tab-title", "first");
        await run.CliAsync("activate-tab", "--tab-relative", "1");
        await run.CliAsync("set-window-title", "integration");
        var panes = await run.WaitForPanesAsync(p => p.Tabs() == 3);

        // Assert
        await Assert.That(panes.Select(p => p.TabTitle())).Contains("logs");
        await Assert.That(panes.Select(p => p.TabTitle())).Contains("first");
        await Assert.That(panes.Single(p => p.Bool("tab_active")).Int("tab_index")).IsEqualTo(1);
        var spawned = panes.Single(p => p.Int("tab_index") == 2);
        await Assert.That(Platform.SamePath(spawned.Str("current_path"), run.Directory)).IsTrue().Because(spawned.Str("current_path"));
        string table = await run.CliAsync("list");
        await Assert.That(table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).IsEqualTo(4);
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task A_tab_dragged_out_of_the_bar_tears_off_into_a_window()
    {
        // Arrange: three tabs; the second is dragged down out of the window and dropped (Host/TabTearOff.cs).
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                action:SpawnTab
                sleep:400
                action:SpawnTab
                sleep:400
                presstab:1
                drag:400,300
                drag:500,600
                sleep:300
                shot:{window}-dragging.bmp
                up:500,600
                sleep:1500
                wait-file:torn
                presstab:0
                drag:300,-200
                drag:300,-400
                sleep:300
                up:300,-400
                sleep:1500
                shot:second-drag.bmp
                wait-file:done
                close-window
                sleep:400
                close-window
                sleep:400
                close-window
                """,
        });

        // Act / Assert 1: the dragged tab is a window of its own.
        var panes = await run.WaitForPanesAsync(p => p.Windows() == 2);
        await Assert.That(panes.Tabs()).IsEqualTo(3);
        await Assert.That(panes.GroupBy(p => p.Int("window_id")).Select(g => g.Count()).Order().ToArray()).IsEquivalentTo(new[] { 1, 2 });
        run.Release("torn");

        // Assert 2: a second drag, above the window this time, loses no tab and no pane.
        await Checks.RenderedAsync(run, "second-drag.bmp");
        panes = await run.PanesAsync();
        await Assert.That(panes.Tabs()).IsEqualTo(3);
        await Assert.That(panes.Length).IsEqualTo(3);
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task A_tab_moves_to_a_new_window_and_back()
    {
        // Arrange
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                action:SpawnTab
                sleep:400
                move-tab:0
                sleep:800
                shot:{window}-two.bmp
                wait-file:two
                action:SpawnTab
                sleep:400
                action:MoveTabToWindow(1)
                sleep:600
                new-window
                sleep:800
                wait-file:three
                close-window
                sleep:500
                close-window
                sleep:500
                close-window
                """,
        });

        // Act / Assert: two windows of one tab each.
        var panes = await run.WaitForPanesAsync(p => p.Windows() == 2);
        await Assert.That(panes.Tabs()).IsEqualTo(2);
        run.Release("two");

        panes = await run.WaitForPanesAsync(p => p.Windows() == 3);
        await Assert.That(panes.Length).IsEqualTo(4);
        run.Release("three");
        await Checks.CleanExitAsync(run);
    }
}
