using ErikTerm.Core;

namespace ErikTerm.Ui.VimMode;

public enum VimMode { Normal, Visual, VisualLine, VisualBlock }

public enum VimPromptKind { None, SearchForward, SearchBackward, Ex }

/// <summary>What gx / gf / go / gO open.</summary>
public enum VimOpen { Url, File, Output, Scrollback }

/// <summary>
/// What vim mode asks of the window: the clipboard, search highlights, the ex commands, opening things, panes and tabs.
/// The engine calls it with the terminal locked; implementations defer anything slow (spawning, layout) until after.
/// </summary>
public interface IVimHost
{
    /// <summary>y and friends: <paramref name="text"/> goes to the clipboard and the primary selection.</summary>
    void Yank(string text);
    /// <summary>Leave vim mode; <paramref name="toLive"/> (i, a) also scrolls back to the prompt.</summary>
    void Leave(bool toLive);
    /// <summary>A short message (vim's message line): "Pattern not found", "Mark not set".</summary>
    void Message(string text, bool error);

    /// <summary>The search pattern changed (as / is typed, accepted, or n / * used it): highlight its matches.</summary>
    void SearchChanged(string pattern);
    /// <summary>The / prompt was cancelled: back to the previous pattern's highlights (or none).</summary>
    void SearchCancelled(string? previous);
    /// <summary>The next match from <paramref name="from"/> for the current pattern (see VimSearch.Next), or null.</summary>
    VimPos? FindMatch(VimPos from, bool forward);
    /// <summary>:noh.</summary>
    void ClearHighlight();

    /// <summary>The ':' line changed (:theme previews), or was cancelled.</summary>
    void PromptChanged(string text);
    void PromptCancelled();
    /// <summary>Runs an ex command (not :N, :noh, :help); returns an error to show in the prompt, or null.</summary>
    string? Execute(ExCommand command);
    /// <summary>erikterm action names (they work as ex commands).</summary>
    IEnumerable<string> ActionNames { get; }
    /// <summary>Completion candidates for a command's argument (theme names, layouts, panes, setting keys).</summary>
    IEnumerable<string> Arguments(string command);

    void Open(VimOpen what, VimPos at);
    /// <summary>; and gh: label links, paths and hashes on screen (quick select).</summary>
    void Hints();
    /// <summary>Ctrl-w <paramref name="command"/> with a count (0: none).</summary>
    void Window(char command, int count);
    /// <summary>gt / gT; <paramref name="index"/> is 1-based for {n}gt, else 0.</summary>
    void Tab(bool previous, int count, int index);
    void Help();
}

/// <summary>
/// Vim mode for one pane: a cursor over the scrollback and screen (absolute lines, so output that keeps arriving doesn't
/// move it), visual selections, yanks, search, marks, the ':' command line and the window commands. Pure logic over
/// <see cref="Terminal"/>; the app draws it and does what <see cref="IVimHost"/> asks. Call with the terminal locked.
/// </summary>
public sealed class VimEngine
{
    private readonly Terminal _t;
    private readonly VimKeyParser _parser = new();
    private readonly Dictionary<char, VimPos> _marks = [];
    private int _want;                       // the column j/k aim for (int.MaxValue after $)
    private (char Kind, char Char)? _lastFind;
    private bool _findChain;                 // the last command was f/t/;/, — so ; repeats instead of showing hints
    private VimPos? _lastJump;

    // Prompt state.
    private VimPos _promptOrigin;
    private long _promptOriginTop;
    private string? _searchBefore;
    private readonly List<string> _searchHistory = [], _exHistory = [];
    private int _historyIndex = -1;
    private List<string>? _cycle;            // Tab completion: the candidates being cycled
    private int _cycleIndex = -1;

    public VimEngine(Terminal t, VimPos cursor)
    {
        _t = t;
        Cursor = cursor;
        _want = cursor.Col;
    }

    public Terminal Term => _t;
    public VimMode Mode { get; private set; }
    public VimPos Cursor { get; private set; }
    /// <summary>The other end of a visual selection.</summary>
    public VimPos Anchor { get; private set; }
    public bool InVisual => Mode != VimMode.Normal;
    /// <summary>Keys of a command being typed (shown next to the mode).</summary>
    public string Pending => _parser.Pending;

    public VimPromptKind Prompt { get; private set; }
    public string PromptText { get; private set; } = "";
    /// <summary>An error from the last Enter on the ':' line (shown in the prompt until the next edit).</summary>
    public string? PromptError { get; private set; }
    /// <summary>Completions for the ':' line as typed, best first.</summary>
    public IReadOnlyList<string> Suggestions { get; private set; } = [];
    /// <summary>The suggestion Tab has put on the line, or -1.</summary>
    public int SuggestionIndex { get; private set; } = -1;

    public string LastSearch { get; private set; } = "";
    public bool LastSearchForward { get; private set; } = true;
    /// <summary>The last yank (:send sends it when there's no selection).</summary>
    public string LastYank { get; private set; } = "";
    /// <summary>The visual selection when ':' was pressed (:send).</summary>
    public string? PromptSelection { get; private set; }
    /// <summary>Bumped on every change worth a frame (cursor, mode, prompt).</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Vim mode starting where the user is looking: at the terminal cursor when it's in view, else the bottom line of the
    /// view (scrolled back).
    /// </summary>
    public static VimEngine Enter(Terminal t)
    {
        long cursorLine = t.ScreenTopLine + t.CursorY;
        long top = t.ViewTopLine, bottom = top + t.Rows - 1;
        VimPos p = cursorLine >= top && cursorLine <= bottom
            ? new VimPos(cursorLine, VimMotions.ClampCol(t, cursorLine, t.CursorX, free: false))
            : VimMotions.FirstNonBlank(t, Math.Min(bottom, VimBuffer.LastLine(t)));
        return new VimEngine(t, VimBuffer.ClampLine(t, p));
    }

