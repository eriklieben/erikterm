using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ErikTerm.Core.Images;

/// <summary>
/// JPEG decoder (ITU T.81): baseline and extended sequential (SOF0/SOF1) and progressive (SOF2) Huffman-coded
/// 8-bit images with 1 (grayscale), 3 (YCbCr, or RGB per the Adobe APP14 marker) or 4 (Adobe CMYK/YCCK)
/// components, any sampling factors, restart intervals, 8- and 16-bit quantization tables, and the EXIF
/// orientation tag. Arithmetic coding, lossless, hierarchical and 12-bit images are rejected.
/// The IDCT and color conversion follow libjpeg's integer "islow" path; chroma is upsampled by replication.
/// Corrupt entropy-coded data is decoded best-effort (the damaged part stays flat) rather than rejected.
/// Output is 8-bit RGBA with opaque alpha.
/// </summary>
public static class Jpeg
{
    private const int FastBits = 9;
    private const int MaxScans = 1000;

    /// <summary>Zigzag index to natural (row-major) index, padded so corrupt run lengths stay in bounds.</summary>
    private static ReadOnlySpan<byte> ZigZag =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63,
    ];

    public static bool IsJpeg(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    /// <summary>
    /// Reads the dimensions from the frame header without decoding, or false if this isn't a JPEG (or has no
    /// usable frame header). Width and height are swapped when the EXIF orientation transposes the image, so they
    /// match what <see cref="Decode(ReadOnlySpan{byte})"/> returns.
    /// </summary>
    public static bool TryGetSize(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (!IsJpeg(data)) return false;
        int orientation = 1;
        int pos = 2;
        while (TryReadMarker(data, ref pos, out int m))
        {
            if (IsStandaloneMarker(m)) continue;
            if (m is 0xD9 or 0xDA) return false;
            if (!TryReadSegment(data, pos, out var seg)) return false;
            if (IsFrameMarker(m)) return TryFrameSize(seg, orientation, out width, out height);
            if (m == 0xE1) orientation = OrientationOr(seg, orientation);
            pos += seg.Length + 2;
        }
        return false;
    }

    private static bool IsStandaloneMarker(int m) => m is 0 or 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7);

    /// <summary>The orientation an APP1 segment gives, or <paramref name="current"/> if it gives none.</summary>
    private static int OrientationOr(ReadOnlySpan<byte> seg, int current)
    {
        int o = ExifOrientation(seg);
        return o != 0 ? o : current;
    }

    /// <summary>Skips to the next marker and reads its code; false at the end of the data.</summary>
    private static bool TryReadMarker(ReadOnlySpan<byte> data, ref int pos, out int marker)
    {
        marker = 0;
        while (pos < data.Length && data[pos] != 0xFF) pos++;
        while (pos < data.Length && data[pos] == 0xFF) pos++;
        if (pos >= data.Length) return false;
        marker = data[pos++];
        return true;
    }

    /// <summary>The body of the segment whose length field is at <paramref name="pos"/>; false if it doesn't fit.</summary>
    private static bool TryReadSegment(ReadOnlySpan<byte> data, int pos, out ReadOnlySpan<byte> segment)
    {
        segment = default;
        if (pos + 2 > data.Length) return false;
        int len = data[pos] << 8 | data[pos + 1];
        if (len < 2 || pos + len > data.Length) return false;
        segment = data.Slice(pos + 2, len - 2);
        return true;
    }

    /// <summary>Dimensions from a frame header, swapped when <paramref name="orientation"/> transposes the image.</summary>
    private static bool TryFrameSize(ReadOnlySpan<byte> seg, int orientation, out int width, out int height)
    {
        width = height = 0;
        if (seg.Length < 5) return false;
        int h = seg[1] << 8 | seg[2], w = seg[3] << 8 | seg[4];
        if (w == 0 || h == 0) return false;
        if (orientation >= 5) (w, h) = (h, w);
        width = w;
        height = h;
        return true;
    }

    public static DecodedImage Decode(ReadOnlySpan<byte> data) => Decode(data, applyExifOrientation: true);

    /// <summary>Decodes to RGBA; with <paramref name="applyExifOrientation"/> the EXIF orientation is applied.</summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> data, bool applyExifOrientation)
    {
        if (!IsJpeg(data)) throw new ImageDecodeException("not a JPEG");
        if (data.Length > ImageLimits.MaxEncodedBytes) throw new ImageDecodeException("JPEG too large");
        return new Decoder(data.ToArray()).Run(applyExifOrientation);
    }

    private static bool IsFrameMarker(int m) => m is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);

    /// <summary>The orientation tag (1–8) from an APP1 Exif segment's IFD0, or 0.</summary>
    private static int ExifOrientation(ReadOnlySpan<byte> seg)
    {
        if (seg.Length < 14 || !seg[..6].SequenceEqual("Exif\0\0"u8)) return 0;
        var t = seg[6..];
        if (!TryExifByteOrder(t, out bool le)) return 0;
        uint ifd = le ? BinaryPrimitives.ReadUInt32LittleEndian(t[4..]) : BinaryPrimitives.ReadUInt32BigEndian(t[4..]);
        if (ifd > (uint)t.Length - 2) return 0;
        int count = (int)ExifU16(t, (int)ifd, le);
        return OrientationEntry(t, (int)ifd + 2, count, le);
    }

    private static bool TryExifByteOrder(ReadOnlySpan<byte> t, out bool littleEndian)
    {
        littleEndian = t[0] == 'I' && t[1] == 'I';
        return littleEndian || (t[0] == 'M' && t[1] == 'M');
    }

    private static uint ExifU16(ReadOnlySpan<byte> t, int o, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(t[o..]) : BinaryPrimitives.ReadUInt16BigEndian(t[o..]);

    /// <summary>Walks the IFD entries from <paramref name="p"/>; the orientation value, or 0.</summary>
    private static int OrientationEntry(ReadOnlySpan<byte> t, int p, int count, bool le)
    {
        for (int i = 0; i < count && p + 12 <= t.Length; i++, p += 12)
        {
            if (ExifU16(t, p, le) != 0x0112) continue;
            if (ExifU16(t, p + 2, le) != 3) return 0; // SHORT
            uint v = ExifU16(t, p + 8, le);
            return v is >= 1 and <= 8 ? (int)v : 0;
        }
        return 0;
    }

    private sealed class Huffman
    {
        public readonly short[] Fast = new short[1 << FastBits];
        /// <summary>
        /// For AC tables: a whole run/size symbol plus its magnitude bits within the first <see cref="FastBits"/>
        /// bits, packed as value &lt;&lt; 16 | run &lt;&lt; 8 | bits consumed; 0 when the slow path is needed.
        /// </summary>
        public readonly int[] FastAc = new int[1 << FastBits];
        public readonly byte[] Values = new byte[256];
        public readonly byte[] Sizes = new byte[256];
        /// <summary>Per code length: one past the largest code of that length.</summary>
        public readonly int[] MaxCode = new int[17];
        /// <summary>Per code length: value index minus code.</summary>
        public readonly int[] Delta = new int[17];

        public Huffman(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
        {
            Fast.AsSpan().Fill(-1);
            values.CopyTo(Values);
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                Delta[len] = k - code;
                for (int i = 0; i < counts[len - 1]; i++, k++, code++)
                {
                    if (code >= 1 << len) throw new ImageDecodeException("bad JPEG Huffman table");
                    Sizes[k] = (byte)len;
                    if (len <= FastBits)
                    {
                        int start = code << (FastBits - len);
                        Fast.AsSpan(start, 1 << (FastBits - len)).Fill((short)k);
                    }
                }
                MaxCode[len] = code;
                code <<= 1;
            }
            BuildFastAc();
        }

        private void BuildFastAc()
        {
            for (int i = 0; i < Fast.Length; i++)
            {
                int idx = Fast[i];
                if (idx < 0) continue;
                int rs = Values[idx], len = Sizes[idx], mag = rs & 15;
                if (mag == 0 || len + mag > FastBits) continue;
                int v = ((i << len) & ((1 << FastBits) - 1)) >> (FastBits - mag);
                if (v < 1 << (mag - 1)) v += (-1 << mag) + 1;
                FastAc[i] = v << 16 | (rs >> 4) << 8 | (len + mag);
            }
        }
    }

    private sealed class Component
    {
        public int Id, H, V, Tq;
        public int BlocksPerLine, BlocksPerColumn; // blocks covering the component's own extent
        public int BlocksW, BlocksH;               // allocated blocks (whole MCUs)
        public short[] Coefs = [];
        public int[]? Quant;
        public int Pred;
        public Huffman? Dc, Ac;
    }

    private enum ScanMode { Sequential, DcFirst, DcRefine, AcFirst, AcRefine }

    private sealed class Decoder(byte[] d)
    {
        private int pos;
        // Entropy-coded segment bit reader: bits are left-aligned in buf.
        private ulong buf;
        private int cnt;
        private bool marker;
        private int overrun; // bytes of zero padding fed after hitting a marker / the end (or "corrupt")

        private readonly Huffman?[] dcTables = new Huffman?[4], acTables = new Huffman?[4];
        private readonly int[]?[] quant = new int[]?[4];
        private int restartInterval;
        private Component[]? comps;
        private bool progressive;
        private int width, height, hmax, vmax, mcusX, mcusY;
        private bool jfif, adobe;
        private int adobeTransform;
        private int orientation = 1;
        private int ss, se, ah, al, eobrun;

        public DecodedImage Run(bool applyOrientation)
        {
            pos = 2;
            int scans = 0;
            bool done = false;
            while (!done)
            {
                int m = NextMarker();
                switch (m)
                {
                    case < 0 or 0xD9: done = true; break;
                    case 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7): break;
                    case 0xC0 or 0xC1: ReadFrame(Segment(), false); break;
                    case 0xC2: ReadFrame(Segment(), true); break;
                    case 0xC3 or 0xC5 or 0xC6 or 0xC7 or (>= 0xC9 and <= 0xCF and not 0xCC): throw UnsupportedFrame(m);
                    case 0xC4: ReadHuffman(Segment()); break;
                    case 0xDB: ReadQuant(Segment()); break;
                    case 0xDD: ReadRestartInterval(Segment()); break;
                    case 0xDA:
                        if (++scans > MaxScans) throw new ImageDecodeException("too many JPEG scans");
                        ReadScan(Segment());
                        break;
                    case 0xE0: ReadJfif(Segment()); break;
                    case 0xE1: orientation = OrientationOr(Segment(), orientation); break;
                    case 0xEE: ReadAdobe(Segment()); break;
                    default: Segment(); break;
                }
            }
            if (comps is null) throw new ImageDecodeException("JPEG has no frame header");
            if (scans == 0) throw new ImageDecodeException("JPEG has no image data");
            return Output(applyOrientation);
        }

        private static ImageDecodeException UnsupportedFrame(int marker) => marker switch
        {
            0xC3 => new ImageDecodeException("unsupported JPEG (lossless)"),
            0xC5 or 0xC6 or 0xC7 => new ImageDecodeException("unsupported JPEG (hierarchical)"),
            _ => new ImageDecodeException("unsupported JPEG (arithmetic coding)"),
        };

        private void ReadRestartInterval(ReadOnlySpan<byte> s)
        {
            if (s.Length < 2) throw new ImageDecodeException("bad JPEG DRI segment");
            restartInterval = s[0] << 8 | s[1];
        }

        private void ReadJfif(ReadOnlySpan<byte> s)
        {
            if (s.Length >= 5 && s[..5].SequenceEqual("JFIF\0"u8)) jfif = true;
        }

        private void ReadAdobe(ReadOnlySpan<byte> s)
        {
            if (s.Length >= 12 && s[..5].SequenceEqual("Adobe"u8)) { adobe = true; adobeTransform = s[11]; }
        }

        /// <summary>The next marker code, skipping fill bytes and stray garbage, or -1 at the end of the data.</summary>
        private int NextMarker()
        {
            while (true)
            {
                while (pos < d.Length && d[pos] != 0xFF) pos++;
                while (pos < d.Length && d[pos] == 0xFF) pos++;
                if (pos >= d.Length) return -1;
                int m = d[pos++];
                if (m != 0) return m;
            }
        }

        private ReadOnlySpan<byte> Segment()
        {
            if (pos + 2 > d.Length) throw new ImageDecodeException("truncated JPEG");
            int len = d[pos] << 8 | d[pos + 1];
            if (len < 2) throw new ImageDecodeException("bad JPEG segment length");
            if (len > d.Length - pos) throw new ImageDecodeException("truncated JPEG");
            var s = d.AsSpan(pos + 2, len - 2);
            pos += len;
            return s;
        }

        private void ReadQuant(ReadOnlySpan<byte> s)
        {
            while (s.Length > 0)
            {
                int pq = s[0] >> 4, tq = s[0] & 15;
                if (tq > 3 || pq > 1) throw new ImageDecodeException("bad JPEG quantization table");
                int n = pq == 0 ? 64 : 128;
                if (s.Length < 1 + n) throw new ImageDecodeException("truncated JPEG quantization table");
                var t = new int[64];
                for (int i = 0; i < 64; i++)
                    t[ZigZag[i]] = pq == 0 ? s[1 + i] : s[1 + 2 * i] << 8 | s[2 + 2 * i];
                quant[tq] = t;
                s = s[(1 + n)..];
            }
        }

        private void ReadHuffman(ReadOnlySpan<byte> s)
        {
            while (s.Length > 0)
            {
                if (s.Length < 17) throw new ImageDecodeException("truncated JPEG Huffman table");
                int tc = s[0] >> 4, th = s[0] & 15;
                if (tc > 1 || th > 3) throw new ImageDecodeException("bad JPEG Huffman table");
                var counts = s.Slice(1, 16);
                int total = 0;
                foreach (byte c in counts) total += c;
                if (total > 256 || s.Length < 17 + total) throw new ImageDecodeException("bad JPEG Huffman table");
                var table = new Huffman(counts, s.Slice(17, total));
                (tc == 0 ? dcTables : acTables)[th] = table;
                s = s[(17 + total)..];
            }
        }

        private void ReadFrame(ReadOnlySpan<byte> s, bool isProgressive)
        {
            if (comps is not null) throw new ImageDecodeException("JPEG has more than one frame");
            if (s.Length < 6) throw new ImageDecodeException("bad JPEG frame header");
            if (s[0] != 8) throw new ImageDecodeException($"unsupported JPEG precision ({s[0]} bits)");
            int h = s[1] << 8 | s[2], w = s[3] << 8 | s[4], n = s[5];
            if (h == 0) throw new ImageDecodeException("unsupported JPEG (height defined by DNL)");
            if (w == 0) throw new ImageDecodeException("JPEG has no pixels");
            if ((ulong)w * (ulong)h > ImageLimits.MaxPixels || w > ImageLimits.MaxDimension || h > ImageLimits.MaxDimension)
                throw new ImageDecodeException($"JPEG too large ({w}x{h})");
            if (n is not (1 or 3 or 4)) throw new ImageDecodeException($"unsupported JPEG component count ({n})");
            if (s.Length < 6 + 3 * n) throw new ImageDecodeException("bad JPEG frame header");
            var cs = ReadFrameComponents(s, n);
            width = w;
            height = h;
            progressive = isProgressive;
            mcusX = (w + 8 * hmax - 1) / (8 * hmax);
            mcusY = (h + 8 * vmax - 1) / (8 * vmax);
            AllocateComponents(cs, w, h);
            comps = cs;
        }

        /// <summary>Reads the component specs of a frame header, tracking the largest sampling factors.</summary>
        private Component[] ReadFrameComponents(ReadOnlySpan<byte> s, int n)
        {
            var cs = new Component[n];
            for (int i = 0; i < n; i++)
            {
                var c = cs[i] = new Component { Id = s[6 + 3 * i], H = s[7 + 3 * i] >> 4, V = s[7 + 3 * i] & 15, Tq = s[8 + 3 * i] };
                if (c.H is < 1 or > 4 || c.V is < 1 or > 4 || c.Tq > 3) throw new ImageDecodeException("bad JPEG component");
                hmax = Math.Max(hmax, c.H);
                vmax = Math.Max(vmax, c.V);
            }
            return cs;
        }

        private void AllocateComponents(Component[] cs, int w, int h)
        {
            foreach (var c in cs)
            {
                int cw = (w * c.H + hmax - 1) / hmax, ch = (h * c.V + vmax - 1) / vmax;
                c.BlocksPerLine = (cw + 7) / 8;
                c.BlocksPerColumn = (ch + 7) / 8;
                c.BlocksW = mcusX * c.H;
                c.BlocksH = mcusY * c.V;
                c.Coefs = new short[(long)c.BlocksW * c.BlocksH * 64];
            }
        }

        private void ReadScan(ReadOnlySpan<byte> s)
        {
            if (comps is null) throw new ImageDecodeException("JPEG scan before frame header");
            if (s.Length < 1) throw new ImageDecodeException("bad JPEG scan header");
            int n = s[0];
            if (n is < 1 or > 4 || s.Length < 4 + 2 * n) throw new ImageDecodeException("bad JPEG scan header");
            var sc = ReadScanComponents(s, n, comps);
            ss = s[1 + 2 * n];
            se = s[2 + 2 * n];
            ah = s[3 + 2 * n] >> 4;
            al = s[3 + 2 * n] & 15;

            var mode = SelectScanMode(n);
            CheckScanTables(sc, mode);

            DecodeScan(sc, mode);

            SkipToNextMarker();
        }

        private Component[] ReadScanComponents(ReadOnlySpan<byte> s, int n, Component[] frame)
        {
            var sc = new Component[n];
            for (int i = 0; i < n; i++)
            {
                int id = s[1 + 2 * i], tables = s[2 + 2 * i];
                sc[i] = Array.Find(frame, c => c.Id == id) ?? throw new ImageDecodeException("JPEG scan references an unknown component");
                int td = tables >> 4, ta = tables & 15;
                if (td > 3 || ta > 3) throw new ImageDecodeException("bad JPEG scan header");
                sc[i].Dc = dcTables[td];
                sc[i].Ac = acTables[ta];
            }
            return sc;
        }

        private ScanMode SelectScanMode(int n)
        {
            if (!progressive) return ScanMode.Sequential;
            if (ss > se || se > 63 || (ss == 0 && se != 0) || (ss > 0 && n != 1) || al > 13)
                throw new ImageDecodeException("bad JPEG progressive scan parameters");
            return (ss == 0, ah == 0) switch
            {
                (true, true) => ScanMode.DcFirst,
                (true, false) => ScanMode.DcRefine,
                (false, true) => ScanMode.AcFirst,
                (false, false) => ScanMode.AcRefine,
            };
        }

        private void CheckScanTables(Component[] sc, ScanMode mode)
        {
            foreach (var c in sc)
            {
                if (mode is ScanMode.Sequential or ScanMode.DcFirst && c.Dc is null) throw new ImageDecodeException("JPEG scan uses an undefined Huffman table");
                if (mode is ScanMode.Sequential or ScanMode.AcFirst or ScanMode.AcRefine && c.Ac is null) throw new ImageDecodeException("JPEG scan uses an undefined Huffman table");
                c.Quant ??= quant[c.Tq] ?? throw new ImageDecodeException("JPEG scan uses an undefined quantization table");
            }
        }

        /// <summary>Skips to the next marker (anything but stuffing, fill bytes and stray restart markers).</summary>
        private void SkipToNextMarker()
        {
            while (pos < d.Length)
            {
                if (d[pos] == 0xFF && pos + 1 < d.Length)
                {
                    int nx = d[pos + 1];
                    if (nx is not (0 or 0xFF or (>= 0xD0 and <= 0xD7))) break;
                }
                pos++;
            }
        }

        private void ResetReader()
        {
            buf = 0;
            cnt = 0;
            marker = false;
            overrun = 0;
            eobrun = 0;
        }

        private void DecodeScan(Component[] sc, ScanMode mode)
        {
            ResetReader();
            foreach (var c in sc) c.Pred = 0;
            var (total, cols) = ScanExtent(sc);
            int todo = restartInterval;
            int mx = -1, my = 0;
            for (long mcu = 0; mcu < total; mcu++)
            {
                if (++mx == cols) { mx = 0; my++; }
                if (restartInterval > 0) todo = CountRestartInterval(sc, todo);
                if (overrun > 64)
                {
                    // Data ran out or is corrupt: leave the rest of this interval as it is.
                    if (restartInterval == 0) break;
                    continue;
                }
                DecodeMcu(sc, mx, my, mode);
            }
        }

        /// <summary>MCUs in a scan and how many of them make up a row (non-interleaved scans use the component's own blocks).</summary>
        private (long Total, int Cols) ScanExtent(Component[] sc)
        {
            if (sc.Length != 1) return ((long)mcusX * mcusY, mcusX);
            return ((long)sc[0].BlocksPerLine * sc[0].BlocksPerColumn, sc[0].BlocksPerLine);
        }

        /// <summary>Counts down the restart interval, consuming the restart marker when it is used up; returns the new count.</summary>
        private int CountRestartInterval(Component[] sc, int todo)
        {
            if (todo == 0)
            {
                Restart();
                foreach (var c in sc) c.Pred = 0;
                todo = restartInterval;
            }
            return todo - 1;
        }

        private void DecodeMcu(Component[] sc, int mx, int my, ScanMode mode)
        {
            if (sc.Length == 1)
            {
                var c = sc[0];
                DecodeBlock(c, c.Coefs.AsSpan((my * c.BlocksW + mx) * 64, 64), mode);
                return;
            }
            foreach (var c in sc)
                for (int v = 0; v < c.V; v++)
                    for (int h = 0; h < c.H; h++)
                    {
                        int bx = mx * c.H + h, by = my * c.V + v;
                        DecodeBlock(c, c.Coefs.AsSpan((by * c.BlocksW + bx) * 64, 64), mode);
                    }
        }

        /// <summary>Drops buffered bits and consumes the next RSTn marker, if that is what comes next.</summary>
        private void Restart()
        {
            ResetReader();
            while (pos < d.Length)
            {
                if (d[pos] == 0xFF && pos + 1 < d.Length)
                {
                    int nx = d[pos + 1];
                    if (nx is >= 0xD0 and <= 0xD7) { pos += 2; return; }
                    if (nx is not (0 or 0xFF)) return; // some other marker: leave it for the bit reader to stop at
                }
                pos++;
            }
        }

        private void DecodeBlock(Component c, Span<short> blk, ScanMode mode)
        {
            switch (mode)
            {
                case ScanMode.Sequential:
                    DecodeDc(c);
                    blk[0] = (short)c.Pred;
                    DecodeAc(c.Ac!, blk);
                    break;
                case ScanMode.DcFirst:
                    DecodeDc(c);
                    blk[0] = (short)(c.Pred << al);
                    break;
                case ScanMode.DcRefine:
                    if (GetBits(1) != 0) blk[0] |= (short)(1 << al);
                    break;
                case ScanMode.AcFirst:
                    DecodeAcFirst(c.Ac!, blk);
                    break;
                case ScanMode.AcRefine:
                    RefineAc(c.Ac!, blk);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecodeDc(Component c)
        {
            int t = DecodeHuffman(c.Dc!);
            c.Pred += t == 0 ? 0 : Receive(t);
        }

        /// <summary>First scan of a progressive AC band.</summary>
        private void DecodeAcFirst(Huffman ac, Span<short> blk)
        {
            if (eobrun > 0) { eobrun--; return; }
            for (int k = ss; k <= se;)
            {
                if (cnt < 16) Fill();
                int fast = ac.FastAc[(int)(buf >> (64 - FastBits))];
                if (fast != 0)
                {
                    int used = fast & 255;
                    buf <<= used;
                    cnt -= used;
                    k += (fast >> 8) & 255;
                    blk[ZigZag[k]] = (short)((fast >> 16) * (1 << al));
                    k++;
                    continue;
                }
                int rs = DecodeHuffman(ac), r = rs >> 4, sz = rs & 15;
                if (sz == 0)
                {
                    if (r < 15)
                    {
                        eobrun = (1 << r) - 1;
                        if (r > 0) eobrun += GetBits(r);
                        return;
                    }
                    k += 16;
                    continue;
                }
                k += r;
                blk[ZigZag[k]] = (short)(Receive(sz) * (1 << al));
                k++;
            }
        }

        // The hot loops below keep the bit buffer in locals (b, n) and sync it with the fields around Fill().

        /// <summary>AC coefficients of a sequential block.</summary>
        private void DecodeAc(Huffman ac, Span<short> blk)
        {
            ulong b = buf;
            int n = cnt;
            var fastAc = ac.FastAc;
            for (int k = 1; k < 64;)
            {
                if (n < 32) { buf = b; cnt = n; Fill(); b = buf; n = cnt; }
                int fast = fastAc[(int)(b >> (64 - FastBits))];
                if (fast != 0)
                {
                    int used = fast & 255;
                    b <<= used;
                    n -= used;
                    k += (fast >> 8) & 255;
                    blk[ZigZag[k]] = (short)(fast >> 16);
                    k++;
                    continue;
                }
                int rs;
                int idx = ac.Fast[(int)(b >> (64 - FastBits))];
                if (idx >= 0)
                {
                    int len = ac.Sizes[idx];
                    b <<= len;
                    n -= len;
                    rs = ac.Values[idx];
                }
                else { buf = b; cnt = n; rs = DecodeHuffmanSlow(ac); b = buf; n = cnt; }
                int r = rs >> 4, sz = rs & 15;
                if (sz == 0)
                {
                    if (r != 15) break;
                    k += 16;
                    continue;
                }
                k += r;
                // At least 32 - 16 bits remain for the (at most 15-bit) magnitude.
                int v = (int)(b >> (64 - sz));
                b <<= sz;
                n -= sz;
                blk[ZigZag[k]] = (short)(v < 1 << (sz - 1) ? v - (1 << sz) + 1 : v);
                k++;
            }
            buf = b;
            cnt = n;
        }

        private void RefineAc(Huffman ac, Span<short> blk)
        {
            int k = eobrun == 0 ? RefineAcSymbols(ac, blk) : ss;
            if (eobrun > 0)
            {
                RefineAcToEnd(blk, k);
                eobrun--;
            }
        }

        /// <summary>Applies run/size symbols until the band ends or an EOB run starts; returns the position reached.</summary>
        private int RefineAcSymbols(Huffman ac, Span<short> blk)
        {
            int p1 = 1 << al, m1 = -1 << al;
            int k = ss, last = se;
            ulong b = buf;
            int n = cnt;
            for (; k <= last; k++)
            {
                if (n < 32) { buf = b; cnt = n; Fill(); b = buf; n = cnt; }
                int rs = NextAcSymbol(ac, ref b, ref n);
                int r = rs >> 4, value = 0;
                // At least 32 - 16 bits remain for the sign bit or EOB run length.
                if ((rs & 15) != 0)
                {
                    value = (long)b < 0 ? p1 : m1;
                    b <<= 1;
                    n--;
                }
                else if (r != 15)
                {
                    eobrun = ReadEobRun(r, ref b, ref n);
                    break;
                }
#pragma warning disable S127 // a JPEG zero run moves the coefficient index by a coded amount
                k = RefineZeroRun(blk, k, last, r, p1, ref b, ref n);
#pragma warning restore S127
                if (value != 0) blk[ZigZag[k]] = (short)value;
            }
            buf = b;
            cnt = n;
            return k;
        }

        /// <summary>The next Huffman symbol of an AC band; the caller guarantees at least 16 buffered bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int NextAcSymbol(Huffman ac, ref ulong b, ref int n)
        {
            int idx = ac.Fast[(int)(b >> (64 - FastBits))];
            if (idx >= 0)
            {
                int len = ac.Sizes[idx];
                b <<= len;
                n -= len;
                return ac.Values[idx];
            }
            buf = b;
            cnt = n;
            int rs = DecodeHuffmanSlow(ac);
            b = buf;
            n = cnt;
            return rs;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadEobRun(int r, ref ulong b, ref int n)
        {
            int run = 1 << r;
            if (r > 0)
            {
                run += (int)(b >> (64 - r));
                b <<= r;
                n -= r;
            }
            return run;
        }

        /// <summary>
        /// Advances over <paramref name="r"/> zero-history coefficients, refining the nonzero ones passed on the way;
        /// returns the position reached. <paramref name="p1"/> is the refinement bit (1 &lt;&lt; Al).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int RefineZeroRun(Span<short> blk, int k, int last, int r, int p1, ref ulong b, ref int n)
        {
            do
            {
                int z = ZigZag[k];
                int coef = blk[z];
                if (coef != 0) RefineNonzero(blk, z, coef, p1, ref b, ref n);
                else if (--r < 0) break;
                k++;
            } while (k <= last);
            return k;
        }

        /// <summary>An EOB run covers the rest of the band: only correction bits for the coefficients that are nonzero.</summary>
        private void RefineAcToEnd(Span<short> blk, int k)
        {
            int p1 = 1 << al;
            ulong b = buf;
            int n = cnt;
            for (; k <= se; k++)
            {
                int z = ZigZag[k];
                int coef = blk[z];
                if (coef != 0) RefineNonzero(blk, z, coef, p1, ref b, ref n);
            }
            buf = b;
            cnt = n;
        }

        /// <summary>
        /// Reads the correction bit of a nonzero coefficient and applies it if it hasn't been yet: the magnitude grows by
        /// <paramref name="p1"/> (1 &lt;&lt; Al; -1 &lt;&lt; Al is its negation).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RefineNonzero(Span<short> blk, int z, int coef, int p1, ref ulong b, ref int n)
        {
            if (n < 1) { buf = b; cnt = n; Fill(); b = buf; n = cnt; }
            if ((long)b < 0 && (coef & p1) == 0) blk[z] = (short)(coef + (coef >= 0 ? p1 : -p1));
            b <<= 1;
            n--;
        }

        private void Fill()
        {
            while (cnt <= 56)
            {
                int b = 0;
                if (marker || pos >= d.Length) { marker = true; overrun++; }
                else
                {
                    b = d[pos];
                    if (b != 0xFF) pos++;
                    else if (pos + 1 < d.Length && d[pos + 1] == 0) pos += 2;
                    else { b = 0; marker = true; overrun++; }
                }
                buf |= (ulong)b << (56 - cnt);
                cnt += 8;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetBits(int n) // 1 <= n <= 16
        {
            if (cnt < n) Fill();
            int v = (int)(buf >> (64 - n));
            buf <<= n;
            cnt -= n;
            return v;
        }

        /// <summary>Reads an n-bit magnitude-coded value (T.81 F.2.2.1 EXTEND).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Receive(int n)
        {
            if (n > 16) { Corrupt(); return 0; }
            int v = GetBits(n);
            return v < 1 << (n - 1) ? v - (1 << n) + 1 : v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int DecodeHuffman(Huffman h)
        {
            if (cnt < 16) Fill();
            int k = h.Fast[(int)(buf >> (64 - FastBits))];
            if (k < 0) return DecodeHuffmanSlow(h);
            int s = h.Sizes[k];
            buf <<= s;
            cnt -= s;
            return h.Values[k];
        }

        private int DecodeHuffmanSlow(Huffman h)
        {
            for (int len = FastBits + 1; len <= 16; len++)
            {
                int v = (int)(buf >> (64 - len));
                if (v < h.MaxCode[len])
                {
                    buf <<= len;
                    cnt -= len;
                    return h.Values[v + h.Delta[len]];
                }
            }
            Corrupt();
            return 0;
        }

        private void Corrupt() => overrun = int.MaxValue / 2;

#pragma warning disable S6640 // pointer loops over every 8x8 block, without a bounds check per sample
        private unsafe DecodedImage Output(bool applyOrientation)
#pragma warning restore S6640
        {
            var cs = comps!;
            int n = cs.Length, w = width, h = height;
            var planes = new byte[n][];
            var strides = new int[n];
            int* ws = stackalloc int[64];
            for (int i = 0; i < n; i++) planes[i] = ReconstructPlane(cs[i], ws, out strides[i]);
            var xmaps = ColumnMaps(cs, w);

            var rgba = GC.AllocateUninitializedArray<byte>(w * h * 4); // every pixel is written
            bool isRgb = n == 3 && (adobe ? adobeTransform == 0 : !jfif && cs[0].Id == 'R' && cs[1].Id == 'G' && cs[2].Id == 'B');
            for (int y = 0; y < h; y++) ConvertRow(rgba.AsSpan(y * w * 4, w * 4), y, planes, strides, xmaps, isRgb, w);

            if (applyOrientation && orientation is >= 2 and <= 8) return Orient(w, h, rgba, orientation);
            return new DecodedImage(w, h, rgba);
        }

        /// <summary>Runs the IDCT over every block of a component and frees its coefficients.</summary>
#pragma warning disable S6640 // pointer loops over every 8x8 block, without a bounds check per sample
        private static unsafe byte[] ReconstructPlane(Component c, int* ws, out int stride)
#pragma warning restore S6640
        {
            int s = c.BlocksPerLine * 8;
            stride = s;
            var plane = GC.AllocateUninitializedArray<byte>(s * c.BlocksPerColumn * 8); // every block is written
            var q = c.Quant ?? new int[64];
            fixed (short* coefs = c.Coefs)
            fixed (int* qp = q)
            fixed (byte* pp = plane)
                for (int by = 0; by < c.BlocksPerColumn; by++)
                    for (int bx = 0; bx < c.BlocksPerLine; bx++)
                        Idct(coefs + (by * c.BlocksW + bx) * 64, qp, ws, pp + by * 8 * s + bx * 8, s);
            c.Coefs = []; // release coefficient memory early
            return plane;
        }

        /// <summary>Column maps for upsampling by replication.</summary>
        private int[][] ColumnMaps(Component[] cs, int w)
        {
            var xmaps = new int[cs.Length][];
            for (int i = 0; i < cs.Length; i++)
            {
                var map = xmaps[i] = new int[w];
                for (int x = 0; x < w; x++) map[x] = x * cs[i].H / hmax;
            }
            return xmaps;
        }

        private ReadOnlySpan<byte> PlaneRow(byte[][] planes, int[] strides, int i, int y) =>
            planes[i].AsSpan(y * comps![i].V / vmax * strides[i]);

        private void ConvertRow(Span<byte> o, int y, byte[][] planes, int[] strides, int[][] xmaps, bool isRgb, int w)
        {
            var r0 = PlaneRow(planes, strides, 0, y);
            var m0 = xmaps[0];
            if (planes.Length == 1)
            {
                WriteGrayRow(o, r0, m0, w);
                return;
            }
            var rows = new PlaneRows(r0, PlaneRow(planes, strides, 1, y), PlaneRow(planes, strides, 2, y),
                planes.Length > 3 ? PlaneRow(planes, strides, 3, y) : default);
            if (planes.Length == 3 && isRgb) WriteRgbRow(o, rows, xmaps, w);
            else if (planes.Length == 3) WriteYCbCrRow(o, rows, xmaps, w);
            else WriteCmykRow(o, rows, xmaps, w);
        }

        /// <summary>One output row's source row in each component plane (<see cref="R3"/> only for CMYK).</summary>
        private readonly ref struct PlaneRows(ReadOnlySpan<byte> r0, ReadOnlySpan<byte> r1, ReadOnlySpan<byte> r2, ReadOnlySpan<byte> r3)
        {
            public readonly ReadOnlySpan<byte> R0 = r0, R1 = r1, R2 = r2, R3 = r3;
        }

        private static void WriteGrayRow(Span<byte> o, ReadOnlySpan<byte> r0, int[] m0, int w)
        {
            for (int x = 0; x < w; x++)
            {
                byte g = r0[m0[x]];
                o[4 * x] = g; o[4 * x + 1] = g; o[4 * x + 2] = g; o[4 * x + 3] = 255;
            }
        }

        private static void WriteRgbRow(Span<byte> o, in PlaneRows rows, int[][] xmaps, int w)
        {
            ReadOnlySpan<byte> r0 = rows.R0, r1 = rows.R1, r2 = rows.R2;
            int[] m0 = xmaps[0], m1 = xmaps[1], m2 = xmaps[2];
            for (int x = 0; x < w; x++)
            {
                o[4 * x] = r0[m0[x]]; o[4 * x + 1] = r1[m1[x]]; o[4 * x + 2] = r2[m2[x]]; o[4 * x + 3] = 255;
            }
        }

#pragma warning disable S6640 // per-pixel color conversion through pointers, without a bounds check per sample
        private static unsafe void WriteYCbCrRow(Span<byte> o, in PlaneRows rows, int[][] xmaps, int w)
#pragma warning restore S6640
        {
            fixed (byte* op = o, p0 = rows.R0, p1 = rows.R1, p2 = rows.R2)
            fixed (int* x0 = xmaps[0], x1 = xmaps[1], x2 = xmaps[2], crR = CrR, cbB = CbB, crG = CrG, cbG = CbG)
            fixed (byte* limitTable = Limit)
            {
                byte* limit = limitTable + LimitOffset;
                uint* d = (uint*)op;
                for (int x = 0; x < w; x++)
                {
                    int yy = p0[x0[x]], cb = p1[x1[x]], cr = p2[x2[x]];
                    uint r = limit[yy + crR[cr]], g = limit[yy + ((cbG[cb] + crG[cr]) >> 16)], b = limit[yy + cbB[cb]];
                    d[x] = BitConverter.IsLittleEndian ? r | g << 8 | b << 16 | 0xFF000000 : r << 24 | g << 16 | b << 8 | 0xFF;
                }
            }
        }

        private void WriteCmykRow(Span<byte> o, in PlaneRows rows, int[][] xmaps, int w)
        {
            ReadOnlySpan<byte> r0 = rows.R0, r1 = rows.R1, r2 = rows.R2, r3 = rows.R3;
            int[] m0 = xmaps[0], m1 = xmaps[1], m2 = xmaps[2], m3 = xmaps[3];
            bool ycck = adobe && adobeTransform == 2;
            for (int x = 0; x < w; x++)
            {
                int a = r0[m0[x]], b = r1[m1[x]], cc = r2[m2[x]], k = r3[m3[x]];
                int cR, cG, cB;
                if (ycck)
                {
                    // YCCK encodes 255 - C (etc.) of the stored CMYK, as libjpeg's ycck_cmyk_convert.
                    cR = 255 - Clamp(a + CrR[cc]); cG = 255 - Clamp(a + ((CbG[b] + CrG[cc]) >> 16)); cB = 255 - Clamp(a + CbB[b]);
                }
                else { cR = a; cG = b; cB = cc; }
                // Adobe stores inverted CMYK (255 = no ink); without the marker assume plain CMYK.
                if (!adobe) { cR = 255 - cR; cG = 255 - cG; cB = 255 - cB; k = 255 - k; }
                o[4 * x] = (byte)((cR * k + 127) / 255);
                o[4 * x + 1] = (byte)((cG * k + 127) / 255);
                o[4 * x + 2] = (byte)((cB * k + 127) / 255);
                o[4 * x + 3] = 255;
            }
        }

        private static DecodedImage Orient(int w, int h, byte[] rgba, int orientation)
        {
            bool swap = orientation >= 5;
            int ow = swap ? h : w, oh = swap ? w : h;
            var src = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan());
            var outBytes = GC.AllocateUninitializedArray<byte>(rgba.Length);
            var dst = MemoryMarshal.Cast<byte, uint>(outBytes.AsSpan());
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    (int dx, int dy) = orientation switch
                    {
                        2 => (w - 1 - x, y),
                        3 => (w - 1 - x, h - 1 - y),
                        4 => (x, h - 1 - y),
                        5 => (y, x),
                        6 => (h - 1 - y, x),
                        7 => (h - 1 - y, w - 1 - x),
                        _ => (y, w - 1 - x),
                    };
                    dst[dy * ow + dx] = src[y * w + x];
                }
            return new DecodedImage(ow, oh, outBytes);
        }

        /// <summary>Dequantizes and inverse-transforms one block into 8x8 samples at <paramref name="output"/>.</summary>
#pragma warning disable S6640 // pointer loops over every 8x8 block, without a bounds check per sample
        private static unsafe void Idct(short* c, int* q, int* ws, byte* output, int stride)
#pragma warning restore S6640
        {
            // Pass 1: columns, into the workspace (scaled up by 2^Pass1Bits).
            if (Vector256.IsHardwareAccelerated) ColumnsVector(c, q, ws);
            else ColumnsScalar(c, q, ws);
            // Pass 2: rows, descaled by 2^(Pass1Bits + 3) and level-shifted.
            for (int r = 0; r < 8; r++)
            {
                int* w = ws + r * 8;
                byte* o = output + r * stride;
                if ((w[1] | w[2] | w[3] | w[4] | w[5] | w[6] | w[7]) == 0)
                {
                    byte v = Clamp(((w[0] + (1 << (Pass1Bits + 2))) >> (Pass1Bits + 3)) + 128);
                    *(ulong*)o = v * 0x0101010101010101UL;
                    continue;
                }
                Butterfly(w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7], ConstBits + Pass1Bits + 3, w, 1);
                for (int i = 0; i < 8; i++) o[i] = Clamp(w[i] + 128);
            }
        }

        /// <summary>Pass 1 without vector support: one column at a time.</summary>
#pragma warning disable S6640 // pointer loops over every 8x8 block, without a bounds check per sample
        private static unsafe void ColumnsScalar(short* c, int* q, int* ws)
#pragma warning restore S6640
        {
            for (int col = 0; col < 8; col++)
            {
                short* p = c + col;
                int* qq = q + col;
                if (p[8] == 0 && p[16] == 0 && p[24] == 0 && p[32] == 0 && p[40] == 0 && p[48] == 0 && p[56] == 0)
                {
                    int dc = p[0] * qq[0] << Pass1Bits;
                    for (int r = 0; r < 8; r++) ws[r * 8 + col] = dc;
                    continue;
                }
                Butterfly(p[0] * qq[0], p[8] * qq[8], p[16] * qq[16], p[24] * qq[24],
                    p[32] * qq[32], p[40] * qq[40], p[48] * qq[48], p[56] * qq[56], ConstBits - Pass1Bits, ws + col, 8);
            }
        }

        /// <summary>Pass 1 for all eight columns at once (one lane per column); same arithmetic as <see cref="Butterfly"/>.</summary>
#pragma warning disable S6640 // loads vectors straight from the coefficient block
        private static unsafe void ColumnsVector(short* c, int* q, int* ws)
#pragma warning restore S6640
        {
            static Vector256<int> Row(short* c, int* q, int r) =>
                Vector256.WidenLower(Vector128.Load(c + 8 * r).ToVector256Unsafe()) * Vector256.Load(q + 8 * r);

            Vector256<int> i0 = Row(c, q, 0), i1 = Row(c, q, 1), i2 = Row(c, q, 2), i3 = Row(c, q, 3);
            Vector256<int> i4 = Row(c, q, 4), i5 = Row(c, q, 5), i6 = Row(c, q, 6), i7 = Row(c, q, 7);
            const int shift = ConstBits - Pass1Bits;
            var round = Vector256.Create(1 << (shift - 1));
            // Even part.
            var z1 = (i2 + i6) * F0541;
            var tmp2 = z1 - i6 * F1847;
            var tmp3 = z1 + i2 * F0765;
            var tmp0 = (i0 + i4) << ConstBits;
            var tmp1 = (i0 - i4) << ConstBits;
            Vector256<int> tmp10 = tmp0 + tmp3 + round, tmp13 = tmp0 - tmp3 + round, tmp11 = tmp1 + tmp2 + round, tmp12 = tmp1 - tmp2 + round;
            // Odd part.
            z1 = i7 + i1;
            Vector256<int> z2 = i5 + i3, z3 = i7 + i3, z4 = i5 + i1;
            var z5 = (z3 + z4) * F1175;
            tmp0 = i7 * F0298; tmp1 = i5 * F2053; tmp2 = i3 * F3072; tmp3 = i1 * F1501;
            z1 *= -F0899; z2 *= -F2562; z3 *= -F1961; z4 *= -F0390;
            z3 += z5; z4 += z5;
            tmp0 += z1 + z3; tmp1 += z2 + z4; tmp2 += z2 + z3; tmp3 += z1 + z4;
            ((tmp10 + tmp3) >> shift).Store(ws);
            ((tmp10 - tmp3) >> shift).Store(ws + 56);
            ((tmp11 + tmp2) >> shift).Store(ws + 8);
            ((tmp11 - tmp2) >> shift).Store(ws + 48);
            ((tmp12 + tmp1) >> shift).Store(ws + 16);
            ((tmp12 - tmp1) >> shift).Store(ws + 40);
            ((tmp13 + tmp0) >> shift).Store(ws + 24);
            ((tmp13 - tmp0) >> shift).Store(ws + 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#pragma warning disable S6640, S107 // writes the IDCT output through a pointer with a stride; takes the eight inputs of libjpeg's 1-D IDCT, inlined and kept in registers
        private static unsafe void Butterfly(int i0, int i1, int i2, int i3, int i4, int i5, int i6, int i7, int shift, int* dst, int step)
#pragma warning restore S6640, S107
        {
            int round = 1 << (shift - 1);
            // Even part.
            int z1 = (i2 + i6) * F0541;
            int tmp2 = z1 - i6 * F1847;
            int tmp3 = z1 + i2 * F0765;
            int tmp0 = (i0 + i4) << ConstBits;
            int tmp1 = (i0 - i4) << ConstBits;
            int tmp10 = tmp0 + tmp3, tmp13 = tmp0 - tmp3, tmp11 = tmp1 + tmp2, tmp12 = tmp1 - tmp2;
            // Odd part.
            tmp0 = i7; tmp1 = i5; tmp2 = i3; tmp3 = i1;
            z1 = tmp0 + tmp3;
            int z2 = tmp1 + tmp2, z3 = tmp0 + tmp2, z4 = tmp1 + tmp3;
            int z5 = (z3 + z4) * F1175;
            tmp0 *= F0298; tmp1 *= F2053; tmp2 *= F3072; tmp3 *= F1501;
            z1 *= -F0899; z2 *= -F2562; z3 *= -F1961; z4 *= -F0390;
            z3 += z5; z4 += z5;
            tmp0 += z1 + z3; tmp1 += z2 + z4; tmp2 += z2 + z3; tmp3 += z1 + z4;
            dst[0] = (tmp10 + tmp3 + round) >> shift;
            dst[7 * step] = (tmp10 - tmp3 + round) >> shift;
            dst[step] = (tmp11 + tmp2 + round) >> shift;
            dst[6 * step] = (tmp11 - tmp2 + round) >> shift;
            dst[2 * step] = (tmp12 + tmp1 + round) >> shift;
            dst[5 * step] = (tmp12 - tmp1 + round) >> shift;
            dst[3 * step] = (tmp13 + tmp0 + round) >> shift;
            dst[4 * step] = (tmp13 - tmp0 + round) >> shift;
        }
    }

    /// <summary>Clamps to [0, 255] when indexed with v + <see cref="LimitOffset"/>, for v in [-LimitOffset, 1024 - LimitOffset).</summary>
    private static readonly byte[] Limit = MakeLimit();
    private const int LimitOffset = 384;

    private static byte[] MakeLimit()
    {
        var t = new byte[1024];
        for (int i = 0; i < t.Length; i++) t[i] = Clamp(i - LimitOffset);
        return t;
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    // YCbCr -> RGB in 16.16 fixed point, as libjpeg's jdcolor.c.
    private static readonly int[] CrR = ColorTable(1.40200, true), CbB = ColorTable(1.77200, true);
    private static readonly int[] CrG = ColorTable(-0.71414, false), CbG = ColorTable(-0.34414, false, 32768);

    private static int[] ColorTable(double factor, bool descale, int bias = 0)
    {
        int fix = (int)Math.Round(Math.Abs(factor) * 65536) * Math.Sign(factor);
        var t = new int[256];
        for (int i = 0; i < 256; i++)
        {
            int x = i - 128;
            t[i] = descale ? (fix * x + 32768) >> 16 : fix * x + bias;
        }
        return t;
    }

    // libjpeg's jidctint.c (slow-but-accurate integer IDCT) constants, 13-bit fixed point.
    private const int ConstBits = 13, Pass1Bits = 2;
    private const int F0298 = 2446, F0390 = 3196, F0541 = 4433, F0765 = 6270, F0899 = 7373, F1175 = 9633,
        F1501 = 12299, F1847 = 15137, F1961 = 16069, F2053 = 16819, F2562 = 20995, F3072 = 25172;
}
