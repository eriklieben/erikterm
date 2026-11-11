using ErikTerm.App.Integrations;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>What <see cref="WindowLinks"/> needs beyond the feature window and the pointer.</summary>
internal interface ILinkWindow : IFeatureWindow, IPointerWindow
{
    /// <summary>Some overlay (palette, menu, quick select, a picker) takes the pointer and keys.</summary>
    bool OverlayOpen { get; }
    /// <summary>A quick select hint for vim mode was picked (Vim/WindowVim.cs).</summary>
    void VimHintChosen(QuickSelect.Item item, bool yank, bool open);
}

/// <summary>
/// Links and what the shell marks on screen: hover/Ctrl+click on hyperlinks (OSC 8 and detected URLs, and the
/// smart links of <see cref="WindowEditor"/>), the prompt gutter (a click selects a command's output,
/// <see cref="WindowPrompts"/>), quick select (labels on everything selectable on screen), and their context-menu
/// entries. Hooked into TermWindow at a handful of clearly marked call sites; the per-frame ones take what they need.
/// </summary>
internal sealed class WindowLinks(ILinkWindow window, IPaneInput input, QuickSelect quickSelect, WindowEditor editor, WindowPrompts prompts, Lock gate)
{
    private readonly LinkHint _linkHint = new();
    private readonly LineText _lineText = new();
    private readonly List<LineSegment> _scratchSegments = [];

    private float _mouseX = -1, _mouseY = -1;
    private Pane? _linkPane;                         // pane whose Links.Hovered is showing
    private (Pane? Pane, long Line, int Col) _probe; // last cell probed for a link
    private Pane? _gutterPane;
    private bool _debugCtrl;                         // debug script: pretend Ctrl is held

    private static long Now => Environment.TickCount64;
    private static bool ShiftHeld => (Sdl.GetModState() & Sdl.KMOD_SHIFT) != 0;
    private bool CtrlHeld => _debugCtrl || (Sdl.GetModState() & Sdl.KMOD_CTRL) != 0;

    /// <summary>Where the pointer was last seen over the window (-1: not known).</summary>
    public float MouseX => _mouseX;
    public float MouseY => _mouseY;

    /// <summary>The pane the hint pill is about (for its overlay theme).</summary>
    public Pane? HintPane => _linkHint.Pane;

    // ==== Hooks ================================================================================

    /// <summary>Tick hook (main thread, once per loop iteration): <paramref name="layout"/> is the active tab's.</summary>
    public void Tick(long now, LayoutTree<Pane> layout)
    {
        if (_linkPane is { } lp)
        {
            if (!layout.Contains(lp)) { _linkPane = null; _linkHint.Forget(lp); }
            else if (lp.Links.HoveredUri is not null && lp.Term.Dirty && _mouseX >= 0 && !window.OverlayOpen)
            {
                _probe = default; // output moved under the pointer
                RefreshHover(now);
            }
        }
        if (_gutterPane is { } gp && !layout.Contains(gp)) _gutterPane = null;
        if (quickSelect.IsOpen && (quickSelect.Pane is not { } qp || !layout.Contains(qp))) quickSelect.Close(now);
    }

    /// <summary>IsAnimating hook: <paramref name="leaves"/> are the active tab's panes.</summary>
    public bool IsAnimating(long now, List<Pane> leaves)
    {
        if (quickSelect.IsAnimating(now) || _linkHint.IsAnimating(now)) return true;
#pragma warning disable S3267 // IsAnimating runs every frame: no LINQ iterator or closure here.
        foreach (var p in leaves) if (p.Links.IsAnimating(now)) return true;
#pragma warning restore S3267
        return false;
    }

    /// <summary>Render hook: the hint pill (outside the terminal lock: it uses a snapshot).</summary>
    public void DrawHint(DrawList dl, Theme theme, long now) => _linkHint.Draw(dl, theme, now);

    /// <summary>The pane left this window: drop the hover state that refers to it.</summary>
    public void Forget(Pane pane)
    {
        if (_linkPane == pane) { _linkHint.Forget(pane); _linkPane = null; }
        if (_gutterPane == pane) { _linkHint.Forget(pane); _gutterPane = null; }
        if (_probe.Pane == pane) _probe = default;
        if (quickSelect.IsOpen && quickSelect.Pane == pane) quickSelect.Close(Now);
    }

