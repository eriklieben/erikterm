using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Vim mode's cursor: a steady block in the accent color with the character under it redrawn in the background
/// color (shaped like its row, so clusters and wide characters come out whole). An unfocused pane gets an outline.
/// Drawn once per pane per frame, over the program's cursor (<see cref="PaneRenderer"/>).
/// </summary>
internal static class VimCursorRenderer
{
    /// <param name="frame">The pane, its dimming as the grid used it and the renderer's glyph drawing, for the character under the block.</param>
    public static void Draw(in PaneFrame frame, Theme theme, bool active)
    {
        var dl = frame.Dl;
        var pane = frame.Pane;
        float ox = frame.Ox, oy = frame.Oy, appear = frame.Appear;
        var vim = pane.Vim!;
        var t = pane.Term;
        var p = vim.Cursor;
        long row = p.Line - t.ViewTopLine;
        if (row < -1 || row > t.Rows || t.GetLine(p.Line) is not { } cells) return;
        var fonts = dl.Fonts;
        int cw = fonts.CellWidth, ch = fonts.CellHeight;
        int width = VimBuffer.CellWidth(t, p.Line, p.Col);
        float px = MathF.Round(ox + p.Col * cw), py = MathF.Round(oy + row * ch);
        var box = new RectF(px, py, cw * width, ch);
        uint accent = frame.Dim.Apply(theme.Accent);
        if (!active)
        {
            float line = MathF.Max(1, MathF.Round(theme.S(1.5f)));
            dl.Rect(box.X, box.Y, box.Right, box.Y + line, accent, appear);
            dl.Rect(box.X, box.Bottom - line, box.Right, box.Bottom, accent, appear);
            dl.Rect(box.X, box.Y, box.X + line, box.Bottom, accent, appear);
            dl.Rect(box.Right - line, box.Y, box.Right, box.Bottom, accent, appear);
            return;
        }
        dl.Rect(box, accent, appear);
        int n = Math.Min(cells.Cells.Length, t.Columns);
        if (p.Col >= n) return;
        dl.PushClip(box);
        frame.Text.DrawCells(dl, new GridText.RowShape(theme.HarfbuzzFeatures, cells, n, p.Col), Math.Max(0, p.Col - 1), Math.Min(n - 1, p.Col + width),
            ox, py, new GridText.CellInk(Colors.Mix(theme.Background, 0x000000, 0.15f), appear));
        dl.PopClip();
    }
}