    /// <summary>Starts in visual mode over an existing (mouse) selection.</summary>
    public void BeginVisual(VimPos anchor, VimPos cursor, VimMode mode)
    {
        Mode = mode;
        Anchor = anchor;
        Cursor = cursor;
        _want = cursor.Col;
        Version++;
    }

    // ==== Input ================================================================================

    /// <summary>Feeds one key in vim notation (see <see cref="VimKeys"/>).</summary>
    public void Feed(string key, IVimHost host)
    {
        Version++;
        if (Prompt != VimPromptKind.None) { PromptKey(key, host); return; }
        Clamp();
        switch (_parser.Feed(key, InVisual, out var cmd))
        {
            case VimParse.Complete: Run(cmd, host); break;
            case VimParse.Invalid: _findChain = false; break;
        }
    }

    public void FeedKeys(string notation, IVimHost host)
    {
        foreach (var key in VimKeys.Parse(notation)) Feed(key, host);
    }

    /// <summary>Moves the cursor (a hint was picked, a click). Visual mode extends to it.</summary>
    public void MoveTo(VimPos p)
    {
        p = VimBuffer.ClampLine(_t, p);
        Cursor = ClampBlock(p with { Col = VimMotions.ClampCol(_t, p.Line, p.Col, free: Mode == VimMode.VisualBlock) });
        _want = Cursor.Col;
        EnsureVisible();
        Version++;
    }

    /// <summary>A click: back to normal mode (from visual), the cursor on the clicked cell (clamped to the line's text).</summary>
    public void Click(VimPos p)
    {
        if (InVisual) SetMode(VimMode.Normal);
        MoveTo(p);
    }

    /// <summary>Lines dropped off the top of the scrollback: positions there move to the first line; marks there are gone.</summary>
    public void Clamp()
    {
        long first = _t.FirstLine, last = Math.Max(first, _t.EndLine - 1);
        if (Cursor.Line < first || Cursor.Line > last) { Cursor = new VimPos(Math.Clamp(Cursor.Line, first, last), 0); Version++; }
        if (Anchor.Line < first || Anchor.Line > last) Anchor = new VimPos(Math.Clamp(Anchor.Line, first, last), 0);
        if (_lastJump is { } j && j.Line < first) _lastJump = null;
        if (_marks.Count > 0)
            foreach (var (k, v) in _marks)
                if (v.Line < first) _marks.Remove(k);
    }

    /// <summary>The terminal reflowed (absolute lines were renumbered): start over at the terminal cursor.</summary>
    public void OnReflow()
    {
        var fresh = Enter(_t);
        Cursor = fresh.Cursor;
        _want = Cursor.Col;
        Mode = VimMode.Normal;
        Prompt = VimPromptKind.None;
        PromptText = "";
        _marks.Clear();
        _lastJump = null;
        _parser.Reset();
        Version++;
    }

    public IReadOnlyDictionary<char, VimPos> Marks => _marks;

    /// <summary>The marks' absolute lines, for the scrollbar's ticks (no allocation). Returns how many.</summary>
    public int CopyMarkLines(Span<long> into)
    {
        int n = 0;
        foreach (var mark in _marks)
        {
            if (n == into.Length) break;
            into[n++] = mark.Value.Line;
        }
        return n;
    }

    // ==== Commands =============================================================================

    private enum Kind { Exclusive, Inclusive, Linewise }

    private void Run(VimCommand cmd, IVimHost host)
    {
        string key = KeyAlias(cmd.Key);
        bool wasFindChain = _findChain;
        _findChain = key is "f" or "F" or "t" or "T" or ";" or ",";

        if (Motion(cmd, key, wasFindChain) is { } motion)
        {
            ApplyMotion(cmd, key, motion, host);
            return;
        }

        // Each group answers whether the key was one of its own; a key none of them knows does nothing.
        _ = RunModeCommand(key, host)
            || RunYankCommand(cmd, key, host)
            || RunTextObject(cmd, key, host)
            || RunSearchCommand(cmd, key, host)
            || RunHostCommand(cmd, key, host, wasFindChain);
    }

    private static string KeyAlias(string key) => key switch
    {
        VimKeys.Left or "<BS>" or "<C-h>" => "h",
        VimKeys.Right or " " => "l",
        VimKeys.Down or "<C-n>" or "<C-j>" => "j",
        VimKeys.Up or "<C-p>" => "k",
        VimKeys.Enter or "+" => "+",
        VimKeys.Home => "0",
        VimKeys.End => "$",
        VimKeys.PageUp => "<C-b>",
        VimKeys.PageDown => "<C-f>",
        _ => key,
    };