    /// <summary>The pane reflowed: hovers and the quick select refer to old line numbers.</summary>
    public void Reflowed(Pane pane, long now)
    {
        if (quickSelect.IsOpen && quickSelect.Pane == pane) quickSelect.Close(now);
        if (_linkPane == pane) { _linkHint.Hide(now); _linkPane = null; }
        if (_gutterPane == pane) { _linkHint.Hide(now); _gutterPane = null; }
        if (_probe.Pane == pane) _probe = default;
    }

    // ==== Hover ================================================================================

    /// <summary>
    /// Mouse-move hook. Tracks the link and gutter under the pointer; returns true when it set the
    /// mouse cursor itself (an armed link or a clickable gutter).
    /// </summary>
    public bool Hover(bool eligible, float x, float y, long now)
    {
        _mouseX = x;
        _mouseY = y;
        if (!eligible || quickSelect.IsOpen) { ClearLinkHover(now); ClearGutterHover(now); return false; }
        return RefreshHover(now);
    }

    /// <summary>Ctrl pressed/released: re-arm the hovered link without waiting for the mouse to move.</summary>
    public void OnModifiers(long now)
    {
        if (_linkPane is not { Links.HoveredUri: not null } || _mouseX < 0 || window.OverlayOpen) return;
        if (RefreshHover(now)) return;
        if (window.WantsMouse(_linkPane)) window.DefaultCursor();
        else window.TextCursor();
    }

    private bool RefreshHover(long now)
    {
        float x = _mouseX, y = _mouseY;
        if (GutterAt(x, y) is { } g)
        {
            ClearLinkHover(now);
            if (SetGutterHover(g.Pane, g.Line, now)) { window.PointerCursor(); return true; }
            return false;
        }
        ClearGutterHover(now);

        var pane = window.PaneAt(x, y);
        if (pane is null) { ClearLinkHover(now); return false; }
        var (col, row) = window.CellAt(pane, x, y);
        var links = pane.Links;
        lock (gate) ProbeLinkAt(pane, links, pane.Term.ViewTopLine + row, col, now);

        if (links.HoveredUri is not { } target) { links.Armed = false; _linkHint.Hide(now); return false; }

        bool armed = ShowLinkHint(pane, links, target, row, now);
        window.Redraw();
        if (armed) { window.PointerCursor(); return true; }
        return false;
    }

    /// <summary>Re-reads the link when the pointer entered a new cell (under the terminal lock).</summary>
    private void ProbeLinkAt(Pane pane, PaneLinks links, long line, int col, long now)
    {
        if (_probe == (pane, line, col)) return;
        _probe = (pane, line, col);
        if (_linkPane is { } old && old != pane) { old.Links.Hovered.Clear(); old.Links.HoverFade.Snap(0); }
        bool found = FindLinkAt(pane, line, col, _scratchSegments, out string uri, out bool isExplicit, out var foundSmart);
        links.HoveredSmart = foundSmart; // file / commit / issue links (WindowEditor)
        float anim = window.Anim;
        if (!found)
        {
            links.HoveredUri = null; // the old segments stay while the underline fades out
            links.HoverFade.Set(0, now, anim * 0.8f);
            return;
        }
        bool same = links.HoveredUri == uri && links.Hovered.SequenceEqual(_scratchSegments);
        if (!same)
        {
            links.HoverFade.Snap(0);
            links.Hovered.Clear();
            links.Hovered.AddRange(_scratchSegments);
        }
        links.HoveredUri = uri;
        links.HoveredExplicit = isExplicit;
        links.HoverFade.Set(1, now, anim);
        _linkPane = pane;
    }

    /// <summary>The hint pill for the hovered link; true when Ctrl has armed it.</summary>
    private bool ShowLinkHint(Pane pane, PaneLinks links, string target, int row, long now)
    {
        bool tracking = pane.Term.MouseTracking != MouseTracking.None;
        bool armed = CtrlHeld && (!tracking || ShiftHeld);
        links.Armed = armed;
        bool nearBottom = row >= pane.Term.Rows - 2;
        string how = tracking ? "Ctrl+Shift+click to open" : "Ctrl+click to open";
        // OSC 8 targets are invisible, so they always get the pill; detected URLs only once Ctrl is down.
        if (links.HoveredSmart is { } smart && armed)
            _linkHint.Show(pane, new LinkHint.Message(WindowEditor.SmartLinkIcon(smart), smart.Display, editor.SmartLinkAction(smart)), armed, nearBottom, window.Theme, now);
        else if (links.HoveredExplicit || armed)
            _linkHint.Show(pane, new LinkHint.Message(armed ? ShellIcons.LinkExternal : ShellIcons.Link, target, armed ? "Click to open" : how), armed, nearBottom, window.Theme, now);
        else _linkHint.Hide(now);
        return armed;
    }

