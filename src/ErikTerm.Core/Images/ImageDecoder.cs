using System.Buffers.Text;

namespace ErikTerm.Core.Images;

/// <summary>Decodes the image files programs send (iTerm2 accepts any format): PNG, GIF and JPEG; anything else becomes a placeholder.</summary>
public static class ImageDecoder
{
    /// <summary>
    /// Decodes a PNG, GIF (first frame) or JPEG (baseline / progressive, EXIF orientation applied); other formats throw.
    /// Any failure inside a decoder (a bug a crafted file reaches: an overflow, an index out of range, an allocation
    /// that fails) also comes out as an <see cref="ImageDecodeException"/>, so it can never escape a terminal's Feed.
    /// </summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> file)
    {
        try
        {
            if (Png.IsPng(file)) return Png.Decode(file);
            if (Gif.IsGif(file)) return Gif.Decode(file);
            if (Jpeg.IsJpeg(file)) return Jpeg.Decode(file);
        }
        catch (ImageDecodeException) { throw; }
        catch (Exception e) when (e is not ThreadInterruptedException) { throw new ImageDecodeException("corrupt image"); }
        throw new ImageDecodeException("unsupported image format");
    }

    /// <summary>Like <see cref="Decode"/>, but a format we can't read becomes a small "unsupported image" box.</summary>
    public static DecodedImage DecodeOrPlaceholder(ReadOnlySpan<byte> file)
    {
        try { return Decode(file); }
        catch (ImageDecodeException) { return Unsupported(); }
    }

    /// <summary>A 48×32 gray box with a border and a cross, shown for images that can't be decoded.</summary>
    public static DecodedImage Unsupported()
    {
        const int w = 48, h = 32;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool border = x < 2 || y < 2 || x >= w - 2 || y >= h - 2;
                // Two diagonals, ~2px thick.
                float fx = x / (float)(w - 1), fy = y / (float)(h - 1);
                bool cross = MathF.Abs(fx - fy) * h < 1.2f || MathF.Abs(fx - (1 - fy)) * h < 1.2f;
                int o = (y * w + x) * 4;
                byte v = border || cross ? (byte)170 : (byte)70;
                px[o] = px[o + 1] = px[o + 2] = v;
                px[o + 3] = border || cross ? (byte)230 : (byte)140;
            }
        return new DecodedImage(w, h, px);
    }

    /// <summary>
    /// Lenient base64 decoding: whitespace and other junk is skipped and missing padding is tolerated
    /// (both happen with hand-written scripts). Returns null on garbage.
    /// </summary>
    public static byte[]? DecodeBase64(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty) return [];
        // Fast path: already clean.
        var buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(text.Length + 3)];
        if (Base64.DecodeFromUtf8(text, buffer, out _, out int written) == System.Buffers.OperationStatus.Done)
            return written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();

        var clean = new byte[text.Length + 3];
        int n = KeepBase64Alphabet(text, clean);
        if (n % 4 == 1) n--; // a dangling sextet carries no full byte
        while (n % 4 != 0) clean[n++] = (byte)'=';
        if (Base64.DecodeFromUtf8(clean.AsSpan(0, n), buffer, out _, out written) != System.Buffers.OperationStatus.Done) return null;
        return buffer.AsSpan(0, written).ToArray();
    }

    /// <summary>Copies the base64 characters of <paramref name="text"/> into <paramref name="clean"/>, mapping the URL-safe alphabet; returns the count.</summary>
    private static int KeepBase64Alphabet(ReadOnlySpan<byte> text, Span<byte> clean)
    {
        int n = 0;
        foreach (byte b in text)
        {
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'+' or (byte)'/')
                clean[n++] = b;
            else if (b == '-') clean[n++] = (byte)'+'; // URL-safe alphabet
            else if (b == '_') clean[n++] = (byte)'/';
        }
        return n;
    }
}
