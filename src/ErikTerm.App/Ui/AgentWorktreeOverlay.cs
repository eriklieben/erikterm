using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>
/// The New Agent Worktree form: prompt, agent, arguments, branch, base, and a preview of exactly what will run. All
/// state and rules are in <see cref="WorktreeForm"/>; this draws it and turns keys and clicks into edits.
/// Enter creates and switches, Ctrl+Enter creates in the background and stays open, Tab cycles the agent, Up/Down
/// move between the fields, Esc cancels.
/// </summary>
internal sealed class AgentWorktreeOverlay
{
    private static readonly WorktreeField[] Fields = [WorktreeField.Prompt, WorktreeField.Arguments, WorktreeField.Branch, WorktreeField.Base];

    private WorktreeForm? _form;
    private bool _open;
    private Tween _visible = new(0);
    private long _caretResetAt;
    private float _animMs = 160;
    private RectF _panel;
    private float _textPx = 16;
    private DrawList? _measure;
    private readonly RectF[] _fieldRects = new RectF[Fields.Length];
    private readonly float[] _fieldScroll = new float[Fields.Length];
    private readonly List<RectF> _agentRects = [];

    /// <summary>Starts the launch the form describes (form, request, in the background, where it opens).</summary>
    public Action<WorktreeForm, WorktreeRequest, bool, WorktreePlacement>? Launch;

    public bool IsOpen => _open;
    public bool IsShowing(WorktreeForm form) => _open && ReferenceEquals(_form, form);
    public bool IsAnimating(long now) => _open || _visible.IsAnimating(now); // the caret blinks while open