    private void ClearLinkHover(long now)
    {
        _probe = default;
        if (_linkPane is { } p)
        {
            p.Links.HoveredUri = null;
            p.Links.Armed = false;
            p.Links.HoverFade.Set(0, now, window.Anim * 0.8f);
        }
        _linkHint.Hide(now);
    }

    /// <summary>The link at a cell (vim mode's gx): its target, and the smart link when it is one. Caller holds the lock.</summary>
    public bool FindLinkAt(Pane pane, long line, int col, out string uri, out SmartLink? smart) =>
        FindLinkAt(pane, line, col, _scratchSegments, out uri, out _, out smart);

    /// <summary>
    /// The link at a cell: an OSC 8 span (extended over soft wraps) or a URL detected in the
    /// logical line. Caller holds the lock. Detection runs only when the pointer enters a new cell.
    /// </summary>
    private bool FindLinkAt(Pane pane, long line, int col, List<LineSegment> segments, out string uri, out bool isExplicit, out SmartLink? smart)
    {
        var t = pane.Term;
        uri = "";
        isExplicit = false;
        smart = null;
        var row = t.GetLine(line);
        if (row is null || col < 0 || col >= row.Cells.Length) { segments.Clear(); return false; }
        if ((row.Cells[col].Flags & CellAttributes.WideTrail) != 0 && col > 0) col--;
        int id = row.LinkIdAt(col);
        if (id != 0 && t.GetHyperlink(id) is { } link)
        {
            segments.Clear();
            uri = link.Uri;
            isExplicit = true;
            AddOsc8Segments(t, line, col, id, segments);
            return true;
        }
        if (row.Cells[col].Rune <= 0x20) { segments.Clear(); return false; } // detected URLs have no blanks

        _lineText.BuildLogicalLine(t, line);
        int index = _lineText.IndexOf(new CellPos(line, col));
        if (index < 0) { segments.Clear(); return false; }
        string text = _lineText.Text;
        foreach (var m in TextPatterns.FindUrls(text))
        {
            if (index < m.Start || index >= m.End) continue;
            segments.Clear();
            uri = m.Of(text);
            AddSegments(t, _lineText, m.Start, m.Length, segments);
            return true;
        }
        // path:line, File.cs(12,5), stack frames, commit hashes, #123 (WindowEditor).
        if (editor.FindSmartLinkAt(pane, text, index, out var found) is { } span)
        {
            segments.Clear();
            uri = found.Key;
            smart = found;
            AddSegments(t, _lineText, span.Start, span.Length, segments);
            return true;
        }
        segments.Clear();
        return false;
    }

    private static void AddOsc8Segments(Terminal t, long line, int col, int id, List<LineSegment> segments)
    {
        var row = t.GetLine(line)!;
        var span = row.Links!.First(s => s.Start <= col && col < s.End);
        segments.Add(new LineSegment(line, span.Start, span.End - 1));
        // Backwards over soft wraps.
        int start = span.Start;
        for (long l = line - 1; start == 0 && t.GetLine(l) is { Wrapped: true, Links: { Count: > 0 } prev } r; l--)
        {
            var last = prev[^1];
            if (last.Id != id || last.End < r.Cells.Length) break;
            segments.Insert(0, new LineSegment(l, last.Start, last.End - 1));
            start = last.Start;
        }
        // Forwards.
        var cur = row;
        int end = span.End;
        for (long l = line + 1; cur.Wrapped && end >= cur.Cells.Length && t.GetLine(l) is { Links: { Count: > 0 } next } r; l++)
        {
            var first = next[0];
            if (first.Id != id || first.Start != 0) break;
            segments.Add(new LineSegment(l, 0, first.End - 1));
            cur = r;
            end = first.End;
        }
    }

    /// <summary>Converts a text match back to per-line cell ranges.</summary>
    private static void AddSegments(Terminal t, LineText lt, int start, int length, List<LineSegment> segments)
    {
        long line = -1;
        int s = 0, e = 0;
        for (int i = start; i < start + length; i++)
        {
            var pos = lt[i];
            int right = pos.Col;
            if (t.GetLine(pos.Line) is { } row && pos.Col < row.Cells.Length && (row.Cells[pos.Col].Flags & CellAttributes.WideLead) != 0) right++;
            if (pos.Line != line)
            {
                if (line >= 0) segments.Add(new LineSegment(line, s, e));
                line = pos.Line;
                s = pos.Col;
                e = right;
            }
            s = Math.Min(s, pos.Col);
            e = Math.Max(e, right);
        }
        if (line >= 0) segments.Add(new LineSegment(line, s, e));
    }

