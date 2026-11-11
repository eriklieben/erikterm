namespace ErikTerm.Ui.Compare;

/// <summary>Where the cursor goes when a compare result arrives.</summary>
public enum CompareFocus { First, Last, Keep }

/// <summary>A note drawn under a line of the view; exactly one of <see cref="Left"/> and <see cref="Right"/> (0-based lines) is set, the other is -1.</summary>
public sealed record CompareNote(long Id, int Left, int Right, string Label, string Body, bool Outdated);

/// <summary>
/// The side-by-side view itself, placed in any rectangle of a frame: aligned columns with gap rows, word-level
/// highlights, overview strip, noise rules whose differences stay on screen but dimmed and counted separately. Hosted
/// by <see cref="CompareScreen"/> (two files) and <see cref="ReviewScreen"/> (a branch). Recomputation runs through an
/// <see cref="IBackground"/> so input stays responsive for the largest files.
/// </summary>
public sealed class ComparePane
{
    /// <summary>Hit actions of the pane; a host's own actions start at <see cref="FirstHostAction"/>.</summary>
    private const int ActFilterAll = 1, ActFilterDiffs = 2, ActFilterContext = 3, ActWhitespace = 4, ActLineEndings = 5, ActComments = 6,
        ActPrev = 7, ActNext = 8, ActOverview = 10, ActRow = 11;
    public const int FirstHostAction = 100;

    private const int GutterWidth = 7, DetailRows = 2, ContextLines = 3;
    /// <summary>A visible entry at or below this is a row of a note, not a line or a separator: see <see cref="NoteEntry"/>.</summary>
    private const int NoteBase = int.MinValue / 2, NoteRowSpan = 256;

    private readonly IBackground _background;
    private CompareSide? _left, _right, _pendingLeft, _pendingRight;
    private CompareResult? _result;
    private NoiseRules _rules = NoiseRules.Default;
    private CompareFilter _filter;
    private CompareFocus _focusOnResult = CompareFocus.First;
    private int[] _visible = [];
    private int _top, _hScroll, _cursor, _generation;
    private bool _computing, _rulesOpen;
    private readonly Dictionary<long, LineChanges> _intraline = [];
    private int _lastRows = 20;
    private IReadOnlyList<CompareNote> _notes = [];
    private int _noteWidth = 60, _markRow = -1;

    public ComparePane(IBackground background, CompareFilter filter = CompareFilter.All)
    {
        _background = background;
        _filter = filter;
    }

    public TuiTheme Theme { get; set; } = TuiTheme.Dark;
    public NoiseRules Rules => _rules;
    public CompareFilter Filter => _filter;
    public CompareResult? Result => _result;
    public CompareSide? Left => _left;
    public CompareSide? Right => _right;
    public bool RulesOpen => _rulesOpen;

    /// <summary>A result, or a message in its place: the file is on screen and <see cref="Step"/> can be trusted.</summary>
    public bool Settled => !_computing && (_left is not null || _right is not null);

    /// <summary>Shown while there is nothing to compare yet.</summary>
    public string IdleMessage { get; set; } = "Comparing...";

    /// <summary>Called on the input thread when a recomputation finishes.</summary>
    public Action? Computed { get; set; }

    public string Summary => _computing ? "Comparing..." : _result?.Summary ?? "";

    /// <summary>Forgets the file on screen ("nothing selected").</summary>
    public void Clear()
    {
        _generation++;
        _left = _right = _pendingLeft = _pendingRight = null;
        _result = null;
        _computing = false;
        _visible = [];
        _top = _cursor = _hScroll = 0;
        _markRow = -1;
        _notes = [];
        _intraline.Clear();
    }

    /// <summary>The sides to compare; <see cref="CompareFocus.Keep"/> with unchanged content changes nothing.</summary>
    public void Show(CompareSide left, CompareSide right, CompareFocus focus = CompareFocus.First)
    {
        if (focus == CompareFocus.Keep && _left is { } oldLeft && _right is { } oldRight && SameContent(oldLeft, left) && SameContent(oldRight, right))
        {
            _left = left;
            _right = right;
            return;
        }
        _focusOnResult = focus;
        _pendingLeft = left;
        _pendingRight = right;
        Recompute();
    }

    private static bool SameContent(CompareSide a, CompareSide b)
    {
        if (a.Error != b.Error || a.Absent != b.Absent) return false;
        if (a.Document is not { } da || b.Document is not { } db) return a.Document is null && b.Document is null;
        return da.Problem == db.Problem && da.ByteLength == db.ByteLength && da.RawLines.AsSpan().SequenceEqual(db.RawLines);
    }

