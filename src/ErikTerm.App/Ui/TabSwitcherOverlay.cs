using ErikTerm.App.Integrations;
using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Ui;

/// <summary>
/// The Ctrl+Tab list: every tab of the window, most recently used first, with its color, number, full title, repo badge,
/// agent status, unread marker and progress. Opened by a hold (<see cref="TabSwitcherMode.Hold"/>: the popup appears only
/// after the quick-tap delay, the caller switches on release) or sticky (ShowTabSwitcher, the bar's "⌄ N more": anchored
/// under the button, stays until Enter, a click or Esc). The state lives in <see cref="TabSwitcherModel"/>; this class
/// draws it, turns input into its verbs, and is only called while open, so a closed list costs nothing.
/// </summary>
internal sealed class TabSwitcherOverlay
{
    /// <summary>What one list row shows. The caller fills these in fresh; the model only knows ids, titles and search text.</summary>
    public sealed class Row
    {
        public int Id;
        public string Number = "";
        public string Title = "";
        public string SearchExtra = ""; // the repository and branch: searchable, never highlighted
        public uint? Color;
        public RepoBadgeModel? Badge;
        public AgentState Agent;
        public bool Active, Activity, Unread;
        public ProgressReport Progress;
        public bool Container;          // a pane's shell runs in a container: the cube before the title
    }

    public enum KeyResult { None, Handled, Accept, Cancel }

    private readonly TabSwitcherModel _model = new();
    private readonly Dictionary<int, Row> _rows = [];
    private Tween _visible = new(0), _highlight = new(0), _scroll = new(0), _height = new(0);
    private bool _revealed;
    private RectF? _anchor;
    private float _animMs = 160;

    // Last layout, for the mouse.
    private RectF _panel, _list;
    private float _rowH;
    private int _hoverRow = -1;

    public bool AnimateProgress;

    public bool IsOpen => _model.IsOpen;
    public TabSwitcherMode Mode => _model.Mode;
    public bool IsVisible(long now) => _model.IsOpen || _visible.Value(now) > 0.001f;
    /// <summary>The next moment the popup has to appear (a hold, before the quick-tap delay is over).</summary>
    public int WakeMs(long now) => _model.IsOpen && !_revealed && _model.Mode == TabSwitcherMode.Hold ? Math.Max(1, _model.PopupWakeMs(now)) : int.MaxValue;
    public bool IsAnimating(long now) =>
        _visible.IsAnimating(now) || _highlight.IsAnimating(now) || _scroll.IsAnimating(now) || _height.IsAnimating(now);

    /// <summary>Opens the list; <paramref name="anchor"/>: under this rectangle (the bar's button), else centred like the palette.</summary>
    public void Open(List<Row> rows, int selected, TabSwitcherMode mode, RectF? anchor, Theme th, long now)
    {
        _animMs = th.AnimationMs;
        _anchor = anchor;
        _hoverRow = -1;
        _rows.Clear();
        var entries = new List<TabSwitcherEntry>(rows.Count);
        foreach (var row in rows)
        {
            _rows[row.Id] = row;
            entries.Add(new TabSwitcherEntry(row.Id, row.Title, row.SearchExtra));
        }
        _model.Open(entries, selected, mode, now);
        _revealed = false;
        _visible.Snap(0);
        _scroll.Snap(0);
        _height.Snap(0);
        _highlight.Snap(_model.SelectedIndex);
        if (mode == TabSwitcherMode.Sticky) Reveal(now);
    }

    /// <summary>Closes at once: a fading list would have nothing left to show, and a tab switch wants no ghost of it.</summary>
    public void Close()
    {
        _model.Close();
        _visible.Snap(0);
        _rows.Clear();
    }

    /// <summary>The selected tab's id and closes; null when nothing is selected.</summary>
    public int? Accept()
    {
        int? id = _model.Accept();
        _visible.Snap(0);
        _rows.Clear();
        return id;
    }

    private void Reveal(long now)
    {
        _revealed = true;
        _visible.Set(1, now, _animMs);
    }