    private void ApplyMotion(VimCommand cmd, string key, (VimPos? Target, Kind Kind, bool Jump) motion, IVimHost host)
    {
        var (target, kind, jump) = motion;
        if (target is not { } to)
        {
            if (key is "'" or "`") host.Message("Mark not set", error: true);
            else if (key is "[[" or "]]" && !_t.HasPromptMarks) host.Message("No prompts marked yet (shell integration)", error: true);
            return;
        }
        if (cmd.Operator == 'y')
        {
            YankMotion(key, to, kind, host);
            return;
        }
        if (jump) _lastJump = Cursor;
        Cursor = ClampBlock(to);
        if (key == "$") _want = int.MaxValue;
        else if (key is not ("j" or "k" or "+" or "-" or "_" or "<C-e>" or "<C-y>")) _want = Cursor.Col;
        EnsureVisible();
    }

    private bool RunModeCommand(string key, IVimHost host)
    {
        switch (key)
        {
            case VimKeys.Esc:
                if (InVisual) SetMode(VimMode.Normal);
                else host.Leave(toLive: false);
                return true;
            case "q": host.Leave(toLive: false); return true;
            case "i" or "a" or "I" or "A": host.Leave(toLive: true); return true;
            case "v": ToggleVisual(VimMode.Visual); return true;
            case "V": ToggleVisual(VimMode.VisualLine); return true;
            case "<C-v>" or "<C-q>": ToggleVisual(VimMode.VisualBlock); return true;
            case "o" or "O" when InVisual:
                (Anchor, Cursor) = (Cursor, Anchor);
                _want = Cursor.Col;
                EnsureVisible();
                return true;
        }
        return false;
    }

    private bool RunYankCommand(VimCommand cmd, string key, IVimHost host)
    {
        switch (key)
        {
            case "y" or "<C-c>" when InVisual: YankSelection(host, lines: false); return true;
            case "Y" when InVisual: YankSelection(host, lines: true); return true;
            case "Y" or "yy":
            {
                long end = Math.Min(Cursor.Line + cmd.N - 1, VimBuffer.LastLine(_t));
                Yank(new VimRange(new VimPos(Cursor.Line, 0), new VimPos(end, 0), Linewise: true), host);
                return true;
            }
            case "yo" or "yc": YankCommandBlock(key, host); return true;
        }
        return false;
    }

    /// <summary>yo yanks the output of the command under the cursor, yc its command line.</summary>
    private void YankCommandBlock(string key, IVimHost host)
    {
        if (!_t.HasPromptMarks || _t.CommandAt(Cursor.Line) is not { } block)
        {
            host.Message(_t.HasPromptMarks ? "No command output here" : "No prompts marked (shell integration)", error: true);
            return;
        }
        if (key == "yo") Yank(new VimRange(new VimPos(block.OutputStart, 0), new VimPos(block.OutputEnd, 0), Linewise: true), host);
        else if (block.Command.Length > 0) { LastYank = block.Command; host.Yank(block.Command); }
        else host.Message("The shell didn't report this command line", error: true);
    }

    private bool RunTextObject(VimCommand cmd, string key, IVimHost host)
    {
        if (key is not ("iw" or "aw" or "iW" or "aW" or "ip" or "ap" or "is" or "as")) return false;
        var range = key[1] == 'p' || key[1] == 's'
            ? VimTextObjects.Paragraph(_t, Cursor, around: key[0] == 'a')
            : VimTextObjects.Word(_t, Cursor, around: key[0] == 'a', big: key[1] == 'W');
        if (cmd.Operator == 'y') { Yank(range, host); return true; }
        if (InVisual)
        {
            if (range.Linewise) Mode = VimMode.VisualLine;
            else if (Mode == VimMode.VisualLine) Mode = VimMode.Visual;
            Anchor = range.Start;
            Cursor = range.Linewise ? new VimPos(range.End.Line, VimMotions.ClampCol(_t, range.End.Line, Cursor.Col, false)) : range.End;
            _want = Cursor.Col;
            EnsureVisible();
        }
        return true;
    }

    private bool RunSearchCommand(VimCommand cmd, string key, IVimHost host)
    {
        switch (key)
        {
            case "/" or "?":
                OpenPrompt(key == "/" ? VimPromptKind.SearchForward : VimPromptKind.SearchBackward);
                return true;
            case ":":
                PromptSelection = InVisual ? SelectionText() : null;
                if (InVisual) SetMode(VimMode.Normal);
                OpenPrompt(VimPromptKind.Ex);
                Suggestions = Ex.Complete("", host.ActionNames, host.Arguments, 8);
                return true;
            case "n" or "N": SearchNext(host, key == "n" ? LastSearchForward : !LastSearchForward, cmd.N); return true;
            case "*" or "#" or "g*" or "g#": SearchWordUnderCursor(cmd, key, host); return true;
        }
        return false;
    }

    private void SearchWordUnderCursor(VimCommand cmd, string key, IVimHost host)
    {
        var w = VimTextObjects.Word(_t, Cursor, around: false, big: false);
        string word = _t.GetText(w.Start.Line, w.Start.Col, w.End.Line, w.End.Col).Trim();
        if (word.Length == 0) { host.Message("No string under cursor", error: true); return; }
        LastSearch = VimSearch.WordPattern(word);
        LastSearchForward = key[^1] == '*';
        // Start from the word's beginning so * doesn't stop on the word itself.
        Cursor = w.Start;
        SearchNext(host, LastSearchForward, cmd.N);
    }

