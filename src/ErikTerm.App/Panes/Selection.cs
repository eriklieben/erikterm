using ErikTerm.Core;

namespace ErikTerm.App.Panes;

internal enum SelectionMode { Cell, Word, Line, Block }

/// <summary>
/// A selection in absolute line coordinates (see Terminal.FirstLine), so it stays on its text while
/// output scrolls. Cell-mode columns are boundaries between cells (0..cols), so dragging across
/// half a cell selects it — like every modern terminal. Block mode (vim's Ctrl-v) is a rectangle: inclusive columns,
/// the same on every line.
/// </summary>
internal sealed class Selection
{
    public long AnchorLine, HeadLine;
    public int AnchorCol, HeadCol;
    public SelectionMode Mode;
    public bool Dragging;
    /// <summary>Made by vim mode's visual mode (Vim/WindowVim.cs keeps it in step with the vim cursor).</summary>
    public bool Vim;

    /// <summary>Inclusive cell range, normalised start ≤ end; null when nothing is selected.</summary>
    public (long StartLine, int StartCol, long EndLine, int EndCol)? Range(Terminal t)
    {
        long sl = AnchorLine, el = HeadLine;
        int sc = AnchorCol, ec = HeadCol;
        if (Mode == SelectionMode.Block) return (Math.Min(sl, el), Math.Min(sc, ec), Math.Max(sl, el), Math.Max(sc, ec));
        if (sl > el || (sl == el && sc > ec)) (sl, sc, el, ec) = (el, ec, sl, sc);

        switch (Mode)
        {
            case SelectionMode.Cell:
                ec -= 1; // boundary → last included cell
                if (sl == el && ec < sc) return null;
                if (ec < 0)
                {
                    if (el == sl) return null;
                    el--;
                    ec = t.Columns - 1;
                }
                break;
            case SelectionMode.Word:
                sc = t.WordAt(sl, sc).Start;
                ec = t.WordAt(el, ec).End;
                break;
            case SelectionMode.Line:
                sc = 0;
                ec = t.Columns - 1;
                break;
        }
        return (sl, sc, el, ec);
    }

    public string Text(Terminal t)
    {
        if (Range(t) is not var (sl, sc, el, ec)) return "";
        if (Mode != SelectionMode.Block) return t.GetText(sl, sc, el, ec);
        var lines = new List<string>();
        for (long line = sl; line <= el; line++) lines.Add(t.GetText(line, sc, line, ec, block: true).TrimEnd());
        return string.Join('\n', lines);
    }
}
