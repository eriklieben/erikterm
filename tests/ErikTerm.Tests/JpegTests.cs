using ErikTerm.Core.Images;

namespace ErikTerm.Tests;

/// <summary>
/// JPEG decoder tests. The fixtures were made with ImageMagick 7 (libjpeg-turbo) from a 37x23 plasma/gradient
/// image; the expected pixels are ImageMagick's own decode (<c>-define jpeg:fancy-upsampling=off ... rgb:-</c>,
/// with <c>-auto-orient</c> for the EXIF ones). Derived fixtures were patched by hand: exif6/exif3 got an APP1 Exif
/// segment with orientation 6/3, q16 has its quantization tables rewritten as 16-bit and SOF0 changed to SOF1, and
/// fill has extra FF fill bytes before the SOS, every RSTn and EOI.
/// </summary>
public class JpegTests
{
    private static readonly string[] AllFixtures =
        ["b444", "b422", "b420", "b440", "b411", "p420", "p444", "r420", "pr420", "gray", "grayp", "cmyk", "exif6", "exif3", "q16", "fill"];

    public static IEnumerable<string> FixtureNames() => AllFixtures;

    /// <summary>Every fixture with its size and the largest channel difference allowed against libjpeg's decode.</summary>
    public static IEnumerable<(string Name, int Width, int Height, int Tolerance)> Fixtures() =>
    [
        ("b444", 37, 23, 1),   // baseline 4:4:4
        ("b422", 37, 23, 1),   // baseline 4:2:2
        ("b420", 37, 23, 1),   // baseline 4:2:0
        ("b440", 37, 23, 1),   // baseline 4:4:0 (1x2)
        ("b411", 37, 23, 1),   // baseline 4:1:1 (4x1)
        ("p420", 37, 23, 1),   // progressive 4:2:0
        ("p444", 37, 23, 1),   // progressive 4:4:4
        ("r420", 37, 23, 1),   // baseline 4:2:0, restart interval 1
        ("pr420", 37, 23, 1),  // progressive 4:2:0, restart interval 1
        ("gray", 33, 17, 1),   // grayscale
        ("grayp", 33, 17, 1),  // progressive grayscale
        ("cmyk", 20, 16, 3),   // Adobe YCCK
        ("exif6", 23, 37, 1),  // EXIF orientation 6 (rotate 90 CW)
        ("exif3", 37, 23, 1),  // EXIF orientation 3 (rotate 180)
        ("q16", 37, 23, 1),    // SOF1 with 16-bit quantization tables
        ("fill", 37, 23, 1),   // fill bytes before markers
    ];

    public static IEnumerable<(string Name, int Width, int Height)> FixtureSizes() =>
        Fixtures().Select(f => (f.Name, f.Width, f.Height));

    private static (byte[] Jpg, byte[] Rgb) Fixture(string name)
    {
        (string jpg, string rgb) = name switch
        {
            "b444" => (B444Jpg, B444Rgb),
            "b422" => (B422Jpg, B422Rgb),
            "b420" => (B420Jpg, B420Rgb),
            "b440" => (B440Jpg, B440Rgb),
            "b411" => (B411Jpg, B411Rgb),
            "p420" => (P420Jpg, P420Rgb),
            "p444" => (P444Jpg, P444Rgb),
            "r420" => (R420Jpg, R420Rgb),
            "pr420" => (Pr420Jpg, Pr420Rgb),
            "gray" => (GrayJpg, GrayRgb),
            "grayp" => (GraypJpg, GraypRgb),
            "cmyk" => (CmykJpg, CmykRgb),
            "exif6" => (Exif6Jpg, Exif6Rgb),
            "exif3" => (Exif3Jpg, Exif3Rgb),
            "q16" => (Q16Jpg, Q16Rgb),
            "fill" => (FillJpg, FillRgb),
            _ => throw new ArgumentException(name),
        };
        return (Convert.FromBase64String(jpg), Convert.FromBase64String(rgb));
    }

    /// <summary>b444 with the frame header's height and width patched (both <paramref name="size"/>).</summary>
    private static byte[] B444Sized(int size)
    {
        var jpg = Fixture("b444").Jpg;
        int sof = IndexOfMarker(jpg, 0xC0);
        jpg[sof + 5] = (byte)(size >> 8); jpg[sof + 6] = (byte)(size & 0xFF); // height
        jpg[sof + 7] = (byte)(size >> 8); jpg[sof + 8] = (byte)(size & 0xFF); // width
        return jpg;
    }

    [Test]
    public async Task Iterm2_and_kitty_show_jpegs()
    {
        // Arrange
        var (jpg, _) = Fixture("p420");
        var t = new ErikTerm.Core.Terminal(40, 10) { CellPixelWidth = 10, CellPixelHeight = 20 };

        // Act
        t.Feed(System.Text.Encoding.UTF8.GetBytes("\e]1337;File=inline=1;width=4;height=2:" + Convert.ToBase64String(jpg) + "\a"));

        // Assert
        var img = await Assert.That(t.Images.Images).HasSingleItem();
        await Assert.That((img.Width, img.Height)).IsEqualTo((37, 23)); // decoded, not the 48x32 "unsupported" box
    }

    [Test]
    public async Task ImageDecoderDecodesJpegs()
    {
        // Arrange
        var (jpg, _) = Fixture("p420");

        // Act
        var img = ImageDecoder.Decode(jpg);

        // Assert
        await Assert.That(img.Rgba.Length).IsEqualTo(37 * 23 * 4);
    }

    [Test]
    [MethodDataSource(nameof(FixtureSizes))]
    public async Task RecognizesAndSizesEveryFixture(string name, int width, int height)
    {
        // Arrange
        var (jpg, _) = Fixture(name);

        // Act
        bool isJpeg = Jpeg.IsJpeg(jpg);
        bool sized = Jpeg.TryGetSize(jpg, out int tw, out int th);

        // Assert
        await Assert.That(isJpeg).IsTrue();
        await Assert.That(sized).IsTrue();
        await Assert.That((tw, th)).IsEqualTo((width, height));
    }

    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task DecodesLikeLibjpeg(string name, int width, int height, int tolerance)
    {
        // Arrange
        var (jpg, rgb) = Fixture(name);

        // Act
        var img = Jpeg.Decode(jpg);
        int pixels = Math.Min(width * height, Math.Min(img.Rgba.Length / 4, rgb.Length / 3));
        int notOpaque = 0, max = 0;
        long sum = 0;
        for (int i = 0; i < pixels; i++)
        {
            if (img.Rgba[4 * i + 3] != 255) notOpaque++;
            for (int c = 0; c < 3; c++)
            {
                int diff = Math.Abs(img.Rgba[4 * i + c] - rgb[3 * i + c]);
                max = Math.Max(max, diff);
                sum += diff;
            }
        }
        double mean = (double)sum / (width * height * 3);

        // Assert
        await Assert.That(img.Width).IsEqualTo(width);
        await Assert.That(img.Height).IsEqualTo(height);
        await Assert.That(img.Rgba.Length).IsEqualTo(width * height * 4);
        await Assert.That(rgb.Length).IsEqualTo(width * height * 3);
        await Assert.That(notOpaque).IsEqualTo(0);
        await Assert.That(max).IsLessThanOrEqualTo(tolerance).Because($"{name}: max channel difference {max} (mean {mean:F3})");
        await Assert.That(mean).IsLessThanOrEqualTo(0.5).Because($"{name}: mean channel difference {mean:F3}");
    }

