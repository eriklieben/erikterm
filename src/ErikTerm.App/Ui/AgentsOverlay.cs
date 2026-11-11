using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Ui;

/// <summary>One agent pane as the overview lists it (texts prepared by the caller).</summary>
internal sealed record AgentRow<TPane>(TPane Pane, int TabIndex, uint WindowId, int TabId, AgentState State, string Title, string Detail, string Where, string Age, AgentUsage? Usage, bool Focused, long Since,
    AgentPrompt? Prompt = null)
    where TPane : class;

/// <summary>
/// Ctrl+Shift+A: every pane running a coding agent, across tabs, most urgent first (waiting, done, working, idle):
/// state, agent, directory, last message, time in state, cost and context use. Enter (or a click) jumps there.
/// A permission prompt that can be read off the screen (<see cref="AgentPrompts"/>) offers its one-time answers on the
/// row: Y / N for the selected row or a click, with what it asks about shown under the list.
/// </summary>
internal sealed class AgentsOverlay<TPane> where TPane : class
{
    private List<AgentRow<TPane>> _rows = [];
    private int _selected;
    private bool _open;
    private Tween _visible = new(0), _highlight = new(0);
    private float _animMs = 160;
    private RectF _list;
    private float _rowH;
    private string _total = "";

    /// <summary>Rebuilds the rows (called on open and about twice a second while open).</summary>
    public Func<List<AgentRow<TPane>>>? Source;
    public Action<TPane>? Jump;
    /// <summary>The user picked one of a row's answers (<see cref="AgentRow{TPane}.Prompt"/>).</summary>
    public Action<AgentRow<TPane>, AgentAnswer>? Answer;

    private readonly List<(RectF Rect, int Row, AgentAnswer Answer)> _answerHits = [];
    private const int MaxBodyRows = 4;

    public bool IsOpen => _open;
    public bool IsAnimating(long now) => _open || _visible.IsAnimating(now) || _highlight.IsAnimating(now);

