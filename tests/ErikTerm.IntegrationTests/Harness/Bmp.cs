using System.Buffers.Binary;

namespace ErikTerm.IntegrationTests.Harness;

/// <summary>
/// A screenshot (SDL_SaveBMP: uncompressed or BI_BITFIELDS, 24 or 32 bits per pixel), read just far enough to check
/// that a frame shows something and to compare two frames.
/// </summary>
internal sealed class Bmp
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>0xRRGGBB per pixel, top row first.</summary>
    private readonly int[] _pixels;

    /// <summary>The alpha channel when the file has one (a translucent window's frame), else null.</summary>
    private readonly byte[]? _alpha;

    private Bmp(int width, int height, int[] pixels, byte[]? alpha)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
        _alpha = alpha;
    }

    public static Bmp Load(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 54 || b[0] != 'B' || b[1] != 'M') throw new InvalidDataException($"{path} is not a BMP");
        int offset = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(10));
        int width = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(18));
        int rawHeight = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(22));
        int bpp = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(28));
        int compression = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(30));
        if (bpp is not (24 or 32) || compression is not (0 or 3)) throw new InvalidDataException($"{path}: {bpp} bpp, compression {compression}");
        int height = Math.Abs(rawHeight);
        bool bottomUp = rawHeight > 0;
        int bytesPer = bpp / 8, stride = (width * bytesPer + 3) & ~3;
        // BI_BITFIELDS: the channel masks follow the 40-byte header (or sit in a V4/V5 header at the same place).
        uint rMask = 0xFF0000, gMask = 0xFF00, bMask = 0xFF, aMask = 0;
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(14));
        if (compression == 3)
        {
            rMask = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(54));
            gMask = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(58));
            bMask = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(62));
            if (headerSize >= 56) aMask = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(66));
        }
        var pixels = new int[width * height];
        var alpha = aMask != 0 && bytesPer == 4 ? new byte[width * height] : null;
        for (int y = 0; y < height; y++)
        {
            int row = offset + (bottomUp ? height - 1 - y : y) * stride;
            for (int x = 0; x < width; x++)
            {
                int at = row + x * bytesPer;
                uint v = bytesPer == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)) : (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16);
                pixels[y * width + x] = (int)(Channel(v, rMask) << 16 | Channel(v, gMask) << 8 | Channel(v, bMask));
                if (alpha is not null) alpha[y * width + x] = (byte)Channel(v, aMask);
            }
        }
        return new Bmp(width, height, pixels, alpha);
    }

    private static uint Channel(uint v, uint mask) =>
        mask == 0 ? 0 : (v & mask) >> System.Numerics.BitOperations.TrailingZeroCount(mask) & 0xFF;

    public int this[int x, int y] => _pixels[y * Width + x];

    /// <summary>The share of pixels that aren't fully opaque (0 without an alpha channel).</summary>
    public double TranslucentShare => _alpha is null ? 0 : _alpha.Count(a => a < 255) / (double)_alpha.Length;

    /// <summary>How many different colors the frame has (text, chrome and background give dozens at least).</summary>
    public int DistinctColors => _pixels.Distinct().Count();

    /// <summary>The share of pixels in the most common color (the background).</summary>
    public double DominantShare => _pixels.GroupBy(p => p).Max(g => g.Count()) / (double)_pixels.Length;

    /// <summary>A frame that shows something: more than a handful of colors and not a single flat color.</summary>
    public bool LooksRendered => DistinctColors >= 8 && DominantShare < 0.995;

    /// <summary>The share of pixels that differ from <paramref name="other"/> (1 when the sizes differ).</summary>
    public double DifferenceFrom(Bmp other)
    {
        if (other.Width != Width || other.Height != Height) return 1;
        int diff = 0;
        for (int i = 0; i < _pixels.Length; i++)
            if (_pixels[i] != other._pixels[i]) diff++;
        return diff / (double)_pixels.Length;
    }

    /// <summary>The share of pixels in the rectangle that aren't the frame's dominant color.</summary>
    public double InkShare(int x0, int y0, int x1, int y1)
    {
        int background = _pixels.GroupBy(p => p).MaxBy(g => g.Count())!.Key;
        int ink = 0, total = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(Width, x1); x++, total++)
                if (_pixels[y * Width + x] != background) ink++;
        return total == 0 ? 0 : ink / (double)total;
    }

    /// <summary>Whether some pixel is close to the color (each channel within <paramref name="tolerance"/>).</summary>
    public bool HasColorNear(int rgb, int tolerance = 24) => _pixels.Any(p =>
        Math.Abs((p >> 16 & 0xFF) - (rgb >> 16 & 0xFF)) <= tolerance && Math.Abs((p >> 8 & 0xFF) - (rgb >> 8 & 0xFF)) <= tolerance
        && Math.Abs((p & 0xFF) - (rgb & 0xFF)) <= tolerance);
}
