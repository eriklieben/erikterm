namespace ErikTerm.Ui.Compare;

/// <summary>
/// A local PR review: the branch's changed files on the left, a <see cref="ComparePane"/> on the right, the merge-base
/// diff like GitHub's "Files changed". Everything from git comes through an <see cref="IReviewSource"/> off the input
/// thread; the only thing it changes in the repository is an explicit fetch. Comments are <see cref="ReviewCommentController"/>'s.
/// </summary>
public sealed class ReviewScreen : ITuiScreen, IReviewCommentView
{
    private const int ActFile = ComparePane.FirstHostAction, ActCommit = ActFile + 1, ActUncommitted = ActFile + 2, ActCommits = ActFile + 3,
        ActFinish = ActFile + 4, ActFetch = ActFile + 5, ActBase = ActFile + 6, ActPicked = ActFile + 7, ActSend = ActFile + 8;
    private const int RefreshMinMs = 1500, PickerWidth = 46, PickerRows = 12;

    private static readonly ReviewFileList NoFiles = new([]);

    private readonly IBackground _background;
    private readonly IReviewSource _source;
    private readonly ComparePane _pane;
    private readonly ReviewCommentController _comments;
    private TuiTheme _theme = TuiTheme.Dark;

    private string? _baseOverride;
    private bool _pickOnStart;
    private ReviewSnapshot? _snapshot;
    private ReviewFileList _list = NoFiles;
    private int _selected = -1, _listTop, _commitCursor, _commitTop, _lastListRows = 10;
    private string? _selectedPath;
    private bool _uncommitted = true, _focusList;
    private ReviewCommit? _commit;
    private IReadOnlyList<ReviewFile> _commitFiles = [];
    private bool _commitsOpen;

    private bool _loading, _reloadQueued, _fileInFlight, _wantFile;
    private CompareFocus _wantFocus = CompareFocus.First;
    private int _pendingStep;
    private long _refreshedAt;

    private ConflictPrediction? _prediction;
    private (string Head, string Base) _predictedFor;
    private int _predictionGeneration;
    private HashSet<string> _conflictFiles = new(StringComparer.Ordinal);

    private bool _fetching;
    private string? _fetchError;
    private string _status = "";
    private bool _statusIsError;

    private bool _pickerOpen;
    private IReadOnlyList<string>? _pickerBranches;
    private string _pickerFilter = "";
    private int _pickerCursor;

    private (int X, int Y, int W, int H) _listRect;

    public ReviewScreen(IBackground background, IReviewSource source, string? baseOverride = null, bool pickBase = false, IReviewCommentHost? comments = null)
    {
        _background = background;
        _source = source;
        _baseOverride = baseOverride;
        _pickOnStart = pickBase;
        _pane = new ComparePane(background, CompareFilter.Context) { IdleMessage = "" };
        _pane.Computed = OnComputed;
        _comments = new ReviewCommentController(background, source, comments ?? new NoReviewCommentHost(), _pane, this);
    }

    ReviewScope IReviewCommentView.Scope => CurrentScope;
    TuiTheme IReviewCommentView.Theme => _theme;
    void IReviewCommentView.Say(string text, bool error) => Say(text, error);

    public bool Done { get; private set; }
    public int ExitCode => 0;
    public ReviewSnapshot? Snapshot => _snapshot;
    public IReadOnlyList<ReviewFile> Files => _list.Files;
    public ReviewFile? Selected => _selected >= 0 && _selected < _list.Count ? _list.Files[_selected] : null;
    public bool Uncommitted => _uncommitted;
    public bool CommitsOpen => _commitsOpen;
    public ReviewCommit? Commit => _commit;
    public bool FocusOnList => _focusList;
    public string Status => _status;
    public ComparePane Pane => _pane;
    public IReadOnlyList<ReviewComment> Comments => _comments.All;
    public bool CommentEditorOpen => _comments.EditorOpen;
    public bool SendConfirmOpen => _comments.SendOpen;
    public bool WantsPaste => true;

    /// <summary>A paste is text for what's being typed in, and never a series of commands.</summary>
    public void OnPaste(string text)
    {
        if (_comments.EditorOpen) _comments.OnPaste(text);
        else if (_pickerOpen) { _pickerFilter += text.ReplaceLineEndings("").Trim(); _pickerCursor = 0; }
    }

    public void OnThemeColor(uint backgroundColor)
    {
        _theme = TuiTheme.ForBackground(backgroundColor);
        _pane.Theme = _theme;
    }

    /// <summary>Coming back to the tab brings the review up to date: agents may have changed things meanwhile.</summary>
    public void OnFocus(bool focused)
    {
        if (focused && Environment.TickCount64 - _refreshedAt >= RefreshMinMs) Reload();
    }

    /// <summary>Starts reading the repository.</summary>
    public void Start() => Reload();

    // ---- Loading ---------------------------------------------------------------------------------

    private void Reload()
    {
        if (_loading) { _reloadQueued = true; return; }
        _loading = true;
        _refreshedAt = Environment.TickCount64;
        string? requested = _baseOverride;
        _background.Run(() => _source.Load(requested), snapshot =>
        {
            _loading = false;
            Apply(snapshot);
            if (_reloadQueued) { _reloadQueued = false; Reload(); }
        });
    }

