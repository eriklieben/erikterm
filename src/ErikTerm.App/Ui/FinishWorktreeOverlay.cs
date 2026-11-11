using ErikTerm.App.Native;
using ErikTerm.App.Rendering;
using ErikTerm.Ui;

namespace ErikTerm.App.Ui;

/// <summary>A yes/no that says exactly what will run. The danger tone is for removing things.</summary>
internal sealed class WorktreeConfirm
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string ConfirmLabel { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }
    public required IReadOnlyList<string> Commands { get; init; }
    public bool Danger { get; init; }
    public required Action Confirmed { get; init; }
    public bool Busy;
    public string? Error;
}

/// <summary>
/// The Finish Worktree dialog (target, commits, files, conflict prediction, the options, the message, what will run)
/// and the plain confirmation used to remove worktrees. Enter runs it (Ctrl+Enter while editing the message); nothing
/// runs without it.
/// </summary>
internal sealed class FinishWorktreeOverlay
{
    private FinishForm? _form;
    private WorktreeConfirm? _confirm;
    private bool _open;
    private Tween _visible = new(0);
    private float _animMs = 160;
    private long _caretResetAt;
    private RectF _panel, _messageBox;
    private readonly RectF[] _optionRects = new RectF[4];
    private float _scrollTop;

    /// <summary>The form was confirmed.</summary>
    public Action<FinishForm>? Run;

    public bool IsOpen => _open;
    public bool IsShowing(FinishForm form) => _open && ReferenceEquals(_form, form);
    public bool IsShowing(WorktreeConfirm confirm) => _open && ReferenceEquals(_confirm, confirm);
    public bool IsAnimating(long now) => _open || _visible.IsAnimating(now); // the caret blinks while open
    public bool EditingMessage => _open && _form is { MessageFocused: true };

    public void Open(FinishForm form, Theme th, long now)
    {
        _form = form;
        _confirm = null;
        Show(th, now);
    }

    public void Open(WorktreeConfirm confirm, Theme th, long now)
    {
        _confirm = confirm;
        _form = null;
        Show(th, now);
    }

    private void Show(Theme th, long now)
    {
        _animMs = th.AnimationMs;
        _open = true;
        _visible.Set(1, now, _animMs);
        _caretResetAt = now;
        _scrollTop = 0;
    }

    public void Close(long now)
    {
        if (!_open) return;
        _open = false;
        _visible.Set(0, now, _animMs * 0.75f);
    }

    public void HandleText(string text, long now)
    {
        if (!_open || _form is not { MessageFocused: true } form) return;
        form.Message.Insert(text);
        _caretResetAt = now;
    }

    public void HandleKey(uint key, ushort mod, long now)
    {
        if (!_open) return;
        if (_confirm is { } confirm)
        {
            HandleConfirmKey(confirm, key, now);
            return;
        }
        if (_form is not { } form) return;
        bool ctrl = (mod & (Sdl.KMOD_CTRL | Sdl.KMOD_GUI)) != 0;
        switch (key)
        {
            case Sdl.K_ESCAPE: Close(now); return;
            case Sdl.K_RETURN or Sdl.K_KP_ENTER: EnterOnForm(form, ctrl, now); return;
            case Sdl.K_TAB:
                if (form.UsesMessage) form.MessageFocused = !form.MessageFocused;
                _caretResetAt = now;
                return;
        }
        if (form.MessageFocused) HandleMessageKey(form.Message, key, ctrl, now);
        else HandleOptionKey(form, key);
    }

    private void HandleConfirmKey(WorktreeConfirm confirm, uint key, long now)
    {
        if (key == Sdl.K_ESCAPE) Close(now);
        else if (key is Sdl.K_RETURN or Sdl.K_KP_ENTER && !confirm.Busy) confirm.Confirmed();
    }

    /// <summary>Enter adds a line while the message is being edited (Ctrl+Enter runs); otherwise it runs.</summary>
    private void EnterOnForm(FinishForm form, bool ctrl, long now)
    {
        if (form.MessageFocused && !ctrl) { form.Message.Insert("\n"); _caretResetAt = now; return; }
        Submit(form);
    }

    private static void HandleOptionKey(FinishForm form, uint key)
    {
        switch (key)
        {
            case Sdl.K_LEFT or 'h': form.CycleOption(-1); break;
            case Sdl.K_RIGHT or 'l': form.CycleOption(1); break;
            case '1': form.SelectOption(FinishOption.Squash); break;
            case '2': form.SelectOption(FinishOption.Merge); break;
            case '3': form.SelectOption(FinishOption.Rebase); break;
            case '4': form.SelectOption(FinishOption.PullRequest); break;
        }
    }

