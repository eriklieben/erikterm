using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.App.Search;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

internal enum PaneSearchKey { NotHandled, Handled, Close, Activate }

/// <summary>
/// The "search all panes" list: the field, the results grouped by pane (each group a header row, then its matches with
/// the match marked) and a status line that says when the list isn't everything. Input and results are the
/// <see cref="WindowPaneSearch{TPane}"/>'s; this holds the selection and draws.
/// </summary>
internal sealed class PaneSearchOverlay<TPane> where TPane : class
{
    private readonly record struct ListRow(PaneSearchGroup<TPane> Group, PaneSearchHit? Hit);

    private readonly List<ListRow> _rows = [];
    private PaneSearchResults<TPane> _results = PaneSearchResults<TPane>.Empty;
    private string _query = "";
    private bool _caseSensitive, _regex, _changed, _selectFirst, _open;
    private int _selected = -1, _first;
    private long _caretResetAt;
    private float _animMs = 160;
    private Tween _visible = new(0);

    // Last layout, for hit-testing.
    private RectF _panel, _list, _caseChip, _regexChip;
    private float _rowH;

    public bool IsOpen => _open;
    public bool IsAnimating(long now) => _open || _visible.IsAnimating(now); // the caret blinks

    public PaneSearchQuery Query => new(_query, _caseSensitive, _regex);

    /// <summary>The selected match and its pane's group, or null.</summary>
    public (PaneSearchGroup<TPane> Group, PaneSearchHit Hit)? SelectedHit =>
        _selected >= 0 && _selected < _rows.Count && _rows[_selected].Hit is { } hit ? (_rows[_selected].Group, hit) : null;

