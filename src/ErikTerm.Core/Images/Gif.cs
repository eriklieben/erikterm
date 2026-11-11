namespace ErikTerm.Core.Images;

/// <summary>
/// GIF decoder for the first frame (no animation): global/local color tables, transparency from the graphic
/// control extension, interlacing. The frame is composed onto a transparent canvas of the logical screen size.
/// </summary>
public static class Gif
{
    public static bool IsGif(ReadOnlySpan<byte> data) =>
        data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8));

    public static DecodedImage Decode(ReadOnlySpan<byte> d)
    {
        if (!IsGif(d) || d.Length < 13) throw new ImageDecodeException("not a GIF");
        int sw = d[6] | d[7] << 8, sh = d[8] | d[9] << 8;
        int flags = d[10];
        int pos = 13;
        byte[]? global = (flags & 0x80) != 0 ? ReadColorTable(d, ref pos, flags) : null;
        int transparent = -1;

        while (pos < d.Length)
        {
            byte b = d[pos++];
            if (b == 0x21) // extension
            {
                if (pos >= d.Length) break;
                pos = ReadExtension(d, pos, ref transparent);
            }
            else if (b == 0x2C) return DecodeFrame(d, pos, sw, sh, global, transparent); // image descriptor
            else if (b == 0x3B) break; // trailer
            else throw new ImageDecodeException("bad GIF block");
        }
        throw new ImageDecodeException("GIF without an image");
    }

    /// <summary>Reads the 3 &lt;&lt; (size + 1) byte color table announced by <paramref name="flags"/> at <paramref name="pos"/>.</summary>
    private static byte[] ReadColorTable(ReadOnlySpan<byte> d, ref int pos, int flags)
    {
        int n = 3 << ((flags & 7) + 1);
        if (pos + n > d.Length) throw new ImageDecodeException("truncated GIF");
        var table = d.Slice(pos, n).ToArray();
        pos += n;
        return table;
    }

    /// <summary>Consumes an extension (its label is at <paramref name="pos"/>), noting a graphic control's transparent index.</summary>
    private static int ReadExtension(ReadOnlySpan<byte> d, int pos, ref int transparent)
    {
        byte label = d[pos++];
        if (label == 0xF9 && pos + 5 < d.Length && d[pos] >= 4 && (d[pos + 1] & 1) != 0) transparent = d[pos + 4];
        return SkipBlocks(d, pos);
    }

    private readonly record struct Frame(int X, int Y, int Width, int Height, bool Interlaced);

    /// <summary>The RGBA canvas of the logical screen the frame is painted onto.</summary>
    private readonly record struct Canvas(byte[] Rgba, int Width, int Height);

    /// <summary>Decodes the frame whose descriptor starts at <paramref name="pos"/> onto a canvas of the logical screen size.</summary>
    private static DecodedImage DecodeFrame(ReadOnlySpan<byte> d, int pos, int sw, int sh, byte[]? global, int transparent)
    {
        if (pos + 9 > d.Length) throw new ImageDecodeException("truncated GIF");
        int fx = d[pos] | d[pos + 1] << 8, fy = d[pos + 2] | d[pos + 3] << 8;
        int fw = d[pos + 4] | d[pos + 5] << 8, fh = d[pos + 6] | d[pos + 7] << 8;
        int f = d[pos + 8];
        pos += 9;
        byte[]? table = (f & 0x80) != 0 ? ReadColorTable(d, ref pos, f) : global;
        if (table is null) throw new ImageDecodeException("GIF without a color table");
        if (sw == 0 || sh == 0) { sw = fx + fw; sh = fy + fh; }
        if (sw <= 0 || sh <= 0 || (long)sw * sh > ImageLimits.MaxPixels) throw new ImageDecodeException("GIF size out of range");
        if ((long)fw * fh > ImageLimits.MaxPixels) throw new ImageDecodeException("GIF frame size out of range");
        if (pos >= d.Length) throw new ImageDecodeException("truncated GIF");
        int minCode = d[pos++];
        if (minCode is < 1 or > 11) throw new ImageDecodeException("bad GIF LZW code size");

        var indices = Lzw(GatherSubBlocks(d, pos), minCode, fw * fh);
        var rgba = new byte[sw * sh * 4];
        PaintFrame(indices, new Frame(fx, fy, fw, fh, (f & 0x40) != 0), table, transparent, new Canvas(rgba, sw, sh));
        return new DecodedImage(sw, sh, rgba);
    }

    private static List<byte> GatherSubBlocks(ReadOnlySpan<byte> d, int pos)
    {
        var lzw = new List<byte>();
        while (pos < d.Length)
        {
            int len = d[pos++];
            if (len == 0) break;
            if (pos + len > d.Length) len = d.Length - pos;
            for (int i = 0; i < len; i++) lzw.Add(d[pos + i]);
            pos += len;
        }
        return lzw;
    }

    /// <summary>Writes the frame's indices row by row, in interlace order if the frame is interlaced.</summary>
    private static void PaintFrame(byte[] indices, Frame frame, byte[] table, int transparent, in Canvas canvas)
    {
        int k = 0;
        for (int pass = 0, row = 0; k < indices.Length && row < frame.Height;)
        {
            k = PaintRow(indices, k, frame, row, table, transparent, canvas);
            if (!frame.Interlaced) row++;
            else if (!NextInterlacedRow(ref row, ref pass, frame.Height)) break;
        }
    }

    /// <summary>Paints one row of the frame starting at index <paramref name="k"/>; returns the next index.</summary>
    private static int PaintRow(byte[] indices, int k, Frame frame, int y, byte[] table, int transparent, in Canvas canvas)
    {
        byte[] rgba = canvas.Rgba;
        int sw = canvas.Width, sh = canvas.Height;
        for (int x = 0; x < frame.Width && k < indices.Length; x++, k++)
        {
            int ci = indices[k];
            int px = frame.X + x, py = frame.Y + y;
            if (ci == transparent || px >= sw || py >= sh || ci * 3 + 2 >= table.Length) continue;
            int o = (py * sw + px) * 4;
            rgba[o] = table[ci * 3];
            rgba[o + 1] = table[ci * 3 + 1];
            rgba[o + 2] = table[ci * 3 + 2];
            rgba[o + 3] = 255;
        }
        return k;
    }

    /// <summary>Moves to the next row of the four-pass interlace; false when the passes are done.</summary>
    private static bool NextInterlacedRow(ref int row, ref int pass, int height)
    {
        ReadOnlySpan<int> start = [0, 4, 2, 1], step = [8, 8, 4, 2];
        row += step[pass];
        while (row >= height && ++pass < 4) row = start[pass];
        return pass < 4;
    }

    private static int SkipBlocks(ReadOnlySpan<byte> d, int pos)
    {
        while (pos < d.Length)
        {
            int len = d[pos++];
            if (len == 0) break;
            pos += len;
        }
        return pos;
    }

    private static byte[] Lzw(List<byte> data, int minCode, int pixels)
    {
        var decoder = new LzwDecoder(minCode, pixels);
        int bits = 0, acc = 0;
        foreach (byte b in data)
        {
            acc |= b << bits;
            bits += 8;
            while (bits >= decoder.CodeSize)
            {
                int code = acc & ((1 << decoder.CodeSize) - 1);
                acc >>= decoder.CodeSize;
                bits -= decoder.CodeSize;
                if (!decoder.Accept(code)) return decoder.Output;
            }
        }
        return decoder.Output;
    }

    /// <summary>The code table of a GIF LZW stream and the pixel indices decoded so far.</summary>
    private sealed class LzwDecoder
    {
        public readonly byte[] Output;
        public int CodeSize;
        private readonly int _minCode, _clear, _end, _pixels;
        private readonly short[] _prefix = new short[4096];
        private readonly byte[] _suffix = new byte[4096];
        private readonly byte[] _stack = new byte[4097];
        private int _next, _old = -1, _outPos;
        private byte _first;

        public LzwDecoder(int minCode, int pixels)
        {
            _minCode = minCode;
            _pixels = pixels;
            Output = new byte[pixels];
            _clear = 1 << minCode;
            _end = _clear + 1;
            CodeSize = minCode + 1;
            _next = _end + 1;
            for (int i = 0; i < _clear; i++) { _prefix[i] = -1; _suffix[i] = (byte)i; }
        }

        /// <summary>Handles one code; false when the stream ends here (end code, output full, or corrupt).</summary>
        public bool Accept(int code)
        {
            if (code == _clear) { CodeSize = _minCode + 1; _next = _end + 1; _old = -1; return true; }
            if (code == _end) return false;
            return _old == -1 ? AcceptFirst(code) : AcceptNext(code);
        }

        private bool AcceptFirst(int code)
        {
            if (code >= _clear) return false;
            if (_outPos < _pixels) Output[_outPos++] = (byte)code;
            _old = code;
            _first = (byte)code;
            return true;
        }

        private bool AcceptNext(int code)
        {
            int sp = 0, c = code;
            if (code >= _next)
            {
                if (code > _next) return false; // corrupt
                _stack[sp++] = _first;
                c = _old;
            }
            while (c >= _clear && sp < 4096) { _stack[sp++] = _suffix[c]; c = _prefix[c]; }
            _first = (byte)c;
            _stack[sp++] = _first;
            while (sp > 0 && _outPos < _pixels) Output[_outPos++] = _stack[--sp];
            if (_outPos >= _pixels) return false;
            if (_next < 4096)
            {
                _prefix[_next] = (short)_old;
                _suffix[_next] = _first;
                _next++;
                if (_next == 1 << CodeSize && CodeSize < 12) CodeSize++;
            }
            _old = code;
            return true;
        }
    }
}