    private bool RunHostCommand(VimCommand cmd, string key, IVimHost host, bool wasFindChain)
    {
        switch (key)
        {
            case ";" when !wasFindChain || _lastFind is null:
                _findChain = false;
                host.Hints();
                return true;
            case "gh": host.Hints(); return true;
            case "gx": host.Open(VimOpen.Url, Cursor); return true;
            case "gf": host.Open(VimOpen.File, Cursor); return true;
            case "go": host.Open(VimOpen.Output, Cursor); return true;
            case "gO": host.Open(VimOpen.Scrollback, Cursor); return true;
            case "g?": host.Help(); return true;
            case "gt": host.Tab(previous: false, cmd.N, cmd.HasCount ? cmd.Count : 0); return true;
            case "gT": host.Tab(previous: true, cmd.N, 0); return true;
            case "<C-w>": host.Window(cmd.Arg, cmd.Count); return true;
            case "m":
                if (cmd.Arg is >= 'a' and <= 'z' or >= 'A' and <= 'Z') _marks[cmd.Arg] = Cursor;
                else host.Message("Invalid mark name", error: true);
                return true;
            case "zz" or "zt" or "zb":
                _t.ScrollViewToLine(key switch
                {
                    "zt" => Cursor.Line,
                    "zb" => Cursor.Line - _t.Rows + 1,
                    _ => Cursor.Line - (_t.Rows - 1) / 2,
                });
                return true;
            case "<C-l>": return true; // redraw: nothing to do
        }
        return false;
    }

    private void ToggleVisual(VimMode mode)
    {
        if (Mode == mode) { SetMode(VimMode.Normal); return; }
        if (Mode == VimMode.Normal) Anchor = Cursor;
        SetMode(mode);
    }

    /// <summary>Changes mode; outside block mode the cursor goes back onto the line's text (vim's virtualedit=block).</summary>
    private void SetMode(VimMode mode)
    {
        Mode = mode;
        if (mode == VimMode.VisualBlock) return;
        int col = VimMotions.ClampCol(_t, Cursor.Line, Cursor.Col, free: false);
        if (col != Cursor.Col) { Cursor = Cursor with { Col = col }; _want = col; }
    }

    /// <summary>
    /// Block mode lets the cursor past the end of a short line (vim's virtualedit=block), so a rectangle can take in a
    /// column of a table with ragged rows — but no further than the longest line the block spans: past that there is
    /// nothing to select. Other modes clamp to the line's own text in the motions.
    /// </summary>
    private VimPos ClampBlock(VimPos p)
    {
        if (Mode != VimMode.VisualBlock) return p;
        long from = Math.Min(Anchor.Line, p.Line), to = Math.Max(Anchor.Line, p.Line);
        int max = Anchor.Col;
        for (long line = from; line <= to && max < p.Col; line++) max = Math.Max(max, VimBuffer.LastContentCol(_t, line));
        return p.Col <= max ? p : p with { Col = VimBuffer.Lead(_t, p.Line, max) };
    }

    /// <summary>The target of a motion, how an operator takes it, and whether it's a jump ('' returns from it). Null: not a motion.</summary>
    private (VimPos? Target, Kind Kind, bool Jump)? Motion(VimCommand cmd, string key, bool findChain) =>
        ColumnMotion(cmd, key)
        ?? VerticalMotion(cmd, key)
        ?? WordMotion(cmd, key)
        ?? JumpMotion(cmd, key)
        ?? FindMotion(cmd, key, findChain)
        ?? MarkMotion(cmd, key)
        ?? ScrollMotion(cmd, key);

    private (VimPos? Target, Kind Kind, bool Jump)? ColumnMotion(VimCommand cmd, string key)
    {
        var t = _t;
        var p = Cursor;
        int n = cmd.N;
        switch (key)
        {
            case "h":
            {
                int col = p.Col;
                for (int i = 0; i < n && VimBuffer.PrevCol(t, p.Line, col) is >= 0 and var x; i++) col = x;
                return (p with { Col = col }, Kind.Exclusive, false);
            }
            case "l":
            {
                int col = p.Col, max = VimMotions.ClampCol(t, p.Line, int.MaxValue, free: Mode == VimMode.VisualBlock);
                for (int i = 0; i < n && VimBuffer.NextCol(t, p.Line, col) is >= 0 and var x && x <= max; i++) col = x;
                return (p with { Col = col }, Kind.Exclusive, false);
            }
            case "0": return (p with { Col = 0 }, Kind.Exclusive, false);
            case "^": return (VimMotions.FirstNonBlank(t, p.Line), Kind.Exclusive, false);
            case "$" or "g_":
            {
                long line = Math.Min(p.Line + n - 1, VimBuffer.LastLine(t));
                return (VimMotions.LineEnd(t, line), Kind.Inclusive, false);
            }
        }
        return null;
    }

    private (VimPos? Target, Kind Kind, bool Jump)? VerticalMotion(VimCommand cmd, string key)
    {
        var t = _t;
        var p = Cursor;
        int n = cmd.N;
        bool free = Mode == VimMode.VisualBlock;
        switch (key)
        {
            case "j" or "+": return (Line(VimMotions.Vertical(t, p, n, _want, free), key == "+"), Kind.Linewise, false);
            case "k" or "-": return (Line(VimMotions.Vertical(t, p, -n, _want, free), key == "-"), Kind.Linewise, false);
            case "_": return (VimMotions.FirstNonBlank(t, Math.Min(p.Line + n - 1, VimBuffer.LastLine(t))), Kind.Linewise, false);
        }
        return null;

        VimPos Line(VimPos to, bool firstNonBlank) => firstNonBlank ? VimMotions.FirstNonBlank(_t, to.Line) : to;
    }

