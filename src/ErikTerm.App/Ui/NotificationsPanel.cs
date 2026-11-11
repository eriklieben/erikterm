using ErikTerm.App.Native;
using ErikTerm.App.Notifications;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Ui;

/// <summary>
/// The Notifications panel (palette: Notifications…, or the bar's "N unread" chip): the last
/// <see cref="NotificationCenter.Capacity"/> notifications of every window, unread first, newest first. Enter (or a
/// click) jumps to the pane, Delete dismisses one, Shift+Delete / "Clear all" empties the history. T (or the All /
/// This tab control) narrows it to the open tab; each row names its tab as it is now. Closing it marks what it listed
/// read. Texts are plain (cleaned by the center) and drawn as such.
/// </summary>
internal sealed class NotificationsPanel
{
    private sealed record Row(NotificationItem Item, string Title, string Where, string Body, string Age, bool Unread);

    private readonly List<NotificationItem> _scratch = [];
    private List<Row> _rows = [];
    private int _selected, _version = -1;
    private bool _open;
    private Tween _visible = new(0), _highlight = new(0);
    private float _animMs = 160;
    private RectF _list, _clearRect, _allRect, _tabRect;
    private float _rowH;
    private bool _clearHover, _thisTabOnly;
    private int _scopeHover; // 1 All, 2 This tab
    private NotificationScope? _tabScope;
    private long _refreshAt;
    private string _count = "";

    public NotificationCenter? Center;
    /// <summary>The window drawing the panel (its own panes aren't labelled with a window).</summary>
    public uint Viewer;
    public Action<NotificationItem>? Jump;
    /// <summary>The label of the tab a pane is in now; null when the pane is gone.</summary>
    public Func<int, string?>? TabLabel;

    public bool IsOpen => _open;
    public bool IsAnimating(long now) => _visible.IsAnimating(now) || _highlight.IsAnimating(now);

