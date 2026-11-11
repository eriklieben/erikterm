using ErikTerm.App.Native;
using ErikTerm.Core.Images;

namespace ErikTerm.Tests;

public class WindowIconTests
{
    private static byte Alpha(DecodedImage image, int x, int y) => image.Rgba[(y * 128 + x) * 4 + 3];

    [Test]
    public async Task EmbeddedIconDecodesTo128Pixels()
    {
        // Act
        var image = WindowIcon.Load();

        // Assert
        var icon = await Assert.That(image).IsNotNull();
        await Assert.That(icon.Width).IsEqualTo(128);
        await Assert.That(icon.Height).IsEqualTo(128);
        await Assert.That(icon.Rgba.Length).IsEqualTo(128 * 128 * 4);
    }

    [Test]
    public async Task EmbeddedIconHasATransparentMargin()
    {
        // Act
        var image = WindowIcon.Load();

        // Assert
        var icon = await Assert.That(image).IsNotNull();
        await Assert.That(Alpha(icon, 0, 0)).IsEqualTo((byte)0); // the tile sits inside a transparent margin
        await Assert.That(Alpha(icon, 127, 127)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task EmbeddedIconTileIsOpaque()
    {
        // Act
        var image = WindowIcon.Load();

        // Assert
        var icon = await Assert.That(image).IsNotNull();
        await Assert.That(Alpha(icon, 64, 100)).IsEqualTo((byte)255); // and is opaque
    }
}