    private void HandleMessageKey(TextInput field, uint key, bool ctrl, long now)
    {
        switch (key)
        {
            case Sdl.K_UP: field.MoveLine(-1); break;
            case Sdl.K_DOWN: field.MoveLine(1); break;
            case Sdl.K_LEFT: field.Left(ctrl); break;
            case Sdl.K_RIGHT: field.Right(ctrl); break;
            case Sdl.K_HOME: field.Home(); break;
            case Sdl.K_END: field.End(); break;
            case Sdl.K_BACKSPACE: field.Backspace(ctrl); break;
            case Sdl.K_DELETE: field.Delete(); break;
            case 'v' when ctrl: field.Insert(Sdl.GetClipboardText()); break;
            case 'u' when ctrl: field.Clear(); break;
            default: return;
        }
        _caretResetAt = now;
    }

    private void Submit(FinishForm form)
    {
        if (form.Busy || form.Problem is not null) return;
        Run?.Invoke(form);
    }

    public void HandleMouseDown(float x, float y, long now)
    {
        if (!_open) return;
        if (!_panel.Contains(x, y)) { Close(now); return; }
        if (_form is not { } form) return;
        for (int i = 0; i < _optionRects.Length; i++)
            if (_optionRects[i].Contains(x, y)) { form.SelectOption((FinishOption)i); form.MessageFocused = false; return; }
        if (form.UsesMessage && _messageBox.Contains(x, y)) { form.MessageFocused = true; _caretResetAt = now; }
    }

    // ---- Drawing -----------------------------------------------------------------------------

    public void Draw(DrawList dl, Theme th, RectF window, long now)
    {
        float v = _visible.Value(now);
        if (v <= 0.001f) return;
        dl.Rect(window, 0x000000, 0.28f * v);
        if (_form is { } form) DrawForm(dl, th, form, window, v, now);
        else if (_confirm is { } confirm) DrawConfirm(dl, th, confirm, window, v);
    }

    /// <summary>A header's icon and bold title on the left, and the muted <see cref="Detail"/> right-aligned.</summary>
    private readonly record struct HeaderText(int Icon, string Title, string Detail);