    /// <summary>
    /// Computes against the newest sides. The screen keeps drawing the old sides and rows until the new result is
    /// there; sides and rows change together, or a row could point past the end of a shorter new file.
    /// </summary>
    private void Recompute()
    {
        int generation = ++_generation;
        var left = _pendingLeft ?? _left;
        var right = _pendingRight ?? _right;
        if (left?.Document is not { } a || right?.Document is not { } b || a.Problem != DocumentProblem.None || b.Problem != DocumentProblem.None)
        {
            Commit(left, right);
            _computing = false;
            _result = null;
            _visible = [];
            Computed?.Invoke();
            return;
        }
        _computing = true;
        var rules = _rules;
        var (syntaxA, syntaxB) = (LanguageSyntax.For(left.Title), LanguageSyntax.For(right.Title));
        _background.Run(() => CompareEngine.Compute(a, b, rules, syntaxA, syntaxB), result =>
        {
            if (generation != _generation) return; // a newer file or rule set was asked for meanwhile
            Commit(left, right);
            _computing = false;
            _result = result;
            _intraline.Clear();
            RebuildVisible();
            if (_focusOnResult != CompareFocus.Keep) FocusEdge(_focusOnResult == CompareFocus.Last);
            _focusOnResult = CompareFocus.Keep; // a rules change stays where it is
            Computed?.Invoke();
        });
    }

    private void Commit(CompareSide? left, CompareSide? right)
    {
        if (_focusOnResult != CompareFocus.Keep)
        {
            _top = _cursor = _hScroll = 0;
            _markRow = -1;
        }
        _left = left;
        _right = right;
        _pendingLeft = _pendingRight = null;
    }

