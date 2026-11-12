using ErikTerm.App;
using ErikTerm.App.Ipc;
using ErikTerm.App.Ui;
using ErikTerm.App.WindowFeatures;
using ErikTerm.Config;

namespace ErikTerm.Tests;

/// <summary>Files and text dropped on a pane (through the debug script's drop steps, which feed the same handler as SDL's events).</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class WindowDropTests
{
    private static (FakeFeatureWindow Window, WindowDrop Drop) New(bool dropFiles = true)
    {
        var window = new FakeFeatureWindow(new Settings { DropFiles = dropFiles });
        return (window, new WindowDrop(window, window, window));
    }

    [Test]
    public async Task ADroppedFileIsPastedAsAQuotedPath()
    {
        // Arrange
        var (window, drop) = New();
        using var _ = window;

        // Act
        bool handled = drop.RunScriptStep("drop", "0,0:/tmp/my file.png", 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(window.Pastes.Single().Text).IsEqualTo("'/tmp/my file.png' "); // a space after, ready for the next argument
        await Assert.That(window.Toasts.Single().Title).IsEqualTo("Pasted path");
    }

    [Test]
    public async Task SeveralFilesArePastedTogether()
    {
        // Arrange
        var (window, drop) = New();
        using var _ = window;

        // Act
        drop.RunScriptStep("drop", "0,0:/a/b.png|/c/d.txt", 0);

        // Assert
        await Assert.That(window.Pastes.Single().Text).IsEqualTo("/a/b.png /c/d.txt ");
        await Assert.That(window.Toasts.Single()).IsEqualTo((ToastKind.Success, "Pasted 2 paths", (string?)"b.png  d.txt"));
    }

    [Test]
    public async Task DroppedFileUrisArePaths()
    {
        // Arrange
        var (window, drop) = New();
        using var _ = window;

        // Act
        drop.RunScriptStep("droptext", "0,0:file:///a/b.png", 0);

        // Assert
        await Assert.That(window.Pastes.Single().Text).IsEqualTo("/a/b.png ");
    }

    [Test]
    public async Task DroppedTextIsPastedAsOneLine()
    {
        // Arrange
        var (window, drop) = New();
        using var _ = window;

        // Act
        drop.RunScriptStep("droptext", "0,0:hello\\nworld", 0);

        // Assert
        await Assert.That(window.Pastes.Single().Text).IsEqualTo("hello world");
        await Assert.That(window.Toasts.Single().Title).IsEqualTo("Pasted dropped text");
    }

    [Test]
    public async Task DroppingOffMeansNothingIsPasted()
    {
        // Arrange
        var (window, drop) = New(dropFiles: false);
        using var _ = window;

        // Act
        drop.RunScriptStep("drop", "0,0:/a/b.png", 0);

        // Assert
        await Assert.That(window.Pastes).IsEmpty();
    }

    [Test]
    public async Task ADropOnAnotherPaneFocusesIt()
    {
        // Arrange
        var (window, drop) = New();
        using var _ = window;
        var other = window.SplitActive();
        window.PointerPane = other;

        // Act
        drop.RunScriptStep("drop", "0,0:/a/b.png", 0);

        // Assert
        await Assert.That(window.Focused).IsSameReferenceAs(other);
        await Assert.That(window.Pastes.Single().Pane).IsSameReferenceAs(other);
    }

    [Test]
    public async Task DraggingOverWakesTheLoopAndLeavingStops()
    {
        // Arrange
        var (window, drop) = New();
        using var _ = window;
        window.PointerPane = window.Focused;

        // Act
        drop.RunScriptStep("dragover", "1,1", 0);
        int over = drop.WakeMs(0);
        bool fading = drop.IsAnimating(1);
        drop.RunScriptStep("dragleave", "", 10);

        // Assert
        await Assert.That(over).IsLessThan(int.MaxValue);
        await Assert.That(fading).IsTrue();
        await Assert.That(drop.WakeMs(10)).IsEqualTo(int.MaxValue);
        await Assert.That(window.Pastes).IsEmpty();
    }

    [Test]
    public async Task AStaleDragEndsOnTick()
    {
        // Arrange: a begin and a position, then nothing (the compositor lost the drag).
        var (window, drop) = New();
        using var _ = window;
        drop.RunScriptStep("dragover", "1,1", 0);

        // Act: the script holds the highlight a minute.
        drop.Tick(120_000, window.Layout);

        // Assert
        await Assert.That(drop.WakeMs(120_000)).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task APaneThatLeftTheLayoutLosesTheHighlight()
    {
        // Arrange
        var (window, drop) = New();
        using var _ = window;
        var other = window.SplitActive();
        window.PointerPane = other;
        drop.RunScriptStep("dragover", "1,1", 0);
        window.Layout.Remove(other);

        // Act
        drop.Tick(1, window.Layout);

        // Assert
        await Assert.That(drop.IsAnimating(2)).IsFalse();
    }

    [Test]
    public async Task ACellClickIsTheWindowsClick()
    {
        // Arrange
        var (window, drop) = New();
        using var _ = window;

        // Act
        bool handled = drop.RunScriptStep("cellclick", "2,3", 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(window.Calls).Contains("ScriptClick");
    }

    [Test]
    public async Task OtherStepsAreNotDrops()
    {
        // Arrange
        var (window, drop) = New();
        using var _ = window;

        // Act
        bool handled = drop.RunScriptStep("key", "a", 0);

        // Assert
        await Assert.That(handled).IsFalse();
    }
}

/// <summary>Which pane or tab an IPC request names in a window.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class IpcTargetsTests
{
    [Test]
    public async Task WithoutAPaneItIsTheFocusedOne()
    {
        // Arrange
        using var window = new FakeFeatureWindow();
        var targets = new IpcTargets(window);

        // Act
        var (tab, pane) = targets.Target(new IpcRequest());

        // Assert
        await Assert.That(tab).IsSameReferenceAs(window.ActiveTab);
        await Assert.That(pane).IsSameReferenceAs(window.Focused);
        await Assert.That(targets.IsActive(tab)).IsTrue();
    }

    [Test]
    public async Task APaneIsFoundInAnyTab()
    {
        // Arrange
        using var window = new FakeFeatureWindow();
        var first = window.ActiveTab;
        window.AddTab();
        var targets = new IpcTargets(window);

        // Act
        var (tab, pane) = targets.Target(new IpcRequest { PaneId = first.Focused.Id });

        // Assert
        await Assert.That(tab).IsSameReferenceAs(first);
        await Assert.That(pane).IsSameReferenceAs(first.Focused);
        await Assert.That(targets.IsActive(tab)).IsFalse();
    }

    [Test]
    public async Task AnUnknownPaneIsAnError()
    {
        // Arrange
        using var window = new FakeFeatureWindow();
        var targets = new IpcTargets(window);

        // Act + Assert
        await Assert.That(() => targets.Target(new IpcRequest { PaneId = -5 })).Throws<IpcException>();
    }

    [Test]
    public async Task AnotherWindowsCallerMeansTheFocusedPane()
    {
        // Arrange: --window-id with the caller's own pane, which lives in another window.
        using var window = new FakeFeatureWindow();
        var targets = new IpcTargets(window);

        // Act
        var (_, pane) = targets.Target(new IpcRequest { PaneId = -5, WindowId = 1 });

        // Assert
        await Assert.That(pane).IsSameReferenceAs(window.Focused);
    }

    [Test]
    public async Task ATabIsFoundByIdOrByItsPane()
    {
        // Arrange
        using var window = new FakeFeatureWindow();
        var first = window.ActiveTab;
        var second = window.AddTab();
        var targets = new IpcTargets(window);

        // Act
        var byId = targets.Tab(new IpcRequest { TabId = first.Id });
        var byPane = targets.Tab(new IpcRequest { PaneId = second.Focused.Id });

        // Assert
        await Assert.That(byId).IsSameReferenceAs(first);
        await Assert.That(byPane).IsSameReferenceAs(second);
    }

    [Test]
    public async Task AnUnknownTabIsAnError()
    {
        // Arrange
        using var window = new FakeFeatureWindow();
        var targets = new IpcTargets(window);

        // Act + Assert
        await Assert.That(() => targets.Tab(new IpcRequest { TabId = -1 })).Throws<IpcException>();
    }
}