    private static void Header(DrawList dl, Theme th, RectF panel, float headerH, in HeaderText text, float v)
    {
        var (icon, title, right) = text;
        float px = th.UiPx, pad = th.S(18), small = px * 0.85f;
        float hb = Widgets.Baseline(dl, px * 1.1f, panel.Y, headerH);
        float x = panel.X + pad;
        x += dl.Glyph(icon, x, hb, px * 1.1f, th.Accent, v) + th.S(12);
        x += dl.Text(title, x, hb, px * 1.1f, th.PanelFg, v, FontStyle.Bold) + th.S(14);
        string fitted = dl.Ellipsize(right, small, panel.Right - pad - x);
        dl.Text(fitted, panel.Right - pad - dl.Measure(fitted, small), hb, small, th.Muted, v);
        dl.Rect(new RectF(panel.X + th.S(1), panel.Y + headerH - 1, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
    }

    /// <summary>Draws the form's text lines one under the other.</summary>
    private struct InfoPen
    {
        public DrawList Dl;
        public Theme Th;
        public float Left, TextW, Small, LineH, Alpha, Y;

        public void Line(string text, uint color, int icon = 0)
        {
            float b = Widgets.Baseline(Dl, Small, Y, LineH);
            float tx = Left;
            if (icon != 0) tx += Dl.Glyph(icon, Left, b, Small, color, Alpha) + Th.S(8);
            Dl.Text(Dl.Ellipsize(text, Small, TextW - (tx - Left)), tx, b, Small, color, Alpha);
            Y += LineH;
        }
    }

    private void DrawForm(DrawList dl, Theme th, FinishForm form, RectF window, float v, long now)
    {
        var f = form.Facts;
        float px = th.UiPx, small = px * 0.85f, lineH = MathF.Round(px * 1.5f);
        float headerH = MathF.Round(px * 3.0f), footerH = MathF.Round(px * 2.3f), rowH = MathF.Round(px * 2.3f);
        float width = MathF.Round(Widgets.FitWidth(px * 52, th.S(460), window.W - th.S(48)));
        float pad = th.S(18), textW = width - 2 * pad;

        var steps = form.Steps;
        string? problem = form.Problem, error = form.Error;
        string? status = form.Busy ? "Checking…" : error ?? problem;
        int subjects = Math.Min(f.Subjects.Count, 3);
        int infoLines = 2 + subjects;
        int commandLines = steps.Count;
        int statusLines = status is null ? 0 : 1;
        int noteLines = form.DroppedAttribution > 0 ? 1 : 0;
        float messageH = form.UsesMessage ? MathF.Round(lineH * 5 + th.S(10)) : 0;
        float panelH = headerH + th.S(8) + infoLines * lineH + rowH + (form.UsesMessage ? messageH + th.S(8) : 0) + commandLines * lineH + (statusLines + noteLines) * lineH + th.S(14) + footerH;
        float slide = (1 - Ease.OutCubic(v)) * th.S(-14);
        var panel = new RectF(MathF.Round(window.X + (window.W - width) / 2), MathF.Round(window.Y + MathF.Max(th.S(12), window.H * 0.08f) + slide), width, panelH);
        _panel = panel;
        Widgets.FloatingPanel(dl, th, panel, th.S(14), v);
        Header(dl, th, panel, headerH, new HeaderText(RepoBadge.PullRequestIcon, "Finish Worktree", $"{f.Branch} → {f.Target}"), v);

        var pen = new InfoPen { Dl = dl, Th = th, Left = panel.X + pad, TextW = textW, Small = small, LineH = lineH, Alpha = v, Y = panel.Y + headerH + th.S(8) };
        DrawFacts(ref pen, th, f, subjects);
        DrawOptions(dl, th, form, pen.Left, pen.Y, rowH, v);
        pen.Y += rowH;

        if (form.UsesMessage)
        {
            var box = new RectF(pen.Left, pen.Y, textW, messageH);
            _messageBox = box;
            bool focused = form.MessageFocused;
            dl.Panel(box, th.S(6), Colors.Mix(th.PanelBg, th.PanelFg, 0.05f), v, focused ? th.Accent : th.Border, v);
            DrawMessage(dl, th, form, box.Inset(th.S(8)), lineH, v, now);
            pen.Y += messageH + th.S(8);
        }

        foreach (var step in steps) pen.Line("$ " + step.Display, th.PanelFg);
        if (form.DroppedAttribution > 0) pen.Line($"Left out {Counted.Of(form.DroppedAttribution, "attribution line")} from the agent's commit", th.Muted, Icons.Info);
        if (status is not null) pen.Line(status, form.Busy ? th.Muted : th.Danger, form.Busy ? 0 : Icons.Error);

        DrawFormFooter(dl, th, form, panel, pad, footerH, v);
    }

    /// <summary>The commits and files, their subjects, and whether the merge would conflict.</summary>
    private static void DrawFacts(ref InfoPen pen, Theme th, FinishFacts f, int subjects)
    {
        string moreFiles = f.Files.Count > 3 ? ", …" : "";
        string fileList = f.Files.Count == 0 ? "" : " · " + string.Join(", ", f.Files.Take(3)) + moreFiles;
        pen.Line($"{Counted.Of(f.Commits, "commit")} · {Counted.Of(f.Files.Count, "file")} changed{fileList}", th.PanelFg, RepoBadge.CommitIcon);
        for (int i = 0; i < subjects; i++)
            pen.Line("  " + f.Subjects[i].Split('\n')[0], th.Muted);
        var (predColor, predIcon) = f.Prediction switch
        {
            { Known: false } => (th.Muted, Icons.Info),
            { Clean: true } => (th.Success, Icons.Check),
            _ => (th.Danger, Icons.Warning),
        };
        pen.Line(f.Prediction.Summary, predColor, predIcon);
    }

    private void DrawOptions(DrawList dl, Theme th, FinishForm form, float x, float y, float rowH, float v)
    {
        float px = th.UiPx, optionPad = px * 0.8f, inset = th.S(4);
        float ob = Widgets.Baseline(dl, px * 0.92f, y, rowH);
        for (int i = 0; i < _optionRects.Length; i++)
        {
            var option = (FinishOption)i;
            string name = $"{i + 1} {FinishRules.Label(option)}";
            float w = dl.Measure(name, px * 0.92f) + 2 * optionPad;
            var chip = new RectF(x, y + inset, w, rowH - 2 * inset);
            _optionRects[i] = chip;
            bool selected = option == form.Option;
            if (selected)
            {
                dl.RoundRect(chip, th.S(6), th.Accent, 0.22f * v);
                dl.RoundRect(new RectF(chip.X + th.S(4), chip.Bottom - th.S(3), chip.W - th.S(8), th.S(2)), th.S(1), th.Accent, v);
            }
            bool blocked = FinishRules.Problem(option, form.Facts, form.Message.Text) is not null;
            uint nameRgb = th.Muted;
            if (selected) nameRgb = th.PanelFg;
            else if (blocked) nameRgb = Colors.Mix(th.Muted, th.PanelBg, 0.45f);
            dl.Text(name, x + optionPad, ob, px * 0.92f, nameRgb, v);
            x += w + th.S(6);
        }
    }

    private static void DrawFormFooter(DrawList dl, Theme th, FinishForm form, RectF panel, float pad, float footerH, float v)
    {
        float fy = panel.Bottom - footerH;
        dl.Rect(new RectF(panel.X + th.S(1), fy, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
        float fb = Widgets.Baseline(dl, th.UiPx * 0.82f, fy, footerH);
        var hints = new FooterHints(dl, th, panel.X + pad, fy + footerH / 2, fb, v, th.S(16));
        if (form.MessageFocused) hints.Add(["Ctrl", "↵"], "run");
        else hints.Add(["↵"], "run");
        hints.Add(["←", "→"], "option");
        if (form.UsesMessage) hints.Add(["Tab"], "message");
        hints.Add(["esc"], "cancel");
    }

    private void DrawMessage(DrawList dl, Theme th, FinishForm form, RectF box, float lineH, float v, long now)
    {
        bool focused = form.MessageFocused;
        float px = th.UiPx * 0.92f;
        string text = form.Message.Text;
        int caret = form.Message.Caret;
        int caretLine = CaretLine(text, caret);
        int visible = Math.Max(1, (int)(box.H / lineH));
        ScrollMessageToCaret(text, caretLine, visible);

        dl.PushClip(box);
        int line = 0, start = 0;
        float baseFirst = Widgets.Baseline(dl, px, box.Y - th.S(1), lineH);
        while (start <= text.Length)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            if (line >= _scrollTop && line < _scrollTop + visible)
            {
                float b = baseFirst + (line - _scrollTop) * lineH;
                dl.Text(text[start..end], box.X, b, px, th.PanelFg, v);
                if (focused && line == caretLine && (now - _caretResetAt) % 1060 < 600)
                    DrawMessageCaret(dl, th, box.X + dl.Measure(text[start..caret], px), b, px, v);
            }
            if (end == text.Length) break;
            start = end + 1;
            line++;
        }
        dl.PopClip();
    }

    private static int CaretLine(string text, int caret)
    {
        int line = 0;
        for (int i = 0; i < caret && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    private void ScrollMessageToCaret(string text, int caretLine, int visible)
    {
        int lines = 1 + text.Count(c => c == '\n');
        if (caretLine < _scrollTop) _scrollTop = caretLine;
        if (caretLine >= _scrollTop + visible) _scrollTop = caretLine - visible + 1;
        _scrollTop = Math.Clamp(_scrollTop, 0, Math.Max(0, lines - visible));
    }

    private static void DrawMessageCaret(DrawList dl, Theme th, float x, float baseline, float px, float v)
    {
        var m = dl.MetricsFor(px);
        dl.Rect(new RectF(x, baseline - m.Ascender + th.S(1), MathF.Max(1, th.S(2)), m.Height - th.S(2)), th.Accent, v);
    }

    private void DrawConfirm(DrawList dl, Theme th, WorktreeConfirm confirm, RectF window, float v)
    {
        float px = th.UiPx, small = px * 0.85f, lineH = MathF.Round(px * 1.5f);
        float headerH = MathF.Round(px * 3.0f), footerH = MathF.Round(px * 2.3f);
        float width = MathF.Round(Widgets.FitWidth(px * 52, th.S(460), window.W - th.S(48)));
        float pad = th.S(18), textW = width - 2 * pad;
        string? status = confirm.Busy ? "Checking again…" : confirm.Error;
        float panelH = headerH + th.S(8) + (confirm.Lines.Count + confirm.Commands.Count + (status is null ? 0 : 1)) * lineH + th.S(14) + footerH;
        float slide = (1 - Ease.OutCubic(v)) * th.S(-14);
        var panel = new RectF(MathF.Round(window.X + (window.W - width) / 2), MathF.Round(window.Y + window.H * 0.14f + slide), width, panelH);
        _panel = panel;
        Widgets.FloatingPanel(dl, th, panel, th.S(14), v);
        Header(dl, th, panel, headerH, new HeaderText(confirm.Danger ? Icons.Warning : Icons.Info, confirm.Title, confirm.Detail), v);

        float y = panel.Y + headerH + th.S(8), left = panel.X + pad;
        void Line(string text, uint color)
        {
            dl.Text(dl.Ellipsize(text, small, textW), left, Widgets.Baseline(dl, small, y, lineH), small, color, v);
            y += lineH;
        }
        foreach (string line in confirm.Lines) Line(line, th.PanelFg);
        foreach (string cmd in confirm.Commands) Line("$ " + cmd, th.Muted);
        if (status is not null) Line(status, confirm.Busy ? th.Muted : th.Danger);
        float fy = panel.Bottom - footerH;
        dl.Rect(new RectF(panel.X + th.S(1), fy, panel.W - th.S(2), MathF.Max(1, th.S(1))), th.Border, v);
        float fb = Widgets.Baseline(dl, px * 0.82f, fy, footerH);
        var hints = new FooterHints(dl, th, panel.X + pad, fy + footerH / 2, fb, v, th.S(16));
        hints.Add(["↵"], confirm.ConfirmLabel);
        hints.Add(["esc"], "keep everything");
    }
}
