using ErikTerm.App;
using ErikTerm.App.Ipc;
using ErikTerm.App.Panes;
using ErikTerm.App.Teammates;
using ErikTerm.App.Ui;
using ErikTerm.App.WindowFeatures;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>The window's side of the tmux emulation: the requests the shim adds, what list-panes reports, and the palette toggle.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class WindowTmuxTests
{
    private static (FakeFeatureWindow Window, WindowTmux Tmux) New(Settings? settings = null)
    {
        var window = new FakeFeatureWindow(settings);
        var gate = new Lock();
        return (window, new WindowTmux(window, window, new IpcTargets(window), new WindowTeammates<Pane>(window), gate));
    }

    [Test]
    public async Task OtherRequestsAreNotTheEmulations()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;

        // Act
        var response = tmux.HandleIpc(new IpcRequest { Command = "list" });

        // Assert
        await Assert.That(response).IsNull();
    }

    [Test]
    public async Task SetPaneTitleNamesThePaneAndAnEmptyOneClearsIt()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;
        var pane = window.Focused;

        // Act
        tmux.HandleIpc(new IpcRequest { Command = "set-pane-title", PaneId = pane.Id, Title = "build" });
        string? named = pane.TmuxTitle;
        tmux.HandleIpc(new IpcRequest { Command = "set-pane-title", PaneId = pane.Id, Title = "" });

        // Assert
        await Assert.That(named).IsEqualTo("build");
        await Assert.That(pane.TmuxTitle).IsNull();
        await Assert.That(window.Redraws).IsEqualTo(2);
    }

    [Test]
    public async Task SelectLayoutArrangesTheActiveTabAndLeavesZoom()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;
        window.SplitActive();
        window.SplitActive();
        window.SetZoom(window.Focused);

        // Act
        var response = tmux.HandleIpc(new IpcRequest { Command = "select-layout", Layout = "tiled" });

        // Assert
        await Assert.That(response!.Ok).IsTrue();
        await Assert.That(window.Zoomed).IsNull();
        await Assert.That(window.Calls).Contains("ApplyLayout(True)");
    }

    [Test]
    public async Task EvenEqualizesABackgroundTabWithoutAnimating()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;
        var background = window.ActiveTab;
        window.SplitActive();
        background.Zoomed = background.Focused;
        window.AddTab();

        // Act
        tmux.HandleIpc(new IpcRequest { Command = "select-layout", TabId = background.Id, Layout = "even" });

        // Assert
        await Assert.That(background.Zoomed).IsNull();
        await Assert.That(window.Calls).Contains("ApplyLayout(False)");
    }

    [Test]
    public async Task AnUnknownLayoutIsAnError()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;

        // Act + Assert
        await Assert.That(() => tmux.HandleIpc(new IpcRequest { Command = "select-layout", Layout = "spiral" })).Throws<IpcException>();
    }

    [Test]
    public async Task ResizePaneInCellsReLaysOut()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;
        window.SplitActive();

        // Act
        var response = tmux.HandleIpc(new IpcRequest { Command = "resize-pane", PaneId = window.Focused.Id, Width = 20 });

        // Assert
        await Assert.That(response!.Ok).IsTrue();
        await Assert.That(window.Calls).Contains("ApplyLayout(True)");
    }

    [Test]
    public async Task ResizePaneInPercent()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;
        var top = window.Focused;
        var bottom = window.NewPane();
        window.Layout.Split(top, bottom, SplitAxis.Vertical);

        // Act
        tmux.HandleIpc(new IpcRequest { Command = "resize-pane", PaneId = top.Id, HeightPercent = 25 });

        // Assert
        await Assert.That(window.Layout.Root.Ratio).IsLessThan(0.5);
    }

    [Test]
    public async Task ResizingTheOnlyPaneChangesNothing()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;

        // Act
        tmux.HandleIpc(new IpcRequest { Command = "resize-pane", Width = 20, Height = 5 });

        // Assert
        await Assert.That(window.Calls.Any(c => c.StartsWith("ApplyLayout"))).IsFalse();
    }

    [Test]
    public async Task RespawnRefusesAPaneThatStillRuns()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;

        // Act + Assert
        await Assert.That(() => tmux.HandleIpc(new IpcRequest { Command = "respawn-pane", PaneId = window.Focused.Id })).Throws<IpcException>();
    }

    [Test]
    public async Task RespawnRefusesADirectoryThatIsNotThere()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;

        // Act + Assert
        await Assert.That(() => tmux.HandleIpc(new IpcRequest { Command = "respawn-pane", Kill = true, Cwd = "/nonexistent/etww" })).Throws<IpcException>();
    }

    [Test]
    public async Task RespawnWithKillRunsTheNewProgramInThePane()
    {
        // Arrange
        var (window, tmux) = New();
        using var _ = window;
        var pane = window.Focused;
        var before = pane.Pty;
        string cwd = Directory.GetCurrentDirectory();

        // Act
        var response = tmux.HandleIpc(new IpcRequest { Command = "respawn-pane", Kill = true, Cwd = cwd, Argv = ["/bin/sh", "-c", "exec sleep 60"] });

        // Assert
        await Assert.That(response!.Ok).IsTrue();
        await Assert.That(pane.Pty).IsNotSameReferenceAs(before);
        await Assert.That(pane.LaunchArgv).IsEquivalentTo(["/bin/sh", "-c", "exec sleep 60"], CollectionOrdering.Matching);
        await Assert.That(pane.LaunchCwd).IsEqualTo(cwd);
    }

    [Test]
    public async Task ListPanesInfoSaysWhereThePaneIsAndWhatItRuns()
    {
        // Arrange
        var (window, tmux) = New(new Settings { ScrollbackLines = 5000 });
        using var _ = window;
        var pane = window.Focused;
        pane.Target = new RectI(100, 40, 400, 200);
        pane.TmuxTitle = "agent";
        var info = new PaneInfo();

        // Act
        tmux.AddInfo(info, pane, new RectI(0, 0, 800, 600), tabActive: true);

        // Assert: cells of 10 x 20 pixels.
        await Assert.That((info.Left, info.Top)).IsEqualTo((10, 2));
        await Assert.That(info.PaneTitle).IsEqualTo("agent");
        await Assert.That(info.HistoryLimit).IsEqualTo(5000);
        await Assert.That(info.TabActive).IsTrue();
        await Assert.That(info.RealTmux).IsFalse();
        await Assert.That(info.Pid).IsEqualTo(pane.Pty.ProcessId);
    }

    [Test]
    public async Task TheEmulationFollowsTheSettingUntilToggled()
    {
        // Arrange
        var (window, tmux) = New(new Settings { TmuxEmulation = true });
        using var _ = window;
        var list = new List<Command>();
        tmux.AddCommands(list);

        // Act
        list.Single().Run!();

        // Assert
        await Assert.That(tmux.Emulation).IsFalse();
        await Assert.That(window.Toasts.Single().Title).IsEqualTo("tmux emulation off");
    }

    [Test]
    public async Task TogglingItOnSaysWhatItDoes()
    {
        // Arrange
        var (window, tmux) = New(new Settings { TmuxEmulation = false });
        using var _ = window;
        var list = new List<Command>();
        tmux.AddCommands(list);

        // Act
        list.Single().Run!();

        // Assert
        await Assert.That(tmux.Emulation).IsTrue();
        await Assert.That(window.Toasts.Single().Title).IsEqualTo("tmux emulation on");
        await Assert.That(list.Single().Title).IsEqualTo("Toggle tmux Emulation (off)");
    }

    [Test]
    public async Task TogglingItOnWithoutAShimIsAnError()
    {
        // Arrange
        var (window, tmux) = New(new Settings { TmuxEmulation = false });
        using var _ = window;
        window.TmuxShimAvailable = false;
        var list = new List<Command>();
        tmux.AddCommands(list);

        // Act
        list.Single().Run!();

        // Assert
        await Assert.That(window.Toasts.Single()).IsEqualTo((ToastKind.Error, "tmux emulation unavailable", (string?)"No control socket or shim directory for this window"));
    }

    [Test]
    public async Task SleepIsNotRealTmux()
    {
        // Arrange
        using var window = new FakeFeatureWindow();
        await Eventually.True(() => window.Focused.Pty.ForegroundProcessName == "sleep");

        // Act
        bool real = WindowTmux.IsRealTmux(window.Focused);

        // Assert
        await Assert.That(real).IsFalse();
    }
}
