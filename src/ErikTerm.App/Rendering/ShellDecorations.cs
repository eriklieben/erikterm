using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Link and shell-integration decorations on a pane: the hovered link's underline, prompt markers
/// in the left gutter (failed commands; every prompt while scrolled back), the command block
/// under a gutter hover, and the flash after jumping to a prompt. Called by <see cref="PaneRenderer"/>
/// under the terminal lock; allocation-free.
/// </summary>
internal static class ShellDecorations
{
    /// <summary>Left edge and width of the gutter marker for a pane whose content starts at <paramref name="paneX"/>.</summary>
    public static (float X, float W) GutterBar(float paneX, Theme th)
    {
        float w = MathF.Max(2, MathF.Round(th.S(2.5f)));
        float x = paneX >= th.S(5) ? MathF.Round(paneX - th.S(3.5f)) : paneX + th.S(0.5f);
        return (x, w);
    }

    /// <summary>Where a pane's rows are on screen this frame.</summary>
    private readonly record struct View(RectF Rect, float Ox, float Oy, int Cw, int Ch, long ViewTop, int Rows, float Appear);

    /// <param name="scrollDy">Smooth-scrolling offset of the rows in pixels (see SmoothScroll).</param>
    public static void Draw(DrawList dl, Pane pane, RectF rect, Theme th, long now, float scrollDy = 0)
    {
        var t = pane.Term;
        var links = pane.Links;
        var fonts = dl.Fonts;
        float ox = MathF.Round(rect.X), oy = MathF.Round(rect.Y) + scrollDy;
        var view = new View(rect, ox, oy, fonts.CellWidth, fonts.CellHeight, t.ViewTopLine, t.Rows, pane.Appear.Value(now));
        float focus = 0.55f + 0.45f * pane.Focus.Value(now);

        dl.PushClip(rect);
        DrawJumpFlash(dl, links, th, in view, links.Flash.Value(now));
        float gutter = links.GutterFade.Value(now);
        DrawCommandWash(dl, links, th, in view, gutter);
        DrawHoveredLink(dl, links, fonts, th, in view, links.HoverFade.Value(now));
        dl.PopClip();

        // Gutter markers (outside the pane clip: they sit in the padding to its left).
        if (t.IsAlternateScreen || !t.HasPromptMarks) return;
        var (gx, gw) = GutterBar(ox, th);
        DrawPromptMarks(dl, t, links, th, in view, gx, gw, gutter, focus);
        DrawCommandRail(dl, links, th, in view, gx, gw, gutter);
    }

    /// <summary>Flash of the prompt ScrollToPrompt jumped to.</summary>
    private static void DrawJumpFlash(DrawList dl, PaneLinks links, Theme th, in View v, float flash)
    {
        if (flash <= 0.001f || links.FlashLine < v.ViewTop || links.FlashLine >= v.ViewTop + v.Rows) return;
        float py = v.Oy + (links.FlashLine - v.ViewTop) * v.Ch;
        dl.Rect(new RectF(v.Rect.X, py, v.Rect.W, v.Ch), th.Accent, 0.22f * flash * v.Appear);
    }

    /// <summary>The on-screen lines of the hovered command's block; false when there are none.</summary>
    private static bool GutterBlock(PaneLinks links, in View v, float gutter, out long first, out long last)
    {
        first = Math.Max(links.GutterStart, v.ViewTop);
        last = Math.Min(links.GutterEnd, v.ViewTop + v.Rows - 1);
        return gutter > 0.001f && links.GutterStart >= 0 && last >= first;
    }

    /// <summary>Command block under a gutter hover: a faint wash over its output.</summary>
    private static void DrawCommandWash(DrawList dl, PaneLinks links, Theme th, in View v, float gutter)
    {
        if (!GutterBlock(links, in v, gutter, out long first, out long last)) return;
        dl.Rect(new RectF(v.Rect.X, v.Oy + (first - v.ViewTop) * v.Ch, v.Rect.W, (last - first + 1) * v.Ch), th.Accent, 0.07f * gutter * v.Appear);
    }

    /// <summary>Hovered link: underline (accent), plus a soft highlight while Ctrl arms it.</summary>
    private static void DrawHoveredLink(DrawList dl, PaneLinks links, FontSet fonts, Theme th, in View v, float hover)
    {
        if (hover <= 0.001f || links.Hovered.Count == 0) return;
        float thick = MathF.Max(1, MathF.Round(fonts.LineThickness * (links.Armed ? 1.5f : 1f)));
        float uy = fonts.UnderlinePosition;
        foreach (var seg in links.Hovered)
        {
            if (seg.Line < v.ViewTop || seg.Line >= v.ViewTop + v.Rows) continue;
            float py = v.Oy + (seg.Line - v.ViewTop) * v.Ch;
            float x0 = v.Ox + seg.StartCol * v.Cw, x1 = v.Ox + (seg.EndCol + 1) * v.Cw;
            if (links.Armed) dl.RoundRect(new RectF(x0 - 1, py, x1 - x0 + 2, v.Ch), th.S(3), th.Accent, 0.16f * hover * v.Appear);
            dl.Rect(x0, py + uy, x1, py + uy + thick, th.Accent, (links.Armed ? 1f : 0.7f) * hover * v.Appear);
        }
    }

    private static void DrawPromptMarks(DrawList dl, Terminal t, PaneLinks links, Theme th, in View v, float gx, float gw, float gutter, float focus)
    {
        bool scrolledBack = t.ViewOffset > 0;
        float barH = MathF.Round(v.Ch * 0.72f);
        for (int y = 0; y < v.Rows; y++)
        {
            var row = t.GetVisibleRow(y);
            if ((row.Marks & SemanticMarks.Prompt) == 0) continue;
            float py = v.Oy + y * v.Ch;
            if (py < v.Rect.Y - 1 || py + v.Ch > v.Rect.Bottom + 1) continue; // mid smooth-scroll: outside the pane clip, so skip partial rows
            bool inBlock = gutter > 0.001f && v.ViewTop + y == links.GutterPrompt;
            if (row.CommandFailed)
                dl.RoundRect(new RectF(gx, MathF.Round(py + (v.Ch - barH) / 2), gw, barH), gw / 2, th.Danger, 0.95f * focus * v.Appear);
            else if (scrolledBack || inBlock)
                DrawPromptDot(dl, row.ExitCode == 0 ? th.Success : th.Accent, th, in v, gx, gw, py, focus);
        }
    }

    private static void DrawPromptDot(DrawList dl, uint color, Theme th, in View v, float gx, float gw, float py, float focus)
    {
        float d = MathF.Max(3, MathF.Round(th.S(4)));
        dl.RoundRect(new RectF(MathF.Round(gx + gw / 2 - d / 2), MathF.Round(py + (v.Ch - d) / 2), d, d), d / 2, color, 0.75f * focus * v.Appear);
    }

    /// <summary>Gutter hover: an accent rail along the hovered command's output.</summary>
    private static void DrawCommandRail(DrawList dl, PaneLinks links, Theme th, in View v, float gx, float gw, float gutter)
    {
        if (!GutterBlock(links, in v, gutter, out long first, out long last)) return;
        float top = v.Oy + (first - v.ViewTop) * v.Ch + th.S(2), bottom = v.Oy + (last - v.ViewTop + 1) * v.Ch - th.S(2);
        dl.RoundRect(new RectF(gx, top, gw, MathF.Max(gw, bottom - top)), gw / 2, th.Accent, 0.85f * gutter * v.Appear);
    }
}
