using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace ErikTerm.Core.Images;

/// <summary>A decoded picture: straight (non-premultiplied) RGBA, 4 bytes per pixel, rows top to bottom.</summary>
public sealed class DecodedImage(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgba { get; } = rgba;
}

/// <summary>Why an image could not be decoded (the message ends up in kitty error replies).</summary>
public sealed class ImageDecodeException(string message) : Exception(message);

/// <summary>
/// PNG decoder (https://www.w3.org/TR/png-3/): every color type and bit depth (1, 2, 4, 8, 16), palettes with
/// tRNS transparency, and Adam7 interlacing. Ancillary chunks other than tRNS are ignored (no gamma / color
/// management) and CRCs are not checked. Output is 8-bit RGBA (16-bit samples keep their high byte).
/// Decompression is bounded by the size the header announces, which is capped at <see cref="ImageLimits.MaxPixels"/>.
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

    /// <summary>Reads the dimensions from the IHDR chunk without decoding, or false if this isn't a PNG.</summary>
    public static bool TryGetSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 24 || !IsPng(data) || !data.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        width = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data[16..]));
        height = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(data[20..]));
        return true;
    }

    public static DecodedImage Decode(ReadOnlySpan<byte> data)
    {
        if (!IsPng(data)) throw new ImageDecodeException("not a PNG");
        var idat = new MemoryStream();
        var info = ReadChunks(data, idat);
        if (!info.HasHeader) throw new ImageDecodeException("PNG without IHDR");
        if (info.ColorType == 3 && info.Palette is null) throw new ImageDecodeException("palette PNG without PLTE");

        int channels = info.ColorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bitsPerPixel = channels * info.Depth;
        int bpp = Math.Max(1, bitsPerPixel / 8); // filter byte distance

        long expected = ExpectedRawSize(info, bitsPerPixel);
        if (expected > int.MaxValue) throw new ImageDecodeException("PNG too large");
        byte[] raw = Inflate(idat, expected);

        int width = info.Width, height = info.Height;
        var rgba = new byte[(long)width * height * 4];
        var ctx = new Context(width, info.Depth, info.ColorType, info.Palette, info.PaletteAlpha, info.TransGray, info.TransR, info.TransG, info.TransB, rgba);
        if (info.Interlace == 0) Unfilter(raw, 0, width, height, bitsPerPixel, bpp, 0, 0, 1, 1, ctx);
        else UnfilterInterlaced(raw, width, height, bitsPerPixel, bpp, ctx);
        return new DecodedImage(width, height, rgba);
    }

    /// <summary>What the chunks before the image data say.</summary>
    private struct ImageInfo
    {
        public bool HasHeader;
        public int Width, Height, Depth, ColorType, Interlace;
        public byte[]? Palette;     // RGB triples
        public byte[]? PaletteAlpha;
        public int TransGray, TransR, TransG, TransB;
    }

    /// <summary>Walks the chunks up to IEND, collecting the header and palette fields and appending IDAT bodies to <paramref name="idat"/>.</summary>
    private static ImageInfo ReadChunks(ReadOnlySpan<byte> data, MemoryStream idat)
    {
        var info = new ImageInfo { ColorType = -1, TransGray = -1, TransR = -1, TransG = -1, TransB = -1 };
        int pos = 8;
        while (pos + 8 <= data.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            var type = data.Slice(pos + 4, 4);
            pos += 8;
            if (len > (uint)(data.Length - pos)) throw new ImageDecodeException("truncated PNG chunk");
            var body = data.Slice(pos, (int)len);
            pos += (int)len + 4; // + CRC

            if (type.SequenceEqual("IHDR"u8)) ReadHeader(body, ref info);
            else if (type.SequenceEqual("PLTE"u8)) info.Palette = body[..(body.Length / 3 * 3)].ToArray();
            else if (type.SequenceEqual("tRNS"u8)) ReadTransparency(body, ref info);
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (idat.Length + body.Length > ImageLimits.MaxEncodedBytes) throw new ImageDecodeException("PNG data too large");
                idat.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8)) break;
        }
        return info;
    }

    private static void ReadHeader(ReadOnlySpan<byte> body, ref ImageInfo info)
    {
        if (body.Length < 13) throw new ImageDecodeException("bad IHDR");
        uint w = BinaryPrimitives.ReadUInt32BigEndian(body);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
        info.Depth = body[8];
        info.ColorType = body[9];
        info.Interlace = body[12];
        if (w == 0 || h == 0) throw new ImageDecodeException("PNG has no pixels");
        if ((ulong)w * h > ImageLimits.MaxPixels || w > ImageLimits.MaxDimension || h > ImageLimits.MaxDimension)
            throw new ImageDecodeException($"PNG too large ({w}x{h})");
        if (body[10] != 0 || body[11] != 0 || info.Interlace > 1) throw new ImageDecodeException("unsupported PNG compression/filter/interlace method");
        if (!IsValidFormat(info.ColorType, info.Depth)) throw new ImageDecodeException($"invalid PNG color type {info.ColorType} / bit depth {info.Depth}");
        info.Width = (int)w;
        info.Height = (int)h;
        info.HasHeader = true;
    }

    private static bool IsValidFormat(int colorType, int depth) => colorType switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        2 or 4 or 6 => depth is 8 or 16,
        _ => false,
    };

    private static void ReadTransparency(ReadOnlySpan<byte> body, ref ImageInfo info)
    {
        if (info.ColorType == 3) info.PaletteAlpha = body.ToArray();
        else if (info.ColorType == 0 && body.Length >= 2) info.TransGray = BinaryPrimitives.ReadUInt16BigEndian(body);
        else if (info.ColorType == 2 && body.Length >= 6)
        {
            info.TransR = BinaryPrimitives.ReadUInt16BigEndian(body);
            info.TransG = BinaryPrimitives.ReadUInt16BigEndian(body[2..]);
            info.TransB = BinaryPrimitives.ReadUInt16BigEndian(body[4..]);
        }
    }

    /// <summary>Size of the decompressed stream: one filter byte + packed samples per scanline, for each pass.</summary>
    private static long ExpectedRawSize(in ImageInfo info, int bitsPerPixel)
    {
        if (info.Interlace == 0) return (long)info.Height * (1 + RowBytes(info.Width, bitsPerPixel));
        long expected = 0;
        for (int p = 0; p < 7; p++)
        {
            var (pw, ph) = PassSize(p, info.Width, info.Height);
            if (pw > 0 && ph > 0) expected += (long)ph * (1 + RowBytes(pw, bitsPerPixel));
        }
        return expected;
    }

    private static byte[] Inflate(MemoryStream idat, long expected)
    {
        var raw = new byte[expected];
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        int got = 0;
        try
        {
            while (got < raw.Length)
            {
                int n = z.Read(raw, got, raw.Length - got);
                if (n == 0) break;
                got += n;
            }
        }
        catch (InvalidDataException) { throw new ImageDecodeException("corrupt PNG data"); }
        if (got < raw.Length) throw new ImageDecodeException("truncated PNG data");
        return raw;
    }

    private static void UnfilterInterlaced(byte[] raw, int width, int height, int bitsPerPixel, int bpp, Context ctx)
    {
        int offset = 0;
        for (int p = 0; p < 7; p++)
        {
            var (pw, ph) = PassSize(p, width, height);
            if (pw == 0 || ph == 0) continue;
            int x0 = Adam7[p * 4], y0 = Adam7[p * 4 + 1], dx = Adam7[p * 4 + 2], dy = Adam7[p * 4 + 3];
            offset = Unfilter(raw, offset, pw, ph, bitsPerPixel, bpp, x0, y0, dx, dy, ctx);
        }
    }

    // Adam7 passes: x start, y start, x step, y step.
    private static ReadOnlySpan<int> Adam7 => [0, 0, 8, 8, 4, 0, 8, 8, 0, 4, 4, 8, 2, 0, 4, 4, 0, 2, 2, 4, 1, 0, 2, 2, 0, 1, 1, 2];

    private static (int W, int H) PassSize(int pass, int width, int height)
    {
        int x0 = Adam7[pass * 4], y0 = Adam7[pass * 4 + 1], dx = Adam7[pass * 4 + 2], dy = Adam7[pass * 4 + 3];
        int w = width > x0 ? (width - x0 + dx - 1) / dx : 0;
        int h = height > y0 ? (height - y0 + dy - 1) / dy : 0;
        return (w, h);
    }

    private static long RowBytes(int width, int bitsPerPixel) => ((long)width * bitsPerPixel + 7) / 8;

    private sealed record Context(int Width, int Depth, int ColorType, byte[]? Palette, byte[]? PaletteAlpha,
        int TransGray, int TransR, int TransG, int TransB, byte[] Rgba);

    /// <summary>Reverses the scanline filters of one (sub)image in place and writes its pixels; returns the end offset.</summary>
    private static int Unfilter(byte[] raw, int offset, int w, int h, int bitsPerPixel, int bpp, int x0, int y0, int dx, int dy, Context ctx)
    {
        int stride = (int)RowBytes(w, bitsPerPixel);
        int prev = -1;
        for (int y = 0; y < h; y++)
        {
            int filter = raw[offset];
            int cur = offset + 1;
            var line = raw.AsSpan(cur, stride);
            var up = prev >= 0 ? raw.AsSpan(prev, stride) : default;
            ReverseFilter(filter, line, up, bpp);
            long rowBase = (long)(y0 + y * dy) * ctx.Width;
            for (int x = 0; x < w; x++) WritePixel(line, x, ctx, (rowBase + x0 + x * dx) * 4);
            prev = cur;
            offset = cur + stride;
        }
        return offset;
    }

    /// <summary>Undoes a scanline filter in place; <paramref name="up"/> is empty on the first line of a pass.</summary>
    private static void ReverseFilter(int filter, Span<byte> line, ReadOnlySpan<byte> up, int bpp)
    {
        switch (filter)
        {
            case 0: break;
            case 1: ReverseSub(line, bpp); break;
            case 2: ReverseUp(line, up); break;
            case 3: ReverseAverage(line, up, bpp); break;
            case 4: ReversePaeth(line, up, bpp); break;
            default: throw new ImageDecodeException($"bad PNG filter type {filter}");
        }
    }

    private static void ReverseSub(Span<byte> line, int bpp)
    {
        for (int i = bpp; i < line.Length; i++) line[i] += line[i - bpp];
    }

    private static void ReverseUp(Span<byte> line, ReadOnlySpan<byte> up)
    {
        if (up.IsEmpty) return;
        for (int i = 0; i < line.Length; i++) line[i] += up[i];
    }

    private static void ReverseAverage(Span<byte> line, ReadOnlySpan<byte> up, int bpp)
    {
        bool hasUp = !up.IsEmpty;
        for (int i = 0; i < line.Length; i++)
        {
            int a = i >= bpp ? line[i - bpp] : 0, b = hasUp ? up[i] : 0;
            line[i] += (byte)((a + b) >> 1);
        }
    }

    private static void ReversePaeth(Span<byte> line, ReadOnlySpan<byte> up, int bpp)
    {
        bool hasUp = !up.IsEmpty;
        for (int i = 0; i < line.Length; i++)
        {
            int a = i >= bpp ? line[i - bpp] : 0, b = hasUp ? up[i] : 0, c = i >= bpp && hasUp ? up[i - bpp] : 0;
            line[i] += (byte)Paeth(a, b, c);
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    /// <summary>Sample <paramref name="index"/> (0-based, in units of the bit depth) of a scanline, scaled to 16 bits.</summary>
    private static int Sample(ReadOnlySpan<byte> line, int index, int depth)
    {
        switch (depth)
        {
            case 8: return line[index] * 257;
            case 16: return line[index * 2] << 8 | line[index * 2 + 1];
            default:
                int bit = index * depth;
                int v = (line[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
                return v * 65535 / ((1 << depth) - 1);
        }
    }

    private static int RawSample(ReadOnlySpan<byte> line, int index, int depth)
    {
        if (depth == 8) return line[index];
        if (depth == 16) return line[index * 2] << 8 | line[index * 2 + 1];
        int bit = index * depth;
        return (line[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
    }

    private static void WritePixel(ReadOnlySpan<byte> line, int x, Context c, long o)
    {
        var dst = c.Rgba;
        int d = c.Depth;
        switch (c.ColorType)
        {
            case 0:
            {
                int raw = RawSample(line, x, d);
                byte g = (byte)(Sample(line, x, d) >> 8);
                dst[o] = dst[o + 1] = dst[o + 2] = g;
                dst[o + 3] = raw == c.TransGray ? (byte)0 : (byte)255;
                break;
            }
            case 2:
                WriteRgbPixel(line, x, c, o);
                break;
            case 3:
                WritePalettePixel(line, x, c, o);
                break;
            case 4:
            {
                byte g = (byte)(Sample(line, x * 2, d) >> 8);
                dst[o] = dst[o + 1] = dst[o + 2] = g;
                dst[o + 3] = (byte)(Sample(line, x * 2 + 1, d) >> 8);
                break;
            }
            default:
                for (int k = 0; k < 4; k++) dst[o + k] = (byte)(Sample(line, x * 4 + k, d) >> 8);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteRgbPixel(ReadOnlySpan<byte> line, int x, Context c, long o)
    {
        var dst = c.Rgba;
        int d = c.Depth;
        int r = RawSample(line, x * 3, d), g = RawSample(line, x * 3 + 1, d), b = RawSample(line, x * 3 + 2, d);
        dst[o] = (byte)(d == 16 ? r >> 8 : r);
        dst[o + 1] = (byte)(d == 16 ? g >> 8 : g);
        dst[o + 2] = (byte)(d == 16 ? b >> 8 : b);
        dst[o + 3] = r == c.TransR && g == c.TransG && b == c.TransB ? (byte)0 : (byte)255;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePalettePixel(ReadOnlySpan<byte> line, int x, Context c, long o)
    {
        var dst = c.Rgba;
        int i = RawSample(line, x, c.Depth);
        var pal = c.Palette!;
        if (i * 3 + 2 < pal.Length)
        {
            dst[o] = pal[i * 3];
            dst[o + 1] = pal[i * 3 + 1];
            dst[o + 2] = pal[i * 3 + 2];
        }
        dst[o + 3] = c.PaletteAlpha is { } pa && i < pa.Length ? pa[i] : (byte)255;
    }
}

/// <summary>Limits that keep hostile or broken images from exhausting memory.</summary>
public static class ImageLimits
{
    /// <summary>Largest decoded image, in pixels (50 MP = 200 MB of RGBA).</summary>
    public const long MaxPixels = 50_000_000;
    public const int MaxDimension = 32_768;
    /// <summary>Largest encoded payload (after base64) we accept for one image.</summary>
    public const int MaxEncodedBytes = 64 * 1024 * 1024;
}
