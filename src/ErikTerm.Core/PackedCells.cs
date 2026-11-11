using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ErikTerm.Core;

/// <summary>
/// The packed form of a scrollback row at rest (<see cref="Row.Pack"/>): its text at one byte a cell (four when it
/// isn't all ASCII) and one entry per stretch of equal colors and attributes, instead of 16 bytes a cell.
/// </summary>
internal static class PackedCells
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PackedRun
    {
        public ushort Start;
        public CellAttributes Flags;
        public uint Fg, Bg;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PackedHeader
    {
        public ushort CellCount, RunCount;
        public int RuneBytes;
    }

    private static int HeaderBytes => Unsafe.SizeOf<PackedHeader>();
    private static int RunBytes => Unsafe.SizeOf<PackedRun>();
    private static int CellBytes => Unsafe.SizeOf<Cell>();

    /// <summary>The packed form of <paramref name="cells"/> (1 to 65535 of them), or null when it wouldn't be smaller.</summary>
    public static byte[]? Pack(ReadOnlySpan<Cell> cells)
    {
        int n = cells.Length;
        var (runs, runeBytes) = Measure(cells);
        int bytes = HeaderBytes + runs * RunBytes + n * runeBytes;
        if (bytes >= n * CellBytes) return null;

        var data = new byte[bytes];
        MemoryMarshal.Write(data, new PackedHeader { CellCount = (ushort)n, RunCount = (ushort)runs, RuneBytes = runeBytes });
        WriteCells(cells, data, runs, runeBytes);
        return data;
    }

    private static (int Runs, int RuneBytes) Measure(ReadOnlySpan<Cell> cells)
    {
        int runs = 1;
        bool wide = false;
        for (int i = 0; i < cells.Length; i++)
        {
            wide |= cells[i].Rune >= 0x80;
            if (i > 0 && !SameStyle(cells[i], cells[i - 1])) runs++;
        }
        return (runs, wide ? sizeof(int) : 1);
    }

    private static bool SameStyle(in Cell a, in Cell b) => a.Fg == b.Fg && a.Bg == b.Bg && a.Flags == b.Flags;

    private static void WriteCells(ReadOnlySpan<Cell> cells, Span<byte> data, int runCount, int runeBytes)
    {
        int textAt = HeaderBytes + runCount * RunBytes;
        var runs = MemoryMarshal.Cast<byte, PackedRun>(data[HeaderBytes..textAt]);
        var bytes = data[textAt..];
        var ints = runeBytes == sizeof(int) ? MemoryMarshal.Cast<byte, int>(bytes) : default;
        int run = -1;
        for (int i = 0; i < cells.Length; i++)
        {
            ref readonly var c = ref cells[i];
            if (i == 0 || !SameStyle(c, cells[i - 1])) runs[++run] = new PackedRun { Start = (ushort)i, Flags = c.Flags, Fg = c.Fg, Bg = c.Bg };
            if (runeBytes == 1) bytes[i] = (byte)c.Rune;
            else ints[i] = c.Rune;
        }
    }

    /// <summary>The cells of a packed row, in an array of <paramref name="length"/> (the rest blank).</summary>
    public static Cell[] Unpack(ReadOnlySpan<byte> data, int length)
    {
        var header = MemoryMarshal.Read<PackedHeader>(data);
        int textAt = HeaderBytes + header.RunCount * RunBytes;
        var runs = MemoryMarshal.Cast<byte, PackedRun>(data[HeaderBytes..textAt]);
        var bytes = data[textAt..];
        var ints = header.RuneBytes == sizeof(int) ? MemoryMarshal.Cast<byte, int>(bytes) : default;
        var cells = new Cell[length];
        for (int r = 0; r < runs.Length; r++)
        {
            int end = r + 1 < runs.Length ? runs[r + 1].Start : header.CellCount;
            for (int i = runs[r].Start; i < end; i++)
                cells[i] = new Cell { Rune = header.RuneBytes == 1 ? bytes[i] : ints[i], Fg = runs[r].Fg, Bg = runs[r].Bg, Flags = runs[r].Flags };
        }
        return cells;
    }
}
