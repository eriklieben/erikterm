using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class WindowDragHitTestTests
{
    // A 400x300 window with a 5 px grab border.
    private const float W = 400, H = 300, M = 5;

    private static WindowGrab At(float x, float y, bool chrome = false) =>
        WindowDragHitTest.Classify(W, H, M, x, y, chrome);

    [Test]
    [Arguments(0f, 0f, WindowGrab.ResizeTopLeft)]
    [Arguments(200f, 0f, WindowGrab.ResizeTop)]
    [Arguments(399f, 0f, WindowGrab.ResizeTopRight)]
    [Arguments(399f, 150f, WindowGrab.ResizeRight)]
    [Arguments(399f, 299f, WindowGrab.ResizeBottomRight)]
    [Arguments(200f, 299f, WindowGrab.ResizeBottom)]
    [Arguments(0f, 299f, WindowGrab.ResizeBottomLeft)]
    [Arguments(0f, 150f, WindowGrab.ResizeLeft)]
    public async Task BordersResize(float x, float y, WindowGrab expected)
    {
        await Assert.That(At(x, y)).IsEqualTo(expected);
    }

    [Test]
    public async Task EmptyChromeDragsTheWindow()
    {
        await Assert.That(At(200, 20, chrome: true)).IsEqualTo(WindowGrab.Move);
    }

    [Test]
    public async Task ChromeDoesNotSwallowTheBorder()
    {
        // The top edge of the tab bar is still the window's resize border.
        await Assert.That(At(200, 2, chrome: true)).IsEqualTo(WindowGrab.ResizeTop);
    }

    [Test]
    public async Task TerminalContentIsLeftAlone()
    {
        // Anything the caller doesn't report as empty chrome keeps its clicks: selection, links, the panes.
        await Assert.That(At(200, 150)).IsEqualTo(WindowGrab.None);
    }

    [Test]
    public async Task NoBorderWhenNotResizable()
    {
        await Assert.That(WindowDragHitTest.Classify(W, H, M, 0, 0, onEmptyChrome: false, resizable: false))
            .IsEqualTo(WindowGrab.None);
    }

    [Test]
    public async Task MarginCannotSwallowATinyWindow()
    {
        // 9 px tall with a 5 px margin: clamped to 3, so the middle row is still reachable.
        await Assert.That(WindowDragHitTest.Classify(9, 9, M, 4, 4, onEmptyChrome: true)).IsEqualTo(WindowGrab.Move);
    }

    [Test]
    [Arguments(-1f, 150f)]
    [Arguments(400f, 150f)]
    [Arguments(200f, 300f)]
    public async Task PointsOutsideTheWindowAreNotBorders(float x, float y)
    {
        await Assert.That(At(x, y)).IsEqualTo(WindowGrab.None);
    }

    [Test]
    public async Task DegenerateSizeIsInert()
    {
        await Assert.That(WindowDragHitTest.Classify(0, 0, M, 0, 0, onEmptyChrome: true)).IsEqualTo(WindowGrab.None);
    }
}
