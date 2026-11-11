using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.App.Ui;

/// <summary>
/// The vim mode cheat sheet (g? or :help): a centred card like the About screen — header with an accent wash, the
/// keymap (<see cref="VimHelp.Sections"/>) in up to three balanced columns of key chips and descriptions, scrollable
/// with the wheel, j/k and the page keys. Esc, q or Enter closes it; so does a click outside.
/// </summary>
internal sealed class VimHelpOverlay
{
    private bool _open;
    private long _openedAt;
    private Tween _visible = new(0);
    private Tween _scroll = new(0);
    private float _animMs = 160;
    private float _maxScroll, _rowH = 24;
    private RectF _panel;

    // Column layout, cached per width.
    private float _layoutW = -1, _layoutPx = -1;
    private int _columns;
    private readonly List<int>[] _sectionsIn = [[], [], []];
    private readonly float[] _keyW = new float[3];
    private string[][] _fitted = [];              // descriptions, ellipsized to their column
    private float _colW;

    private static readonly string[] Titles = VimHelp.Sections.Select(s => s.Title.ToUpperInvariant()).ToArray();

    public bool IsOpen => _open;
    public string EnterKey = "Ctrl+Shift+X";

    public bool IsAnimating(long now) => _visible.IsAnimating(now) || _scroll.IsAnimating(now) || (_open && now - _openedAt < _animMs * 3);

    public void Open(Theme th, long now)
    {
        _open = true;
        _openedAt = now;
        _animMs = th.AnimationMs;
        _visible.Snap(0);
        _visible.Set(1, now, _animMs * 1.5f);
        _scroll.Snap(0);
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.9f);
    }

    public bool HandleKey(uint key, ushort mod, long now)
    {
        if (!_open) return false;
        switch (key)
        {
            case Sdl.K_ESCAPE or Sdl.K_RETURN or Sdl.K_KP_ENTER or 'q':
            case '?' or '/' when (mod & Sdl.KMOD_SHIFT) != 0:
                Close(now);
                break;
            case 'j' or Sdl.K_DOWN: ScrollBy(_rowH * 2, now); break;
            case 'k' or Sdl.K_UP: ScrollBy(-_rowH * 2, now); break;
            case 'd' or 'f' or Sdl.K_PAGEDOWN or Sdl.K_SPACE: ScrollBy(_rowH * 10, now); break;
            case 'u' or 'b' or Sdl.K_PAGEUP: ScrollBy(-_rowH * 10, now); break;
            case 'g' or Sdl.K_HOME: ScrollTo((mod & Sdl.KMOD_SHIFT) != 0 ? _maxScroll : 0, now); break;
            case Sdl.K_END: ScrollTo(_maxScroll, now); break;
        }
        return true;
    }

    public bool HandleMouseDown(float x, float y, long now)
    {
        if (!_open) return false;
        if (!_panel.Contains(x, y)) Close(now);
        return true;
    }

    public bool HandleWheel(int lines, long now)
    {
        if (!_open) return false;
        ScrollBy(-lines * _rowH * 3, now);
        return true;
    }

    private void ScrollBy(float d, long now) => ScrollTo(_scroll.Target + d, now);
    private void ScrollTo(float y, long now) => _scroll.Set(Math.Clamp(MathF.Round(y), 0, _maxScroll), now, _animMs * 0.9f);

    private void LayoutColumns(DrawList dl, Theme th, float innerW, float px, float colGap)
    {
        _layoutW = innerW;
        _layoutPx = px;
        float keyPx = px * 0.86f, textPx = px * 0.86f;
        var (widestKey, widestText) = WidestEntry(dl, keyPx, textPx);
        // As many columns as fit a key chip plus most of a description (up to three).
        float want = widestKey + th.S(18) + MathF.Min(widestText, px * 19);
        _columns = Math.Clamp((int)((innerW + colGap) / (want + colGap)), 1, 3);
        _colW = (innerW - colGap * (_columns - 1)) / _columns;

        AssignSections();
        _fitted = new string[VimHelp.Sections.Count][];
        for (int c = 0; c < _columns; c++) FitColumn(dl, th, c, keyPx, textPx);
    }

    private static (float Key, float Text) WidestEntry(DrawList dl, float keyPx, float textPx)
    {
        float widestKey = 0, widestText = 0;
        foreach (var sec in VimHelp.Sections)
            foreach (var e in sec.Entries)
            {
                widestKey = MathF.Max(widestKey, dl.Measure(e.Keys, keyPx, FontStyle.Bold));
                widestText = MathF.Max(widestText, dl.Measure(e.Text, textPx));
            }
        return (widestKey, widestText);
    }

    /// <summary>Greedy: each section into the shortest column so far (in rows), keeping the order within a column.</summary>
    private void AssignSections()
    {
        foreach (var l in _sectionsIn) l.Clear();
        var heights = new float[_columns];
        for (int i = 0; i < VimHelp.Sections.Count; i++)
        {
            int best = 0;
            for (int c = 1; c < _columns; c++) if (heights[c] < heights[best] - 0.5f) best = c;
            _sectionsIn[best].Add(i);
            heights[best] += VimHelp.Sections[i].Entries.Length + 2.2f;
        }
    }

    /// <summary>The column's key chip width, and its descriptions ellipsized to what's left.</summary>
    private void FitColumn(DrawList dl, Theme th, int c, float keyPx, float textPx)
    {
        float kw = 0;
        foreach (int i in _sectionsIn[c])
            foreach (var e in VimHelp.Sections[i].Entries) kw = MathF.Max(kw, dl.Measure(e.Keys, keyPx, FontStyle.Bold));
        _keyW[c] = kw + th.S(18);
        float room = _colW - _keyW[c];
        foreach (int i in _sectionsIn[c])
            _fitted[i] = VimHelp.Sections[i].Entries.Select(e => room > th.S(30) ? dl.Ellipsize(e.Text, textPx, room) : "").ToArray();
    }

    /// <summary>The panel's geometry for this frame.</summary>
    private readonly record struct HelpFrame(RectF Panel, RectF Shown, float Oy, float Radius, float HeaderH, float FooterH, float InnerPad, float ContentAlpha);

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;
        float e = Ease.OutCubic(v);
        float px = th.UiPx;
        dl.Rect(window, 0x000000, 0.36f * v);

        float margin = th.S(window.W < th.S(560) ? 10 : 24);
        float panelW = MathF.Round(MathF.Min(px * 72, window.W - 2 * margin));
        float headerH = MathF.Round(px * 4.6f), footerH = MathF.Round(px * 2.6f);
        float innerPad = th.S(26);
        float innerW = panelW - innerPad * 2;
        float colGap = th.S(28);
