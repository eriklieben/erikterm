using ErikTerm.App.Rendering;
using ErikTerm.App.Vim;
using ErikTerm.Ui;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.App.Ui;

/// <summary>
/// Vim mode's chrome on a pane: the mode pill in its bottom-right corner (<c>-- NORMAL --</c>, <c>-- VISUAL --</c>, …
/// with the keys of a command being typed) and, while one is open, the <c>/</c> <c>?</c> <c>:</c> prompt along the
/// bottom with its match count or error, and the ':' completions above it. Reads the engine without the terminal lock
/// (only the main thread changes it); strings are cached so a steady frame allocates nothing.
/// </summary>
internal sealed class VimOverlay<TPane> where TPane : class, IVimPane
{
    private readonly Dictionary<TPane, Tween> _appear = [];

    // Cached labels.
    private (VimMode Mode, string Pending) _pillKey = (VimMode.Normal, "\0");
    private string _pillText = "", _pendingText = "";
    private (int Current, int Count, bool Complete) _counterKey = (-2, -1, false);
    private string _counter = "";

    /// <summary>Where this frame's pills and prompts are: toasts stack above them instead of covering them.</summary>
    public List<RectF> Occupied { get; } = [];

    public bool IsAnimating(long now)
    {
#pragma warning disable S3267 // IsAnimating runs every frame: no LINQ iterator or closure here.
        foreach (var t in _appear.Values) if (t.IsAnimating(now)) return true;
#pragma warning restore S3267
        return false;
    }

    public void Show(TPane pane, Theme th, long now)
    {
        var t = new Tween(0);
        t.Set(1, now, th.AnimationMs * 1.2f);
        _appear[pane] = t;
    }

    public void Forget(TPane pane) => _appear.Remove(pane);

    public static string ModeName(VimMode mode) => mode switch
    {
        VimMode.Visual => "-- VISUAL --",
        VimMode.VisualLine => "-- V-LINE --",
        VimMode.VisualBlock => "-- V-BLOCK --",
        _ => "-- NORMAL --",
    };

    // ---- Pill -------------------------------------------------------------------------------

    public void Draw(DrawList dl, Theme th, TPane pane, VimEngine vim, bool focused, IVimSearch<TPane> search, long now)
    {
        float v = _appear.TryGetValue(pane, out var tw) ? tw.Value(now) : 1;
        if (v <= 0.001f) return;
        RectF pr = pane.OverlayArea(now); // above the teammate strip / exit bar (Teammates/WindowTeammates.cs)
        if (vim.Prompt != VimPromptKind.None) DrawPrompt(dl, th, pr, vim, search, v);
        else DrawPill(dl, th, pr, vim, focused, v);
    }

    private void DrawPill(DrawList dl, Theme th, RectF pr, VimEngine vim, bool focused, float v)
    {
        var key = (vim.Mode, vim.Pending);
        if (key != _pillKey)
        {
            _pillKey = key;
            _pillText = ModeName(vim.Mode);
            _pendingText = vim.Pending;
        }
        float px = th.UiPx * 0.84f;
        float h = MathF.Round(px * 2.05f);
        float padX = th.S(12);
        float textW = dl.Measure(_pillText, px, FontStyle.Bold);
        float pendW = _pendingText.Length > 0 ? dl.Measure(_pendingText, px) + th.S(10) : 0;
        float w = MathF.Round(padX * 2 + textW + pendW);
        if (w > pr.W - th.S(20)) return;
        float slide = (1 - Ease.OutCubic(v)) * th.S(8);
        var r = new RectF(MathF.Round(pr.Right - w - th.S(10)), MathF.Round(pr.Bottom - h - th.S(10) + slide), w, h);
        Occupied.Add(r);

        uint ink = vim.InVisual ? th.Warning : th.Accent;
        float a = v * (focused ? 1 : 0.6f);
        dl.Shadow(r.Offset(0, th.S(6)), h / 2, th.S(16), 0.45f * a);
        dl.Panel(r, h / 2, th.PanelBg, 0.96f * a, Colors.Mix(th.PanelBg, ink, 0.55f), a, MathF.Max(1, th.S(1)));
        float b = Widgets.Baseline(dl, px, r.Y, h);
        float x = r.X + padX;
        if (pendW > 0) x += dl.Text(_pendingText, x, b, px, th.Muted, a) + th.S(10);
        dl.Text(_pillText, x, b, px, ink, a, FontStyle.Bold);
    }

    // ---- Prompt -----------------------------------------------------------------------------