    private (VimPos? Target, Kind Kind, bool Jump)? WordMotion(VimCommand cmd, string key)
    {
        var t = _t;
        var p = Cursor;
        int n = cmd.N;
        switch (key)
        {
            case "w" or "W": return (VimMotions.WordForward(t, p, n, key == "W"), Kind.Exclusive, false);
            case "b" or "B": return (VimMotions.WordBackward(t, p, n, key == "B"), Kind.Exclusive, false);
            case "e" or "E": return (VimMotions.WordEnd(t, p, n, key == "E"), Kind.Inclusive, false);
            case "ge" or "gE": return (VimMotions.WordEndBackward(t, p, n, key == "gE"), Kind.Inclusive, false);
        }
        return null;
    }

    private (VimPos? Target, Kind Kind, bool Jump)? JumpMotion(VimCommand cmd, string key)
    {
        if (key is not ("gg" or "G" or "H" or "L" or "M" or "%" or "{" or "}" or "[[" or "]]")) return null;
        var t = _t;
        var p = Cursor;
        int n = cmd.N;
        long top = t.ViewTopLine, last = VimBuffer.LastLine(t);
        long bottom = Math.Min(top + t.Rows - 1, last);
        switch (key)
        {
            case "gg" or "G":
            {
                long withoutCount = key == "gg" ? t.FirstLine : last;
                long line = cmd.HasCount ? t.FirstLine + cmd.Count - 1 : withoutCount;
                line = Math.Clamp(line, t.FirstLine, last);
                return (VimMotions.FirstNonBlank(t, line), Kind.Linewise, true);
            }
            case "H": return (VimMotions.FirstNonBlank(t, Math.Clamp(top + n - 1, top, bottom)), Kind.Linewise, true);
            case "L": return (VimMotions.FirstNonBlank(t, Math.Clamp(bottom - n + 1, top, bottom)), Kind.Linewise, true);
            case "M": return (VimMotions.FirstNonBlank(t, top + (bottom - top) / 2), Kind.Linewise, true);
            case "%": return (VimMotions.MatchBracket(t, p), Kind.Inclusive, true);
            case "{" or "}": return (VimMotions.Block(t, p, key == "}" ? 1 : -1, n), Kind.Exclusive, true);
            default: return (VimMotions.Prompt(t, p, key == "]]" ? 1 : -1, n), Kind.Linewise, true);
        }
    }

    private (VimPos? Target, Kind Kind, bool Jump)? FindMotion(VimCommand cmd, string key, bool findChain)
    {
        var t = _t;
        var p = Cursor;
        int n = cmd.N;
        switch (key)
        {
            case "f" or "F" or "t" or "T":
            {
                _lastFind = (key[0], cmd.Arg);
                var to = VimMotions.FindChar(t, p, key[0], cmd.Arg, n);
                return (to, key is "f" or "t" ? Kind.Inclusive : Kind.Exclusive, false);
            }
            case ";" or "," when findChain && _lastFind is { } lf:
            {
                char kind = key == ";" ? lf.Kind : lf.Kind switch { 'f' => 'F', 'F' => 'f', 't' => 'T', _ => 't' };
                var to = VimMotions.FindChar(t, p, kind, lf.Char, n, repeat: true);
                return (to, kind is 'f' or 't' ? Kind.Inclusive : Kind.Exclusive, false);
            }
            case ",": return (null, Kind.Exclusive, false); // nothing to repeat
        }
        return null;
    }

    private (VimPos? Target, Kind Kind, bool Jump)? MarkMotion(VimCommand cmd, string key)
    {
        if (key is not ("'" or "`")) return null;
        var t = _t;
        VimPos? mark = LookUpMark(cmd.Arg);
        if (mark is not { } at || at.Line < t.FirstLine) { _marks.Remove(cmd.Arg); return (null, Kind.Exclusive, false); }
        at = VimBuffer.ClampLine(t, at);
        return key == "'"
            ? (VimMotions.FirstNonBlank(t, at.Line), Kind.Linewise, true)
            : (at with { Col = VimMotions.ClampCol(t, at.Line, at.Col, free: Mode == VimMode.VisualBlock) }, Kind.Exclusive, true);
    }

    private VimPos? LookUpMark(char name)
    {
        if (name is '\'' or '`') return _lastJump;
        return _marks.TryGetValue(name, out var m) ? m : null;
    }

    private (VimPos? Target, Kind Kind, bool Jump)? ScrollMotion(VimCommand cmd, string key) => key switch
    {
        "<C-d>" or "<C-u>" or "<C-f>" or "<C-b>" => PageScroll(cmd, key),
        "<C-e>" or "<C-y>" => LineScroll(cmd, key),
        _ => null,
    };

    private (VimPos? Target, Kind Kind, bool Jump)? PageScroll(VimCommand cmd, string key)
    {
        var t = _t;
        var p = Cursor;
        bool free = Mode == VimMode.VisualBlock;
        long top = t.ViewTopLine;
        int page = Math.Max(1, t.Rows - 2), half = Math.Max(1, t.Rows / 2);
        int dir = key is "<C-d>" or "<C-f>" ? 1 : -1;
        long halfPage = cmd.HasCount ? cmd.Count : half;
        long amount = key is "<C-d>" or "<C-u>" ? halfPage : (long)page * cmd.N;
        t.ScrollViewToLine(top + dir * amount);
        var to = VimMotions.Vertical(t, p, dir * amount, _want, free);
        // The cursor stays on screen: at least on the new top (Ctrl-f) / bottom (Ctrl-b).
        return (KeepInView(to, free), Kind.Linewise, false);
    }