    [Test]
    public async Task ExifOrientationCanBeIgnored()
    {
        // Arrange
        var (jpg, _) = Fixture("exif6");
        var plain = Jpeg.Decode(Fixture("b444").Jpg);

        // Act
        var img = Jpeg.Decode(jpg, applyExifOrientation: false);

        // Assert
        await Assert.That((img.Width, img.Height)).IsEqualTo((37, 23));
        await Assert.That(img.Rgba).IsEquivalentTo(plain.Rgba, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("r420")]
    [Arguments("pr420")]
    [Arguments("fill")]
    public async Task ProgressiveAndRestartVariantsMatchBaseline(string variant)
    {
        // Arrange
        var baseline = Jpeg.Decode(Fixture("b420").Jpg).Rgba;

        // Act
        var rgba = Jpeg.Decode(Fixture(variant).Jpg).Rgba;

        // Assert
        await Assert.That(rgba).IsEquivalentTo(baseline, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("", false)]
    [Arguments("FFD8", false)]
    [Arguments("89504E47", false)] // a PNG
    [Arguments("FFD8FF", true)]
    public async Task IsJpegChecksTheSignature(string hex, bool expected)
    {
        // Act
        bool isJpeg = Jpeg.IsJpeg(Convert.FromHexString(hex));

        // Assert
        await Assert.That(isJpeg).IsEqualTo(expected);
    }

    [Test]
    [Arguments("FFD8FF")]   // the signature but no frame header
    [Arguments("01020304")] // not a JPEG
    public async Task TryGetSizeFailsWithoutAFrameHeader(string hex)
    {
        // Act
        bool sized = Jpeg.TryGetSize(Convert.FromHexString(hex), out _, out _);

        // Assert
        await Assert.That(sized).IsFalse();
    }

    [Test]
    public async Task TryGetSizeFailsWhenCutShortInsideTheFrameHeader()
    {
        // Arrange
        var jpg = Fixture("gray").Jpg;
        int sof = IndexOfMarker(jpg, 0xC0);

        // Act
        bool sized = Jpeg.TryGetSize(jpg.AsSpan(0, sof + 4), out _, out _);

        // Assert
        await Assert.That(sized).IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments("89504E470D0A1A0A")] // a PNG signature
    [Arguments("FFD8FFD9")]         // SOI then EOI: no frame
    public async Task RejectsNonJpeg(string hex)
    {
        // Arrange
        var data = Convert.FromHexString(hex);

        // Act
        var act = () => Jpeg.Decode(data);

        // Assert
        await Assert.That(act).ThrowsExactly<ImageDecodeException>();
    }

    [Test]
    public async Task TryGetSizeReportsHugeDimensionsFromTheHeader()
    {
        // Arrange
        var jpg = B444Sized(60000);

        // Act
        bool sized = Jpeg.TryGetSize(jpg, out int w, out int h);

        // Assert
        await Assert.That(sized).IsTrue();
        await Assert.That((w, h)).IsEqualTo((60000, 60000));
    }

    [Test]
    public async Task RejectsHugeDimensionsBeforeAllocating()
    {
        // Arrange
        var jpg = B444Sized(60000);

        // Act
        // Measured on this thread around the decode alone (no awaits in between).
        long before = GC.GetAllocatedBytesForCurrentThread();
        Exception? error = null;
        try { Jpeg.Decode(jpg); }
        catch (Exception ex) { error = ex; }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        await Assert.That(error).IsTypeOf<ImageDecodeException>();
        await Assert.That(error!.Message).Contains("too large");
        await Assert.That(allocated).IsLessThan(1 << 20);
    }

    [Test]
    public async Task RejectsDimensionsWithinMaxDimensionButOverMaxPixels()
    {
        // Arrange
        var jpg = B444Sized(30000);

        // Act
        var act = () => Jpeg.Decode(jpg);

        // Assert
        await Assert.That(act).ThrowsExactly<ImageDecodeException>();
    }

    [Test]
    [Arguments(0xC3, "lossless")]
    [Arguments(0xC9, "arithmetic")]
    [Arguments(0xCA, "arithmetic")]
    [Arguments(0xC5, "hierarchical")]
    public async Task RejectsUnsupportedProcesses(int sofMarker, string expected)
    {
        // Arrange
        var jpg = Fixture("b444").Jpg;
        jpg[IndexOfMarker(jpg, 0xC0) + 1] = (byte)sofMarker;

        // Act
        var act = () => Jpeg.Decode(jpg);

        // Assert
        var ex = await Assert.That(act).ThrowsExactly<ImageDecodeException>();
        await Assert.That(ex!.Message).Contains("unsupported");
        await Assert.That(ex.Message).Contains(expected);
    }

    [Test]
    public async Task Rejects12BitPrecision()
    {
        // Arrange
        var jpg = Fixture("b444").Jpg;
        jpg[IndexOfMarker(jpg, 0xC0) + 4] = 12;

        // Act
        var act = () => Jpeg.Decode(jpg);

        // Assert
        await Assert.That(act).ThrowsExactly<ImageDecodeException>().WithMessageContaining("unsupported");
    }

    [Test]
    [MethodDataSource(nameof(FixtureNames))]
    public async Task TruncatedInputFailsCleanlyOrDecodesPartially(string name)
    {
        // Arrange
        var jpg = Fixture(name).Jpg;
        bool sized = Jpeg.TryGetSize(jpg, out int w, out int h);

        // Act
        // Every prefix: an ImageDecodeException is fine, anything else escapes and fails the test.
        var wrongShape = new List<string>();
        for (int len = 0; len < jpg.Length; len++)
        {
            try
            {
                var img = Jpeg.Decode(jpg.AsSpan(0, len));
                if ((img.Width, img.Height) != (w, h) || img.Rgba.Length != w * h * 4)
                    wrongShape.Add($"{len} bytes: {img.Width}x{img.Height}, {img.Rgba.Length} bytes of RGBA");
            }
            catch (ImageDecodeException)
            {
            }
        }

        // Assert
        await Assert.That(sized).IsTrue();
        await Assert.That(wrongShape).IsEmpty();
    }

    [Test]
    public async Task CorruptedInputOnlyThrowsImageDecodeException()
    {
        // Arrange
        // One seeded generator across all fixtures, in order: the same corruptions every run.
        var rng = new Random(12345);

        // Act
        int decoded = 0, rejected = 0;
        var wrongLength = new List<string>();
        foreach (string name in AllFixtures)
        {
            var original = Fixture(name).Jpg;
            for (int iter = 0; iter < 300; iter++)
            {
                var jpg = (byte[])original.Clone();
                int flips = 1 + rng.Next(4);
                for (int f = 0; f < flips; f++)
                {
                    int at = rng.Next(jpg.Length);
                    jpg[at] = rng.Next(3) switch
                    {
                        0 => (byte)rng.Next(256),
                        1 => 0xFF,
                        _ => (byte)(jpg[at] ^ (1 << rng.Next(8))),
                    };
                }
                try
                {
                    var img = Jpeg.Decode(jpg);
                    if ((long)img.Width * img.Height * 4 != img.Rgba.Length) wrongLength.Add($"{name} #{iter}");
                    decoded++;
                }
                catch (ImageDecodeException)
                {
                    rejected++;
                }
            }
        }

        // Assert
        await Assert.That(wrongLength).IsEmpty();
        await Assert.That(decoded).IsGreaterThan(0).Because($"decoded {decoded}, rejected {rejected}");
        await Assert.That(rejected).IsGreaterThan(0).Because($"decoded {decoded}, rejected {rejected}");
    }

    [Test]
    [Arguments(0xDB, 0xFF, 0xFF)] // DQT that runs past the end
    [Arguments(0xDB, 0, 1)]       // DQT shorter than the length field itself
    [Arguments(0xC0, 0, 8)]       // frame header without its components
    public async Task BogusSegmentLengthsAreRejected(int marker, int lengthHigh, int lengthLow)
    {
        // Arrange
        var jpg = Fixture("b420").Jpg;
        int segment = IndexOfMarker(jpg, marker);
        jpg[segment + 2] = (byte)lengthHigh;
        jpg[segment + 3] = (byte)lengthLow;

        // Act
        var act = () => Jpeg.Decode(jpg);

        // Assert
        await Assert.That(act).ThrowsExactly<ImageDecodeException>();
    }

    [Test]
    [MethodDataSource(nameof(FixtureNames))]
    public async Task GarbageEntropyDataStillYieldsAnImage(string name)
    {
        // Arrange
        var jpg = Fixture(name).Jpg;
        int sos = IndexOfMarker(jpg, 0xDA);
        int start = sos + 2 + (jpg[sos + 2] << 8 | jpg[sos + 3]);
        var rng = new Random(7);
        for (int i = start; i < jpg.Length - 2; i++)
            if (jpg[i] != 0xFF && (i == 0 || jpg[i - 1] != 0xFF)) jpg[i] = (byte)rng.Next(0xFF);

        // Act
        bool sized = Jpeg.TryGetSize(jpg, out int w, out int h);
        (int Width, int Height)? decoded = null;
        try
        {
            var img = Jpeg.Decode(jpg);
            decoded = (img.Width, img.Height);
        }
        catch (ImageDecodeException)
        {
        }

        // Assert
        // Rejecting it is fine; an image, if there is one, has the header's size.
        await Assert.That(sized).IsTrue();
        await Assert.That(decoded ?? (w, h)).IsEqualTo((w, h));
    }

    /// <summary>Offset of the first FF xx marker among the header segments.</summary>
    private static int IndexOfMarker(byte[] jpg, int marker)
    {
        int pos = 2;
        while (pos + 4 <= jpg.Length && jpg[pos] == 0xFF)
        {
            if (jpg[pos + 1] == 0xFF) { pos++; continue; } // fill byte
            if (jpg[pos + 1] == marker) return pos;
            pos += 2 + (jpg[pos + 2] << 8 | jpg[pos + 3]);
        }
        throw new InvalidOperationException($"marker {marker:X2} not found");
    }

    // b444.jpg: 37x23, 816 bytes
    private const string B444Jpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIe
        JBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAAXACUD
        AREAAhEBAxEB/8QAGQAAAgMBAAAAAAAAAAAAAAAABQYCBwgD/8QAJxAAAgEDAwMEAwEAAAAAAAAAAQIDBAURABIhBjFBBxMiUQhhcYH/xAAZAQAC
        AwEAAAAAAAAAAAAAAAAEBQEDBwb/xAAmEQABBAIBAwMFAAAAAAAAAAABAAIDBBEhMQUS8BMUQSIyYYHR/9oADAMBAAIRAxEAPwBF9OLXd+q+oYbF
        YoDUVcwYgl9qoo5Zmbwo/wB8AZJAPJt6c97u0BM32NbV1Xn0S6ps9ilr1noLm8KF5IIGbfgDJK7lG7+dz4BOps9Dk7e4EK2v1Jw+ngFVq0lTC0VX
        Rs0ioCJqZEBLqccr53LjIHYgkYyQQlPTRICD+j58H+fkFlDckikG0et0qSxwz084eGRVZHU5BUjII5850qloFn3NXVR3mvaMHOUchki2ZGFPnGDo
        L2BPCLZdG9pU/C7qmz2r1IqqW5yxUjXGiNPTzSMFX3A6tsJPA3bTj7IA7ka3OxQ9AkhYxDL3DZWx+obzbbBZqm73eripaOnQu8kjAdh2H2x7ADkn
        gaDRTWlxwFii23D36l541VY2kZ1j4+IJJxpO6qMnSdlgAx3ZK7VFQLGzVkIZLfPIWqFUfGCRjzJx2Vict9H5Hux1WabJhg8qYbDoiR8eeeFHbdVE
        RNkqR4xpVL0zeE0bccBhUNZKUB++B51tVqswjIWTQzO4bymcvVSrFFNUSyxoMRrJIWA/Q+tJHxRk4ITStPI3g7RW3E07FlyCOODqoVI+MaRkXUZT
        oo4tXJJShT8lbgg6FnoRt21E+7eDgqdmp6imWWBTG1IuPY5O9RzlD4IHGDnODgjjLLzXaOUbFeAyCv/Z
        """;

    private const string B444Rgb = """
        bEsEclEKc1INbk8McFUOgmwhgXAifHAeh3EmemMVkW0fnmUYplgNsl8ToFwJlmAIkV4FqHAdp2kcn2IHs3kAxXwN0G8g228K22UD22cE6nkT428I
        53II5HEE6IAN5YYO8nYG7nYF7HcG8XcI+XkMZ0oIUTMAdFYWe10dalEOcl4ZbFsTbF4TeVkgimwuhmMhn24rrm8soF0WpGwfnXMfonkhl20LlWcH
        rXsym2kItXUew3oA03Udy2QHz2gL1GwNzF8C4W4R4W8P33MQ3ngT8n4R738P74EQ9IIS/IIVZk0UVDsCYkoOYkoMc1sdcl8ddmQibl8cgF0nelsi
        h2ssfl8ckmoiqX4wkmwXknMZkngZk3cUmHgljnMAJgAAq4UKtHcksmsAxm4V0nYf0m8azGMQ4XQi3m8e224b33Uh7YAX7IMY64UZ7oQX84AVTTwG
        Z1YeYE0SX0sOYU8Ra1sda10gbGAkdWEmgHAyeW4sgngwiHgqiXMemH0iknYVinUakHUwhG4IlX01f2sINQoErYQYrnUcvmoUym8czWoZz2gZ02wd
        ymUV0Gka2XIj64Ag64Uj64kk74kk8oUgWU0bUUQPST0DW00QXlIWX1IbaF4rVUsabWxAk5JmoaJ2p6Z2kItUgXM2knc0gl0Wg2Qrl3g/mYMgloEw
        kX4ygGMrXi4Xr38rzXgl0XIi0msc3nco0XIeyHAZ2X8n4YMs33cg4H0i5YMo6oUp8YMqRzsTVEkcU0kWTkQPSD4LYFktWlQy1tG0+f/2/P/6/f75
        ///6///0z8Swh21SjmdIkWdZjHAMm3tAhnEijns5l39DYDkSeUUevXUry3Qtx2Qg0nIownAevXEZzX4i0nog2Hgk13sm2oAq4IEv5n8wUkYsPjMV
        RTsXRz4XVU8tTUcv///z+/jz9f//+vr//ff///v/8/L6//77//zxgGZViWM2jGgul3g3jnNEkHtQi3kvmHZRXS8OhFcewYJMw3I9yHY6xHw0wH0u
        yHsp1Xor14Ix1IMx04Uz2IU334M6PjAjPzIfRzsjTUMoPjgiw7+2//3//v3/9f/0///9//j///n//v///P/2+vnk4Ne6iGgTkGlKgF47iXFDgXE+
        iXVUmX9CqYJbMRwAo3hOs3JKt28/vHtBuns2vm8q2Xg0xnsqwnspwn4tyH8y0X82NTcsLzIfPj4mPz4pWFRJ//79/fv8//7//v//////9/X2//7/
        //7/8/Hy//3+//7/mnFTe1UwlnZHlHVGi3BDjnZKlHxOmIFNmIFiFAAAm3NPsoZZvXZKxINNyINIvIVEtHMzu3Y1xHg6zn081H8+Mz44OUQ2Mjkn
        NTkof4B4/v78/v///v/9//////////////3+/fv8////////9fX1pJB1d2NAiXNKjHVLjnRRkXdWl39ZoIlflIBfnHtcNiQArnVXqX9Vum9IsXxG
        sXY+s3c5uHg7wHo+yX1Bz4FBJzpALD89M0M5QU1DhIqG/P//7vLx+v/59fP0////+fn5/f39////+Pr5+Pr5/v//op2HbWRHc2NCfmVHiGpSiWpV
        j3VamIRjooVlintagmBHRTYZrnVimG9Pu3tYrHlMqnU/rnVAtHU/vXpDxoFGHTNKL0JRJTY9Lzo8gYaK+vv////////7//7///7//Pz8/f/+/v//
        /v///////Pz8nY2Acl1KeVpFg11KjmRWimZYjnJdk35jk3ZYpYtwk4lwZEo9Zk9Bnm5inXBZonBVoHFHom9EpnBCrnZFuH9KGCtVKDhZHytBJi0/
        VFVn//3/+Pb7//77//7//Pr7////+vz7/v//////8/Hy//7/j2Ngfk9Hi1dKjllLkmBVjGJWjm9di3NbmXpelHpjjoZxnYmASzoyfldSlXFllmpd
        nG5Um25PnnBMpnZOrn1SFChdDh9NIzBTMjdVJydBvbrP//3///7/9vT1////9vj3/v///////vr7//v/1sfMfkFIgERDj1ZNjVdNj15Xi2BZj2td
        iGlVmXVffW1Ul3ttjYx4jGZjOyghgFZXjnFtnXJinnFeoHRbpHlZqn1cFCloCh9UGitXHylNMDZWPkJb+////P////7/////+fv6////8/Hy//v/
        //n/aFFZdkFLe0pNhVpTg11UimdhiWZilXBqkWtikHFfnn1sjYZsknFih3xqiWBeFwMCf2RpmWxnmm5lnXJionhipnxjDSlqCCFaGC1cFihQKzhb
        NT9aRVFdvcjK//3+9/f3/v///f39//7/1cbLZU5WWj9IaUNQakpNbFNObFhPfGRgf2FjkWprkWVkknBkinRfjHJbl4ZsoX1tln50dFdZLhshkGJk
        lGdkmW1inXRionhiAipeAyteCC5bEjRaHjtdK0BfM0BgOT9fTVlvcn+SiZaneoGTXlxqVUdWXkJRZ0NRYUhDZ0pMbEpYckpieVFphF9wjGtyjnBw
        jXFtkXZvlnlxmHxxmX1ylHlwh2tnd15aUTAphmhgimxkkWpjpXVxAixoBCxnCi9kEzNiHDlhJz9jLkJjMURkKUNeM0tlPVFqQU9pRkxkTkphVUdg
        VkRcW0tVX01bZExiaEtnblBsd1t0fmJ4fmZ2gWxzhXB1iXN1jHd0jnl0jXh1hXBtfGdmVjk1aExIkXRwkW5sl2lpAC91BTB0DTBwFDRvGjhsID9r
        JUdsKExsJk9vJ0ttLElrNUlsPEptRExwS1FzUFZ4T1NuVVVxWVRyXVRzYld3a12AcGCEcGCFc2aAd2uBem+AfXF9fnN7gHR4f3BzfG1wjnZ2QCsq
        eGNijW9xoHd/ATJ/BTOADjR/FTZ7Gzt4HUJ2IE12I1V4Klp+MFt+OFh+PFJ5OUtzOUhxO052P1V+RVl+SFt8TFt6Ul15WmCAYGKIZWKPZmKUZ2SP
        a2mQb26OcW6Jb26AcGx6cmx2dG11fm5xgnN2IRIXdl9nknB+ADWFBzaIDjeHFDqFGT6CHkd/IlKAJVmBKFR3LVN3MU9xM0psOkxwQFJ4P1J6Nkx1
        PFyCPlx+QF56R2F6UGR/VWOGWmGNXF+SYWGVZGaXaGyZanCUZ2yJZGl9Z2l4a2t3fHF5eXJ5f3qBDQELgGp3BDeGBjmIDDuJET+KGUSJIUqIKFGF
        LFaGMFZ6NVh4N1RyN05tPlFvSlp7Slp+RFN6P2KCPWB+PmB7RWN9S2WAUGOEUmCHVV2LXluSYGKVZmqZZ3CXZG+PYGqDY2p8aGt6d3F9Z2dxb3J7
        iIePiX+KBjqDBzuECD6IDkKLF0iLJEyKLk6HNE+ENlZ9O1x/PV97PFp0OVNsPE9tRlN1T1d+R2aFQ2KBQmGAR2SETWWHUGKIUmCHVl+IYFmMYF6P
        ZWiVZm+WZW+SYW2HY2uAaG+BdXOBd3uHc32GYmtyfHyE
        """;

    // b422.jpg: 37x23, 750 bytes
    private const string B422Jpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIe
        JBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAAXACUD
        ASEAAhEBAxEB/8QAGgAAAgMBAQAAAAAAAAAAAAAAAAUGBwgCA//EACcQAAIBAwMDBAMBAAAAAAAAAAECAwQFEQASIQYxQQcTIlEIYXGB/8QAFgEB
        AQEAAAAAAAAAAAAAAAAABQYH/8QAJREAAgEDAgUFAAAAAAAAAAAAAQIAAwQRITEFEhVB8BMiUbHR/9oADAMBAAIRAxEAPwCC+nFru/VfUMNisUBq
        KuYMQS+1UUcszN4Uf74AySAbqvPol1TZ7FLXrPQXN4ULyQQM2/AGSV3KN387nwCdSnSXq02PxFUvTTqStWkqYWiq6NmkVARNTIgJdTjlfO5cZA7E
        EjGSCH1ulSWOGennDwyKrI6nIKkZBHPnOpu74fgBseefUquFXpAZGMeQyRbMjCnzjB0aH6ex1/ZRLejG8in4XdU2e1epFVS3OWKka40Rp6eaRgq+
        4HVthJ4G7acfZAHcjWx+obzbbBZqm73eripaOnQu8kjAdh2H2x7ADknga22vb+g5WY3RPOBiYottw9+peeNVWNpGdY+PiCSca9qioFjZqyEMlvnk
        LVCqPjBIx5k47KxOW+j8j3Y6n2tFbKkbyhVzSYEHMe26qIibJUjxjRoV+Ge4xUXrAY0lDWSlAfvgedScvVSrFFNUSyxoMRrJIWA/Q+tbBf0EDa95
        l9nWcEEGNbcTTsWXII44Oni1cklKFPyVuCDoetZUym20apX1RlyZ3ZqeopllgUxtSLj2OTvUc5Q+CBxg5zg4I4yxok0E7xSnfDl1n//Z
        """;

    private const string B422Rgb = """
        bEoKclAQcFMRbVAOcFQThWkoh2wphWonj20mgV8YlmoflGgdmF8OoGcWnF4Jm10IlF4AqHIQp2sVnmIMt3MIwX0Sz3Ea0HIb2mgA22kA83QX6msO
        5nQE5HIC5X8a6IId8HQQ73MP8XMN83UP+HkQZ0oIUTQAc1gVel8caVIOdF0ZcFkVclsXeFsXim0pimIamXEppXQllGMUpW0ap28cpHgfl2sSlGcM
        rH8kn2YPsHcgxHQVynobzmIH0mYL3WkE0l4A3nAP3nAP2HYV23kY8X8M8X8M84AL9YIN+4QMZ04MVTwAYEwHYEwHb14ZcWAbdGUibl8cd2IjdF8g
        iGspfWAekmsgpn80l2oXm24blHUjk3QimHkejm8UKwAAqXw4uHgWrGwKymki1HMs2W8Jz2UA3nUk2nEg0nMX2Hkd7H8g7YAh74Eg74Eg8X8eUDwA
        alYZXk8OXE0MW1IPZ14bZ18hamIkbmIufHA8fmo3h3NAj3M0i28wm3kvk3EnjHIlkHYpgmwZlX8shmMhNhMAtH0qrHUivGgixHAqzmsQzmsQ02wd
        y2QVzGwW1XUf6YMX7IYa8Ygd8oke8YYcXUsZVEIQSD4DWE4TWVUYWlYZYmAvT00cbmpHlJBtqp19rqGBmIZYgnBCjXg5dmEig2gbl3wvl347l347
        l3wvgWYZZDIArnw/xnsow3glzWwl2Xgx1XEb0GwW3Xwr4oEw3XoV4X4Z6YMh7IYk7oYlSzkVWEYiU0gaTkMVQkAPXVsqVVY00tOx///z///z//z0
        //72//7k0MSqgHNGfXBDkXArjGsmmXtFiWs1lHkymn84ZjcLdkcbuncovnsswGYqzHI2yW0WyGwV1Hgr03cq13cr2Xkt3nwx4X805X83VkQuQjAa
        RTsYRz0aUVErSkok///v+Pvo/P3/+vv/+/f///z/+fLq//72///hdG5OiWQtjGcwk3dHj3NDkntJjHVDmXdJVjQGjFQZvYVKwHQ2xHg6yXkwyHgv
        yXovzH0y1YQp1YQp2IQs2oYu3oUxQi8hQzAiRzshTkIoOzocw8Kk/f/1/v/2+Pn//P3//Pr//Pr///3///3//fji3NfBh2I2kGs/fGIxi3FAgm1Q
        iXRXmX5JoodSRBIAp3VStnUxtHMvwXk+v3c8t3MqxYE4xHoxw3kwx3o0yn03z349KzwsJTYmN0AvOEEwVFdG///x/f3z///2/v/9/v/99fb6/v//
        ////8vL0//z8//39lnNVeFU3l3VIlnRHkm46l3M/m3lLn31PoH5ZHAAAnXJPsIVisXxGu4ZQxYY/woM8sXQ1tXg5v3w2xYI8zIU3MEA2NUU7MTks
        Mjotf4F0/v/z///4///4/v/9/v/9/P//+////Pz+//////79+PTzpZB1d2JHinJMjHROjnZIkXlLmn5Xo4dgm3xgmntfRBwConpgsXxUq3ZOuHlD
        s3Q+u3I7vXQ9xHg6yX0/0YE8Kzk8Lz1AOT89REpIiImD///68vHs/v348/X0/v//9vr7+v7//v//+Pn7/Pj3//79qZqFcGFMc2JEeGdJgm5Lg29M
        jHdYmINkn4RxkXZjgl5OUi4eqnhhnmxVuHxasXVTs3FBtHJCuXQ9v3pDyIFBJTBCND9RLDM9MThChYWH+/v9///9///9/v///v//+f3++////v//
        /v////79//v6oot7c1xMdltGe2BLhWlRhWlRjXNclnxlj3VmpIp7n4F2Zkg9cUo5mHFgn29ZoHBanXJHnHFGn3RBpXpHsYNHHCtMKThZIipBJS1E
        VFZi/P7/9/f5/////v//+vv9/P//+Pz9//7///7/9vDw//39kGRbfFBHhVlMh1tOjmNSjWJRkW5bkm9clHppk3lomIFxoIl5Uzcpd1tNk3Jjj25f
        mHFSl3BRmnNKoHlQqYFQFydYDh5PJS9SLjhbJik8ubzP/v///v//9PX3/v//9/f5//////3///n7//z908nKeEVBekdDjFdRjFdRkF5VkF5VkWpb
        jWZXkndkhGlWk35rm4Zzg2xePygaeFtTjnFppG9ho25gpXFbqXVfrnxbFSlkCh5ZHCtWGypVMTdPPUNb/P///P///v///v//+/n8//7/+e7y//v/
        //r9ZFRXb0ZEdk1LhVlYhlpZkGRhj2NglXFlkGxgjHNfmYBsloFuinVij3dtfmZcHwAAg2NkoGlkoWplo29hqHRmrHpjEChoCCBgGytcFiZXMDdR
        Nz5YTE9YwsXO/f7/9vf7//3///v///v/08fLZE9WVkFIYkhHZkxLcFBTc1NWg2FggmBfjG5miGpii3RkinNjiXJkmoN1m317mXt5eVRcOhUdjGVg
        kGlklW9imnRnnXtiCCVnCSZoECpbFjBhIjlZKUBgL0NcMERdS1l0cH6ZkJGtfX6aZVduVEZdW0NRXkZUZEZEaEpIa0tWbk5ZdVRpgWB1i2t6jm59
        jnBuknRylntqmH1smH5tlHpph21geV9SUi8riWZij2lmkGpnonZzAyprBCtsCy9jEDRoHDpeI0FlKEZiKkhkKkJeM0tnQU9sQU9sSkpiS0tjVEla
        UkdYXEtTX05WY05hZE9ialFvdFt5fGJ9fmR/gmt1hm95iXRxjHd0jnl0jXhzhXBtfGdkVjg2aUtJlHJxkG5tk2trAC9zAjF1BzRtCzhxFTxnG0Jt
        IkhsJkxwKk1tKEtrLkhpMUtsPEtoQE9sTVFsUlZxUlNoVVZrWVR0WlV1YVZ+aF2Fb2CHb2CHdGaAeGqEfG5/fnCBf3J8gHN9fHB6eW13jHZ4QCos
        e2FkinBznHl/ATN6AzV8Cjd4DTp7FT50GkN5Ikt3KFF9MFd+MlmAN1l+NFZ7NkxxNEpvQE1wRlN2Rlp1SFx3TFp9T12AWl+JXmONZGONZWSOaWWK
        bWmOcm2Lcm2LcG2Cb2yBb2x9cG1+fW5zgnN4JBEXdGFnjXN8AzV+BjiBDjiAETuDGT+AH0WGKk2DL1KILFF7LVJ8L091LExyN010PVN6Q1B8PEl1
        Pl16Pl16Ql17RmF/UGKGUmSIWWKLWWKLYGOMZGeQam2QbG+SaGyFZGiBY2p6ZWx8eHN5eHN5gniBDAILfGx3BjiBCDqDEDqGFD6KH0GJJUePLk6H
        MlKLM1R9NVZ/NlV0MlFwPVF2R1uATFeERlF+PmKCPGCAQF98RGOATmKDT2OEU1+FU1+FWl+JX2SOZ2uRa2+VZ26IYmmDYGx4Ym56cXR7ZWhvcnF5
        iIePh3+KBTuDBjyEDTuIEkCNH0ONJUmTLk6HMFCJNVd8OlyBQF54PFp0PFFwO1BvRlJ6TFiARGaLQGKHQ2CAR2SEUWSEUGODVV6HVl+IWlyNXV+Q
        ZWiVam2aZm+MYmuIYG57ZHJ/bnd8dH2Cd3yCZWpwfH2C
        """;

    // b420.jpg: 37x23, 715 bytes
    private const string B420Jpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIe
        JBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAAXACUD
        ASIAAhEBAxEB/8QAGgABAAIDAQAAAAAAAAAAAAAAAAUGAgcIA//EACcQAAIBBAEDBAIDAAAAAAAAAAECAwAEBREhEjFBBgciUQgTYXGB/8QAFwEB
        AQEBAAAAAAAAAAAAAAAABQYHBP/EAB0RAAICAwEBAQAAAAAAAAAAAAECAAMEBRExEiH/2gAMAwEAAhEDEQA/AKL7cYvL+q/UMOCwUBuLuYMQS/Sq
        KOWZm8KP98AbJAO6sz7JeqcPgpb9Z7DJvCheSCBm69AbJXqUdX9dz4BNUT8LvVOHxXuRdWuTlitGyNkbe3mkYKv7A6t0EngdXSdfZAHciux/UOZx
        uAw1zl8vdxWtnboXeSRgOw7D7Y9gByTwKMs0VSdS0fs6as6wn6QzilpLmForuzZpFQETWyICXU65Xz1LrYHYgka2QRPY6VJY4Z7ecPDIqsjqdgqR
        sEc+d1AY3IfvuXnjVVjaRnWPj4gknVe1xcDBs15CGTHzyFrhVHxgkY8ycdlYnbfR+R7sanb9Crjg9lLibKzGJU+GXWGSLo2NKfOtGlQ+OuiIm2VI
        8apQ50JB58xxd1wezQ2EtQH76HmrOXupViimuJZY0Go1kkLAfwPqlK3zYVoG8mLYTt0HslccTbsWXYI44NTi3cklqFPyVuCDSlFW49ZQ9WLU5FpX
        paZ4a3uLZZYFMbWi6/RyetRztD4IHGjvejojjbKUoVkQHnIimZaF9n//2Q==
        """;

    private const string B420Rgb = """
        akoNcFATb1QPbFEMblcLg2wghW4gg2wejG8jfmEVlWwdk2oblWELnWkTnF8Cm14BimEHnnUbmXITkGkKqHgWsoIgxXYZxnca1WYM1mcN8XQY6GsP
        5HIR4nAP4oEY5YQb7HkE63gD7HcD7nkF9XwLaEgLUjIAc1gTel8aalMHdV4ScVoMc1wOeVwQi24ijGMUm3IjqHQel2MNqWwPq24RonkflWwSkGkK
        qIEimmoIq3sZw3QXyXod0GEH1GUL4mUJ11oA4W8O4W8O2HcO23oR8n8K8n8K9H8L9oEN/IMSZE4UUjwCXkwOXkwObV0fb18hc2UobV8idWIqcl8n
        h2oyfF8nkGoppH49lGogmG4kmXMgmHIfmnghkG4XKwIAqYAouHYirGoWy2oZ1XQj22sX0WEN4XUa3XEW0nQQ2HoW64IZ7IMa7oId7oId8IAcUTsB
        a1UbYE4QXkwOX08Ra1sda10gbmAjdGEpgm83hWgwjnE5lnAvkmwroXctmW8llW8cmXMgi2kSnnwljWQMPRQAvHomtHIexmUUzm0c1mYS1mYS2GwR
        0GQJzG4K1XcT64IZ7oUc8oYh84ci9IQgWU0bUEQSRT0MVU0cV1MmWFQnYl45T0smbmlMlI9yqJyCrKCGlYVjf29NiXZLcl80h2Uem3kymn41mn41
        l3o0gWQeYzMArX07w3k4wHY1yW4n1XozznMiyW4d1oEm24Yr1nol2n4p4oMv5YYy6YYzSDwKVUkXUUkYTEQTQz8SXlotWVUw1tKt///k///k//zi
        //7k//7c1MSihHFGgW5DknApjWskmn41im4llXgym344aTkAeUkHvXMywXc2wmcgznMsyG0cx2wb0Xwh0Hsg1Xkk13sm3X4q4IEt5IEuTkolOjYR
        QD0aQj8cUFA0SUkt///x+Pvq/f75+/z3+/r2///79vTo///0///tcG1ahWYwiGkzkHlDjHU/kHxJinZDl3hJVDUGhVUktoZVuHc/vHtDwnw3wXs2
        xHwwx38z0oI50oI51YM514U724U6OjYROzcSQj8cSUYjOjoewsKm/v/w///x+/z3///7/v35/v35///0///0+/jl2tfEhGUvjW44emMtiXI8hHA9
        i3dEm3xNpIVWRBQAp3dGtXQ8s3I6wXs2v3k0unImyIA0yHgvx3cuy3kvznwy1H4zNjcnMDEhPD0tPT4uVFZI///z/P34///7/v///v//9fX9/v7/
        /v//8fL0/v/6///7lXRRd1YzkHZTj3VShHFQiXZVkHxblIBfnX9bGQAAnnNJsYZcsXtMu4VWwYVPvoJMuHExvHU1wXo4x4A+zYI+OzwsQEExNjcn
        Nzgof4Fz/v/y///7///7/v///v///v7//f3/+/z+/v/////79fbxro1qgF88i3FOjXNQiXZVjHlYlIBfnYlom31ZmnxYSB0ApntRsnxNrHZHtnpE
        sXU/u3Q0vXY2wXo4xn89zYI+LjZDMjpHNz5EQklPhYmI/P//8fHx/f399fP4//7/+vf+/vv///7/+vj9+vj5//7/uJOBf1pIfVxLgmFQhWpXhmtY
        jHZfmIJrnoZskHhef2FFTzEVo3xdl3BRrn9hp3har3NBsHRCs3VCuXtIwYNKKDA9Nz9MLDM5MTg+goaF+Pz7//////////7///7//fr///z///7/
        //7///7//fv8rId1fVhGellIf15NhGlWhGlWinRdk31mj3ddpIxyoYNnaEouc0wtmnNUn3BSoHFTqm48qW07rG47snRBvH5FGSpYJjdlHCtKHy5N
        T1ll9///9Pj5/P////7//Pr7//3///n7//z///z/+O/y//z/jWRgeVBMgllThFtViWVZiGRYjG9djXBeknxnkXtmmIJtoIp1VDcneFtLlXFlkW1h
        nW5SnG1RoG9PpnVVrnxZFyhWDh9NIjFQKzpZIiw4tb/L/P///P//9vT1//7/+/X3//3///z///j7//z/0snMcUhEc0pGg1pUg1pUh2NXh2NXim1b
        hmlXj3lkgWtWlH5pnIZxh2paQyYWflpOlHBkoXJWoHFVpHNTqHdXrnxZESptBh9iFixbFStaKzpPN0Zb+f//+f/////9///9/vj4//39+O/y//z/
        //v/YFVZcURJeEtQhlhYh1lZkWRekGNdlXFhkGxcjHNfmYBsloByinRmj3ZvfmVeHwAAg2NklW9klnBlmnNkn3hppXxoDyhrByBjFy1cEihXKzpP
        MkFWSVFUv8fK//78+Pf1//39//v7//z/0cjLXVJWT0RIb0JHc0ZLekxMfU9Pi15Yil1XkW1djWlZjXRgjHNfiHJkmYN1mH94ln12dlZXNxcYjGZb
        kGpflm9gm3RloXhkBChiBSljDCthEjFnHTpmJEFtKkRlK0VmQ110aIKZhpepc4SWW1xxSktgUkZeVUlhXEZbYEpfY05fZlFibldnemNzhW19iHCA
        im+AjnOEkniBlHqDln14knl0hW5ed2BQUTAriGdij2lmkGpnonZ1BipkBytlDi1jEzJoHDllI0BsKkRlLEZnKUNaMkxjQFFjQFFjSktgS0xhVEhg
        UkZeXkhdYUtgY05fZE9galNjdF1tfGR0fmZ2hGl6iG1+i3F6jnR9kXhzkHdyiHFhf2hYWTgzbEtGl3Fuk21qlmppAy9wBTFyCjJvDjZzFjpuHEB0
        JUZvKUpzK0xrKUppMEhiM0tlQElmRE1qUk9uV1RzUlJuVVVxWFVwWVZxXlpzZWF6bGOAbGOAcWaEdWqIem6GfHCIfXN8fnR9e3NxeHBui3d4Pyss
        emFkiXBzmXp/BjJzCDR1DjZzETl2GT1xHkJ2KElyLk94NVZ1N1h3P1dxPFRuQElmPkdkTEloUk9uVFRwVlZyWVZxXFl0Yl53ZmJ7a2J/bGOAbmOB
        cmeFeGyEeGyEdmx1dWt0dW1rdm5sgW1uhnJzKA8SeF9ikXJ3ATWBBDiECzqADj2DF0B+HUaEJ0+CLFSHK1J5LFN6MU9zLkxwPExwQlJ2SU50Qkdt
        Qlp+Qlp+RFt9SF+BUmKGVGSIV2OLV2OLXWSQYWiUaW2Ta2+VZ22HY2mDY2p6ZWx8dXR6dXR6f3qBCQQLeW13BDiEBjqGDTyCEUCGG0SCIUqIKVGE
        LVWIL1Z9MVh/NlR4MlB0QFB0Slp+UVZ8S1B2Rl6CRFyARVx+SWCCUWGFUmKGU1+HU1+HWF+LXWSQZ2uRa2+VaG6IY2mDY2p6ZWx8dHN5aGdtdXB3
        i4aNin6IAzqKBDuLCzyIEEGNG0eIIU2OKFGHKlOJMVeENlyJPV2EOVmAPE93O052SFF6TleAQ2eLP2OHQGGERGWITGaLS2WKT2CMUGGNVF6PV2GS
        YWmXZm6cZW+SYWuOYGyEZHCIbneAdH2Gd3uGZWl0fHyG
        """;

    // b440.jpg: 37x23, 765 bytes
    private const string B440Jpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIe
        JBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAAXACUD
        ARIAAhEBAxEB/8QAGgAAAwADAQAAAAAAAAAAAAAAAAUGAQIIB//EACcQAAIBAwMDBAMBAAAAAAAAAAECAwQFEQASIQYxQQcTIlEIYXGB/8QAGQEB
        AQEAAwAAAAAAAAAAAAAABQMEAgcI/8QAJBEAAgICAQQBBQAAAAAAAAAAAQIAAwQRBQYSITETIzJBUaH/2gAMAwEAAhEDEQA/AIX04td36r6hhsVi
        gNRVzBiCX2qijlmZvCj/AHwBkkA1/wCF3VNntXqRVUtzlipGuNEaenmkYKvuB1bYSeBu2nH2QB3I0e/Twqb6g8Swzy48S7vPol1TZ7FLXrPQXN4U
        LyQQM2/AGSV3KN387nwCddGdQ3m22CzVN3u9XFS0dOhd5JGA7DsPtj2AHJPA1KzhsVx61L1Z16nQM4paSphaKro2aRUBE1MiAl1OOV87lxkDsQSM
        ZIIxbbh79S88aqsbSM6x8fEEk40MeFQ7DDcXXItrIZT/AGP7dKkscM9POHhkVWR1OQVIyCOfOdJqioFjZqyEMlvnkLVCqPjBIx5k47KxOW+j8j3Y
        6xP06G+yK1c63p5awyRbMjCnzjB0nt1URE2SpHjGj24Ar41EE5rXszwayUoD98Dzo16JyKkK7InRuO7E63KcvVSrFFNUSyxoMRrJIWA/Q+tGgXVe
        7WopjWMDoGNbcTTsWXII44OjXH4a/wBS9WVd3EFjHi1cklKFPyVuCDo1DIxqh6Wa2vsDa3N7NT1FMssCmNqRcexyd6jnKHwQOMHOcHBHGWNFPWi/
        iaa8u0bG5//Z
        """;

    private const string B440Rgb = """
        Zk4EblMMb1QNbFEKcFYMgm0egnAcf28YjHAdgGEPkm0dlmgblWASoWYWnF4LnVwIkFwQpXEeo20Vm2QKrXUYwHscy3UO1XMG1GgG1mgH6XgY4W8P
        5HIR33EP6H4a6YId7XgE7HcG7nYI8XYO93kSZEwCUDUAc1gRel8YbFIIdF8QblwIb18IeV0KjW4ciWQUnnAjqHMlm2AQqWsYrWwYqHQonGgVmmQM
        s3win2cKuXQVyXMM2HYJz2MB1GYF2mkJ0F4A4W8O3nAO3nQQ33gT834K834N9n4Q+X4W/oAZZE4SVDwAYkoMYkoMcVwdcl4hdGQobV8ic2Iuc10r
        hGs1fV4njmoupn07lGoimW0ikHcjkHYflXohi3AZIQUApoAvrnomq2sTymwU1XUe028XzGQL33Yb23IW2HES3HgY64IX7YIa74Ef8YAg8X8fUTsA
        bVUZZEwOYkoMY04PblodbFwgbmAjcmEtg207gmkzj3A5lHA0lGspoXcvmm4jjHMfkXcghmsSmX4ng2cUOhQAsn4qs3MbxWcPzm4XzmoS0WkQ1m0S
        zmUJ0msM2XUV64IX74Qc84Uj9oUl9YMjXEsgU0IUSzsKW0sYXlAhXlApZls7UkgtbWdXlI17pZ2IrKCGlIVkgXBFi3dFdWArg2cemHoxnX00mn05
        lnk/gmEsXjMAr3pEwXs1wXcwwXEq0nwxzXMlym4b3H4n4YMr1Xwg2YAk4oQs5oYw6oYxSzoPWEcZV0cWUkIPSjwNZFYvXVIy2c+0///v///t//3o
        //7k//7d1sWahnJAhG86jnIpimwjnX00im0plHc9nHtGZDkEe0YQu3Uvwngxumojy3Uqx20fyGwZ13ki1ngg1Hsf1n0h3X8n4YEr5YEsT0cwOzQX
        RDwYRj4ZVU8tTUcx///0+/f2/P3/+vv9+fv6///79PTq///x///sc21ViGQwjGcymnU+k3FBlXhOj3FNmXVRWzELhVUkuYVTsnlCvHtBwns9xng2
        yXkyz3sz0IQ40YM404Q52IQ73IQ6OzMcPDUYRj4aTUUgPzkXxsCq//7z//79+vv9/v///P79/f75///2///x/fjk3de/h2MvkWw3hF8okG4+iWxC
        kHJOnXlVq4FbRBQAqnZEr3Y/s3I4wXo8xHY0v28o0Hw0xnouxngtyXovz3sy1X0zNDYxLzEmPD0rPT8pVVdC///2+/z+/v7//v///P//8vf6+///
        /P//8vH2//3///3/jndXclgzkHdOjXdOgXNOg3lVi39Zk4JXn35dHAAAm3RNsoVcsHtPwINUxYNRw4BMtHI2unY5wHo8yX9AzYJBOTs2P0E2Njcl
        NzkjgIJt/v/1/v///v7//v///P//+///+v//+f3///7///7/+fP1p5Bwe2E8i3JJi3VMhnhThnxYj4NdnItgnXxbnXtWRR4Ap3pRsXxQsXRFunhG
        tnM/t3U5u3c6wHo8yH4/zYJBKjdKLztJNj9GREpKhYmI/P//7vH2+f7/9fTv///79vr5+v7//v//+vf//fX///v/tZOJf1pKg1pEhWFHiGpShW1T
        jndYnIJhn4VskXdefWJHUDEVoXxhmm5Rrn9jqndarXJKrnNJsnVIuXtKwYJPJDFENEBOKzQ7Mzk5goaF+Pz9/P//+//////6///7+f38+////v//
        //3///v///j/qYd9fVhIgFdBgl5Eh2lRg2tRjHVWl31ckHZdpYtyn4RpaUoucUwxnXFUn3BUo3BTqG1Fp2xCq25BsnRDvH1KGCpYJjdiHSpNIy1I
        UFdp+///8/j++/////77+/v5/v//+Pz9/v////3/+e70//r/j2JlgE1KjlVOjlhOkWBZjGFalWphl2telXpnk3pmmIJtoIl3UjgpeFpPk3FnkWxk
        nW1ZnGxVoG5TpnVUrX1ZFihWDh9KIzBTLzlUIyo8ub3I+///+///+PTx///99vj3/P///v///vn9//v/1sfMc0ZJekdEj1ZPjVdNj15Xi2BZk2hf
        kGRXkndkg2pWlH5pnIVzhWtcQyUafFpQlG9noXFdoHBZpHJXqHdWrX1ZFCloCh5ZGilgHydVMDZWPkNW+////P////3/////+fr8/P//8vLy//77
        //z4ZFVQY0tJb1BLglxTg11UiWhjhWdnjnJxim1pjnFfmX9ul4BwiHVnjHhxe2ZjGwECf2VomG1nmW5lmnNin3lio35jEidmCx9aGyphHCRSMDZW
        OT5RS1BWwsbH//z/9/f5/v//+v7/////z8rHXlNPU0Q/YUlHaktGdlBHeVNKg2Jdf2Fhim5th2pmj3JgjHJhiXJil4R2lYF6k357clhZMxkcj2Re
        k2hflm9em3Ven3pfAShpAylnCSxmEzFlHjlmKEBmLENjLUVhQF52aIKZhZevd4GaW1t1TkhiUkVhVkdkZEJbZEhfZUxiak5ldlJshV14jmeCj2uF
        inB5jHV9kHl/kX1+kn59jnt3gm1odGBZTzArhmdijGtmkmlnpnR1AyprBStpCy5oFDJmHThlJz9lLENjLkZiJkRcMkxjP1FpRE5nSkpkT0ljVEdj
        U0RhZkRdZUlgZUxiaExjck5of1dyhV55hWF7hGpzhm93iXJ4i3d4jXl4jHl1hXBrfGhhVzgzaktGlHNulWxqmmhpAC9zBDF0CTJyDzVzGDlvHz9u
        KEVvLEpwKk1rK0ppL0hmOElnQEloSEtqUE9vWFJ0T1NsTllvTlpwUlpxW1t1ZmB8amSAaGWAdWWAeGqEe22EfHGCfXKAfnR/fHF5eW50i3d5Pist
        dmRkinBznXh/AzJ2BzR3DTZ2Ejh2GzxyIUFwK0hyMU91NFd1OVh3Pld1QVJwQEloQkVkSklpU01vUVVuT1pwT1txVV10X195Z2F9aWN/aGWAcmJ9
        dWeBeWuCeG1+dmt5dWt2dmtzd2xygW1vhXJ0JBISeV9ilXB3ADaAAzmDCTuEDj2DF0CCH0WDKk2FMVKHLFJ5L1J4L1BzMUtuOU1wQ1F0Rk92QEhv
        P1x6O156O2B7QWN/TWSEVGSGVmSHVWWHZl+SamSUbGuVbG6UaGuKZGiBZmh9aWp+dnN8dXR8fHuDCgMLfWt3ATmDBTuFCz2GEUCGG0SGI0mHLE+H
        MlOIMFZ9NFd9NFV4NU9yPVF0S1l8Tld+SVF4Q2B+PWB8PGF8QmSATGODUmKEUmCDUWGDYVqNZmCQammTbG6UaWyLZGiBZmh9aWp+dXJ7aGdvcnF5
        jIWNjnyIAjyFBDyGCD2JEEGNGkaPJEuOK0+NMFCLMleDOVyEPF6DO1l7OFJzO09yRVN6TFiCS2KLQ2GHP2GGR2OKUWKNVl6MWFyLV16MXVmWXV6W
        YmiaZm+aY3CTYWyIYWuEZ2+Gb3WBdHyHc32HZWl0fnuG
        """;

    // b411.jpg: 37x23, 721 bytes
    private const string B411Jpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIe
        JBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAAXACUD
        AUEAAhEBAxEB/8QAGgAAAgMBAQAAAAAAAAAAAAAAAAUGBwgCA//EACcQAAIBBAEDBAIDAAAAAAAAAAECAwAEBREhEjFBBgciUQgTYXGB/8QAFQEB
        AQAAAAAAAAAAAAAAAAAABgf/xAAlEQABAwMDAwUAAAAAAAAAAAABAAIEAwUREiFBExVRMWHB0fD/2gAMAwEAAhEDEQA/AIL7cYvL+q/UMOCwUBuL
        uYMQS/SqKOWZm8KP98AbJAN1Zn2S9U4fBS36z2GTeFC8kEDN16A2SvUo6v67nwCarVpLmForuzZpFQETWyICXU65Xz1LrYHYgka2QQ+x0qSxwz28
        4eGRVZHU7BUjYI587oFcrY4Uw4+PlKrPNIrYJ2+08hki6NjSnzrRoooor29x/FNxNGPVRT8LvVOHxXuRdWuTlitGyNkbe3mkYKv7A6t0EngdXSdf
        ZAHcitj+oczjcBhrnL5e7itbO3Qu8kjAdh2H2x7ADkngVijG5D99y88aqsbSM6x8fEEk6r2uLgYNmvIQyY+eQtcKo+MEjHmTjsrE7b6PyPdjVduE
        YatB4yFLLc7DS8HwnuOuiIm2VI8aooooo62blJhNd7KhsJagP30PNScvdSrFFNcSyxoNRrJIWA/gfVNccTbsWXYI44NPFu5JLUKfkrcEGqrPi03P
        cFO7fKqMZjgrvDW9xbLLApja0XX6OT1qOdofBA40d70dEcbYooo70KfJSNs5uAv/2Q==
        """;

    private const string B411Rgb = """
        cUkEd08KeFALdU0IbVcOgmwjhG4lgmwjmWU2i1comWU2l2M0oFwJqGQRoFwJn1sIl14Aq3IJp24FnmUAwWoay3QkyXIiynMj2mUF22YG7HcX424O
        2ncP2HUN44AY5oMb8nQN8XMM8XMM83UO+HkSakkCVDMAd1YPfl0WY1UKbmAValwRbF4TfFYljmg3hmAvlW8+qHQgl2MPom4apHAcpnoRmW0ElWkA
        rYEYrF8PvXAgwHMjxnkpz2ID02YH12oLzF8A1nQP1nQP2HYR23kU8n8I8n8I84AJ9YIL+YUOZlAFVD4AYkwBYkwBaWEYa2MacGgfamIZdmAvc10s
        gWs6dmAvi28bn4Mvi28bj3MflXgSlHcRmHsVjnELOwAAuXcrt3Upq2kdzWwO13YY0nETyGcJ23kY13UU1XMS23kY7X8e7oAf74Eg74Eg8IAeTz4A
        aVgSYE8JXk0HWlIRZl4dZ18eamIhbWE3e29Fd2tBgHRKhnkqgnUmjYAxhXgpjHUYkHkchG0Ql4Ajll0YRg0AtHs2rHMuwWkSyXEaxm4Xxm4X024W
        y2YO0GsT2XQc64IX7oUa8Ygd8oke8IccXE0SU0QJSzwBW0wRXlEdX1Ieal0pV0oWcWhLl45xp56Bq6KFkYxMe3Y2gn09a2YmgmgdlnwxmX80mX80
        qXA5k1ojaC8AsnlCyHkqxXYnwXIjzX4v1m8i0Wod4Xot5n8y33kX430b6YMh7IYk7oYlRT0PUkocUUkbTEQWTDkYZ1QzY1Av4M2s//7z//7z//zx
        //7z///UzMqaeXdHdnREjm84iWozmntEims0o25EqXRKajULekUbuXYvvXozsW4nvXozy2ojymki13Yv1nUu2HYt2ngv3nwz4X825H83SkspNjcV
        PT4cP0AeXEk4VUIx//vq//Tj//v///n9/vj8//3/9vfX///j///jb3BQhWQ7iGc+lnVMknFIonFRnGtLonFRXy4OiFcSuYhDrn04soE8y3g0yncz
        y3g0zns314Mr14Mr2IQs2oYu3IYvLzocMDsdOEMlP0osQjQryryz//vy//zz/Pj///z//vr//vr//v/q/v/q9vzi1dvBg2JBjGtKf149jm1MkGZN
        l21UoHZdqX9mPxoAon05o346oXw4wXs2v3k0uHItxoA7xnkzxXgyx3o0yn03zn88MTolKzQfN0ArOEEsWFNQ//77//v4//77/P7//P7/8/X//P7/
        ///z8vTm/v/y///zlXZKd1gslXZKlHVJlmlKm25PoXRVpXhZpnphIgAAnXFYsIRrt3pDwYRNwoVOv4JLuHEvvHUzwXo4x4A+zIQ8MkAvN0U0LTsq
        LjwrgH97//76///7///7+f//+f//+f//+P//+f/x/P/0/P/08vjqq49nfWE5jXFJj3NLl3BTmnNWoXpdqoNmnnpinXlhQR0Fn3tjsX1Lq3dFsHxK
        q3dFwnA4xHI6yHY+zXtD0YA/Kjs1Lj85MkM9PU5IiIiG///98fHv/f377fb7+P//8vv/9v///P/49vzy9vzy/P/4sZl3eGA+eWE/fmZEi2lQjGpR
        lXNaoX9mn4RxkXZjfGFOTDEepnxSmnBGrIJYpXtRuG88uXA9u3I/wXhFyIBGJDI9M0FMJzVALDpFhoSF/Pr7//7///7/+///+///+P3/+v/////7
        ///7///7/fz4qIl0eVpFeVpFfl9KimdUimdUk3BdnHlmj3RppIl+nYJ3ZEk+cUwvmHNWmHNWmXRXoXFDoHBConJEqHhKsYJMIClILTZVIClIIyxL
        WlVb//3/+vX7//3///7//Pr9//7//Pr9//v///v/+e7y//v/jWVbeVFHglpQhFxSjWNXjGJWlWtflmxglHlyk3hxmn94ooeAWjUjfllHlnFfkm1b
        mXFNmHBMmnJOoHhUp4BVGyZUEh1LIy5cLDdlLSgvwLvC//3///3/9vXz///9+Pf1///9//n///X///n/1sbQdkZEeEhGiFhWiFhWjGBXjGBXlGhf
        kGRbkXVxg2djlnp2noJ+jGdeSCMafllQlG9mpHBbo29apXFcqXVgrntgFiljCx5YFypkFiljNThBQURN/P///P///v/7/v/7+fv2/v/7+u32//r/
        //r/YVRdckREeUtLhlhYh1lZkGZaj2VZmW9jlGpeiXJqln93ln93inNrk3VrgmRaHwEAg2Vbn2tgoGxho29kqHRprHlmDSloBSFgES1sDChnMDpD
        N0FKR1FavcfQ+P/58fvy+f/69//4//v/0cfQXFJbTkRNbUNEcUdId01OelBRh2FUhmBTkmxfjmhbh3VphnRohXNnloR4mIB2ln50clpQMxsRi2db
        j2tflHBkmXVpnXpnCClWCSpXDC1aEjNgHjtZJUJgKEVjKUZkTVpqcn+PiZamdoOTZlhpVUdYVEZXV0laaEBYbERcb0dfckpif1Bki1xwlWZ6mGl9
        j29wk3N0l3d4mXl6kYJvjX5rgHFecmNQUTAriGdijGtmjWxnn3d1CSpfCitgDS5jEjNoGjpgIUFnJUVrJ0dtLUJXNktgPVJnPVJnTUpdTkteTkte
        TElcYkVhZUhkZ0pmaEtnck9nfFlxg2B4hWJ6hGtxiG91i3J4jnV7iXxziHtygHNqd2phVzgzaktGknNujm9qk2tpBy1sCS9uCzFwDzV0FTtoG0Fu
        IUd0JUt4LUxoK0pmK0pmLk1pPUtlQU9pRlRuS1lzVk9uWVJxWlNyW1RzZlV1bVx8cWCAcWCAdWZ5eWp9fG2Afm+CfXR5fnV6e3J3eG90knRyRigm
        fmBejW9tnXl5BjB6CDJ8CzV/DjiCFD92GUR7IEuCJlGIMVd7M1l9NFp+MVd7NE9tMk1rN1JwPVh2Sld6TFl8TVp9UF2AXl6EYmKIZGSKZWWLamWD
        bmmHcm2Lcm2LcW1+cGx9cGx9cW1+hGtxiXB2KA8VeF9lkXJ6BDOBBzaECjmHDTyKFUJ9G0iDI1CLKFWQK1J7LFN8KlF6J053MFFyNld4Nld4L1Bx
        QlqAQlqAQ1uBR1+FU2CKVWKMVmONVmONYGSHZGiLaW2Qa2+Sa2uHZ2eDZ2eDaWmFf298f298hnaDEAANgGl5BjeECDmGCzyJD0CNG0WBIUuHJ1GN
        K1WRM1R/NVaBM1R/L1B7N1R2QV6AQV6AO1h6Rl6ERFyCRFyCSGCGTmGJT2KKTmGJTmGJV2GFXGaKY22RZ3GVaWyLZGeGZGeGZmmIdXF/aWVzc299
        iYWTiX6OBjqDBzuECT2GDkKLHkaBJEyHKFCLKlKNO1R9QFmCQluEPleAPVBxPE9wQlV2SFt8T2GFS12BSlyATmCEUWOJUGKIT2GHUGKIVGGEV2SH
        X2yPZHGUaW6MZWqIZGmHaG2Lb3d6dX2AdX2AY2tufH2C
        """;

    // p420.jpg: 37x23, 896 bytes
    private const string P420Jpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0o
        MCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCj/wgARCAAXACUD
        ASIAAhEBAxEB/8QAGQABAAIDAAAAAAAAAAAAAAAAAAQFAgYH/8QAFwEBAQEBAAAAAAAAAAAAAAAABAMGAv/aAAwDAQACEAMQAAABp9spupw75NNh
        ZDbbIoytNsDWZqTLIIyBrf/EAB4QAAICAgIDAAAAAAAAAAAAAAEDAAIEIQURBhIT/9oACAEBAAEFAsJbMhzOKetWxKHuAzxjIWvNc2iV0v2SflKW
        iqzZlNT20sET/8QAGhEAAwEAAwAAAAAAAAAAAAAAAAEDAhEUMf/aAAgBAwEBPwFVfqJ2eDsk2zO3wKjP/8QAGxEAAgMAAwAAAAAAAAAAAAAAAAEC
        BBEDFCH/2gAIAQIBAT8BdWK8kSq6dU5UhwWGI//EACEQAAICAQMFAQAAAAAAAAAAAAECABEDEBJBITEyQmGB/9oACAEBAAY/Ahjwi2MLWr1wIGX9
        EBB6aMHpd60DC+RqUSxNw8D3+aiya1I9eJ//xAAeEAEAAgMAAwEBAAAAAAAAAAABABEhMVFhcYFBkf/aAAgBAQABPyH3JJo6y9qFuz5FayhpN+vM
        AWSyMCogaWbtjUA8e1Zd4BbqX3An1767/ezB+TJLqIGhbmXEE9k15T6PE//aAAwDAQACAAMAAAAQLB/CeA//xAAYEQACAwAAAAAAAAAAAAAAAAAA
        AREhMf/aAAgBAwEBPxB1g3h4xV0qORmzIOn/xAAYEQEBAQEBAAAAAAAAAAAAAAABADERIf/aAAgBAgEBPxDsB7HnZY5Gcm4kHnL/xAAeEAEAAQUB
        AQEBAAAAAAAAAAABEQAhMUFRYXGB4f/aAAgBAQABPxBBo1FgBdTQfwu3NT8+2F24J+Z8aUARJRHdoRIblMwgHBgLiOyuEdiKd+NMDcNTDHUDZTLM
        Jx+HV0ZahUKjkltQEVZJhW/lL8PpUw5UeVBlbdJB0hsHzlORkOLVB25ZGhRnFm7JXhaGcMal/9k=
        """;

    private const string P420Rgb = """
        bUgTdE8ad1oYXkEAf2obf2obemgUh3UhhG4bfGYTl3QijmsZjV4GmmsTnGQHmmIFl2kcmGodnXMdlWsVqnUVtH8ft2kF0oQg12kI1WcG6WwP8HMW
        424P4GsM4HgZ538g7nUK7nUK8nUJ9XgM+30PaUQPXDcCYkUDh2ooWUQAhXAhdGIOb10JhW8cgGoXkG0bkm8dp3ggonMbll4BqnIVlWcaoXMmlmwW
        kWcRpXAQqHMT0YMft2kF2GoJ1GYF4GMG42YJ4GsM53IT5X0e43sc+oEW+oEW/YAU/4IW/4MVXj4PVzcIeWAoVz4Gc2EjdGIkZlgbdmgreGUtZ1Qc
        e2AphWozkWssn3k6oXUumGwlonswnncsi2YWmXQkMQgAqYAosW8YtXMczW8YznAZ12kS1GYP1mgP3W8W2nYY2HQW6n4c7IAe8YEf8YEf8X8cYkIT
        bk4fVTwEbVQcZFIUZlQWfW8yXE4RbVoih3Q8iG02gmcwnXc4j2kqhFgRoXUujGUap4A1lnEhkm0dhVwEQxoAxIIrqWcQyWsU0HIb3W8Y2mwV12kQ
        1WcO0m4Q23cZ6X0b7IAe84Mh9IQi9IIfY00mUTsUUUAVUkEWXlIqXVEpVk0uYlk6dmtXkIVxtaSQsaCMlH9ij3pdk3pSdl01kW4sh2Qip4hFknMw
        ooQ+elwWVSkAsoY/uHUuvHkyx3EmyHIn0XUizHAdznIb4IQt2Hwn3IAr44Uu5Ycw54UsUDoTW0UeTj0SUkEWTEAYY1cvYlk60cip//7q//jk/ezY
        //zo//zf0Lueln1VfGM7mHUzhmMhn4A9jG0qlHYwl3kzeEwFcUUAu3gxwH02xnAlxG4j1Xkm0nYjznIb3YEq1nol2Hwn3X8o3oAp4X8mQDcYSUAh
        Rj8iUUotS0kySkgx/f3z+vrw+vj7//7///3//fj8/PXv//74//nneXJgfF4oh2kzinE7mH9Jh3E/h3E/popYPyMAhFslpn1HuX5CsHU5wnk0w3o1
        xHIozHow0oM40oM41YM314U524U4PjUWPTQVSEEkQzwfQD4nxMKr///2///2//7/+vj7/Pf7//3///v1//r0///t08y6iWs1gmQuh244hWw2kXtJ
        iHJAlHhGrJBeSyIAlGs1wYZKtHk9v3Yxwnk0xnQqzXsxyXovyHkuyngszXsv1H4xNDcmLjEgOTwrPD8uTVFD/v/0+v/7/P/9/P//9fj//P7//P7/
        +Pn7/P3//v/4/P/2kHRNf2M8jXNQiW9Mj3hWkHlXlYFgl4NilHxWFwAApoFUn3pNrnlFt4JOwn9KxoNOunM1vHU3wng3yH490IJAQkU0LzIhQ0Y1
        Oz4thop8/v/0+f76+f769fj//P///P7/+fv/+vv9/f7//f/3+/71popjgmY/hWtIhmxJj3hWi3RSkX1cnIhnkXlTkXlTNRAArIdaqnVBrnlFyIVQ
        tHE8vHU3vnc5w3k4yH490IJALjtELDlCJzI0RlFTfoeC9//7/P//8/f2/v//8vP49fX9/v7//v///f7//v/7/v/7tJWAgmNOf19Kg2NOkG9ch2ZT
        jHNdn4ZwnYlujnpfhW5PPCUGsYprnXZXo25PrXhZr3JDsHNEtHZFuXtKwoBMJzQ9MT5HMj0/NUBCeYJ98/z3/P///P///P3//v///v7/9vb++vv9
        /v///v/7+vz3p4hze1xHfFxHf19Ki2pXg2JPh25Yl35okHxhmIRppo9wY0wtakMkpH1eqXRVom1Op2o7qGs8rG49snRDu3lFFyVSLDpnHCpHIjBN
        UFli+P//5efm/v///fn6//7///3//ff7//3//ff59fDt//77iGdeclFIhFxUg1tTkWJYj2BWjmpalHBgkXxnkXxnjHtnoI97Riocd1tNonpyimJa
        mm1Qm25Rn3BSpHVXq3tXEyFOHixZCBYzLTtYIyw1tr/I/v///v////7/8e3u//z///3///n7//3///77z8rHb05Fa0pBiWFZhFxUkGFXk2RakW1d
        kGxciHNemINuh3Zij35qmX1vMxcJeVFJl29noXRXoXRXpHVXp3harX1ZGSpwDB1jIzFgIC5dNT1QO0NW/Pz+8/P1//z9/PP0//v9//n78OTo//v/
        8eXna19hakdFZ0RCi1tZhlZUk1xXl2BbmWtel2lcjHNfjXRglIR1j39whnFsg25pIAAFjGxxk3JjlXRlmndknXpno31oCxxiFyhuFSNSGihXKTFE
        OEBTT09RzMzO//b3//z9//T2//r8//v/yLzAZFhaUUVHbUpIZEE/hFRSglJQkFlUk1xXl2lcmWtemH9rgGdTinprmIh5hnFsl4J9eVleLAwRimla
        jGtckm9cmHVin3lkBytfBytfECxeFjJkIzpkKUBqL0NkL0NkRVtyaoCXiZireYibX11ySUdcUEJZV0lgVkhVWkxZX01dYU9fb1ZrfWR5iWyCim2D
        jG+Bj3KEj3iAkXqCkH58jXt5fG9mbWBXUSwklXBojmdikGlkpHh1BytfCCxgES1fFjJkIThiJz5oL0NkMERlLEJZNEphPUxfO0pdSkhdT01iVkhf
        T0FYW01aXU9cYU9fYlBgbVRpd15zgGN5gGN5hGd5iGt9iHF5inN7i3l3i3l3gXRrd2phYz42WjUtn3hzm3RvmW1qAC5qAjBsCjFsDjVwGTlsHz9y
        JkZvK0t0MVBvMVBvMkpmLkZiPUZjSFFuVlNyVFFwU1VqVFZrWFZuWVdvYVh1aF98b2B/bl9+c2N+d2eCeWx+em1/eXJ6fHV9eHZ5dXN2knx+SzU3
        eWBjgmlskHN3AzFtBjRwDzZxEThzGjptIEBzKUlyL094N1Z1Pl18Q1t3O1NvPUZjPkdkTktqUk9uVFZrVVdsWVdvXFpyZVx5aWB9b2B/b2B/cWF8
        dWWAd2p8d2p8c2x0c2x0cW9ycnBzdV9hj3l7MBcad15hk3Z6ADl/ADuBCjyBDD6DFj99HEWDJk6BLFSHKlF6KVB5K01yLE5zO090QFR5Rk92Qkty
        RVx7RFt6SVx9TWCBV2CJWmOMYGGNYWKOZGKKaGaObWyMbm2NaG2DZGl/Y2p8ZGt9fHmCbmt0eHB7CAALf3N9ATyCAz6EDD6DEEKHG0SCIEmHKFCD
        LFSHMViBLlV+L1F2MFJ3QFR5RVl+TVZ9S1R7R159RVx7SFt8TF+AVV6HVV6HWluHXF2JYF6GZGKKammJb26Oam+FZWqAY2p8ZGt9a2hxeneAhHyH
        cGhzjICKAD6JAT+KCj6HD0OMHEiJIU2OKFGHKVKILleDM1yIO16ENll/Nk50M0txQU92SVd+QWWHPWGDQF6ERGKITV+NTF6MUlyNVF6PW1+OXGCP
        Y2aPam2Wa3GRZmyMYmyHZG6JeX+LY2l1cnSAg4WRfXuG
        """;

    // p444.jpg: 37x23, 1192 bytes
    private const string P444Jpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYU
        GBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wgARCAAXACUD
        AREAAhEBAxEB/8QAGQAAAgMBAAAAAAAAAAAAAAAABQYCBwgD/8QAGQEAAgMBAAAAAAAAAAAAAAAABAUBAwcG/9oADAMBAAIQAxAAAAFP5phbE2IK
        k8yu6IuIUubBlOpRSMmK2/WuTK5jTWs5owKGJGsowNfMIv8A/8QAIRAAAgEDAwUAAAAAAAAAAAAAAgQDAAEFERIhBgcTFCP/2gAIAQEAAQUCwqrG
        VcZ6JeTV3GNQlYrCVtO2OUXVzTjkKC0LG8zP0bwy8Kxc6mVQ/O/luQLRlHX/xAAeEQACAgIDAQEAAAAAAAAAAAABAgADBBEhMUETIv/aAAgBAwEB
        PwE2yvLI/MS9q2i5IYdxciI+4Bs6EI17EtKcQXkStz5KbGHR5iZTngz7sDqJkT//xAAhEQABBAEEAwEAAAAAAAAAAAABAAIDEQQTFCExBRASQf/a
        AAgBAgEBPwEYrnGlL451Wtp9gp+MW9hbYlS42kbHow8owNkHKfiKaFpFpzGk0VotUmMwdLSH6v/EACQQAAIBAgUEAwAAAAAAAAAAAAECAwAREBIh
        MVEEEyJhMkJi/9oACAEBAAY/Al6bpVzyN72HNNLmjnKi5VN6V49QPlGBvSsrXU6g4SJMRH3o8iM3N9qeedxHEguSaLDQE3y0ZF0hY+X5POKhmLAb
        AnDmmXTt/Xkeq//EAB8QAQABBAMBAQEAAAAAAAAAAAERACFRYTFBcZGBof/aAAgBAQABPyFhollgBynooSSwz6iQmlKkj0DJs/t9RA+4C4laLynJ
        7pQWm7qY+hmp18Dj8Muqi2KjCoQMsTh2/lefuWoHcqDTuotlpJinIknlEC2OaCGRjn6LR071L//aAAwDAQACAAMAAAAQC1xt4KjH/8QAIhEBAAIB
        BAEFAQAAAAAAAAAAAQARITFBUWHwkaGxwdHh/9oACAEDAQE/EGDLHrNDDuWt+/6bdY4YUq1+bQMqwjl1jFqQRWbG3Cvh5/fXmHVjzzqU61+0Dv6E
        tkmQQwRn/8QAIBEAAgICAQUBAAAAAAAAAAAAAREAITFBcRBRYYHhof/aAAgBAgEBPxBdgSvkFZWfUIAHwj2znSO9irTBaO3MQfrjaw9AsEuIlBYr
        kfPktQCiAiYMQDpVLwYQ5T//xAAdEAEBAQEBAAMBAQAAAAAAAAABESEAMUFRgWFx/9oACAEBAAE/ECeKBbxvB/WoFUFjP8rAq8rFmL4C4k30KV5r
        LlQMCEVXDAojolEb8jz6I30h39OH6USYbTIoK6A0HJrUJqClPSQGqga95Eg5hVnxhxoDxxuoHjlnBV4k1alAk4k2fRyKBxaPorh5yI0CIpxxlYJ5
        f5/nPhdKgtgkYGlkJw//2Q==
        """;

    private const string P444Rgb = """
        bEwDbkwEfFoTcE8IdlgQhGgeg2kchm8fjGsmg2Eal2ggnWAQq2EMrF4LpF8Rml8TnWQLqV0PjXYWqXESx2ENzXMF0nog1HEJ2mYT3m4K4nER420T
        5nEK420L7n0d6H8M9XUS7nIO63QO8XoS+n0TYEMHXUAEbE8TcVQYbVMWemIie2Ylc2AchGQrhGUlkGopnWsso2UoqWolm2UPnXIMm3IYk4MhpmIj
        iW4Ap38QvHcpwXECxHkc0F4G1GcA1moH1GYH33IJ23EL23gT2X4J838E8H8F74EI9YMQ+oUUWkcMWEQJV0MGaVUYdGAjbFocalocalwdclkXeGAi
        fmYohGYolW8ulW4hpIAolXQVjXYWhXsZpG8Po3YRAwACo3cAtn0UtGwIwXIVyHcMzHIRy2gN23AQ3G0U3msa6HgW74MX74Qa7YQb638a7nwbTkAA
        Z1oWY1YQV0kCWk4GcWQeZVoYbmUkdGkagXY2cWYwhXo4hHYekn0gk3YqimUujHompGgyjX0dlG0EjmcYIyIOunQfwXgZyG4iymwU0G0czWcdz2oY
        zWkdzWgi23ki6IEi64cl7Yol7ogj8oUeW08VUEQIU0UEVUgEXlMRX1UXYVgfXVMgZ2Ywko5oraqLnptwlpFRf3Eoi24yi2E3jGE3lHUjnXklk4k0
        mnc1fVscVDoVtmkjzXIszG0b1W8k2nMu03Qi0HUm03cu3oAp13Ue23wi4YIm5YIk7YIiTj4cTD0WTj8UWUseVEcbS0EdY1k+zMSv///9///7//77
        +Pfz//744tXChm9Fi2sujHAvlW8okYBMjoA5gHlNnnosZEYQbEkft3cvxHolxnAjzHEoxW8YyXMe1Hcn23gb2H0q3IEu34Qx44Q05oA2TD0qSDki
        SjwfRToaS0IlW1RC//74//3//Pv///3//fv8//3//v3/+PX+//7ohnRCjmouf2c7kmk9e3sbiXJQkX0+m3c5TDUjkVUjxYA/xnY7xHQ3u3You3oo
        xH8xyoAhzn0hzn8i0YIm14Mr3YIzPzIfQDQcTEMkSEIgQDseyMW0//76/fz////9/frz///2+vz3+f//+f/////61s2+iVwjkmo3hmwzlmo9kG5V
        jnRbm35WpoNDNhwAnHNFsndNuHdNu31Aung7xXY9zXUtyXo4xnk1x3o0zn430n44NTQyNz0vND4jOD4iW1hF//72//7/+////v////////7/+Pr5
        /P///f/+/vr7//v/l3JFgF4ukHA9kXJElHRNlHZUlXdVnX9dm39NAwIAontaun1EtXdEsYZRvItQwHwrsHcquXkvwHoyyXw2z4I8Mjg0LzstMD4n
        PUcvfX5w//v4//7/+P3//P79/////Pr7/////f/+/v////7///z+qZB6emJGg2tJj3VQjnVNjHNLln5QoIpYoYtimmZQOCQZp29MuXxGv3pbs3VC
        v35GvHM8vnlAwXtAyX1B14FEMkBANEU9Lj4zMj40lpiV////+vv//P///v///f39//7///v8////+//+8vTz////qJqPbV1NcV1Eg2lOhGlLhW1J
        lX9WnIpal31apYBmhFhVRDssoGVFunRQsGxZpXlSqHFIsnFHvG9FwXVExYBHIjNDGSk2O0hQMTpBeXyF//7//v7////7/P////7///z+//z9/Pz8
        /v///v///f/+ppF+fWROfmBIg2BKhF9MhWRTjHFei3JclH1enY5xnIJrW01MYFpEqm9Ro29ijHFWo25OnnJLnHZJpnpJuoBOITNbLDpdHidEMzdQ
        Sktf/v7////9///2/v///f39//7///7/+/v7/P79+vj5//7/h1tOfU8/hFVBiFdGkmRXkWhikm9pkHJqkH5qlX9YjINknYN0RTs6cV5Ymm1Qk3Vr
        k3VPnXJPpHBLp3hOqoRVCx9aFiNYHSRQKi5RLzNMvsLO//////z1/v///f39//7/+Pj4/v////7///z/08LIgERMg0hKilNMjlpPlWVbil9YhmFY
        iWdbiWlclnhgm31Xj39llGV5JxkYgGRMm2hnpG1YnnBWm3JSpHdWs3xdCyRkESReHylaIytQJjFNND9R+/////7/////////+Pr59fn4/P//+/f4
        //n/clRedD9Rd0VOglZVhmBXjGdeh2VckG5lkW9lonpukW9ll4FanX1ykXR4m25pCAAAkWFhnW5koG5jpm5fqndirX9nBiZjDiRdGSdWHStQJzhU
        NkZdQEtdwMPS//7//v7+/P//+///+//+ysTGalBZXjhFbERPakdLdVhSdFxSdFtWfmJhknJ1i2dpk2pkinpZhXlrj3twoYZxiHNugFhgJSAckWRn
        kGdlkmxjmXRkoHplAC1gACxdCStZGDBcJjheMEBhMkNfMkNdSFlzc4OdiJOven6ZWlVrXEtbXkVLYUFEZERHZ0ZPakhYcUpfflJrjGB3jmx6i3B5
        i3JrlHxyl3xxlXltmX1xmHxwh2tgdVpPTC8rhWhki2plkWlnpHRyBStpBSxnBy9jDzViGjxiJ0FkL0JjM0JhJjtWL0dhNlBpO1NrPUxjSEleVEVc
        X0VeXkxYX05eYE1hZUtkblBsd1lzemF3d2R3gW12gm10g29xi3Z1kHt4i3Nvgmpmg2tnXEM+aVBLj3JsmXNwl2tqBS1zBy90CjJwDzZvFjxtH0Js
        JkZsLEpsL1ZzK1BrLU9qM1FrPVJvP0xuSk91VlV/T1NsUVVuUlZvVlNuX1R0aVt8bGGDamOEc2aEcWR+dGh+fnGCgXWBem12emtwg3R5hnNtPywm
        cVlVlXd3mG9zADR9AzN9DzN/GDR9HTl6H0J4H054IVd7IVx8KVd4N1R0OEdoPkZrOUVtOVB6NVSCRFh5RFt6SF16TVx5V119YmKIZmWPZWaSZ2GP
        bmmSb22ScHCMcnCFcG58cGp2cmx2em5uhXl5JxcYdl9llHR/ADeDAzaFEDaHGDeHHjyEH0WDIFOCIlyEIleBKlR8OVF3PEZpSExvRFF0QVeANFR9
        QFyBQWB/QmF9SGJ9UmGAWWGGXGGLXGCPYl+Uamuba26ZZ22PZ26IaG+BZ2t3aGh0dnF4enV8enN7CwAKfGV1CDeFCDmGCzyLD0CMFkWLH0uKKE+G
        L1OFNVWGMlF9NlJ3OlNxQVVwQlFwRVJ1SFF6Ql6DP2CBQGJ+RWN/TmOCVGGEVl2HVVyIYl6RYmOTYGeTY22RY3GMYm2DY2p8a259cnJ+cHJ+c3WB
        fXmHjYCRCjqCBzuGCD2JDUONGEiQJUyPL02JNkyFNlN/NVV8Nll5O114O1ZxPlFxSVF2VliBRmCDQV+BP2B/RGOCTWSGVGSIVWGHU1+FY1uOYF6N
        YGWPYm+SY3KRX2uFYWl+bXKGcHSAaHF6dH2GdXmCf36G
        """;

    // r420.jpg: 37x23, 733 bytes
    private const string R420Jpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIe
        JBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAAXACUD
        ASIAAhEBAxEB/8QAGQAAAwEBAQAAAAAAAAAAAAAAAAUGBwMI/8QAJxAAAgEEAQMEAgMAAAAAAAAAAQIDAAQFESESMUEGByJRCBNhcYH/xAAYAQAD
        AQEAAAAAAAAAAAAAAAAEBQYHAP/EAB0RAAICAwEBAQAAAAAAAAAAAAECAAMEBRExEiH/3QAEAAH/2gAMAwEAAhEDEQA/AIX24xeX9V+oYcFgoDcX
        cwYgl+lUUcszN4Uf74A2SAdqzPsl6pw+Clv1nsMm8KF5IIGbr0BslepR1f13PgE1Cfhd6pw+K9yLq1ycsVo2Rsjb280jBV/YHVugk8Dq6Tr7IA7k
        V7H9Q5nG4DDXOXy93Fa2duhd5JGA7DsPtj2AHJPApZZoqk6lo/YTVnWE/SGf/9BK0lzC0V3Zs0ioCJrZEBLqdcr56l1sDsQSNbIIfY6VJY4Z7ecP
        DIqsjqdgqRsEc+d0gxuQ/fcvPGqrG0jOsfHxBJOq7XFwMGzXkIZMfPIWuFUfGCRjzJx2Vidt9H5HuxqKv0KuOD2V2JsrMYlT4Z//0aCGSLo2NKfO
        tGik+OuiIm2VI8aorMjoSDz5mlLuuD2f/9LDsJagP30PNU5e6lWKKa4lljQajWSQsB/A+qKKr9hWgbyIMJ26D2f/04rHE27Fl2COODTxbuSS1Cn5
        K3BBoop1bj1lD1YNTkWlelp//9RThre4tllgUxtaLr9HJ61HO0PggcaO96OiONsUUVzIgPOQhMy0L7P/2Q==
        """;

    private const string R420Rgb = B420Rgb;

    // pr420.jpg: 37x23, 1166 bytes
    private const string Pr420Jpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIe
        JBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wgARCAAXACUD
        ASIAAhEBAxEB/8QAGQAAAwEBAQAAAAAAAAAAAAAAAAQFBgIH/8QAGAEAAwEBAAAAAAAAAAAAAAAAAwQFBgD/3QAEAAH/2gAMAwEAAhADEAAAAYW1
        g+yKk//QSfn9xK//0aAmZjS//9LDVArz/wD/04jwOi//1FAOJ//EAB8QAAEEAgIDAAAAAAAAAAAAAAMAAQIEBREGIhITIf/aAAgBAQABBQLHCLas
        f//QNhLQQf/R3Jl//9Ib7b//02dl/9ThdoIsl//VsGGAP//WGTcv/9eUvQv/0By+f//RDFf/0uzr/9MfVf/U8neP/9UMZRX/xAAbEQACAgMBAAAA
        AAAAAAAAAAAAAgEDBBExIf/aAAgBAwEBPwFb27B//9CrJavw/9GM0//SpmT/00sbXT//1IubR//EAB0RAAICAQUAAAAAAAAAAAAAAAECAAQRAxQh
        MWH/2gAIAQIBAT8Baio4af/QegDP/9HYeT//0rCjM//TbTXHU//UIE//xAAhEAACAQMEAwEAAAAAAAAAAAABAgADERIhMUFREyIzYf/aAAgBAQAG
        PwIUKAyYz//QL3SpbcCf/9EMmvaz/9IFToZ//9Of/9RlqEL5ExUnuf/VarVYKi8mf//WuNr7T//XzHzJ9vw9z//Qn//Rn//SALEji5n/05//1J//
        1SNMeJ//xAAgEAEAAgIBBAMAAAAAAAAAAAABABEhMVFBYYHwkbHx/9oACAEBAAE/Ic2l9aA6q8T/0AC82tvxZmf/0VKkGo2du5P/0gs0hE4n/9Op
        xP/UQc2FRkNean//1QzpanuZ/9a6IEk4T//XsNpaBpvo9fnmf//QytT/0c8//9KyCGlk/9PJZP/UKnYz/9UAbHzO3v7/AP/aAAwDAQACAAMAAAAQ
        L//QO//RC//Sj//T9//UA//EABcRAQEBAQAAAAAAAAAAAAAAAAEAETH/2gAIAQMBAT8QY63/0GVcb//RwO3/0tw7f//TTpn/1C9X/8QAGBEBAQEB
        AQAAAAAAAAAAAAAAAQARMSH/2gAIAQIBAT8Q1z9v/9AnDt//0VDk/9IPF//TY9n/1BuZf//EABwQAQACAwEBAQAAAAAAAAAAAAERIQAxQVGBYf/a
        AAgBAQABPxBu4kLEr4A+8CVB/9B7vashKwPjbwXP/9EJgoOrRfVEhpFIlE//0jysGkUSJfZz/9OzIXYhz//UdHsBAmVosj1A2mf/1XfxMGjR6tAW
        tGf/1tHgEoqxn//XAAqAtLppGfDbaz//0GJlHIz/0SN4O5//0krNhAH4eZ//02pIJVOf/9QQuBHP/9VloLdiuXxCoZmGEqcf/9k=
        """;

    private const string Pr420Rgb = B420Rgb;

    // gray.jpg: 33x17, 405 bytes
    private const string GrayJpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIe
        JBweHx7/wAALCAARACEBAREA/8QAFwABAQEBAAAAAAAAAAAAAAAABQcGA//EAC4QAAEDAwEGBAYDAAAAAAAAAAECAwQABREHBhIhMUFRE1JxkRUi
        MjM0QoGx0f/aAAgBAQAAPwAjQLYmfqFepMVuWIMKI2FyHyneVxPBKRniTxqr6i6OJ2Z2ZevVruSpiIgCn232wFbucZSR/VShCnIylyQXHYSiC4n6
        lMnzDH69x0596ftzzSEJVvpcQvinjkY70j4qfKfepXoHqJe9PL5Imw47c6PKbDciM6opCgDkEK6EfzVR1I14u21+z7tht1nbtbEjAkuKeLi1JBzu
        jgAB71hrJNLaR4g3VcsA866LdMCWt5lSlwj8y28Z8HupOP17jp0pj4kPMPc1IrJzPrWgjffHp/tJMc0U3B/IPqKUr//Z
        """;

    private const string GrayRgb = """
        UFBQR0dHWVlZVFRUXV1dZWVlbW1tZ2dnYmJicXFxbm5uaGhoZWVlZWVlbW1tcXFxcnJybW1tf39/eXl5f39/fHx8dnZ2fn5+fX19fn5+hISEjIyM
        jo6OioqKiYmJi4uLkpKSQ0NDRkZGUVFRT09PXFxcY2NjYGBgYmJiZ2dnZ2dnaGhodnZ2f39/dXV1cHBwdHR0dnZ2ZmZmRUVFfX19hISEeHh4dnZ2
        enp6eXl5enp6fn5+g4ODiYmJkJCQlJSUlpaWkJCQR0dHS0tLTU1NUlJSW1tbX19fU1NTYmJiZ2dncHBwcXFxbm5ucXFxc3Nzc3Nzd3d3dXV1ZmZm
        VVVVR0dHfn5+goKCfHx8fn5+fX19fX19fn5+gICAiYmJkpKSl5eXl5eXk5OTQUFBQUFBQUFBR0dHQkJCWlpab29vmJiYsrKyv7+/v7+/oaGhgYGB
        bW1tZmZmcXFxfX19hYWFd3d3TExMTU1Nf39/fHx8fn5+gYGBfn5+f39/hYWFi4uLj4+PkJCQkZGRlJSUQEBAQ0NDR0dHRkZGTU1Nl5eX1tbW/f39
        ////+fn5////9fX1zs7Ok5OTZ2dnbGxseXl5enp6dHR0eXl5UlJSVlZWgoKChISEgoKCfHx8fn5+iIiIjIyMiIiIioqKkJCQj4+PNTU1NjY2Nzc3
        PT09dnZ239/f////////////+Pj4////////////19fXiYmJZmZma2trbGxsd3d3eXl5goKCUVFRW1tbe3t7hYWFg4ODhYWFioqKiIiIg4ODh4eH
        kJCQjIyMPDw8PDw8ODg4T09Ptra2////////8/Pz/////f39////9/f3/////v7+p6enY2Njbm5uf39/eHh4enp6g4ODfn5+Pj4+XFxcd3d3goKC
        i4uLh4eHgYGBgICAhISEh4eHjIyMNzc3PT09PDw8XFxczc3N+/v7+Pj4/////////Pz8////+vr6////////ra2tcXFxbGxsaWlpcHBwe3t7hYWF
        iIiIiIiIUVFRWVlZdHR0hoaGgICAenp6gICAhISEf39/jIyMNDQ0Ozs7Nzc3Tk5Ov7+/////+fn5/////Pz8////////8/Pz////////qqqqZGRk
        YmJiZmZmampqb29vd3d3gICAhISEg4ODVVVVYmJidnZ2fn5+eHh4dnZ2fX19f39/ioqKJSUlKioqLy8vPDw8l5eX+fn5/////f39////+Pj4/f39
        /v7+////4+PjiIiIX19fYWFhZ2dna2trbm5ucnJyeXl5fn5+f39/hoaGT09PUFBQdHR0dXV1dnZ2gICAeXl5g4ODLy8vJSUlJycnJiYmYGBgycnJ
        /f39////////+Pj4////////9fX1sbGxXl5eTExMXl5eZWVlbGxsbm5ucHBwdnZ2fHx8f39/fHx8dnZ2REREOzs7dHR0gYGBcHBwhYWFhYWFIiIi
        IyMjNDQ0LCwsLCwsYGBgoqKi0tLS4+Pj6+vr7+/vysrKj4+PV1dXPT09VFRUVlZWXV1dZmZmbGxscXFxdnZ2e3t7fn5+fn5+h4eHcXFxSkpKSUlJ
        aWlpe3t7dHR0fX19KysrIyMjJiYmMTExPT09QkJCRUVFU1NTbm5ua2trY2NjUVFRSkpKTExMS0tLUVFRT09PVFRUXFxcZmZmb29vc3NzdXV1dnZ2
        g4ODdXV1gICAdnZ2TExMT09PcnJyeXl5eHh4IyMjMTExNjY2ODg4QEBAPDw8Ozs7R0dHRERES0tLU1NTUlJSTU1NTExMUVFRV1dXVFRUVVVVW1tb
        ZGRkbGxsbm5ubm5ub29vc3Nze3t7a2trbW1teHh4VlZWSUlJeXl5eXl5LCwsLy8vKSkpLCwsOzs7SkpKU1NTVVVVUVFRSEhIQ0NDSUlJTk5OU1NT
        WlpaWVlZXFxcW1tbXl5eZGRkZ2dnZ2dnampqbW1teHh4eHh4bm5ua2trcHBwZ2dnU1NTSkpKcHBwMDAwLy8vNTU1QUFBQUFBRkZGUlJSTU1NUFBQ
        T09PTExMUFBQUFBQVFRUXFxcVVVVXV1dXV1dX19fYWFhYGBgYGBgZmZmbm5ubGxsaGhocXFxbW1tZmZmfn5+enp6RUVFbW1tNjY2NTU1ODg4Pz8/
        RUVFSUlJTU1NUFBQU1NTUlJSVlZWTk5OVVVVVFRUXV1dXV1dW1tbXl5eYWFhYWFhYGBgYGBgZGRkZ2dnb29vbm5uampqa2trcnJydXV1d3d3fn5+
        fn5+
        """;

    // grayp.jpg: 33x17, 534 bytes
    private const string GraypJpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIe
        JBweHx7/wgALCAARACEBAREA/8QAFwABAQEBAAAAAAAAAAAAAAAABAYFAv/aAAgBAQAAAAEdZJ6CJWpwumSOglqv/8QAIBAAAQQCAQUAAAAAAAAA
        AAAABQACAwQBERITFCExMv/aAAgBAQABBQIBSeQmIh+2reWqPOFtACMw+ckelt14X6W+DuooU33hM+l//8QAIhAAAgEDAwUBAAAAAAAAAAAAAQID
        ABESITJRECAiMUFh/9oACAEBAAY/AmUNgijyNGaKTPH2CKy1KffyuejOih1YWZTRgjhEQbcb3rWrjZxx3//EAB8QAQACAQQDAQAAAAAAAAAAAAEA
        ETEQIWFxQVGBsf/aAAgBAQABPyHd61Tb0QX0b96riCjuGGXn1BA2BxKepZgUFX9mTIRcnqVPBjbdcn9Tid9b463/2gAIAQEAAAAQrcf/xAAhEAEA
        AgEEAgMBAAAAAAAAAAABABExIUFRkWFxEIGx0f/aAAgBAQABPxApIzch0C9V1hgDBRqVaPyEtWHIOFdjbPMzcnqsrmeZ3AyATADYmxPuUKiGBG6a
        AHccKbFDmO8PCvyo7G20pw7Zm+5j9f2Zzk9nx//Z
        """;

    private const string GraypRgb = GrayRgb;

    // cmyk.jpg: 20x16, 745 bytes
    private const string CmykJpg = """
        /9j/7gAOQWRvYmUAZAAAAAAC/9sAQwADAgIDAgIDAwMDBAMDBAUIBQUEBAUKBwcGCAwKDAwLCgsLDQ4SEA0OEQ4LCxAWEBETFBUVFQwPFxgWFBgS
        FBUU/9sAQwEDBAQFBAUJBQUJFA0LDRQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQU/8AAFAgAEAAUBAER
        AAIRAQMRAQQRAP/EABkAAAIDAQAAAAAAAAAAAAAAAAQHBQYICf/EACkQAAEDAgQFBQEBAAAAAAAAAAECAwQFEQAGBxIIITFBYRMiUXGBFTL/xAAY
        AQEBAAMAAAAAAAAAAAAAAAAGBwQICf/EACcRAAIBAgYABgMAAAAAAAAAAAECEQADBAUGEiFRBxQVQWGBMXGR/9oADgQBAAIRAxEEAAA/AOetZ1Xq
        ECthyHuSU23+qnar6t2xb848Qi15PIorJAJMkyeh+qHpplFtGzixB6pocG2gz+ren0nN86uP01JkuRITDLYVfYBuU5fybWHxhuZG1FLDifVcUtqU
        2lbRPyRcg3688ME1PbxFpGPAIB/vtU4zTJtwO0QVMH6o7JmcZeQc3zoc2Sp2Il4wntq/Yy8lRSHPCSRY/aT84vbWag4ncHNoPQX6YzvU1YSpmihw
        EcEVo+jaosKgoAcT7fb/AKA6fuAuJjh7pFVzs7VmJP8AN9UJSpDKBsVbkOQ6H6xza0nqW/ZwYsOu+OzzW9ud5DbJe7ZeG+efv24rHuj2t2c9AsqV
        KhUxiBLp0p5T8cy0qJjuKABUkJIuOQNj3wtK/leoIibWGmC3FSlKGwqwCU8hY28d8WHCazEhHTj8fNRd9KYrBud9wMW5nuag6XNqVUROcqgbmPSF
        LW8d20ubiSo9O5OK+3nmO2gIkExXkiymyOh/BhzZ1EQg2MYoveyPE23KhZozL+oj1IpqYcpmW6tk7UupYK96exJHfsfI84//2Q==
        """;

    private const string CmykRgb = """
        aEkJclMFc1kUfmYhfmcYkmogo2Yoo18Tm2QSm28VpmwKvnEKynQLzGoA22wH4nQW3nMN7noM9HwM8nsOX0UMZUwQblgbcF4edmIcgWYjlWwnoW8c
        lXAdnnogglwQkV4MvnQQy20S0mcTz2YM3XQW630Y8YEa8YEaU0QJXk8NZ1oRYFYXcWUrgHI8jHo6i3UrjG0hjXMijnEibE8To2oWyW8i0m0j0XEX
        1nQe5H0j64Ul7IUlVUUZTkIQTEQVeG9CtKyQz8m3xr2bpZtvh2wzjHMqkn00g2oreFMayHEo0G0nyXUgzXQi13sm3oIq44QqTj0nQTgXUUktwLe1
        /vv++////vz+7ObcnoRgi20xhm81kXdEfFwummQqwnQuuXQtzX0t0H4t14Eu3YQuODQhREIge3lY9fH2+P//8/vp/P/9//3/y7Kmg2Q5kXI/iHBP
        m3pNbVIsoW07wYBHvnw5vnk0xnozzn40MjcuPEExj5SA/f7//P75+v30/v//+/f84NnWhWtLjm9KjHZaln1WgmZEeVc/tHpTuXxJt3hEv3pCyX9B
        Lz9GLTc6lJ+d///4/vn+/vn+//7//v/1ztvXhG1SfV9Dg29UkH5XnINle11Qil9Hq3VSpnFMr3VJunpIITVPKTVHfoOS/fv9//v4//7/+vn6+/r/
        2NHBelZNiVtRjGZejGthlnxlj31kaVNJj2tYl29WpXdSq3xOFiRcIylSWVlz5OXo+//7+Pv0///99Pf3spCWe09NilxPiGZWjm9clHphkHxkd2BV
        aUtFlWhcn29brHtfFCVfGSVTLDROi5Wp2urq9f/7+/z0x7y6eE5db0VLflxagWVejm5jkHFhk3pnkHdoYUhGfltWnG9nm3JmBSxlCzJcHDlWOEtk
        X2+Ggo6hhHuNZU9fYUBPaUZVdFJheltrjGx6jHB1lHpzmX1wfGBbZk1HjGVelWpkBClsDy9mHjtqIj1fKkdlNU5sPUpmSU1tVE5iV05lXk9qalZ0
        dmOAd2d8gnJ9f3J1gnNwbVpYdFpWmXNyADiBDzR2GDhvHkt1KFd5M05xO0pvPU52QFh5RVt1UVp4ZWGEY2OFZGaJcW6McW+BcWtyd2txXU5Sdl5k
        ADR/DjmGGj+AJFKDKll7MlNwPVRxPVJvPll9Ol56Ql1/WGKGW2OIYmiVZ2qTY2yJZ2p3cXB8ZmBpXlVcCjeDDkGKGkaMLkuJMk19Nll7PlhxRlFw
        S1d5QWSCQmKGU16CVFqFX2CTZWaQZXGLY26CZWt4fn+HfHqB
        """;

    // exif6.jpg: 23x37, 852 bytes
    private const string Exif6Jpg = """
        /9j/4QAiRXhpZgAATU0AKgAAAAgAAQESAAMAAAABAAYAAAAAAAD/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8L
        CwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4e
        Hh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAAXACUDAREAAhEBAxEB/8QAGQAAAgMBAAAAAAAAAAAAAAAABQYCBwgD/8QAJxAAAgEDAwME
        AwEAAAAAAAAAAQIDBAURABIhBjFBBxMiUQhhcYH/xAAZAQACAwEAAAAAAAAAAAAAAAAEBQEDBwb/xAAmEQABBAIBAwMFAAAAAAAAAAABAAIDBBEh
        MQUS8BMUQSIyYYHR/9oADAMBAAIRAxEAPwBF9OLXd+q+oYbFYoDUVcwYgl9qoo5Zmbwo/wB8AZJAPJt6c97u0BM32NbV1Xn0S6ps9ilr1noLm8KF
        5IIGbfgDJK7lG7+dz4BOps9Dk7e4EK2v1Jw+ngFVq0lTC0VXRs0ioCJqZEBLqccr53LjIHYgkYyQQlPTRICD+j58H+fkFlDckikG0et0qSxwz084
        eGRVZHU5BUjII5850qloFn3NXVR3mvaMHOUchki2ZGFPnGDoL2BPCLZdG9pU/C7qmz2r1IqqW5yxUjXGiNPTzSMFX3A6tsJPA3bTj7IA7ka3OxQ9
        AkhYxDL3DZWx+obzbbBZqm73eripaOnQu8kjAdh2H2x7ADkngaDRTWlxwFii23D36l541VY2kZ1j4+IJJxpO6qMnSdlgAx3ZK7VFQLGzVkIZLfPI
        WqFUfGCRjzJx2Vict9H5Hux1WabJhg8qYbDoiR8eeeFHbdVERNkqR4xpVL0zeE0bccBhUNZKUB++B51tVqswjIWTQzO4bymcvVSrFFNUSyxoMRrJ
        IWA/Q+tJHxRk4ITStPI3g7RW3E07FlyCOODqoVI+MaRkXUZToo4tXJJShT8lbgg6FnoRt21E+7eDgqdmp6imWWBTG1IuPY5O9RzlD4IHGDnODgjj
        LLzXaOUbFeAyCv/Z
        """;

    private const string Exif6Rgb = """
        BjqDBDeGADWFATJ/AC91AixoAipeDSlqFCloFChdGCtVHTNKJzpAMz44NTcsPjAjUkYsRzsTWU0bTTwGZk0UZ0oIbEsEBzuEBjmIBzaIBTOABTB0
        BCxnAyteCCFaCh9UDh9NKDhZL0JRLD89OUQ2LzIfPzIfPjMVVEkcUUQPZ1YeVDsCUTMAclEKCD6IDDuJDjeHDjR/DTBwCi9kCC5bGC1cGitXIzBT
        HytBJTY9M0M5MjknPj4mRzsjRTsXU0kWST0DYE0SYkoOdFYWc1INDkKLET+KFDqFFTZ7FDRvEzNiEjRaFihQHylNMjdVJi0/Lzo8QU1DNTkoPz4p
        TUMoRz4XTkQPW00QX0sOYkoMe10dbk8MF0iLGUSJGT6CGzt4GjhsHDlhHjtdKzhbMDZWJydBVFVngYaKhIqGf4B4WFRJPjgiVU8tSD4LXlIWYU8R
        c1sdalEOcFUOJEyKIUqIHkd/HUJ2ID9rJz9jK0BfNT9aPkJbvbrP//3/+vv//P///v78//79w7+2TUcvYFktX1Iba1sdcl8dcl4ZgmwhLk6HKFGF
        IlKAIE12JUdsLkJjM0BgRVFd+/////3/+Pb7////7vLx/v///fv8//3////zWlQyaF4ra10gdmQibFsTgXAiNE+ELFaGJVmBI1V4KExsMURkOT9f
        vcjK/P////7///77///7+v/5/v/9//7//v3/+/jz1tG0VUsabGAkbl8cbF4TfHAeNlZ9MFZ6KFR3Klp+Jk9vKUNeTVlv//3+//7/9vT1//7///7/
        9fP0/////v//9f/09f//+f/2bWxAdWEmgF0neVkgh3EmO1x/NVh4LVN3MFt+J0ttM0tlcn+S9/f3/////////Pr7//7////////////////9+vr/
        /P/6k5JmgHAyelsiimwuemMVPV97N1RyMU9xOFh+LElrPVFqiZan/v//+fv69vj3/////Pz8+fn5////9/X2//j//ff//f75oaJ2eW4sh2sshmMh
        kW0fPFp0N05tM0psPFJ5NUlsQU9peoGT/f39/////v//+vz7/f/+/f39//3+//7///n///v////6p6Z2gngwfl8cn24rnmUYOVNsPlFvOkxwOUtz
        PEptRkxkXlxq//7/8/Hy/////v///v///////fv8//7//v//8/L6///0kItUiHgqkmoirm8splgNPE9tSlp7QFJ4OUhxRExwTkphVUdW1cbL//v/
        /vr7/////v//+Pr5////8/Hy/P/2//77z8SwgXM2iXMeqX4woF0Wsl8TRlN1Slp+P1J6O052S1FzVUdgXkJRZU5W//n///v/8/Hy////+Pr5////
        //3++vnk//zxh21Sknc0mH0ikmwXpGwfoFwJT1d+RFN6Nkx1P1V+UFZ4VkRcZ0NRWj9IaFFZ1sfM//7//Pz8/v//9fX1//7/4Ne6gGZVjmdIgl0W
        knYVknMZnXMflmAIR2aFP2KCPFyCRVl+T1NuW0tVYUhDaUNQdkFLfkFIj2NgnY2Aop2HpJB1mnFTiGgTiWM2kWdZg2QrinUakngZonkhkV4FQ2KB
        PWB+Plx+SFt8VVVxX01bZ0pMakpNe0pNgERDfk9Hcl1KbWRHd2NAe1UwkGlKjGgujHAMl3g/kHUwk3cUl20LqHAdQmGAPmB7QF56TFt6WVRyZExi
        bEpYbFNOhVpTj1ZNi1dKeVpFc2NCiXNKlnZHgF47l3g3m3tAmYMghG4ImHgllWcHp2kcR2SERWN9R2F6Ul15XVRzaEtnckpibFhPg11UjVdNjllL
        g11KfmVHjHVLlHVGiXFDjnNEhnEiloEwlX01jnMArXsyn2IHTWWHS2WAUGR/WmCAYld3blBseVFpfGRgimdhj15XkmBVjmRWiGpSjnRRi3BDgXE+
        kHtQjns5kX4yf2sIJgAAm2kIs3kAUGKIUGOEVWOGYGKIa12Ad1t0hF9wf2FjiWZii2BZjGJWimZYiWpVkXdWjnZKiXVUi3kvl39DgGMrNQoEq4UK
        tXUexXwNUmCHUmCHWmGNZWKPcGCEfmJ4jGtykWprlXBqj2tdjm9djnJdj3Val39ZlHxOmX9CmHZRYDkSXi4XrYQYtHckw3oA0G8gVl+IVV2LXF+S
        ZmKUcGCFfmZ2jnBwkWVkkWtiiGlVi3Nbk35jmIRjoIlfmIFNqYJbXS8OeUUer38rrnUcsmsA03Ud228KYFmMXluSYWGVZ2SPc2aAgWxzjXFtknBk
        kHFfmXVfmXpek3ZYooVllIBfmIFiMRwAhFcevXUrzXglvmoUxm4Vy2QH22UDYF6PYGKVZGaXa2mQd2uBhXB1kXZvinRfnn1sfW1UlHpjpYtwinta
        nHtcFAAAo3hOwYJMy3Qt0XIiym8c0nYfz2gL22cEZWiVZmqZaGyZb26Oem+AiXN1lnlxjHJbjYZsl3ttjoZxk4lwgmBHNiQAm3NPs3JKw3I9x2Qg
        0msczWoZ0m8a1GwN6nkTZm+WZ3CXanCUcW6JfXF9jHd0mHxxl4ZsknFijYx4nYmAZEo9RTYZrnVXsoZZt28/yHY60nIo3ncoz2gZzGMQzF8C428I
        ZW+SZG+PZ2yJb26AfnN7jnl0mX1yoX1th3xqjGZjSzoyZk9BrnViqX9VvXZKvHtBxHw0wnAe0XIe02wd4XQi4W4R53IIYW2HYGqDZGl9cGx6gHR4
        jXh1lHlwln50iWBeOyghfldSnm5imG9Pum9IxINNuns2wH0uvXEZyHAZymUV3m8e4W8P5HEEY2uAY2p8Z2l4cmx2f3BzhXBth2tndFdZFwMCgFZX
        lXFlnXBZu3tYsXxGyINIvm8qyHspzX4i2X8n0Gka224b33MQ6IANaG+BaGt6a2t3dG11fG1wfGdmd15aLhshf2RpjnFtlmpdonBVrHlMsXY+vIVE
        2Xg01Xor0nog4YMs2XIj33Uh3ngT5YYOdXOBd3F9fHF5fm5xjnZ2Vjk1UTApkGJkmWxnnXJinG5UoHFHqnU/s3c5tHMzxnsq14Ix2Hgk33cg64Ag
        7YAX8n4R8nYGd3uHZ2dxeXJ5gnN2QCsqaExIhmhglGdkmm5lnnFem25Pom9ErnVAuHg7u3Y1wnsp1IMx13sm4H0i64Uj7IMY738P7nYFc32Gb3J7
        f3qBIRIXeGNikXRwimxkmW1inXJioHRbnnBMpnBCtHU/wHo+xHg6wn4t04Uz2oAq5YMo64kk64UZ74EQ7HcGYmtyiIePDQELdl9njW9xkW5skWpj
        nXRionhipHlZpnZOrnZFvXpDyX1Bzn08yH8y2IU34IEv6oUp74kk7oQX9IIS8XcIfHyEiX+KgGp3knB+oHd/l2lppXVxonhipnxjqn1crn1SuH9K
        xoFGz4FB1H8+0X8234M65n8w8YMq8oUg84AV/IIV+XkM
        """;

    // exif3.jpg: 37x23, 751 bytes
    private const string Exif3Jpg = """
        /9j/4QAiRXhpZgAATU0AKgAAAAgAAQESAAMAAAABAAMAAAAAAAD/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8L
        CwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4e
        Hh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAAXACUDASIAAhEBAxEB/8QAGgABAAIDAQAAAAAAAAAAAAAAAAUGAgcIA//EACcQAAIBBAED
        BAIDAAAAAAAAAAECAwAEBREhEjFBBgciUQgTYXGB/8QAFwEBAQEBAAAAAAAAAAAAAAAABQYHBP/EAB0RAAICAwEBAQAAAAAAAAAAAAECAAMEBREx
        EiH/2gAMAwEAAhEDEQA/AKL7cYvL+q/UMOCwUBuLuYMQS/SqKOWZm8KP98AbJAO6sz7JeqcPgpb9Z7DJvCheSCBm69AbJXqUdX9dz4BNUT8LvVOH
        xXuRdWuTlitGyNkbe3mkYKv7A6t0EngdXSdfZAHciux/UOZxuAw1zl8vdxWtnboXeSRgOw7D7Y9gByTwKMs0VSdS0fs6as6wn6QzilpLmForuzZp
        FQETWyICXU65Xz1LrYHYgka2QRPY6VJY4Z7ecPDIqsjqdgqRsEc+d1AY3IfvuXnjVVjaRnWPj4gknVe1xcDBs15CGTHzyFrhVHxgkY8ycdlYnbfR
        +R7sanb9Crjg9lLibKzGJU+GXWGSLo2NKfOtGlQ+OuiIm2VI8apQ50JB58xxd1wezQ2EtQH76HmrOXupViimuJZY0Go1kkLAfwPqlK3zYVoG8mLY
        Tt0HslccTbsWXYI44NTi3cklqFPyVuCDSlFW49ZQ9WLU5FpXpaZ4a3uLZZYFMbWi6/RyetRztD4IHGjvejojjbKUoVkQHnIimZaF9n//2Q==
        """;

    private const string Exif3Rgb = """
        fHyGZWl0d3uGdH2GbneAZHCIYGyEYWuOZW+SZm6cYWmXV2GSVF6PUGGNT2CMS2WKTGaLRGWIQGGEP2OHQ2eLTleASFF6O052PE93OVmAPV2ENlyJ
        MVeEKlOJKFGHIU2OG0eIEEGNCzyIBDuLAzqKin6Ii4aNdXB3aGdtdHN5ZWx8Y2p6Y2mDaG6Ia2+VZ2uRXWSQWF+LU1+HU1+HUmKGUWGFSWCCRVx+
        RFyARl6CS1B2UVZ8Slp+QFB0MlB0NlR4MVh/L1Z9LVWIKVGEIUqIG0SCEUCGDTyCBjqGBDiEeW13CQQLf3qBdXR6dXR6ZWx8Y2p6Y2mDZ22Ha2+V
        aW2TYWiUXWSQV2OLV2OLVGSIUmKGSF+BRFt9Qlp+Qlp+QkdtSU50QlJ2PExwLkxwMU9zLFN6K1J5LFSHJ0+CHUaEF0B+Dj2DCzqABDiEATWBkXJ3
        eF9iKA8ShnJzgW1udm5sdW1rdWt0dmx1eGyEeGyEcmeFbmOBbGOAa2J/ZmJ7Yl53XFl0WVZxVlZyVFRwUk9uTEloPkdkQElmPFRuP1dxN1h3NVZ1
        Lk94KElyHkJ2GT1xETl2DjZzCDR1BjJzmXp/iXBzemFkPyssi3d4eHBue3NxfnR9fXN8fHCIem6GdWqIcWaEbGOAbGOAZWF6XlpzWVZxWFVwVVVx
        UlJuV1RzUk9uRE1qQElmM0tlMEhiKUppK0xrKUpzJUZvHEB0FjpuDjZzCjJvBTFyAy9wlmppk21ql3FubEtGWTgzf2hYiHFhkHdykXhzjnR9i3F6
        iG1+hGl6fmZ2fGR0dF1talNjZE9gY05fYUtgXkhdUkZeVEhgS0xhSktgQFFjQFFjMkxjKUNaLEZnKkRlI0BsHDllEzJoDi1jBytlBipkonZ1kGpn
        j2lmiGdiUTArd2BQhW5eknl0ln14lHqDkniBjnOEim+AiHCAhW19emNzbldnZlFiY05fYEpfXEZbVUlhUkZeSktgW1xxc4SWhpepaIKZQ110K0Vm
        KkRlJEFtHTpmEjFnDCthBSljBChioXhkm3Rllm9gkGpfjGZbNxcYdlZXln12mH94mYN1iHJkjHNfjXRgjWlZkW1dil1Xi15YfU9PekxMc0ZLb0JH
        T0RIXVJW0cjL//z///v7//39+Pf1//78v8fKSVFUMkFWKzpPEihXFy1cByBjDyhrpXxon3hpmnNklnBllW9kg2NkHwAAfmVej3ZvinRmloBymYBs
        jHNfkGxclXFhkGNdkWReh1lZhlhYeEtQcURJYFVZ//v///z/+O/y//39/vj4///9///9+f//+f//N0ZbKzpPFStaFixbBh9iESptrnxZqHdXpHNT
        oHFVoXJWlHBkflpOQyYWh2panIZxlH5pgWtWj3lkhmlXim1bh2NXh2NXg1pUg1pUc0pGcUhE0snM//z///j7//z///3/+/X3//7/9vT1/P///P//
        tb/LIiw4KzpZIjFQDh9NFyhWrnxZpnVVoG9PnG1RnW5SkW1hlXFleFtLVDcnoIp1mIJtkXtmknxnjXBejG9diGRYiWVZhFtVgllTeVBMjWRg//z/
        +O/y//z///z///n7//3//Pr7//7//P//9Pj59///T1llHy5NHCtKJjdlGSpYvH5FsnRBrG47qW07qm48oHFTn3BSmnNUc0wtaEouoYNnpIxyj3dd
        k31minRdhGlWhGlWf15NellIfVhGrId1/fv8//7///7///7///z//fr///7///7/////////+Pz7goaFMTg+LDM5Nz9MKDA9wYNKuXtIs3VCsHRC
        r3NBp3harn9hl3BRo3xdTzEVf2FFkHhenoZsmIJrjHZfhmtYhWpXgmFQfVxLf1pIuJOB//7/+vj5+vj9//7//vv/+vf+//7/9fP4/f398fHx/P//
        hYmIQklPNz5EMjpHLjZDzYI+xn89wXo4vXY2u3Q0sXU/tnpErHZHsnxNpntRSB0AmnxYm31ZnYlolIBfjHlYiXZVjXNQi3FOgF88ro1q9fbx///7
        /v//+/z+/f3//v7//v///v/////7///7/v/yf4FzNzgoNjcnQEExOzwszYI+x4A+wXo4vHU1uHExvoJMwYVPu4VWsXtMsYZcnnNJGQAAnX9blIBf
        kHxbiXZVhHFQj3VSkHZTd1YzlXRR///7/v/68fL0/v///v7/9fX9/v///v/////7/P34///zVFZIPT4uPD0tMDEhNjcn1H4zznwyy3kvx3cuyHgv
        yIA0unImv3k0wXs2s3I6tXQ8p3dGRBQApIVWm3xNi3dEhHA9iXI8emMtjW44hGUv2tfE+/jl///0///0/v35/v35///7+/z3///x/v/wwsKmOjoe
        SUYjQj8cOzcSOjYR24U614U71YM50oI50oI5x38zxHwwwXs2wnw3vHtDuHc/toZVhVUkVDUGl3hJinZDkHxJjHU/kHlDiGkzhWYwcG1a///t///0
        9vTo///7+/r2+/z3/f75+Pvq///xSUktUFA0Qj8cQD0aOjYRTkol5IEu4IEt3X4q13sm1Xkk0Hsg0Xwhx2wbyG0cznMswmcgwXc2vXMyeUkHaTkA
        m344lXgyim4lmn41jWskknApgW5DhHFG1MSi//7c//7k//zi///k///k1tKtWVUwXlotQz8STEQTUUkYVUkXSDwK6YYz5YYy4oMv2n4p1nol24Yr
        1oEmyW4dznMi1XozyW4nwHY1w3k4rX07YzMAgWQel3o0mn41mn41m3kyh2Uecl80iXZLf29NlYVjrKCGqJyClI9ybmlMT0smYl45WFQnV1MmVU0c
        RT0MUEQSWU0b9IQg84ci8oYh7oUc64IZ1XcTzG4K0GQJ2GwR1mYS1mYSzm0cxmUUtHIevHomPRQAjWQMnnwli2kSmXMglW8cmW8loXctkmwrlnAv
        jnE5hWgwgm83dGEpbmAja10ga1sdX08RXkwOYE4Qa1UbUTsB8IAc7oId7oId7IMa64IZ2HoW0nQQ3XEW4XUa0WEN22sX1XQjy2oZrGoWuHYiqYAo
        KwIAkG4XmnghmHIfmXMgmG4klGogpH49kGopfF8nh2oycl8ndWIqbV8ic2Uob18hbV0fXkwOXkwOUjwCZE4U/IMS9oEN9H8L8n8K8n8K23oR2HcO
        4W8O4W8O11oA4mUJ1GUL0GEHyXodw3QXq3sZmmoIqIEikGkKlWwSonkfq24RqWwPl2MNqHQem3IjjGMUi24ieVwQc1wOcVoMdV4SalMHel8ac1gT
        UjIAaEgL9XwL7nkF7HcD63gD7HkE5YQb4oEY4nAP5HIR6GsP8XQY1mcN1WYMxncaxXYZsoIgqHgWkGkKmXITnnUbimEHm14BnF8CnWkTlWELk2ob
        lWwdfmEVjG8jg2wehW4gg2wgblcLbFEMb1QPcFATakoN
        """;

    // q16.jpg: 37x23, 944 bytes
    private const string Q16Jpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wCDEAAFAAMABAAEAAQAAwAFAAQABAAEAAUABQAFAAYABwAMAAgABwAHAAcABwAPAAsACwAJAAwAEQAPABIA
        EgARAA8AEQARABMAFgAcABcAEwAUABoAFQARABEAGAAhABgAGgAdAB0AHwAfAB8AEwAXACIAJAAiAB4AJAAcAB4AHwAe/9sAgxEABQAFAAUABwAG
        AAcADgAIAAgADgAeABQAEQAUAB4AHgAeAB4AHgAeAB4AHgAeAB4AHgAeAB4AHgAeAB4AHgAeAB4AHgAeAB4AHgAeAB4AHgAeAB4AHgAeAB4AHgAe
        AB4AHgAeAB4AHgAeAB4AHgAeAB4AHgAeAB4AHgAeAB4AHv/BABEIABcAJQMBEQACEQEDEQH/xAAZAAACAwEAAAAAAAAAAAAAAAAFBgIHCAP/xAAn
        EAACAQMDAwQDAQAAAAAAAAABAgMEBREAEiEGMUEHEyJRCGFxgf/EABkBAAIDAQAAAAAAAAAAAAAAAAQFAQMHBv/EACYRAAEEAgEDAwUAAAAAAAAA
        AAEAAgMEESExBRLwExRBIjJhgdH/2gAMAwEAAhEDEQA/AEX04td36r6hhsVigNRVzBiCX2qijlmZvCj/AHwBkkA8m3pz3u7QEzfY1tXVefRLqmz2
        KWvWegubwoXkggZt+AMkruUbv53PgE6mz0OTt7gQra/UnD6eAVWrSVMLRVdGzSKgImpkQEupxyvncuMgdiCRjJBCU9NEgIP6Pnwf5+QWUNySKQbR
        63SpLHDPTzh4ZFVkdTkFSMgjnznSqWgWfc1dVHea9owc5RyGSLZkYU+cYOgvYE8Itl0b2lT8LuqbPavUiqpbnLFSNcaI09PNIwVfcDq2wk8DdtOP
        sgDuRrc7FD0CSFjEMvcNlbH6hvNtsFmqbvd6uKlo6dC7ySMB2HYfbHsAOSeBoNFNaXHAWKLbcPfqXnjVVjaRnWPj4gknGk7qoydJ2WADHdkrtUVA
        sbNWQhkt88haoVR8YJGPMnHZWJy30fke7HVZpsmGDyphsOiJHx554Udt1URE2SpHjGlUvTN4TRtxwGFQ1kpQH74HnW1WqzCMhZNDM7hvKZy9VKsU
        U1RLLGgxGskhYD9D60kfFGTghNK08jeDtFbcTTsWXII44OqhUj4xpGRdRlOiji1cklKFPyVuCDoWehG3bUT7t4OCp2anqKZZYFMbUi49jk71HOUP
        ggcYOc4OCOMsvNdo5RsV4DIK/9k=
        """;

    private const string Q16Rgb = B444Rgb;

    // fill.jpg: 37x23, 753 bytes
    private const string FillJpg = """
        /9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0dHx8fExciJCIe
        JBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh7/wAARCAAXACUD
        ASIAAhEBAxEB/8QAGQAAAwEBAQAAAAAAAAAAAAAAAAUGBwMI/8QAJxAAAgEEAQMEAgMAAAAAAAAAAQIDAAQFESESMUEGByJRCBNhcYH/xAAYAQAD
        AQEAAAAAAAAAAAAAAAAEBQYHAP/EAB0RAAICAwEBAQAAAAAAAAAAAAECAAMEBRExEiH/3QAEAAH////aAAwDAQACEQMRAD8AhfbjF5f1X6hhwWCg
        NxdzBiCX6VRRyzM3hR/vgDZIB2rM+yXqnD4KW/WewybwoXkggZuvQGyV6lHV/Xc+ATUJ+F3qnD4r3IurXJyxWjZGyNvbzSMFX9gdW6CTwOrpOvsg
        DuRXsf1DmcbgMNc5fL3cVrZ26F3kkYDsOw+2PYAck8CllmiqTqWj9hNWdYT9IZ//////0ErSXMLRXdmzSKgImtkQEup1yvnqXWwOxBI1sgh9jpUl
        jhnt5w8MiqyOp2CpGwRz53SDG5D99y88aqsbSM6x8fEEk6rtcXAwbNeQhkx88ha4VR8YJGPMnHZWJ230fke7Goq/Qq44PZXYmysxiVPhn//////R
        oIZIujY0p860aKT466IibZUjxqisyOhIPPmaUu64PZ//////0sOwlqA/fQ81Tl7qVYopriWWNBqNZJCwH8D6ooqv2FaBvIgwnboPZ//////TiscT
        bsWXYI44NPFu5JLUKfkrcEGiinVuPWUPVg1ORaV6Wn//////1FOGt7i2WWBTG1ouv0cnrUc7Q+CBxo73o6I42xRRXMiA85CEzLQvs//////Z
        """;

    private const string FillRgb = B420Rgb;
}