    // ==== Clicks ===============================================================================

    /// <summary>Mouse-down hook (left button, over a pane): Ctrl+click opens the link under the pointer.</summary>
    public bool TryOpenLinkAt(Pane pane, float x, float y)
    {
        if (!CtrlHeld) return false;
        if (pane.Term.MouseTracking != MouseTracking.None && !ShiftHeld) return false;
        if (LinkUnder(pane, x, y) is not { } link) return false;
        if (link.Smart is { } smart) editor.OpenSmartLink(pane, smart);
        else Clipboard.OpenLink(window, link.Uri);
        return true;
    }

    private (string Uri, SmartLink? Smart)? LinkUnder(Pane pane, float x, float y)
    {
        var (col, row) = window.CellAt(pane, x, y);
        lock (gate)
            return FindLinkAt(pane, pane.Term.ViewTopLine + row, col, _scratchSegments, out string uri, out _, out var smart) ? (uri, smart) : null;
    }

    // ==== Prompt gutter ========================================================================

    /// <summary>The pane + absolute line whose left gutter (padding) is under the pointer.</summary>
    private (Pane Pane, long Line)? GutterAt(float x, float y)
    {
        float width = window.Theme.S(9);
        foreach (var p in window.VisiblePanes())
        {
            var r = p.Target;
            if (y < r.Y || y >= r.Bottom || x >= r.X || x < r.X - width) continue;
            bool marks;
            long top;
            lock (gate) { marks = p.Term.HasPromptMarks && !p.Term.IsAlternateScreen; top = p.Term.ViewTopLine; }
            if (!marks) return null;
            int row = Math.Clamp((int)((y - r.Y) / window.CellHeight), 0, p.Term.Rows - 1);
            return (p, top + row);
        }
        return null;
    }

    /// <summary>Whether clicking the gutter at (x, y) would select a command (decides gutter vs divider, see GapHitTest).</summary>
    public bool GutterClickable(float x, float y)
    {
        if (GutterAt(x, y) is not { } g) return false;
        lock (gate) return WindowPrompts.BlockContaining(g.Pane.Term, g.Line) is not null;
    }

    private bool SetGutterHover(Pane pane, long line, long now)
    {
        CommandBlock? block;
        lock (gate) block = WindowPrompts.BlockContaining(pane.Term, line);
        var links = pane.Links;
        if (block is not { } b) { ClearGutterHover(now); return false; }
        float anim = window.Anim;
        if (_gutterPane is { } old && old != pane) old.Links.GutterFade.Set(0, now, anim);
        _gutterPane = pane;
        if (links.GutterPrompt != b.PromptLine) links.GutterFade.Snap(0);
        links.GutterStart = b.OutputStart;
        links.GutterEnd = b.OutputEnd;
        links.GutterPrompt = b.PromptLine;
        links.GutterFade.Set(1, now, anim);
        bool failed = b.ExitCode is not (Row.NoExitCode or 0);
        var th = window.Theme;
        _linkHint.Show(pane, new LinkHint.Message(failed ? ShellIcons.Error : ShellIcons.Output, WindowPrompts.DescribeBlock(b), "Click to select output", failed ? th.Danger : null), true, line - pane.Term.ViewTopLine >= pane.Term.Rows - 2, th, now);
        return true;
    }

    private void ClearGutterHover(long now)
    {
        if (_gutterPane is not { } p) return;
        p.Links.GutterFade.Set(0, now, window.Anim * 1.2f);
        _gutterPane = null;
        _linkHint.Hide(now);
    }

    /// <summary>Mouse-down hook (left button, before pane hit-testing): a click in the gutter selects that command's output.</summary>
    public bool TryGutterClick(float x, float y)
    {
        if (GutterAt(x, y) is not { } g) return false;
        CommandBlock? block;
        lock (gate) block = WindowPrompts.BlockContaining(g.Pane.Term, g.Line);
        if (block is null) return false;
        window.Focus(g.Pane);
        prompts.SelectBlock(g.Pane, block.Value, copy: false);
        return true;
    }

    // ==== Actions ==============================================================================

