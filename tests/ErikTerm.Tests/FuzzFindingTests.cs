using System.Text;
using ErikTerm.Core;
using ErikTerm.Core.Images;

namespace ErikTerm.Tests;

/// <summary>
/// Regression tests for what fuzz/ErikTerm.Fuzz found (docs/development/fuzzing.md). Each input is a minimal one
/// written for the bug, not the fuzzer's.
/// </summary>
public class FuzzFindingTests
{
    private static void Feed(Terminal t, string s) => t.Feed(Encoding.UTF8.GetBytes(s));

    /// <summary>A kitty a=T of one RGB pixel with the given placement keys.</summary>
    private static string KittyPixel(string keys) => $"\e_Ga=T,f=24,s=1,v=1,q=2,{keys};{Convert.ToBase64String([1, 2, 3])}\e\\";

    [Test]
    [Arguments("c=2147483647")]
    [Arguments("c=4294967295")]
    public async Task Kitty_placement_wider_than_any_screen_leaves_the_cursor_on_the_screen(string keys)
    {
        // Arrange
        var t = new Terminal(20, 5);
        Feed(t, "\e[1;6H");

        // Act
        Feed(t, KittyPixel(keys));
        Feed(t, "ab");

        // Assert
        await Assert.That(t.CursorX).IsBetween(0, 19);
        await Assert.That(t.CursorY).IsBetween(0, 4);
    }

    [Test]
    public async Task Kitty_placement_taller_than_any_screen_moves_the_cursor_in_bounded_time()
    {
        // Arrange
        var t = new Terminal(20, 5);

        // Act
        var cpu = ThreadCpuTime.Measure(() =>
        {
            Feed(t, KittyPixel("r=2147483647"));
            Feed(t, "ab");
        });

        // Assert
        await Assert.That(t.CursorY).IsEqualTo(4);
        await Assert.That(cpu).IsLessThan(TimeSpan.FromSeconds(2)).Because("one line feed per requested row");
    }

    /// <summary>Bytes allocated on this thread while decoding <paramref name="sixel"/>, and the image (null when refused).</summary>
    private static (long Allocated, DecodedImage? Image) DecodeSixel(string sixel)
    {
        byte[] data = Encoding.ASCII.GetBytes(sixel);
        long before = GC.GetAllocatedBytesForCurrentThread();
        DecodedImage? image;
        try { image = Sixel.Decode(data); }
        catch (ImageDecodeException) { image = null; }
        return (GC.GetAllocatedBytesForCurrentThread() - before, image);
    }

    /// <summary>
    /// The most decoding a sixel image may allocate in all, 4 bytes a pixel: the canvases it grows through (three times
    /// <see cref="ImageLimits.MaxPixels"/>) and the RGBA copy. Before the fix it was gigabytes.
    /// </summary>
    private const long SixelAllocationBound = 4 * ImageLimits.MaxPixels * 4;

    [Test]
    public async Task Sixel_growing_one_column_at_a_time_near_the_pixel_limit_allocates_a_bounded_amount()
    {
        // A tall declared canvas, painted wide enough that doubling its width no longer fits, then one column more at a time.

        // Arrange
        string sixel = "\"1;1;8;9393!3332~" + new string('~', 40);

        // Act
        var (allocated, image) = DecodeSixel(sixel);

        // Assert
        await Assert.That(image).IsNotNull();
        await Assert.That(image!.Width).IsEqualTo(3372);
        await Assert.That(image.Height).IsEqualTo(9393);
        await Assert.That(allocated).IsLessThan(SixelAllocationBound);
    }

    [Test]
    public async Task Sixel_image_without_raster_attributes_still_grows_to_the_pixel_limit()
    {
        // The budget on growing must not clip a plain image: one band 7000 wide, then bands below it up to 7140 rows.

        // Arrange
        var sb = new StringBuilder("!7000~");
        for (int band = 1; band < 7140 / 6; band++) sb.Append("-!7000~");

        // Act
        var (_, image) = DecodeSixel(sb.ToString());

        // Assert
        await Assert.That(image).IsNotNull();
        await Assert.That(image!.Width).IsEqualTo(7000);
        await Assert.That(image.Height).IsEqualTo(7140);
    }