    /// <summary>Hold mode: the popup appears once the delay is over.</summary>
    public bool Tick(long now)
    {
        if (!_model.IsOpen || _revealed || !_model.PopupVisible(now)) return false;
        Reveal(now);
        return true;
    }

    /// <summary>The tabs as they are now (any order): keeps titles, states and the selection in step; closes when none are left.</summary>
    public void Sync(List<Row> rows)
    {
        if (!_model.IsOpen) return;
        var entries = new List<TabSwitcherEntry>(rows.Count);
        _rows.Clear();
        foreach (var row in rows)
        {
            _rows[row.Id] = row;
            entries.Add(new TabSwitcherEntry(row.Id, row.Title, row.SearchExtra));
        }
        _model.Sync(entries);
    }

    // ---- Input ---------------------------------------------------------------------------------

    public void HandleText(string text, long now)
    {
        if (!_model.IsOpen) return;
        _model.Type(text);
        Selected(now);
    }

    /// <summary>A key while the list is open. Printable keys are not handled here: their text comes separately.</summary>
    public KeyResult HandleKey(uint key, ushort mod, long now)
    {
        if (!_model.IsOpen) return KeyResult.None;
        switch (key)
        {
            case Sdl.K_ESCAPE: return KeyResult.Cancel;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER: return KeyResult.Accept;
            case Sdl.K_TAB: Step((mod & Sdl.KMOD_SHIFT) != 0 ? -1 : 1, now); return KeyResult.Handled;
            case Sdl.K_UP: Step(-1, now); return KeyResult.Handled;
            case Sdl.K_DOWN: Step(1, now); return KeyResult.Handled;
            case Sdl.K_PAGEUP: Page(-1, now); return KeyResult.Handled;
            case Sdl.K_PAGEDOWN: Page(1, now); return KeyResult.Handled;
            case Sdl.K_HOME: _model.Select(0); Selected(now); return KeyResult.Handled;
            case Sdl.K_END: _model.Select(_model.Matches.Count - 1); Selected(now); return KeyResult.Handled;
            case Sdl.K_BACKSPACE:
                _model.Backspace();
                Selected(now);
                return KeyResult.Handled;
        }
        return KeyResult.None;
    }

    private int PageSize => Math.Max(1, _rowsFit - 1);

    /// <summary>A step to the next or previous entry (wraps).</summary>
    public void Step(int delta, long now)
    {
        _model.Move(delta);
        Selected(now);
    }

    private void Page(int dir, long now)
    {
        _model.Move(dir * PageSize, clamp: true);
        Selected(now);
    }

    /// <summary>Filter text typed from a key press that produces no text event (Ctrl held). The text event, if any, is dropped by the caller.</summary>
    public void TypeFromKey(string text, long now)
    {
        _model.Type(text);
        Selected(now);
    }

    /// <summary>The wheel, one line at a time.</summary>
    public void HandleWheel(int lines, long now)
    {
        if (lines == 0 || !_model.IsOpen) return;
        _model.Move(-Math.Sign(lines));
        Selected(now);
    }

    private void Selected(long now) => _highlight.Set(_model.SelectedIndex, now, _animMs * 0.6f);

    public void HandleMouseMove(float x, float y, long now)
    {
        if (!_model.PopupVisible(now)) return;
        int row = RowAt(x, y);
        if (row == _hoverRow) return;
        _hoverRow = row;
        if (row >= 0) { _model.Select(row); Selected(now); }
    }

    /// <summary>A click: a row accepts, outside the panel cancels.</summary>
    public KeyResult HandleMouseDown(float x, float y, long now)
    {
        if (!_model.PopupVisible(now)) return KeyResult.None;
        if (!_panel.Contains(x, y)) return KeyResult.Cancel;
        int row = RowAt(x, y);
        if (row < 0) return KeyResult.Handled;
        _model.Select(row);
        return KeyResult.Accept;
    }