    private void RebuildVisible()
    {
        if (_result is not { } r) { _visible = []; return; }
        var visible = new List<int>(r.Rows.Length);
        var anchored = NoteAnchors(r);
        switch (_filter)
        {
            case CompareFilter.All:
                for (int i = 0; i < r.Rows.Length; i++) visible.Add(i);
                break;
            case CompareFilter.Differences:
                for (int i = 0; i < r.Rows.Length; i++) if (r.Rows[i].Kind != RowKind.Equal || anchored.ContainsKey(i)) visible.Add(i);
                break;
            default:
                AddContext(r, visible, anchored);
                break;
        }
        _visible = anchored.Count == 0 ? [.. visible] : WithNoteRows(visible, anchored);
        _top = Math.Clamp(_top, 0, Math.Max(0, _visible.Length - 1));
        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, _visible.Length - 1));
    }

    /// <summary>Differences with a few equal lines around them; a separator row (negative: lines skipped) stands for the rest.</summary>
    private static void AddContext(CompareResult r, List<int> visible, Dictionary<int, List<int>> anchored)
    {
        var keep = new bool[r.Rows.Length];
        foreach (int row in anchored.Keys) keep[row] = true;
        foreach (var block in r.Blocks)
            for (int i = Math.Max(0, block.FirstRow - ContextLines); i < Math.Min(r.Rows.Length, block.FirstRow + block.RowCount + ContextLines); i++) keep[i] = true;
        int skipped = 0;
        for (int i = 0; i < keep.Length; i++)
        {
            if (keep[i])
            {
                if (skipped > 0) { visible.Add(-skipped); skipped = 0; }
                visible.Add(i);
            }
            else skipped++;
        }
        if (skipped > 0) visible.Add(-skipped);
    }

    // ---- Notes and the marked range -------------------------------------------------------------

    private static bool IsNoteEntry(int entry) => entry <= NoteBase;
    private static int NoteEntry(int note, int row) => NoteBase - (note * NoteRowSpan + row);
    private static (int Note, int Row) DecodeNote(int entry) => ((NoteBase - entry) / NoteRowSpan, (NoteBase - entry) % NoteRowSpan);

    /// <summary>Replaces the notes under the lines; the cursor stays on its line.</summary>
    public void SetNotes(IReadOnlyList<CompareNote> notes)
    {
        if (notes.Count == 0 && _notes.Count == 0) return;
        int row = CursorRow(), offset = _cursor - _top;
        _notes = notes;
        RebuildVisible();
        if (row < 0) return;
        int v = Array.IndexOf(_visible, row);
        if (v < 0) return;
        _cursor = v;
        _top = Math.Clamp(v - offset, 0, Math.Max(0, _visible.Length - _lastRows));
    }

    /// <summary>The note the cursor is on, or null.</summary>
    public long? CursorNote =>
        _cursor < _visible.Length && IsNoteEntry(_visible[_cursor]) ? _notes[DecodeNote(_visible[_cursor]).Note].Id : null;

    public bool HasMark => _markRow >= 0;

    /// <summary>Starts a range at the cursor's line, or drops the one that's there.</summary>
    public void ToggleMark() => _markRow = _markRow >= 0 ? -1 : CursorRow();

    public void ClearMark() => _markRow = -1;

    /// <summary>The lines a comment would be on: the marked range to the cursor, or the cursor's line; null off the lines.</summary>
    public IReadOnlyList<CompareRow>? SelectedRows()
    {
        if (_result is not { } r || CursorRow() is not (>= 0 and var row)) return null;
        int first = _markRow >= 0 ? Math.Min(_markRow, row) : row, last = Math.Max(_markRow, row);
        return r.Rows[first..(last + 1)];
    }

    /// <summary>The compare row of the cursor (a note's own line when it's on a note); -1 on a separator or with nothing shown.</summary>
    private int CursorRow()
    {
        for (int v = Math.Min(_cursor, _visible.Length - 1); v >= 0; v--)
            if (!IsNoteEntry(_visible[v])) return _visible[v] >= 0 ? _visible[v] : -1;
        return -1;
    }

    /// <summary>Which notes sit under which compare row.</summary>
    private Dictionary<int, List<int>> NoteAnchors(CompareResult r)
    {
        var map = new Dictionary<int, List<int>>();
        if (_notes.Count == 0) return map;
        var (byLeft, byRight) = RowsBySide(r);
        for (int n = 0; n < _notes.Count; n++)
        {
            var note = _notes[n];
            if (!(note.Right >= 0 ? byRight : byLeft).TryGetValue(note.Right >= 0 ? note.Right : note.Left, out int row)) continue;
            if (!map.TryGetValue(row, out var list)) map[row] = list = [];
            list.Add(n);
        }
        return map;
    }

    /// <summary>The compare row of each left line and of each right line.</summary>
    private static (Dictionary<int, int> ByLeft, Dictionary<int, int> ByRight) RowsBySide(CompareResult r)
    {
        var byLeft = new Dictionary<int, int>();
        var byRight = new Dictionary<int, int>();
        for (int i = 0; i < r.Rows.Length; i++)
        {
            if (r.Rows[i].Left >= 0) byLeft[r.Rows[i].Left] = i;
            if (r.Rows[i].Right >= 0) byRight[r.Rows[i].Right] = i;
        }
        return (byLeft, byRight);
    }

    private int[] WithNoteRows(List<int> visible, Dictionary<int, List<int>> anchored)
    {
        var entries = new List<int>(visible.Count + anchored.Count * 2);
        foreach (int entry in visible)
        {
            entries.Add(entry);
            if (entry < 0 || !anchored.TryGetValue(entry, out var notes)) continue;
            foreach (int n in notes)
                for (int row = 0; row < Math.Min(NoteRowSpan, NoteLines(_notes[n]).Count); row++) entries.Add(NoteEntry(n, row));
        }
        return [.. entries];
    }

    private List<(int Start, int End)> NoteLines(CompareNote note) => TuiText.Wrap(note.Body, Math.Max(10, _noteWidth - note.Label.Length - 2));

    /// <summary>One row of a note: its label on the first, the body wrapped under itself.</summary>
    private void DrawNoteRow(Frame f, int entry, int visibleIndex, int y, int x, int width)
    {
        var (index, row) = DecodeNote(entry);
        var note = _notes[index];
        bool selected = _cursor < _visible.Length && IsNoteEntry(_visible[_cursor]) && DecodeNote(_visible[_cursor]).Note == index;
        uint bg = selected ? Theme.Surface1 : Theme.Surface0;
        f.Fill(x, y, width, 1, Theme.Text, Theme.Base);
        int left = x + GutterWidth - 1;
        f.Fill(left, y, width - GutterWidth + 1, 1, Theme.Text, bg);
        f.Put(left, y, "▎", note.Outdated ? Theme.Peach : Theme.Mauve, bg);
        if (row == 0) f.Text(left + 2, y, width - GutterWidth, note.Label, note.Outdated ? Theme.Peach : Theme.Subtext, bg, CellAttrs.Bold);
        var lines = NoteLines(note);
        if (row < lines.Count)
        {
            var (start, end) = lines[row];
            int textX = left + 2 + note.Label.Length + 2;
            f.Text(textX, y, x + width - textX - 1, note.Body.AsSpan(start, end - start), Theme.Text, bg);
        }
        if (selected && row == 0 && visibleIndex == _cursor) DrawNoteHint(f, y, x, width, bg);
    }

    private void DrawNoteHint(Frame f, int y, int x, int width, uint bg)
    {
        const string Hint = " a edit  x delete ";
        int hx = x + width - Hint.Length;
        if (hx > x + GutterWidth) f.Text(hx, y, Hint.Length, Hint, Theme.Lavender, bg);
    }

    private void FocusEdge(bool last)
    {
        if (_result is not { } r || r.Blocks.Length == 0) { _cursor = 0; return; }
        int block = last ? FirstBlockVisible(r, r.Blocks.Length - 1, -1) : FirstBlockVisible(r, 0, +1);
        if (block >= 0) MoveToBlock(block);
    }

    private static int FirstBlockVisible(CompareResult r, int startBlock, int step)
    {
        bool anyImportant = r.Blocks.Any(b => b.Important);
        for (int k = startBlock; k >= 0 && k < r.Blocks.Length; k += step)
            if (!anyImportant || r.Blocks[k].Important) return k;
        return -1;
    }

    private void MoveToBlock(int block)
    {
        if (_result is not { } r || block < 0 || block >= r.Blocks.Length) return;
        int first = r.Blocks[block].FirstRow;
        int v = Array.FindIndex(_visible, row => row >= first);
        if (v < 0) return;
        _cursor = v;
        _top = Math.Clamp(v - _lastRows / 3, 0, Math.Max(0, _visible.Length - _lastRows));
    }

    /// <summary>The keys of the view; false when the key isn't one of them (the host handles closing and the rest).</summary>
    public bool OnKey(KeyEvent key)
    {
        if (_rulesOpen && OnRulesKey(key)) return true;
        if (key.Key != TuiKey.Char) return OnSpecialKey(key.Key);
        return key.Ctrl ? OnCtrlKey(key.Rune) : OnLetterKey((char)key.Rune);
    }

    private bool OnCtrlKey(int rune)
    {
        if (rune == 'd') { Scroll(_lastRows / 2); return true; }
        if (rune == 'u') { Scroll(-_lastRows / 2); return true; }
        return false;
    }

    private bool OnSpecialKey(TuiKey key)
    {
        switch (key)
        {
            case TuiKey.Down: Scroll(1); return true;
            case TuiKey.Up: Scroll(-1); return true;
            case TuiKey.PageDown: Scroll(_lastRows - 1); return true;
            case TuiKey.PageUp: Scroll(-(_lastRows - 1)); return true;
            case TuiKey.Home: JumpTo(0); return true;
            case TuiKey.End: JumpTo(_visible.Length - 1); return true;
            case TuiKey.Left: _hScroll = Math.Max(0, _hScroll - 8); return true;
            case TuiKey.Right: _hScroll += 8; return true;
        }
        return false;
    }

    private bool OnLetterKey(char letter)
    {
        switch (letter)
        {
            case 'j': Scroll(1); return true;
            case 'k': Scroll(-1); return true;
            case 'g': JumpTo(0); return true;
            case 'G': JumpTo(_visible.Length - 1); return true;
            case 'h': _hScroll = Math.Max(0, _hScroll - 8); return true;
            case 'l': _hScroll += 8; return true;
            case ']' or 'n': Step(+1); return true;
            case '[' or 'p': Step(-1); return true;
            case 'd': CycleFilter(); return true;
            case 'i': _rulesOpen = true; return true;
        }
        return false;
    }

    private bool OnRulesKey(KeyEvent key)
    {
        if (key.Key == TuiKey.Esc || key.Is('i') || key.Is('q') || key.Key == TuiKey.Enter) { _rulesOpen = false; return true; }
        if (key.Key != TuiKey.Char) return true;
        switch ((char)key.Rune)
        {
            case '1' or 'w': Toggle(ActWhitespace); break;
            case '2' or 'e': Toggle(ActLineEndings); break;
            case '3' or 'c': Toggle(ActComments); break;
            default: return false; // other keys act normally
        }
        return true;
    }

    private void Toggle(int action)
    {
        _rules = action switch
        {
            ActWhitespace => _rules with { Whitespace = !_rules.Whitespace },
            ActLineEndings => _rules with { LineEndings = !_rules.LineEndings },
            _ => _rules with { Comments = !_rules.Comments },
        };
        Recompute();
    }

    private void CycleFilter() => SetFilter((CompareFilter)(((int)_filter + 1) % 3));

    private void SetFilter(CompareFilter filter)
    {
        _filter = filter;
        int rowAtCursor = Math.Max(0, CursorRow());
        RebuildVisible();
        int v = Array.FindIndex(_visible, row => row >= rowAtCursor);
        _cursor = v >= 0 ? v : 0;
        _top = Math.Clamp(_cursor - _lastRows / 3, 0, Math.Max(0, _visible.Length - _lastRows));
    }

    public void Scroll(int delta)
    {
        if (_visible.Length == 0) return;
        _top = Math.Clamp(_top + delta, 0, Math.Max(0, _visible.Length - _lastRows));
        _cursor = Math.Clamp(_cursor + delta, 0, _visible.Length - 1);
    }

    private void JumpTo(int index)
    {
        if (_visible.Length == 0) return;
        _cursor = Math.Clamp(index, 0, _visible.Length - 1);
        _top = Math.Clamp(_cursor - _lastRows / 3, 0, Math.Max(0, _visible.Length - _lastRows));
    }

    /// <summary>
    /// Moves to the next or previous important difference; with no important ones it walks the unimportant ones.
    /// Past the last one it wraps, or with <paramref name="wrap"/> off returns false and stays.
    /// </summary>
    public bool Step(int direction, bool wrap = true)
    {
        if (_result is not { Blocks.Length: > 0 } r) return false;
        int currentRow = Math.Max(0, CursorRow());
        bool anyImportant = r.Blocks.Any(b => b.Important);
        int found = direction > 0 ? NextBlock(r, currentRow, anyImportant, wrap) : PreviousBlock(r, currentRow, anyImportant, wrap);
        if (found < 0) return false;
        MoveToBlock(found);
        return true;
    }

    private static bool Eligible(DiffBlock block, bool anyImportant) => !anyImportant || block.Important;

    private static int NextBlock(CompareResult r, int currentRow, bool anyImportant, bool wrap)
    {
        for (int k = 0; k < r.Blocks.Length; k++) if (r.Blocks[k].FirstRow > currentRow && Eligible(r.Blocks[k], anyImportant)) return k;
        return wrap ? FirstBlockVisible(r, 0, +1) : -1;
    }

    private static int PreviousBlock(CompareResult r, int currentRow, bool anyImportant, bool wrap)
    {
        for (int k = r.Blocks.Length - 1; k >= 0; k--) if (r.Blocks[k].FirstRow < currentRow && Eligible(r.Blocks[k], anyImportant)) return k;
        return wrap ? FirstBlockVisible(r, r.Blocks.Length - 1, -1) : -1;
    }

    /// <summary>Handles a click on one of the pane's regions; false when the region is the host's.</summary>
    public bool OnHit(HitRegion hit, MouseEvent mouse)
    {
        switch (hit.Action)
        {
            case ActFilterAll: SetFilter(CompareFilter.All); return true;
            case ActFilterDiffs: SetFilter(CompareFilter.Differences); return true;
            case ActFilterContext: SetFilter(CompareFilter.Context); return true;
            case ActWhitespace or ActLineEndings or ActComments: Toggle(hit.Action); return true;
            case ActPrev: Step(-1); return true;
            case ActNext: Step(+1); return true;
            case ActOverview:
                if (_visible.Length > 0)
                {
                    int rows = Math.Max(1, hit.Height);
                    _top = Math.Clamp((mouse.Y - hit.Y) * _visible.Length / rows, 0, Math.Max(0, _visible.Length - _lastRows));
                    _cursor = Math.Clamp(_top, 0, _visible.Length - 1);
                }
                return true;
            case ActRow: _cursor = hit.Arg; return true;
        }
        return false;
    }

    // ---- Drawing ---------------------------------------------------------------------------------

    /// <summary>Draws the columns in (x, y, width, height), the current row's detail at its bottom.</summary>
    public void Render(Frame f, int x, int y, int width, int height)
    {
        bool detail = height >= 6;
        int bottom = detail ? y + height - 1 - DetailRows : y + height;
        int bodyRows = bottom - y;
        _lastRows = Math.Max(1, bodyRows - 1);

        if (Problem() is { } message) { DrawCentered(f, x, y + 1, width, bottom, message); return; }
        if (_result is null || _left is null || _right is null) { DrawIdle(f, x, y + 1, width, bottom); return; }

        int stripX = x + width - 1, colsW = width - 1;
        int leftW = colsW / 2, rightX = x + leftW + 1, rightW = colsW - leftW - 1;
        DrawPaneTitle(f, x, y, leftW, _left.Title, _left.Subtitle, Theme.Blue);
        DrawPaneTitle(f, rightX, y, rightW, _right.Title, _right.Subtitle, Theme.Peach);

        int noteWidth = colsW - GutterWidth - 2;
        if (noteWidth != _noteWidth)
        {
            _noteWidth = noteWidth;
            if (_notes.Count > 0) SetNotes(_notes);
        }

        int rowsTop = y + 1, rows = _lastRows;
        for (int r = 0; r < rows; r++)
        {
            int v = _top + r;
            if (v >= _visible.Length) break;
            DrawRow(f, v, rowsTop + r, x, leftW, rightX, rightW);
            f.AddHit(x, rowsTop + r, colsW, 1, ActRow, v);
        }
        for (int row = y; row < rowsTop + rows; row++)
            if (!NoteAtScreenRow(row - rowsTop)) f.Put(x + leftW, row, "│", Theme.Surface1, Theme.Base);
        DrawOverview(f, stripX, rowsTop, rows);
        if (detail) DrawDetail(f, x, bottom, width);
    }

    private void DrawIdle(Frame f, int x, int y, int width, int bottom)
    {
        if (_computing || IdleMessage.Length > 0) DrawCentered(f, x, y, width, bottom, _computing ? "Comparing..." : IdleMessage);
    }

    private bool NoteAtScreenRow(int r) => r >= 0 && _top + r < _visible.Length && IsNoteEntry(_visible[_top + r]);

    /// <summary>The Ignore popup; drawn last by the host so it sits on top.</summary>
    public void RenderPopups(Frame f, int y)
    {
        if (_rulesOpen) DrawRulesPopup(f, y);
    }

    private string? Problem()
    {
        foreach (var side in new[] { _left, _right })
        {
            if (side is null) continue;
            if (side.Error is not null) return $"{side.Title}: {side.Error}";
            if (side.Document?.Problem == DocumentProblem.Binary) return BinaryMessage();
            if (side.Document?.Problem == DocumentProblem.TooLarge) return $"{side.Title} is too large to compare here ({FormatSize(side.Document.ByteLength)}, the limit is {FormatSize(TextDocument.MaxBytes)}).";
        }
        return null;
    }

    private string BinaryMessage()
    {
        var left = _left!.Document!;
        var right = _right!.Document!;
        if (_left.Absent) return $"{_right.Title} is a new binary file ({FormatSize(right.ByteLength)}).";
        if (_right.Absent) return $"{_left.Title} is a binary file that was deleted ({FormatSize(left.ByteLength)}).";
        if (left.Problem == DocumentProblem.None) return $"{_right.Title} is binary and {_left.Title} is text.";
        return right.Problem == DocumentProblem.None
            ? $"{_left.Title} is binary and {_right.Title} is text."
            : $"Binary files: {FormatSize(left.ByteLength)} and {FormatSize(right.ByteLength)}.";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB",
    };

    private void DrawCentered(Frame f, int x, int top, int width, int bottom, string message)
    {
        int y = top + Math.Max(0, (bottom - top) / 2 - 1);
        int tx = x + Math.Max(1, (width - message.Length) / 2);
        f.Text(tx, y, x + width - tx - 1, message, Theme.Subtext, Theme.Base);
    }

    /// <summary>A toolbar button; an active one is filled with its colour, or, an <paramref name="ignoreStyle"/> one, with the quiet background.</summary>
    private int ToolButton(Frame f, int x, int y, int limit, FrameButton button, bool ignoreStyle = false)
    {
        int width = button.Label.Length + 2;
        if (x + width >= limit) return x;
        uint accent = button.Color ?? Theme.Lavender;
        uint bg = Theme.Surface0;
        uint fg = Theme.Text;
        if (button.Active)
        {
            bg = ignoreStyle ? Theme.AutoBg : accent;
            fg = ignoreStyle ? Theme.Text : Theme.OnAccent;
        }
        f.Fill(x, y, width, 1, fg, bg);
        f.Text(x + 1, y, width - 1, button.Label, fg, bg);
        f.AddHit(x, y, width, 1, button.Action, button.Arg);
        return x + width + 1;
    }

    /// <summary>View and Ignore buttons with the summary on the right, across (x, y, width).</summary>
    public void DrawToolbar(Frame f, int x, int y, int width)
    {
        int limit = x + width;
        f.Fill(x, y, width, 1, Theme.Text, Theme.Mantle);
        int cx = x + 1;
        cx = ToolButton(f, cx, y, limit, new("All", ActFilterAll, Active: _filter == CompareFilter.All));
        cx = ToolButton(f, cx, y, limit, new("Differences", ActFilterDiffs, Active: _filter == CompareFilter.Differences));
        cx = ToolButton(f, cx, y, limit, new("Context", ActFilterContext, Active: _filter == CompareFilter.Context));
        cx = f.Text(cx + 1, y, Math.Max(0, limit - cx), "Ignore", Theme.Subtext, Theme.Mantle) + 1;
        cx = ToolButton(f, cx, y, limit, new("whitespace", ActWhitespace, Active: _rules.Whitespace, Color: Theme.Green), ignoreStyle: true);
        cx = ToolButton(f, cx, y, limit, new("line endings", ActLineEndings, Active: _rules.LineEndings, Color: Theme.Green), ignoreStyle: true);
        cx = ToolButton(f, cx, y, limit, new("comments", ActComments, Active: _rules.Comments, Color: Theme.Green), ignoreStyle: true);
        string summary = Summary;
        int sx = Math.Max(cx + 1, limit - summary.Length - 2);
        f.Text(sx, y, Math.Max(0, limit - sx - 1), summary, Theme.Subtext, Theme.Mantle);
    }

    private void DrawPaneTitle(Frame f, int x, int y, int width, string title, string subtitle, uint color)
    {
        f.Fill(x, y, width, 1, Theme.Text, Theme.Mantle);
        int nx = f.Text(x + 1, y, Math.Max(0, width - 2), title, color, Theme.Mantle, CellAttrs.Bold);
        if (subtitle.Length > 0) f.Text(nx + 2, y, Math.Max(0, x + width - nx - 3), subtitle, Theme.Subtext, Theme.Mantle);
    }

    private void DrawRow(Frame f, int visibleIndex, int y, int x, int leftW, int rightX, int rightW)
    {
        int rowIndex = _visible[visibleIndex];
        if (IsNoteEntry(rowIndex)) { DrawNoteRow(f, rowIndex, visibleIndex, y, x, leftW + 1 + rightW); return; }
        if (rowIndex < 0)
        {
            f.Fill(x, y, leftW + 1 + rightW, 1, Theme.Overlay, Theme.Mantle);
            f.Text(x + 2, y, leftW + rightW - 3, $"⋯ {-rowIndex} equal lines", Theme.Overlay, Theme.Mantle);
            return;
        }
        var row = _result!.Rows[rowIndex];
        bool selected = visibleIndex == _cursor || InMark(rowIndex);
        var changes = row.Kind == RowKind.Equal || row.Left < 0 || row.Right < 0
            ? LineChanges.None
            : Changes(row);
        bool eolOnly = row.Kind != RowKind.Equal && row.Left >= 0 && row.Right >= 0
            && TextDocument.Content(_left!.Document!.RawLines[row.Left]).SequenceEqual(TextDocument.Content(_right!.Document!.RawLines[row.Right]));
        DrawCell(f, new FrameRun(x, y, leftW), row, true, selected, changes.Left, eolOnly);
        DrawCell(f, new FrameRun(rightX, y, rightW), row, false, selected, changes.Right, eolOnly);
    }

    private bool InMark(int rowIndex)
    {
        if (_markRow < 0 || CursorRow() is not (>= 0 and var row)) return false;
        return rowIndex >= Math.Min(_markRow, row) && rowIndex <= Math.Max(_markRow, row);
    }

    private LineChanges Changes(CompareRow row)
    {
        long key = ((long)row.Left << 32) | (uint)row.Right;
        if (_intraline.TryGetValue(key, out var cached)) return cached;
        var doc = _left!.Document!;
        var other = _right!.Document!;
        var changes = Intraline.Compute(TextDocument.ContentString(doc.RawLines[row.Left]), TextDocument.ContentString(other.RawLines[row.Right]));
        if (_intraline.Count > 4096) _intraline.Clear();
        _intraline[key] = changes;
        return changes;
    }

    /// <summary>One side of <paramref name="row"/>: <paramref name="isLeft"/> is the left one.</summary>
    private void DrawCell(Frame f, FrameRun at, CompareRow row, bool isLeft, bool selected, TextSpan[] spans, bool eolOnly)
    {
        var (x, y, width) = at;
        int line = isLeft ? row.Left : row.Right;
        bool otherGap = (isLeft ? row.Right : row.Left) < 0;
        var side = isLeft ? _left! : _right!;
        var kind = row.Kind;
        var (bg, spanBg, fg, attr) = CellStyle(kind, isLeft);
        if (kind == RowKind.Important && otherGap) spans = [new TextSpan(0, int.MaxValue / 2)];

        if (line < 0)
        {
            f.Fill(x, y, width, 1, Theme.GapFg, Theme.Base);
            for (int i = 0; i < width; i++) f.Put(x + i, y, "╱", Theme.GapFg, Theme.Base);
            return;
        }

        var raw = side.Document!.RawLines[line];
        f.Fill(x, y, width, 1, fg, bg);
        uint markerFg = Theme.Overlay;
        if (selected) markerFg = isLeft ? Theme.Blue : Theme.Peach;
        f.Put(x, y, selected ? "▌" : " ", markerFg, bg);
        string number = (line + 1).ToString();
        f.Text(x + GutterWidth - 1 - number.Length, y, number.Length, number, Theme.Overlay, bg);
        CompareRender.Line(f, new FrameRun(x + GutterWidth, y, width - GutterWidth), TextDocument.Content(raw), _hScroll, spans, new FrameLineStyle(fg, bg, spanBg, attr));
        if (eolOnly) DrawEolHint(f, x, y, width, raw);
    }

    private (uint Bg, uint SpanBg, uint Fg, CellAttrs Attr) CellStyle(RowKind kind, bool isLeft) => kind switch
    {
        RowKind.Important => (isLeft ? Theme.OursBg : Theme.TheirsBg, isLeft ? Theme.OursHi : Theme.TheirsHi, Theme.Text, CellAttrs.None),
        RowKind.Unimportant => (Theme.QuietBg, Theme.QuietBg, Theme.Overlay, CellAttrs.Dim),
        _ => (Theme.Base, Theme.Base, Theme.Text, CellAttrs.None),
    };

    /// <summary>A row whose text is the same but whose terminator isn't says so at the end of the line: it's the whole difference.</summary>
    private void DrawEolHint(Frame f, int x, int y, int width, string raw)
    {
        string name = "no newline";
        if (raw.EndsWith("\r\n", StringComparison.Ordinal)) name = "CRLF";
        else if (raw.EndsWith('\n')) name = "LF";
        string hint = $" {name} ";
        int hx = x + width - hint.Length;
        if (hx > x + GutterWidth) f.Text(hx, y, hint.Length, hint, Theme.Overlay, Theme.Mantle);
    }

    private void DrawOverview(Frame f, int x, int y, int height)
    {
        if (height <= 0 || _result is null) return;
        var rows = _result.Rows;
        var visible = _visible;
        var cells = Overview.Compute(visible.Length, height, i => OverviewSeverity(rows, visible[i]));
        int thumbStart = visible.Length == 0 ? 0 : _top * height / visible.Length;
        int thumbEnd = visible.Length == 0 ? height : Math.Min(height, (_top + _lastRows) * height / visible.Length + 1);
        for (int r = 0; r < height; r++)
        {
            uint bg = r >= thumbStart && r < thumbEnd ? Theme.Surface1 : Theme.Mantle;
            uint color = cells[r] switch { 2 => Theme.Blue, 1 => Theme.Overlay, _ => bg };
            f.Put(x, y + r, cells[r] == 0 ? " " : "█", color, bg);
        }
        f.AddHit(x, y, 1, height, ActOverview);
    }

    private static int OverviewSeverity(CompareRow[] rows, int row)
    {
        if (row < 0) return 0;
        return rows[row].Kind switch { RowKind.Important => 2, RowKind.Unimportant => 1, _ => 0 };
    }

    /// <summary>The current row stacked in full width, showing where the two sides differ (long lines scroll to the first change).</summary>
    private void DrawDetail(Frame f, int x, int y, int width)
    {
        f.Fill(x, y, width, DetailRows, Theme.Text, Theme.Mantle);
        if (_cursor >= _visible.Length || _visible[_cursor] < 0) return;
        var row = _result!.Rows[_visible[_cursor]];
        var changes = row.Left >= 0 && row.Right >= 0 ? Changes(row) : LineChanges.None;
        int scroll = FirstChange(changes);
        DrawDetailLine(f, new FrameRun(x, y, width), row.Left, _left!, true, changes.Left, scroll);
        DrawDetailLine(f, new FrameRun(x, y + 1, width), row.Right, _right!, false, changes.Right, scroll);
    }

    private static int FirstChange(LineChanges changes)
    {
        int first = int.MaxValue;
        if (changes.Left.Length > 0) first = Math.Min(first, changes.Left[0].Start);
        if (changes.Right.Length > 0) first = Math.Min(first, changes.Right[0].Start);
        return first == int.MaxValue ? 0 : Math.Max(0, first - 16);
    }

    /// <summary>One side's line in the detail rows, named and in its colour (blue for the left, peach for the right).</summary>
    private void DrawDetailLine(Frame f, FrameRun at, int line, CompareSide side, bool isLeft, TextSpan[] spans, int scroll)
    {
        var (x, y, width) = at;
        uint color = isLeft ? Theme.Blue : Theme.Peach;
        string name = side.Subtitle.Length > 0 ? side.Subtitle : "left";
        if (!isLeft && side.Subtitle.Length == 0) name = "right";
        if (name.Length > 7) name = name[..7];
        f.Text(x + 1, y, 8, name, color, Theme.Mantle, CellAttrs.Bold);
        if (line < 0) return;
        uint spanBg = isLeft ? Theme.OursHi : Theme.TheirsHi;
        CompareRender.Line(f, new FrameRun(x + 9, y, width - 10), TextDocument.Content(side.Document!.RawLines[line]), scroll, spans, new FrameLineStyle(Theme.Text, Theme.Mantle, spanBg));
    }

    private void DrawRulesPopup(Frame f, int top)
    {
        const int Width = 44, Height = 7;
        int x = Math.Max(0, f.Width - Width - 2), y = top;
        f.Fill(x, y, Width, Height, Theme.Text, Theme.Surface0);
        f.Text(x + 2, y + 1, Width - 4, "Ignore these differences (dimmed):", Theme.Subtext, Theme.Surface0, CellAttrs.Bold);
        DrawRuleLine(f, x, y + 2, "1  whitespace", _rules.Whitespace);
        DrawRuleLine(f, x, y + 3, "2  line endings", _rules.LineEndings);
        DrawRuleLine(f, x, y + 4, "3  comments", _rules.Comments);
        f.Text(x + 2, y + 5, Width - 4, "1, 2, 3 toggle  ·  i or Esc close", Theme.Overlay, Theme.Surface0);
    }

    private void DrawRuleLine(Frame f, int x, int y, string label, bool on)
    {
        f.Text(x + 2, y, 5, on ? "[x]" : "[ ]", on ? Theme.Green : Theme.Overlay, Theme.Surface0);
        f.Text(x + 7, y, 30, label, Theme.Text, Theme.Surface0);
    }
}
