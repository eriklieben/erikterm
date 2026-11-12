namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>
/// Every overlay opens over the pane, draws something of its own and closes again with Escape, leaving the pane as it
/// was: the command palette, the context menu, search, quick select, About, the settings page, the theme browser, the
/// agents overview, the notifications panel, the vim cheat sheet, the pane picker and the worktree switcher.
/// </summary>
public class OverlayTests
{
    private static readonly (string Name, string Open)[] Overlays =
    [
        ("palette", "action:ActivateCommandPalette\ntype:split"),
        ("palette-empty", "action:ActivateCommandPalette\ntype:zzzzqqq-nothing"),
        ("context-menu", "menu\nhover-menu\nhover-menu"),
        ("search", "action:Search\ntype:needle"),
        ("search-all", "action:SearchAllPanes\ntype:needle"),
        ("quick-select", "action:QuickSelect"),
        ("about", "action:ShowAbout"),
        ("settings", "action:OpenSettings\nsleep:400\nkey:down\nkey:down\nkey:tab\ntype:font"),
        ("themes", "action:BrowseThemes\ntype:gruv\nkey:down"),
        ("agents", "action:AgentsOverview"),
        ("notifications", "action:NotificationsPanel"),
        ("vim-help", "vim:g?"),
        ("pane-select", "action:PaneSelect"),
        ("tab-switcher", "action:ShowTabSwitcher"),
    ];

    [Test]
    public async Task Each_overlay_draws_and_closes_with_escape()
    {
        // Arrange: text to search and pick from, then each overlay between two plain frames.
        var script = new List<string>
        {
            @"feed:\e[H\e[2Jneedle in a haystack, see https://example.com/docs and /usr/share/doc/README\r\n" +
                @"commit 1a2b3c4d5e6f deadbeef 203.0.113.20 needle again\r\n",
            "sleep:400",
            "shot:base.bmp",
        };
        foreach (var (name, open) in Overlays)
        {
            script.Add(open);
            script.Add("sleep:500");
            script.Add($"shot:{name}.bmp");
            script.Add("sleep:200");
            script.Add("key:escape");
            script.Add("key:escape");
            script.Add("sleep:300");
        }
        script.Add("shot:after.bmp");
        script.Add("sleep:300");
        script.Add("close-window");
        await using var run = AppRun.Start(new Scenario { Script = string.Join('\n', script) });

        // Act
        var baseline = await Checks.RenderedAsync(run, "base.bmp");

        // Assert: each overlay changed the frame, and everything was closed again at the end.
        foreach (var (name, _) in Overlays)
        {
            var shot = await Checks.RenderedAsync(run, $"{name}.bmp");
            await Checks.DifferAsync(baseline, shot, because: name);
        }
        var after = await Checks.RenderedAsync(run, "after.bmp");
        await Assert.That(after.DifferenceFrom(baseline)).IsLessThan(0.02);
        string text = await run.CliAsync("get-text");
        await Assert.That(text).Contains("needle in a haystack");
        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Palette_commands_run_the_chosen_action()
    {
        // Arrange: the palette's own commands (Split Right, a font size step, Clear Scrollback, Reload) by name.
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                action:ActivateCommandPalette
                type:Split Right
                key:enter
                sleep:500
                wait-file:split
                action:ActivateCommandPalette
                type:Increase Font Size
                key:enter
                sleep:500
                wait-file:font
                action:ActivateCommandPalette
                type:Reset Font Size
                key:enter
                action:ActivateCommandPalette
                type:Show Config Problems
                key:enter
                sleep:300
                key:escape
                action:ActivateCommandPalette
                type:Reload Configuration
                key:enter
                sleep:500
                action:ActivateCommandPalette
                key:down
                key:down
                key:up
                type:Toggle Broadcast
                key:enter
                sleep:200
                action:ToggleBroadcastInput
                action:ActivateCommandPalette
                type:Close Pane
                key:enter
                sleep:500
                wait-file:closed
                close-window
                """,
        });

        // Act / Assert
        var panes = await run.WaitForPanesAsync(p => p.Length == 2);
        int before = panes.Max(p => p.Rows());
        run.Release("split");

        panes = await run.WaitForPanesAsync(p => p.Length == 2 && p.Max(x => x.Rows()) < before);
        run.Release("font");

        panes = await run.WaitForPanesAsync(p => p.Length == 1);
        run.Release("closed");
        await Checks.CleanExitAsync(run);
    }
}
