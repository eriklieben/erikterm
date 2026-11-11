using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Tidewatch;

/// <summary>The water level over the next hours as a PNG: a filled curve, a line every 6 hours, a dot per high and low.</summary>
public static class Chart
{
    private const int Width = 880, Height = 260;

    private static readonly byte[] Background = [30, 30, 46], Grid = [49, 50, 68], Midnight = [69, 71, 90],
        Line = [137, 220, 235], Fill = [116, 199, 236], HighDot = [249, 226, 175], LowDot = [203, 166, 247];

    public static byte[] Render(Station station, DateTime start, int hours)
    {
        var levels = new double[Width];
        for (int x = 0; x < Width; x++)
            levels[x] = Forecast.HeightAt(station, start.AddHours(hours * x / (Width - 1.0)));
        double lo = levels.Min() - 0.3, hi = levels.Max() + 0.3;
        int Y(double level) => (int)((hi - level) / (hi - lo) * (Height - 1));

        var px = new byte[Width * Height * 3];
        void Put(int x, int y, ReadOnlySpan<byte> c)
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height) c.CopyTo(px.AsSpan((y * Width + x) * 3));
        }

        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++) Put(x, y, Background);
        for (int h = 0; h <= hours; h += 6)
        {
            int x = (int)(h / (double)hours * (Width - 1));
            for (int y = 0; y < Height; y++) Put(x, y, h % 24 == 0 ? Midnight : Grid);
        }
        Span<byte> shade = stackalloc byte[3];
        for (int x = 0; x < Width; x++)
        {
            int top = Y(levels[x]);
            for (int y = top; y < Height; y++)
            {
                double a = 0.55 * Math.Pow(1 - (y - top) / (double)Math.Max(1, Height - top), 1.5);
                for (int i = 0; i < 3; i++) shade[i] = (byte)(Fill[i] * a + Background[i] * (1 - a));
                Put(x, y, shade);
            }
        }
        for (int x = 0; x < Width; x++) // the curve, 3 px thick, joined to the previous column
        {
            int y0 = Y(levels[Math.Max(0, x - 1)]), y1 = Y(levels[x]);
            for (int y = Math.Min(y0, y1) - 1; y <= Math.Max(y0, y1) + 1; y++) Put(x, y, Line);
        }
        foreach (var (when, height, kind) in Forecast.Extremes(station, start, hours / 24))
        {
            int cx = (int)((when - start).TotalHours / hours * (Width - 1)), cy = Y(height);
            for (int dx = -5; dx <= 5; dx++)
                for (int dy = -5; dy <= 5; dy++)
                    if (dx * dx + dy * dy <= 25) Put(cx + dx, cy + dy, kind == TideKind.High ? HighDot : LowDot);
        }
        return Png(px);
    }

    private static byte[] Png(byte[] rgb)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
            for (int y = 0; y < Height; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(rgb, y * Width * 3, Width * 3);
            }
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), Height);
        header[8] = 8; // bit depth; colour type 2 (RGB) follows
        header[9] = 2;

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string tag, byte[] data)
    {
        var buf = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        Encoding.ASCII.GetBytes(tag, buf.AsSpan(4));
        data.CopyTo(buf, 8);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8 + data.Length), Crc32(buf.AsSpan(4, 4 + data.Length)));
        s.Write(buf);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}

/// <summary>Shows a PNG inline with the kitty graphics protocol, in 4096-character base64 chunks.</summary>
public static class Kitty
{
    public static void WritePng(TextWriter output, byte[] png)
    {
        string data = Convert.ToBase64String(png);
        for (int i = 0; i < data.Length; i += 4096)
        {
            string head = i == 0 ? "a=T,f=100,q=2," : "";
            int more = i + 4096 < data.Length ? 1 : 0;
            output.Write($"\e_G{head}m={more};{data.AsSpan(i, Math.Min(4096, data.Length - i))}\e\\");
        }
        output.WriteLine();
    }
}
