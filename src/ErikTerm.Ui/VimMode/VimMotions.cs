using ErikTerm.Core;

namespace ErikTerm.Ui.VimMode;

/// <summary>Vim motions over a <see cref="Terminal"/> (see <see cref="VimBuffer"/> for the buffer model). Call with the terminal locked.</summary>
public static class VimMotions
{
    private const int MaxScanLines = 20_000;

    // ---- Words ---------------------------------------------------------------------------------

    /// <summary>w / W: the start of the next word (an empty line counts as one).</summary>
    public static VimPos WordForward(Terminal t, VimPos p, int count, bool big)
    {
        long last = VimBuffer.LastLine(t);
        var ch = VimBuffer.At(t, p, big);
        for (int i = 0; i < count; i++)
        {
            var cls = ch.Class;
            VimBuffer.Ch? n = ch;
            if (cls is VimClass.Word or VimClass.Punct)
                while (n is { } x && x.Class == cls) n = VimBuffer.Next(t, x, last, big);
            else n = VimBuffer.Next(t, ch, last, big);
            while (n is { Class: VimClass.Blank } b) n = VimBuffer.Next(t, b, last, big);
            if (n is not { } found) return LastPos(t, last);
            ch = found;
        }
        return ch.Pos;
    }

    /// <summary>e / E: the end of the word (the next one when already at an end).</summary>
    public static VimPos WordEnd(Terminal t, VimPos p, int count, bool big)
    {
        long last = VimBuffer.LastLine(t);
        var ch = VimBuffer.At(t, p, big);
        for (int i = 0; i < count; i++)
        {
            var n = VimBuffer.Next(t, ch, last, big);
            while (n is { Class: VimClass.Blank or VimClass.Empty } b) n = VimBuffer.Next(t, b, last, big);
            if (n is not { } start) return LastPos(t, last);
            var cls = start.Class;
            var end = start;
            while (VimBuffer.Next(t, end, last, big) is { } x && x.Class == cls) end = x;
            ch = end;
        }
        return ch.Pos;
    }

    /// <summary>b / B: the start of the word (the previous one when already at a start).</summary>
    public static VimPos WordBackward(Terminal t, VimPos p, int count, bool big)
    {
        long first = t.FirstLine;
        var ch = VimBuffer.At(t, p, big);
        for (int i = 0; i < count; i++)
        {
            var n = VimBuffer.Prev(t, ch, first, big);
            while (n is { Class: VimClass.Blank } b) n = VimBuffer.Prev(t, b, first, big);
            if (n is not { } end) return new VimPos(first, 0);
            var cls = end.Class;
            var start = end;
            if (cls != VimClass.Empty)
                while (VimBuffer.Prev(t, start, first, big) is { } x && x.Class == cls) start = x;
            ch = start;
        }
        return ch.Pos;
    }

    /// <summary>ge / gE: the end of the previous word.</summary>
    public static VimPos WordEndBackward(Terminal t, VimPos p, int count, bool big)
    {
        long first = t.FirstLine;
        var ch = VimBuffer.At(t, p, big);
        for (int i = 0; i < count; i++)
        {
            var cls = ch.Class;
            VimBuffer.Ch? n = ch;
            if (cls is VimClass.Word or VimClass.Punct)
                while (n is { } x && x.Class == cls) n = VimBuffer.Prev(t, x, first, big);
            else n = VimBuffer.Prev(t, ch, first, big);
            while (n is { Class: VimClass.Blank } b) n = VimBuffer.Prev(t, b, first, big);
            if (n is not { } found) return new VimPos(first, 0);
            ch = found;
        }
        return ch.Pos;
    }

    private static VimPos LastPos(Terminal t, long last) => new(last, Math.Max(0, VimBuffer.LastContentCol(t, last)));

    // ---- Lines ---------------------------------------------------------------------------------

    /// <summary>The allowed column on a line: up to the last character (anywhere on the row in block mode / <paramref name="free"/>).</summary>
    public static int ClampCol(Terminal t, long line, int col, bool free)
    {
        int max = free ? VimBuffer.Width(t, line) - 1 : Math.Max(0, VimBuffer.LastContentCol(t, line));
        return VimBuffer.Lead(t, line, Math.Clamp(col, 0, Math.Max(0, max)));
    }

    /// <summary>j / k (and every other line-wise move): <paramref name="delta"/> lines, keeping the wanted column.</summary>
    public static VimPos Vertical(Terminal t, VimPos p, long delta, int wantCol, bool free)
    {
        long line = Math.Clamp(p.Line + delta, t.FirstLine, VimBuffer.LastLine(t));
        return new VimPos(line, ClampCol(t, line, wantCol, free));
    }

    public static VimPos FirstNonBlank(Terminal t, long line) => new(line, VimBuffer.FirstNonBlank(t, line));

    public static VimPos LineEnd(Terminal t, long line) => new(line, Math.Max(0, VimBuffer.LastContentCol(t, line)));

    /// <summary>
    /// f / F / t / T: the <paramref name="count"/>th <paramref name="c"/> on the line, or null. <paramref name="repeat"/>
    /// (; and ,): a t/T already next to its character looks past it instead of staying put.
    /// </summary>
    public static VimPos? FindChar(Terminal t, VimPos p, char kind, char c, int count, bool repeat = false)
    {
        bool forward = kind is 'f' or 't', till = kind is 't' or 'T';
        int at = p.Col;
        for (int n = 0; n < count; n++)
        {
            int x = StepCol(t, p.Line, at, forward);
            if (till && repeat && n == 0 && x >= 0 && Matches(t, p.Line, x, c)) x = StepCol(t, p.Line, x, forward);
            x = SeekChar(t, p.Line, x, c, forward);
            if (x < 0) return null;
            at = x;
        }
        if (till) at = StepCol(t, p.Line, at, !forward);
        return at < 0 ? null : new VimPos(p.Line, at);
    }