    public void Open(Theme th, long now)
    {
        _animMs = th.AnimationMs;
        _open = true;
        _visible.Set(1, now, _animMs);
        _caretResetAt = now;
        _changed = false;
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.75f);
    }

    public void SetQuery(string text) { _query = text; _changed = true; }

    /// <summary>True once after the query or its options changed: time for a new scan.</summary>
    public bool ConsumeQueryChange()
    {
        bool changed = _changed;
        _changed = false;
        return changed;
    }

    public void SelectFirstOnNextResults() => _selectFirst = true;

    public void SetResults(PaneSearchResults<TPane> results)
    {
        var keep = _selectFirst ? null : SelectedHit?.Hit;
        _selectFirst = false;
        _results = results;
        _rows.Clear();
        foreach (var group in results.Groups)
        {
            _rows.Add(new ListRow(group, null));
            foreach (var hit in group.Hits) _rows.Add(new ListRow(group, hit));
        }
        int found = keep is null ? -1 : _rows.FindIndex(r => ReferenceEquals(r.Hit, keep));
        _selected = found >= 0 ? found : NearestHit(0, +1);
    }

    /// <summary>The first hit row at or after <paramref name="from"/> in the direction of <paramref name="step"/>, or -1.</summary>
    private int NearestHit(int from, int step)
    {
        for (int i = from; i >= 0 && i < _rows.Count; i += step)
            if (_rows[i].Hit is not null) return i;
        return -1;
    }

    // ---- Input ------------------------------------------------------------------------------

    public void HandleText(string text, long now)
    {
        _query += text.Replace("\n", " ").Replace("\r", "");
        _caretResetAt = now;
        _changed = true;
    }

    public void Paste(string text, long now) => HandleText(text, now);

    public PaneSearchKey HandleKey(uint key, ushort mod, long now)
    {
        bool ctrl = (mod & Sdl.KMOD_CTRL) != 0, shift = (mod & Sdl.KMOD_SHIFT) != 0, alt = (mod & Sdl.KMOD_LALT) != 0;
        _caretResetAt = now;
        switch (key)
        {
            case Sdl.K_ESCAPE: return PaneSearchKey.Close;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER: return SelectedHit is null ? PaneSearchKey.Handled : PaneSearchKey.Activate;
            case Sdl.K_UP: Move(-1); return PaneSearchKey.Handled;
            case Sdl.K_DOWN: Move(+1); return PaneSearchKey.Handled;
            case Sdl.K_PAGEUP: Move(-10); return PaneSearchKey.Handled;
            case Sdl.K_PAGEDOWN: Move(+10); return PaneSearchKey.Handled;
            case Sdl.K_BACKSPACE: DeleteBack(ctrl); return PaneSearchKey.Handled;
        }
        if (alt && !ctrl && key == 'c') { _caseSensitive = !_caseSensitive; _changed = true; return PaneSearchKey.Handled; }
        if (alt && !ctrl && key == 'r') { _regex = !_regex; _changed = true; return PaneSearchKey.Handled; }
        if (ctrl && !shift && key == 'u') { _query = ""; _changed = true; return PaneSearchKey.Handled; }
        return PaneSearchKey.NotHandled;
    }

    private void DeleteBack(bool word)
    {
        if (_query.Length == 0) return;
        if (word)
        {
            string trimmed = _query.TrimEnd();
            int cut = trimmed.LastIndexOf(' ');
            _query = cut < 0 ? "" : trimmed[..(cut + 1)];
        }
        else _query = _query[..^(char.IsLowSurrogate(_query[^1]) && _query.Length > 1 ? 2 : 1)];
        _changed = true;
    }

    private void Move(int delta)
    {
        if (_rows.Count == 0) return;
        int step = delta > 0 ? 1 : -1, at = Math.Max(_selected, 0), target = at;
        for (int moved = 0, i = at + step; moved < Math.Abs(delta) && i >= 0 && i < _rows.Count; i += step)
        {
            if (_rows[i].Hit is null) continue;
            target = i;
            moved++;
        }
        _selected = target;
    }

    public void HandleMouseMove(float x, float y)
    {
        if (RowAt(x, y) is { } row && _rows[row].Hit is not null) _selected = row;
    }

    public PaneSearchKey HandleMouseDown(float x, float y, long now)
    {
        if (RowAt(x, y) is { } row)
        {
            if (_rows[row].Hit is null) return PaneSearchKey.Handled;
            _selected = row;
            return PaneSearchKey.Activate;
        }
        if (!_panel.Contains(x, y)) return PaneSearchKey.Close;
        _caretResetAt = now;
        if (_caseChip.Contains(x, y)) { _caseSensitive = !_caseSensitive; _changed = true; }
        else if (_regexChip.Contains(x, y)) { _regex = !_regex; _changed = true; }
        return PaneSearchKey.Handled;
    }

    public void HandleWheel(int lines) => Move(-lines);

    private int? RowAt(float x, float y)
    {
        if (_rowH <= 0 || !_list.Contains(x, y)) return null;
        int row = _first + (int)((y - _list.Y) / _rowH);
        return row >= 0 && row < _rows.Count ? row : null;
    }

    // ---- Drawing ----------------------------------------------------------------------------

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;
        dl.Rect(window, 0x000000, 0.28f * v);

        float px = th.UiPx;
        float rowH = MathF.Round(px * 2.25f);
        float fieldH = MathF.Round(px * 3.0f), statusH = MathF.Round(px * 2.0f), footerH = MathF.Round(px * 2.3f);
        float width = MathF.Round(Widgets.FitWidth(px * 56, th.S(420), window.W - th.S(48)));
        int shown = Math.Clamp(_rows.Count, 1, Math.Max(1, (int)((window.H * 0.72f - fieldH - statusH - footerH) / rowH)));
        float slide = (1 - Ease.OutCubic(v)) * th.S(-14);
        var panel = new RectF(MathF.Round(window.X + (window.W - width) / 2), MathF.Round(window.Y + window.H * 0.1f + slide), width,
            fieldH + statusH + shown * rowH + th.S(8) + footerH);
        _panel = panel;
        Widgets.FloatingPanel(dl, th, panel, th.S(14), v);

        DrawField(dl, th, panel, fieldH, v, now);
        DrawStatus(dl, th, panel, fieldH, statusH, v);

        var list = new RectF(panel.X, panel.Y + fieldH + statusH + th.S(4), panel.W, shown * rowH);
        _list = list;
        _rowH = rowH;
        dl.PushClip(list);
        if (_rows.Count == 0) DrawEmpty(dl, th, list, rowH, v);
        else DrawRows(dl, th, list, shown, rowH, v);
        dl.PopClip();

        float fy = panel.Bottom - footerH;
        dl.Rect(new RectF(panel.X + th.S(1), fy, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
        float fb = Widgets.Baseline(dl, px * 0.82f, fy, footerH);
        float fx = panel.X + th.S(18);
        fx = Hint(dl, th, fx, fy + footerH / 2, fb, PaneSearchOverlayKeys.Arrows, "navigate", v);
        fx = Hint(dl, th, fx, fy + footerH / 2, fb, PaneSearchOverlayKeys.Enter, "go to match", v);
        fx = Hint(dl, th, fx, fy + footerH / 2, fb, PaneSearchOverlayKeys.AltC, "case", v);
        fx = Hint(dl, th, fx, fy + footerH / 2, fb, PaneSearchOverlayKeys.AltR, "regex", v);
        Hint(dl, th, fx, fy + footerH / 2, fb, PaneSearchOverlayKeys.Esc, "close", v);
    }

    private void DrawField(DrawList dl, Theme th, RectF panel, float h, float v, long now)
    {
        float px = th.UiPx * 1.05f, chipPx = th.UiPx * 0.82f, pad = th.S(16);
        float baseline = Widgets.Baseline(dl, px, panel.Y, h);
        float x = panel.X + pad;
        x += dl.Glyph(Icons.Search, x, baseline, px * 1.05f, th.Accent, v) + th.S(10);

        _regexChip = Chip(dl, th, ".*", panel.Right - th.S(12), panel.Y, h, chipPx, _regex, v);
        _caseChip = Chip(dl, th, "Aa", _regexChip.X - th.S(4), panel.Y, h, chipPx, _caseSensitive, v);

        var field = new RectF(x, panel.Y, MathF.Max(0, _caseChip.X - th.S(12) - x), h);
        float typedW = dl.Measure(_query, px), shift = MathF.Max(0, typedW + th.S(4) - field.W);
        bool invalid = _results.Error is not null;
        dl.PushClip(field);
        if (_query.Length == 0) dl.Text("Search all panes", x, baseline, px, th.Muted, v);
        else dl.Text(_query, x - shift, baseline, px, invalid ? th.Danger : th.PanelFg, v);
        if (_open && (now - _caretResetAt) % 1060 < 600)
        {
            var m = dl.MetricsFor(px);
            dl.Rect(new RectF(x - shift + typedW + th.S(1), baseline - m.Ascender + th.S(1), MathF.Max(1, th.S(2)), m.Height - th.S(2)), th.Accent, v);
        }
        dl.PopClip();
        dl.Rect(new RectF(panel.X + th.S(1), panel.Y + h - 1, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
    }

    private void DrawStatus(DrawList dl, Theme th, RectF panel, float top, float h, float v)
    {
        string status = _query.Length == 0 ? "" : _results.StatusText();
        if (status.Length == 0) return;
        float px = th.UiPx * 0.82f;
        uint rgb = th.Muted;
        if (_results.Error is not null || _results.Incomplete) rgb = Colors.Mix(th.Danger, th.Muted, 0.35f);
        dl.Text(dl.Ellipsize(status, px, panel.W - th.S(36)), panel.X + th.S(18), Widgets.Baseline(dl, px, panel.Y + top, h), px, rgb, v);
    }

    private string EmptyMessage
    {
        get
        {
            if (_query.Length == 0) return "Type to search the scrollback of every pane";
            return _results.Done ? "Nothing found" : "Searching…";
        }
    }

    private void DrawEmpty(DrawList dl, Theme th, RectF list, float rowH, float v)
    {
        string message = EmptyMessage;
        float px = th.UiPx, mw = dl.Measure(message, px);
        dl.Text(message, list.X + (list.W - mw) / 2, Widgets.Baseline(dl, px, list.Y, rowH), px, th.Muted, v);
    }

    private void DrawRows(DrawList dl, Theme th, RectF list, int shown, float rowH, float v)
    {
        // Keep the row before the selection in view too, so its group header shows.
        if (_selected >= 0 && _selected < _first + 1) _first = Math.Max(0, _selected - 1);
        else if (_selected >= _first + shown) _first = _selected - shown + 1;
        _first = Math.Clamp(_first, 0, Math.Max(0, _rows.Count - shown));

        for (int i = _first; i < Math.Min(_rows.Count, _first + shown); i++)
        {
            var r = new RectF(list.X, list.Y + (i - _first) * rowH, list.W, rowH);
            if (_rows[i].Hit is { } hit) DrawHit(dl, th, r, hit, i == _selected, v);
            else DrawHeader(dl, th, r, _rows[i].Group, v);
        }
    }

    private static void DrawHeader(DrawList dl, Theme th, RectF r, PaneSearchGroup<TPane> group, float v)
    {
        float px = th.UiPx * 0.88f, baseline = Widgets.Baseline(dl, px, r.Y, r.H), pad = th.S(18);
        string count = group.Hits.Length.ToString() + (group.Capped ? "+" : "");
        float countW = dl.Measure(count, px);
        dl.Text(count, r.Right - pad - countW, baseline, px, th.Muted, v);
        float x = r.X + pad;
        float room = r.W - 2 * pad - countW - th.S(12);
        x += dl.Text(dl.Ellipsize(group.Target.Title, px, room * 0.6f), x, baseline, px, th.Accent, v, FontStyle.Bold);
        if (group.Target.Place.Length > 0)
            dl.Text(dl.Ellipsize("  ·  " + group.Target.Place, px, r.X + pad + room - x), x, baseline, px, th.Muted, v);
    }

    private static void DrawHit(DrawList dl, Theme th, RectF r, PaneSearchHit hit, bool selected, float v)
    {
        float px = th.UiPx * 0.92f, pad = th.S(30), baseline = Widgets.Baseline(dl, px, r.Y, r.H);
        if (selected) dl.RoundRect(new RectF(r.X + th.S(8), r.Y + th.S(1), r.W - th.S(16), r.H - th.S(2)), th.S(7), th.Accent, 0.14f * v);

        string before = hit.Preview[..hit.PreviewIndex], match = hit.Preview.Substring(hit.PreviewIndex, hit.PreviewLength);
        float room = r.W - pad - th.S(18), beforeW = dl.Measure(before, px), matchW = dl.Measure(match, px);
        // A match that would end past the edge pulls the line left, keeping the match itself readable.
        float shift = MathF.Max(0, beforeW + MathF.Min(matchW, room * 0.6f) - room);
        float x = r.X + pad - shift;
        var clip = new RectF(r.X + pad, r.Y, room, r.H);
        dl.PushClip(clip);
        var hl = new RectF(x + beforeW - th.S(1), r.Y + th.S(3), matchW + th.S(2), r.H - th.S(6));
        dl.RoundRect(hl, th.S(4), th.Accent, 0.32f * v);
        dl.Text(hit.Preview, x, baseline, px, selected ? th.PanelFg : Colors.Mix(th.PanelFg, th.Muted, 0.25f), v);
        dl.PopClip();
    }

    private static RectF Chip(DrawList dl, Theme th, string label, float right, float top, float h, float px, bool on, float v)
    {
        float tw = dl.Measure(label, px, FontStyle.Bold), ch = MathF.Round(h - th.S(18));
        float w = MathF.Round(MathF.Max(ch * 1.25f, tw + th.S(12)));
        var r = new RectF(right - w, MathF.Round(top + (h - ch) / 2), w, ch);
        if (on) dl.Panel(r, th.S(6), Colors.Mix(th.PanelBg, th.Accent, 0.28f), v, th.Accent, 0.8f * v);
        var m = dl.MetricsFor(px);
        dl.Text(label, r.X + (w - tw) / 2, MathF.Round(r.Y + (ch - m.Height) / 2 + m.Ascender), px, on ? th.Accent : th.Muted, v, FontStyle.Bold);
        return r;
    }

    private static float Hint(DrawList dl, Theme th, float x, float centerY, float baseline, string[] keys, string label, float a)
    {
        float kpx = th.UiPx * 0.78f;
        float h = MathF.Round(dl.MetricsFor(kpx).Height + th.S(6)), w = 0;
        foreach (var k in keys) w += MathF.Max(h, dl.Measure(k, kpx) + 2 * th.S(5)) + th.S(3);
        w -= th.S(3);
        Widgets.Keycaps(dl, th, keys, x + w, centerY, a);
        x += w + th.S(6);
        x += dl.Text(label, x, baseline, th.UiPx * 0.82f, th.Muted, a);
        return x + th.S(18);
    }
}

/// <summary>Key names of the footer hints; outside the generic overlay so there is one instance of each.</summary>
internal static class PaneSearchOverlayKeys
{
    public static readonly string[] Arrows = ["↑", "↓"], Enter = ["↵"], AltC = ["alt", "c"], AltR = ["alt", "r"], Esc = ["esc"];
}