    private void Apply(ReviewSnapshot snapshot)
    {
        _snapshot = snapshot;
        _comments.OnSnapshot(snapshot);
        if (_commit is { } commit && !snapshot.Commits.Any(c => c.Sha == commit.Sha))
        {
            _commit = null;
            _commitFiles = [];
        }
        RebuildList(CompareFocus.Keep);
        StartPrediction(snapshot);
        if (_pickOnStart) { _pickOnStart = false; OpenPicker(); }
    }

    private IReadOnlyList<ReviewFile> CurrentFiles()
    {
        if (_commit is not null) return _commitFiles;
        if (_snapshot is not { State: ReviewState.Ok } s) return [];
        return _uncommitted ? s.WithUncommitted : s.Committed;
    }

    private ReviewScope CurrentScope
    {
        get
        {
            if (_commit is { } commit) return new ReviewScope(ReviewScopeKind.Commit, commit);
            return new ReviewScope(_uncommitted ? ReviewScopeKind.Uncommitted : ReviewScopeKind.Branch);
        }
    }

    /// <summary>A new set of files: the same file stays selected when it's still there.</summary>
    private void RebuildList(CompareFocus focus)
    {
        _list = new ReviewFileList(CurrentFiles());
        int index = _selectedPath is null ? -1 : _list.IndexOfPath(_selectedPath);
        bool kept = index >= 0;
        if (!kept) index = _list.Count > 0 ? 0 : -1;
        _selected = index;
        _selectedPath = index >= 0 ? _list.Files[index].Path : null;
        if (index < 0)
        {
            _pane.Clear();
            return;
        }
        LoadSelected(kept ? focus : CompareFocus.First);
    }

    /// <summary>One load at a time: holding j over a long list ends on the file it stopped at, without a git process per row.</summary>
    private void LoadSelected(CompareFocus focus)
    {
        _wantFile = true;
        _wantFocus = focus;
        if (!_fileInFlight) StartLoad();
    }

    private void StartLoad()
    {
        if (_snapshot is not { } snapshot || Selected is not { } file) { _wantFile = false; return; }
        _wantFile = false;
        _fileInFlight = true;
        var scope = CurrentScope;
        var focus = _wantFocus;
        _background.Run(() => _source.Sides(snapshot, file, scope), sides =>
        {
            _fileInFlight = false;
            if (_wantFile || !ReferenceEquals(Selected, file) && Selected?.Path != file.Path) { StartLoad(); return; }
            _pane.Show(sides.Left, sides.Right, focus);
            _comments.Publish();
            if (_pane.Settled) RunPendingStep();
        });
    }

    private void OnComputed()
    {
        _comments.Publish();
        if (!_fileInFlight && !_wantFile) RunPendingStep();
    }

    private void RunPendingStep()
    {
        if (_pendingStep == 0) return;
        int direction = _pendingStep;
        _pendingStep = 0;
        StepChange(direction);
    }

    private void StartPrediction(ReviewSnapshot s)
    {
        if (s.State != ReviewState.Ok) { _prediction = null; _conflictFiles.Clear(); return; }
        if (_predictedFor == (s.HeadOid, s.BaseOid)) return;
        _predictedFor = (s.HeadOid, s.BaseOid);
        _prediction = null;
        _conflictFiles.Clear();
        int generation = ++_predictionGeneration;
        _background.Run(() => _source.Predict(s), prediction =>
        {
            if (generation != _predictionGeneration) return;
            _prediction = prediction;
            _conflictFiles = new HashSet<string>(prediction.Files, StringComparer.Ordinal);
        });
    }

    // ---- Moving ----------------------------------------------------------------------------------

    private void Select(int index, CompareFocus focus)
    {
        if (index < 0 || index >= _list.Count) return;
        _selected = index;
        _selectedPath = _list.Files[index].Path;
        LoadSelected(focus);
    }

    /// <summary>Next or previous change; past the last one of a file, the next file.</summary>
    private void StepChange(int direction)
    {
        _status = "";
        if (_list.Count == 0) return;
        if (_fileInFlight || _wantFile || !_pane.Settled) { _pendingStep = direction; return; }
        if (_pane.Step(direction, wrap: false)) return;
        int next = _list.Step(_selected, direction);
        if (next < 0)
        {
            next = direction > 0 ? 0 : _list.Count - 1;
            _status = direction > 0 ? "Back at the first file" : "Back at the last file";
            _statusIsError = false;
        }
        Select(next, direction > 0 ? CompareFocus.First : CompareFocus.Last);
    }

    private void MoveFile(int delta)
    {
        if (_list.Count == 0) return;
        Select(Math.Clamp(_selected + delta, 0, _list.Count - 1), CompareFocus.First);
    }

    private void ToggleUncommitted()
    {
        if (_commit is not null) { Say("A single commit has no uncommitted changes"); return; }
        _uncommitted = !_uncommitted;
        RebuildList(CompareFocus.Keep);
    }

