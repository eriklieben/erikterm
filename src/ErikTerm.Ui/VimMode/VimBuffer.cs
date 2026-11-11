using ErikTerm.Core;

namespace ErikTerm.Ui.VimMode;

/// <summary>A cell position in absolute lines (see <see cref="Terminal.FirstLine"/>), so it stays on its text while output scrolls.</summary>
public readonly record struct VimPos(long Line, int Col) : IComparable<VimPos>
{
    public int CompareTo(VimPos other) => Line != other.Line ? Line.CompareTo(other.Line) : Col.CompareTo(other.Col);
    public static bool operator <(VimPos a, VimPos b) => a.CompareTo(b) < 0;
    public static bool operator >(VimPos a, VimPos b) => a.CompareTo(b) > 0;
    public static bool operator <=(VimPos a, VimPos b) => a.CompareTo(b) <= 0;
    public static bool operator >=(VimPos a, VimPos b) => a.CompareTo(b) >= 0;
    public static VimPos Min(VimPos a, VimPos b) => a <= b ? a : b;
    public static VimPos Max(VimPos a, VimPos b) => a >= b ? a : b;
}

/// <summary>Character classes for word motions. <see cref="Empty"/> is an empty line, which counts as a word.</summary>
public enum VimClass : byte { Blank, Punct, Word, Empty }

/// <summary>
/// The terminal as a vim buffer: every row (scrollback and screen) is a line; a soft-wrapped row continues on the
/// next, so words broken by a wrap stay one word. A wide character or a grapheme cluster is one position (the cursor
/// never rests on a wide character's second cell). Call with the terminal locked.
/// </summary>
public static class VimBuffer
{
    /// <summary>The last line vim moves to: the terminal cursor's row, or a row below it that has text.</summary>
    public static long LastLine(Terminal t)
    {
        long cursor = t.IsAlternateScreen ? t.EndLine - 1 : t.ScreenTopLine + t.CursorY;
        for (long line = t.EndLine - 1; line > cursor; line--)
            if (LastContentCol(t, line) >= 0) return line;
        return Math.Max(t.FirstLine, cursor);
    }

    public static VimPos ClampLine(Terminal t, VimPos p)
    {
        long line = Math.Clamp(p.Line, t.FirstLine, LastLine(t));
        return line == p.Line ? p : new VimPos(line, p.Col);
    }

    /// <summary>The last column with a visible character (wide: its first cell), or -1 for an empty line.</summary>
    public static int LastContentCol(Terminal t, long line)
    {
        if (t.GetLine(line) is not { } row) return -1;
        var cells = row.Cells;
        for (int x = cells.Length - 1; x >= 0; x--)
        {
            ref readonly var c = ref cells[x];
            if ((c.Flags & CellAttributes.WideTrail) != 0) continue;
            if (c.Rune > 0x20) return x;
        }
        return -1;
    }

    /// <summary>The row's width in cells (it may differ from Columns until the next write after a resize).</summary>
    public static int Width(Terminal t, long line) => t.GetLine(line)?.Width ?? t.Columns;

    public static bool Wrapped(Terminal t, long line) => t.GetLine(line)?.Wrapped == true;

    /// <summary>Moves a column off a wide character's second cell.</summary>
    public static int Lead(Terminal t, long line, int col)
    {
        if (col <= 0 || t.GetLine(line) is not { } row || col >= row.Cells.Length) return Math.Max(0, col);
        return (row.Cells[col].Flags & CellAttributes.WideTrail) != 0 ? col - 1 : col;
    }

    /// <summary>1, or 2 for a wide character.</summary>
    public static int CellWidth(Terminal t, long line, int col)
    {
        if (t.GetLine(line) is not { } row || col < 0 || col >= row.Cells.Length) return 1;
        return (row.Cells[col].Flags & CellAttributes.WideLead) != 0 ? 2 : 1;
    }

    public static int Rune(Terminal t, long line, int col)
    {
        if (t.GetLine(line) is not { } row || col < 0 || col >= row.Cells.Length) return 0;
        col = Lead(t, line, col);
        return row.Cells[col].Rune;
    }

    public static VimClass ClassOf(int rune, bool bigWord)
    {
        if (rune <= 0x20) return VimClass.Blank;
        if (bigWord) return VimClass.Word;
        if (rune > 0x7F) return VimClass.Word;
        char c = (char)rune;
        return char.IsLetterOrDigit(c) || c == '_' ? VimClass.Word : VimClass.Punct;
    }

