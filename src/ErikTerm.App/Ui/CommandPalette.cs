using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>
/// Ctrl+Shift+P: a floating, fuzzy-searchable list of every command. Matches are highlighted, the
/// selection glides between rows, recently used commands float to the top, and commands with a
/// preview (color schemes) apply live while highlighted and revert on Escape.
/// </summary>
internal sealed class CommandPalette
{
    private sealed record Result(Command Command, int Score, List<int> Positions);

    /// <summary>How a list is shown: the search field's placeholder, the footer's key hints, or a prompt instead of a list.</summary>
    public sealed class Options
    {
        public string Placeholder = "Search commands…";
        /// <summary>Footer hints after ↑↓ navigate (default: ↵ run).</summary>
        public (string[] Keys, string Label)[]? Hints;
        /// <summary>A text prompt: no list; Enter hands the typed text (and the modifiers) to this.</summary>
        public Action<string, RunModifiers>? Prompt;
        /// <summary>The line shown under a prompt's field (what Enter will do).</summary>
        public string? PromptHint;
        /// <summary>Count label: "N commands" (default), "N themes", …</summary>
        public string Noun = "commands";
    }

    private static readonly Options DefaultOptions = new();
    private Options _options = DefaultOptions;

    private readonly List<Result> _results = [];
    private readonly Dictionary<string, long> _recent = [];
    private List<Command> _commands = [];
    private string _query = "";
    private int _selected;
    private bool _open;
    private Tween _visible = new(0);
    private Tween _highlightRow = new(0);
    private Tween _scroll = new(0);
    private Tween _height = new(0);
    private long _caretResetAt;
    private float _animMs = 160;
    private Command? _previewed;

    // Last layout, for mouse hit-testing.
    private RectF _panel, _list;
    private float _rowH;
    private int _hover = -1;

    public Action<Command, RunModifiers>? Execute;
    public Action<Command?>? PreviewChanged; // null = revert

    public bool IsOpen => _open;
    public bool IsVisible(long now) => _open || _visible.Value(now) > 0.001f;
    public bool IsAnimating(long now) =>
        _visible.IsAnimating(now) || _highlightRow.IsAnimating(now) || _scroll.IsAnimating(now) || _height.IsAnimating(now) || _open; // caret blinks

    public void Open(List<Command> commands, Theme th, long now, Options? options = null)
    {
        _options = options ?? DefaultOptions;
        _commands = commands;
        _animMs = th.AnimationMs;
        _query = "";
        _open = true;
        _visible.Set(1, now, _animMs);
        _caretResetAt = now;
        _previewed = null;
        Refilter(now);
    }