    public void Open(Theme th, long now)
    {
        _animMs = th.AnimationMs;
        _open = true;
        _visible.Set(1, now, _animMs);
        Refresh();
        _selected = 0;
        _highlight.Snap(0);
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.75f);
    }

    public void Refresh()
    {
        var current = _selected < _rows.Count ? _rows[_selected].Pane : null;
        _rows = Source?.Invoke() ?? [];
        int i = current is null ? -1 : _rows.FindIndex(r => r.Pane == current);
        _selected = Math.Clamp(i >= 0 ? i : _selected, 0, Math.Max(0, _rows.Count - 1));
        double total = 0;
        foreach (var r in _rows) total += r.Usage?.CostUsd ?? 0;
        _total = total > 0 ? $"{AgentUsage.Cost(total)} total" : "";
    }

    /// <param name="plain">No modifier is held: Ctrl+Y must not answer.</param>
    public void HandleKey(uint key, long now, bool plain = true)
    {
        switch (key)
        {
            case 'y' or 'n' when plain: AnswerSelected(key == 'y'); break;
            case Sdl.K_ESCAPE: Close(now); break;
            case Sdl.K_UP or 'k': Select(_selected - 1, now); break;
            case Sdl.K_DOWN or 'j' or Sdl.K_TAB: Select(_selected + 1, now); break;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER: Activate(_selected, now); break;
        }
    }

    private void AnswerSelected(bool yes)
    {
        if (_selected < _rows.Count && _rows[_selected] is { Prompt: { } prompt } row) Answer?.Invoke(row, yes ? prompt.Yes : prompt.No);
    }

    public void HandleMouseDown(float x, float y, long now)
    {
        foreach (var hit in _answerHits)
        {
            if (!hit.Rect.Contains(x, y) || hit.Row >= _rows.Count) continue;
            Answer?.Invoke(_rows[hit.Row], hit.Answer);
            return;
        }
        if (_list.Contains(x, y) && _rowH > 0)
        {
            int i = (int)((y - _list.Y) / _rowH);
            if (i >= 0 && i < _rows.Count) { Activate(i, now); return; }
        }
        Close(now);
    }

    public void HandleMouseMove(float x, float y, long now)
    {
        if (!_list.Contains(x, y) || _rowH <= 0) return;
        int i = (int)((y - _list.Y) / _rowH);
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
        var pane = _rows[i].Pane;
        Close(now);
        Jump?.Invoke(pane);
    }

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;
        dl.Rect(window, 0x000000, 0.28f * v);

        float px = th.UiPx;
        float rowH = MathF.Round(px * 3.3f);
        float headerH = MathF.Round(px * 3.0f), footerH = MathF.Round(px * 2.3f);
        float width = MathF.Round(Widgets.FitWidth(px * 48, th.S(420), window.W - th.S(48)));
        int shown = Math.Clamp(_rows.Count, 1, Math.Max(1, (int)((window.H * 0.7f - headerH - footerH) / rowH)));
        float listH = _rows.Count == 0 ? rowH : shown * rowH;
        float slide = (1 - Ease.OutCubic(v)) * th.S(-14);
        var selectedPrompt = _selected < _rows.Count ? _rows[_selected].Prompt : null;
        float bodyLineH = MathF.Round(px * 1.35f);
        float bodyH = selectedPrompt is null ? 0 : BodyRowsWithMore(selectedPrompt) * bodyLineH + th.S(10);
        var panel = new RectF(MathF.Round(window.X + (window.W - width) / 2), MathF.Round(window.Y + window.H * 0.12f + slide), width,
            headerH + listH + bodyH + th.S(12) + footerH);
        Widgets.FloatingPanel(dl, th, panel, th.S(14), v);

        float pad = th.S(18);
        float hb = Widgets.Baseline(dl, px * 1.1f, panel.Y, headerH);
        float x = panel.X + pad;
        x += dl.Glyph(Icons.Sparkle, x, hb, px * 1.1f, th.Accent, v) + th.S(12);
        dl.Text("Agents", x, hb, px * 1.1f, th.PanelFg, v, FontStyle.Bold);
        if (_total.Length > 0)
        {
            float tw = dl.Measure(_total, px * 0.85f);
            dl.Text(_total, panel.Right - pad - tw, hb, px * 0.85f, th.Muted, v);
        }
        dl.Rect(new RectF(panel.X + th.S(1), panel.Y + headerH - 1, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);

        var list = new RectF(panel.X, panel.Y + headerH + th.S(6), panel.W, listH);
        _list = list;
        _rowH = rowH;
        _answerHits.Clear();
        dl.PushClip(list);
        if (_rows.Count == 0)
        {
            const string msg = "No coding agents running";
            float mw = dl.Measure(msg, px);
            dl.Text(msg, list.X + (list.W - mw) / 2, Widgets.Baseline(dl, px, list.Y, rowH), px, th.Muted, v);
        }
        else
        {
            int first = Math.Clamp(_selected - shown + 1, 0, Math.Max(0, _rows.Count - shown));
            float hy = list.Y + (_highlight.Value(now) - first) * rowH;
            var hl = new RectF(list.X + th.S(8), hy + th.S(2), list.W - th.S(16), rowH - th.S(4));
            dl.RoundRect(hl, th.S(8), th.Accent, 0.14f * v);
            for (int i = first; i < Math.Min(_rows.Count, first + shown); i++)
                DrawRow(dl, th, i, list, list.Y + (i - first) * rowH, rowH, px, pad, v, now);
        }
        dl.PopClip();
        if (selectedPrompt is not null) DrawPromptBody(dl, th, selectedPrompt, new RectF(panel.X + pad, list.Bottom + th.S(6), panel.W - 2 * pad, bodyH), bodyLineH, v);

        float fy = panel.Bottom - footerH;
        dl.Rect(new RectF(panel.X + th.S(1), fy, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
        float fb = Widgets.Baseline(dl, px * 0.82f, fy, footerH);
        float fx = panel.X + pad;
        fx = Hint(dl, th, fx, fy + footerH / 2, fb, AgentsOverlayKeys.Arrows, "navigate", v);
        fx = Hint(dl, th, fx, fy + footerH / 2, fb, AgentsOverlayKeys.Enter, "jump to pane", v);
        fx = Hint(dl, th, fx, fy + footerH / 2, fb, AgentsOverlayKeys.Esc, "close", v);
        if (selectedPrompt is null) return;
        fx = Hint(dl, th, fx, fy + footerH / 2, fb, AgentsOverlayKeys.Yes, "yes, once", v);
        Hint(dl, th, fx, fy + footerH / 2, fb, AgentsOverlayKeys.No, "no", v);
    }

    private void DrawRow(DrawList dl, Theme th, int i, RectF list, float top, float rowH, float px, float pad, float v, long now)
    {
        var row = _rows[i];
        float l1 = MathF.Round(top + rowH * 0.40f), l2 = MathF.Round(top + rowH * 0.78f);
        float dx = list.X + pad + th.S(6);
        AgentBadge.Dot(dl, th, row.State, dx, top + rowH * 0.32f, MathF.Round(th.S(9)), v, now);
        float tx = dx + th.S(18);
        float ageW = dl.Measure(row.Age, px * 0.85f);
        float right = list.Right - pad;
        dl.Text(row.Age, right - ageW, l1, px * 0.85f, th.Muted, v);
        float titleEnd = right - ageW - th.S(16);
        if (row.Prompt is { } prompt)
        {
            titleEnd = AnswerPill(dl, th, prompt.No, "n", th.Danger, i, titleEnd, top + rowH * 0.32f, l1, v);
            titleEnd = AnswerPill(dl, th, prompt.Yes, "y", th.Success, i, titleEnd, top + rowH * 0.32f, l1, v);
        }
        string title = dl.Ellipsize(row.Title, px, titleEnd - tx);
        dl.Text(title, tx, l1, px, i == _selected ? th.PanelFg : Colors.Mix(th.PanelFg, th.Muted, 0.2f), v, FontStyle.Bold);
        float sx = dx + th.S(18);
        string where = dl.Ellipsize(row.Where, px * 0.85f, (right - sx) * 0.45f);
        sx += dl.Text(where, sx, l2, px * 0.85f, th.Muted, v);
        if (row.Detail.Length > 0)
        {
            sx += dl.Text("  ·  ", sx, l2, px * 0.85f, th.Muted, v * 0.6f);
            dl.Text(dl.Ellipsize(row.Detail, px * 0.85f, right - sx), sx, l2, px * 0.85f, Colors.Mix(th.PanelFg, th.Muted, 0.35f), v);
        }
    }

    private static int BodyRowsShown(AgentPrompt prompt) => Math.Min(prompt.Body.Length, MaxBodyRows);

    private static int BodyRowsWithMore(AgentPrompt prompt) => BodyRowsShown(prompt) + (prompt.Body.Length > MaxBodyRows ? 1 : 0);

    /// <summary>One answer as a small pill, right-aligned at <paramref name="right"/>; returns the x left of it.</summary>
    private float AnswerPill(DrawList dl, Theme th, AgentAnswer answer, string key, uint color, int row, float right, float centerY, float baseline, float v)
    {
        float px = th.UiPx * 0.85f, h = MathF.Round(px * 1.5f);
        string text = $"{key}  {answer.Label}";
        float w = dl.Measure(text, px) + 2 * th.S(8);
        var r = new RectF(right - w, MathF.Round(centerY - h / 2), w, h);
        dl.RoundRect(r, h / 2, color, 0.18f * v);
        dl.Text(text, r.X + th.S(8), baseline, px, Colors.Mix(th.PanelFg, color, 0.45f), v, FontStyle.Bold);
        _answerHits.Add((r, row, answer));
        return r.X - th.S(6);
    }

    /// <summary>What the selected row's prompt asks about, so an answer isn't given blind.</summary>
    private static void DrawPromptBody(DrawList dl, Theme th, AgentPrompt prompt, RectF area, float lineH, float v)
    {
        float px = th.UiPx * 0.85f;
        float y = area.Y;
        for (int i = 0; i < BodyRowsShown(prompt); i++, y += lineH)
            dl.Text(dl.Ellipsize(prompt.Body[i], px, area.W), area.X, Widgets.Baseline(dl, px, y, lineH), px, Colors.Mix(th.PanelFg, th.Muted, 0.2f), v);
        if (prompt.Body.Length > MaxBodyRows)
            dl.Text($"… {prompt.Body.Length - MaxBodyRows} more lines", area.X, Widgets.Baseline(dl, px, y, lineH), px, th.Muted, v);
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
internal static class AgentsOverlayKeys
{
    public static readonly string[] Arrows = ["↑", "↓"], Enter = ["↵"], Esc = ["esc"], Yes = ["y"], No = ["n"];
}