    private (VimPos? Target, Kind Kind, bool Jump)? LineScroll(VimCommand cmd, string key)
    {
        var p = Cursor;
        bool free = Mode == VimMode.VisualBlock;
        int n = cmd.N;
        _t.ScrollViewToLine(_t.ViewTopLine + (key == "<C-e>" ? n : -n));
        return (KeepInView(p, free), Kind.Linewise, false);
    }

    private VimPos KeepInView(VimPos to, bool free)
    {
        long newTop = _t.ViewTopLine, newBottom = newTop + _t.Rows - 1;
        if (to.Line < newTop) to = VimMotions.Vertical(_t, to, newTop - to.Line, _want, free);
        if (to.Line > newBottom) to = VimMotions.Vertical(_t, to, newBottom - to.Line, _want, free);
        return to;
    }

    // ==== Visual & yank ========================================================================

    /// <summary>The visual selection (inclusive), or null in normal mode. <see cref="VimMode.VisualBlock"/>: a rectangle.</summary>
    public VimRange? Selection
    {
        get
        {
            if (!InVisual) return null;
            var s = VimPos.Min(Anchor, Cursor);
            var e = VimPos.Max(Anchor, Cursor);
            return Mode switch
            {
                VimMode.VisualLine => new VimRange(new VimPos(s.Line, 0), new VimPos(e.Line, 0), Linewise: true),
                VimMode.VisualBlock => new VimRange(
                    new VimPos(s.Line, Math.Min(Anchor.Col, Cursor.Col)),
                    new VimPos(e.Line, Math.Max(Anchor.Col, Cursor.Col))),
                _ => new VimRange(s, e),
            };
        }
    }

    /// <summary>The block's columns, inclusive (wide characters at the right edge count whole).</summary>
    public (int Left, int Right) BlockColumns()
    {
        int left = Math.Min(Anchor.Col, Cursor.Col), right = Math.Max(Anchor.Col, Cursor.Col);
        var rightPos = Anchor.Col >= Cursor.Col ? Anchor : Cursor;
        return (left, right + VimBuffer.CellWidth(_t, rightPos.Line, right) - 1);
    }

    /// <summary>The text of the visual selection.</summary>
    public string SelectionText()
    {
        if (Selection is not { } r) return "";
        if (Mode == VimMode.VisualBlock)
        {
            var (left, right) = BlockColumns();
            return BlockText(r.Start.Line, left, r.End.Line, right);
        }
        return RangeText(r);
    }

    private string BlockText(long top, int left, long bottom, int right)
    {
        var lines = new List<string>((int)Math.Min(bottom - top + 1, 100_000));
        for (long line = top; line <= bottom; line++)
            lines.Add(_t.GetText(line, VimBuffer.Lead(_t, line, left), line, right, block: true).TrimEnd());
        return string.Join('\n', lines);
    }

    /// <summary>The text of a range: whole lines (joined across soft wraps) or from start to end, inclusive.</summary>
    public string RangeText(VimRange r)
    {
        if (r.Linewise) return _t.GetText(r.Start.Line, 0, r.End.Line, int.MaxValue / 2);
        int endCol = r.End.Col + VimBuffer.CellWidth(_t, r.End.Line, r.End.Col) - 1;
        string text = _t.GetText(r.Start.Line, r.Start.Col, r.End.Line, endCol);
        // GetText trims blanks at line ends; blanks inside a line that the range ends on are part of it (yw, aw).
        if (endCol < VimBuffer.LastContentCol(_t, r.End.Line))
        {
            int blanks = 0;
            int from = r.End.Line == r.Start.Line ? r.Start.Col : 0;
            for (int x = endCol; x >= from && VimBuffer.Rune(_t, r.End.Line, x) <= 0x20; x--) blanks++;
            int have = text.Length - text.TrimEnd(' ').Length;
            if (blanks > have) text += new string(' ', blanks - have);
        }
        return text;
    }

    private void YankSelection(IVimHost host, bool lines)
    {
        if (Selection is not { } r) return;
        string text = lines ? RangeText(r with { Linewise = true }) : SelectionText();
        SetMode(VimMode.Normal);
        Cursor = lines ? new VimPos(r.Start.Line, Cursor.Col) : r.Start;
        Cursor = new VimPos(Cursor.Line, VimMotions.ClampCol(_t, Cursor.Line, Cursor.Col, false));
        _want = Cursor.Col;
        LastYank = text;
        host.Yank(text);
        EnsureVisible();
    }

    private void Yank(VimRange r, IVimHost host)
    {
        string text = RangeText(r);
        LastYank = text;
        host.Yank(text);
        var start = r.Linewise ? new VimPos(r.Start.Line, Math.Min(Cursor.Col, VimMotions.ClampCol(_t, r.Start.Line, Cursor.Col, false))) : r.Start;
        if (start < Cursor) { Cursor = start; _want = Cursor.Col; EnsureVisible(); }
    }