    private void Say(string text, bool error = false) { _status = text; _statusIsError = error; }

    // ---- Commits ---------------------------------------------------------------------------------

    private void OpenCommits()
    {
        if (_snapshot is not { State: ReviewState.Ok, Commits.Count: > 0 } s)
        {
            Say("No commits on this branch against its base");
            return;
        }
        _commitsOpen = true;
        _commitCursor = _commit is { } c ? 1 + IndexOfCommit(s, c) : 0;
    }

    private static int IndexOfCommit(ReviewSnapshot s, ReviewCommit commit)
    {
        for (int i = 0; i < s.Commits.Count; i++) if (s.Commits[i].Sha == commit.Sha) return i;
        return -1;
    }

    private void ChooseCommit(int row)
    {
        _commitsOpen = false;
        if (_snapshot is not { } s) return;
        if (row <= 0 || row > s.Commits.Count) { BackToBranch(); return; }
        var commit = s.Commits[row - 1];
        if (_commit?.Sha == commit.Sha) return;
        _commit = commit;
        _commitFiles = [];
        _selectedPath = null;
        RebuildList(CompareFocus.First);
        _background.Run(() => _source.CommitFiles(s, commit), files =>
        {
            if (_commit?.Sha != commit.Sha) return;
            _commitFiles = files;
            RebuildList(CompareFocus.First);
        });
    }

    private void BackToBranch()
    {
        if (_commit is null) return;
        _commit = null;
        _commitFiles = [];
        _selectedPath = null;
        RebuildList(CompareFocus.First);
    }

    // ---- Keys ------------------------------------------------------------------------------------

    public void OnKey(KeyEvent key)
    {
        if (OnModalKey(key)) return;
        if (key.Key == TuiKey.Char && key.Ctrl && key.Rune == 'c') { Done = true; return; }
        ClearStatusAfter(key);
        _comments.BeforeKey(key);

        if (_commitsOpen && OnCommitsKey(key)) return;
        if (key.Key == TuiKey.Esc && _pane.HasMark) { _pane.ClearMark(); return; }

        if (OnLeaveOrFocusKey(key)) return;
        if (key.Key == TuiKey.Char && !key.Ctrl && (OnCommandKey((char)key.Rune) || _comments.OnCommandKey((char)key.Rune))) return;
        if (_focusList && OnListKey(key)) return;
        _pane.OnKey(key);
    }

    /// <summary>An open dialog or popup takes the key before anything else.</summary>
    private bool OnModalKey(KeyEvent key)
    {
        if (_comments.ModalOpen) { _comments.OnModalKey(key); return true; }
        if (_pickerOpen) { OnPickerKey(key); return true; }
        return _pane.RulesOpen && _pane.OnKey(key);
    }

    private void ClearStatusAfter(KeyEvent key)
    {
        if (key.Key != TuiKey.Char || key.Rune != 'q') _status = _fetching ? _status : "";
    }

    private bool OnLeaveOrFocusKey(KeyEvent key)
    {
        if (key.Key == TuiKey.Esc)
        {
            if (_commit is not null) BackToBranch();
            else Done = true;
            return true;
        }
        if (key.Is('q')) { Done = true; return true; }
        if (key.Key == TuiKey.Tab || key.Key == TuiKey.BackTab) { _focusList = !_focusList; return true; }
        return false;
    }

    private bool OnCommandKey(char letter)
    {
        switch (letter)
        {
            case ']' or 'n': StepChange(+1); return true;
            case '[' or 'p': StepChange(-1); return true;
            case '}' or 'J': MoveFile(+1); return true;
            case '{' or 'K': MoveFile(-1); return true;
            case 'u': ToggleUncommitted(); return true;
            case 'c': OpenCommits(); return true;
            case 'b': OpenPicker(); return true;
            case 'F': Fetch(); return true;
            case 'f': Finish(); return true;
            case 'r': Say("Refreshing..."); Reload(); return true;
        }
        return false;
    }

    private bool OnListKey(KeyEvent key)
    {
        switch (key.Key)
        {
            case TuiKey.Enter: _focusList = false; return true;
            case TuiKey.Down: MoveFile(+1); return true;
            case TuiKey.Up: MoveFile(-1); return true;
            case TuiKey.PageDown: MoveFile(_lastListRows - 1); return true;
            case TuiKey.PageUp: MoveFile(-(_lastListRows - 1)); return true;
            case TuiKey.Home: Select(0, CompareFocus.First); return true;
            case TuiKey.End: Select(_list.Count - 1, CompareFocus.First); return true;
        }
        if (key.Is('j')) { MoveFile(+1); return true; }
        if (key.Is('k')) { MoveFile(-1); return true; }
        if (key.Is('g')) { Select(0, CompareFocus.First); return true; }
        if (key.Is('G')) { Select(_list.Count - 1, CompareFocus.First); return true; }
        return false;
    }

