using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Ui;

/// <summary>One row of the worktree switcher or the cleanup list, with its texts prepared off the draw path.</summary>
internal sealed class WorktreeRow
{
    public required WorktreeEntry Entry { get; init; }
    public required string Title { get; init; }
    public string Branch = "";
    public string Detail = "";
    public string Status = "";
    public AgentState Agent;
    /// <summary>Switcher: the Aspire AppHosts running in this worktree.</summary>
    public IReadOnlyList<AspireDashboard> Dashboards = [];
    /// <summary>Cleanup: can be picked (landed, clean, nothing open in it).</summary>
    public bool Selectable = true;
    public bool Checked;
    public bool Warn;
}

/// <summary>
/// The worktree list: the switcher (palette Git: Worktrees…) and, in cleanup mode, the landed worktrees with why each
/// counts as landed. Rows arrive and update from the background; drawing never waits for git.
/// </summary>
internal sealed class WorktreesOverlay
{
    private const int MaxDashboardPills = 3;

    private readonly List<(RectF Rect, AspireDashboard Dashboard)> _dashboardPills = [];
    private List<WorktreeRow> _rows = [];
    private string _title = "", _subtitle = "";
    private int _selected;
    private bool _open, _cleanup;
    private Tween _visible = new(0), _highlight = new(0);
    private float _animMs = 160;
    private RectF _list, _panel;
    private float _rowH;

    public Action<WorktreeRow>? Activated, FinishRequested;
    public Action<AspireDashboard>? DashboardRequested;
    public Action? NewRequested, CleanUpRequested, RefreshRequested, RemoveRequested;

    public bool IsOpen => _open;
    public bool IsCleanup => _cleanup;
    public bool IsAnimating(long now) => _open || _visible.IsAnimating(now) || _highlight.IsAnimating(now);
    public IEnumerable<WorktreeRow> CheckedRows => _rows.Where(r => r.Selectable && r.Checked);