    private int RowAt(float x, float y)
    {
        if (!_list.Contains(x, y) || _rowH <= 0) return -1;
        int row = (int)((y - _list.Y) / _rowH + _scroll.Target);
        return row >= 0 && row < _model.Matches.Count ? row : -1;
    }

    // ---- Drawing -------------------------------------------------------------------------------

    private int _rowsFit = 12;
    private const int MaxVisibleRows = 18;

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;

        var matches = _model.Matches;
        float px = th.UiPx;
        float rowH = MathF.Round(px * 2.15f);
        float headerH = MathF.Round(px * 2.8f);
        float footerH = MathF.Round(px * 2.3f);
        float padX = th.S(16);

        float width = MathF.Round(Widgets.FitWidth(NaturalWidth(dl, th, px), px * 30, window.W - th.S(48)));
        bool anchored = _anchor is not null;

        // How many rows fit under the top of the panel; the list scrolls past that.
        float top = anchored ? _anchor!.Value.Bottom + th.S(6) : MathF.Round(window.Y + window.H * 0.12f);
        float maxList = MathF.Max(rowH, window.Bottom - top - th.S(16) - headerH - footerH - th.S(10));
        _rowsFit = Math.Clamp((int)(maxList / rowH), 1, MaxVisibleRows);
        int rows = Math.Min(_rowsFit, Math.Max(1, matches.Count));
        float listH = matches.Count == 0 ? rowH * 2 : rows * rowH;
        if (_height.Target == 0) _height.Snap(listH);
#pragma warning disable S1244 // Target only ever holds a value set here, so exact comparison is the point.
        else if (_height.Target != listH) _height.Set(listH, now, _animMs * 0.8f);
#pragma warning restore S1244
        float animatedListH = MathF.Round(_height.Value(now));
        float panelH = headerH + animatedListH + th.S(10) + footerH;

        // Keep the selection in view (the selection moves, and the window may have shrunk).
        float scrollTarget = _scroll.Target;
        int sel = _model.SelectedIndex;
        if (sel < scrollTarget) scrollTarget = sel;
        else if (sel >= scrollTarget + rows) scrollTarget = sel - rows + 1;
        scrollTarget = Math.Clamp(scrollTarget, 0, Math.Max(0, matches.Count - rows));
#pragma warning disable S1244 // Target only ever holds a value set here, so exact comparison is the point.
        if (_scroll.Target != scrollTarget) _scroll.Set(scrollTarget, now, _animMs * 0.6f);
#pragma warning restore S1244

        float slide = (1 - Ease.OutCubic(v)) * th.S(-10);
        RectF panel;
        if (_anchor is { } a)
        {
            float x = MathF.Round(Math.Clamp(a.Right - width, window.X + th.S(8), window.Right - width - th.S(8)));
            panel = new RectF(x, MathF.Round(a.Bottom + th.S(6) + slide), width, panelH);
        }
        else
        {
            dl.Rect(window, 0x000000, 0.22f * v);
            panel = new RectF(MathF.Round(window.X + (window.W - width) / 2), MathF.Round(window.Y + window.H * 0.12f + slide), width, panelH);
        }

        Widgets.FloatingPanel(dl, th, panel, th.S(12), v);
        _panel = panel;
        _rowH = rowH;

        DrawHeader(dl, th, panel, headerH, padX, px, v);
        dl.Rect(new RectF(panel.X + th.S(1), panel.Y + headerH - 1, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);

        var list = new RectF(panel.X, panel.Y + headerH + th.S(5), panel.W, animatedListH);
        _list = list;
        dl.PushClip(list);
        if (matches.Count == 0)
        {
            string msg = "No matching tabs";
            float b = Widgets.Baseline(dl, px, list.Y, rowH * 2);
            dl.Text(msg, list.X + (list.W - dl.Measure(msg, px)) / 2, b, px, th.Muted, v);
        }
        else DrawRows(dl, th, list, rowH, padX, px, matches, now, v);
        dl.PopClip();
        if (matches.Count > rows) DrawScrollThumb(dl, th, list, matches.Count, rows, now, v);

        DrawFooter(dl, th, panel, footerH, padX, v);
    }

