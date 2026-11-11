using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Vim;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

internal enum SearchKeyResult { Handled, NotHandled, Close, Previous, Next }

/// <summary>
/// Ctrl+Shift+F: a floating find bar in the top-right corner of a pane. Searches the scrollback and
/// screen as you type (incrementally, a bounded number of rows per frame), highlights every match
/// and outlines the current one; Enter/↑ goes to older matches, Shift+Enter/↓ to newer ones.
/// <para>
/// Vim mode's / and ? use the same engine and highlights without the bar (<see cref="BeginVim"/>): vim draws its own
/// prompt, moves its cursor between matches and says which one is current; the highlights stay until :noh.
/// </para>
/// </summary>
internal sealed class SearchBar : IVimSearch<Pane>
{
    private enum Hit { None, Field, Case, Regex, Previous, Next, Close }

    public readonly TerminalSearch Engine = new();
    TerminalSearch IVimSearch<Pane>.Engine => Engine;

    private bool _open;
    private Tween _visible = new(0);
    private Tween _pulse = new(0);
    private float _animMs = 160;
    private string _query = "";
    private bool _caseSensitive, _regex;
    private long _caretResetAt;

    // The current match, by index and by value (indices shift when old lines drop off).
    private int _current = -1;
    private SearchMatch _currentMatch;
    private bool _hasCurrent, _userPicked, _revealPending;
    private bool _held; // the current match came from outside (the search across panes) and the scan hasn't reached it yet
    private int _seenRevision = -1;
    private long _anchorLine;
    private int _anchorCol;

    // Last layout, for hit-testing.
    private RectF _panel, _caseChip, _regexChip, _prevButton, _nextButton, _closeButton;
    private Hit _hover;

    // Scrollbar markers, rebuilt when the matches or the history size change.

    // Vim mode's search (no bar): its own pattern, so the bar's query survives.
    private bool _vim;
    private string _vimPattern = "";
    private bool _vimCase;
    private bool _barShown = true; // the fade belongs to the bar (false: to vim's highlights, which have no bar)

    public Pane? Pane { get; private set; }
    public bool IsOpen => _open;
    /// <summary>Vim mode is searching (highlights showing, no bar).</summary>
    public bool IsVim => _vim;
    /// <summary>The bar is open or vim is searching: the engine scans and highlights draw.</summary>
    public bool IsActive => _open || _vim;
    public string Query => _query;
    public bool IsVisible(long now) => _open || _visible.Value(now) > 0.001f;
    public bool IsAnimating(long now) => _visible.IsAnimating(now) || _pulse.IsAnimating(now) || (IsActive && !Engine.Complete);

    /// <summary>The caret blinks while open; this is when it next changes.</summary>
    public int MsUntilCaretChange(long now)
    {
        if (!_open) return int.MaxValue;
        int phase = (int)((now - _caretResetAt) % 1060);
        return phase < 600 ? 600 - phase : 1060 - phase;
    }

    public SearchMatch? Current => _hasCurrent ? _currentMatch : null;

    public void Open(Pane pane, string? initialQuery, Theme th, long anchorLine, int anchorCol, long now)
    {
        _vim = false;
        _held = false;
        _barShown = true;
        if (!ReferenceEquals(Pane, pane)) Engine.Restart();
        Pane = pane;
        _animMs = th.AnimationMs;
        if (initialQuery is not null) _query = initialQuery;
        _open = true;
        _visible.Set(1, now, _animMs);
        _caretResetAt = now;
        _anchorLine = anchorLine;
        _anchorCol = anchorCol;
        _hasCurrent = false;
        _userPicked = false;
        _revealPending = true;
        _seenRevision = -1;
        _hover = Hit.None;
    }