    public void Close(long now, bool revertPreview = true)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.75f);
        if (revertPreview && _previewed is not null) PreviewChanged?.Invoke(null);
        _previewed = null;
    }

    private void Run(long now, RunModifiers mods = RunModifiers.None)
    {
        if (_options.Prompt is { } prompt)
        {
            string text = _query;
            Close(now);
            if (text.Trim().Length > 0) prompt(text, mods);
            return;
        }
        if (_selected < 0 || _selected >= _results.Count) return;
        var cmd = _results[_selected].Command;
        if (!cmd.Enabled) return;
        _recent[cmd.Id] = now;
        _previewed = null; // keep whatever was previewed
        Close(now, revertPreview: false);
        Execute?.Invoke(cmd, mods);
    }

    private static RunModifiers Modifiers(ushort mod) =>
        ((mod & Sdl.KMOD_SHIFT) != 0 ? RunModifiers.Shift : 0) | ((mod & Sdl.KMOD_CTRL) != 0 ? RunModifiers.Ctrl : 0)
        | ((mod & Sdl.KMOD_LALT) != 0 ? RunModifiers.Alt : 0);

    // ---- Input ------------------------------------------------------------------------------

    public void HandleText(string text, long now)
    {
        if (!_open) return;
        _query += text;
        _caretResetAt = now;
        Refilter(now);
    }

    public bool HandleKey(uint key, ushort mod, long now)
    {
        if (!_open) return false;
        bool ctrl = (mod & Sdl.KMOD_CTRL) != 0;
        switch (key)
        {
            case Sdl.K_ESCAPE: Close(now); return true;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER: Run(now, Modifiers(mod)); return true;
            case Sdl.K_UP: Move(-1, now); return true;
            case Sdl.K_DOWN: Move(1, now); return true;
            case Sdl.K_TAB: Move((mod & Sdl.KMOD_SHIFT) != 0 ? -1 : 1, now); return true;
            case Sdl.K_PAGEUP: Move(-PageSize, now); return true;
            case Sdl.K_PAGEDOWN: Move(PageSize, now); return true;
            case Sdl.K_HOME when ctrl: Select(0, now); return true;
            case Sdl.K_END when ctrl: Select(_results.Count - 1, now); return true;
            case Sdl.K_BACKSPACE: DeleteBack(ctrl, now); return true;
        }
        if (ctrl && key is 'n' or 'j') { Move(1, now); return true; }
        if (ctrl && key is 'p' or 'k') { Move(-1, now); return true; }
        if (ctrl && key == 'u') { _query = ""; Refilter(now); return true; }
        return true; // swallow everything else while open (text arrives via HandleText)
    }

    /// <summary>Backspace: one character, or with Ctrl the last word.</summary>
    private void DeleteBack(bool word, long now)
    {
        if (_query.Length == 0) return;
        if (word)
        {
            string trimmed = _query.TrimEnd();
            int cut = trimmed.LastIndexOf(' ');
            _query = cut < 0 ? "" : trimmed[..(cut + 1)];
        }
        else _query = _query[..^(char.IsLowSurrogate(_query[^1]) && _query.Length > 1 ? 2 : 1)];
        _caretResetAt = now;
        Refilter(now);
    }

    public bool HandleMouseMove(float x, float y, long now)
    {
        if (!_open) return false;
        int row = RowAt(x, y);
        if (row != _hover && row >= 0) Select(row, now);
        _hover = row;
        return true;
    }

    public bool HandleMouseDown(float x, float y, long now)
    {
        if (!_open) return false;
        if (!_panel.Contains(x, y)) { Close(now); return true; }
        int row = RowAt(x, y);
        if (row >= 0) { Select(row, now); Run(now, Modifiers(Sdl.GetModState())); }
        return true;
    }

    public bool HandleWheel(int lines, long now)
    {
        if (!_open) return false;
        Move(-lines, now);
        return true;
    }

    private int PageSize => Math.Max(1, VisibleRows - 1);
    private int VisibleRows => _options.Prompt is not null ? 1 : Math.Max(1, Math.Min(Math.Min(_rowsSetting, _rowsFit), _results.Count));
    private int _rowsSetting = 12, _rowsFit = int.MaxValue;

    private int RowAt(float x, float y)
    {
        if (!_list.Contains(x, y) || _rowH <= 0) return -1;
        int row = (int)((y - _list.Y) / _rowH + _scroll.Target);
        return row >= 0 && row < _results.Count ? row : -1;
    }

    private void Move(int delta, long now)
    {
        if (_results.Count == 0) return;
        int next = _selected + delta;
        // Wrap only on single steps, clamp on page jumps.
        if (Math.Abs(delta) == 1) next = (next + _results.Count) % _results.Count;
        Select(Math.Clamp(next, 0, _results.Count - 1), now);
    }

    private void Select(int index, long now, bool snap = false)
    {
        if (_results.Count == 0) { _selected = 0; return; }
        _selected = Math.Clamp(index, 0, _results.Count - 1);
        if (snap) _highlightRow.Snap(_selected); else _highlightRow.Set(_selected, now, _animMs * 0.6f);

        // Keep the selection inside the visible window.
        float top = _scroll.Target;
        int visible = VisibleRows;
        if (_selected < top) top = _selected;
        else if (_selected >= top + visible) top = _selected - visible + 1;
        top = Math.Clamp(top, 0, Math.Max(0, _results.Count - visible));
        if (snap) _scroll.Snap(top); else _scroll.Set(top, now, _animMs * 0.6f);

        var cmd = _results[_selected].Command;
        if (cmd.Preview is not null)
        {
            if (!ReferenceEquals(_previewed, cmd)) { _previewed = cmd; PreviewChanged?.Invoke(cmd); }
        }
        else if (_previewed is not null)
        {
            _previewed = null;
            PreviewChanged?.Invoke(null);
        }
    }

    private void Refilter(long now)
    {
        _results.Clear();
        if (_options.Prompt is not null) return; // the typed text is the payload: nothing to filter
        foreach (var cmd in _commands) AddIfMatching(cmd);

        if (_query.Length == 0) SortByRecentUse();
        else SortByScore();

        _scroll.Snap(0);
        Select(0, now, snap: true);
    }

    private void AddIfMatching(Command cmd)
    {
        var positions = new List<int>();
        if (_query.Length == 0)
        {
            _results.Add(new Result(cmd, 0, positions));
            return;
        }
        // Match against "Category: Title" so "pane split" works, but highlight within the title.
        if (!Fuzzy.Match(_query, cmd.SearchText, out int score, positions)) return;
        int offset = cmd.Category.Length + 2;
        positions.RemoveAll(p => p < offset);
        for (int i = 0; i < positions.Count; i++) positions[i] -= offset;
        _results.Add(new Result(cmd, score, positions));
    }

    /// <summary>Recently used first, then the natural order.</summary>
    private void SortByRecentUse()
    {
        var order = _commands.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);
        _results.Sort((a, b) =>
        {
            long ra = _recent.GetValueOrDefault(a.Command.Id), rb = _recent.GetValueOrDefault(b.Command.Id);
            if (ra != rb) return rb.CompareTo(ra);
            return order[a.Command].CompareTo(order[b.Command]);
        });
    }

    private void SortByScore()
    {
        _results.Sort((a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = _recent.GetValueOrDefault(b.Command.Id).CompareTo(_recent.GetValueOrDefault(a.Command.Id));
            return c != 0 ? c : string.CompareOrdinal(a.Command.Title, b.Command.Title);
        });
    }

    // ---- Drawing ----------------------------------------------------------------------------

    /// <summary>The sizes one frame's drawing shares.</summary>
    private readonly record struct PaletteFrame(float Px, float RowH, float HeaderH, float FooterH, float Pad, float Alpha, long Now);

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;
        _rowsSetting = th.PaletteRows;

        // Backdrop dims the terminal so the palette reads as a separate layer.
        dl.Rect(window, 0x000000, 0.28f * v);

        float px = th.UiPx;
        float rowH = MathF.Round(px * 2.15f);
        float headerH = MathF.Round(px * 3.1f);
        float footerH = MathF.Round(px * 2.3f);
        float width = MathF.Round(Widgets.FitWidth(px * 42, th.S(380), window.W - th.S(48)));
        _rowsFit = Math.Max(1, (int)((window.H * 0.88f - headerH - footerH - th.S(20)) / rowH)); // what fits under the top of the panel
        bool prompt = _options.Prompt is not null;
        float animatedListH = AnimateListHeight(prompt, VisibleRows, rowH, now);
        float panelH = headerH + animatedListH + th.S(12) + footerH;

        float slide = (1 - Ease.OutCubic(v)) * th.S(-14);
        var panel = new RectF(MathF.Round(window.X + (window.W - width) / 2), MathF.Round(window.Y + window.H * 0.12f + slide), width, panelH);
        float radius = th.S(14);
        Widgets.FloatingPanel(dl, th, panel, radius, v);
        _panel = panel;
        _rowH = rowH;

        var f = new PaletteFrame(px, rowH, headerH, footerH, th.S(18), v, now);
        DrawSearchField(dl, th, panel, prompt, f);
        dl.Rect(new RectF(panel.X + th.S(1), panel.Y + headerH - 1, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);

        // Results.
        var list = new RectF(panel.X, panel.Y + headerH + th.S(6), panel.W, animatedListH);
        _list = list;
        dl.PushClip(list);
        if (prompt) DrawPromptHint(dl, th, list, f);
        else if (_results.Count == 0) DrawNoMatches(dl, th, list, f);
        else DrawResults(dl, th, list, animatedListH, f);
        dl.PopClip();

        DrawFooter(dl, th, panel, prompt, f);
    }

    private float AnimateListHeight(bool prompt, int rows, float rowH, long now)
    {
        float listH;
        if (prompt) listH = rowH * 1.4f;
        else listH = _results.Count == 0 ? rowH * 2 : rows * rowH;
        _height.Set(listH, now, _animMs * 0.8f);
        if (!_height.IsAnimating(now) && _height.Value(now) == 0) _height.Snap(listH);
        return MathF.Round(_height.Value(now));
    }

    private void DrawSearchField(DrawList dl, Theme th, RectF panel, bool prompt, in PaletteFrame f)
    {
        float px = f.Px, v = f.Alpha;
        float baseline = Widgets.Baseline(dl, px * 1.1f, panel.Y, f.HeaderH);
        float x = panel.X + f.Pad;
        x += dl.Glyph(Icons.Search, x, baseline, px * 1.1f, th.Accent, v) + th.S(12);
        if (_query.Length == 0)
            dl.Text(_options.Placeholder, x, baseline, px * 1.1f, th.Muted, v);
        float typed = dl.Text(_query, x, baseline, px * 1.1f, th.PanelFg, v);
        bool caretOn = (f.Now - _caretResetAt) % 1060 < 600;
        if (_open && caretOn)
        {
            var m = dl.MetricsFor(px * 1.1f);
            dl.Rect(new RectF(x + typed + th.S(1), baseline - m.Ascender + th.S(1), MathF.Max(1, th.S(2)), m.Height - th.S(2)), th.Accent, v);
        }
        if (!prompt)
        {
            string count = _query.Length == 0 ? $"{_results.Count} {_options.Noun}" : $"{_results.Count} of {_commands.Count}";
            float countW = dl.Measure(count, px * 0.85f);
            dl.Text(count, panel.Right - f.Pad - countW, baseline, px * 0.85f, th.Muted, v * 0.9f);
        }
    }

    private void DrawPromptHint(DrawList dl, Theme th, RectF list, in PaletteFrame f)
    {
        float px = f.Px;
        float b = Widgets.Baseline(dl, px * 0.92f, list.Y, f.RowH * 1.2f);
        dl.Text(dl.Ellipsize(_options.PromptHint ?? "", px * 0.92f, list.W - f.Pad * 2), list.X + f.Pad + th.S(4), b, px * 0.92f, th.Muted, f.Alpha);
    }

    private static void DrawNoMatches(DrawList dl, Theme th, RectF list, in PaletteFrame f)
    {
        float px = f.Px;
        float b = Widgets.Baseline(dl, px, list.Y, f.RowH * 2);
        string msg = "No matching commands";
        float mw = dl.Measure(msg, px) + px * 1.6f;
        float mx = list.X + (list.W - mw) / 2;
        mx += dl.Glyph(Icons.Search, mx, b, px, th.Muted, f.Alpha) + px * 0.6f;
        dl.Text(msg, mx, b, px, th.Muted, f.Alpha);
    }

    private void DrawResults(DrawList dl, Theme th, RectF list, float animatedListH, in PaletteFrame f)
    {
        float scroll = _scroll.Value(f.Now);
        float hy = list.Y + (_highlightRow.Value(f.Now) - scroll) * f.RowH;
        var highlight = new RectF(list.X + th.S(8), hy + th.S(2), list.W - th.S(16), f.RowH - th.S(4));
        dl.RoundRect(highlight, th.S(8), th.Accent, 0.16f * f.Alpha);
        dl.RoundRect(new RectF(highlight.X, highlight.Y + th.S(7), th.S(3), highlight.H - th.S(14)), th.S(1.5f), th.Accent, f.Alpha);

        int first = Math.Max(0, (int)MathF.Floor(scroll));
        int last = Math.Min(_results.Count - 1, (int)MathF.Ceiling(scroll + animatedListH / f.RowH));
        for (int i = first; i <= last; i++)
            DrawResultRow(dl, th, list, _results[i], list.Y + (i - scroll) * f.RowH, i == _selected, f);
    }

    private static void DrawResultRow(DrawList dl, Theme th, RectF list, Result res, float top, bool selected, in PaletteFrame f)
    {
        var cmd = res.Command;
        float px = f.Px, pad = f.Pad;
        float rb = Widgets.Baseline(dl, px, top, f.RowH);
        float alpha = f.Alpha * (cmd.Enabled ? 1 : 0.45f);

        float cx = list.X + pad + th.S(4);
        dl.Glyph(cmd.Icon, cx, rb, px * 1.05f, cmd.IconColor ?? (selected ? th.Accent : th.Muted), alpha);
        cx += px * 1.9f;
        cx += dl.Text(cmd.Category, cx, rb, px * 0.92f, th.Muted, alpha);
        cx += dl.Text(" › ", cx, rb, px * 0.92f, th.Muted, alpha * 0.7f);
        float shortcutW = cmd.Shortcut.Length > 0 ? Widgets.KeycapsWidth(dl, th, cmd.Shortcut) + th.S(12) : 0;
        string title = dl.Ellipsize(cmd.Title, px, list.Right - pad - shortcutW - cx);
        float titleW = dl.Text(title, cx, rb, px, selected ? th.PanelFg : Colors.Mix(th.PanelFg, th.Muted, 0.25f), alpha,
            new TextHighlight(FontStyle.Regular, res.Positions, th.Accent));
        if (cmd.Detail is { Length: > 0 } detail && ReferenceEquals(title, cmd.Title))
        {
            float dx = cx + titleW + px * 0.8f, room = list.Right - pad - shortcutW - dx;
            if (room > px * 3) dl.Text(dl.Ellipsize(detail, px * 0.85f, room), dx, rb, px * 0.85f, th.Muted, alpha * 0.85f);
        }

        if (cmd.Shortcut.Length > 0)
            Widgets.Keycaps(dl, th, cmd.Shortcut, list.Right - pad, top + f.RowH / 2, alpha, selected);
    }

    /// <summary>The key hints along the bottom of the panel.</summary>
    private void DrawFooter(DrawList dl, Theme th, RectF panel, bool prompt, in PaletteFrame f)
    {
        float v = f.Alpha, footerH = f.FooterH;
        float fy = panel.Bottom - footerH;
        dl.Rect(new RectF(panel.X + th.S(1), fy, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
        float fb = Widgets.Baseline(dl, f.Px * 0.82f, fy, footerH);
        var footer = new FooterHints(dl, th, panel.X + f.Pad, fy + footerH / 2, fb, v, th.S(18));
        if (!prompt) footer.Add(NavigateKeys, "navigate");
        if (_options.Hints is { } hints) foreach (var (keys, label) in hints) footer.Add(keys, label);
        else footer.Add(RunKeys, "run");
        footer.Add(CloseKeys, "close");
    }

    private static readonly string[] NavigateKeys = ["↑", "↓"], RunKeys = ["↵"], CloseKeys = ["esc"];
}