    public void Open(string title, string subtitle, bool cleanup, Theme th, long now)
    {
        _title = title;
        _subtitle = subtitle;
        _cleanup = cleanup;
        _animMs = th.AnimationMs;
        _open = true;
        _selected = 0;
        _rows = [];
        _highlight.Snap(0);
        _visible.Set(1, now, _animMs);
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.75f);
    }

    /// <summary>New rows, keeping the selection on the same worktree.</summary>
    public void SetRows(List<WorktreeRow> rows)
    {
        string? current = _selected < _rows.Count ? _rows[_selected].Entry.Path : null;
        // Keep the user's picks in cleanup mode.
        foreach (var row in rows)
            if (_rows.Find(r => r.Entry.Path == row.Entry.Path) is { } old && old.Selectable) row.Checked = old.Checked && row.Selectable;
        _rows = rows;
        int i = current is null ? -1 : rows.FindIndex(r => r.Entry.Path == current);
        _selected = Math.Clamp(i >= 0 ? i : _selected, 0, Math.Max(0, rows.Count - 1));
    }

    /// <summary>Key handling. Returns nothing: every key is consumed while open.</summary>
    public void HandleKey(uint key, long now)
    {
        switch (key)
        {
            case Sdl.K_ESCAPE: Close(now); break;
            case Sdl.K_UP or 'k': Select(_selected - 1, now); break;
            case Sdl.K_DOWN or 'j' or Sdl.K_TAB: Select(_selected + 1, now); break;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER:
                if (_cleanup) RemoveRequested?.Invoke();
                else if (_selected < _rows.Count) Activated?.Invoke(_rows[_selected]);
                break;
            case ' ' when _cleanup && _selected < _rows.Count && _rows[_selected].Selectable:
                _rows[_selected].Checked = !_rows[_selected].Checked;
                break;
            case 'n' when !_cleanup: NewRequested?.Invoke(); break;
            case 'f' when !_cleanup && _selected < _rows.Count: FinishRequested?.Invoke(_rows[_selected]); break;
            case 'c' when !_cleanup: CleanUpRequested?.Invoke(); break;
            case 'r': RefreshRequested?.Invoke(); break;
            case 'd' when !_cleanup && SelectedHasDashboard: DashboardRequested?.Invoke(_rows[_selected].Dashboards[0]); break;
        }
    }

    public void HandleMouseDown(float x, float y, long now)
    {
        if (!_panel.Contains(x, y)) { Close(now); return; }
        foreach (var (rect, dashboard) in _dashboardPills)
            if (rect.Contains(x, y)) { DashboardRequested?.Invoke(dashboard); return; }
        if (!_list.Contains(x, y) || _rowH <= 0) return;
        int i = (int)((y - _list.Y) / _rowH) + _firstShown;
        if (i < 0 || i >= _rows.Count) return;
        if (_cleanup) { Select(i, now); if (_rows[i].Selectable) _rows[i].Checked = !_rows[i].Checked; }
        else Activated?.Invoke(_rows[i]);
    }

    public void HandleMouseMove(float x, float y, long now)
    {
        if (!_list.Contains(x, y) || _rowH <= 0) return;
        int i = (int)((y - _list.Y) / _rowH) + _firstShown;
        if (i >= 0 && i < _rows.Count && i != _selected) Select(i, now);
    }

    private void Select(int i, long now)
    {
        if (_rows.Count == 0) return;
        _selected = (i % _rows.Count + _rows.Count) % _rows.Count;
        _highlight.Set(_selected, now, _animMs * 0.6f);
    }

    private int _firstShown;
    private bool SelectedHasDashboard => _selected < _rows.Count && _rows[_selected].Dashboards.Count > 0;

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;
        dl.Rect(window, 0x000000, 0.28f * v);

        float px = th.UiPx, small = px * 0.85f;
        float rowH = MathF.Round(px * 3.2f), headerH = MathF.Round(px * 3.0f), footerH = MathF.Round(px * 2.3f);
        float width = MathF.Round(Widgets.FitWidth(px * 52, th.S(440), window.W - th.S(48)));
        int shown = Math.Clamp(_rows.Count, 1, Math.Max(1, (int)((window.H * 0.7f - headerH - footerH) / rowH)));
        float slide = (1 - Ease.OutCubic(v)) * th.S(-14);
        var panel = new RectF(MathF.Round(window.X + (window.W - width) / 2), MathF.Round(window.Y + window.H * 0.12f + slide), width, headerH + shown * rowH + th.S(12) + footerH);
        _panel = panel;
        Widgets.FloatingPanel(dl, th, panel, th.S(14), v);

        float pad = th.S(18);
        float hb = Widgets.Baseline(dl, px * 1.1f, panel.Y, headerH);
        float x = panel.X + pad;
        x += dl.Glyph(RepoBadge.BranchIcon, x, hb, px * 1.1f, th.Accent, v) + th.S(12);
        dl.Text(_title, x, hb, px * 1.1f, th.PanelFg, v, FontStyle.Bold);
        if (_subtitle.Length > 0)
        {
            string sub = dl.Ellipsize(_subtitle, small, width * 0.5f);
            dl.Text(sub, panel.Right - pad - dl.Measure(sub, small), hb, small, th.Muted, v);
        }
        dl.Rect(new RectF(panel.X + th.S(1), panel.Y + headerH - 1, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);

        var list = new RectF(panel.X, panel.Y + headerH + th.S(6), panel.W, shown * rowH);
        _list = list;
        _rowH = rowH;
        _dashboardPills.Clear();
        dl.PushClip(list);
        if (_rows.Count == 0)
        {
            string msg = _cleanup ? "Nothing to clean up" : "Reading worktrees…";
            dl.Text(msg, list.X + (list.W - dl.Measure(msg, px)) / 2, Widgets.Baseline(dl, px, list.Y, rowH), px, th.Muted, v);
        }
        else
        {
            _firstShown = Math.Clamp(_selected - shown + 1, 0, Math.Max(0, _rows.Count - shown));
            float hy = list.Y + (_highlight.Value(now) - _firstShown) * rowH;
            dl.RoundRect(new RectF(list.X + th.S(8), hy + th.S(2), list.W - th.S(16), rowH - th.S(4)), th.S(8), th.Accent, 0.14f * v);
            for (int i = _firstShown; i < Math.Min(_rows.Count, _firstShown + shown); i++)
                DrawRow(dl, th, _rows[i], list, list.Y + (i - _firstShown) * rowH, rowH, pad, v, now);
        }
        dl.PopClip();

        float fy = panel.Bottom - footerH;
        dl.Rect(new RectF(panel.X + th.S(1), fy, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
        float fx = panel.X + pad, fb = Widgets.Baseline(dl, px * 0.82f, fy, footerH);
        if (_cleanup)
        {
            fx = Hint(dl, th, fx, fy, footerH, fb, ["Space"], "pick", v);
            fx = Hint(dl, th, fx, fy, footerH, fb, ["↵"], "remove…", v);
            Hint(dl, th, fx, fy, footerH, fb, ["esc"], "close", v);
        }
        else
        {
            fx = Hint(dl, th, fx, fy, footerH, fb, ["↵"], "open", v);
            fx = Hint(dl, th, fx, fy, footerH, fb, ["n"], "new agent", v);
            fx = Hint(dl, th, fx, fy, footerH, fb, ["f"], "finish", v);
            fx = Hint(dl, th, fx, fy, footerH, fb, ["c"], "clean up", v);
            fx = Hint(dl, th, fx, fy, footerH, fb, ["r"], "refresh", v);
            if (SelectedHasDashboard) fx = Hint(dl, th, fx, fy, footerH, fb, ["d"], "dashboard", v);
            Hint(dl, th, fx, fy, footerH, fb, ["esc"], "close", v);
        }
    }

    private void DrawRow(DrawList dl, Theme th, WorktreeRow row, RectF list, float top, float rowH, float pad, float v, long now)
    {
        float px = th.UiPx, small = px * 0.85f;
        float l1 = MathF.Round(top + rowH * 0.42f), l2 = MathF.Round(top + rowH * 0.80f);
        float x = list.X + pad + th.S(6), right = list.Right - pad;
        float a = row.Selectable ? v : v * 0.55f;
        if (_cleanup)
        {
            float box = MathF.Round(px * 0.95f);
            var r = new RectF(x, MathF.Round(top + (rowH - box) / 2), box, box);
            dl.Panel(r, th.S(3), Colors.Mix(th.PanelBg, th.PanelFg, 0.05f), a, row.Checked ? th.Accent : th.Border, a);
            if (row.Checked) dl.RoundRect(r.Inset(th.S(4)), th.S(2), th.Accent, a);
            x += box + th.S(12);
        }
        else if (row.Agent != AgentState.None)
        {
            AgentBadge.Dot(dl, th, row.Agent, x + th.S(4), top + rowH * 0.34f, MathF.Round(th.S(9)), v, now);
            x += th.S(18);
        }
        else
        {
            dl.Glyph(RepoBadge.BranchIcon, x, l1, small, row.Entry.IsMain ? th.Accent : th.Muted, v);
            x += th.S(18);
        }

        float statusW = dl.Measure(row.Status, small);
        dl.Text(row.Status, right - statusW, l1, small, row.Warn ? th.Warning : th.Muted, a);
        float textW = right - statusW - th.S(10) - x;
        float tw = dl.Text(dl.Ellipsize(row.Title, px, textW * 0.6f), x, l1, px, th.PanelFg, a, FontStyle.Bold);
        if (row.Branch.Length > 0 && row.Branch != row.Title)
            dl.Text(dl.Ellipsize(row.Branch, small, textW - tw - th.S(10)), x + tw + th.S(10), l1, small, th.Accent, a);
        float detailRight = DrawDashboardPills(dl, th, row, l2, right, a);
        dl.Text(dl.Ellipsize(row.Detail, small, detailRight - x), x, l2, small, th.Muted, a);
    }

    /// <summary>Right-aligned on the row's second line; returns where the text left of them has to stop.</summary>
    private float DrawDashboardPills(DrawList dl, Theme th, WorktreeRow row, float baseline, float right, float a)
    {
        if (row.Dashboards.Count == 0) return right;
        float px = th.UiPx * 0.78f, pad = th.S(8);
        var m = dl.MetricsFor(px);
        float h = MathF.Round(m.Height + th.S(3));
        foreach (var dashboard in row.Dashboards.Take(MaxDashboardPills))
        {
            float w = dl.Measure(dashboard.Label, px) + 2 * pad;
            var r = new RectF(right - w, MathF.Round(baseline - m.Ascender - th.S(1.5f)), w, h);
            dl.Panel(r, h / 2, Colors.Mix(th.PanelBg, th.Accent, 0.10f), a, Colors.Mix(th.PanelBg, th.Accent, 0.35f), a);
            dl.Text(dashboard.Label, r.X + pad, baseline, px, th.PanelFg, a);
            _dashboardPills.Add((r, dashboard));
            right = r.X - th.S(6);
        }
        return right - th.S(4);
    }

    internal static float Hint(DrawList dl, Theme th, float x, float top, float h, float baseline, string[] keys, string label, float a)
    {
        float keysWidth = 0, keyH = MathF.Round(dl.MetricsFor(th.UiPx * 0.78f).Height + th.S(6));
        foreach (var k in keys) keysWidth += MathF.Max(keyH, dl.Measure(k, th.UiPx * 0.78f) + 2 * th.S(5)) + th.S(3);
        keysWidth -= th.S(3);
        Widgets.Keycaps(dl, th, keys, x + keysWidth, top + h / 2, a);
        x += keysWidth + th.S(6);
        x += dl.Text(label, x, baseline, th.UiPx * 0.82f, th.Muted, a);
        return x + th.S(16);
    }
}
