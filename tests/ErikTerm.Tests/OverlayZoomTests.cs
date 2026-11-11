using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class OverlayZoomTests
{
    // 12 pt at scale 1: 16 px of text.
    private const float UiPx = 16, Scale = 1, BigW = 4000, BigH = 3000;

    [Test]
    public async Task AtZoomOneNothingChanges()
    {
        // Act
        float z = OverlayZoom.For(1, 1000, 640, UiPx, Scale);

        // Assert
        await Assert.That(z).IsEqualTo(1f);
    }

    [Test]
    public async Task ZoomGrowsTheOverlaysInARoomyWindow()
    {
        // Act
        float z = OverlayZoom.For(2, BigW, BigH, UiPx, Scale);

        // Assert
        await Assert.That(z).IsEqualTo(2f);
    }

    [Test]
    public async Task AHighZoomIsLimitedByTheWindowHeight()
    {
        // Act
        float z = OverlayZoom.For(4, 2000, 1000, UiPx, Scale);

        // Assert: 1000 px holds 34 lines of 16 px text at most 1.8x.
        await Assert.That(z).IsEqualTo(1.8f).Within(0.001f);
    }

    [Test]
    public async Task AHighZoomIsLimitedByTheWindowWidth()
    {
        // Act
        float z = OverlayZoom.For(4, 700, 3000, UiPx, Scale);

        // Assert: the 508 pt dialog minimum needs the width first.
        await Assert.That(z).IsEqualTo(1.2f).Within(0.001f);
    }

    [Test]
    public async Task TheZoomedOverlaysFitTheWindowHeight()
    {
        // Arrange
        const float height = 1000;

        // Act
        float z = OverlayZoom.For(4, 2000, height, UiPx, Scale);

        // Assert
        await Assert.That(UiPx * z * OverlayZoom.FitHeightLines).IsLessThanOrEqualTo(height);
    }

    [Test]
    public async Task ASmallWindowKeepsTheSizeOfZoomOne()
    {
        // Act
        float z = OverlayZoom.For(4, 400, 300, UiPx, Scale);

        // Assert
        await Assert.That(z).IsEqualTo(1f);
    }

    [Test]
    public async Task ZoomingOutStopsAtALegibleSize()
    {
        // Act
        float z = OverlayZoom.For(0.4f, BigW, BigH, UiPx, Scale);

        // Assert: 9 pt of 12 pt.
        await Assert.That(z).IsEqualTo(0.75f).Within(0.001f);
    }

    [Test]
    public async Task AZoomAboveTheFloorIsFollowed()
    {
        // Act
        float z = OverlayZoom.For(0.9f, BigW, BigH, UiPx, Scale);

        // Assert
        await Assert.That(z).IsEqualTo(0.9f).Within(0.001f);
    }

    [Test]
    public async Task AnAlreadySmallPaletteFontIsNotShrunk()
    {
        // Arrange: command_palette_font_size = 6.
        const float small = 8;

        // Act
        float z = OverlayZoom.For(0.4f, BigW, BigH, small, Scale);

        // Assert
        await Assert.That(z).IsEqualTo(1f);
    }

    [Test]
    public async Task TheDisplayScaleDoesNotChangeTheFactor()
    {
        // Act
        float z = OverlayZoom.For(0.4f, BigW * 2, BigH * 2, UiPx * 2, 2);

        // Assert
        await Assert.That(z).IsEqualTo(0.75f).Within(0.001f);
    }

    [Test]
    public async Task ResizingMovesTheLimitInSteps()
    {
        // Act
        float a = OverlayZoom.For(4, 2000, 1000, UiPx, Scale);
        float b = OverlayZoom.For(4, 2000, 1005, UiPx, Scale);

        // Assert
        await Assert.That(a).IsEqualTo(b);
    }
}