    private void DrawHeader(DrawList dl, Theme th, RectF panel, float headerH, float padX, float px, float v)
    {
        float baseline = Widgets.Baseline(dl, px, panel.Y, headerH);
        float x = panel.X + padX;
        x += dl.Glyph(Icons.Search, x, baseline, px * 1.02f, th.Accent, v) + th.S(10);
        if (_model.Query.Length == 0) dl.Text("Type to filter tabs…", x, baseline, px, th.Muted, v);
        else
        {
            float typed = dl.Text(_model.Query, x, baseline, px, th.PanelFg, v);
            var m = dl.MetricsFor(px);
            dl.Rect(new RectF(x + typed + th.S(1), baseline - m.Ascender + th.S(1), MathF.Max(1, th.S(2)), m.Height - th.S(2)), th.Accent, v);
        }
        string count = _model.Query.Length == 0 ? _model.EntryCount + " tabs" : $"{_model.Matches.Count} of {_model.EntryCount}";
        float countW = dl.Measure(count, px * 0.82f);
        dl.Text(count, panel.Right - padX - countW, baseline, px * 0.82f, th.Muted, v * 0.9f);
    }

    private void DrawRows(DrawList dl, Theme th, RectF list, float rowH, float padX, float px, IReadOnlyList<TabSwitcherMatch> matches, long now, float v)
    {
        float scroll = _scroll.Value(now);
        float hy = list.Y + (_highlight.Value(now) - scroll) * rowH;
        var highlight = new RectF(list.X + th.S(6), hy + th.S(2), list.W - th.S(12), rowH - th.S(4));
        dl.RoundRect(highlight, th.S(7), th.Accent, 0.16f * v);

        int first = Math.Max(0, (int)MathF.Floor(scroll));
        int last = Math.Min(matches.Count - 1, (int)MathF.Ceiling(scroll + list.H / rowH));
        for (int i = first; i <= last; i++)
        {
            var match = matches[i];
            if (!_rows.TryGetValue(match.Entry.Id, out var row)) continue;
            float top = list.Y + (i - scroll) * rowH;
            bool selected = i == _model.SelectedIndex;
            DrawRow(dl, th, row, match, new RectF(list.X, top, list.W, rowH), padX, px, selected, now, v);
        }
    }

    private void DrawRow(DrawList dl, Theme th, Row row, TabSwitcherMatch match, RectF rect, float padX, float px, bool selected, long now, float v)
    {
        float baseline = Widgets.Baseline(dl, px, rect.Y, rect.H);
        float cx = rect.X + padX;

        // Tab color: a small pill in the tab's color, the accent when none is set.
        float barW = MathF.Round(th.S(4)), barH = rect.H - th.S(14);
        dl.RoundRect(new RectF(cx, rect.Y + th.S(7), barW, barH), barW / 2, row.Color ?? th.Muted, row.Color is null ? 0.35f * v : v);
        cx += barW + th.S(10);

        uint numberRgb = row.Color ?? (selected ? th.Accent : th.Muted);
        float numberSlot = MathF.Round(px * 1.7f);
        dl.Text(row.Number, cx, baseline, px * 0.92f, numberRgb, v, FontStyle.Bold);
        cx += numberSlot;

        if (row.Container) cx += ContainerIcon.Draw(dl, th, cx, baseline, px, selected ? 1 : 0, 0, v) + th.S(7);
        float titleRoom = rect.Right - padX - RightColumnsWidth(dl, th, row, px) - cx;
        string title = dl.Ellipsize(row.Title, px, MathF.Max(th.S(40), titleRoom));
        float titleDim = selected ? 0 : 0.2f;
        uint titleRgb = row.Active ? th.PanelFg : Colors.Mix(th.PanelFg, th.Muted, titleDim);
        var highlight = ReferenceEquals(title, row.Title) && match.Positions.Count > 0 ? match.Positions : null;
        float titleW = dl.Text(title, cx, baseline, px, titleRgb, v, row.Active ? FontStyle.Bold : FontStyle.Regular, highlight, th.Accent);
        cx += titleW + th.S(12);

        // Right to left: unread marker, agent state, repo badge.
        float right = rect.Right - padX;
        right -= DrawMarker(dl, th, row, right, rect, px, baseline, v);
        right -= DrawAgent(dl, th, row, right, rect, px, baseline, now, v);
        if (row.Badge is { } badge) DrawBadge(dl, th, badge, row.Title, cx, right - th.S(10), baseline, px, now, v);

        if (row.Progress.IsActive)
        {
            float h = MathF.Max(1, MathF.Round(th.S(2)));
            var track = new RectF(rect.X + padX + barW + th.S(10), rect.Bottom - h - th.S(3), rect.W - 2 * padX - barW - th.S(10), h);
            ProgressBar.Draw(dl, th, track, row.Progress, v, now, AnimateProgress, row.Color);
        }
    }