    [Test]
    public async Task Sixel_growing_by_turns_in_width_and_height_near_the_pixel_limit_allocates_a_bounded_amount()
    {
        // Each step paints one band lower and one column wider than anything before it, near half the pixel limit.

        // Arrange
        var sb = new StringBuilder("\"1;1;5000;5000").Append('-', 5000 / 6);
        for (int k = 1; k <= 40; k++) sb.Append("-!").Append(5000 + k).Append('~');

        // Act
        var (allocated, image) = DecodeSixel(sb.ToString());

        // Assert
        await Assert.That(image).IsNotNull();
        await Assert.That(allocated).IsLessThan(SixelAllocationBound);
    }

    /// <summary>A GIF89a with a two-color table and one frame, whose LZW data is one pixel of index 1.</summary>
    private static byte[] GifWithFrame(int screenW, int screenH, int frameW, int frameH, bool interlaced = false)
    {
        var gif = new List<byte>("GIF89a"u8.ToArray());
        gif.AddRange([(byte)screenW, (byte)(screenW >> 8), (byte)screenH, (byte)(screenH >> 8), 0x80, 0, 0]);
        gif.AddRange([0, 0, 0, 255, 255, 255]);
        gif.AddRange([0x2C, 0, 0, 0, 0, (byte)frameW, (byte)(frameW >> 8), (byte)frameH, (byte)(frameH >> 8), (byte)(interlaced ? 0x40 : 0)]);
        gif.AddRange([2, 2, 0x4C, 0x01, 0, 0x3B]); // clear, 1, end
        return [.. gif];
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Gif_frame_far_larger_than_its_screen_allocates_for_the_screen_only(bool interlaced)
    {
        // Arrange
        byte[] gif = GifWithFrame(2, 1, 7000, 7000, interlaced);
        long before = GC.GetAllocatedBytesForCurrentThread();

        // Act
        var image = Gif.Decode(gif);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        await Assert.That(image.Width).IsEqualTo(2);
        await Assert.That(image.Height).IsEqualTo(1);
        await Assert.That(image.Rgba).IsEquivalentTo(new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 }, CollectionOrdering.Matching);
        await Assert.That(allocated).IsLessThan(1L << 20).Because("the frame's 49 MB of indices were held before painting");
    }

    // ---- Found next to them --------------------------------------------------------------------------------------

    private static byte[] JpegSegment(byte marker, params byte[] body) =>
        [0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];

    /// <summary>
    /// A progressive grayscale JPEG of <paramref name="size"/>² with a restart interval of one MCU and
    /// <paramref name="scans"/> DC scans that each carry a single byte of data.
    /// </summary>
    private static byte[] JpegOfEmptyScans(int size, int scans)
    {
        byte[] oneCode = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]; // one code of length 1, symbol 0
        var jpg = new List<byte> { 0xFF, 0xD8 };
        jpg.AddRange(JpegSegment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]));
        jpg.AddRange(JpegSegment(0xC2, 8, (byte)(size >> 8), (byte)size, (byte)(size >> 8), (byte)size, 1, 1, 0x11, 0));
        jpg.AddRange(JpegSegment(0xC4, [0x00, .. oneCode, 0x10, .. oneCode]));
        jpg.AddRange(JpegSegment(0xDD, 0, 1));
        for (int i = 0; i < scans; i++)
        {
            jpg.AddRange(JpegSegment(0xDA, 1, 1, 0x00, 0, 0, 0));
            jpg.Add(0);
        }
        jpg.AddRange([0xFF, 0xD9]);
        return [.. jpg];
    }

    [Test]
    public async Task Jpeg_scans_without_data_are_not_decoded_restart_interval_by_restart_interval()
    {
        // Arrange
        byte[] jpg = JpegOfEmptyScans(2048, 1000);
        DecodedImage? image = null;

        // Act
        var cpu = ThreadCpuTime.Measure(() => image = Jpeg.Decode(jpg));

        // Assert
        await Assert.That(image!.Width).IsEqualTo(2048);
        await Assert.That(cpu).IsLessThan(TimeSpan.FromSeconds(2)).Because("16 KB of scan headers decoded 65 million MCUs of padding");
    }
}
