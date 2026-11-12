namespace ErikTerm.Core.Images;

/// <summary>
/// Sixel decoder (DEC STD 070 / VT340): color registers (<c>#Pc;Pu;Px;Py;Pz</c>, HLS and RGB), raster attributes
/// (<c>"Pan;Pad;Ph;Pv</c>), repeat (<c>!Pn</c>), carriage return (<c>$</c>) and new band (<c>-</c>).
/// Pixels that are never painted stay transparent. Aspect ratio parameters are ignored (square pixels).
/// </summary>
public static class Sixel
{
    private const int MaxRegisters = 1024;

    /// <summary>Decodes sixel data; anything wrong with it (or too large) is an <see cref="ImageDecodeException"/>.</summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> data)
    {
        try { return DecodeCore(data); }
        catch (ImageDecodeException) { throw; }
        catch (Exception e) when (e is not ThreadInterruptedException) { throw new ImageDecodeException("bad sixel data"); }
    }

    private static DecodedImage DecodeCore(ReadOnlySpan<byte> data)
    {
        var palette = new uint[MaxRegisters];
        DefaultPalette.CopyTo(palette);
        for (int r = 16; r < MaxRegisters; r++) palette[r] = DefaultPalette[r % 16];

        var canvas = new Canvas(palette);
        Span<int> args = stackalloc int[5];
        int i = 0;
        while (i < data.Length)
        {
            byte b = data[i++];
            switch (b)
            {
                case >= 0x3F and <= 0x7E: canvas.Paint(b - 0x3F, 1); break;
                case (byte)'!': ReadRepeat(data, ref i, canvas); break;
                case (byte)'#': ReadColor(data, ref i, args, canvas); break;
                case (byte)'"': ReadRaster(data, ref i, args, canvas); break;
                case (byte)'$': canvas.CarriageReturn(); break;
                case (byte)'-': canvas.NextBand(); break;
                default: break; // whitespace, stray bytes
            }
        }
        return canvas.ToImage();
    }

    private static void ReadRepeat(ReadOnlySpan<byte> data, ref int i, Canvas canvas)
    {
        int n = ReadNumber(data, ref i);
        if (i < data.Length && data[i] is >= 0x3F and <= 0x7E)
        {
            canvas.Paint(data[i] - 0x3F, Math.Clamp(n, 1, ImageLimits.MaxDimension));
            i++;
        }
    }

    private static void ReadColor(ReadOnlySpan<byte> data, ref int i, Span<int> args, Canvas canvas)
    {
        int count = ReadArgs(data, ref i, args);
        int reg = Math.Clamp(args[0], 0, MaxRegisters - 1);
        if (count >= 5)
        {
            if (args[1] == 2)
                canvas.Palette[reg] = Rgb(args[2] * 255 / 100, args[3] * 255 / 100, args[4] * 255 / 100);
            else if (args[1] == 1)
                canvas.Palette[reg] = Hls(args[2], args[3], args[4]);
        }
        canvas.Color = canvas.Palette[reg];
    }

    private static void ReadRaster(ReadOnlySpan<byte> data, ref int i, Span<int> args, Canvas canvas)
    {
        int count = ReadArgs(data, ref i, args);
        if (count >= 4 && args[2] > 0 && args[3] > 0)
            canvas.Declare(Math.Min(args[2], ImageLimits.MaxDimension), Math.Min(args[3], ImageLimits.MaxDimension));
    }

    /// <summary>The pixels painted so far, with the sixel cursor and the extent of the image.</summary>
    private sealed class Canvas(uint[] palette)
    {
        public readonly uint[] Palette = palette;
        public uint Color = palette[0];
        private int _capW, _capH, _stride;
        private uint[] _pixels = [];
        private int _width, _height; // extent actually painted (or declared by raster attributes)
        private int _x, _band;

        public void CarriageReturn() => _x = 0;

        public void NextBand()
        {
            _x = 0;
            _band = Math.Min(_band + 1, ImageLimits.MaxDimension / 6 + 1);
        }

        /// <summary>Raster attributes: reserves the declared size, if it is within the pixel limit.</summary>
        public void Declare(int w, int h)
        {
            // The image keeps the widest and the tallest declaration, so check them together: two that each fit can
            // still add up to far more than the limit.
            int nw = Math.Max(_width, w), nh = Math.Max(_height, h);
            if ((long)nw * nh > ImageLimits.MaxPixels || !Ensure(nw, nh)) return;
            _width = nw;
            _height = nh;
        }

        public void Paint(int bits, int repeat)
        {
            int y0 = _band * 6;
            // Positions past the largest image only move the cursor (saturating: x never overflows).
            if (y0 >= ImageLimits.MaxDimension || _x >= ImageLimits.MaxDimension) { _x = Advance(_x, repeat); return; }
            int x1 = Math.Min(_x + repeat, ImageLimits.MaxDimension);
            if (bits != 0)
            {
                if (!Ensure(x1, y0 + 6)) { _x = Advance(_x, repeat); return; }
                PaintBand(bits, y0, x1);
            }
            _x = Advance(_x, repeat);
        }

