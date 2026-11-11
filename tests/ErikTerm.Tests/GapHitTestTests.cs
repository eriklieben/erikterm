using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class GapHitTestTests
{
    // A side-by-side split: left pane [0,100), gap [100,109), right pane from 109; hairline at x = 104.
    private static readonly RectI Gap = new(100, 0, 9, 200);
    private const float Line = 1, Slop = 3;

    private static bool Hit(float x, float y = 50, bool live = false, SplitAxis axis = SplitAxis.Horizontal) =>
        GapHitTest.HitsDivider(Gap, axis, Line, Slop, x, y, live);

    [Test]
    public async Task HairlineMatchesRenderer()
    {
        // Act
        float end = GapHitTest.HairlineEnd(Gap, Line);

        // Assert
        await Assert.That(end).IsEqualTo(105);
    }

    [Test]
    [Arguments(97f, 50f, true)]
    [Arguments(104f, 50f, true)]
    [Arguments(108f, 50f, true)]
    [Arguments(111.9f, 50f, true)]
    [Arguments(96.9f, 50f, false)]
    [Arguments(112f, 50f, false)]
    [Arguments(104f, 204f, false)] // past the end
    public async Task WithoutAGutterTheWholeGapPlusSlopIsTheDivider(float x, float y, bool expected)
    {
        // Act
        bool hit = Hit(x, y);

        // Assert
        await Assert.That(hit).IsEqualTo(expected);
    }

    [Test]
    [Arguments(97f, true)]    // slop into the left pane
    [Arguments(104f, true)]   // the hairline itself
    [Arguments(105f, false)]  // right of the hairline: gutter
    [Arguments(108.5f, false)]
    [Arguments(110f, false)]  // the right pane's text, not the divider's slop
    public async Task ALiveGutterTakesTheGapPastTheHairline(float x, bool expected)
    {
        // Act
        bool hit = Hit(x, live: true);

        // Assert
        await Assert.That(hit).IsEqualTo(expected);
    }

    [Test]
    public async Task StackedSplitsIgnoreTheGutter()
    {
        // Arrange
        var gap = new RectI(0, 100, 200, 9);

        // Act
        bool hit = GapHitTest.HitsDivider(gap, SplitAxis.Vertical, Line, Slop, 50, 107, true);

        // Assert
        await Assert.That(hit).IsTrue();
    }

    // Scale 2: gap 18, hairline 2px at 208..210.
    private static readonly RectI HiDpiGap = new(200, 0, 18, 400);

    [Test]
    public async Task HiDpiHairlineIsTwoPixels()
    {
        // Act
        float end = GapHitTest.HairlineEnd(HiDpiGap, 2);

        // Assert
        await Assert.That(end).IsEqualTo(210);
    }

    [Test]
    [Arguments(209.5f, true)]
    [Arguments(210f, false)] // 8px of gutter
    public async Task HiDpiGutterStartsAfterTheHairline(float x, bool expected)
    {
        // Act
        bool hit = GapHitTest.HitsDivider(HiDpiGap, SplitAxis.Horizontal, 2, 6, x, 10, true);

        // Assert
        await Assert.That(hit).IsEqualTo(expected);
    }
}