    private void YankMotion(string key, VimPos to, Kind kind, IVimHost host)
    {
        var from = Cursor;
        var s = VimPos.Min(from, to);
        var e = VimPos.Max(from, to);
        if (kind == Kind.Linewise) { Yank(new VimRange(new VimPos(s.Line, 0), new VimPos(e.Line, 0), Linewise: true), host); return; }
        if (kind == Kind.Exclusive)
        {
            // yw on a line's last word stops at the line end; an exclusive end at column 0 ends the line before (vim's rule).
            if (key is "w" or "W" && e.Line > s.Line) e = VimMotions.LineEnd(_t, s.Line);
            else if (e.Col == 0 && e.Line > s.Line) e = VimMotions.LineEnd(_t, e.Line - 1);
            else
            {
                int prev = VimBuffer.PrevCol(_t, e.Line, e.Col);
                if (prev < 0 || e == s) return; // nothing between
                e = e with { Col = prev };
            }
        }
        Yank(new VimRange(s, e), host);
    }

    // ==== Search ===============================================================================

    private void SearchNext(IVimHost host, bool forward, int count)
    {
        if (LastSearch.Length == 0) { host.Message("No previous regular expression", error: true); return; }
        host.SearchChanged(LastSearch);
        var p = Cursor;
        for (int i = 0; i < count; i++)
        {
            if (host.FindMatch(p, forward) is not { } m) { host.Message("Pattern not found: " + LastSearch, error: true); return; }
            p = m;
        }
        _lastJump = Cursor;
        Cursor = p;
        _want = p.Col;
        EnsureVisible(center: true);
    }

    /// <summary>
    /// While / or ? is open (call per frame as the scan finds matches): the cursor previews the first match from where the
    /// search started; with none (yet) it stays there.
    /// </summary>
    public void UpdateIncremental(IVimHost host)
    {
        if (Prompt is not (VimPromptKind.SearchForward or VimPromptKind.SearchBackward)) return;
        VimPos target = _promptOrigin;
        if (PromptText.Length > 0 && host.FindMatch(_promptOrigin, Prompt == VimPromptKind.SearchForward) is { } m) target = m;
        if (target == Cursor) return;
        Cursor = target;
        if (target == _promptOrigin) _t.ScrollViewToLine(_promptOriginTop);
        else EnsureVisible(center: true);
        Version++;
    }

    // ==== Prompt ===============================================================================

    private void OpenPrompt(VimPromptKind kind)
    {
        Prompt = kind;
        PromptText = "";
        PromptError = null;
        _promptOrigin = Cursor;
        _promptOriginTop = _t.ViewTopLine;
        _searchBefore = LastSearch.Length > 0 ? LastSearch : null;
        _historyIndex = -1;
        _cycle = null;
        _cycleIndex = -1;
        Suggestions = [];
        SuggestionIndex = -1;
    }

    /// <summary>Opens the / prompt from outside (the Search action while in vim mode).</summary>
    public void OpenSearchPrompt(bool forward = true) => OpenPrompt(forward ? VimPromptKind.SearchForward : VimPromptKind.SearchBackward);

    /// <summary>Inserts text into the open prompt (paste).</summary>
    public void PromptInsert(string text, IVimHost host)
    {
        if (Prompt == VimPromptKind.None) return;
        PromptText += text.Replace("\r", "").Replace('\n', ' ');
        PromptEdited(host);
    }

    private void PromptKey(string key, IVimHost host)
    {
        switch (key)
        {
            case VimKeys.Esc or "<C-c>" or "<C-[>":
                CancelPrompt(host);
                return;
            case VimKeys.Enter:
                AcceptPrompt(host);
                return;
            case VimKeys.Backspace or "<C-h>":
                if (PromptText.Length == 0) { CancelPrompt(host); return; }
                PromptText = WithoutLastChar(PromptText);
                break;
            case "<C-u>": PromptText = ""; break;
            case "<C-w>": PromptText = WithoutLastWord(PromptText); break;
            case VimKeys.Tab or VimKeys.ShiftTab or "<C-n>" or "<C-p>" when Prompt == VimPromptKind.Ex:
                CycleCompletion(host, key is VimKeys.Tab or "<C-n>" ? 1 : -1);
                return;
            case VimKeys.Up or VimKeys.Down:
                if (!StepPromptHistory(key)) return;
                break;
            default:
                if (!TypeIntoPrompt(key)) return;
                break;
        }
        PromptEdited(host);
    }

    private static string WithoutLastChar(string text) => text[..^(text.Length > 1 && char.IsLowSurrogate(text[^1]) ? 2 : 1)];

    private static string WithoutLastWord(string text)
    {
        string trimmed = text.TrimEnd();
        int cut = trimmed.LastIndexOfAny([' ', '/', '.', '-', '_']);
        return cut < 0 ? "" : trimmed[..(cut + 1)];
    }

    /// <summary>Moves through the prompt's history; false when it has none.</summary>
    private bool StepPromptHistory(string key)
    {
        var history = Prompt == VimPromptKind.Ex ? _exHistory : _searchHistory;
        if (history.Count == 0) return false;
        int i = _historyIndex < 0 ? history.Count : _historyIndex;
        i = Math.Clamp(i + (key == VimKeys.Up ? -1 : 1), 0, history.Count);
        _historyIndex = i;
        PromptText = i < history.Count ? history[i] : "";
        return true;
    }

