using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class WindowControlsLayoutTests
{
    [Test]
    [Arguments(false, false, true)]  // borderless on Windows / macOS: no frame, so the window draws its own chrome
    [Arguments(true, false, false)]  // decorated: the system frame does it
    [Arguments(false, true, false)]  // borderless on Linux: the compositor places and sizes windows
    [Arguments(true, true, false)]
    public async Task OnlyABorderlessWindowOffLinuxDrawsItsOwnChrome(bool decorations, bool linux, bool expected)
    {
        // Act
        bool selfDrawn = WindowControlsLayout.SelfDrawnChrome(decorations, linux);

        // Assert
        await Assert.That(selfDrawn).IsEqualTo(expected);
    }

    // A 400 pt wide window, a 30 pt button, a 26 pt strip inset 6 from the right: x from 304 to 394.
    private const float W = 400, BtnW = 30, H = 26, Inset = 6;

    private static RectF Strip() => WindowControlsLayout.Strip(W, 4, BtnW, H, Inset);

    [Test]
    public async Task StripIsRightAligned()
    {
        // Act
        var s = Strip();

        // Assert
        await Assert.That(s.Right).IsEqualTo(W - Inset);
        await Assert.That(s.W).IsEqualTo(BtnW * WindowControlsLayout.Count);
        await Assert.That(s.Y).IsEqualTo(4f);
        await Assert.That(s.H).IsEqualTo(H);
    }

    [Test]
    public async Task ButtonsRunLeftToRightWithCloseLast()
    {
        var s = Strip();

        var min = WindowControlsLayout.Button(s, (int)WindowControl.Minimize);
        var max = WindowControlsLayout.Button(s, (int)WindowControl.Maximize);
        var close = WindowControlsLayout.Button(s, (int)WindowControl.Close);

        await Assert.That(min.X).IsEqualTo(s.X);
        await Assert.That(max.X).IsEqualTo(min.Right);
        await Assert.That(close.Right).IsEqualTo(s.Right);
    }

    [Test]
    [Arguments(305f, WindowControl.Minimize)]
    [Arguments(333f, WindowControl.Minimize)]
    [Arguments(335f, WindowControl.Maximize)]
    [Arguments(365f, WindowControl.Close)]
    [Arguments(393f, WindowControl.Close)]
    public async Task HitTestPicksTheButtonUnderThePoint(float x, WindowControl expected)
    {
        await Assert.That(WindowControlsLayout.HitTest(Strip(), x, 10)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(303f, 10f)]  // left of the strip: the tab bar's empty area, which drags the window
    [Arguments(395f, 10f)]  // in the inset, past the last button
    [Arguments(350f, 40f)]  // below the strip
    [Arguments(350f, -1f)]  // above it
    public async Task HitTestMissesEverythingElse(float x, float y)
    {
        await Assert.That(WindowControlsLayout.HitTest(Strip(), x, y)).IsEqualTo(WindowControl.None);
    }

    [Test]
    public async Task AnEmptyStripIsNeverHit()
    {
        await Assert.That(WindowControlsLayout.HitTest(new RectF(0, 0, 0, 0), 0, 0)).IsEqualTo(WindowControl.None);
    }

    [Test]
    [Arguments(10f, 5f, true)]
    [Arguments(399f, 0f, true)]
    [Arguments(10f, 30f, false)]   // below the band
    [Arguments(-1f, 5f, false)]    // outside the window (the pointer left it)
    [Arguments(400f, 5f, false)]
    public async Task RevealBandCoversTheFullTopEdge(float x, float y, bool expected)
    {
        await Assert.That(WindowControlsLayout.InRevealBand(W, 30, x, y)).IsEqualTo(expected);
    }
}