    /// <summary>Execute hook for QuickSelect, ScrollToPrompt, SelectCommandOutput, CopyCommandOutput.</summary>
    public void Execute(ActionSpec action, uint key, ushort mod)
    {
        long now = Now;
        switch (action.Name)
        {
            case "QuickSelect": OpenQuickSelect(now); break;
            case "ScrollToPrompt":
                int dir = int.TryParse((action.Arg ?? "-1").Trim().Trim('\'', '"'), out int d) && d > 0 ? 1 : -1;
                prompts.ScrollToPrompt(dir, key, mod, now);
                break;
            case "SelectCommandOutput": prompts.SelectLastCommand(copy: false); break;
            case "CopyCommandOutput": prompts.SelectLastCommand(copy: true); break;
        }
    }

    // ==== Quick select =========================================================================

    public void OpenQuickSelect(long now, QuickSelectPurpose purpose = QuickSelectPurpose.Copy)
    {
        var pane = window.Focused;
        var items = new List<QuickSelect.Item>();
        lock (gate)
        {
            var t = pane.Term;
            long top = t.ViewTopLine;
            _lineText.Build(t, top, top + t.Rows - 1);
            string text = _lineText.Text;
            foreach (var m in TextPatterns.FindQuickSelect(text))
            {
                var segments = new List<LineSegment>(1);
                AddSegments(t, _lineText, m.Start, m.Length, segments);
                items.Add(new QuickSelect.Item { Text = m.Of(text), Kind = m.Kind, Segments = segments, Cells = Snapshot(t, segments) });
            }
            // OSC 8 links too: their target, not their text.
            for (int y = 0; y < t.Rows; y++)
            {
                if (t.GetLine(top + y) is not { Links: { Count: > 0 } spans }) continue;
                foreach (var span in spans)
                {
                    if (t.GetHyperlink(span.Id) is not { } link) continue;
                    var seg = new LineSegment(top + y, span.Start, span.End - 1);
                    items.RemoveAll(i => i.Segments.Any(s => s.Line == seg.Line && s.StartCol <= seg.EndCol && seg.StartCol <= s.EndCol));
                    var segments = new List<LineSegment> { seg };
                    items.Add(new QuickSelect.Item { Text = link.Uri, Kind = MatchKind.Url, Segments = segments, Cells = Snapshot(t, segments) });
                }
            }
        }
        items.Sort((a, b) => a.Segments[0].Line != b.Segments[0].Line
            ? a.Segments[0].Line.CompareTo(b.Segments[0].Line)
            : a.Segments[0].StartCol.CompareTo(b.Segments[0].StartCol));
        const int max = 26 * 26;
        if (items.Count > max) items.RemoveRange(0, items.Count - max); // keep the newest
        if (items.Count == 0)
        {
            window.Toast(ToastKind.Info, "Nothing to select", "No URLs, paths, hashes, addresses or numbers on screen");
            return;
        }
        window.CloseContextMenu(now);
        ClearLinkHover(now);
        quickSelect.Open(pane, items, window.Theme, now, purpose);
    }

    private static FontStyle StyleOf(CellAttributes flags) =>
        (FontStyle)(((flags & CellAttributes.Bold) != 0 ? 1 : 0) | ((flags & CellAttributes.Italic) != 0 ? 2 : 0));

    private static List<QuickSelect.GlyphCell> Snapshot(Terminal t, List<LineSegment> segments)
    {
        var cells = new List<QuickSelect.GlyphCell>();
        foreach (var seg in segments)
        {
            if (t.GetLine(seg.Line) is not { } row) continue;
            for (int x = seg.StartCol; x <= seg.EndCol && x < row.Cells.Length; x++)
            {
                ref readonly var c = ref row.Cells[x];
                if (c.Rune <= 0x20 || (c.Flags & CellAttributes.WideTrail) != 0) continue;
                cells.Add(new QuickSelect.GlyphCell(seg.Line, x, c.Rune, StyleOf(c.Flags)));
            }
        }
        return cells;
    }

    /// <summary>Key-down hook while quick select is open.</summary>
    public void QuickSelectKey(uint key, ushort mod, long now)
    {
        if (quickSelect.HandleKey(key, (mod & Sdl.KMOD_SHIFT) != 0, now, (mod & Sdl.KMOD_CTRL) != 0 || _debugCtrl) is { } pick) QuickSelectChosen(pick.Item, pick.Paste);
    }