#pragma warning disable S1244 // Cache keys: the stored values are exactly the last ones, and any change means lay out again.
        if (innerW != _layoutW || px != _layoutPx) LayoutColumns(dl, th, innerW, px, colGap);
#pragma warning restore S1244

        _rowH = MathF.Round(px * 1.7f);
        float titleH = MathF.Round(px * 2.2f);
        float contentH = ContentHeight(titleH, th.S(10));
        float bodyPad = th.S(14);
        float panelH = MathF.Round(MathF.Min(headerH + footerH + contentH + bodyPad * 2, window.H - 2 * margin));
        var panel = new RectF(MathF.Round(window.X + (window.W - panelW) / 2), MathF.Round(window.Y + (window.H - panelH) * 0.46f), panelW, panelH);
        _panel = panel;

        float scale = 0.96f + 0.04f * e, rise = (1 - e) * th.S(18);
        var shown = new RectF(
            MathF.Round(panel.X + panel.W * (1 - scale) / 2), MathF.Round(panel.Y + panel.H * (1 - scale) / 2 + rise),
            MathF.Round(panel.W * scale), MathF.Round(panel.H * scale));
        float radius = th.S(16);
        Widgets.FloatingPanel(dl, th, shown, radius, v);
        float ca = Ease.OutCubic(Math.Clamp((v - 0.15f) / 0.85f, 0, 1));
        float oy = panel.Y + MathF.Round(rise);
        var f = new HelpFrame(panel, shown, oy, radius, headerH, footerH, innerPad, ca);
        dl.PushClip(shown);

        DrawHeader(dl, th, f);

        // Body.
        float bodyTop = oy + headerH;
        float bodyH = MathF.Max(0, panelH - headerH - footerH);
        var view = new RectF(panel.X + th.S(1), bodyTop, panel.W - th.S(2), bodyH);
        _maxScroll = MathF.Max(0, MathF.Round(contentH + bodyPad * 2 - bodyH));
        if (_scroll.Target > _maxScroll) _scroll.Snap(_maxScroll);
        float scroll = _scroll.Value(now);
        dl.PushClip(view);
        for (int c = 0; c < _columns; c++)
            DrawColumn(dl, th, c, panel.X + innerPad + c * (_colW + colGap), bodyTop + bodyPad - scroll, titleH, view, ca, now);
        dl.PopClip();
        if (_maxScroll > 0)
        {
            float trackH = bodyH - th.S(16);
            float thumbH = MathF.Max(th.S(28), trackH * bodyH / (contentH + bodyPad * 2));
            float thumbY = view.Y + th.S(8) + (trackH - thumbH) * (scroll / _maxScroll);
            dl.RoundRect(new RectF(view.Right - th.S(8), thumbY, th.S(4), thumbH), th.S(2), th.Muted, 0.45f * ca);
        }

        DrawFooter(dl, th, f);
        dl.PopClip();
    }

    /// <summary>The tallest column.</summary>
    private float ContentHeight(float titleH, float sectionGap)
    {
        float contentH = 0;
        for (int c = 0; c < _columns; c++)
        {
            float h = 0;
            foreach (int i in _sectionsIn[c]) h += titleH + VimHelp.Sections[i].Entries.Length * _rowH + sectionGap;
            contentH = MathF.Max(contentH, h);
        }
        return contentH;
    }

    /// <summary>Header: accent wash, icon tile, title and how to get here.</summary>
    private void DrawHeader(DrawList dl, Theme th, in HelpFrame f)
    {
        float px = th.UiPx, oy = f.Oy, headerH = f.HeaderH, ca = f.ContentAlpha;
        var panel = f.Panel;
        dl.PushClip(new RectF(f.Shown.X, f.Shown.Y, f.Shown.W, headerH + (oy - panel.Y)));
        dl.RoundRect(f.Shown.Inset(1), f.Radius - 1, th.Accent, 0.06f * ca);
        dl.PopClip();
        float tile = MathF.Round(px * 2.5f);
        var tileR = new RectF(panel.X + f.InnerPad, MathF.Round(oy + (headerH - tile) / 2), tile, tile);
        dl.Panel(tileR, th.S(9), Colors.Mix(th.PanelBg, th.Accent, 0.22f), ca, Colors.Mix(th.PanelBg, th.Accent, 0.6f), ca);
        var tm = dl.MetricsFor(px * 1.25f);
        float gw = dl.Atlas.Get(Icons.Keyboard, FontStyle.Regular, px * 1.25f).Advance;
        dl.Glyph(Icons.Keyboard, MathF.Round(tileR.X + (tile - gw) / 2), MathF.Round(tileR.Y + (tile - tm.Height) / 2 + tm.Ascender), px * 1.25f, th.Accent, ca);
        float tx = tileR.Right + th.S(14);
        float titlePx = px * 1.3f;
        dl.Text("Vim mode", tx, MathF.Round(oy + headerH * 0.47f), titlePx, th.PanelFg, ca, FontStyle.Bold);
        dl.Text(EnterKey + " enters it in the focused pane — the keys below work until i, a, q or Esc",
            tx, MathF.Round(oy + headerH * 0.47f + px * 1.45f), px * 0.84f, th.Muted, ca);
        dl.Rect(new RectF(panel.X + th.S(1), oy + headerH, panel.W - th.S(2), MathF.Max(1, MathF.Round(th.S(1)))), th.Border, ca);
    }

    private void DrawColumn(DrawList dl, Theme th, int c, float x, float y, float titleH, RectF view, float ca, long now)
    {
        float px = th.UiPx, headPx = px * 0.78f;
        foreach (int i in _sectionsIn[c])
        {
            float stagger = _open ? Math.Clamp((now - _openedAt - (i * 22f)) / (_animMs * 2), 0, 1) : 1;
            float a = ca * Ease.OutCubic(stagger);
            float lift = (1 - Ease.OutCubic(stagger)) * th.S(8);
            float hb = MathF.Round(y + titleH * 0.68f + lift);
            dl.Text(Titles[i], x, hb, headPx, th.Accent, a, FontStyle.Bold);
            y += titleH;
            y = DrawEntries(dl, th, i, c, x, y, lift, view, a);
            y += th.S(10);
        }
    }

    /// <summary>A section's rows that are in view; returns the y below the section.</summary>
    private float DrawEntries(DrawList dl, Theme th, int section, int c, float x, float y, float lift, RectF view, float a)
    {
        float keyPx = th.UiPx * 0.86f, textPx = th.UiPx * 0.86f;
        var entries = VimHelp.Sections[section].Entries;
        var texts = _fitted[section];
        for (int k = 0; k < entries.Length; k++)
        {
            var entry = entries[k];
            if (y + _rowH >= view.Y && y <= view.Bottom)
            {
                float b = Widgets.Baseline(dl, keyPx, y + lift, _rowH);
                var chip = new RectF(x - th.S(6), MathF.Round(y + lift + th.S(3)), dl.Measure(entry.Keys, keyPx, FontStyle.Bold) + th.S(12), _rowH - th.S(6));
                dl.RoundRect(chip, th.S(5), Colors.Mix(th.PanelBg, th.PanelFg, 0.08f), a);
                dl.Text(entry.Keys, x, b, keyPx, th.PanelFg, a, FontStyle.Bold);
                dl.Text(texts[k], x + _keyW[c], Widgets.Baseline(dl, textPx, y + lift, _rowH), textPx, th.Muted, a);
            }
            y += _rowH;
        }
        return y;
    }

    private static void DrawFooter(DrawList dl, Theme th, in HelpFrame f)
    {
        float px = th.UiPx, ca = f.ContentAlpha;
        var panel = f.Panel;
        float footTop = MathF.Round(f.Oy + panel.H - f.FooterH);
        dl.PushClip(new RectF(f.Shown.X, footTop, f.Shown.W, f.Shown.Bottom - footTop));
        dl.RoundRect(f.Shown.Inset(1), f.Radius - 1, 0x000000, 0.12f * ca);
        dl.PopClip();
        dl.Rect(new RectF(panel.X + th.S(1), footTop, panel.W - th.S(2), MathF.Max(1, MathF.Round(th.S(1)))), th.Border, ca);
        float fb = Widgets.Baseline(dl, px * 0.84f, footTop, f.FooterH);
        dl.Text("Esc, q or Enter closes · j / k scroll · the palette and Ctrl+Shift shortcuts keep working in vim mode",
            panel.X + f.InnerPad, fb, px * 0.84f, th.Muted, ca);
    }
}
