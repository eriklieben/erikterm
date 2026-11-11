using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Ui.Compare;

/// <summary>Character attributes of a cell.</summary>
[Flags]
public enum CellAttrs : byte { None = 0, Bold = 1, Dim = 2, Underline = 4 }

/// <summary>One terminal cell. <see cref="Text"/> is one grapheme's text; an empty string marks the right half of a wide character.</summary>
public struct Cell
{
    public string Text { get; set; }
    public uint Fg { get; set; }
    public uint Bg { get; set; }
    public CellAttrs Attr { get; set; }
}

/// <summary>A run of <see cref="Width"/> cells on row <see cref="Y"/> of a frame, from column <see cref="X"/>.</summary>
public readonly record struct FrameRun(int X, int Y, int Width)
{
    /// <summary>The same run <paramref name="rows"/> rows further down.</summary>
    public FrameRun Down(int rows) => this with { Y = Y + rows };
}

/// <summary>How a line of text is drawn: its colours, the background of its highlighted spans, and its attributes.</summary>
public readonly record struct FrameLineStyle(uint Fg, uint Bg, uint SpanBg, CellAttrs Attr = CellAttrs.None);

/// <summary>
/// A button in a screen's toolbar: its label, the hit action (and argument) a click reports, and how it looks: a
/// primary or active button is filled with <see cref="Color"/> (the screen's accent when null).
/// </summary>
public readonly record struct FrameButton(string Label, int Action, int Arg = 0, bool Primary = false, bool Active = false, uint? Color = null);

/// <summary>A clickable rectangle; <see cref="Action"/> and <see cref="Arg"/> mean what the screen drew them to mean.</summary>
public readonly record struct HitRegion(int X, int Y, int Width, int Height, int Action, int Arg = 0)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>A grid of cells plus what's clickable on it: a screen draws here, <see cref="FrameEncoder"/> turns it into terminal output.</summary>
public sealed class Frame
{
    private static readonly string[] Ascii = BuildAscii();

    private static string[] BuildAscii()
    {
        var table = new string[128];
        for (int c = 0; c < 128; c++) table[c] = ((char)c).ToString();
        return table;
    }

    public Frame(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Cells = new Cell[Width * Height];
        Clear(0, 0);
    }

    public int Width { get; }
    public int Height { get; }
    public Cell[] Cells { get; }
    public List<HitRegion> Hits { get; } = [];

    public void Clear(uint fg, uint bg)
    {
        Hits.Clear();
        for (int i = 0; i < Cells.Length; i++) Cells[i] = new Cell { Text = " ", Fg = fg, Bg = bg };
    }

    public void Fill(int x, int y, int width, int height, uint fg, uint bg)
    {
        for (int row = Math.Max(0, y); row < Math.Min(Height, y + height); row++)
            for (int col = Math.Max(0, x); col < Math.Min(Width, x + width); col++)
                Cells[row * Width + col] = new Cell { Text = " ", Fg = fg, Bg = bg };
    }

    public void AddHit(int x, int y, int width, int height, int action, int arg = 0)
    {
        if (width > 0 && height > 0) Hits.Add(new HitRegion(x, y, width, height, action, arg));
    }

    /// <summary>Which hit region is at (x, y): the one drawn last, or null.</summary>
    public HitRegion? HitAt(int x, int y)
    {
        for (int i = Hits.Count - 1; i >= 0; i--) if (Hits[i].Contains(x, y)) return Hits[i];
        return null;
    }