    private bool OnCommitsKey(KeyEvent key)
    {
        int count = (_snapshot?.Commits.Count ?? 0) + 1;
        switch (key.Key)
        {
            case TuiKey.Esc: _commitsOpen = false; return true;
            case TuiKey.Enter: ChooseCommit(_commitCursor); return true;
            case TuiKey.Down: _commitCursor = Math.Min(_commitCursor + 1, count - 1); return true;
            case TuiKey.Up: _commitCursor = Math.Max(_commitCursor - 1, 0); return true;
            case TuiKey.PageDown: _commitCursor = Math.Min(_commitCursor + _lastListRows - 1, count - 1); return true;
            case TuiKey.PageUp: _commitCursor = Math.Max(_commitCursor - _lastListRows + 1, 0); return true;
            case TuiKey.Home: _commitCursor = 0; return true;
            case TuiKey.End: _commitCursor = count - 1; return true;
        }
        if (key.Is('j')) { _commitCursor = Math.Min(_commitCursor + 1, count - 1); return true; }
        if (key.Is('k')) { _commitCursor = Math.Max(_commitCursor - 1, 0); return true; }
        if (key.Is('c')) { _commitsOpen = false; return true; }
        return false;
    }

    // ---- Fetch, Finish ---------------------------------------------------------------------------

    private void Fetch()
    {
        if (_fetching || _snapshot is not { } s) return;
        if (!s.HasOrigin) { Say("This repository has no origin to fetch from", error: true); return; }
        _fetching = true;
        _fetchError = null;
        _background.Run(() => _source.Fetch(s), error =>
        {
            _fetching = false;
            _fetchError = error;
            if (error is null) Say($"Fetched origin {s.Base}");
            Reload();
        });
    }

    private void Finish()
    {
        if (_snapshot is not { } s) return;
        if (!s.LinkedWorktree) { Say("Finish Worktree is for a linked worktree; this is the main one"); return; }
        _background.Run(() => _source.RequestFinish(s), problem =>
        {
            if (problem is null) Say("Finish Worktree is open");
            else Say(problem, error: true);
        });
    }

    // ---- Base picker -----------------------------------------------------------------------------

    private void OpenPicker()
    {
        if (_snapshot is not { } s) return;
        _pickerOpen = true;
        _pickerFilter = "";
        _pickerCursor = 0;
        _pickerBranches = null;
        _background.Run(() => _source.Branches(s), branches => { if (_pickerOpen) _pickerBranches = branches; });
    }

    private List<string> PickerMatches()
    {
        var all = _pickerBranches ?? [];
        return [.. all.Where(b => b.Contains(_pickerFilter, StringComparison.OrdinalIgnoreCase))];
    }

    private void OnPickerKey(KeyEvent key)
    {
        var matches = PickerMatches();
        switch (key.Key)
        {
            case TuiKey.Esc: _pickerOpen = false; return;
            case TuiKey.Enter:
                if (_pickerCursor < matches.Count) ChooseBase(matches[_pickerCursor]);
                return;
            case TuiKey.Down: _pickerCursor = Math.Min(_pickerCursor + 1, Math.Max(0, matches.Count - 1)); return;
            case TuiKey.Up: _pickerCursor = Math.Max(_pickerCursor - 1, 0); return;
            case TuiKey.Backspace:
                if (_pickerFilter.Length > 0) { _pickerFilter = _pickerFilter[..^1]; _pickerCursor = 0; }
                return;
            case TuiKey.Char when key.Ctrl && key.Rune == 'c': Done = true; return;
            case TuiKey.Char when !key.Ctrl && !key.Alt && key.Rune >= 0x20:
                _pickerFilter += char.ConvertFromUtf32(key.Rune);
                _pickerCursor = 0;
                return;
        }
    }

    private void ChooseBase(string branch)
    {
        _pickerOpen = false;
        _baseOverride = branch;
        _selectedPath = null;
        _commit = null;
        _commitFiles = [];
        Reload();
    }

    // ---- Mouse -----------------------------------------------------------------------------------

    public void OnMouse(MouseEvent mouse, Frame? lastFrame)
    {
        if (lastFrame is null) return;
        bool overList = mouse.X >= _listRect.X && mouse.X < _listRect.X + _listRect.W && mouse.Y >= _listRect.Y && mouse.Y < _listRect.Y + _listRect.H;
        switch (mouse.Kind)
        {
            case MouseKind.WheelUp when overList: ScrollList(-3); return;
            case MouseKind.WheelDown when overList: ScrollList(3); return;
            case MouseKind.WheelUp: _pane.Scroll(-3); return;
            case MouseKind.WheelDown: _pane.Scroll(3); return;
            case MouseKind.Press when mouse.Button == 0:
                if (lastFrame.HitAt(mouse.X, mouse.Y) is not { } hit) return;
                if (!OnHostHit(hit)) _pane.OnHit(hit, mouse);
                return;
        }
    }

    private void ScrollList(int delta)
    {
        if (_commitsOpen) _commitTop = Math.Max(0, _commitTop + delta);
        else _listTop = Math.Max(0, _listTop + delta);
    }