    /// <summary>First non-blank column of a line (0 when blank).</summary>
    public static int FirstNonBlank(Terminal t, long line)
    {
        if (t.GetLine(line) is not { } row) return 0;
        var cells = row.Cells;
        for (int x = 0; x < cells.Length; x++)
            if ((cells[x].Flags & CellAttributes.WideTrail) == 0 && cells[x].Rune > 0x20) return x;
        return 0;
    }

    /// <summary>The next position on the row (past a wide character), or -1 at the row's end.</summary>
    public static int NextCol(Terminal t, long line, int col)
    {
        int next = col + CellWidth(t, line, col);
        return next < Width(t, line) ? next : -1;
    }

    /// <summary>The previous position on the row (a wide character's first cell), or -1 at column 0.</summary>
    public static int PrevCol(Terminal t, long line, int col) => col <= 0 ? -1 : Lead(t, line, col - 1);

    // ---- The character stream for word motions -------------------------------------------------

    /// <summary>
    /// One position of the stream: a cell, the line break after a line's text (<see cref="Eol"/>, a blank), or an
    /// empty line (<see cref="VimClass.Empty"/>). Wrapped rows have no line break: the next row continues them.
    /// </summary>
    public readonly record struct Ch(long Line, int Col, VimClass Class, bool Eol)
    {
        public VimPos Pos => new(Line, Col);
    }

    /// <summary>The stream position at <paramref name="p"/> (past the text on a line it is that line's break).</summary>
    public static Ch At(Terminal t, VimPos p, bool bigWord)
    {
        int last = LastContentCol(t, p.Line);
        bool wrapped = Wrapped(t, p.Line);
        if (!wrapped && last < 0) return new Ch(p.Line, 0, VimClass.Empty, false);
        if (!wrapped && p.Col > last) return new Ch(p.Line, last + 1, VimClass.Blank, true);
        int col = Lead(t, p.Line, p.Col);
        return new Ch(p.Line, col, ClassOf(Rune(t, p.Line, col), bigWord), false);
    }

    private static Ch LineStart(Terminal t, long line, bool bigWord)
    {
        if (!Wrapped(t, line) && LastContentCol(t, line) < 0) return new Ch(line, 0, VimClass.Empty, false);
        return new Ch(line, 0, ClassOf(Rune(t, line, 0), bigWord), false);
    }

    private static Ch LineEnd(Terminal t, long line, bool bigWord)
    {
        int last = LastContentCol(t, line);
        if (Wrapped(t, line))
        {
            int col = Lead(t, line, Width(t, line) - 1);
            return new Ch(line, col, ClassOf(Rune(t, line, col), bigWord), false);
        }
        return last < 0 ? new Ch(line, 0, VimClass.Empty, false) : new Ch(line, last + 1, VimClass.Blank, true);
    }

    public static Ch? Next(Terminal t, Ch ch, long lastLine, bool bigWord)
    {
        if (ch.Eol || ch.Class == VimClass.Empty)
            return ch.Line < lastLine ? LineStart(t, ch.Line + 1, bigWord) : null;
        int next = NextCol(t, ch.Line, ch.Col);
        if (Wrapped(t, ch.Line))
        {
            if (next >= 0) return new Ch(ch.Line, next, ClassOf(Rune(t, ch.Line, next), bigWord), false);
            return ch.Line < lastLine ? LineStart(t, ch.Line + 1, bigWord) : null;
        }
        int last = LastContentCol(t, ch.Line);
        if (next < 0 || next > last) return new Ch(ch.Line, last + 1, VimClass.Blank, true);
        return new Ch(ch.Line, next, ClassOf(Rune(t, ch.Line, next), bigWord), false);
    }

    public static Ch? Prev(Terminal t, Ch ch, long firstLine, bool bigWord)
    {
        if (ch.Eol)
        {
            int last = LastContentCol(t, ch.Line);
            return new Ch(ch.Line, last, ClassOf(Rune(t, ch.Line, last), bigWord), false);
        }
        int prev = ch.Class == VimClass.Empty ? -1 : PrevCol(t, ch.Line, ch.Col);
        if (prev >= 0) return new Ch(ch.Line, prev, ClassOf(Rune(t, ch.Line, prev), bigWord), false);
        return ch.Line > firstLine ? LineEnd(t, ch.Line - 1, bigWord) : null;
    }
}
