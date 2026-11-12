namespace ErikTerm.IntegrationTests.Scenarios;

/// <summary>Splits, zoom, the pane picker, layouts and pane moves: through actions and through erikterm cli.</summary>
public class LayoutTests
{
    [Test]
    public async Task Splits_zoom_and_layout_actions_change_the_panes()
    {
        // Arrange
        await using var run = AppRun.Start(new Scenario
        {
            Script = """
                shot:one.bmp
                sleep:300
                action:SplitHorizontal
                sleep:400
                action:SplitVertical
                sleep:400
                shot:splits.bmp
                wait-file:split
                action:TogglePaneZoomState
                sleep:400
                shot:zoom.bmp
                wait-file:zoomed
                action:TogglePaneZoomState
                action:PaneSelect
                sleep:400
                shot:pane-select.bmp
                key:escape
                action:ActivatePaneDirection(Left)
                action:AdjustPaneSize(Right,6)
                sleep:300
                wait-file:adjusted
                action:EqualizePanes
                action:SwapPane
                action:SwapPane(Previous)
                action:RotatePanes(Clockwise)
                action:RotatePanes(CounterClockwise)
                action:PaneLayout(tiled)
                sleep:300
                action:MovePaneToEdge(Right)
                action:PaneLayout(even-horizontal)
                sleep:300
                action:MovePaneToNewTab
                sleep:500
                wait-file:moved
                close-window
                """,
        });

        // Act / Assert: three panes, one focused.
        var one = await Checks.RenderedAsync(run, "one.bmp");
        var splits = await Checks.RenderedAsync(run, "splits.bmp");
        await Checks.DifferAsync(one, splits, because: "two splits draw dividers and two more panes");
        var panes = await run.WaitForPanesAsync(p => p.Length == 3);
        await Assert.That(panes.Count(p => p.Bool("is_active"))).IsEqualTo(1);
        await Assert.That(panes.Max(p => p.Cols())).IsLessThan(100);
        run.Release("split");

        // Zoom: the focused pane takes the whole tab.
        var zoom = await Checks.RenderedAsync(run, "zoom.bmp");
        await Checks.DifferAsync(splits, zoom, because: "zooming hides the other panes");
        panes = await run.WaitForPanesAsync(p => p.Any(x => x.Bool("is_zoomed")));
        await Assert.That(panes.Single(p => p.Bool("is_zoomed")).Cols()).IsGreaterThanOrEqualTo(95);
        run.Release("zoomed");

        var pick = await Checks.RenderedAsync(run, "pane-select.bmp");
        await Checks.DifferAsync(splits, pick, because: "the pane picker labels every pane");

        // AdjustPaneSize(Right,6) on the left pane: it grows.
        panes = await run.WaitForPanesAsync(p => p.Length == 3 && p.Max(x => x.Cols()) > 49);
        run.Release("adjusted");

        // MovePaneToNewTab: two tabs, one with a single pane.
        panes = await run.WaitForPanesAsync(p => p.Tabs() == 2);
        await Assert.That(panes.PanesPerTab().Order().ToArray()).IsEquivalentTo(new[] { 1, 2 });
        run.Release("moved");

        await Checks.CleanExitAsync(run);
    }

    [Test]
    public async Task Cli_layout_commands_split_resize_swap_and_kill_panes()
    {
        // Arrange
        await using var run = AppRun.Start(new Scenario { Script = "wait-file:done\nclose-window" });

        // Act
        string right = (await run.CliAsync("split-pane", "--right", "--percent", "30")).Trim();
        string bottom = (await run.CliAsync("split-pane", "--bottom", "--pane-id", right, "--", Platform.Shell)).Trim();
        var panes = await run.WaitForPanesAsync(p => p.Length == 3);

        // Assert: the 30 % split is the narrow one.
        var narrow = panes.Single(p => p.Int("pane_id") == int.Parse(right));
        await Assert.That(narrow.Cols()).IsLessThan(40);
        await Assert.That(int.Parse(bottom)).IsNotEqualTo(int.Parse(right));

        await run.CliAsync("adjust-pane-size", "--amount", "5", "Left");
        await run.CliAsync("activate-pane-direction", "Left");
        string neighbour = (await run.CliAsync("get-pane-direction", "Right")).Trim();
        await Assert.That(neighbour).IsNotEmpty();
        await run.CliAsync("zoom-pane", "--toggle");
        panes = await run.WaitForPanesAsync(p => p.Any(x => x.Bool("is_zoomed")));
        await run.CliAsync("zoom-pane", "--toggle");
        await run.CliAsync("swap-pane", "--previous");
        await run.CliAsync("rotate-panes", "--counter-clockwise");
        foreach (var layout in new[] { "even-horizontal", "even-vertical", "main-vertical", "main-horizontal", "tiled" })
            await run.CliAsync("select-layout", layout);
        await run.CliAsync("activate-pane", "--pane-id", bottom);
        await run.CliAsync("move-pane-to-new-tab", "--pane-id", bottom);
        panes = await run.WaitForPanesAsync(p => p.Tabs() == 2);
        await run.CliAsync("activate-tab", "--tab-index", "0");
        await run.CliAsync("kill-pane", "--pane-id", right);
        panes = await run.WaitForPanesAsync(p => p.Length == 2);

        // Assert
        await Assert.That(panes.PanesPerTab()).IsEquivalentTo(new[] { 1, 1 });
        run.Release("done");
        await Checks.CleanExitAsync(run);
    }
}