    private bool OnHostHit(HitRegion hit)
    {
        switch (hit.Action)
        {
            case ActFile: Select(hit.Arg, CompareFocus.First); return true;
            case ActCommit: ChooseCommit(hit.Arg); return true;
            case ActUncommitted: ToggleUncommitted(); return true;
            case ActCommits:
                if (_commitsOpen) _commitsOpen = false;
                else OpenCommits();
                return true;
            case ActFinish: Finish(); return true;
            case ActFetch: Fetch(); return true;
            case ActBase: OpenPicker(); return true;
            case ActSend: _comments.BeginSend(); return true;
            case ActPicked:
                var matches = PickerMatches();
                if (hit.Arg >= 0 && hit.Arg < matches.Count) ChooseBase(matches[hit.Arg]);
                return true;
        }
        return false;
    }

    // ---- Drawing ---------------------------------------------------------------------------------

    public void Render(Frame frame)
    {
        frame.Clear(_theme.Text, _theme.Base);
        string? note = NoteText(out bool noteError);
        var layout = ReviewLayout.Compute(frame.Width, frame.Height, note is not null);

        DrawHeader(frame);
        int y = 1;
        if (layout.Toolbar) _pane.DrawToolbar(frame, 0, y++, frame.Width);
        if (layout.Note && note is not null) DrawNote(frame, y++, note, noteError);
        DrawFooter(frame);

        int bodyRows = frame.Height - 1 - y;
        if (bodyRows <= 0) return;

        if (_snapshot is null) { DrawCentered(frame, y, bodyRows, "Reading the repository..."); return; }
        if (_list.Count == 0 && !_commitsOpen)
        {
            _listRect = default;
            DrawEmpty(frame, y, bodyRows);
            RenderPopups(frame, y);
            return;
        }

        switch (layout.Mode)
        {
            case ReviewLayoutMode.Wide:
                DrawLeft(frame, 0, y, layout.ListWidth, bodyRows);
                for (int r = y; r < y + bodyRows; r++) frame.Put(layout.ListWidth, r, "│", _theme.Surface1, _theme.Base);
                _pane.Render(frame, layout.ListWidth + 1, y, frame.Width - layout.ListWidth - 1, bodyRows);
                break;
            case ReviewLayoutMode.Stacked:
                int listRows = Math.Min(layout.ListHeight, bodyRows - 4);
                DrawLeft(frame, 0, y, frame.Width, listRows);
                _pane.Render(frame, 0, y + listRows, frame.Width, bodyRows - listRows);
                break;
            default:
                if (_focusList || _commitsOpen) DrawLeft(frame, 0, y, frame.Width, bodyRows);
                else
                {
                    _listRect = default;
                    DrawCurrentFile(frame, y);
                    _pane.Render(frame, 0, y + 1, frame.Width, bodyRows - 1);
                }
                break;
        }
        RenderPopups(frame, y);
    }

    private void RenderPopups(Frame f, int y)
    {
        _pane.RenderPopups(f, y);
        if (_pickerOpen) DrawPicker(f);
        _comments.RenderPopups(f);
    }

    private string? NoteText(out bool error)
    {
        error = false;
        if (_snapshot is not { } s) return null;
        if (_fetching) return $"Fetching: git fetch origin {FetchName(s)} ...";
        if (_fetchError is { } problem) { error = true; return $"Fetch failed: {problem}"; }
        var parts = new List<string>();
        if (ReviewRules.BehindNote(s.Base, s.BehindCount) is { } behind)
            parts.Add($"{behind}  (F fetch{(s.RemoteBase is { } r ? $", b compare with {r}" : "")})");
        if (s.Operation is { } operation) parts.Add(operation);
        if (s.OnBase && _list.Count > 0) parts.Add($"On {s.Base}: showing only uncommitted changes (b compares with another branch)");
        return parts.Count == 0 ? null : string.Join("   ·   ", parts);
    }

    private static string FetchName(ReviewSnapshot s) => s.Base.StartsWith("origin/", StringComparison.Ordinal) ? s.Base["origin/".Length..] : s.Base;

    private void DrawNote(Frame f, int y, string note, bool error)
    {
        f.Fill(0, y, f.Width, 1, _theme.Text, _theme.Mantle);
        f.Text(1, y, f.Width - 2, note, error ? _theme.Red : _theme.Peach, _theme.Mantle);
    }

