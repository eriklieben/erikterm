using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>wezterm-style pane picker: every pane gets a big letter badge; press it to jump there.</summary>
internal sealed class PaneSelect
{
    private const string Alphabet = "asdfjklghqwertyuiopzxcvbnm";

    private readonly List<(Pane Pane, char Label)> _targets = [];
    private readonly HashSet<Pane> _picked = [];
    private Action<List<Pane>>? _multi;   // several panes: letters toggle, Enter hands them over
    private bool _open;
    private Tween _visible = new(0);
    private float _animMs = 160;

    public Action<Pane>? Selected;

    public bool IsOpen => _open;
    public bool IsVisible(long now) => _open || _visible.Value(now) > 0.001f;
    public bool IsAnimating(long now) => _visible.IsAnimating(now);

    /// <summary>Picks several panes: a letter (or a click) toggles a pane, Enter confirms, Esc cancels.</summary>
    public void OpenMulti(IEnumerable<Pane> panes, Theme th, long now, Action<List<Pane>> done)
    {
        Open(panes, th, now);
        _multi = done;
    }

    public void Open(IEnumerable<Pane> panes, Theme th, long now)
    {
        _multi = null;
        _picked.Clear();
        _targets.Clear();
        int i = 0;
        foreach (var p in panes) _targets.Add((p, Alphabet[i++ % Alphabet.Length]));
        _open = true;
        _animMs = th.AnimationMs;
        _visible.Snap(0);
        _visible.Set(1, now, _animMs * 1.6f);
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.6f);
    }

    public bool HandleKey(uint key, long now)
    {
        if (!_open) return false;
        if (key == Sdl.K_ESCAPE) { Close(now); return true; }
        if (_multi is { } done && key is Sdl.K_RETURN or Sdl.K_KP_ENTER)
        {
            var chosen = _targets.Where(t => _picked.Contains(t.Pane)).Select(t => t.Pane).ToList();
            Close(now);
            done?.Invoke(chosen);
            return true;
        }
        foreach (var (pane, label) in _targets)
        {
            if (key == label)
            {
                if (_multi is not null) { Toggle(pane); return true; }
                Close(now);
                Selected?.Invoke(pane);
                return true;
            }
        }
        return true;
    }

    private void Toggle(Pane pane)
    {
        if (!_picked.Remove(pane)) _picked.Add(pane);
    }

    public bool HandleMouseDown(float x, float y, long now)
    {
        if (!_open) return false;
        foreach (var (pane, _) in _targets)
        {
            if (pane.Target.Contains(x, y))
            {
                if (_multi is not null) { Toggle(pane); return true; }
                Close(now);
                Selected?.Invoke(pane);
                return true;
            }
        }
        Close(now);
        return true;
    }

    public void Draw(DrawList dl, Theme th, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;
        float pop = _open ? Ease.OutBack(Math.Clamp(v, 0, 1)) : v;

        foreach (var (pane, label) in _targets)
        {
            RectF r = pane.Rect.Value(now);
            dl.Rect(r, 0x000000, 0.38f * v);

            float px = th.PaneSelectPx * (0.85f + 0.15f * pop);
            var m = dl.MetricsFor(px);
            string text = label.ToString().ToUpperInvariant();
            float tw = dl.Measure(text, px, FontStyle.Bold);
            float size = MathF.Round(MathF.Max(m.Height, tw) + th.S(26));
            var badge = new RectF(MathF.Round(r.X + (r.W - size) / 2), MathF.Round(r.Y + (r.H - size) / 2 - th.S(10)), size, size);
            dl.Shadow(badge.Offset(0, th.S(8)), th.S(18), th.S(22), 0.5f * v);
            dl.RoundRect(badge, th.S(18), _picked.Contains(pane) ? th.Warning : th.PaneSelectBg, v);
            dl.Text(text, badge.X + (size - tw) / 2, MathF.Round(badge.Y + (size - m.Height) / 2 + m.Ascender), px, th.PaneSelectFg, v, FontStyle.Bold);

            // What's running there, under the badge.
            var agent = pane.Agent.State;
            string captionText = agent != ErikTerm.Ui.AgentStatus.AgentState.None && pane.Agent.PillText.Length > 0 ? pane.Agent.PillText : pane.Label;
            float dotW = agent != ErikTerm.Ui.AgentStatus.AgentState.None ? th.S(16) : 0;
            string caption = dl.Ellipsize(captionText, th.UiPx, r.W - th.S(24) - dotW);
            float cw = dl.Measure(caption, th.UiPx) + dotW;
            var cm = dl.MetricsFor(th.UiPx);
            float cb = badge.Bottom + th.S(14) + cm.Ascender;
            float cx = r.X + (r.W - cw) / 2;
            if (dotW > 0) AgentBadge.Dot(dl, th, agent, cx + th.S(5), MathF.Round(cb - cm.Ascender + cm.Height / 2), MathF.Round(th.S(8)), v, now);
            dl.Text(caption, cx + dotW, MathF.Round(cb), th.UiPx, th.Foreground, 0.85f * v);
        }
    }
}