    /// <summary>
    /// Opens the bar on a match found elsewhere (search across panes) with its query: that match is current and outlined
    /// straight away, while the scan of this pane catches up.
    /// </summary>
    public void OpenAt(Pane pane, string query, bool caseSensitive, bool regex, SearchMatch match, Theme th, long now)
    {
        Open(pane, query, th, match.StartLine, match.StartCol, now);
        _caseSensitive = caseSensitive;
        _regex = regex;
        Engine.SetQuery(query, caseSensitive, regex);
        _revealPending = false;
        _userPicked = true;
        _held = true;
        _hasCurrent = false;
        SetCurrent(match, now);
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.8f);
    }

    /// <summary>Drops the pane reference once it is gone (closed, or its tab closed).</summary>
    /// <summary>
    /// The pane reflowed: matches are re-found by the engine (it watches Terminal.ReflowCount), but the current match
    /// and the scrollbar marks refer to old line numbers. Pick a match afresh, without moving the view.
    /// </summary>
    public void OnReflow()
    {
        Engine.Restart();
        _current = -1;
        _hasCurrent = false;
        _userPicked = false;
        _revealPending = false;
        _held = false;
        _seenRevision = -1;
        if (Pane is { } p) { _anchorLine = p.Term.ViewTopLine + p.Term.Rows - 1; _anchorCol = p.Term.Columns; }
    }

    // ---- Vim mode ---------------------------------------------------------------------------

    /// <summary>
    /// Vim's search: highlights every match of <paramref name="pattern"/> (vim syntax, smartcase) in
    /// <paramref name="pane"/> without showing the bar (closing it if it was open).
    /// </summary>
    public void BeginVim(Pane pane, string pattern, Theme th, long now)
    {
        _open = false;
        _barShown = false;
        if (!ReferenceEquals(Pane, pane)) Engine.Restart();
        Pane = pane;
        _animMs = th.AnimationMs;
        if (!_vim || _vimPattern != pattern) { _hasCurrent = false; _current = -1; }
        _vim = true;
        _vimPattern = ErikTerm.Ui.VimMode.VimSearch.ToRegex(pattern);
        _vimCase = ErikTerm.Ui.VimMode.VimSearch.CaseSensitive(pattern);
        _userPicked = true;
        _revealPending = false;
        _visible.Set(1, now, _animMs);
    }

    /// <summary>:noh, leaving vim mode, or an empty / prompt: no more highlights.</summary>
    public void EndVim(long now)
    {
        if (!_vim) return;
        _vim = false;
        _hasCurrent = false;
        _visible.Set(0, now, _animMs * 0.8f);
    }

    /// <summary>Scans the rest of the terminal now (vim's n / Enter need every match), within a time budget.</summary>
    public void CompleteScan(Terminal t, double budgetMs = 60)
    {
        if (!IsActive) return;
        Engine.SetQuery(_vim ? _vimPattern : _query, _vim ? _vimCase : _caseSensitive, _vim || _regex);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!Engine.Complete && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < budgetMs) Engine.Step(t, 4000);
    }

    /// <summary>The match vim's cursor is on (outlined), or none.</summary>
    public void SetCurrent(SearchMatch? match, long now)
    {
        if (match is not { } m) { _hasCurrent = false; return; }
        if (_hasCurrent && m == _currentMatch) return;
        _currentMatch = m;
        _hasCurrent = true;
        _current = Engine.IndexOf(m);
        _pulse.Snap(1);
        _pulse.Set(0, now, 420);
    }

    /// <summary>The vim pattern's regex error, if any.</summary>
    public string? VimError => _vim ? Engine.Error : null;

    public void Detach(Pane pane)
    {
        if (!ReferenceEquals(Pane, pane)) return;
        _vim = false;
        _open = false;
        _visible.Snap(0);
        Pane = null;
        Engine.Restart();
    }

    // ---- Per-frame update (terminal locked) --------------------------------------------------

    /// <summary>
    /// Advances the scan within a time budget and keeps the current match. Returns the match to
    /// scroll to when the view should move (the first result while you type, if it's off screen).
    /// </summary>
    public SearchMatch? Update(Terminal t, long now)
    {
        if (!IsActive || Pane is null) return null;
        if (_vim)
        {
            UpdateVim(t);
            return null;
        }
        if (Engine.SetQuery(_query, _caseSensitive, _regex))
        {
            _hasCurrent = false;
            _userPicked = false;
            _revealPending = true;
            _held = false;
            _seenRevision = -1;
        }

        ScanForAFrame(t);

        if (_held && Engine.Complete) { _held = false; _seenRevision = -1; }
        if (Engine.Revision == _seenRevision) return null;
        _seenRevision = Engine.Revision;
        if (_held) return null;

        if (Engine.Matches.Count == 0) { _current = -1; _hasCurrent = false; return null; }
        KeepCurrentMatch(now);

        // Reveal the auto-picked match once, when it's off screen; never fight the user's scrolling.
        if (_revealPending && !_userPicked && (Engine.Complete || !IsVisibleIn(t, _currentMatch)))
        {
            _revealPending = false;
            return IsVisibleIn(t, _currentMatch) ? null : _currentMatch;
        }
        return null;
    }

    /// <summary>Vim moves its own cursor between matches; just keep scanning.</summary>
    private void UpdateVim(Terminal t)
    {
        Engine.SetQuery(_vimPattern, _vimCase, true);
        ScanForAFrame(t);
        if (_hasCurrent && Engine.IndexOf(_currentMatch) < 0) _hasCurrent = false;
    }

    private void ScanForAFrame(Terminal t)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        do Engine.Step(t, 1500);
        while (!Engine.Complete && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < 4);
    }

    /// <summary>After the matches changed: the same match again if it is still there, else the nearest one, pulsing when it changed.</summary>
    private void KeepCurrentMatch(long now)
    {
        var matches = Engine.Matches;
        int previous = _current;
        int index = CurrentMatchIndex();
        bool changed = !_hasCurrent || matches[index] != _currentMatch;
        _current = index;
        _currentMatch = matches[index];
        _hasCurrent = true;
        if (changed && previous != index) _pulse.Snap(1);
        if (changed) _pulse.Set(0, now, 420);
    }

    private int CurrentMatchIndex()
    {
        int index = _hasCurrent ? Engine.IndexOf(_currentMatch) : -1;
        if (index >= 0) return index;
        if (_hasCurrent && _userPicked) index = Engine.LastAtOrBefore(_currentMatch.StartLine, _currentMatch.StartCol);
        else index = Engine.LastAtOrBefore(_anchorLine, _anchorCol);
        return index < 0 ? 0 : index;
    }

    private static bool IsVisibleIn(Terminal t, SearchMatch m) =>
        m.StartLine >= t.ViewTopLine && m.EndLine < t.ViewTopLine + t.Rows;

    /// <summary>Steps to an older (-1) or newer (+1) match, wrapping. Returns it so the caller can scroll there.</summary>
    public SearchMatch? Step(int delta, long now)
    {
        var matches = Engine.Matches;
        if (matches.Count == 0) return null;
        int i = _hasCurrent ? Engine.IndexOf(_currentMatch) : -1;
        if (i < 0) i = Math.Clamp(_current, 0, matches.Count - 1);
        else i = ((i + delta) % matches.Count + matches.Count) % matches.Count;
        _current = i;
        _currentMatch = matches[i];
        _hasCurrent = true;
        _userPicked = true;
        _revealPending = false;
        _held = false;
        _pulse.Snap(1);
        _pulse.Set(0, now, 420);
        return _currentMatch;
    }

    // ---- Input ------------------------------------------------------------------------------

    public void HandleText(string text, long now)
    {
        if (!_open) return;
        _query += text.Replace("\n", " ").Replace("\r", "");
        _caretResetAt = now;
    }

    public SearchKeyResult HandleKey(uint key, ushort mod, long now)
    {
        if (!_open) return SearchKeyResult.NotHandled;
        bool ctrl = (mod & Sdl.KMOD_CTRL) != 0, shift = (mod & Sdl.KMOD_SHIFT) != 0, alt = (mod & Sdl.KMOD_LALT) != 0;
        _caretResetAt = now;
        switch (key)
        {
            case Sdl.K_ESCAPE: return SearchKeyResult.Close;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER: return shift ? SearchKeyResult.Next : SearchKeyResult.Previous;
            case Sdl.K_UP: return SearchKeyResult.Previous;
            case Sdl.K_DOWN: return SearchKeyResult.Next;
            case Sdl.K_BACKSPACE: DeleteBack(ctrl); return SearchKeyResult.Handled;
        }
        if (alt && !ctrl) return HandleAltKey(key, now);
        return ctrl && !shift ? HandleCtrlKey(key, now) : SearchKeyResult.NotHandled;
    }

    /// <summary>Backspace: one character, or with Ctrl the last word.</summary>
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
    }

    private SearchKeyResult HandleAltKey(uint key, long now)
    {
        switch (key)
        {
            case 'c': ToggleCase(now); return SearchKeyResult.Handled;
            case 'r': ToggleRegex(now); return SearchKeyResult.Handled;
            default: return SearchKeyResult.NotHandled;
        }
    }

    private SearchKeyResult HandleCtrlKey(uint key, long now)
    {
        switch (key)
        {
            case 'r': CycleMode(now); return SearchKeyResult.Handled; // wezterm: cycle match mode
            case 'u': _query = ""; return SearchKeyResult.Handled;
            case 'n': return SearchKeyResult.Next;
            case 'p': return SearchKeyResult.Previous;
            default: return SearchKeyResult.NotHandled;
        }
    }

    public void ToggleCase(long now) { _caseSensitive = !_caseSensitive; _caretResetAt = now; }
    public void ToggleRegex(long now) { _regex = !_regex; _caretResetAt = now; }

    private void CycleMode(long now)
    {
        // insensitive → case-sensitive → regex → insensitive
        if (_regex) { _regex = false; _caseSensitive = false; }
        else if (_caseSensitive) _regex = true;
        else _caseSensitive = true;
        _caretResetAt = now;
    }

    public void Paste(string text, long now) => HandleText(text, now);

    private Hit HitTest(float x, float y)
    {
        if (!_open || !_panel.Contains(x, y)) return Hit.None;
        if (_caseChip.Contains(x, y)) return Hit.Case;
        if (_regexChip.Contains(x, y)) return Hit.Regex;
        if (_prevButton.Contains(x, y)) return Hit.Previous;
        if (_nextButton.Contains(x, y)) return Hit.Next;
        if (_closeButton.Contains(x, y)) return Hit.Close;
        return Hit.Field;
    }

    public bool Contains(float x, float y) => _open && _panel.Contains(x, y);

    /// <summary>Returns true when the pointer is over the bar (so the caller shows a pointer cursor).</summary>
    public bool HandleMouseMove(float x, float y)
    {
        _hover = HitTest(x, y);
        return _hover != Hit.None;
    }

    /// <summary>Clicks on the bar's buttons; null when the click wasn't on the bar.</summary>
    public SearchKeyResult? HandleMouseDown(float x, float y, long now)
    {
        switch (HitTest(x, y))
        {
            case Hit.None: return null;
            case Hit.Case: ToggleCase(now); return SearchKeyResult.Handled;
            case Hit.Regex: ToggleRegex(now); return SearchKeyResult.Handled;
            case Hit.Previous: return SearchKeyResult.Previous;
            case Hit.Next: return SearchKeyResult.Next;
            case Hit.Close: return SearchKeyResult.Close;
            default: _caretResetAt = now; return SearchKeyResult.Handled;
        }
    }

    // ---- Drawing ----------------------------------------------------------------------------

    /// <summary>Where the visible terminal lines are, for drawing the matches on them.</summary>
    private readonly record struct HighlightGrid(float Ox, float Oy, long Top, long Bottom, int Columns, int CellW, int CellH, float Radius, float Alpha);

    /// <summary>Match highlights for <paramref name="pane"/>, under the glyphs (the scrollbar's markers: ScrollbarRenderer.cs).</summary>
    public void DrawHighlights(DrawList dl, Pane pane, float ox, float oy, Theme th, float appear)
    {
        if (!ReferenceEquals(pane, Pane)) return;
        long now = Environment.TickCount64;
        float v = _visible.Value(now) * appear;
        if (v <= 0.002f || (Engine.Matches.Count == 0 && !_held)) return;

        var t = pane.Term;
        var matches = Engine.Matches;
        int ch = dl.Fonts.CellHeight;
        long top = t.ViewTopLine, bottom = top + t.Rows - 1;
        var grid = new HighlightGrid(ox, oy, top, bottom, t.Columns, dl.Fonts.CellWidth, ch, MathF.Max(2, MathF.Round(ch * 0.18f)), v);

        for (int i = Engine.LowerBound(top - 16); i < matches.Count; i++)
        {
            var m = matches[i];
            if (m.StartLine > bottom) break;
            if (m.EndLine >= top) DrawMatch(dl, th, m, grid, now);
        }
        // The scan may not have reached the held match yet; once it has, the loop above drew it.
        if (_held && _hasCurrent && Engine.IndexOf(_currentMatch) < 0 && _currentMatch.EndLine >= top && _currentMatch.StartLine <= bottom)
            DrawMatch(dl, th, _currentMatch, grid, now);
    }

    private void DrawMatch(DrawList dl, Theme th, SearchMatch m, in HighlightGrid g, long now)
    {
        bool current = _hasCurrent && m == _currentMatch;
        for (long line = Math.Max(m.StartLine, g.Top); line <= Math.Min(m.EndLine, g.Bottom); line++)
        {
            int c0 = line == m.StartLine ? m.StartCol : 0;
            int c1 = line == m.EndLine ? m.EndCol : g.Columns - 1;
            var r = new RectF(g.Ox + c0 * g.CellW, g.Oy + (line - g.Top) * g.CellH, (c1 - c0 + 1) * g.CellW, g.CellH);
            if (current) DrawCurrentMatchLine(dl, th, r, g, now);
            else dl.RoundRect(r.Inset(MathF.Max(0, th.S(0.5f))), g.Radius, th.Accent, 0.26f * g.Alpha);
        }
    }

    private void DrawCurrentMatchLine(DrawList dl, Theme th, RectF r, in HighlightGrid g, long now)
    {
        float pulse = Ease.OutCubic(_pulse.Value(now));
        var outer = Grow(r, th.S(1.5f) + pulse * th.S(3));
        dl.Shadow(outer, g.Radius + th.S(2), th.S(6), 0.35f * g.Alpha);
        dl.RoundRect(r, g.Radius, th.Accent, 0.46f * g.Alpha);
        Outline(dl, outer, g.Radius + th.S(1.5f), MathF.Max(1.5f, th.S(1.5f)), th.Accent, g.Alpha * (1 - 0.4f * pulse));
    }

    private static RectF Grow(RectF r, float d) => new(r.X - d, r.Y - d, r.W + 2 * d, r.H + 2 * d);

    /// <summary>A rounded outline: the filled shape clipped to four bands along its edges.</summary>
    public static void Outline(DrawList dl, RectF r, float radius, float thickness, uint rgb, float a)
    {
        ReadOnlySpan<RectF> bands =
        [
            new(r.X, r.Y, r.W, thickness),
            new(r.X, r.Bottom - thickness, r.W, thickness),
            new(r.X, r.Y + thickness, thickness, r.H - 2 * thickness),
            new(r.Right - thickness, r.Y + thickness, thickness, r.H - 2 * thickness),
        ];
        foreach (var band in bands)
        {
            dl.PushClip(band);
            dl.RoundRect(r, radius, rgb, a);
            dl.PopClip();
        }
    }

    public void Draw(DrawList dl, Theme th, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f || Pane is null || !_barShown) return;
        RectF pr = Pane.Rect.Value(now);

        float px = th.UiPx * 0.95f;
        float h = MathF.Round(px * 2.6f);
        float width = MathF.Round(MathF.Min(th.S(440), pr.W - th.S(24)));
        if (width < th.S(160)) return;
        float slide = (1 - Ease.OutCubic(v)) * th.S(-10);
        var panel = new RectF(MathF.Round(pr.Right - th.S(14) - width), MathF.Round(pr.Y + th.S(10) + slide), width, h);
        _panel = panel;
        Widgets.FloatingPanel(dl, th, panel, th.S(12), v);

        float pad = th.S(12);
        float baseline = Widgets.Baseline(dl, px, panel.Y, h);
        float x = panel.X + pad;
        x += dl.Glyph(Icons.Search, x, baseline, px * 1.05f, th.Accent, v) + th.S(10);

        // Right side, laid out right-to-left: close, next, previous | regex, case | counter.
        float btn = MathF.Round(h - th.S(14));
        float right = panel.Right - th.S(7);
        _closeButton = new RectF(right - btn, panel.Y + (h - btn) / 2, btn, btn);
        right -= btn + th.S(2);
        _nextButton = _closeButton with { X = right - btn };
        right -= btn + th.S(2);
        _prevButton = _closeButton with { X = right - btn };
        right -= btn + th.S(8);
        bool hasMatches = Engine.Matches.Count > 0;
        IconButton(dl, th, _closeButton, Icons.Close, _hover == Hit.Close, true, v);
        IconButton(dl, th, _nextButton, Icons.ArrowDown, _hover == Hit.Next, hasMatches, v);
        IconButton(dl, th, _prevButton, Icons.ArrowUp, _hover == Hit.Previous, hasMatches, v);
        dl.Rect(new RectF(right, panel.Y + th.S(10), MathF.Max(1, th.S(1)), h - th.S(20)), th.Border, v);
        right -= th.S(8);

        _regexChip = Chip(dl, th, Hit.Regex, right, panel, v);
        right = _regexChip.X - th.S(4);
        _caseChip = Chip(dl, th, Hit.Case, right, panel, v);
        right = _caseChip.X - th.S(10);

        // Counter.
        string counter = Counter();
        uint counterRgb = th.Muted;
        if (Engine.Error is not null) counterRgb = th.Danger;
        else if (_query.Length > 0 && !hasMatches && Engine.Complete) counterRgb = Colors.Mix(th.Danger, th.Muted, 0.35f);
        float counterPx = px * 0.85f;
        float counterW = counter.Length > 0 ? dl.Measure(counter, counterPx) : 0;
        if (counterW > 0) dl.Text(counter, right - counterW, baseline, counterPx, counterRgb, v);
        right -= counterW + th.S(10);

        // Query field: scrolls so the end (and caret) stays visible.
        var field = new RectF(x, panel.Y, MathF.Max(0, right - x), h);
        float typedW = dl.Measure(_query, px);
        float shift = MathF.Max(0, typedW + th.S(4) - field.W);
        dl.PushClip(field);
        if (_query.Length == 0) dl.Text("Find in scrollback", x, baseline, px, th.Muted, v);
        else dl.Text(_query, x - shift, baseline, px, Engine.Error is null ? th.PanelFg : th.Danger, v);
        if (_open && (now - _caretResetAt) % 1060 < 600)
        {
            var m = dl.MetricsFor(px);
            dl.Rect(new RectF(x - shift + typedW + th.S(1), baseline - m.Ascender + th.S(1), MathF.Max(1, th.S(2)), m.Height - th.S(2)), th.Accent, v);
        }
        dl.PopClip();
    }

    private string _counter = "";
    private (int Current, int Count, bool Complete, bool Truncated, bool Error, bool Empty, bool Has) _counterKey;

    /// <summary>"3 of 17", rebuilt only when it changes (no per-frame strings).</summary>
    private string Counter()
    {
        (int Current, int Count, bool Complete, bool Truncated, bool Error, bool Empty, bool Has) key =
            (_current, Engine.Matches.Count, Engine.Complete, Engine.Truncated, Engine.Error is not null, _query.Length == 0, _hasCurrent);
        if (key == _counterKey && _counter.Length > 0) return _counter;
        _counterKey = key;
        if (key.Error) _counter = "Invalid regex";
        else if (key.Empty) _counter = "";
        else if (key.Count == 0) _counter = key.Complete ? "No results" : "Searching…";
        else
        {
            string total = key.Truncated ? $"{key.Count}+" : key.Count.ToString();
            _counter = (key.Has ? $"{key.Current + 1} of {total}" : total) + (key.Complete ? "" : "…");
        }
        return _counter;
    }

    /// <summary>The regex (".*") or case ("Aa") toggle, right-aligned at <paramref name="right"/> and centred in the bar's height.</summary>
    private RectF Chip(DrawList dl, Theme th, Hit which, float right, RectF bar, float v)
    {
        string label = which == Hit.Regex ? ".*" : "Aa";
        bool on = which == Hit.Regex ? _regex : _caseSensitive, hover = _hover == which;
        float px = th.UiPx * 0.95f * 0.82f, top = bar.Y, h = bar.H;
        float tw = dl.Measure(label, px, FontStyle.Bold);
        float ch = MathF.Round(h - th.S(16));
        float w = MathF.Round(MathF.Max(ch * 1.25f, tw + th.S(12)));
        var r = new RectF(right - w, MathF.Round(top + (h - ch) / 2), w, ch);
        if (on) dl.Panel(r, th.S(6), Colors.Mix(th.PanelBg, th.Accent, 0.28f), v, th.Accent, 0.8f * v);
        else if (hover) dl.RoundRect(r, th.S(6), th.PanelFg, 0.09f * v);
        var m = dl.MetricsFor(px);
        uint labelRgb = hover ? th.PanelFg : th.Muted;
        if (on) labelRgb = th.Accent;
        dl.Text(label, r.X + (w - tw) / 2, MathF.Round(r.Y + (ch - m.Height) / 2 + m.Ascender), px, labelRgb, v, FontStyle.Bold);
        return r;
    }

    private static void IconButton(DrawList dl, Theme th, RectF r, int icon, bool hover, bool enabled, float v)
    {
        float px = th.UiPx * 0.95f * 0.95f;
        float a
 = v * (enabled ? 1 : 0.4f);
        if (hover && enabled) dl.RoundRect(r, th.S(6), th.PanelFg, 0.10f * v);
        var g = dl.Atlas.Get(icon, FontStyle.Regular, px);
        var m = dl.MetricsFor(px);
        float gx = r.X + (r.W - g.Advance) / 2;
        dl.Glyph(icon, gx, MathF.Round(r.Y + (r.H - m.Height) / 2 + m.Ascender), px, hover && enabled ? th.PanelFg : th.Muted, a);
    }
}