    private void DrawHeader(Frame f)
    {
        f.Fill(0, 0, f.Width, 1, _theme.Text, _theme.Mantle);
        int right = f.Width - 1;

        if (_snapshot is not { State: ReviewState.Ok } s)
        {
            f.Text(1, 0, f.Width - 2, "Review", _theme.Text, _theme.Mantle, CellAttr.Bold);
            return;
        }

        if (s.LinkedWorktree) right = RightPill(f, right, "f Finish worktree", _theme.AutoBg, _theme.Green, ActFinish);
        if (_comments.Count > 0) right = RightPill(f, right, $"s send {Counted.Of(_comments.Count, "comment")}", _theme.CustomBg, _theme.Mauve, ActSend);
        right = DrawConflictPill(f, right);

        int x = 1;
        x = f.Text(x, 0, right - x, s.Base, _theme.Blue, _theme.Mantle, CellAttr.Bold);
        x = f.Text(x, 0, right - x, " ← ", _theme.Overlay, _theme.Mantle);
        x = f.Text(x, 0, right - x, s.Branch, _theme.Peach, _theme.Mantle, CellAttr.Bold);
        if (_commit is { } c)
        {
            x = f.Text(x, 0, right - x, "  commit ", _theme.Overlay, _theme.Mantle);
            x = f.Text(x, 0, right - x, c.ShortSha, _theme.Mauve, _theme.Mantle);
            x = f.Text(x + 1, 0, Math.Min(right - x - 1, 28), c.Subject, _theme.Subtext, _theme.Mantle);
        }

        x = DrawCounts(f, x + 2, right);
        if (_commit is null) x = Pill(f, x, right, _uncommitted ? $"u uncommitted on ({s.UncommittedCount})" : "u uncommitted off", _uncommitted ? _theme.Peach : _theme.Surface0, _uncommitted ? _theme.OnAccent : _theme.Text, ActUncommitted);
        Pill(f, x, right, $"c {Counted.Of(s.Commits.Count, "commit")}", _commitsOpen || _commit is not null ? _theme.Mauve : _theme.Surface0, _commitsOpen || _commit is not null ? _theme.OnAccent : _theme.Text, ActCommits);
    }

    private int DrawCounts(Frame f, int x, int right)
    {
        int added = 0, deleted = 0;
        foreach (var file in _list.Files) { added += Math.Max(0, file.Added); deleted += Math.Max(0, file.Deleted); }
        x = f.Text(x, 0, Math.Max(0, right - x), Counted.Of(_list.Count, "file"), _theme.Subtext, _theme.Mantle);
        x = f.Text(x + 1, 0, Math.Max(0, right - x - 1), $"+{added}", _theme.Green, _theme.Mantle);
        return f.Text(x + 1, 0, Math.Max(0, right - x - 1), $"-{deleted}", _theme.Red, _theme.Mantle) + 2;
    }

    private void DrawCounts(Frame f, int x, int y, string counts, uint bg)
    {
        int plus = counts.IndexOf(' ');
        if (plus > 0)
        {
            f.Text(x, y, plus, counts.AsSpan(0, plus), _theme.Green, bg);
            f.Text(x + plus, y, counts.Length - plus, counts.AsSpan(plus), _theme.Red, bg);
            return;
        }
        uint countColor = counts[0] switch
        {
            '+' => _theme.Green,
            '-' => _theme.Red,
            _ => _theme.Overlay,
        };
        f.Text(x, y, counts.Length, counts, countColor, bg);
    }