    /// <summary>The characters of a row as a string, wide characters once (for tests).</summary>
    public string RowText(int y)
    {
        var sb = new StringBuilder();
        for (int x = 0; x < Width; x++) sb.Append(Cells[y * Width + x].Text);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Everything on screen (for tests).</summary>
    public string AllText()
    {
        var sb = new StringBuilder();
        for (int y = 0; y < Height; y++) sb.Append(RowText(y)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Draws text clipped to <paramref name="maxWidth"/> cells; returns the x after it. File content and file names go
    /// through here, so anything that could be terminal control (escape, C1, bidi overrides) is replaced by a dot.
    /// </summary>
    public int Text(int x, int y, int maxWidth, ReadOnlySpan<char> text, uint fg, uint bg, CellAttrs attr = CellAttrs.None)
    {
        if (y < 0 || y >= Height) return x;
        int end = Math.Min(Width, x + maxWidth);
        foreach (var rune in text.EnumerateRunes())
        {
            if (x >= end) break;
            int cp = Displayable(rune.Value);
            int w = CharWidth.Of(cp);
            if (w == 0) { AppendToCellBefore(x, y, cp); continue; }
            if (x + w > end) { x = PadWithBlanks(x, end, y, fg, bg, attr); break; }
            Put(x, y, cp < 128 ? Ascii[cp] : char.ConvertFromUtf32(cp), fg, bg, attr);
            if (w == 2) Put(x + 1, y, "", fg, bg, attr);
            x += w;
        }
        return x;
    }

    private static int Displayable(int cp)
    {
        if (cp == '\t') cp = ' ';
        return IsControl(cp) ? '·' : cp;
    }

    private void AppendToCellBefore(int x, int y, int cp)
    {
        if (x <= 0) return;
        ref var prev = ref Cells[y * Width + x - 1];
        if (prev.Text.Length != 0) { prev.Text += char.ConvertFromUtf32(cp); }
    }

    private int PadWithBlanks(int x, int end, int y, uint fg, uint bg, CellAttrs attr)
    {
        while (x < end) Put(x++, y, " ", fg, bg, attr);
        return x;
    }

    public void Put(int x, int y, string text, uint fg, uint bg, CellAttrs attr = CellAttrs.None)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        Cells[y * Width + x] = new Cell { Text = text, Fg = fg, Bg = bg, Attr = attr };
    }

    public static bool IsControl(int cp) => cp < 0x20 || cp == 0x7F || (cp >= 0x80 && cp < 0xA0)
        || cp is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069 || cp == 0xFEFF;

    /// <summary>A display column for each character position with tab stops every four cells.</summary>
    public static int TabAdvance(int col) => 4 - (col % 4);
}

/// <summary>
/// Turns frames into terminal bytes: synchronized output, only cells that changed since the last frame (the first
/// frame after a <see cref="Reset"/> draws everything). The cursor stays hidden.
/// </summary>
public sealed class FrameEncoder
{
    private Frame? _previous;

    public void Reset() => _previous = null;

    public byte[] Encode(Frame frame)
    {
        var prev = _previous is { } p && p.Width == frame.Width && p.Height == frame.Height ? p : null;
        var sb = new StringBuilder(frame.Width * frame.Height / 4);
        sb.Append("\e[?2026h");
        if (prev is null) sb.Append("\e[2J");

        var pen = new Pen();
        for (int y = 0; y < frame.Height; y++) EncodeRow(frame, prev, y, sb, pen);

        sb.Append("\e[0m\e[?2026l");
        _previous = frame;
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void EncodeRow(Frame frame, Frame? prev, int y, StringBuilder sb, Pen pen)
    {
        for (int x = 0; x < frame.Width; x++)
        {
            ref var cell = ref frame.Cells[y * frame.Width + x];
            if (cell.Text.Length == 0) continue; // the right half of a wide character was drawn with its left half
            if (prev is not null && Unchanged(frame, prev, x, y)) continue;
            pen.MoveTo(sb, x, y);
            pen.Style(sb, in cell);
            sb.Append(cell.Text);
            pen.Advance(x + (HasRightHalf(frame, x, y) ? 2 : 1), y);
        }
    }

    private static bool HasRightHalf(Frame frame, int x, int y) =>
        x + 1 < frame.Width && frame.Cells[y * frame.Width + x + 1].Text.Length == 0;

    private static bool Unchanged(Frame frame, Frame prev, int x, int y)
    {
        ref var cell = ref frame.Cells[y * frame.Width + x];
        ref var old = ref prev.Cells[y * frame.Width + x];
        if (HasRightHalf(frame, x, y) && prev.Cells[y * frame.Width + x + 1].Text.Length != 0) return false;
        return old.Text == cell.Text && old.Fg == cell.Fg && old.Bg == cell.Bg && old.Attr == cell.Attr;
    }

    /// <summary>Where the terminal's cursor is and the colors it writes with, so that only changes are sent.</summary>
    private sealed class Pen
    {
        private uint _fg, _bg;
        private CellAttrs _attr = CellAttrs.None;
        private bool _styled;
        private int _x = -1, _y = -1;

        public void MoveTo(StringBuilder sb, int x, int y)
        {
            if (_x != x || _y != y) sb.Append("\e[").Append(y + 1).Append(';').Append(x + 1).Append('H');
        }

        public void Style(StringBuilder sb, in Cell cell)
        {
            if (_styled && cell.Fg == _fg && cell.Bg == _bg && cell.Attr == _attr) return;
            AppendSgr(sb, cell.Fg, cell.Bg, cell.Attr);
            _fg = cell.Fg;
            _bg = cell.Bg;
            _attr = cell.Attr;
            _styled = true;
        }

        public void Advance(int x, int y) => (_x, _y) = (x, y);

        private static void AppendSgr(StringBuilder sb, uint fg, uint bg, CellAttrs attr)
        {
            sb.Append("\e[0");
            if (attr.HasFlag(CellAttrs.Bold)) sb.Append(";1");
            if (attr.HasFlag(CellAttrs.Dim)) sb.Append(";2");
            if (attr.HasFlag(CellAttrs.Underline)) sb.Append(";4");
            sb.Append(";38;2;").Append((fg >> 16) & 0xFF).Append(';').Append((fg >> 8) & 0xFF).Append(';').Append(fg & 0xFF);
            sb.Append(";48;2;").Append((bg >> 16) & 0xFF).Append(';').Append((bg >> 8) & 0xFF).Append(';').Append(bg & 0xFF);
            sb.Append('m');
        }
    }
}