    /// <summary>Appends a typed character; false for any other key.</summary>
    private bool TypeIntoPrompt(string key)
    {
        bool typed = key.Length >= 1 && key[0] != '<' || key == "<" || key.Length == 2 && char.IsHighSurrogate(key[0]);
        if (typed) PromptText += key;
        return typed;
    }

    private void PromptEdited(IVimHost host)
    {
        PromptError = null;
        _cycle = null;
        _cycleIndex = -1;
        SuggestionIndex = -1;
        switch (Prompt)
        {
            case VimPromptKind.Ex:
                Suggestions = Ex.Complete(PromptText, host.ActionNames, host.Arguments, 8);
                host.PromptChanged(PromptText);
                break;
            case VimPromptKind.SearchForward or VimPromptKind.SearchBackward:
                if (PromptText.Length > 0) host.SearchChanged(PromptText);
                else host.SearchCancelled(null);
                UpdateIncremental(host);
                break;
        }
    }

    private void CycleCompletion(IVimHost host, int dir)
    {
        _cycle ??= Ex.Complete(PromptText, host.ActionNames, host.Arguments);
        if (_cycle.Count == 0) return;
        if (_cycleIndex < 0) _cycleIndex = dir > 0 ? 0 : _cycle.Count - 1;
        else _cycleIndex = ((_cycleIndex + dir) % _cycle.Count + _cycle.Count) % _cycle.Count;
        PromptText = _cycle[_cycleIndex];
        PromptError = null;
        SuggestionIndex = -1;
        // Keep the visible suggestions in step with what Tab picks.
        Suggestions = _cycle.Count <= 8 ? _cycle : _cycle.Skip(Math.Max(0, Math.Min(_cycleIndex - 3, _cycle.Count - 8))).Take(8).ToList();
        for (int i = 0; i < Suggestions.Count; i++) if (Suggestions[i] == PromptText) SuggestionIndex = i;
        host.PromptChanged(PromptText);
    }

    private void CancelPrompt(IVimHost host)
    {
        var kind = Prompt;
        Prompt = VimPromptKind.None;
        PromptText = "";
        PromptError = null;
        Suggestions = [];
        if (kind == VimPromptKind.Ex) { host.PromptCancelled(); return; }
        Cursor = _promptOrigin;
        _t.ScrollViewToLine(_promptOriginTop);
        host.SearchCancelled(_searchBefore);
    }

    private void AcceptPrompt(IVimHost host)
    {
        if (Prompt == VimPromptKind.Ex) AcceptExCommand(PromptText, host);
        else AcceptSearch(Prompt, PromptText, host);
    }

    private void AcceptExCommand(string text, IVimHost host)
    {
        if (text.Trim().Length == 0) { CancelPrompt(host); return; }
        Remember(_exHistory, text);
        var cmd = Ex.Parse(text, host.ActionNames, out string? error);
        if (cmd is null) { PromptError = error ?? "Not an editor command"; return; }
        string? failure = null;
        switch (cmd.Name)
        {
            case "goto": GoToLineOf(cmd); break;
            case "nohlsearch": host.ClearHighlight(); break;
            case "help": host.Help(); break;
            default: failure = host.Execute(cmd); break;
        }
        if (failure is not null) { PromptError = failure; return; }
        Prompt = VimPromptKind.None;
        PromptText = "";
        Suggestions = [];
    }

    private void GoToLineOf(ExCommand cmd)
    {
        long line = cmd.Number == int.MaxValue ? VimBuffer.LastLine(_t) : _t.FirstLine + Math.Max(1, cmd.Number ?? 1) - 1;
        line = Math.Clamp(line, _t.FirstLine, VimBuffer.LastLine(_t));
        _lastJump = Cursor;
        Cursor = VimMotions.FirstNonBlank(_t, line);
        _want = Cursor.Col;
        EnsureVisible(center: true);
    }

    private void AcceptSearch(VimPromptKind kind, string text, IVimHost host)
    {
        Prompt = VimPromptKind.None;
        PromptText = "";
        string pattern = text.Length > 0 ? text : LastSearch;
        Cursor = _promptOrigin;
        if (pattern.Length == 0) { host.SearchCancelled(null); host.Message("No previous regular expression", error: true); return; }
        Remember(_searchHistory, pattern);
        LastSearch = pattern;
        LastSearchForward = kind == VimPromptKind.SearchForward;
        host.SearchChanged(pattern);
        if (host.FindMatch(_promptOrigin, LastSearchForward) is { } m)
        {
            _lastJump = _promptOrigin;
            Cursor = m;
            _want = m.Col;
            EnsureVisible(center: true);
        }
        else
        {
            _t.ScrollViewToLine(_promptOriginTop);
            host.Message("Pattern not found: " + pattern, error: true);
        }
    }

    private static void Remember(List<string> history, string text)
    {
        history.Remove(text);
        history.Add(text);
        if (history.Count > 100) history.RemoveAt(0);
    }

    // ==== View =================================================================================

    /// <summary>Scrolls just enough to show the cursor (or centres it after a far jump).</summary>
    public void EnsureVisible(bool center = false)
    {
        long top = _t.ViewTopLine;
        int rows = _t.Rows;
        if (Cursor.Line >= top && Cursor.Line < top + rows) return;
        if (center) _t.ScrollViewToLine(Cursor.Line - (rows - 1) / 2);
        else if (Cursor.Line < top) _t.ScrollViewToLine(Cursor.Line);
        else _t.ScrollViewToLine(Cursor.Line - rows + 1);
    }
}