    private int DrawConflictPill(Frame f, int right)
    {
        if (_prediction is not { } p)
            return RightLabel(f, right, "checking merge...", _theme.Overlay);
        if (!p.Known) return RightLabel(f, right, Trim(p.Note ?? "no prediction", 34), _theme.Overlay);
        if (p.Clean) return RightLabel(f, right, "merges cleanly", _theme.Green);
        string label = p.Files.Count == 0 ? "predicted conflicts" : $"{Counted.Of(p.Files.Count, "predicted conflict")}";
        return RightPill(f, right, label, _theme.ConflictBg, _theme.Red, 0);
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private int RightLabel(Frame f, int right, string label, uint color)
    {
        int x = right - label.Length - 1;
        if (x < 1) return right;
        f.Text(x, 0, label.Length, label, color, _theme.Mantle);
        return x - 2;
    }

    private static int RightPill(Frame f, int right, string label, uint bg, uint fg, int action)
    {
        int width = label.Length + 2;
        int x = right - width - 1;
        if (x < 1) return right;
        f.Fill(x, 0, width, 1, fg, bg);
        f.Text(x + 1, 0, width - 1, label, fg, bg);
        if (action != 0) f.AddHit(x, 0, width, 1, action);
        return x - 2;
    }

    private static int Pill(Frame f, int x, int limit, string label, uint bg, uint fg, int action)
    {
        int width = label.Length + 2;
        if (x + width >= limit) return x;
        f.Fill(x, 0, width, 1, fg, bg);
        f.Text(x + 1, 0, width - 1, label, fg, bg);
        f.AddHit(x, 0, width, 1, action);
        return x + width + 1;
    }

    private void DrawFooter(Frame f)
    {
        int y = f.Height - 1;
        f.Fill(0, y, f.Width, 1, _theme.Subtext, _theme.Crust);
        string[] items = ["] [ change", "J K file", "Tab list", "a comment", "v range", "x delete", "s send", "u uncommitted", "c commits", "b base", "F fetch", "f finish", "d view", "i ignore", "r refresh", "q close"];
        int limit = _status.Length > 0 ? Math.Max(1, f.Width - _status.Length - 3) : f.Width;
        int x = 1;
        foreach (string item in items)
        {
            if (x + item.Length + 2 >= limit) break;
            x = f.Text(x, y, Math.Max(0, limit - x), item, _theme.Subtext, _theme.Crust) + 2;
        }
        if (_status.Length > 0)
        {
            int sx = Math.Max(1, f.Width - _status.Length - 2);
            f.Text(sx, y, f.Width - sx - 1, _status, _statusIsError ? _theme.Red : _theme.Text, _theme.Crust);
        }
    }

    private void DrawCentered(Frame f, int top, int rows, string message)
    {
        int y = top + Math.Max(0, rows / 2 - 1);
        int x = Math.Max(1, (f.Width - message.Length) / 2);
        f.Text(x, y, f.Width - x - 1, message, _theme.Subtext, _theme.Base);
    }

    private void DrawEmpty(Frame f, int top, int rows)
    {
        string message = _snapshot is { } s ? ReviewRules.EmptyMessage(s) : "";
        if (_commit is not null) message = _loading ? "Reading the commit..." : "This commit changes no files.";
        int y = top + Math.Max(0, rows / 2 - 2);
        int x = Math.Max(1, (f.Width - message.Length) / 2);
        f.Text(x, y, f.Width - x - 1, message, _theme.Text, _theme.Base, CellAttr.Bold);
        const string Hint = "b  compare with another branch     r  refresh     q  close";
        int hx = Math.Max(1, (f.Width - Hint.Length) / 2);
        f.Text(hx, y + 2, f.Width - hx - 1, Hint, _theme.Subtext, _theme.Base);
    }

    /// <summary>The left column: the commit list while it's open, otherwise the files.</summary>
    private void DrawLeft(Frame f, int x, int y, int width, int height)
    {
        _listRect = (x, y, width, height);
        if (_commitsOpen) DrawCommits(f, x, y, width, height);
        else DrawFiles(f, x, y, width, height);
    }

    private void DrawListTitle(Frame f, int x, int y, int width, string title, bool focused)
    {
        f.Fill(x, y, width, 1, _theme.Subtext, _theme.Mantle);
        f.Text(x + 1, y, width - 2, title, focused ? _theme.Lavender : _theme.Subtext, _theme.Mantle, CellAttr.Bold);
    }

    private void DrawFiles(Frame f, int x, int y, int width, int height)
    {
        f.Fill(x, y, width, height, _theme.Text, _theme.Mantle);
        DrawListTitle(f, x, y, width, _list.Count == 1 ? "1 FILE" : $"{_list.Count} FILES", _focusList);
        int visible = height - 1;
        _lastListRows = Math.Max(1, visible);
        if (visible <= 0) return;

        var rows = _list.Rows;
        int selectedRow = _list.RowOfFile(_selected);
        if (selectedRow >= 0)
        {
            if (selectedRow < _listTop) _listTop = Math.Max(0, selectedRow > 0 && rows[selectedRow - 1].IsFolder ? selectedRow - 1 : selectedRow);
            if (selectedRow >= _listTop + visible) _listTop = selectedRow - visible + 1;
        }
        _listTop = Math.Clamp(_listTop, 0, Math.Max(0, rows.Count - visible));

        for (int i = 0; i < visible && _listTop + i < rows.Count; i++)
        {
            var row = rows[_listTop + i];
            int ry = y + 1 + i;
            if (row.IsFolder)
            {
                f.Text(x + 1, ry, width - 2, Tail(row.Text, width - 2), _theme.Subtext, _theme.Mantle, CellAttr.Bold);
                continue;
            }
            DrawFileRow(f, x, ry, width, _list.Files[row.File], row.Text, row.File == _selected);
            f.AddHit(x, ry, width, 1, ActFile, row.File);
        }
    }

    private static string Tail(string text, int width) =>
        text.Length <= width || width < 2 ? text : "…" + text[(text.Length - width + 1)..];

    private void DrawFileRow(Frame f, int x, int y, int width, ReviewFile file, string name, bool selected)
    {
        uint bg = selected ? _theme.Surface0 : _theme.Mantle;
        f.Fill(x, y, width, 1, _theme.Text, bg);
        if (selected) f.Put(x, y, "▌", _focusList ? _theme.Lavender : _theme.Surface1, bg);
        if (file.Uncommitted) f.Put(x + 1, y, "•", _theme.Peach, bg);
        f.Put(x + 2, y, file.Letter, ChangeColor(file.Change), bg, CellAttr.Bold);
        string counts = CountsText(file);
        int nameMax = width - 5 - (counts.Length + 2);
        if (_comments.CountIn(file.Path) > 0) f.Put(x + 3, y, "▪", _theme.Mauve, bg);
        uint nameColor = selected ? _theme.Text : _theme.Subtext;
        if (_conflictFiles.Contains(file.Path)) nameColor = _theme.Red;
        int nx = f.Text(x + 4, y, nameMax, name, nameColor, bg);
        if (file.OldPath is { } old && nx < x + 4 + nameMax - 3)
            f.Text(nx, y, x + 4 + nameMax - nx, $" ← {FileName(old)}", _theme.Overlay, bg);
        if (counts.Length > 0) DrawCounts(f, x + width - counts.Length - 1, y, counts, bg);
    }

    private uint ChangeColor(ReviewChange change) => change switch
    {
        ReviewChange.Added => _theme.Green,
        ReviewChange.Deleted or ReviewChange.Unmerged => _theme.Red,
        ReviewChange.Renamed or ReviewChange.Copied => _theme.Mauve,
        _ => _theme.Blue,
    };

    private static string FileName(string path) { int s = path.LastIndexOf('/'); return s < 0 ? path : path[(s + 1)..]; }

    private static string CountsText(ReviewFile f)
    {
        if (f.Binary) return "binary";
        if (f.Added <= 0 && f.Deleted <= 0) return "";
        if (f.Deleted <= 0) return $"+{f.Added}";
        if (f.Added <= 0) return $"-{f.Deleted}";
        return $"+{f.Added} -{f.Deleted}";
    }

    private void DrawCurrentFile(Frame f, int y)
    {
        f.Fill(0, y, f.Width, 1, _theme.Text, _theme.Mantle);
        if (Selected is not { } file) return;
        string position = $"{_selected + 1}/{_list.Count} ";
        int x = f.Text(1, y, f.Width - 2, position, _theme.Subtext, _theme.Mantle);
        x = f.Text(x, y, f.Width - x - 1, file.Letter + " ", _theme.Blue, _theme.Mantle, CellAttr.Bold);
        string counts = CountsText(file);
        f.Text(x, y, f.Width - x - counts.Length - 2, Tail(file.Path, f.Width - x - counts.Length - 2), _theme.Text, _theme.Mantle);
        if (counts.Length > 0) f.Text(f.Width - counts.Length - 1, y, counts.Length, counts, _theme.Overlay, _theme.Mantle);
    }

    private void DrawCommits(Frame f, int x, int y, int width, int height)
    {
        f.Fill(x, y, width, height, _theme.Text, _theme.Mantle);
        DrawListTitle(f, x, y, width, "COMMITS  Enter choose  Esc back", true);
        int visible = height - 1;
        _lastListRows = Math.Max(1, visible);
        if (visible <= 0 || _snapshot is not { } s) return;
        int count = s.Commits.Count + 1;
        if (_commitCursor < _commitTop) _commitTop = _commitCursor;
        if (_commitCursor >= _commitTop + visible) _commitTop = _commitCursor - visible + 1;
        _commitTop = Math.Clamp(_commitTop, 0, Math.Max(0, count - visible));
        for (int i = 0; i < visible && _commitTop + i < count; i++) DrawCommitRow(f, x, y + 1 + i, width, s, _commitTop + i);
    }

    /// <summary>Row 0 is "all changes"; the rows after it are the snapshot's commits.</summary>
    private void DrawCommitRow(Frame f, int x, int y, int width, ReviewSnapshot s, int row)
    {
        bool selected = row == _commitCursor;
        uint bg = selected ? _theme.Surface0 : _theme.Mantle;
        f.Fill(x, y, width, 1, _theme.Text, bg);
        if (selected) f.Put(x, y, "▌", _theme.Lavender, bg);
        if (row == 0)
        {
            f.Text(x + 2, y, width - 3, "All changes on the branch", _commit is null ? _theme.Green : _theme.Text, bg, CellAttr.Bold);
        }
        else
        {
            var commit = s.Commits[row - 1];
            int nx = f.Text(x + 2, y, width - 3, commit.ShortSha, _commit?.Sha == commit.Sha ? _theme.Green : _theme.Mauve, bg);
            f.Text(nx + 1, y, x + width - nx - 2, commit.Subject, _theme.Text, bg);
        }
        f.AddHit(x, y, width, 1, ActCommit, row);
    }

    private void DrawPicker(Frame f)
    {
        int width = Math.Min(PickerWidth, f.Width - 4);
        int height = Math.Min(PickerRows + 4, f.Height - 2);
        int x = Math.Max(0, (f.Width - width) / 2), y = Math.Max(0, (f.Height - height) / 2);
        f.Fill(x, y, width, height, _theme.Text, _theme.Surface0);
        f.Text(x + 2, y + 1, width - 4, "Compare with branch", _theme.Subtext, _theme.Surface0, CellAttr.Bold);
        int promptX = f.Text(x + 2, y + 2, 2, "> ", _theme.Lavender, _theme.Surface0);
        f.Text(promptX, y + 2, x + width - promptX - 2, _pickerFilter.Length > 0 ? _pickerFilter : "", _theme.Text, _theme.Surface0);
        if (_pickerBranches is null) { f.Text(x + 2, y + 4, width - 4, "Reading branches...", _theme.Overlay, _theme.Surface0); return; }
        var matches = PickerMatches();
        int rows = height - 4;
        int top = Math.Clamp(_pickerCursor - rows + 1, 0, Math.Max(0, matches.Count - rows));
        for (int i = 0; i < rows && top + i < matches.Count; i++)
        {
            int ry = y + 3 + i;
            bool selected = top + i == _pickerCursor;
            uint bg = selected ? _theme.Surface1 : _theme.Surface0;
            f.Fill(x + 1, ry, width - 2, 1, _theme.Text, bg);
            f.Text(x + 2, ry, width - 4, matches[top + i], _theme.Text, bg);
            f.AddHit(x + 1, ry, width - 2, 1, ActPicked, top + i);
        }
    }
}