    private static int StepCol(Terminal t, long line, int col, bool forward) =>
        forward ? VimBuffer.NextCol(t, line, col) : VimBuffer.PrevCol(t, line, col);

    private static int SeekChar(Terminal t, long line, int x, char c, bool forward)
    {
        while (x >= 0 && !Matches(t, line, x, c)) x = StepCol(t, line, x, forward);
        return x;
    }

    private static bool Matches(Terminal t, long line, int col, char c) => VimBuffer.Rune(t, line, col) == c;

    // ---- Brackets ------------------------------------------------------------------------------

    /// <summary>%: the bracket matching the one at (or after, on the line) the cursor.</summary>
    public static VimPos? MatchBracket(Terminal t, VimPos p)
    {
        int col = p.Col, rune = 0;
        while (col >= 0)
        {
            rune = VimBuffer.Rune(t, p.Line, col);
            if (rune is '(' or ')' or '[' or ']' or '{' or '}') break;
            col = VimBuffer.NextCol(t, p.Line, col);
        }
        if (col < 0) return null;
        (int open, int close, bool forward) = rune switch
        {
            '(' => ('(', ')', true), ')' => ('(', ')', false),
            '[' => ('[', ']', true), ']' => ('[', ']', false),
            '{' => ('{', '}', true), _ => ('{', '}', false),
        };
        return ScanToMatch(t, p.Line, col, open, close, forward);
    }

    /// <summary>Walks from a bracket across lines until the nesting closes, or null at the buffer's end or after <see cref="MaxScanLines"/> lines.</summary>
    private static VimPos? ScanToMatch(Terminal t, long line, int x, int open, int close, bool forward)
    {
        int sign = forward ? 1 : -1;
        int depth = 0;
        long first = t.FirstLine, last = VimBuffer.LastLine(t);
        for (int scanned = 0; scanned < MaxScanLines;)
        {
            int r = VimBuffer.Rune(t, line, x);
            if (r == open) depth += sign;
            else if (r == close) depth -= sign;
            if (depth == 0) return new VimPos(line, x);
            x = StepCol(t, line, x, forward);
            if (x >= 0) continue;
            line += sign;
            scanned++;
            if (line < first || line > last) return null;
            x = forward ? 0 : VimBuffer.Lead(t, line, VimBuffer.Width(t, line) - 1);
        }
        return null;
    }

    // ---- Prompts and command output (OSC 133) ---------------------------------------------------

    /// <summary>[[ / ]]: the previous / next prompt, on the command it ran (or its first character).</summary>
    public static VimPos? Prompt(Terminal t, VimPos p, int dir, int count)
    {
        long line = p.Line;
        for (int i = 0; i < count; i++)
        {
            if (t.FindPrompt(line, dir) is not { } next) return i == 0 ? null : PromptPos(t, line);
            line = next;
        }
        return PromptPos(t, line);
    }

    private static VimPos PromptPos(Terminal t, long line)
    {
        int col = t.GetLine(line) is { CommandColumn: >= 0 } row ? row.CommandColumn : VimBuffer.FirstNonBlank(t, line);
        return new VimPos(line, ClampCol(t, line, col, free: false));
    }

    /// <summary>
    /// { / }: the start of the previous / next command's output (OSC 133;C), or — without shell integration — vim's
    /// paragraph motion (the blank line before / after the text).
    /// </summary>
    public static VimPos? Block(Terminal t, VimPos p, int dir, int count)
    {
        if (!t.HasPromptMarks || t.IsAlternateScreen) return Paragraph(t, p, dir, count);
        long first = t.FirstLine, last = VimBuffer.LastLine(t);
        long line = p.Line, found = -1;
        for (int i = 0; i < count; i++)
        {
            long l = line + dir;
            for (; l >= first && l <= last; l += dir)
                if (t.GetLine(l) is { } row && (row.Marks & SemanticMarks.Output) != 0 && (row.Marks & SemanticMarks.Prompt) == 0
                    && VimBuffer.LastContentCol(t, l) >= 0) break;
            if (l < first || l > last) break;
            found = line = l;
        }
        return found < 0 ? null : FirstNonBlank(t, found);
    }

    /// <summary>Vim's { and }: the next blank line after (before) a run of text, or the last (first) line.</summary>
    public static VimPos? Paragraph(Terminal t, VimPos p, int dir, int count)
    {
        long first = t.FirstLine, last = VimBuffer.LastLine(t);
        long line = p.Line;
        for (int i = 0; i < count; i++)
        {
            long l = line + dir;
            while (l >= first && l <= last && Blank(t, l)) l += dir;  // skip blank lines first
            while (l >= first && l <= last && !Blank(t, l)) l += dir; // then the paragraph
            line = Math.Clamp(l, first, last);
            if (l < first || l > last) break;
        }
        return line == p.Line ? null : new VimPos(line, 0);
    }

    public static bool Blank(Terminal t, long line) => VimBuffer.LastContentCol(t, line) < 0;
}
