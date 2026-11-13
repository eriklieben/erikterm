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

        var rgba = new byte[sw * sh * 4];
        var painter = new FramePainter(new Frame(fx, fy, fw, fh, (f & 0x40) != 0), table, transparent, new Canvas(rgba, sw, sh));
        Lzw(GatherSubBlocks(d, pos), minCode, painter);
        painter.Finish();
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

    /// <summary>
    /// Takes the frame's color indices as the LZW stream produces them and paints them row by row (in interlace order
    /// if the frame is interlaced), so only one row of indices is ever held: the frame descriptor's size, which costs
    /// a file nothing to declare, only decides how many indices are read, not what is allocated.
    /// </summary>
    private sealed class FramePainter(Frame frame, byte[] table, int transparent, Canvas canvas)
    {
        private readonly byte[] _indices = new byte[frame.Width];
        private int _x, _row, _pass;

        /// <summary>Every row of the frame is painted: the stream's remaining codes are ignored.</summary>
        public bool Full { get; private set; } = frame.Width == 0 || frame.Height == 0;

        public void Put(byte index)
        {
            _indices[_x++] = index;
            if (_x == _indices.Length) NextRow();
        }

        /// <summary>The stream ended early: the pixels it didn't reach get index 0, as in a frame of zeros.</summary>
        public void Finish()
        {
            if (Full) return;
            _indices.AsSpan(_x).Clear();
            NextRow();
            _indices.AsSpan().Clear();
            while (!Full) NextRow();
        }

        private void NextRow()
        {
            PaintRow(_row);
            _x = 0;
            if (!frame.Interlaced) Full = ++_row >= frame.Height;
            else Full = !NextInterlacedRow(ref _row, ref _pass, frame.Height);
        }

        /// <summary>Paints the indices as frame row <paramref name="y"/>, clipped to the canvas.</summary>
        private void PaintRow(int y)
        {
            int py = frame.Y + y;
            if (py >= canvas.Height) return;
            byte[] rgba = canvas.Rgba;
            int visible = Math.Min(frame.Width, canvas.Width - frame.X);
            for (int x = 0; x < visible; x++)
            {
                int ci = _indices[x];
                if (ci == transparent || ci * 3 + 2 >= table.Length) continue;
                int o = (py * canvas.Width + frame.X + x) * 4;
                rgba[o] = table[ci * 3];
                rgba[o + 1] = table[ci * 3 + 1];
                rgba[o + 2] = table[ci * 3 + 2];
                rgba[o + 3] = 255;
            }
        }
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

    private static void Lzw(List<byte> data, int minCode, FramePainter painter)
    {
        var decoder = new LzwDecoder(minCode, painter);
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
                if (!decoder.Accept(code)) return;
            }
        }
    }

    /// <summary>The code table of a GIF LZW stream; the pixel indices it decodes go to a <see cref="FramePainter"/>.</summary>
    private sealed class LzwDecoder
    {
        public int CodeSize;
        private readonly FramePainter _output;
        private readonly int _minCode, _clear, _end;
        private readonly short[] _prefix = new short[4096];
        private readonly byte[] _suffix = new byte[4096];
        private readonly byte[] _stack = new byte[4097];
        private int _next, _old = -1;
        private byte _first;

        public LzwDecoder(int minCode, FramePainter output)
        {
            _minCode = minCode;
            _output = output;
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
            if (!_output.Full) _output.Put((byte)code);
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
            while (sp > 0 && !_output.Full) _output.Put(_stack[--sp]);
            if (_output.Full) return false;
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
