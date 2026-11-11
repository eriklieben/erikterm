using ErikTerm.Core;

namespace ErikTerm.Ui.VimMode;

/// <summary>A selected range, inclusive at both ends; <see cref="Linewise"/> ranges cover whole lines.</summary>
public readonly record struct VimRange(VimPos Start, VimPos End, bool Linewise = false);

/// <summary>Text objects: iw aw iW aW (on the cursor's row) and ip / ap (the command's output, else a paragraph).</summary>
public static class VimTextObjects
{
    /// <summary>iw / aw / iW / aW at <paramref name="p"/>.</summary>
    public static VimRange Word(Terminal t, VimPos p, bool around, bool big)
    {
        long line = p.Line;
        int width = VimBuffer.Width(t, line);
        int col = Math.Clamp(VimBuffer.Lead(t, line, p.Col), 0, Math.Max(0, width - 1));
        int last = VimBuffer.LastContentCol(t, line);
        if (last < 0) return new VimRange(new VimPos(line, 0), new VimPos(line, 0));
        if (col > last) col = last;

        var words = new LineWords(t, line, last, big);
        var cls = words.ClassAt(col);
        (int s, int e) = words.Run(col, cls);
        if (around) (s, e) = cls == VimClass.Blank ? words.WithWordAfter(s, e) : words.WithBlanksBeside(s, e);
        return new VimRange(new VimPos(line, s), new VimPos(line, e));
    }

    /// <summary>The runs of same-class characters on one line, up to its last content column.</summary>
    private readonly struct LineWords(Terminal t, long line, int last, bool big)
    {
        public VimClass ClassAt(int x) => VimBuffer.ClassOf(VimBuffer.Rune(t, line, x), big);

        public (int Start, int End) Run(int at, VimClass c)
        {
            int start = at, end = at;
            for (int x = VimBuffer.PrevCol(t, line, start); x >= 0 && ClassAt(x) == c; x = VimBuffer.PrevCol(t, line, x)) start = x;
            for (int x = VimBuffer.NextCol(t, line, end); x >= 0 && x <= Math.Max(last, at) && ClassAt(x) == c; x = VimBuffer.NextCol(t, line, x)) end = x;
            return (start, end);
        }

        /// <summary>Blanks plus the word after them.</summary>
        public (int Start, int End) WithWordAfter(int s, int e)
        {
            int next = VimBuffer.NextCol(t, line, e);
            if (next >= 0 && next <= last) e = Run(next, ClassAt(next)).End;
            return (s, e);
        }

        /// <summary>A word plus the blanks after it, or else the ones before it.</summary>
        public (int Start, int End) WithBlanksBeside(int s, int e)
        {
            int next = VimBuffer.NextCol(t, line, e);
            if (next >= 0 && next <= last && ClassAt(next) == VimClass.Blank) return (s, Run(next, VimClass.Blank).End);
            int prev = VimBuffer.PrevCol(t, line, s);
            if (prev >= 0 && ClassAt(prev) == VimClass.Blank) s = Run(prev, VimClass.Blank).Start;
            return (s, e);
        }
    }

    /// <summary>
    /// ip / ap: the output of the command at the cursor (OSC 133) — ap adds its prompt line — or, without shell
    /// integration, the paragraph (run of non-blank lines, or of blank ones) around the cursor.
    /// </summary>
    public static VimRange Paragraph(Terminal t, VimPos p, bool around)
    {
        if (t.HasPromptMarks && !t.IsAlternateScreen && t.CommandAt(p.Line) is { } block)
        {
            long start = around ? block.PromptLine : block.OutputStart;
            return new VimRange(new VimPos(start, 0), new VimPos(block.OutputEnd, 0), Linewise: true);
        }
        long first = t.FirstLine, last = VimBuffer.LastLine(t);
        bool blank = VimMotions.Blank(t, p.Line);
        long s = p.Line, e = p.Line;
        while (s > first && VimMotions.Blank(t, s - 1) == blank) s--;
        while (e < last && VimMotions.Blank(t, e + 1) == blank) e++;
        if (around && !blank) while (e < last && VimMotions.Blank(t, e + 1)) e++;
        return new VimRange(new VimPos(s, 0), new VimPos(e, 0), Linewise: true);
    }
}