    public void Open(Theme th, long now)
    {
        _animMs = th.AnimationMs;
        _open = true;
        _thisTabOnly = false;
        _visible.Set(1, now, _animMs);
        _version = -1;
        Refresh(now);
        _selected = 0;
        _highlight.Snap(0);
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.75f);
        if (!_thisTabOnly) Center?.MarkAllRead(); // you've seen the list
        else foreach (var row in _rows) Center?.MarkRead(row.Item.Id); // and only this much of it
    }

    /// <summary>Panes moved or the open tab changed: the rows are placed anew on the next tick.</summary>
    public void PlacesChanged(NotificationScope? tabScope)
    {
        _tabScope = tabScope;
        _version = -1;
    }

    /// <summary>Hook from the window's tick while open: rebuilds the rows when the history changed (ages once a minute).</summary>
    public bool Tick(long now)
    {
        if (!_open || Center is null) return false;
        if (Center.Version == _version && now < _refreshAt) return false;
        Refresh(now);
        return true;
    }

    /// <summary>When the ages need rewriting (for the event loop's sleep).</summary>
    public int WakeMs(long now) => _open ? (int)Math.Clamp(_refreshAt - now, 1, int.MaxValue) : int.MaxValue;

    private void Refresh(long now)
    {
        if (Center is null) return;
        var current = _selected < _rows.Count ? _rows[_selected].Item : null;
        _version = Center.Version;
        Center.Sorted(_scratch);
        _rows = BuildRows(now, out long next);
        _refreshAt = now + next;
        int i = current is null ? -1 : _rows.FindIndex(r => ReferenceEquals(r.Item, current));
        _selected = Math.Clamp(i >= 0 ? i : _selected, 0, Math.Max(0, _rows.Count - 1));
        _highlight.Snap(_selected);
        UpdateCount();
    }

    /// <summary>The rows to list; <paramref name="next"/> is how long until one of their ages changes.</summary>
    private List<Row> BuildRows(long now, out long next)
    {
        var rows = new List<Row>(_scratch.Count);
        next = 60_000;
        foreach (var it in _scratch)
        {
            if (_thisTabOnly && _tabScope is { } scope && !scope.IsHere(it.PaneId)) continue;
            string title = it.Count > 1 ? $"{it.Title}  ×{it.Count}" : it.Title;
            rows.Add(new Row(it, title, Where(it), it.Body, NotificationText.Age(now - it.At), !it.Read));
            next = Math.Min(next, NotificationText.UntilAgeChanges(now - it.At));
        }
        return rows;
    }

    private void UpdateCount()
    {
        int unread = 0;
        foreach (var row in _rows) if (row.Unread) unread++;
        if (_rows.Count == 0) _count = "";
        else if (unread == 0) _count = $"{_rows.Count} read";
        else _count = unread == _rows.Count ? $"{unread} unread" : $"{unread} unread · {_rows.Count} in all";
    }

    /// <summary>The row's place: the tab it's in now, or what it was when posted if the pane has closed.</summary>
    private string Where(NotificationItem it) =>
        TabLabel?.Invoke(it.PaneId) is { } tab ? NotificationText.Join(tab, it.Place) : NotificationText.Join("Closed", it.Where(Viewer));

    private void ToggleScope(bool thisTabOnly, long now)
    {
        if (_thisTabOnly == thisTabOnly) return;
        _thisTabOnly = thisTabOnly;
        Refresh(now);
    }

    public void HandleKey(uint key, ushort mod, long now)
    {
        bool shift = (mod & Sdl.KMOD_SHIFT) != 0;
        switch (key)
        {
            case Sdl.K_ESCAPE: Close(now); break;
            case Sdl.K_UP or 'k': Select(_selected - 1, now); break;
            case Sdl.K_DOWN or 'j' or Sdl.K_TAB: Select(_selected + 1, now); break;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER: Activate(_selected, now); break;
            case Sdl.K_DELETE or Sdl.K_BACKSPACE or 'd' when shift: ClearAll(now); break;
            case Sdl.K_DELETE or Sdl.K_BACKSPACE or 'd': Dismiss(_selected, now); break;
            case 't': ToggleScope(!_thisTabOnly, now); break;
        }
    }

    public void HandleMouseDown(float x, float y, long now)
    {
        if (_clearRect.Contains(x, y) && _rows.Count > 0) { ClearAll(now); return; }
        if (_allRect.Contains(x, y)) { ToggleScope(false, now); return; }
        if (_tabRect.Contains(x, y)) { ToggleScope(true, now); return; }
        if (_list.Contains(x, y) && _rowH > 0)
        {
            int i = FirstShown + (int)((y - _list.Y) / _rowH);
            if (i >= 0 && i < _rows.Count) { Activate(i, now); return; }
        }
        if (!_panel.Contains(x, y)) Close(now);
    }

    public void HandleMouseMove(float x, float y, long now)
    {
        _clearHover = _clearRect.Contains(x, y);
        if (_allRect.Contains(x, y)) _scopeHover = 1;
        else _scopeHover = _tabRect.Contains(x, y) ? 2 : 0;
        if (!_list.Contains(x, y) || _rowH <= 0) return;
        int i = FirstShown + (int)((y - _list.Y) / _rowH);
        if (i >= 0 && i < _rows.Count && i != _selected) Select(i, now);
    }

    private void Select(int i, long now)
    {
        if (_rows.Count == 0) return;
        _selected = (i % _rows.Count + _rows.Count) % _rows.Count;
        _highlight.Set(_selected, now, _animMs * 0.6f);
    }

    private void Activate(int i, long now)
    {
        if (i < 0 || i >= _rows.Count) return;
        var item = _rows[i].Item;
        Center?.MarkRead(item.Id);
        Close(now);
        Jump?.Invoke(item);
    }

    private void Dismiss(int i, long now)
    {
        if (i < 0 || i >= _rows.Count || Center is null) return;
        Center.Remove(_rows[i].Item.Id);
        Refresh(now);
    }

    /// <summary>"Clear all": the whole history, or just the listed rows in the This tab view.</summary>
    private void ClearAll(long now)
    {
        if (!_thisTabOnly) Center?.Clear();
        else foreach (var row in _rows) Center?.Remove(row.Item.Id);
        Refresh(now);
    }

    private RectF _panel;
    private int _shown = 1;
    private int FirstShown => Math.Clamp(_selected - _shown + 1, 0, Math.Max(0, _rows.Count - _shown));

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;
        dl.Rect(window, 0x000000, 0.28f * v);

        float px = th.UiPx;
        float rowH = MathF.Round(px * 3.3f);
        float headerH = MathF.Round(px * 3.0f), footerH = MathF.Round(px * 2.3f);
        float width = MathF.Round(Widgets.FitWidth(px * 50, th.S(420), window.W - th.S(48)));
        _shown = Math.Clamp(_rows.Count, 1, Math.Max(1, (int)((window.H * 0.7f - headerH - footerH) / rowH)));
        float listH = _rows.Count == 0 ? rowH : _shown * rowH;
        float slide = (1 - Ease.OutCubic(v)) * th.S(-14);
        var panel = new RectF(MathF.Round(window.X + (window.W - width) / 2), MathF.Round(window.Y + window.H * 0.12f + slide), width,
            headerH + listH + th.S(12) + footerH);
        _panel = panel;
        Widgets.FloatingPanel(dl, th, panel, th.S(14), v);

        float pad = th.S(18);
        DrawHeader(dl, th, panel, headerH, pad, v);

        var list = new RectF(panel.X, panel.Y + headerH + th.S(6), panel.W, listH);
        _list = list;
        _rowH = rowH;
        dl.PushClip(list);
        if (_rows.Count == 0) DrawEmpty(dl, th, list, rowH, v);
        else DrawRows(dl, th, list, rowH, pad, v, now);
        dl.PopClip();

        DrawFooter(dl, th, panel, footerH, pad, v);
    }

    private void DrawHeader(DrawList dl, Theme th, RectF panel, float headerH, float pad, float v)
    {
        float px = th.UiPx;
        float hb = Widgets.Baseline(dl, px * 1.1f, panel.Y, headerH);
        float x = panel.X + pad;
        x += dl.Glyph(Icons.Bell, x, hb, px * 1.1f, th.Accent, v) + th.S(12);
        x += dl.Text("Notifications", x, hb, px * 1.1f, th.PanelFg, v, FontStyle.Bold) + th.S(12);
        if (_count.Length > 0) dl.Text(_count, x, hb, px * 0.85f, th.Muted, v);

        // "Clear all" on the right of the header, the All / This tab control before it.
        float sp = px * 0.85f;
        float ch = MathF.Round(dl.MetricsFor(sp).Height * 1.5f), cy = MathF.Round(panel.Y + (headerH - ch) / 2);
        float headRight = DrawClearButton(dl, th, panel.Right - pad, cy, ch, sp, v);
        DrawScopeControl(dl, th, x, headRight, cy, ch, v);
        dl.Rect(new RectF(panel.X + th.S(1), panel.Y + headerH - 1, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
    }

    /// <summary>Returns where the header's right side continues.</summary>
    private float DrawClearButton(DrawList dl, Theme th, float headRight, float cy, float ch, float sp, float v)
    {
        if (_rows.Count == 0)
        {
            _clearRect = default;
            return headRight;
        }
        string clear = _thisTabOnly ? "Clear tab" : "Clear all";
        float cw = MathF.Round(dl.Measure(clear, sp) + th.S(20));
        _clearRect = new RectF(headRight - cw, cy, cw, ch);
        dl.Panel(_clearRect, th.S(6), Colors.Mix(th.PanelBg, th.PanelFg, _clearHover ? 0.15f : 0.07f), v, th.Border, v);
        dl.Text(clear, _clearRect.X + th.S(10), Widgets.Baseline(dl, sp, _clearRect.Y, ch), sp, th.PanelFg, v);
        return _clearRect.X - th.S(10);
    }

    /// <summary>The All / This tab segments, right-aligned at <paramref name="headRight"/> when they fit after the count at <paramref name="x"/>.</summary>
    private void DrawScopeControl(DrawList dl, Theme th, float x, float headRight, float cy, float ch, float v)
    {
        float px = th.UiPx, sp = px * 0.85f;
        float tabW = MathF.Round(dl.Measure(ThisTab, sp) + th.S(20)), allW = MathF.Round(dl.Measure(All, sp) + th.S(20));
        if (headRight - tabW - allW <= x + dl.Measure(_count, px * 0.85f) + th.S(12))
        {
            _allRect = _tabRect = default; // a narrow window: T still toggles
            return;
        }
        _tabRect = new RectF(headRight - tabW, cy, tabW, ch);
        _allRect = new RectF(_tabRect.X - allW, cy, allW, ch);
        var whole = new RectF(_allRect.X, cy, allW + tabW, ch);
        dl.Panel(whole, th.S(6), Colors.Mix(th.PanelBg, th.PanelFg, 0.05f), v, th.Border, v);
        ScopeSegment(dl, th, _allRect, All, !_thisTabOnly, _scopeHover == 1, v);
        ScopeSegment(dl, th, _tabRect, ThisTab, _thisTabOnly, _scopeHover == 2, v);
    }

    private void DrawEmpty(DrawList dl, Theme th, RectF list, float rowH, float v)
    {
        float px = th.UiPx;
        string msg = _thisTabOnly ? "No notifications on this tab" : "No notifications";
        float mw = dl.Measure(msg, px);
        dl.Text(msg, list.X + (list.W - mw) / 2, Widgets.Baseline(dl, px, list.Y, rowH), px, th.Muted, v);
    }

    private void DrawRows(DrawList dl, Theme th, RectF list, float rowH, float pad, float v, long now)
    {
        int first = FirstShown;
        float hy = list.Y + (_highlight.Value(now) - first) * rowH;
        dl.RoundRect(new RectF(list.X + th.S(8), hy + th.S(2), list.W - th.S(16), rowH - th.S(4)), th.S(8), th.Accent, 0.14f * v);
        for (int i = first; i < Math.Min(_rows.Count, first + _shown); i++)
            DrawRow(dl, th, _rows[i], new RectF(list.X, list.Y + (i - first) * rowH, list.W, rowH), pad, v, now);
    }

    /// <summary>One notification in <paramref name="rect"/> (the list's width): its dot or bell, title and age, then where and what.</summary>
    private static void DrawRow(DrawList dl, Theme th, Row row, RectF rect, float pad, float v, long now)
    {
        float px = th.UiPx, top = rect.Y, rowH = rect.H;
        float l1 = MathF.Round(top + rowH * 0.40f), l2 = MathF.Round(top + rowH * 0.78f);
        float dx = rect.X + pad + th.S(6);
        float fade = row.Unread ? 1 : 0.6f;
        if (row.Item.AgentState != AgentState.None)
            AgentBadge.Dot(dl, th, row.Item.AgentState, new AgentBadge.DotSpot(dx, top + rowH * 0.32f, MathF.Round(th.S(9))), v * fade, now);
        else
            dl.Glyph(Icons.Bell, dx - px * 0.3f, l1, px * 0.9f, row.Unread ? th.Accent : th.Muted, v);
        if (row.Unread) dl.RoundRect(new RectF(rect.X + th.S(9), top + rowH * 0.32f - th.S(3), th.S(6), th.S(6)), th.S(3), th.Accent, v);
        float tx = dx + th.S(18);
        float ageW = dl.Measure(row.Age, px * 0.85f);
        float right = rect.Right - pad;
        dl.Text(row.Age, right - ageW, l1, px * 0.85f, th.Muted, v);
        string title = dl.Ellipsize(row.Title, px, right - ageW - th.S(16) - tx, FontStyle.Bold);
        dl.Text(title, tx, l1, px, row.Unread ? th.PanelFg : Colors.Mix(th.PanelFg, th.Muted, 0.45f), v,
            row.Unread ? FontStyle.Bold : FontStyle.Regular);
        DrawRowDetail(dl, th, row, tx, right, l2, v);
    }

    /// <summary>The second line: where it came from, then the body.</summary>
    private static void DrawRowDetail(DrawList dl, Theme th, Row row, float tx, float right, float l2, float v)
    {
        float px = th.UiPx;
        float sx = tx;
        if (row.Where.Length > 0)
        {
            string where = dl.Ellipsize(row.Where, px * 0.85f, (right - sx) * 0.45f);
            sx += dl.Text(where, sx, l2, px * 0.85f, th.Muted, v);
        }
        if (row.Body.Length > 0)
        {
            if (sx > tx) sx += dl.Text("  ·  ", sx, l2, px * 0.85f, th.Muted, v * 0.6f);
            dl.Text(dl.Ellipsize(row.Body, px * 0.85f, right - sx), sx, l2, px * 0.85f, Colors.Mix(th.PanelFg, th.Muted, 0.35f), v);
        }
    }

    private void DrawFooter(DrawList dl, Theme th, RectF panel, float footerH, float pad, float v)
    {
        float fy = panel.Bottom - footerH;
        dl.Rect(new RectF(panel.X + th.S(1), fy, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
        float fb = Widgets.Baseline(dl, th.UiPx * 0.82f, fy, footerH);
        var hints = new FooterHints(dl, th, panel.X + pad, fy + footerH / 2, fb, v, th.S(18));
        hints.Add(Arrows, "navigate");
        hints.Add(Enter, "jump to pane");
        hints.Add(Del, "dismiss");
        hints.Add(ToggleKey, _thisTabOnly ? "all tabs" : "this tab");
        hints.Add(Esc, "close");
    }

    private const string All = "All", ThisTab = "This tab";
    private static readonly string[] Arrows = ["↑", "↓"], Enter = ["↵"], Del = ["del"], Esc = ["esc"], ToggleKey = ["t"];

    /// <summary>One segment of the scope control: filled when <paramref name="on"/>, lighter on <paramref name="hover"/>.</summary>
    private static void ScopeSegment(DrawList dl, Theme th, RectF r, string label, bool on, bool hover, float a)
    {
        float px = th.UiPx * 0.85f;

        if (on) dl.RoundRect(r, th.S(6), Colors.Mix(th.PanelBg, th.Accent, 0.32f), a);
        else if (hover) dl.RoundRect(r, th.S(6), Colors.Mix(th.PanelBg, th.PanelFg, 0.12f), a);
        dl.Text(label, r.X + th.S(10), Widgets.Baseline(dl, px, r.Y, r.H), px, on ? th.PanelFg : th.Muted, a);
    }
}