    /// <summary>The repo badge, right-aligned at <paramref name="right"/>, and the repo's name left of it (from <paramref name="left"/>) when there is room.</summary>
    private static void DrawBadge(DrawList dl, Theme th, RepoBadgeModel badge, string title, float left, float right, float baseline, float px, long now, float v)
    {
        float maxW = right - left;
        if (maxW <= th.S(16)) return;
        float bw = RepoBadge.DrawCompact(dl, th, badge, right, baseline, px * 0.9f, v * 0.9f, maxW, now, animate: true);
        string repo = string.Equals(badge.RepoName, title, StringComparison.OrdinalIgnoreCase) ? "" : badge.RepoName; // the title says it already
        DrawRepoName(dl, th, repo, left, right - bw - (bw > 0 ? th.S(10) : 0), baseline, px, v);
    }

    /// <summary>The repository's name, muted, ending at <paramref name="right"/>: in what room the badge left.</summary>
    private static void DrawRepoName(DrawList dl, Theme th, string repo, float left, float right, float baseline, float px, float v)
    {
        if (repo.Length == 0 || right - left < th.S(28)) return;
        float small = px * 0.82f;
        string name = dl.Ellipsize(repo, small, right - left);
        dl.Text(name, right - dl.Measure(name, small), baseline, small, th.Muted, v * 0.9f);
    }

    private static float DrawMarker(DrawList dl, Theme th, Row row, float right, RectF rect, float px, float baseline, float v)
    {
        if (row.Unread)
        {
            float w = dl.Atlas.Get(Icons.BellDot, FontStyle.Regular, px).Advance;
            dl.Glyph(Icons.BellDot, right - w, baseline, px, th.Danger, v);
            return w + th.S(8);
        }
        if (row.Activity && !row.Active)
        {
            float d = MathF.Round(th.S(6));
            dl.RoundRect(new RectF(right - d, rect.Y + (rect.H - d) / 2, d, d), d / 2, th.Accent, v);
            return d + th.S(10);
        }
        return 0;
    }

    private static float DrawAgent(DrawList dl, Theme th, Row row, float right, RectF rect, float px, float baseline, long now, float v)
    {
        if (row.Agent == AgentState.None) return 0;
        float small = px * 0.82f;
        string word = AgentStateText.Word(row.Agent);
        float w = dl.Measure(word, small);
        dl.Text(word, right - w, baseline, small, row.Agent == AgentState.Waiting ? th.Danger : th.Muted, v);
        float d = MathF.Round(th.S(7));
        AgentBadge.Dot(dl, th, row.Agent, right - w - th.S(9), rect.Y + rect.H / 2, d, v, now);
        return w + th.S(9) + d + th.S(8);
    }

