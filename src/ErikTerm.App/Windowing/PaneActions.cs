using ErikTerm.App.Chrome;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// The actions that work on the focused pane, the split layout and the window itself: zoom, focus and resize by
/// direction (handing the key to Neovim / tmux where they want it), font zoom, scrolling the history, selecting,
/// pasting, full screen and transparency.
/// </summary>
internal sealed class PaneActions(IWindowHost window, Lock gate, PaneSelect paneSelect, WindowBackdrop backdrop)
{
    /// <summary>Ctrl+= / Ctrl+- / Ctrl+0: this window's font zoom, from 0.4 to 4 times the size.</summary>
    public void ZoomFont(string action)
    {
        float zoom = window.FontZoom;
        window.FontZoom = action switch
        {
            "IncreaseFontSize" => MathF.Min(zoom * 1.1f, 4),
            "DecreaseFontSize" => MathF.Max(zoom / 1.1f, 0.4f),
            _ => 1,
        };
        window.ApplyFontSize();
    }

    public void ToggleZoom()
    {
        if (window.ShownPaneCount > 1) window.SetZoom(window.Zoomed is null ? window.Focused : null);
    }

    public void OpenPaneSelect(long now)
    {
        if (window.ShownPaneCount > 1) paneSelect.Open(window.PaneRects.Select(p => p.Pane), window.Theme, now);
        else window.Toast(ToastKind.Info, "Only one pane", "Split with Ctrl+Shift+\\ or Ctrl+Shift+-");
    }

    public void PasteFrom(string source) =>
        window.PasteText(window.Focused, source == "PrimarySelection" ? Sdl.GetPrimarySelectionText() : Sdl.GetClipboardText(), broadcast: true);

    public void ScrollByPage(string firstArg, long now)
    {
        var focused = window.Focused;
        int pages = int.TryParse(firstArg, out int pg) ? pg : -1;
        lock (gate) focused.Term.ScrollView(-pages * Math.Max(1, focused.Term.Rows - 2));
        focused.ScrollActivityAt = now;
    }

    public void ScrollToTop(long now)
    {
        var focused = window.Focused;
        lock (gate) focused.Term.ScrollView(int.MaxValue / 2);
        focused.ScrollActivityAt = now;
    }

    public void ScrollToBottom(long now)
    {
        var focused = window.Focused;
        lock (gate) focused.Term.ResetView();
        focused.ScrollActivityAt = now;
    }

    public void ClearScrollback()
    {
        var focused = window.Focused;
        lock (gate) { focused.Term.Feed("\e[3J"u8); focused.Selection = null; }
    }

    /// <summary>Palette › Reset Terminal: RIS for the focused pane's program.</summary>
    public void ResetTerminal()
    {
        var focused = window.Focused;
        lock (gate) focused.Term.Feed("\ec"u8);
    }

    public void SelectAll()
    {
        var focused = window.Focused;
        lock (gate)
        {
            var t = focused.Term;
            focused.Selection = new Selection
            {
                AnchorLine = t.FirstLine, AnchorCol = 0, HeadLine = t.EndLine - 1, HeadCol = t.Columns - 1, Mode = SelectionMode.Line,
            };
        }
    }

    public void ToggleFullScreen()
    {
        nint sdlWindow = window.SdlWindow;
        bool full = (Sdl.GetWindowFlags(sdlWindow) & Sdl.WINDOW_FULLSCREEN) != 0;
        Sdl.SetWindowFullscreen(sdlWindow, !full);
    }

    public void ToggleTransparency()
    {
        bool opaque = backdrop.ToggleOpaque(); // an opaque window has nothing to blur (Chrome/WindowBackdrop.cs)
        window.Toast(ToastKind.Info, opaque ? "Transparency off" : "Transparency on");
    }

    public static Direction ParseDirection(string s) => s.ToLowerInvariant() switch
    {
        "left" => Direction.Left,
        "right" => Direction.Right,
        "up" => Direction.Up,
        _ => Direction.Down,
    };

    /// <summary>
    /// Neovim (smart-splits sets IS_NVIM) and tmux handle these keys themselves first — the same rules
    /// as the wezterm config this replaces.
    /// </summary>
    private bool ShouldPassThrough(Pane pane)
    {
        bool nvim;
        lock (gate) nvim = pane.Term.UserVars.TryGetValue("IS_NVIM", out var v) && v == "true";
        return nvim || window.IsRealTmux(pane); // WindowFeatures/WindowTmux.cs: not erikterm's own tmux shim
    }

    public void ActivateDirection(Direction dir, uint key, ushort mod)
    {
        bool fromKey = key != 0;
        if (window.Zoomed is not null) window.SetZoom(null);
        var focused = window.Focused;
        var neighbor = window.Layout.Neighbor(focused, dir, window.ContentBounds(), window.Gap);
        if (fromKey && (neighbor is null || ShouldPassThrough(focused)))
        {
            window.PassThrough(key, mod);
            return;
        }
        if (neighbor is not null) window.Focus(neighbor);
    }

    public void AdjustSize(Direction dir, int cells, uint key, ushort mod)
    {
        bool fromKey = key != 0;
        var focused = window.Focused;
        if (fromKey && (window.ShownPaneCount == 1 || ShouldPassThrough(focused)))
        {
            window.PassThrough(key, mod);
            return;
        }
        var fonts = window.Fonts;
        int px = cells * (dir is Direction.Left or Direction.Right ? fonts.CellWidth : fonts.CellHeight);
        int min = 4 * fonts.CellWidth;
        if (window.Layout.Adjust(focused, dir, px, window.ContentBounds(), window.Gap, min)) window.ApplyLayout(animate: true);
    }

    public static int AdjustAmount(string arg) => arg.Contains(',') && int.TryParse(arg.Split(',')[1].Trim(), out int a) ? a : 3;

    /// <summary>A pane's grid grows by <paramref name="cells"/> along <paramref name="axis"/> (vim mode's Ctrl-w + / -).</summary>
    public bool GrowFocused(SplitAxis axis, int cells)
    {
        var fonts = window.Fonts;
        return window.Layout.Grow(window.Focused, axis, cells * (axis == SplitAxis.Horizontal ? fonts.CellWidth : fonts.CellHeight), window.ContentBounds(), window.Gap, 4 * fonts.CellWidth);
    }
}
