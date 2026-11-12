using ErikTerm.App.Native;
using ErikTerm.App.Ui;

namespace ErikTerm.Tests;

/// <summary>The About screen's keyboard: the footer's buttons in focus order, copying diagnostics, closing it.</summary>
public class AboutScreenKeysTests
{
    private sealed class Harness
    {
        public readonly AboutScreen Screen = new();
        public int Copies, ConfigsOpened;

        public Harness()
        {
            Screen.CopyDiagnostics = () => Copies++;
            Screen.OpenConfig = () => ConfigsOpened++;
            Screen.Open(new AboutInfo { Version = "1.2.3", Commit = "abc1234", BuildDate = "2026-11-12" }, new Theme(), 0);
        }

        public void Key(uint key, ushort mod = 0) => Screen.HandleKey(key, mod, 0);
        public void Tabs(int n)
        {
            for (int i = 0; i < n; i++) Key(Sdl.K_TAB);
        }
    }

    [Test]
    public async Task EnterClosesItFromTheStart()
    {
        // Arrange: the Close button has the focus, unshown.
        var h = new Harness();

        // Act
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Screen.IsOpen).IsFalse();
        await Assert.That(h.Copies + h.ConfigsOpened).IsEqualTo(0);
    }

    [Test]
    public async Task EscapeClosesIt()
    {
        // Arrange
        var h = new Harness();

        // Act
        bool handled = h.Screen.HandleKey(Sdl.K_ESCAPE, 0, 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(h.Screen.IsOpen).IsFalse();
    }

    [Test]
    public async Task CtrlCCopiesTheDiagnostics()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key('c', Sdl.KMOD_CTRL);

        // Assert
        await Assert.That(h.Copies).IsEqualTo(1);
        await Assert.That(h.Screen.IsOpen).IsTrue();
    }

    [Test]
    public async Task TabThreeTimesReachesCopy()
    {
        // Arrange: Overview, Licenses, Copy.
        var h = new Harness();
        h.Tabs(3);

        // Act
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Copies).IsEqualTo(1);
    }

    [Test]
    public async Task TheConfigButtonClosesAndOpensTheConfig()
    {
        // Arrange
        var h = new Harness();
        h.Tabs(4);

        // Act
        h.Key(Sdl.K_KP_ENTER);

        // Assert
        await Assert.That(h.ConfigsOpened).IsEqualTo(1);
        await Assert.That(h.Screen.IsOpen).IsFalse();
    }

    [Test]
    public async Task ShiftTabGoesBackwardsAndWraps()
    {
        // Arrange: the first Shift+Tab lands on the last item, Close.
        var h = new Harness();
        h.Key(Sdl.K_TAB, Sdl.KMOD_SHIFT);
        h.Key(Sdl.K_TAB, Sdl.KMOD_SHIFT);

        // Act: OpenConfig.
        h.Key(Sdl.K_SPACE);

        // Assert
        await Assert.That(h.ConfigsOpened).IsEqualTo(1);
    }

    [Test]
    public async Task ArrowsMoveAlongTheFooterButtons()
    {
        // Arrange: on Copy.
        var h = new Harness();
        h.Tabs(3);

        // Act
        h.Key(Sdl.K_RIGHT);
        h.Key(Sdl.K_RIGHT);
        h.Key(Sdl.K_RIGHT); // stays on Close
        h.Key(Sdl.K_LEFT);  // OpenConfig
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.ConfigsOpened).IsEqualTo(1);
    }

    [Test]
    public async Task OnTheTabsArrowsSwitchTabsAndEnterStaysOpen()
    {
        // Arrange: the Overview tab has the focus.
        var h = new Harness();
        h.Tabs(1);

        // Act
        h.Key(Sdl.K_RIGHT);
        h.Key(Sdl.K_RETURN);
        h.Key(Sdl.K_LEFT);
        h.Key(Sdl.K_RETURN);

        // Assert
        await Assert.That(h.Screen.IsOpen).IsTrue();
        await Assert.That(h.Copies + h.ConfigsOpened).IsEqualTo(0);
    }

    [Test]
    [Arguments(Sdl.K_UP)]
    [Arguments(Sdl.K_DOWN)]
    [Arguments(Sdl.K_PAGEUP)]
    [Arguments(Sdl.K_PAGEDOWN)]
    [Arguments(Sdl.K_HOME)]
    [Arguments(Sdl.K_END)]
    [Arguments(Sdl.K_LEFT)]
    [Arguments(Sdl.K_TAB)]
    public async Task ScrollingAndTabKeysKeepItOpen(uint key)
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key(key);
        h.Key(Sdl.K_TAB, Sdl.KMOD_CTRL);

        // Assert
        await Assert.That(h.Screen.IsOpen).IsTrue();
        await Assert.That(h.Screen.IsAnimating(0)).IsTrue();
    }

    [Test]
    public async Task SpaceWithoutAVisibleFocusScrollsInsteadOfPressing()
    {
        // Arrange
        var h = new Harness();

        // Act
        h.Key(Sdl.K_SPACE);

        // Assert: Close has the focus, but it isn't shown, so nothing was pressed.
        await Assert.That(h.Screen.IsOpen).IsTrue();
    }

    [Test]
    public async Task TheMouseOutsideThePanelCloses()
    {
        // Arrange: never drawn, so every point is outside.
        var h = new Harness();

        // Act
        bool move = h.Screen.HandleMouseMove(1, 1, 0);
        bool down = h.Screen.HandleMouseDown(1, 1, 0);

        // Assert
        await Assert.That(move).IsFalse();
        await Assert.That(down).IsTrue();
        await Assert.That(h.Screen.IsOpen).IsFalse();
    }

    [Test]
    public async Task WheelAndMouseUpAreTakenWhileOpenOnly()
    {
        // Arrange
        var h = new Harness();

        // Act
        bool wheel = h.Screen.HandleWheel(-2, 0);
        bool up = h.Screen.HandleMouseUp(1, 1, 0);
        h.Screen.Close(0);
        bool closed = h.Screen.HandleWheel(1, 0) || h.Screen.HandleMouseUp(1, 1, 0) || h.Screen.HandleMouseDown(1, 1, 0)
            || h.Screen.HandleMouseMove(1, 1, 0) || h.Screen.HandleKey(Sdl.K_ESCAPE, 0, 0);

        // Assert
        await Assert.That(wheel && up).IsTrue();
        await Assert.That(closed).IsFalse();
        await Assert.That(h.Screen.IsAnimating(100_000)).IsFalse();
    }
}
