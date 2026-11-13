using ErikTerm.App.Rendering;
using ErikTerm.Config;

namespace ErikTerm.Tests;

/// <summary>minimum_contrast: WCAG ratios and the foreground adjustment (Rendering/Contrast.cs).</summary>
public class MinimumContrastTests
{
    [Test]
    public async Task BlackOnWhiteIsTwentyOneToOne()
    {
        // Act
        float ratio = Contrast.Ratio(0x000000, 0xFFFFFF);

        // Assert
        await Assert.That(ratio).IsEqualTo(21f).Within(0.01f);
    }

    [Test]
    public async Task EnoughContrastIsLeftAlone()
    {
        // Act
        uint fg = Contrast.Adjust(0xCAD3F5, 0x24273A, 4.5f);

        // Assert
        await Assert.That(fg).IsEqualTo(0xCAD3F5u);
    }

    [Test]
    [Arguments(0x494D64u, 0x24273Au, 4.5f)] // Catppuccin's "black" on its background
    [Arguments(0x1D1F21u, 0x1D1F21u, 4.5f)] // a scheme whose black is the background
    [Arguments(0x777777u, 0x888888u, 3.0f)]
    [Arguments(0xEEEEEEu, 0xFFFFFFu, 7.0f)] // light theme
    [Arguments(0x3060FFu, 0x0000AAu, 4.5f)]
    public async Task TooLittleContrastIsRaisedToTheMinimum(uint fg, uint bg, float minimum)
    {
        // Act
        uint adjusted = Contrast.Adjust(fg, bg, minimum);

        // Assert
        await Assert.That(Contrast.Ratio(adjusted, bg)).IsGreaterThanOrEqualTo(minimum - 0.01f);
    }

    [Test]
    public async Task LightTextOnDarkGetsLighter()
    {
        // Act
        uint adjusted = Contrast.Adjust(0x494D64, 0x24273A, 4.5f);

        // Assert
        await Assert.That(Contrast.Luminance(adjusted)).IsGreaterThan(Contrast.Luminance(0x494D64));
    }

    [Test]
    public async Task AdjustedColorKeepsItsHue()
    {
        // Act: a dark blue on black brightens but stays blue.
        uint adjusted = Contrast.Adjust(0x101060, 0x000000, 4.5f);

        // Assert
        await Assert.That(Colors.B(adjusted)).IsGreaterThan(Colors.R(adjusted));
        await Assert.That(Colors.B(adjusted)).IsGreaterThan(Colors.G(adjusted));
    }

    [Test]
    public async Task ItGoesDarkerWhenLighterCantReachTheMinimum()
    {
        // Arrange: on a light gray nothing lighter has 4.5:1, so a light foreground turns dark.
        uint bg = 0x999999, fg = 0xAAAAAA;

        // Act
        uint adjusted = Contrast.Adjust(fg, bg, 4.5f);

        // Assert
        await Assert.That(Contrast.Luminance(adjusted)).IsLessThan(Contrast.Luminance(bg));
        await Assert.That(Contrast.Ratio(adjusted, bg)).IsGreaterThanOrEqualTo(4.49f);
    }

    [Test]
    public async Task UnreachableMinimumGivesBlackOrWhite()
    {
        // Act: nothing has 15:1 against mid gray.
        uint adjusted = Contrast.Adjust(0x808080, 0x777777, 15f);

        // Assert
        await Assert.That(adjusted is 0x000000u or 0xFFFFFFu).IsTrue();
    }

    [Test]
    [Arguments(0x2500, true)]   // ─ box drawing
    [Arguments(0x2588, true)]   // █ full block
    [Arguments(0x259F, true)]
    [Arguments(0xE0B0, true)]   // powerline
    [Arguments(0x1FB00, true)]  // legacy computing
    [Arguments((int)'a', false)]
    [Arguments(0x2800, false)]  // braille (graphs) is text
    public async Task GraphicsCharactersKeepTheirColors(int rune, bool expected)
    {
        // Act
        bool graphic = Contrast.IsGraphic(rune);

        // Assert
        await Assert.That(graphic).IsEqualTo(expected);
    }

    [Test]
    public async Task CacheOffLeavesColorsAlone()
    {
        // Arrange
        var cache = new ContrastCache();
        cache.Configure(1);

        // Act
        uint fg = cache.Apply(0x494D64, 0x24273A);

        // Assert
        await Assert.That(fg).IsEqualTo(0x494D64u);
    }

    [Test]
    public async Task CacheGivesTheAdjustedColor()
    {
        // Arrange
        var cache = new ContrastCache();
        cache.Configure(4.5f);
        uint first = cache.Apply(0x494D64, 0x24273A);

        // Act
        uint again = cache.Apply(0x494D64, 0x24273A);

        // Assert
        await Assert.That(again).IsEqualTo(Contrast.Adjust(0x494D64, 0x24273A, 4.5f));
        await Assert.That(again).IsEqualTo(first);
    }

    [Test]
    public async Task CacheDoesNotAllocate()
    {
        // Arrange
        var cache = new ContrastCache();
        cache.Configure(4.5f);
        for (uint c = 0; c < 64; c++) cache.Apply(c * 0x030303, 0x24273A);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 10; round++)
            for (uint c = 0; c < 64; c++) cache.Apply(c * 0x030303, 0x24273A);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        await Assert.That(allocated).IsEqualTo(0L);
    }

    // ---- The setting --------------------------------------------------------------------------

    [Test]
    public async Task MinimumContrastIsLeftToTheSchemeByDefault()
    {
        // Act
        var s = SettingsLoader.Parse("").Settings;

        // Assert: unset, so the scheme's default applies (BuiltInSchemeContrastTests)
        await Assert.That(s.MinimumContrast).IsNull();
        await Assert.That(s.MinimumContrastFor(s.Colors)).IsEqualTo(ColorSchemes.BuiltInMinimumContrast);
    }

    [Test]
    public async Task MinimumContrastIsRead()
    {
        // Act
        var s = SettingsLoader.Parse("minimum_contrast = 4.5").Settings;

        // Assert
        await Assert.That(s.MinimumContrast).IsEqualTo(4.5);
    }

    [Test]
    [Arguments("minimum_contrast = 0.5")]
    [Arguments("minimum_contrast = 30")]
    public async Task MinimumContrastOutOfRangeIsReported(string toml)
    {
        // Act
        var result = SettingsLoader.Parse(toml);

        // Assert
        await Assert.That(result.Diagnostics).Contains(d => d.Contains("between 1 and 21", StringComparison.Ordinal));
    }
}