        private void PaintBand(int bits, int y0, int x1)
        {
            for (int k = 0; k < 6; k++)
            {
                if ((bits & (1 << k)) == 0) continue;
                int row = (y0 + k) * _stride;
                _pixels.AsSpan(row + _x, x1 - _x).Fill(Color | 0xFF000000);
                _height = Math.Max(_height, y0 + k + 1);
            }
            _width = Math.Max(_width, x1);
        }

        private static int Advance(int x, int repeat) => (int)Math.Min((long)x + repeat, ImageLimits.MaxDimension);

        private bool Ensure(int w, int h)
        {
            if (w <= _capW && h <= _capH) return true;
            int nw = Math.Max(_capW, w), nh = Math.Max(_capH, h);
            if (nw > _capW) nw = Math.Min(ImageLimits.MaxDimension, Math.Max(nw, _capW * 2));
            if (nh > _capH) nh = Math.Min(ImageLimits.MaxDimension, Math.Max(nh, _capH * 2));
            if ((long)nw * nh > ImageLimits.MaxPixels)
            {
                // Doubling doesn't fit: no spare width, and all the height the limit leaves, so a tall image isn't copied again for every band.
                nw = Math.Max(_width, w);
                int minHeight = Math.Max(_height, h);
                if ((long)nw * minHeight > ImageLimits.MaxPixels) return false;
                nh = (int)Math.Max(minHeight, Math.Min(nh, ImageLimits.MaxPixels / nw));
            }
            var next = new uint[nw * nh];
            int keepW = Math.Min(_capW, nw);
            for (int y = 0; y < Math.Min(_capH, nh); y++) _pixels.AsSpan(y * _stride, keepW).CopyTo(next.AsSpan(y * nw));
            _pixels = next;
            _capW = _stride = nw;
            _capH = nh;
            return true;
        }

        public DecodedImage ToImage()
        {
            if (_width == 0 || _height == 0) throw new ImageDecodeException("empty sixel image");
            if ((long)_width * _height > ImageLimits.MaxPixels) throw new ImageDecodeException("sixel image too large");
            var rgba = new byte[(long)_width * _height * 4];
            for (int y = 0; y < _height; y++)
                for (int px = 0; px < _width; px++)
                {
                    uint c = y < _capH && px < _capW ? _pixels[y * _stride + px] : 0;
                    long o = ((long)y * _width + px) * 4;
                    rgba[o] = (byte)(c >> 16);
                    rgba[o + 1] = (byte)(c >> 8);
                    rgba[o + 2] = (byte)c;
                    rgba[o + 3] = (byte)(c >> 24);
                }
            return new DecodedImage(_width, _height, rgba);
        }
    }

    private static int ReadNumber(ReadOnlySpan<byte> d, ref int i)
    {
        int n = 0;
        while (i < d.Length && d[i] is >= (byte)'0' and <= (byte)'9')
        {
            n = Math.Min(n * 10 + (d[i] - '0'), 1_000_000);
            i++;
        }
        return n;
    }

    private static int ReadArgs(ReadOnlySpan<byte> d, ref int i, Span<int> args)
    {
        args.Clear();
        int count = 0;
        while (true)
        {
            int n = ReadNumber(d, ref i);
            if (count < args.Length) args[count] = n;
            count++;
            if (i < d.Length && d[i] == ';') { i++; continue; }
            return count;
        }
    }

    private static uint Rgb(int r, int g, int b) => (uint)(Math.Clamp(r, 0, 255) << 16 | Math.Clamp(g, 0, 255) << 8 | Math.Clamp(b, 0, 255));

    /// <summary>DEC HLS: hue 0–360 with 0 = blue (red at 120), lightness and saturation 0–100.</summary>
    private static uint Hls(int h, int l, int s)
    {
        double hue = ((h + 240) % 360) / 360.0, light = Math.Clamp(l, 0, 100) / 100.0, sat = Math.Clamp(s, 0, 100) / 100.0;
        if (sat == 0) { int v = (int)Math.Round(light * 255); return Rgb(v, v, v); }
        double q = light < 0.5 ? light * (1 + sat) : light + sat - light * sat, p = 2 * light - q;
        return Rgb((int)Math.Round(Hue(p, q, hue + 1 / 3.0) * 255), (int)Math.Round(Hue(p, q, hue) * 255), (int)Math.Round(Hue(p, q, hue - 1 / 3.0) * 255));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
    }

    /// <summary>VT340 default color registers.</summary>
    private static ReadOnlySpan<uint> DefaultPalette =>
    [
        0x000000, 0x3333CC, 0xCC2121, 0x33CC33, 0xCC33CC, 0x33CCCC, 0xCCCC33, 0x878787,
        0x424242, 0x545499, 0x994242, 0x549954, 0x995499, 0x549999, 0x999954, 0xCCCCCC,
    ];
}
