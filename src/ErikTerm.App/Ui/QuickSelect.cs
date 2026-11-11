using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>What picking a label does: copy it (Ctrl+Shift+Space), or vim mode's hints (; / gh) — move the cursor there.</summary>
internal enum QuickSelectPurpose { Copy, VimHints }

/// <summary>
/// wezterm's QuickSelect: everything on screen that looks like a URL, path, hash, address, UUID or
/// long number gets a pill label; the rest of the pane dims. Typing a label copies the match
/// (Shift + label also pastes it). Cells are snapshotted on open, so drawing needs no lock.
/// Vim mode's hints use the same matcher and overlay: a label moves the vim cursor there, an upper-case label yanks it
/// (the <c>Paste</c> flag), Ctrl+label — or Enter / Ctrl-o, then the label — opens it.
/// </summary>
internal sealed class QuickSelect
{
    public readonly record struct GlyphCell(long Line, int Col, int Rune, FontStyle Style);

    public sealed class Item
    {
        public required string Text;
        public required MatchKind Kind;
        public required List<LineSegment> Segments;
        public required List<GlyphCell> Cells;
        public string Label = "";
        public readonly List<RectF> Hit = [];
    }

    private Pane? _pane;
    private List<Item> _items = [];
    private string _typed = "";
    private bool _open;
    private Tween _visible = new(0);
    private long _openedAt;
    private float _animMs = 160;
    private Item? _chosen;
    private string _countText = "";
    private static readonly (string Key, string Label)[] HelpParts = [("a–z", "copy"), ("Shift", "copy & paste"), ("Ctrl", "open"), ("Esc", "cancel")];
    private static readonly (string Key, string Label)[] VimHelpParts = [("a–z", "jump"), ("A–Z", "yank"), ("Ctrl / Enter", "open"), ("Esc", "cancel")];
    private static readonly (string Key, string Label)[] VimOpenParts = [("a–z", "open"), ("Enter", "jump instead"), ("Esc", "cancel")];
    private bool _openArmed; // vim hints: Enter / Ctrl-o was pressed, the next label opens
    private Tween _chosenFlash = new(0);

    public bool IsOpen => _open;
    public Pane? Pane => _pane;
    /// <summary>The last pick was made with Ctrl held: open it (link, file, commit) instead of copying.</summary>
    public bool ChosenToOpen { get; private set; }
    public QuickSelectPurpose Purpose { get; private set; }

    public bool IsAnimating(long now) =>
        _visible.IsAnimating(now) || _chosenFlash.IsAnimating(now) || (_open && now - _openedAt < PopMs + StaggerMs * 24);

    private float PopMs => _animMs * 1.8f;
    private const float StaggerMs = 7;