    public void Open(WorktreeForm form, Theme th, long now)
    {
        _form = form;
        _animMs = th.AnimationMs;
        _open = true;
        Array.Clear(_fieldScroll);
        _visible.Set(1, now, _animMs);
        _caretResetAt = now;
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.75f);
    }

    // ---- Input ------------------------------------------------------------------------------

    public void HandleText(string text, long now)
    {
        if (!_open || _form is not { } form) return;
        form.Field(form.Focus).Insert(text);
        Edited(form, now);
    }

    public void HandleKey(uint key, ushort mod, long now)
    {
        if (!_open || _form is not { } form) return;
        bool ctrl = (mod & (Sdl.KMOD_CTRL | Sdl.KMOD_GUI)) != 0, shift = (mod & Sdl.KMOD_SHIFT) != 0;
        var field = form.Field(form.Focus);
        switch (key)
        {
            case Sdl.K_ESCAPE: Close(now); return;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER: Submit(form, background: ctrl, alternate: (mod & Sdl.KMOD_LALT) != 0, now); return;
            case Sdl.K_TAB: form.CycleAgent(shift ? -1 : 1); _caretResetAt = now; return;
            case Sdl.K_UP: form.MoveFocus(-1); _caretResetAt = now; return;
            case Sdl.K_DOWN: form.MoveFocus(1); _caretResetAt = now; return;
            case Sdl.K_LEFT: field.Left(ctrl); _caretResetAt = now; return;
            case Sdl.K_RIGHT: field.Right(ctrl); _caretResetAt = now; return;
            case Sdl.K_HOME: field.Home(); _caretResetAt = now; return;
            case Sdl.K_END: field.End(); _caretResetAt = now; return;
            case Sdl.K_BACKSPACE: field.Backspace(ctrl); Edited(form, now); return;
            case Sdl.K_DELETE: field.Delete(); Edited(form, now); return;
        }
        if (!ctrl) return; // text arrives via HandleText
        switch (key)
        {
            case 'a': field.Home(); _caretResetAt = now; break;
            case 'e': field.End(); _caretResetAt = now; break;
            case 'u': field.Clear(); Edited(form, now); break;
            case 'w': field.Backspace(word: true); Edited(form, now); break;
            case 'v': field.Insert(Sdl.GetClipboardText()); Edited(form, now); break;
        }
    }

    private void Edited(WorktreeForm form, long now)
    {
        form.Changed(form.Focus);
        _caretResetAt = now;
    }

    private void Submit(WorktreeForm form, bool background, bool alternate, long now)
    {
        _caretResetAt = now;
        if (form.TrySubmit() is { } request) Launch?.Invoke(form, request, background, form.PlacementFor(alternate));
    }

    public void HandleMouseDown(float x, float y, long now)
    {
        if (!_open || _form is not { } form) return;
        if (!_panel.Contains(x, y)) { Close(now); return; }
        for (int i = 0; i < _agentRects.Count && i < form.Agents.Count; i++)
            if (_agentRects[i].Contains(x, y)) { form.SelectAgent(i); return; }
        for (int i = 0; i < Fields.Length; i++)
        {
            if (!_fieldRects[i].Contains(x, y)) continue;
            form.Focus = Fields[i];
            PlaceCaret(form.Field(Fields[i]), x - _fieldRects[i].X - FieldPad + _fieldScroll[i]);
            _caretResetAt = now;
            return;
        }
    }

    private float FieldPad => _textPx * 0.55f;

    /// <summary>Puts the caret at the character boundary nearest to <paramref name="offset"/> pixels into the text.</summary>
    private void PlaceCaret(TextInput field, float offset)
    {
        if (_measure is not { } dl) return;
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i <= field.Text.Length; i++)
        {
            if (i < field.Text.Length && char.IsLowSurrogate(field.Text[i])) continue;
            float distance = MathF.Abs(dl.Measure(field.Text[..i], _textPx) - offset);
            if (distance >= bestDistance) break;
            (best, bestDistance) = (i, distance);
        }
        field.MoveTo(best);
    }

    // ---- Drawing ----------------------------------------------------------------------------

    /// <summary>Draws the preview and status lines one under the other.</summary>
    private struct InfoPen
    {
        public DrawList Dl;
        public float Left, TextW, Small, LineH, Alpha, Y;

        public void Line(string text, uint color, bool mono = false)
        {
            float b = Widgets.Baseline(Dl, Small, Y, LineH);
            Dl.Text(mono ? text : Dl.Ellipsize(text, Small, TextW), Left, b, Small, color, Alpha);
            Y += LineH;
        }
    }

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f || _form is not { } form) return;
        _measure = dl;

        dl.Rect(window, 0x000000, 0.28f * v);

        float px = th.UiPx, small = px * 0.85f;
        _textPx = px;
        float rowH = MathF.Round(px * 2.4f), headerH = MathF.Round(px * 3.1f), footerH = MathF.Round(px * 2.3f), lineH = MathF.Round(px * 1.45f);
        float width = MathF.Round(Widgets.FitWidth(px * 50, th.S(420), window.W - th.S(48)));
        float pad = th.S(18), labelW = MathF.Round(px * 6.2f);
        float textW = width - 2 * pad;

        var previewLines = WrapPreview(dl, form.Preview, small, textW - dl.Measure("$ ", small));
        string? status = form.Busy ? "Creating the worktree…" : form.Error ?? form.Warning;
        var statusLines = status is null ? null : Wrap(dl, status, small, textW - px * 1.4f, 3);
        int infoLines = 1 + previewLines.Count + (form.Setup is null ? 0 : 1) + (statusLines?.Count ?? 0);
        float panelH = headerH + 5 * rowH + th.S(10) + infoLines * lineH + th.S(10) + footerH;

        float slide = (1 - Ease.OutCubic(v)) * th.S(-14);
        var panel = new RectF(MathF.Round(window.X + (window.W - width) / 2), MathF.Round(window.Y + MathF.Max(th.S(12), window.H * 0.10f) + slide), width, panelH);
        Widgets.FloatingPanel(dl, th, panel, th.S(14), v);
        _panel = panel;

        DrawHeader(dl, th, form, panel, headerH, pad, v);

        // Fields and the agent picker, in reading order.
        float y = panel.Y + headerH + th.S(4);
        DrawField(dl, th, form, 0, "Prompt", "What should the agent do? (empty: it just opens)", panel, y, rowH, labelW, pad, v, now);
        y += rowH;
        DrawAgents(dl, th, form, panel, y, rowH, labelW, pad, v);
        y += rowH;
        DrawField(dl, th, form, 1, "Arguments", "Extra arguments for this launch", panel, y, rowH, labelW, pad, v, now);
        y += rowH;
        DrawField(dl, th, form, 2, "Branch", "Branch name", panel, y, rowH, labelW, pad, v, now);
        y += rowH;
        DrawField(dl, th, form, 3, "Base", "Base branch or commit", panel, y, rowH, labelW, pad, v, now);
        y += rowH + th.S(6);

        // What will happen, in full.
        var pen = new InfoPen { Dl = dl, Left = panel.X + pad, TextW = textW, Small = small, LineH = lineH, Alpha = v, Y = y };
        pen.Line(form.Directory.Length > 0 ? "in " + form.Directory : "", th.Muted);
        if (form.Setup is { } setup) pen.Line("setup: " + setup, th.Muted);
        for (int i = 0; i < previewLines.Count; i++)
            pen.Line((i == 0 ? "$ " : "  ") + previewLines[i], th.PanelFg, mono: true);
        if (statusLines is not null) DrawStatus(ref pen, th, form, statusLines);

        DrawFooter(dl, th, form, panel, pad, footerH, v);
    }

    private static void DrawHeader(DrawList dl, Theme th, WorktreeForm form, RectF panel, float headerH, float pad, float v)
    {
        float px = th.UiPx, small = px * 0.85f;
        float baseline = Widgets.Baseline(dl, px * 1.1f, panel.Y, headerH);
        float x = panel.X + pad;
        x += dl.Glyph(Icons.Sparkle, x, baseline, px * 1.1f, th.Accent, v) + th.S(10);
        dl.Text("New Agent Worktree", x, baseline, px * 1.1f, th.PanelFg, v);
        string repo = dl.Ellipsize(form.RepoName, small, panel.W * 0.4f);
        dl.Text(repo, panel.Right - pad - dl.Measure(repo, small), baseline, small, th.Muted, v);
        dl.Rect(new RectF(panel.X + th.S(1), panel.Y + headerH - 1, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
    }

    /// <summary>The busy, error or warning text; its first line has the icon.</summary>
    private static void DrawStatus(ref InfoPen pen, Theme th, WorktreeForm form, List<string> statusLines)
    {
        bool error = !form.Busy && form.Error is not null;
        uint color = th.Muted;
        if (!form.Busy) color = error ? th.Danger : th.Warning;
        for (int i = 0; i < statusLines.Count; i++)
        {
            float b = Widgets.Baseline(pen.Dl, pen.Small, pen.Y, pen.LineH);
            if (i == 0) pen.Dl.Glyph(error ? Icons.Error : Icons.Warning, pen.Left, b, pen.Small, color, pen.Alpha);
            pen.Dl.Text(statusLines[i], pen.Left + th.UiPx * 1.4f, b, pen.Small, color, pen.Alpha);
            pen.Y += pen.LineH;
        }
    }

    private static void DrawFooter(DrawList dl, Theme th, WorktreeForm form, RectF panel, float pad, float footerH, float v)
    {
        float fy = panel.Bottom - footerH;
        dl.Rect(new RectF(panel.X + th.S(1), fy, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
        float fx = panel.X + pad, fb = Widgets.Baseline(dl, th.UiPx * 0.82f, fy, footerH);
        fx = Hint(dl, th, fx, fy, footerH, fb, ["↵"], "create", v);
        fx = Hint(dl, th, fx, fy, footerH, fb, ["Ctrl", "↵"], "in background", v);
        fx = Hint(dl, th, fx, fy, footerH, fb, ["Alt", "↵"], form.Placement == WorktreePlacement.Tab ? "in a split" : "in a tab", v);
        fx = Hint(dl, th, fx, fy, footerH, fb, ["Tab"], "agent", v);
        fx = Hint(dl, th, fx, fy, footerH, fb, ["↑", "↓"], "field", v);
        Hint(dl, th, fx, fy, footerH, fb, ["esc"], "cancel", v);
    }

    private void DrawField(DrawList dl, Theme th, WorktreeForm form, int index, string label, string placeholder, RectF panel, float y, float rowH, float labelW, float pad, float v, long now)
    {
        float px = th.UiPx;
        var kind = Fields[index];
        var field = form.Field(kind);
        bool focused = form.Focus == kind;
        float baseline = Widgets.Baseline(dl, px, y, rowH);
        bool dangerous = kind == WorktreeField.Arguments && WorktreeLauncher.BypassesPermissions(field.Text);
        uint labelRgb = focused ? th.PanelFg : th.Muted;
        if (dangerous) labelRgb = th.Warning;
        dl.Text(label, panel.X + pad, baseline, px * 0.92f, labelRgb, v);

        float inset = th.S(4);
        var box = new RectF(panel.X + pad + labelW, y + inset, panel.W - 2 * pad - labelW, rowH - 2 * inset);
        _fieldRects[index] = box;
        dl.Panel(box, th.S(6), Colors.Mix(th.PanelBg, th.PanelFg, 0.05f), v, focused ? th.Accent : th.Border, v);

        float textPad = FieldPad, inner = box.W - 2 * textPad;
        float caretX = dl.Measure(field.Text[..field.Caret], px);
        ref float scroll = ref _fieldScroll[index];
        if (caretX - scroll > inner) scroll = caretX - inner;
        if (caretX < scroll) scroll = caretX;
        scroll = MathF.Max(0, MathF.Min(scroll, MathF.Max(0, dl.Measure(field.Text, px) - inner)));

        dl.PushClip(box);
        float tx = box.X + textPad - scroll;
        if (field.Text.Length == 0) dl.Text(placeholder, box.X + textPad, baseline, px * 0.92f, th.Muted, v * 0.8f);
        else dl.Text(field.Text, tx, baseline, px, th.PanelFg, v);
        if (focused && _open && (now - _caretResetAt) % 1060 < 600)
        {
            var m = dl.MetricsFor(px);
            dl.Rect(new RectF(tx + caretX, baseline - m.Ascender + th.S(1), MathF.Max(1, th.S(2)), m.Height - th.S(2)), th.Accent, v);
        }
        dl.PopClip();
    }

    private void DrawAgents(DrawList dl, Theme th, WorktreeForm form, RectF panel, float y, float rowH, float labelW, float pad, float v)
    {
        float px = th.UiPx, chipPad = px * 0.7f;
        float baseline = Widgets.Baseline(dl, px, y, rowH);
        dl.Text("Agent", panel.X + pad, baseline, px * 0.92f, th.Muted, v);
        float x = panel.X + pad + labelW, right = panel.Right - pad, inset = th.S(4);
        _agentRects.Clear();
        if (form.Agents.Count == 0) { dl.Text("none found on PATH", x, baseline, px * 0.92f, th.Danger, v); return; }
        for (int i = 0; i < form.Agents.Count; i++)
        {
            string name = form.Agents[i].Name;
            float w = dl.Measure(name, px * 0.92f) + 2 * chipPad;
            if (x + w > right) break; // more agents than room: Tab still reaches them
            var chip = new RectF(x, y + inset, w, rowH - 2 * inset);
            _agentRects.Add(chip);
            bool selected = i == form.AgentIndex;
            if (selected)
            {
                dl.RoundRect(chip, th.S(6), th.Accent, 0.22f * v);
                dl.RoundRect(new RectF(chip.X + th.S(4), chip.Bottom - th.S(3), chip.W - th.S(8), th.S(2)), th.S(1), th.Accent, v);
            }
            dl.Text(name, x + chipPad, baseline, px * 0.92f, selected ? th.PanelFg : th.Muted, v);
            x += w + th.S(6);
        }
    }

    /// <summary>The command in up to three lines. The prompt was shortened in the form, so arguments stay readable.</summary>
    private static List<string> WrapPreview(DrawList dl, string preview, float px, float width) =>
        preview.Length == 0 ? [""] : Wrap(dl, preview, px, width, 3);

    /// <summary>Greedy character wrap to <paramref name="maxLines"/>; what doesn't fit ends in an ellipsis.</summary>
    private static List<string> Wrap(DrawList dl, string text, float px, float width, int maxLines)
    {
        var lines = new List<string>(maxLines);
        int start = 0;
        while (start < text.Length && lines.Count < maxLines)
        {
            if (lines.Count == maxLines - 1)
            {
                lines.Add(dl.Ellipsize(text[start..], px, width));
                break;
            }
            int end = LineEnd(dl, text, start, px, width);
            lines.Add(text[start..end].TrimEnd());
            start = end;
        }
        return lines.Count > 0 ? lines : [""];
    }

    /// <summary>Where the line starting at <paramref name="start"/> ends: after the last space that fits, else at the last character that does.</summary>
    private static int LineEnd(DrawList dl, string text, int start, float px, float width)
    {
        int end = start, lastSpace = -1;
        while (end < text.Length && dl.Measure(text[start..(end + 1)], px) <= width)
        {
            if (text[end] == ' ') lastSpace = end;
            end++;
        }
        if (end < text.Length && lastSpace > start) end = lastSpace + 1;
        if (end == start) end = start + 1; // a glyph wider than the line
        return end;
    }

    private static float Hint(DrawList dl, Theme th, float x, float top, float h, float baseline, string[] keys, string label, float a)
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
