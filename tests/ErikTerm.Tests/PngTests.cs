using System.Buffers.Binary;
using System.IO.Compression;
using ErikTerm.Core.Images;

namespace ErikTerm.Tests;

public class PngTests
{
    /// <summary>Raw sample values per pixel (channels × [0, 2^depth)), produced by a deterministic pattern.</summary>
    private static int[] Samples(int x, int y, int channels, int depth, int seed)
    {
        int max = (1 << depth) - 1;
        var s = new int[channels];
        for (int c = 0; c < channels; c++)
            s[c] = (int)((uint)((x * 31 + y * 17 + c * 7 + seed) * 2654435761u) >> 8) % (max + 1);
        return s;
    }

    /// <summary>Hand-encodes a PNG: every scanline uses a different filter type, optionally Adam7-interlaced.</summary>
    private static byte[] Encode(int w, int h, int colorType, int depth, bool interlace, int seed, byte[]? plte = null, byte[]? trns = null)
    {
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bitsPerPixel = channels * depth;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        var raw = new MemoryStream();
        int[][] passes = interlace
            ? [[0, 0, 8, 8], [4, 0, 8, 8], [0, 4, 4, 8], [2, 0, 4, 4], [0, 2, 2, 4], [1, 0, 2, 2], [0, 1, 1, 2]]
            : [[0, 0, 1, 1]];
        int filterCounter = 0;
        foreach (var p in passes)
        {
            int pw = w > p[0] ? (w - p[0] + p[2] - 1) / p[2] : 0, ph = h > p[1] ? (h - p[1] + p[3] - 1) / p[3] : 0;
            if (pw == 0 || ph == 0) continue;
            int stride = (pw * bitsPerPixel + 7) / 8;
            var prev = new byte[stride];
            for (int y = 0; y < ph; y++)
            {
                var line = new byte[stride];
                for (int x = 0; x < pw; x++)
                {
                    var s = Samples(p[0] + x * p[2], p[1] + y * p[3], channels, depth, seed);
                    for (int c = 0; c < channels; c++)
                    {
                        int index = x * channels + c;
                        if (depth == 8) line[index] = (byte)s[c];
                        else if (depth == 16) { line[index * 2] = (byte)(s[c] >> 8); line[index * 2 + 1] = (byte)s[c]; }
                        else
                        {
                            int bit = index * depth;
                            line[bit >> 3] |= (byte)(s[c] << (8 - depth - (bit & 7)));
                        }
                    }
                }
                int filter = filterCounter++ % 5;
                raw.WriteByte((byte)filter);
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                    int pred = filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) >> 1,
                        _ => Paeth(a, b, c),
                    };
                    raw.WriteByte((byte)(line[i] - pred));
                }
                prev = line;
            }
        }

        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw.ToArray());

        var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = (byte)depth;
        ihdr[9] = (byte)colorType;
        ihdr[12] = (byte)(interlace ? 1 : 0);
        Chunk(png, "IHDR", ihdr);
        if (plte is not null) Chunk(png, "PLTE", plte);
        if (trns is not null) Chunk(png, "tRNS", trns);
        // Split the data over two IDAT chunks.
        var data = z.ToArray();
        Chunk(png, "IDAT", data[..(data.Length / 2)]);
        Chunk(png, "IDAT", data[(data.Length / 2)..]);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    internal static void Chunk(Stream s, string type, byte[] body)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
        s.Write(len);
        var typed = new byte[4 + body.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        body.CopyTo(typed, 4);
        s.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(typed));
        s.Write(crc);
    }

    private static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }

    /// <summary>The RGBA the decoder should produce for a pixel.</summary>
    private static (byte R, byte G, byte B, byte A) Expected(int x, int y, int colorType, int depth, int seed, byte[]? plte, byte[]? trns)
    {
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var s = Samples(x, y, channels, depth, seed);
        int max = (1 << depth) - 1;
        byte To8(int v) => depth == 16 ? (byte)(v >> 8) : (byte)(v * 255 / max);
        switch (colorType)
        {
            case 0:
            {
                byte g = To8(s[0]);
                bool transparent = trns is not null && BinaryPrimitives.ReadUInt16BigEndian(trns) == s[0];
                return (g, g, g, transparent ? (byte)0 : (byte)255);
            }
            case 2:
            {
                bool transparent = trns is not null && BinaryPrimitives.ReadUInt16BigEndian(trns) == s[0]
                    && BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2)) == s[1] && BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4)) == s[2];
                return (To8(s[0]), To8(s[1]), To8(s[2]), transparent ? (byte)0 : (byte)255);
            }
            case 3:
            {
                int i = s[0];
                return (plte![i * 3], plte[i * 3 + 1], plte[i * 3 + 2], trns is not null && i < trns.Length ? trns[i] : (byte)255);
            }
            case 4:
            {
                byte g = To8(s[0]);
                return (g, g, g, To8(s[1]));
            }
            default:
                return (To8(s[0]), To8(s[1]), To8(s[2]), To8(s[3]));
        }
    }

    public static IEnumerable<(int ColorType, int Depth, bool Interlace)> Formats()
    {
        (int Type, int[] Depths)[] formats = [(0, [1, 2, 4, 8, 16]), (2, [8, 16]), (3, [1, 2, 4, 8]), (4, [8, 16]), (6, [8, 16])];
        foreach (var (type, depths) in formats)
            foreach (int d in depths)
                foreach (bool interlace in new[] { false, true })
                    yield return (type, d, interlace);
    }

    private const int SampleWidth = 13, SampleHeight = 11; // odd sizes exercise partial bytes and empty Adam7 passes

    /// <summary>A sample image in the given format (palette and tRNS where the format has them) and the RGBA it should decode to.</summary>
    private static (byte[] Png, byte[] ExpectedRgba) Sample(int colorType, int depth, bool interlace)
    {
        const int w = SampleWidth, h = SampleHeight;
        int seed = colorType * 100 + depth;
        byte[]? plte = null, trns = null;
        if (colorType == 3)
        {
            int entries = 1 << depth;
            plte = new byte[entries * 3];
            for (int i = 0; i < plte.Length; i++) plte[i] = (byte)(i * 37 + 11);
            trns = new byte[Math.Max(1, entries / 2)]; // shorter than the palette: the rest is opaque
            for (int i = 0; i < trns.Length; i++) trns[i] = (byte)(i * 50);
        }
        else if (colorType == 0)
        {
            var s = Samples(3, 2, 1, depth, seed);
            trns = [(byte)(s[0] >> 8), (byte)s[0]];
        }
        else if (colorType == 2)
        {
            var s = Samples(5, 4, 3, depth, seed);
            trns = [(byte)(s[0] >> 8), (byte)s[0], (byte)(s[1] >> 8), (byte)s[1], (byte)(s[2] >> 8), (byte)s[2]];
        }

        var expected = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var e = Expected(x, y, colorType, depth, seed, plte, trns);
                int o = (y * w + x) * 4;
                (expected[o], expected[o + 1], expected[o + 2], expected[o + 3]) = (e.R, e.G, e.B, e.A);
            }
        return (Encode(w, h, colorType, depth, interlace, seed, plte, trns), expected);
    }

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task Decodes_every_color_type_and_bit_depth(int colorType, int depth, bool interlace)
    {
        // Arrange
        var (png, expected) = Sample(colorType, depth, interlace);

        // Act
        var img = Png.Decode(png);

        // Assert
        // The RGBA is compared as one sequence: a mismatch reports its index (pixel = index / 4).
        await Assert.That(img.Width).IsEqualTo(SampleWidth);
        await Assert.That(img.Height).IsEqualTo(SampleHeight);
        await Assert.That(img.Rgba).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task Reads_the_size_of_every_color_type_and_bit_depth(int colorType, int depth, bool interlace)
    {
        // Arrange
        var (png, _) = Sample(colorType, depth, interlace);

        // Act
        bool found = Png.TryGetSize(png, out int width, out int height);

        // Assert
        await Assert.That((found, width, height)).IsEqualTo((true, SampleWidth, SampleHeight));
    }

    [Test]
    public async Task Rejects_decompression_bombs_before_inflating()
    {
        // Arrange
        // A header claiming 100k x 100k pixels: rejected from the header, nothing is allocated for it.
        var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 100_000);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IEND", []);

        // Act
        var act = () => Png.Decode(png.ToArray());

        // Assert
        await Assert.That(act).ThrowsExactly<ImageDecodeException>().WithMessageContaining("too large");
    }

    [Test]
    public async Task Rejects_truncated_data()
    {
        // Arrange
        var png = Encode(8, 8, 6, 8, false, 1);

        // Act
        var act = () => Png.Decode(png.AsSpan(0, png.Length - 30));

        // Assert
        await Assert.That(act).ThrowsExactly<ImageDecodeException>();
    }

    [Test]
    public async Task Rejects_data_that_is_not_a_png()
    {
        // Act
        var act = () => Png.Decode("not a png at all"u8);

        // Assert
        await Assert.That(act).ThrowsExactly<ImageDecodeException>();
    }

    [Test]
    public async Task Decodes_a_gif_with_transparency()
    {
        // Arrange
        // 1×1 GIF89a whose only pixel is the transparent index.
        var gif = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

        // Act
        var img = ImageDecoder.Decode(gif);

        // Assert
        await Assert.That((img.Width, img.Height)).IsEqualTo((1, 1));
        await Assert.That(img.Rgba[3]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Unknown_formats_become_a_placeholder()
    {
        // Act
        var img = ImageDecoder.DecodeOrPlaceholder([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]); // a JPEG header

        // Assert
        await Assert.That(img.Width).IsGreaterThan(0);
        await Assert.That(img.Height).IsGreaterThan(0);
    }

    [Test]
    [Arguments("aGVsbG8=")]
    [Arguments("aGVs\nbG8")] // a line break inside, no padding
    public async Task Lenient_base64(string encoded)
    {
        // Act
        var decoded = ImageDecoder.DecodeBase64(System.Text.Encoding.ASCII.GetBytes(encoded));

        // Assert
        await Assert.That(decoded).IsEquivalentTo("hello"u8.ToArray(), CollectionOrdering.Matching);
    }

    /// <summary>A PNG for other tests: RGBA 8-bit with the given pixels.</summary>
    internal static byte[] EncodeRgba(int w, int h, Func<int, int, uint> rgba)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++)
            {
                uint p = rgba(x, y);
                raw.WriteByte((byte)(p >> 24)); raw.WriteByte((byte)(p >> 16)); raw.WriteByte((byte)(p >> 8)); raw.WriteByte((byte)p);
            }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw.ToArray());
        var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", z.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }
}