    public void Open(Pane pane, List<Item> items, Theme th, long now, QuickSelectPurpose purpose = QuickSelectPurpose.Copy)
    {
        Purpose = purpose;
        _openArmed = false;
        _pane = pane;
        _items = items;
        _typed = "";
        _open = true;
        _animMs = th.AnimationMs;
        _openedAt = now;
        _chosen = null;
        _countText = items.Count == 1 ? "1 match" : $"{items.Count} matches";
        _visible.Snap(0);
        _visible.Set(1, now, _animMs);
        // Labels from the bottom up: the newest output gets the home-row keys.
        var labels = HintLabels.Generate(items.Count);
        for (int i = 0; i < items.Count; i++) items[items.Count - 1 - i].Label = labels[i];
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * (_chosen is null ? 0.8f : 2.2f));
    }

    /// <summary>A pressed key; returns the chosen item and whether to paste it too.</summary>
    public (Item Item, bool Paste)? HandleKey(uint key, bool shift, long now, bool ctrl = false)
    {
        if (!_open) return null;
        if (key == Sdl.K_ESCAPE) { Close(now); return null; }
        if (key == Sdl.K_BACKSPACE) { DeleteTyped(now); return null; }
        if (IsArmKey(key, ctrl))
        {
            _openArmed = !_openArmed;
            return null;
        }
        if (key is < 'a' or > 'z') return null;

        string typed = _typed + (char)key;
        if (!AnyLabelStartsWith(typed)) return null; // not a label: ignore the key
        _typed = typed;
        if (ExactLabel(typed) is not { } exact) return null;
        ChosenToOpen = ctrl || _openArmed;
        return Choose(exact, shift && !ctrl, now);
    }

    private void DeleteTyped(long now)
    {
        if (_typed.Length > 0) _typed = _typed[..^1];
        else Close(now);
    }

    /// <summary>Vim hints: Enter or Ctrl-o makes the next label open instead of jump.</summary>
    private bool IsArmKey(uint key, bool ctrl) =>
        Purpose == QuickSelectPurpose.VimHints && (key is Sdl.K_RETURN or Sdl.K_KP_ENTER || ctrl && key == 'o');

    private bool AnyLabelStartsWith(string typed) =>
        _items.Any(item => item.Label.StartsWith(typed, StringComparison.Ordinal));

    /// <summary>The item whose whole label is <paramref name="typed"/>; the last one if several are.</summary>
    private Item? ExactLabel(string typed)
    {
        Item? exact = null;
        foreach (var item in _items)
            if (item.Label.StartsWith(typed, StringComparison.Ordinal) && item.Label.Length == typed.Length) exact = item;
        return exact;
    }

    private (Item, bool) Choose(Item item, bool paste, long now)
    {
        _chosen = item;
        _chosenFlash.Snap(1);
        _chosenFlash.Set(0, now, _animMs * 3);
        Close(now);
        return (item, paste);
    }

    public int HitTest(float x, float y)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Hit.Any(r => r.Contains(x, y))) return i;
        return -1;
    }

    /// <summary>Clicking a match picks it (Shift-click pastes); clicking elsewhere cancels.</summary>
    public (Item Item, bool Paste)? HandleMouseDown(float x, float y, bool shift, long now, bool ctrl = false)
    {
        if (!_open) return null;
        int hit = HitTest(x, y);
        if (hit < 0) { Close(now); return null; }
        ChosenToOpen = ctrl;
        return Choose(_items[hit], shift && !ctrl, now);
    }

    /// <summary>Where the pane's visible lines are, and how the labels are set.</summary>
    private readonly record struct PaneGrid(float Ox, float Oy, int CellW, int CellH, long ViewTop, int Rows, float LabelPx, FontSet.Metrics LabelMetrics, float Since)
    {
        public bool Shows(long line) => line >= ViewTop && line < ViewTop + Rows;
    }

    public void Draw(DrawList dl, Theme th, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f || _pane is null) return;
        var pane = _pane;
        int ch = dl.Fonts.CellHeight;
        RectF pr = pane.Rect.Value(now);

        dl.PushClip(pr);
        dl.Rect(pr, th.Background, 0.66f * v);

        float labelPx = MathF.Min(ch * 0.66f, th.UiPx * 0.95f);
        var grid = new PaneGrid(MathF.Round(pr.X), MathF.Round(pr.Y), dl.Fonts.CellWidth, ch,
            pane.Term.ViewTopLine, // a racy read is fine: worst case one stale frame
            pane.Term.Rows, labelPx, dl.MetricsFor(labelPx), now - _openedAt);

        for (int i = 0; i < _items.Count; i++)
            DrawItem(dl, th, _items[i], i, grid, v, now);
        dl.PopClip();

        if (_open || v > 0.01f) DrawHelp(dl, th, pr, v);
    }

    private void DrawItem(DrawList dl, Theme th, Item item, int index, in PaneGrid g, float v, long now)
    {
        item.Hit.Clear();
        bool candidate = _open ? item.Label.StartsWith(_typed, StringComparison.Ordinal) : item == _chosen;
        float a = v * (candidate ? 1 : 0.28f);
        if (!_open && item == _chosen) a = MathF.Max(a, _chosenFlash.Value(now));

        // Highlight + crisp glyphs over the dimmed grid.
        DrawSegments(dl, th, item, g, a, now);
        DrawCells(dl, th, item, g, a);

        if (_open && item.Segments.Count > 0 && g.Shows(item.Segments[0].Line))
            DrawLabelPill(dl, th, item, _items.Count - 1 - index, candidate, g, v);
    }

    private void DrawSegments(DrawList dl, Theme th, Item item, in PaneGrid g, float a, long now)
    {
        foreach (var seg in item.Segments)
        {
            if (!g.Shows(seg.Line)) continue;
            float py = g.Oy + (seg.Line - g.ViewTop) * g.CellH;
            var r = new RectF(g.Ox + seg.StartCol * g.CellW - 1, py, (seg.EndCol - seg.StartCol + 1) * g.CellW + 2, g.CellH);
            item.Hit.Add(r);
            float flash = item == _chosen ? _chosenFlash.Value(now) : 0;
            dl.RoundRect(r, th.S(3), th.Accent, (0.20f + 0.35f * flash) * a);
        }
    }

    private static void DrawCells(DrawList dl, Theme th, Item item, in PaneGrid g, float a)
    {
        foreach (var c in item.Cells)
        {
            if (!g.Shows(c.Line)) continue;
            var glyph = dl.Atlas.Get(c.Rune, c.Style);
            if (glyph.Empty) continue;
            float gx = g.Ox + c.Col * g.CellW + glyph.Left, gy = g.Oy + (c.Line - g.ViewTop) * g.CellH + dl.Fonts.Baseline - glyph.Top;
            dl.Quad(gx, gy, gx + glyph.Width, gy + glyph.Height, in glyph.Uv, th.Foreground, a);
        }
    }

    /// <summary>The label pill, popping in with a slight stagger from the bottom up.</summary>
    private void DrawLabelPill(DrawList dl, Theme th, Item item, int order, bool candidate, in PaneGrid g, float v)
    {
        float t = Math.Clamp((g.Since - Math.Min(order, 24) * StaggerMs) / PopMs, 0, 1);
        float pop = Ease.OutBack(t);
        if (t <= 0) return;

        var first = item.Segments[0];
        float labelPx = g.LabelPx;
        var lm = g.LabelMetrics;
        float tw = dl.Measure(item.Label, labelPx, FontStyle.Bold);
        float bh = MathF.Round(MathF.Min(g.CellH, lm.Height + th.S(6)));
        float bw = MathF.Round(MathF.Max(bh, tw + th.S(10)));
        float cy = g.Oy + (first.Line - g.ViewTop) * g.CellH + g.CellH / 2f;
        float bx = g.Ox + first.StartCol * g.CellW - th.S(2);
        float s = 0.6f + 0.4f * pop;
        var badge = new RectF(MathF.Round(bx), MathF.Round(cy - bh * s / 2), MathF.Round(bw * s), MathF.Round(bh * s));
        float ba = (candidate ? 1 : 0.3f) * v * MathF.Min(1, t * 2.5f);
        dl.Shadow(badge.Offset(0, th.S(2)), th.S(5), th.S(6), 0.55f * ba);
        dl.Panel(badge, th.S(5), th.PaneSelectBg, ba, Colors.Mix(th.PaneSelectBg, th.Accent, 0.55f), ba);
        float baseline = MathF.Round(badge.Y + (badge.H - lm.Height) / 2 + lm.Ascender);
        float tx = badge.X + (badge.W - tw * s) / 2;
        // Already-typed letters fade so the remaining ones stand out.
        int typed = candidate ? _typed.Length : 0;
        for (int k = 0; k < item.Label.Length; k++)
        {
            tx += dl.Glyph(item.Label[k], tx, baseline, labelPx * s, th.PaneSelectFg, ba * (k < typed ? 0.45f : 1), FontStyle.Bold);
        }
    }

    /// <summary>A floating hint bar at the bottom centre of the pane.</summary>
    private void DrawHelp(DrawList dl, Theme th, RectF pr, float v)
    {
        float px = th.UiPx * 0.86f;
        var parts = HelpParts;
        string title = "Quick Select";
        if (Purpose != QuickSelectPurpose.Copy)
        {
            parts = _openArmed ? VimOpenParts : VimHelpParts;
            title = _openArmed ? "Open" : "Hints";
        }
        string count = _countText;
        float titleW = dl.Measure(title, px, FontStyle.Bold), countW = dl.Measure(count, px * 0.92f);
        float keyPx = th.UiPx * 0.74f;
        var km = dl.MetricsFor(keyPx);
        float keyH = MathF.Round(km.Height + th.S(6)), keyPad = th.S(6), gap = th.S(14);

        float w = th.S(14) * 2 + px * 1.7f + titleW + th.S(10) + countW;
        foreach (var (k, l) in parts) w += gap + MathF.Max(keyH, dl.Measure(k, keyPx) + keyPad * 2) + th.S(6) + dl.Measure(l, px * 0.92f);
        w = MathF.Round(w);
        float h = MathF.Round(px * 2.3f);
        if (w > pr.W - th.S(16)) return; // narrow pane: the labels speak for themselves

        float slide = (1 - Ease.OutCubic(v)) * th.S(10);
        var r = new RectF(MathF.Round(pr.X + (pr.W - w) / 2), MathF.Round(pr.Bottom - h - th.S(14) + slide), w, h);
        Widgets.FloatingPanel(dl, th, r, h / 2, v);
        float b = Widgets.Baseline(dl, px, r.Y, h);
        float x = r.X + th.S(14);
        dl.Glyph(ShellIcons.Zap, x, b, px * 1.05f, th.Accent, v);
        x = r.X + th.S(14) + px * 1.7f;
        x += dl.Text(title, x, b, px, th.PanelFg, v, FontStyle.Bold) + th.S(10);
        x += dl.Text(count, x, Widgets.Baseline(dl, px * 0.92f, r.Y, h), px * 0.92f, th.Muted, v);
        foreach (var (k, l) in parts)
        {
            x += gap;
            float kw = MathF.Max(keyH, dl.Measure(k, keyPx) + keyPad * 2);
            var kr = new RectF(x, MathF.Round(r.Y + (h - keyH) / 2), kw, keyH);
            dl.RoundRect(kr.Offset(0, th.S(1)), th.S(4), 0x000000, 0.25f * v);
            dl.Panel(kr, th.S(4), Colors.Mix(th.PanelBg, th.PanelFg, 0.10f), v, th.Border, v);
            dl.Text(k, x + (kw - dl.Measure(k, keyPx)) / 2, kr.Y + (keyH - km.Height) / 2 + km.Ascender, keyPx, th.PanelFg, v);
            x += kw + th.S(6);
            x += dl.Text(l, x, Widgets.Baseline(dl, px * 0.92f, r.Y, h), px * 0.92f, th.Muted, v);
        }
    }
}