    private void DrawScrollThumb(DrawList dl, Theme th, RectF list, int count, int rows, long now, float v)
    {
        float trackH = list.H;
        float thumbH = MathF.Max(th.S(14), trackH * rows / count);
        float pos = _scroll.Value(now) / Math.Max(1, count - rows);
        float y = list.Y + (trackH - thumbH) * Math.Clamp(pos, 0, 1);
        float w = MathF.Max(2, MathF.Round(th.S(3)));
        dl.RoundRect(new RectF(list.Right - w - th.S(3), y, w, thumbH), w / 2, th.Muted, 0.5f * v);
    }

    private void DrawFooter(DrawList dl, Theme th, RectF panel, float footerH, float padX, float v)
    {
        float y = panel.Bottom - footerH;
        dl.Rect(new RectF(panel.X + th.S(1), y, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
        float baseline = Widgets.Baseline(dl, th.UiPx * 0.82f, y, footerH);
        float x = panel.X + padX;
        if (_model.Mode == TabSwitcherMode.Hold)
        {
            x = Hint(dl, th, x, y, footerH, baseline, TabKeys, "move", v);
            x = Hint(dl, th, x, y, footerH, baseline, ReleaseKeys, "release to switch", v);
        }
        else
        {
            x = Hint(dl, th, x, y, footerH, baseline, NavKeys, "move", v);
            x = Hint(dl, th, x, y, footerH, baseline, EnterKeys, "switch", v);
        }
        Hint(dl, th, x, y, footerH, baseline, CancelKeys, "cancel", v);
    }

    private static readonly string[] TabKeys = ["Tab"], ReleaseKeys = ["Ctrl"], NavKeys = ["↑", "↓"], EnterKeys = ["↵"], CancelKeys = ["esc"];

    private static float Hint(DrawList dl, Theme th, float x, float top, float h, float baseline, string[] keys, string label, float a)
    {
        float keysW = KeysWidth(dl, th, keys);
        Widgets.Keycaps(dl, th, keys, x + keysW, top + h / 2, a);
        x += keysW + th.S(6);
        x += dl.Text(label, x, baseline, th.UiPx * 0.82f, th.Muted, a);
        return x + th.S(16);
    }

    private static float KeysWidth(DrawList dl, Theme th, string[] keys)
    {
        float px = th.UiPx * 0.78f;
        float h = MathF.Round(dl.MetricsFor(px).Height + th.S(6)), pad = th.S(5), total = 0;
        foreach (var k in keys) total += MathF.Max(h, dl.Measure(k, px) + 2 * pad) + th.S(3);
        return total - th.S(3);
    }

    /// <summary>What the panel needs so every title shows in full, up to what the window can hold.</summary>
    private float NaturalWidth(DrawList dl, Theme th, float px)
    {
        float widest = 0;
        foreach (var row in _rows.Values)
        {
            float w = dl.Measure(row.Title, px) + RightColumnsWidth(dl, th, row, px) + (row.Container ? px * 1.4f + th.S(7) : 0);
            if (row.Badge is { } badge)
            {
                w += RepoBadge.CompactWidth(dl, th, badge, px * 0.9f) + th.S(12);
                if (badge.RepoName.Length > 0) w += dl.Measure(badge.RepoName, px * 0.82f) + th.S(10);
            }
            widest = MathF.Max(widest, w);
        }
        float pad = th.S(16);
        float fixedLeft = MathF.Round(th.S(4)) + th.S(10) + MathF.Round(px * 1.7f) + th.S(12);
        return widest + 2 * pad + fixedLeft + th.S(14);
    }

    /// <summary>The widths of the columns to the right of the title: unread marker and agent state.</summary>
    private static float RightColumnsWidth(DrawList dl, Theme th, Row row, float px)
    {
        float w = 0;
        if (row.Unread) w += dl.Atlas.Get(Icons.BellDot, FontStyle.Regular, px).Advance + th.S(8);
        else if (row.Activity && !row.Active) w += MathF.Round(th.S(6)) + th.S(10);
        if (row.Agent != AgentState.None)
            w += dl.Measure(AgentStateText.Word(row.Agent), px * 0.82f) + th.S(9) + MathF.Round(th.S(7)) + th.S(8);
        return w;
    }
}