    /// <summary>Mouse-down hook while quick select is open.</summary>
    public void QuickSelectMouseDown(float x, float y, long now)
    {
        if (quickSelect.HandleMouseDown(x, y, ShiftHeld, now, CtrlHeld) is { } pick) QuickSelectChosen(pick.Item, pick.Paste);
    }

    private void QuickSelectChosen(QuickSelect.Item item, bool paste)
    {
        if (quickSelect.Purpose == QuickSelectPurpose.VimHints) { window.VimHintChosen(item, yank: paste, open: quickSelect.ChosenToOpen); return; } // TermWindow.cs
        if (quickSelect.ChosenToOpen && quickSelect.Pane is { } from && editor.OpenQuickSelectItem(from, item)) return; // Ctrl+label (WindowEditor)
        Sdl.SetClipboardText(item.Text);
        Sdl.SetPrimarySelectionText(item.Text);
        if (paste && quickSelect.Pane is { } pane) input.PasteText(pane, item.Text, broadcast: true);
        window.Toast(ToastKind.Success, paste ? "Copied and pasted" : "Copied", item.Text);
    }

    // ==== Menu =================================================================================

    /// <summary>Context-menu hook: link and command-output entries for what's under the pointer.</summary>
    public void AddMenuItems(List<ContextMenu.Item> items, float x, float y)
    {
        var pane = window.PaneAt(x, y);
        if (pane is null) return;
        var link = LinkUnder(pane, x, y);
        if (link?.Smart is { } smart) editor.AddMenuItems(items, pane, smart); // Open in Neovim, Show Commit, … (WindowEditor)
        else if (link?.Uri is { } uri)
        {
            bool opens = LinkOpener.Normalize(uri) is not null;
            items.InsertRange(0,
            [
                new ContextMenu.Item("Open Link", ShellIcons.LinkExternal, null, () => Clipboard.OpenLink(window, uri), opens),
                new ContextMenu.Item("Copy Link Address", ShellIcons.Link, null, () =>
                {
                    Sdl.SetClipboardText(uri);
                    window.Toast(ToastKind.Success, "Copied link address", uri);
                }),
                ContextMenu.Item.Separator,
            ]);
        }

        CommandBlock? block;
        lock (gate)
        {
            var (_, row) = window.CellAt(pane, x, y);
            block = pane.Term.HasPromptMarks ? pane.Term.CommandAt(pane.Term.ViewTopLine + row) : null;
        }
        if (block is { } b)
        {
            int at = items.FindIndex(i => i.Label == "Select All");
            items.Insert(at < 0 ? items.Count : at + 1,
                new ContextMenu.Item("Copy Command Output", ShellIcons.Output, window.ShortcutText("CopyCommandOutput"), () => prompts.SelectBlock(pane, b, copy: true)));
        }
    }

    // ==== Debug script =========================================================================

    /// <summary>
    /// RunScript hook for ERIKTERM_DEBUG_SCRIPT steps: <c>feed:TEXT</c> (C# escapes, \x3b for ';'),
    /// <c>mouse:COL,ROW</c>, <c>ctrl:1|0</c>, <c>rclick:COL,ROW</c>, <c>gutter:ROW</c>, <c>scroll:N</c>.
    /// </summary>
    public void RunScriptStep(string kind, string arg, long now)
    {
        var focused = window.Focused;
        (float X, float Y) CellCenter(string spec)
        {
            var parts = spec.Split(',');
            int col = int.Parse(parts[0]), row = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            var r = focused.Target;
            return (r.X + (col + 0.5f) * window.CellWidth, r.Y + (row + 0.5f) * window.CellHeight);
        }
        switch (kind)
        {
            case "feed":
                lock (gate) focused.Term.Feed(System.Text.Encoding.UTF8.GetBytes(System.Text.RegularExpressions.Regex.Unescape(arg)));
                break;
            case "mouse":
                var (mx, my) = CellCenter(arg);
                window.ScriptMouseMove(mx, my, now);
                break;
            case "ctrl":
                _debugCtrl = arg is "1" or "on" or "true";
                OnModifiers(now);
                break;
            case "rclick":
                var (rx, ry) = CellCenter(arg);
                window.ScriptMouseMove(rx, ry, now);
                window.OpenContextMenu(rx, ry, now);
                break;
            case "gutter":
                var (_, gy) = CellCenter("0," + arg);
                window.ScriptMouseMove(focused.Target.X - window.Theme.S(4), gy, now);
                break;
            case "scroll":
                lock (gate) focused.Term.ScrollView(int.Parse(arg));
                focused.ScrollActivityAt = now;
                break;
        }
    }
}
