using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>Right-click menu: icons, shortcut hints, separators, disabled items, keyboard + mouse navigation.</summary>
internal sealed class ContextMenu
{
    public sealed record Item(string Label, int Icon, string? Shortcut, Action? Run, bool Enabled = true)
    {
        public static readonly Item Separator = new("", 0, null, null, false);
        public bool IsSeparator => ReferenceEquals(this, Separator);
    }

    private List<Item> _items = [];
    private bool _open;
    private Tween _visible = new(0);
    private Tween _highlightY = new(0);
    private float _anchorX, _anchorY;
    private int _hover = -1;
    private float _animMs = 160;
    private RectF _panel;
    private readonly List<RectF> _rows = [];

    public bool IsOpen => _open;
    public bool IsVisible(long now) => _open || _visible.Value(now) > 0.001f;
    public bool IsAnimating(long now) => _visible.IsAnimating(now) || _highlightY.IsAnimating(now);

    public void Open(List<Item> items, float x, float y, Theme th, long now)
    {
        _items = items;
        _anchorX = x;
        _anchorY = y;
        _hover = -1;
        _open = true;
        _animMs = th.AnimationMs;
        _visible.Snap(0);
        _visible.Set(1, now, _animMs);
        _rows.Clear();
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.6f);
    }

    private void Activate(int index, long now)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        if (item.IsSeparator || !item.Enabled) return;
        Close(now);
        item.Run?.Invoke();
    }

    public bool HandleKey(uint key, long now)
    {
        if (!_open) return false;
        switch (key)
        {
            case Sdl.K_ESCAPE: Close(now); break;
            case Sdl.K_UP: Step(-1, now); break;
            case Sdl.K_DOWN or Sdl.K_TAB: Step(1, now); break;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER or Sdl.K_SPACE: Activate(_hover, now); break;
        }
        return true;
    }

    private void Step(int dir, long now)
    {
        if (_items.Count == 0) return;
        int i = _hover;
        for (int n = 0; n < _items.Count; n++)
        {
            int start = i;
            if (i < 0) start = dir > 0 ? -1 : 0;
            i = (start + dir + _items.Count) % _items.Count;
            if (!_items[i].IsSeparator && _items[i].Enabled) break;
        }
        SetHover(i, now);
    }

    private void SetHover(int index, long now)
    {
        if (index == _hover) return;
        bool first = _hover < 0;
        _hover = index;
        if (index >= 0 && index < _rows.Count)
        {
            if (first) _highlightY.Snap(_rows[index].Y); else _highlightY.Set(_rows[index].Y, now, _animMs * 0.5f);
        }
    }

    public bool HandleMouseMove(float x, float y, long now)
    {
        if (!_open) return false;
        int hit = HitTest(x, y);
        if (hit >= 0 && !_items[hit].IsSeparator && _items[hit].Enabled) SetHover(hit, now);
        else if (!_panel.Contains(x, y)) _hover = -1;
        return true;
    }

    public bool HandleMouseDown(float x, float y, long now)
    {
        if (!_open) return false;
        if (!_panel.Contains(x, y)) { Close(now); return false; } // let the click through (e.g. to focus a pane)
        return true;
    }

    public bool HandleMouseUp(float x, float y, long now)
    {
        if (!_open) return false;
        int hit = HitTest(x, y);
        if (hit >= 0) Activate(hit, now);
        return true;
    }

    private int HitTest(float x, float y)
    {
        for (int i = 0; i < _rows.Count; i++) if (_rows[i].Contains(x, y)) return i;
        return -1;
    }

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f || _items.Count == 0) return;

        float px = th.UiPx * 0.95f;
        float rowH = MathF.Round(px * 2.0f), sepH = th.S(9), pad = th.S(6);
        float iconW = IconColumn(px);

        float width = MenuWidth(dl, th, px, pad, iconW);
        float height = pad * 2;
        foreach (var item in _items) height += item.IsSeparator ? sepH : rowH;

        // Open toward the space available; keep fully on screen.
        float x = _anchorX, y = _anchorY;
        if (x + width > window.Right - th.S(8)) x = MathF.Max(window.X + th.S(8), _anchorX - width);
        if (y + height > window.Bottom - th.S(8)) y = MathF.Max(window.Y + th.S(8), _anchorY - height);
        float drop = (1 - Ease.OutCubic(v)) * th.S(-6);
        var panel = new RectF(MathF.Round(x), MathF.Round(y + drop), width, MathF.Round(height));
        _panel = panel;

        float radius = th.S(10);
        Widgets.FloatingPanel(dl, th, panel, radius, v);

        PlaceRows(panel, pad, rowH, sepH);
        if (_hover >= 0 && _hover < _rows.Count) DrawHoverHighlight(dl, th, rowH, v, now);

        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].IsSeparator) DrawSeparator(dl, th, _rows[i], v);
            else DrawItem(dl, th, _items[i], _rows[i], i == _hover, px, v);
        }
    }

    private float MenuWidth(DrawList dl, Theme th, float px, float pad, float iconW)
    {
        float labelW = 0, shortcutW = 0;
        foreach (var item in _items)
        {
            if (item.IsSeparator) continue;
            labelW = MathF.Max(labelW, dl.Measure(item.Label, px));
            if (item.Shortcut is not null) shortcutW = MathF.Max(shortcutW, dl.Measure(item.Shortcut, px * 0.86f));
        }
        return MathF.Round(MathF.Max(th.S(220), pad * 2 + th.S(10) + iconW + labelW + (shortcutW > 0 ? th.S(28) + shortcutW : 0) + th.S(12)));
    }

    private void PlaceRows(RectF panel, float pad, float rowH, float sepH)
    {
        bool layout = _rows.Count != _items.Count;
        if (layout) _rows.Clear();
        float cy = panel.Y + pad;
        for (int i = 0; i < _items.Count; i++)
        {
            float h = _items[i].IsSeparator ? sepH : rowH;
            var r = new RectF(panel.X + pad, cy, panel.W - 2 * pad, h);
            if (layout) _rows.Add(r); else _rows[i] = r;
            cy += h;
        }
    }

    private void DrawHoverHighlight(DrawList dl, Theme th, float rowH, float v, long now)
    {
        var hr = _rows[_hover];
        float hy = _highlightY.Value(now);
        if (MathF.Abs(hy - hr.Y) > rowH * 3) { _highlightY.Snap(hr.Y); hy = hr.Y; }
        dl.RoundRect(hr with { Y = hy }, th.S(6), th.Accent, 0.20f * v);
    }

    /// <summary>The width of the icon column before the labels.</summary>
    private static float IconColumn(float px) => px * 1.9f;

    private static void DrawSeparator(DrawList dl, Theme th, RectF r, float v) =>
        dl.Rect(new RectF(r.X + th.S(6), MathF.Round(r.Y + r.H / 2), r.W - th.S(12), MathF.Max(1, th.S(1))), th.Border, v);

    private static void DrawItem(DrawList dl, Theme th, Item item, RectF r, bool hot, float px, float v)
    {
        float a = v * (item.Enabled ? 1 : 0.4f);
        float baseline = Widgets.Baseline(dl, px, r.Y, r.H);
        float tx = r.X + th.S(10);
        if (item.Icon != 0) dl.Glyph(item.Icon, tx, baseline, px * 1.05f, hot ? th.Accent : th.Muted, a);
        tx += IconColumn(px);
        dl.Text(item.Label, tx, baseline, px, th.PanelFg, a);
        if (item.Shortcut is not null)
        {
            float sw = dl.Measure(item.Shortcut, px * 0.86f);
            dl.Text(item.Shortcut, r.Right - th.S(10) - sw, baseline, px * 0.86f, th.Muted, a);
        }
    }
}
