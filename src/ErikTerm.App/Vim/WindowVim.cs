using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.App.Vim;

/// <summary>
/// Vim mode (ActivateCopyMode, Ctrl+Shift+X): the keyboard route through a pane's scrollback — motions, visual
/// selections and yanks, / search (the search bar's engine and highlights), hints (quick select), opening links, files
/// and output in Neovim, marks, the ':' command line and Ctrl-w pane commands. The logic is ErikTerm.Ui.VimMode's
/// <see cref="VimEngine"/>, one per pane (<see cref="IVimPane.Vim"/>); this class feeds it keys, mirrors its selection
/// into the pane's, and carries out what it asks (<see cref="Host"/>). TermWindow forwards its hook points here:
/// keys, text, tick, overlays, help clicks, actions, Search (opens /), the quick select pick and the IPC switch.
/// </summary>
internal sealed class WindowVim<TPane>(IVimWindow<TPane> window, IVimProcess process, IVimSearch<TPane> search, Lock gate)
    where TPane : class, IVimPane
{
    private const string CopyModeAction = "ActivateCopyMode";
    private const string LayoutCategory = "Layout";

    private readonly VimOverlay<TPane> _overlay = new();
    private readonly VimHelpOverlay _help = new();
    private readonly List<Action> _deferred = [];
    private TPane? _previewPane;              // the pane whose :theme is being previewed
    private TPane? _dragPane;                 // a left-button drag in a pane in vim mode
    private VimPos _dragAnchor;

    private static long Now => Environment.TickCount64;

    /// <summary>Where this frame's pills and prompts are: toasts stack above them instead of covering them.</summary>
    public List<RectF> OverlayOccupied => _overlay.Occupied;

    // ==== Entering and leaving ===================================================================

    private void Toggle(TPane pane)
    {
        if (pane.Vim is null) Enter(pane);
        else Leave(pane, toLive: false);
    }

    private void Enter(TPane pane)
    {
        if (pane.Vim is not null) return;
        long now = Now;
        window.CloseBarsForVim(now); // the Ctrl+Shift+F bar gives way to vim's /
        lock (gate)
        {
            var t = pane.Term;
            var vim = VimEngine.Enter(t);
            // A mouse selection carries over as a visual one.
            if (pane.Selection is { Dragging: false } sel && sel.Range(t) is var (sl, sc, el, ec))
            {
                var mode = sel.Mode switch { SelectionMode.Line => VimMode.VisualLine, SelectionMode.Block => VimMode.VisualBlock, _ => VimMode.Visual };
                vim.BeginVisual(new VimPos(sl, sc), new VimPos(el, VimBuffer.Lead(t, el, ec)), mode);
            }
            pane.Vim = vim;
            SyncSelection(pane);
        }
        _overlay.Show(pane, window.Theme, now);
        window.Redraw();
    }

    private void Leave(TPane pane, bool toLive)
    {
        if (pane.Vim is null) return;
        long now = Now;
        lock (gate)
        {
            pane.Vim = null;
            if (pane.Selection is { Vim: true }) pane.Selection = null;
            if (toLive) pane.Term.ResetView();
        }
        if (toLive) pane.SnapScroll();
        if (search.IsVim && ReferenceEquals(search.Pane, pane)) search.EndVim(now);
        window.CloseHints(pane, now);
        if (_previewPane == pane) RevertThemePreview();
        _help.Close(now);
        _overlay.Forget(pane);
        if (_dragPane == pane) _dragPane = null;
        window.Redraw();
    }

    // ==== Hooks ================================================================================

    /// <summary>Hook from OnKeyDown, after the modal overlays: keys while the help is open or the focused pane is in vim mode.</summary>
    public bool KeyDown(uint key, ushort mod, long now)
    {
        if (_help.IsOpen) { _help.HandleKey(key, mod, now); window.SuppressText = true; return true; }
        var pane = window.Focused;
        if (pane.Vim is not { } vim || window.TabBarEditing) return false;
        bool ctrl = (mod & Sdl.KMOD_CTRL) != 0, shift = (mod & Sdl.KMOD_SHIFT) != 0;
        bool alt = (mod & Sdl.KMOD_LALT) != 0, super = (mod & Sdl.KMOD_GUI) != 0;

        string? vk = VimInput.KeyFor(key, ctrl, shift, alt, super);
        bool bound = KeyNames.Chord(key, mod) is { } chord && process.Keys.ContainsKey(chord);
        if (vk is not null && VimInput.TakesFirst(vk, ctrl, bound, vim.Prompt != VimPromptKind.None || vim.Pending.Length > 0))
        {
            Feed(pane, vk);
            window.SuppressText = true;
            return true;
        }
        // Printable keys: their text input event follows (that's what vim reads, so layouts and Shift just work).
        if (!ctrl && !alt && !super && vk is null && (!bound || key is > 0x20 and < 0x7F)) { window.SuppressText = false; return true; }

        window.SuppressText = true; // nothing reaches the program while vim mode is on
        if (KeyNames.Chord(key, mod) is { } c2 && process.Keys.TryGetValue(c2, out var action)) RunBoundAction(pane, vim, action, key, mod);
        return true;
    }

    private void RunBoundAction(TPane pane, VimEngine vim, ActionSpec action, uint key, ushort mod)
    {
        switch (action.Name)
        {
            case CopyModeAction or "VimMode": Leave(pane, toLive: false); break;
            case "Search": OpenSearchPrompt(); break;
            case "PasteFrom" when vim.Prompt != VimPromptKind.None:
                string text = action.Arg == "PrimarySelection" ? window.Primary() : window.Clipboard();
                lock (gate) vim.PromptInsert(text, HostFor(pane));
                RunDeferred();
                break;
            case "CopyTo" when vim.InVisual: Feed(pane, "y"); break;
            default: window.Execute(action, key, mod); break;
        }
    }

    /// <summary>Hook from OnTextInput: typed characters go to vim.</summary>
    public bool TextInput(string text)
    {
        if (_help.IsOpen) return true;
        var pane = window.Focused;
        if (pane.Vim is null || window.TabBarEditing) return false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (pane.Vim is null) break; // a key left vim mode
            Feed(pane, rune.ToString());
        }
        return true;
    }

    /// <summary>Tick hook: lines evicted from the scrollback, incremental search as the scan finds matches, deferred work.</summary>
    public void Tick()
    {
        foreach (var pane in window.Layout.LeafList)
        {
            if (pane.Vim is not { } vim) continue;
            int before = vim.Version;
            lock (gate)
            {
                vim.Clamp();
                if (vim.Prompt is VimPromptKind.SearchForward or VimPromptKind.SearchBackward && search.IsVim && !search.Engine.Complete)
                    vim.UpdateIncremental(HostFor(pane));
                if (vim.Version != before) // only when something moved: a steady frame allocates nothing
                {
                    SyncSelection(pane);
                    SyncSearchCurrent(pane);
                }
            }
            if (vim.Version != before) window.Redraw();
        }
        RunDeferred();
    }

    public bool IsAnimating(long now) => _overlay.IsAnimating(now) || _help.IsAnimating(now);

    /// <summary>Render hook: pills and prompts of panes in vim mode, then the cheat sheet.</summary>
    public void DrawOverlays(DrawList dl, RectF area, long now)
    {
        _overlay.Occupied.Clear();
        var focused = window.Focused;
        foreach (var pane in window.VisiblePanes())
            if (pane.Vim is { } vim) _overlay.Draw(dl, window.PaneOverlayTheme(pane, now), pane, vim, pane == focused, search, now);
        _help.Draw(dl, window.OverlayTheme, area, now);
    }

    public bool HelpMouseDown(float x, float y, long now) => _help.HandleMouseDown(x, y, now);

    public bool HelpWheel(int lines, long now) => _help.HandleWheel(lines, now);

    public void CloseHelp(long now) => _help.Close(now);

    /// <summary>OpenSearch hook: in vim mode, the Search action (Ctrl+Shift+F, palette) opens vim's / prompt.</summary>
    public bool OpenSearchPrompt()
    {
        if (window.Focused.Vim is not { } vim) return false;
        lock (gate)
        {
            if (vim.Prompt == VimPromptKind.None) vim.OpenSearchPrompt();
        }
        window.Redraw();
        return true;
    }

    /// <summary>Hook from the reflow check: line numbers changed under vim's cursor and marks.</summary>
    public void Reflowed(TPane pane)
    {
        if (pane.Vim is not { } vim) return;
        lock (gate) vim.OnReflow();
    }

    // ---- Mouse ------------------------------------------------------------------------------------

    /// <summary>
    /// Hook from OnMouseDown (after Ctrl+click links): in a pane in vim mode a click moves vim's cursor, a double click
    /// selects the word and a triple click the line (visual mode); a drag from there selects (TermWindow.cs move / up).
    /// </summary>
    public bool MouseDown(TPane pane, byte clicks, float x, float y)
    {
        if (pane.Vim is not { } vim) return false;
        if (vim.Prompt != VimPromptKind.None) return true; // the / : prompt has the keyboard; a click doesn't move under it
        lock (gate)
        {
            if (pane.Selection is { Vim: false }) pane.Selection = null;
            var at = VimBuffer.ClampLine(pane.Term, CellAt(pane, x, y));
            at = at with { Col = VimMotions.ClampCol(pane.Term, at.Line, at.Col, free: false) };
            if (clicks >= 3) vim.BeginVisual(new VimPos(at.Line, 0), at, VimMode.VisualLine);
            else if (clicks == 2)
            {
                var word = VimTextObjects.Word(pane.Term, at, around: false, big: false);
                vim.BeginVisual(word.Start, word.End, VimMode.Visual);
            }
            else vim.Click(at);
            _dragAnchor = vim.InVisual ? vim.Anchor : vim.Cursor;
            SyncSelection(pane);
        }
        _dragPane = pane;
        window.Redraw();
        return true;
    }

    /// <summary>Hook from OnMouseMove: the drag extends a visual selection from where it started (past an edge: scrolls).</summary>
    public bool MouseDrag(float x, float y)
    {
        if (_dragPane is not { } pane) return false;
        if (pane.Vim is not { } vim || !window.Layout.Contains(pane)) { _dragPane = null; return false; }
        lock (gate)
        {
            var r = pane.Target;
            var at = CellAt(pane, x, y);
            if (y < r.Y) at = at with { Line = at.Line - 1 };          // MoveTo scrolls to keep the cursor in view
            else if (y >= r.Bottom) at = at with { Line = at.Line + 1 };
            if (!vim.InVisual)
            {
                var cell = VimBuffer.ClampLine(pane.Term, at);
                if (cell with { Col = VimMotions.ClampCol(pane.Term, cell.Line, cell.Col, free: false) } == vim.Cursor) return true;
                vim.BeginVisual(_dragAnchor, vim.Cursor, VimMode.Visual);
            }
            vim.MoveTo(at);
            SyncSelection(pane);
        }
        window.Redraw();
        return true;
    }

    /// <summary>Hook from OnMouseUp: the drag ends; what it selected goes to the primary selection (the X11 convention).</summary>
    public bool MouseUp()
    {
        if (_dragPane is not { } pane) return false;
        _dragPane = null;
        string text = "";
        lock (gate) if (pane.Vim is { InVisual: true } vim) text = vim.SelectionText();
        if (text.Length > 0) window.SetPrimary(text);
        return true;
    }

    private VimPos CellAt(TPane pane, float x, float y)
    {
        var (col, row) = window.CellAt(pane, x, y);
        return new VimPos(pane.Term.ViewTopLine + row, col);
    }

    // ==== Feeding keys =========================================================================

    private void Feed(TPane pane, string key)
    {
        if (pane.Vim is not { } vim) return;
        var host = HostFor(pane);
        lock (gate)
        {
            // A mouse selection made while in visual mode replaces it; y in normal mode copies it.
            if (vim.InVisual && pane.Selection is { Vim: false }) vim.Feed(VimKeys.Esc, host);
            else if (!vim.InVisual && key == "y" && vim.Pending.Length == 0 && vim.Prompt == VimPromptKind.None
                && pane.Selection is { Vim: false, Dragging: false } mouse && mouse.Range(pane.Term) is var (sl, sc, el, ec))
            {
                var mode = mouse.Mode switch { SelectionMode.Line => VimMode.VisualLine, SelectionMode.Block => VimMode.VisualBlock, _ => VimMode.Visual };
                vim.BeginVisual(new VimPos(sl, sc), new VimPos(el, VimBuffer.Lead(pane.Term, el, ec)), mode);
            }
            vim.Feed(key, host);
            SyncSelection(pane);
            SyncSearchCurrent(pane);
        }
        RunDeferred();
        window.Redraw();
    }

    /// <summary>
    /// Debug script step vim:KEYS (vim notation: vim:5j&lt;C-v&gt;jjy). Each key goes through the real routing — a key
    /// down, then its text — so overlays (hints, the cheat sheet) see it as typed. Enters vim mode first.
    /// </summary>
    public void Script(string notation)
    {
        if (window.Focused.Vim is null && !window.QuickSelectOpen && !_help.IsOpen) Enter(window.Focused);
        foreach (var key in VimKeys.Parse(notation)) ScriptKey(key, Now);
    }

    private void ScriptKey(string key, long now)
    {
        if (VimKeys.IsCtrl(key, out char c)) { window.ScriptKeyDown(c, Sdl.KMOD_CTRL, now); return; }
        uint special = key switch
        {
            VimKeys.Esc => Sdl.K_ESCAPE, VimKeys.Enter => Sdl.K_RETURN, VimKeys.Backspace => Sdl.K_BACKSPACE,
            VimKeys.Tab or VimKeys.ShiftTab => Sdl.K_TAB, VimKeys.Up => Sdl.K_UP, VimKeys.Down => Sdl.K_DOWN,
            VimKeys.Left => Sdl.K_LEFT, VimKeys.Right => Sdl.K_RIGHT, _ => 0,
        };
        if (special != 0) { window.ScriptKeyDown(special, key == VimKeys.ShiftTab ? Sdl.KMOD_SHIFT : (ushort)0, now); return; }
        char ch = key[0];
        window.ScriptKeyDown(char.IsAsciiLetter(ch) ? char.ToLowerInvariant(ch) : ch, char.IsAsciiLetterUpper(ch) ? Sdl.KMOD_SHIFT : (ushort)0, now);
        window.ScriptTextInput(key, now);
    }

    private void RunDeferred()
    {
        while (_deferred.Count > 0)
        {
            var work = _deferred[0];
            _deferred.RemoveAt(0);
            work();
        }
    }

    /// <summary>The visual selection, mirrored into the pane's (so drawing, copy and selection_fg work as for the mouse).</summary>
    private static void SyncSelection(TPane pane)
    {
        if (pane.Vim is not { } vim) return;
        if (vim.Selection is not { } r)
        {
            if (pane.Selection is { Vim: true }) pane.Selection = null;
            return;
        }
        var t = pane.Term;
        Selection sel = vim.Mode switch
        {
            VimMode.VisualLine => new Selection { AnchorLine = r.Start.Line, AnchorCol = 0, HeadLine = r.End.Line, HeadCol = 0, Mode = SelectionMode.Line },
            VimMode.VisualBlock => BlockSelection(vim, r),
            _ => new Selection
            {
                AnchorLine = r.Start.Line, AnchorCol = r.Start.Col, HeadLine = r.End.Line,
                HeadCol = r.End.Col + VimBuffer.CellWidth(t, r.End.Line, r.End.Col), Mode = SelectionMode.Cell,
            },
        };
        sel.Vim = true;
        pane.Selection = sel;

        static Selection BlockSelection(VimEngine vim, VimRange r)
        {
            var (left, right) = vim.BlockColumns();
            return new Selection { AnchorLine = r.Start.Line, AnchorCol = left, HeadLine = r.End.Line, HeadCol = right, Mode = SelectionMode.Block };
        }
    }

    /// <summary>The search match vim's cursor is on gets the current-match outline.</summary>
    private void SyncSearchCurrent(TPane pane)
    {
        if (pane.Vim is not { } vim || !search.IsVim || !ReferenceEquals(search.Pane, pane)) return;
        var matches = search.Engine.Matches;
        int i = VimSearch.Next(matches, vim.Cursor, forward: true, inclusive: true);
        search.SetCurrent(i >= 0 && matches[i].StartLine == vim.Cursor.Line && matches[i].StartCol == vim.Cursor.Col ? matches[i] : null, Now);
    }

    private Host HostFor(TPane pane) => new(this, pane, window, search);

    // ==== Hints (quick select) ===================================================================

    /// <summary>A hint was picked: yank it (upper case), or move there. The window has already tried to open it (Ctrl / Enter).</summary>
    public void HintChosen(TPane pane, string text, VimPos? at, bool yank)
    {
        if (yank)
        {
            window.SetClipboard(text);
            window.SetPrimary(text);
            ToastCopied(text);
            return;
        }
        if (pane.Vim is not { } vim || at is not { } target) return;
        lock (gate)
        {
            vim.MoveTo(target);
            SyncSelection(pane);
        }
        window.Redraw();
    }

    private void ToastCopied(string text)
    {
        int chars = text.EnumerateRunes().Count();
        window.Toast(ToastKind.Success, "Copied", $"{chars} character{(chars == 1 ? "" : "s")}");
    }

    // ==== Actions ==============================================================================

    /// <summary>Execute hook: vim mode and the pane arrangement actions. <paramref name="edge"/> is MovePaneToEdge's argument, parsed.</summary>
    public void ExecuteAction(string name, string firstArg, Direction edge)
    {
        switch (name)
        {
            case CopyModeAction or "VimMode": Toggle(window.Focused); break;
            case "SwapPane": SwapPanes(firstArg.Equals("Previous", StringComparison.OrdinalIgnoreCase) ? -1 : 1); break;
            case "RotatePanes": RotatePanes(!firstArg.Equals("CounterClockwise", StringComparison.OrdinalIgnoreCase)); break;
            case "EqualizePanes": EqualizePanes(); break;
            case "MovePaneToNewTab": window.MovePaneToNewTab(window.Focused); break;
            case "MovePaneToEdge": MovePaneToEdge(edge); break;
            case "PaneLayout": ApplyPaneLayout(firstArg); break;
        }
    }

    private bool NeedPanes()
    {
        if (window.ShownPaneCount > 1) return true;
        window.Toast(ToastKind.Info, "Only one pane", "Split with Ctrl+Shift+\\ or Ctrl+Shift+-");
        return false;
    }

    private void LeaveZoom()
    {
        if (window.Zoomed is not null) window.SetZoom(null);
    }

    private void SwapPanes(int offset)
    {
        if (!NeedPanes()) return;
        LeaveZoom();
        window.Layout.SwapWithNext(window.Focused, offset);
        window.ApplyLayout(animate: true);
    }

    private void RotatePanes(bool clockwise)
    {
        if (!NeedPanes()) return;
        LeaveZoom();
        window.Layout.Rotate(clockwise);
        window.ApplyLayout(animate: true);
    }

    private void EqualizePanes()
    {
        if (!NeedPanes()) return;
        window.Layout.Equalize();
        window.ApplyLayout(animate: true);
    }

    private void MovePaneToEdge(Direction edge)
    {
        if (!NeedPanes()) return;
        LeaveZoom();
        window.Layout.MoveToEdge(window.Focused, edge);
        window.ApplyLayout(animate: true);
    }

    private void ApplyPaneLayout(string name)
    {
        if (name == "even") { EqualizePanes(); return; }
        if (LayoutPresets.Parse(name.Trim().ToLowerInvariant()) is not { } preset) return;
        if (!NeedPanes()) return;
        LeaveZoom();
        LayoutPresets.Apply(window.Layout, preset);
        window.ApplyLayout(animate: true);
    }

    // ==== Window commands (Ctrl-w) ===============================================================

    private void WindowCommand(char c, int count)
    {
        int n = count > 0 ? count : 1;
        if (FocusCommand(c, n) || ArrangeCommand(c, n)) return;
        window.Toast(ToastKind.Info, $"Ctrl-w {c}: not a window command", "g? shows the vim mode keys");
    }

    private bool FocusCommand(char c, int n)
    {
        switch (c)
        {
            case 'h' or 'j' or 'k' or 'l':
                window.ActivateDirection(c switch { 'h' => Direction.Left, 'j' => Direction.Down, 'k' => Direction.Up, _ => Direction.Right });
                return true;
            case 'w' or 'W' or 'p': CycleFocus(c, n); return true;
            case 's' or 'S' or 'n': window.Split(SplitAxis.Vertical, null); return true;
            case 'v': window.Split(SplitAxis.Horizontal, null); return true;
            case 'q' or 'c': window.RequestClosePane(window.Focused); return true;
            case 'o' or 'z':
                if (window.ShownPaneCount > 1) window.SetZoom(window.Zoomed is null ? window.Focused : null);
                return true;
            default: return false;
        }
    }

    private void CycleFocus(char c, int n)
    {
        var leaves = window.Layout.LeafList.Where(p => !p.LayoutHidden).ToList(); // not parked teammates (Teammates/WindowTeammates.cs)
        if (leaves.Count < 2) return;
        int i = leaves.IndexOf(window.Focused), d = c == 'W' ? -1 : 1;
        window.Focus(leaves[((i + d * n) % leaves.Count + leaves.Count) % leaves.Count]);
    }

    private bool ArrangeCommand(char c, int n)
    {
        switch (c)
        {
            case 'x': SwapPanes(1); return true;
            case 'r': RotatePanes(clockwise: true); return true;
            case 'R': RotatePanes(clockwise: false); return true;
            case '=': EqualizePanes(); return true;
            case 'T': window.MovePaneToNewTab(window.Focused); return true;
            case 'H': MovePaneToEdge(Direction.Left); return true;
            case 'J': MovePaneToEdge(Direction.Down); return true;
            case 'K': MovePaneToEdge(Direction.Up); return true;
            case 'L': MovePaneToEdge(Direction.Right); return true;
            case '<' or '>' or '+' or '-': ResizeFocused(c, n); return true;
            default: return false;
        }
    }

    private void ResizeFocused(char c, int n)
    {
        // Vim's sense: > and + grow the focused pane, < and - shrink it, by one cell (a count multiplies).
        if (!NeedPanes()) return;
        bool wide = c is '<' or '>';
        LeaveZoom();
        if (window.GrowFocused(wide ? SplitAxis.Horizontal : SplitAxis.Vertical, c is '>' or '+' ? n : -n))
            window.ApplyLayout(animate: true);
    }

    private void TabCommand(bool previous, int count, int index)
    {
        if (index > 0) { window.ActivateTab(Math.Min(index, window.TabCount) - 1); return; }
        if (window.TabCount < 2) return;
        int n = window.TabCount, d = previous ? -count : count;
        window.ActivateTab(((window.ActiveTabIndex + d) % n + n) % n);
    }

    // ==== Opening things ========================================================================

    private void OpenFromVim(TPane pane, VimOpen what, VimPos at)
    {
        switch (what)
        {
            case ErikTerm.Ui.VimMode.VimOpen.Url or ErikTerm.Ui.VimMode.VimOpen.File: OpenLinkAt(pane, what, at); break;
            case ErikTerm.Ui.VimMode.VimOpen.Output or ErikTerm.Ui.VimMode.VimOpen.Scrollback: OpenText(pane, what, at); break;
        }
    }

    private void OpenLinkAt(TPane pane, VimOpen what, VimPos at)
    {
        bool found;
        string uri;
        SmartLink? smart;
        lock (gate)
        {
            found = window.FindLinkAt(pane, at.Line, at.Col, out uri, out smart);
            // Nothing right under the cursor: the first link after it on the line.
            for (int col = VimBuffer.NextCol(pane.Term, at.Line, at.Col); !found && col >= 0; col = VimBuffer.NextCol(pane.Term, at.Line, col))
                found = window.FindLinkAt(pane, at.Line, col, out uri, out smart);
        }
        if (!found) { window.Toast(ToastKind.Info, what == ErikTerm.Ui.VimMode.VimOpen.Url ? "No link under the cursor" : "No file under the cursor"); return; }
        if (smart is { } s) window.OpenSmartLink(pane, s); // file:line in Neovim, commit, issue (WindowFeatures/WindowEditor.cs)
        else if (what == ErikTerm.Ui.VimMode.VimOpen.File) window.Toast(ToastKind.Info, "Not a file", uri);
        else window.OpenLink(uri);
    }

    private void OpenText(TPane pane, VimOpen what, VimPos at)
    {
        string text;
        long first;
        lock (gate)
        {
            var t = pane.Term;
            if (what == ErikTerm.Ui.VimMode.VimOpen.Output)
            {
                if (!t.HasPromptMarks || t.CommandAt(at.Line) is not { } block)
                {
                    window.Toast(ToastKind.Info, t.HasPromptMarks ? "No command output here" : "No prompts marked yet", "gO opens the whole scrollback");
                    return;
                }
                first = block.OutputStart;
                text = t.GetText(block.OutputStart, 0, block.OutputEnd, int.MaxValue / 2);
            }
            else
            {
                first = t.FirstLine;
                text = t.GetText(t.FirstLine, 0, VimBuffer.LastLine(t), int.MaxValue / 2);
            }
            // The cursor's line in the exported text (soft-wrapped rows were joined).
            at = new VimPos(VimFiles.CountLines(t, first, at.Line), 0);
        }
        string? file = WriteExport(text + "\n", what == ErikTerm.Ui.VimMode.VimOpen.Output ? "output" : "scrollback");
        if (file is null) return;
        OpenReadOnly(file, (int)Math.Max(1, at.Line));
    }

    /// <summary>
    /// Writes text for Neovim to a file only you can read ($XDG_RUNTIME_DIR/erikterm, else the temp directory; 0600).
    /// Old exports (over 6 hours) are removed on the way.
    /// </summary>
    private string? WriteExport(string text, string kind)
    {
        try
        {
            string root = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run && Directory.Exists(run) ? run : Path.GetTempPath();
            string dir = Path.Combine(root, "erikterm", "exports");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var old in Directory.EnumerateFiles(dir, "*.txt"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromHours(6)) File.Delete(old);
            string path = Path.Combine(dir, $"{kind}-{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}.txt");
            VimFiles.WritePrivate(path, text, overwrite: false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            window.Toast(ToastKind.Error, "Couldn't write the export", ex.Message);
            return null;
        }
    }

    /// <summary>Opens a file read-only at a line: in the tab's Neovim (:drop + readonly), else nvim -R in a split.</summary>
    private void OpenReadOnly(string path, int line)
    {
        string? editor = window.PreferredEditor();
        if (EditorCommands.IsNvim(editor) && window.RunningNvim() is { } target && ToolRunner.Find("nvim") is { } nvim)
        {
            var (pane, server) = target;
            string keys = EditorCommands.RemoteSendKeys(path, line, 1) + ":<C-U>setlocal readonly nomodifiable<CR>";
            string[] argv = [nvim, "--server", server, "--remote-send", keys];
            window.RunInBackground(() => ToolRunner.Run(argv, null, 3000) is { Ok: true } ? "ok" : "failed", r =>
            {
                if (r == "ok" && window.FindPane(pane.Id) is { } still && window.Layout.Contains(still)) window.Focus(still);
                else OpenReadOnlyInSplit(editor, path, line);
            });
            return;
        }
        OpenReadOnlyInSplit(editor, path, line);
    }

    private void OpenReadOnlyInSplit(string? editor, string path, int line)
    {
        var argv = EditorCommands.EditorArgv(editor ?? "nvim", path, line, 1).ToList();
        if (Path.GetFileName(argv[0]) is "nvim" or "vim" or "vi" or "view") argv.Insert(1, "-R");
        if (ToolRunner.Find(argv[0]) is null && !File.Exists(argv[0])) { window.Toast(ToastKind.Error, "Editor not found", argv[0]); return; }
        window.SplitRun(argv.ToArray());
    }

    // ==== Ex commands ==========================================================================

    private void Later(Action work) => _deferred.Add(work);

    /// <summary>Runs an ex command (validated now, carried out after the key is handled). Returns an error, or null.</summary>
    private string? ExecuteEx(TPane pane, VimEngine vim, ExCommand cmd)
    {
        switch (cmd.Name)
        {
            case "split" or "vsplit": return ExSplit(cmd);
            case "quit": Later(() => { if (cmd.Bang) window.ClosePane(pane); else window.RequestClosePane(pane); }); return null;
            case "only": return ExOnly(pane, cmd);
            case "tabnew": Later(window.SpawnTab); return null;
            case "tabnext": Later(() => TabCommand(false, 1, cmd.Number ?? 0)); return null;
            case "tabprevious": Later(() => TabCommand(true, Math.Max(1, cmd.Number ?? 1), 0)); return null;
            case "tabclose": Later(() => window.CloseActiveTab(cmd.Bang)); return null;
            case "theme": return ExTheme(cmd);
            case "set": return ExSet(cmd);
            case "write": return ExWrite(pane, cmd);
            case "send": return ExSend(pane, vim, cmd);
            case "layout": return ExLayout(cmd);
            case "action": return ExAction(cmd);
            default: return $"Not an editor command: {cmd.Name}";
        }
    }

    private string? ExSplit(ExCommand cmd)
    {
        var axis = cmd.Name == "split" ? SplitAxis.Vertical : SplitAxis.Horizontal;
        string[]? argv = cmd.Arg.Length > 0 ? [ErikTerm.Pty.Pty.DefaultShell(), "-c", cmd.Arg] : null;
        Later(() => window.Split(axis, argv));
        return null;
    }

    private string? ExOnly(TPane pane, ExCommand cmd)
    {
        var others = window.Layout.LeafList.Where(p => p != pane).ToList();
        int busy = others.Count(p => p.RunsProgram);
        if (busy > 0 && !cmd.Bang) return $"{busy} other pane{(busy == 1 ? " is" : "s are")} running a program (add ! to close anyway)";
        Later(() => { LeaveZoom(); foreach (var p in others) window.ClosePane(p); });
        return null;
    }

    private string? ExTheme(ExCommand cmd)
    {
        if (FindScheme(cmd.Arg) is not { } scheme) return $"Unknown theme: {cmd.Arg}";
        Later(() =>
        {
            _previewPane = null;
            process.SetSchemeOverride(scheme);
            window.SchemePreview = null;
            process.ApplySchemeToAllWindows();
            window.Toast(ToastKind.Success, $"Theme: {scheme.Name}", $"Set color_scheme = \"{scheme.Name}\" in the config to keep it");
        });
        return null;
    }

    private string? ExSet(ExCommand cmd)
    {
        if (!ConfigOverrides.TryParse(cmd.Arg, out var o, out string error)) return error;
        Later(() =>
        {
            process.AddSessionOverride(o!); // Host/ProcessConfig.cs: reloads, like --set
            window.Toast(ToastKind.Success, "Set for this session", cmd.Arg);
        });
        return null;
    }

    private string? ExWrite(TPane pane, ExCommand cmd)
    {
        string path;
        try { path = Path.GetFullPath(SettingsLoader.ExpandHome(cmd.Arg.Trim('"', '\''))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return $"Invalid file name: {cmd.Arg}"; }
        if (Directory.Exists(path)) return $"\"{cmd.Arg}\" is a directory";
        if (File.Exists(path) && !cmd.Bang) return "File exists (add ! to override)";
        if (Path.GetDirectoryName(path) is { } dir && !Directory.Exists(dir)) return $"No such directory: {dir}";
        var t = pane.Term;
        string text = t.GetText(t.FirstLine, 0, VimBuffer.LastLine(t), int.MaxValue / 2) + "\n";
        try { VimFiles.WritePrivate(path, text, overwrite: cmd.Bang); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
        int lines = text.Count(ch => ch == '\n');
        Later(() => window.Toast(ToastKind.Success, $"Wrote {lines} lines", FileRefResolver.ShortDisplay(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
        return null;
    }

    private string? ExSend(TPane pane, VimEngine vim, ExCommand cmd)
    {
        string text = vim.PromptSelection is { Length: > 0 } sel ? sel : vim.LastYank;
        if (text.Length == 0) return "Nothing to send: select (v) or yank (y) something first";
        if (FindSendTarget(cmd.Arg, pane) is not { } target) return $"No pane or agent matches '{cmd.Arg}'";
        Later(() =>
        {
            window.PasteText(target, text);
            int chars = text.EnumerateRunes().Count();
            window.Toast(ToastKind.Success, $"Sent to {DescribePane(target)}", $"{chars} character{(chars == 1 ? "" : "s")}");
        });
        return null;
    }

    private string? ExLayout(ExCommand cmd)
    {
        string name = cmd.Arg.Trim().ToLowerInvariant();
        if (name != "even" && LayoutPresets.Parse(name) is null) return $"Unknown layout: {cmd.Arg} (even-horizontal, even-vertical, main-vertical, main-horizontal, tiled)";
        Later(() => ApplyPaneLayout(name));
        return null;
    }

    private string? ExAction(ExCommand cmd)
    {
        if (!ActionSpec.TryParse(cmd.Arg, out var action, out string error)) return error;
        Later(() => window.Execute(action, 0, 0));
        return null;
    }

    private ColorScheme? FindScheme(string name) => VimSchemes.Find(process.AllSchemes(), name);

    /// <summary>:theme as it's typed: the theme it would pick shows right away; Esc goes back.</summary>
    private void PromptChanged(TPane pane, string text)
    {
        var cmd = text.TrimStart(':', ' ');
        int space = cmd.IndexOf(' ');
        string verb = space < 0 ? cmd : cmd[..space];
        string arg = space < 0 ? "" : cmd[(space + 1)..].Trim();
        if (Ex.Find(verb)?.Name == "theme" && arg.Length > 0 && FindScheme(arg) is { } scheme)
        {
            if (window.SchemePreview == scheme) return;
            _previewPane = pane;
            _deferred.Add(() => { window.SchemePreview = scheme; window.ApplyScheme(); });
        }
        else if (_previewPane is not null) _deferred.Add(RevertThemePreview);
    }

    private void RevertThemePreview()
    {
        _previewPane = null;
        if (window.SchemePreview is null) return;
        window.SchemePreview = null;
        window.ApplyScheme();
    }

    /// <summary>:send's target: a pane id, or a pane whose agent / program / title contains the text (this window first).</summary>
    private TPane? FindSendTarget(string spec, TPane self)
    {
        spec = spec.Trim().TrimStart('%');
        var panes = new List<TPane>();
        for (int i = 0; i < window.TabCount; i++) panes.AddRange(window.TabLayout(i).LeafList);
        if (int.TryParse(spec, out int id)) return panes.FirstOrDefault(p => p.Id == id) ?? window.FindPane(id);
        bool Has(string? s) => s is not null && s.Contains(spec, StringComparison.OrdinalIgnoreCase);
        return panes.FirstOrDefault(p => p != self && Has(p.AgentName))
            ?? panes.FirstOrDefault(p => p != self && (Has(p.Label) || Has(p.TmuxTitle)));
    }

    private static string DescribePane(TPane p) => p.AgentName is { } agent ? $"{agent} (pane {p.Id})" : $"pane {p.Id} · {p.Label}";

    private IEnumerable<string> VimArguments(string command, TPane self) => command switch
    {
        "theme" => process.AllSchemes().Select(s => s.Name),
        "layout" => VimCompletionLists.LayoutNames,
        "set" => VimCompletionLists.SettingKeys.Select(k => k + "="),
        "send" => SendCandidates(self),
        _ => [],
    };

    private IEnumerable<string> SendCandidates(TPane self)
    {
        for (int i = 0; i < window.TabCount; i++)
            foreach (var p in window.TabLayout(i).LeafList.Where(leaf => leaf != self))
                yield return p.AgentName is { } agent ? agent.ToLowerInvariant() : p.Id.ToString();
    }

    // ==== IPC and the palette =====================================================================

    /// <summary>IPC hook: the pane arrangement commands vim mode's Ctrl-w uses, for <c>erikterm cli</c> (swap-pane, rotate-panes, move-pane-to-new-tab; select-layout is the tmux emulation's).</summary>
    public IpcResponse? HandleIpc(IpcRequest r) => r.Command switch
    {
        "swap-pane" => IpcSwapPane(r),
        "rotate-panes" => IpcRotatePanes(r),
        "move-pane-to-new-tab" => IpcMovePaneToNewTab(r),
        _ => null,
    };

    private IpcResponse IpcSwapPane(IpcRequest r)
    {
        var (tab, pane) = window.IpcTarget(r);
        bool active = tab == window.ActiveTabIndex;
        var layout = window.TabLayout(tab);
        if (layout.Count < 2) throw new IpcException("only one pane in the tab");
        window.ClearTabZoom(tab);
        if (r.OtherPaneId is { } other)
        {
            var with = layout.LeafList.FirstOrDefault(p => p.Id == other) ?? throw new IpcException($"pane {other} is not in the same tab");
            if (!layout.Swap(pane, with)) throw new IpcException("can't swap a pane with itself");
        }
        else layout.SwapWithNext(pane, r.Direction == "Previous" ? -1 : 1);
        window.ApplyLayout(animate: active);
        return IpcResponse.Success();
    }

    private IpcResponse IpcRotatePanes(IpcRequest r)
    {
        int tab = window.IpcTab(r);
        bool active = tab == window.ActiveTabIndex;
        var layout = window.TabLayout(tab);
        if (layout.Count < 2) throw new IpcException("only one pane in the tab");
        window.ClearTabZoom(tab);
        layout.Rotate(clockwise: r.Direction != "CounterClockwise");
        window.ApplyLayout(animate: active);
        return IpcResponse.Success();
    }

    private IpcResponse IpcMovePaneToNewTab(IpcRequest r)
    {
        var (tab, pane) = window.IpcTarget(r);
        if (window.TabLayout(tab).Count < 2) throw new IpcException("the pane is already alone in its tab");
        if (tab != window.ActiveTabIndex) window.ActivateTab(tab);
        if (!window.MovePaneToNewTab(pane)) throw new IpcException("couldn't move the pane");
        return new IpcResponse { Ok = true, PaneId = pane.Id };
    }

    /// <summary>Command-palette hook.</summary>
    public void AddCommands(Action<string, string, int, string, Func<bool>?> add)
    {
        bool multi() => window.ShownPaneCount > 1;
        add("Edit", window.Focused.Vim is null ? "Vim Mode (Copy Mode)" : "Leave Vim Mode", Icons.Keyboard, CopyModeAction, null);
        add("Pane", "Swap with Next Pane", Icons.Layout, "SwapPane", multi);
        add("Pane", "Swap with Previous Pane", Icons.Layout, "SwapPane(Previous)", multi);
        add("Pane", "Rotate Panes Clockwise", Icons.Refresh, "RotatePanes(Clockwise)", multi);
        add("Pane", "Rotate Panes Counter-Clockwise", Icons.Refresh, "RotatePanes(CounterClockwise)", multi);
        add("Pane", "Equalize Pane Sizes", Icons.Layout, "EqualizePanes", multi);
        add("Pane", "Move Pane to New Tab", Icons.Add, "MovePaneToNewTab", multi);
        add("Pane", "Move Pane to the Left Edge", Icons.ArrowLeft, "MovePaneToEdge(Left)", multi);
        add("Pane", "Move Pane to the Right Edge", Icons.ArrowRight, "MovePaneToEdge(Right)", multi);
        add("Pane", "Move Pane to the Top", Icons.ArrowUp, "MovePaneToEdge(Up)", multi);
        add("Pane", "Move Pane to the Bottom", Icons.ArrowDown, "MovePaneToEdge(Down)", multi);
        add(LayoutCategory, "Even Horizontal (side by side)", Icons.SplitRight, "PaneLayout(even-horizontal)", multi);
        add(LayoutCategory, "Even Vertical (stacked)", Icons.SplitDown, "PaneLayout(even-vertical)", multi);
        add(LayoutCategory, "Main Vertical (one big pane left)", Icons.Layout, "PaneLayout(main-vertical)", multi);
        add(LayoutCategory, "Main Horizontal (one big pane on top)", Icons.Layout, "PaneLayout(main-horizontal)", multi);
        add(LayoutCategory, "Tiled (grid)", Icons.Layout, "PaneLayout(tiled)", multi);
    }

    // ==== The host ===============================================================================

    /// <summary>What the engine asks for, for one pane (called with the terminal locked: slow work is deferred).</summary>
    private sealed class Host(WindowVim<TPane> w, TPane pane, IVimWindow<TPane> window, IVimSearch<TPane> search) : IVimHost
    {
        public void Yank(string text)
        {
            if (text.Length == 0) return;
            w._deferred.Add(() =>
            {
                window.SetClipboard(text);
                window.SetPrimary(text);
                w.ToastCopied(text);
            });
        }

        public void Leave(bool toLive) => w._deferred.Add(() => w.Leave(pane, toLive));

        public void Message(string text, bool error) => w._deferred.Add(() => window.Toast(error ? ToastKind.Error : ToastKind.Info, text));

        public void SearchChanged(string pattern) => search.BeginVim(pane, pattern, window.Theme, Now);

        public void SearchCancelled(string? previous)
        {
            if (previous is not null) search.BeginVim(pane, previous, window.Theme, Now);
            else search.EndVim(Now);
        }

        public VimPos? FindMatch(VimPos from, bool forward)
        {
            var s = search;
            if (!s.IsVim || !ReferenceEquals(s.Pane, pane)) return null;
            s.CompleteScan(pane.Term);
            var matches = s.Engine.Matches;
            int i = VimSearch.Next(matches, from, forward);
            return i < 0 ? null : new VimPos(matches[i].StartLine, matches[i].StartCol);
        }

        public void ClearHighlight() => search.EndVim(Now);

        public void PromptChanged(string text) => w.PromptChanged(pane, text);
        public void PromptCancelled() => w._deferred.Add(w.RevertThemePreview);

        public string? Execute(ExCommand command) => w.ExecuteEx(pane, pane.Vim!, command);

        public IEnumerable<string> ActionNames => ActionSpec.Known.Keys.Where(k => k != "None");
        public IEnumerable<string> Arguments(string command) => w.VimArguments(command, pane);

        public void Open(VimOpen what, VimPos at) => w._deferred.Add(() => w.OpenFromVim(pane, what, at));
        public void Hints() => w._deferred.Add(() => window.OpenHints(Now));
        public void Window(char command, int count) => w._deferred.Add(() => w.WindowCommand(command, count));
        public void Tab(bool previous, int count, int index) => w._deferred.Add(() => w.TabCommand(previous, count, index));

        public void Help() => w._deferred.Add(() =>
        {
            w._help.EnterKey = window.ShortcutText(CopyModeAction) ?? CopyModeAction;
            w._help.Open(window.Theme, Now);
        });
    }
}

/// <summary>Completion lists of vim mode's <c>:</c> commands; outside the generic class so there is one instance of each.</summary>
internal static class VimCompletionLists
{
    public static readonly string[] SettingKeys =
    [
        "font_size", "line_height", "font", "color_scheme", "window_background_opacity", "text_background_opacity",
        "default_cursor_style", "cursor_blink_rate", "cursor_blink_style", "cursor_thickness", "cursor_height", "cursor_animation", "smooth_scrolling", "window_padding", "window_controls", "chrome_reveal", "window_blur",
        "scrollback_lines", "inactive_pane_hsb", "visual_bell", "animation_duration_ms", "enable_tab_bar",
        "hide_tab_bar_if_only_one_tab", "ctrl_tab_order", "tab_switcher_double_ctrl", "bold_brightens_ansi_colors", "harfbuzz_features", "git_status", "github_status",
        "editor_open", "agent_status", "agent_notifications", "notification_bar_scope", "tmux_emulation", "command_palette_font_size",
        "minimum_contrast", "exit_behavior", "shell_integration", "notify_command_duration", "remember_commands", "snippets", "git_worktree_dir", "git_worktree_setup", "worktree_open_in", "git_conflict_check",
    ];

    public static readonly string[] LayoutNames = ["even-horizontal", "even-vertical", "main-vertical", "main-horizontal", "tiled", "even"];
}