    private void DrawPrompt(DrawList dl, Theme th, RectF pr, VimEngine vim, IVimSearch<TPane> search, float v)
    {
        float px = th.UiPx * 0.92f;
        float h = MathF.Round(px * 2.4f);
        float margin = th.S(10);
        var r = new RectF(MathF.Round(pr.X + margin), MathF.Round(pr.Bottom - h - margin), MathF.Round(pr.W - 2 * margin), h);
        if (r.W < th.S(120)) return;
        Occupied.Add(r);
        Widgets.FloatingPanel(dl, th, r, th.S(10), v);

        float b = Widgets.Baseline(dl, px, r.Y, h);
        float x = r.X + th.S(14);
        char lead = vim.Prompt switch { VimPromptKind.SearchForward => '/', VimPromptKind.SearchBackward => '?', _ => ':' };
        x += dl.Glyph(lead, x, b, px, th.Accent, v, FontStyle.Bold) + th.S(4);

        // Right side: the match counter (search) or the error.
        string? error = vim.PromptError ?? (vim.Prompt != VimPromptKind.Ex && search.VimError is not null ? "Invalid pattern" : null);
        string right = error ?? (vim.Prompt == VimPromptKind.Ex ? "" : Counter(vim, search));
        float rightPx = px * 0.86f;
        float rightW = right.Length > 0 ? dl.Measure(right, rightPx) : 0;
        uint rightInk = th.Muted;
        if (error is not null) rightInk = th.Danger;
        else if (_counter == "No match") rightInk = Colors.Mix(th.Danger, th.Muted, 0.35f);
        float rightX = r.Right - th.S(14) - rightW;
        if (rightW > 0) dl.Text(right, rightX, Widgets.Baseline(dl, rightPx, r.Y, h), rightPx, rightInk, v);

        // The text, scrolled so the caret stays in view.
        var field = new RectF(x, r.Y, MathF.Max(0, rightX - th.S(12) - x), h);
        string text = vim.PromptText;
        float typedW = dl.Measure(text, px);
        float shift = MathF.Max(0, typedW + th.S(4) - field.W);
        dl.PushClip(field);
        dl.Text(text, x - shift, b, px, error is not null ? th.Danger : th.PanelFg, v);
        var m = dl.MetricsFor(px);
        dl.Rect(new RectF(x - shift + typedW + th.S(1), b - m.Ascender + th.S(1), MathF.Max(1, th.S(2)), m.Height - th.S(2)), th.Accent, v);
        dl.PopClip();

        if (vim.Prompt == VimPromptKind.Ex) DrawSuggestions(dl, th, r, vim, v);
    }

    private string Counter(VimEngine vim, IVimSearch<TPane> search)
    {
        var matches = search.Engine.Matches;
        int current = -1;
        if (search.IsVim && matches.Count > 0 && vim.PromptText.Length > 0)
        {
            int i = VimSearch.Next(matches, vim.Cursor, forward: true, inclusive: true);
            if (i >= 0 && matches[i].StartLine == vim.Cursor.Line && matches[i].StartCol == vim.Cursor.Col) current = i;
        }
        var key = (current, vim.PromptText.Length == 0 ? -1 : matches.Count, search.Engine.Complete);
        if (key == _counterKey) return _counter;
        _counterKey = key;
        if (key.Item2 < 0) _counter = "";
        else if (key.Item2 == 0) _counter = key.Complete ? "No match" : "Searching…";
        else _counter = current >= 0 ? $"{current + 1}/{key.Item2}" : $"{key.Item2} matches";
        return _counter;
    }

    private static void DrawSuggestions(DrawList dl, Theme th, RectF prompt, VimEngine vim, float v)
    {
        var items = vim.Suggestions;
        if (items.Count == 0) return;
        float px = th.UiPx * 0.86f;
        float rowH = MathF.Round(px * 1.85f);
        float pad = th.S(6);
        float w = MathF.Min(prompt.W, th.S(560));
        float h = rowH * items.Count + pad * 2;
        var r = new RectF(prompt.X, MathF.Round(prompt.Y - h - th.S(6)), w, h);
        if (r.Y < th.S(4)) return;
        Widgets.FloatingPanel(dl, th, r, th.S(10), v);
        float y = r.Y + pad;
        for (int i = 0; i < items.Count; i++)
        {
            var row = new RectF(r.X + pad, y, r.W - pad * 2, rowH);
            bool picked = i == vim.SuggestionIndex;
            if (picked) dl.RoundRect(row, th.S(6), th.Accent, 0.22f * v);
            float b = Widgets.Baseline(dl, px, y, rowH);
            float x = row.X + th.S(8);
            string item = items[i];
            float tw = dl.Text(item, x, b, px, picked ? th.PanelFg : Colors.Mix(th.PanelFg, th.Muted, 0.25f), v, picked ? FontStyle.Bold : FontStyle.Regular);
            if (item.IndexOf(' ') < 0 && Ex.Find(item) is { } spec)
            {
                float hx = x + MathF.Max(tw + th.S(16), th.S(120));
                float max = row.Right - hx - th.S(8);
                if (max > th.S(60)) dl.Text(dl.Ellipsize(spec.Help, px * 0.92f, max), hx, b, px * 0.92f, th.Muted, v);
            }
            y += rowH;
        }
    }
}
