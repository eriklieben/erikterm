namespace ErikTerm.Ui.Compare;

/// <summary>What the comments need from the review around them.</summary>
internal interface IReviewCommentView
{
    ReviewSnapshot? Snapshot { get; }
    ReviewScope Scope { get; }
    ReviewFile? Selected { get; }
    TuiTheme Theme { get; }
    void Say(string text, bool error = false);
}

/// <summary>
/// Comments on a review: writing, editing and deleting them, showing them under their lines in the <see cref="ComparePane"/>,
/// and sending them all to a coding agent in the worktree. Nothing is sent without Enter in the confirmation that names
/// the pane; comments stay until the agent's pane took them, and across runs through <see cref="IReviewCommentHost"/>.
/// </summary>
internal sealed class ReviewCommentController(IBackground background, IReviewSource source, IReviewCommentHost host, ComparePane pane, IReviewCommentView view)
{
    private const int EditorWidth = 72, EditorMaxRows = 8, SendWidth = 84, SendPreviewRows = 10;

    private sealed record Draft(long? EditingId, string Path, ReviewCommentSide Side, int StartLine, int EndLine, string[] Quoted, string? Commit, string Title);

    private sealed record SendPlan(string Prompt, int Count, IReadOnlyList<ReviewAgentTarget> Targets);

    private readonly List<ReviewComment> _comments = [];
    private long _nextId = 1;
    private (string Root, string Branch)? _key;

    private CommentEditor? _editor;
    private Draft? _draft;
    private bool _discardArmed;
    private long? _deleteArmed;

    private SendPlan? _plan;
    private int _targetCursor;
    private bool _busy;

    public int Count => _comments.Count;
    public IReadOnlyList<ReviewComment> All => _comments;
    /// <summary>A popup has the keyboard.</summary>
    public bool ModalOpen => _editor is not null || _plan is not null;
    public bool EditorOpen => _editor is not null;
    public bool SendOpen => _plan is not null;
    public string? EditorText => _editor?.Text;

    public int CountIn(string path)
    {
        int n = 0;
        foreach (var c in _comments) if (c.Path == path) n++;
        return n;
    }

    // ---- State ------------------------------------------------------------------------------------

    /// <summary>A new snapshot: comments belong to a worktree and branch, so another branch brings its own.</summary>
    public void OnSnapshot(ReviewSnapshot snapshot)
    {
        if (snapshot.State != ReviewState.Ok || _key == (snapshot.Root, snapshot.Branch)) return;
        _key = (snapshot.Root, snapshot.Branch);
        _comments.Clear();
        _nextId = 1;
        foreach (var c in host.Load(snapshot.Root, snapshot.Branch)) _comments.Add(c with { Id = _nextId++ });
        Publish();
    }

    /// <summary>Shows the selected file's comments under their lines, wherever those lines are now.</summary>
    public void Publish()
    {
        if (view.Selected is not { } file || pane.Right?.Title != file.Path) { pane.SetNotes([]); return; }
        string? commit = view.Scope.Commit?.Sha;
        var notes = new List<CompareNote>();
        foreach (var comment in _comments)
        {
            if (comment.Path != file.Path || comment.Commit != commit) continue;
            if ((comment.Side == ReviewCommentSide.New ? pane.Right : pane.Left)?.Document is not { Problem: DocumentProblem.None, RawLines.Length: > 0 } document) continue;
            notes.Add(NoteFor(comment, document));
        }
        pane.SetNotes(notes);
    }

    private static CompareNote NoteFor(ReviewComment comment, TextDocument document)
    {
        int? found = ReviewCommentRules.Locate(comment, document);
        int start = found ?? Math.Clamp(comment.StartLine, 1, document.RawLines.Length);
        int end = Math.Min(start + comment.EndLine - comment.StartLine, document.RawLines.Length);
        string label = ReviewCommentRules.LineLabel(start, end);
        if (comment.Side == ReviewCommentSide.Old) label = "old " + label;
        if (found is null) label += " outdated";
        bool isNew = comment.Side == ReviewCommentSide.New;
        return new CompareNote(comment.Id, isNew ? -1 : end - 1, isNew ? end - 1 : -1, label, comment.Body, found is null);
    }

    private void Persist()
    {
        if (_key is not { } key) return;
        if (host.Save(key.Root, key.Branch, _comments) is { } problem)
            view.Say($"Couldn't save the comments: {problem}", error: true);
    }

    // ---- Keys -------------------------------------------------------------------------------------

    /// <summary>Hook before any key: a delete is confirmed by the very next key.</summary>
    public void BeforeKey(KeyEvent key)
    {
        if (!key.Is('x')) _deleteArmed = null;
    }

    /// <summary>The comment commands; false when <paramref name="letter"/> isn't one of them.</summary>
    public bool OnCommandKey(char letter)
    {
        switch (letter)
        {
            case 'a' or 'e': OpenEditor(); return true;
            case 'v': ToggleRange(); return true;
            case 'x': Delete(); return true;
            case 's': BeginSend(); return true;
        }
        return false;
    }

    private void ToggleRange()
    {
        pane.ToggleMark();
        if (pane.HasMark) view.Say("Move to the end of the range, then a to comment on it");
    }

    public void OnModalKey(KeyEvent key)
    {
        if (_editor is not null) OnEditorKey(key);
        else OnSendKey(key);
    }

    public void OnPaste(string text) => _editor?.Insert(text);

    // ---- Writing ----------------------------------------------------------------------------------

    private void OpenEditor()
    {
        if (pane.CursorNote is { } id && _comments.Find(c => c.Id == id) is { } existing) { Edit(existing); return; }
        if (view.Selected is not { } file) { view.Say("No file to comment on"); return; }
        if (pane.SelectedRows() is not { } rows) { view.Say("Move to a line first: comments go on lines of a text diff"); return; }

        var right = rows.Where(r => r.Right >= 0).Select(r => r.Right).ToList();
        var side = right.Count > 0 ? ReviewCommentSide.New : ReviewCommentSide.Old;
        var lines = right.Count > 0 ? right : rows.Where(r => r.Left >= 0).Select(r => r.Left).ToList();
        if (lines.Count == 0) { view.Say("Move to a line first: comments go on lines of a text diff"); return; }
        int start = lines.Min() + 1, end = lines.Max() + 1;

        var document = (side == ReviewCommentSide.New ? pane.Right : pane.Left)!.Document!;
        string title = $"{file.Path}:{ReviewCommentRules.LineLabel(start, end)[1..]}";
        _draft = new Draft(null, file.Path, side, start, end, ReviewCommentRules.Quote(document, start, end), view.Scope.Commit?.Sha, title);
        _editor = new CommentEditor("");
        _discardArmed = false;
    }

    private void Edit(ReviewComment comment)
    {
        _draft = new Draft(comment.Id, comment.Path, comment.Side, comment.StartLine, comment.EndLine, comment.Quoted, comment.Commit,
            $"{comment.Path}:{ReviewCommentRules.LineLabel(comment.StartLine, comment.EndLine)[1..]}");
        _editor = new CommentEditor(comment.Body);
        _discardArmed = false;
    }

    private void OnEditorKey(KeyEvent key)
    {
        if (key.Key == TuiKey.Esc || (key.Key == TuiKey.Char && key.Ctrl && key.Rune == 'c')) { LeaveEditor(); return; }
        if (key.Key == TuiKey.Enter) { SaveDraft(); return; }
        _discardArmed = false;
        _editor!.OnKey(key, _editorTextWidth);
    }

    private void LeaveEditor()
    {
        if (_editor!.Changed && !_discardArmed)
        {
            _discardArmed = true;
            return;
        }
        CloseEditor();
    }

    private void CloseEditor()
    {
        _editor = null;
        _draft = null;
    }

    private void SaveDraft()
    {
        string body = _editor!.Text.Trim();
        var draft = _draft!;
        if (body.Length == 0)
        {
            CloseEditor();
            view.Say(draft.EditingId is null ? "Empty comment not added" : "Empty text: the comment is unchanged (x deletes it)");
            return;
        }
        if (draft.EditingId is { } id)
        {
            int index = _comments.FindIndex(c => c.Id == id);
            if (index >= 0) _comments[index] = _comments[index] with { Body = body };
        }
        else _comments.Add(new ReviewComment(_nextId++, draft.Path, draft.Side, draft.StartLine, draft.EndLine, draft.Quoted, body, draft.Commit));
        CloseEditor();
        pane.ClearMark();
        Persist();
        Publish();
        view.Say($"{Counted.Of(_comments.Count, "comment")} to send: s sends them");
    }

    private void Delete()
    {
        if (pane.CursorNote is not { } id) { view.Say("Move onto a comment to delete it"); return; }
        if (_deleteArmed != id)
        {
            _deleteArmed = id;
            view.Say("Press x again to delete this comment");
            return;
        }
        _deleteArmed = null;
        _comments.RemoveAll(c => c.Id == id);
        Persist();
        Publish();
        view.Say("Comment deleted");
    }

    // ---- Sending ----------------------------------------------------------------------------------

    /// <summary>Only opens the confirmation: the line numbers follow the files as they are now, and the agents are looked up now.</summary>
    public void BeginSend()
    {
        if (_comments.Count == 0) { view.Say("No comments to send: a adds one on the current line"); return; }
        if (_busy || view.Snapshot is not { State: ReviewState.Ok } snapshot) return;
        _busy = true;
        view.Say("Looking for the coding agent in this worktree...");
        var comments = _comments.ToArray();
        background.Run(() =>
        {
            var placed = Place(snapshot, comments);
            return (Prompt: ReviewPrompt.Format(snapshot.Branch, snapshot.Base, placed), Search: host.FindAgents(snapshot.Root));
        }, result =>
        {
            _busy = false;
            ShowTargets(result.Prompt, comments.Length, result.Search);
        });
    }

    private void ShowTargets(string prompt, int count, ReviewAgentSearch search)
    {
        if (search.Targets.Count == 0)
        {
            string why = search.Problem ?? "No coding agent is running in this worktree";
            view.Say($"{why}: your {(count == 1 ? "comment is" : "comments are")} kept", error: true);
            return;
        }
        view.Say("");
        _targetCursor = 0;
        _plan = new SendPlan(prompt, count, search.Targets);
    }

    /// <summary>Where each comment's lines are in the work tree, which is what the agent will open.</summary>
    private List<PlacedComment> Place(ReviewSnapshot snapshot, ReviewComment[] comments)
    {
        var scope = new ReviewScope(ReviewScopeKind.Uncommitted);
        var sides = new Dictionary<string, (CompareSide Left, CompareSide Right)?>(StringComparer.Ordinal);
        var placed = new List<PlacedComment>(comments.Length);
        foreach (var comment in comments)
        {
            if (comment.Commit is not null) { placed.Add(new PlacedComment(comment, comment.StartLine)); continue; } // a commit doesn't change
            if (!sides.TryGetValue(comment.Path, out var pair))
            {
                var file = snapshot.WithUncommitted.FirstOrDefault(f => f.Path == comment.Path);
                pair = file is null ? null : source.Sides(snapshot, file, scope);
                sides[comment.Path] = pair;
            }
            var side = SideOf(pair, comment.Side);
            int? line = side?.Document is { Problem: DocumentProblem.None } document ? ReviewCommentRules.Locate(comment, document) : null;
            placed.Add(new PlacedComment(comment, line));
        }
        return placed;
    }

    private static CompareSide? SideOf((CompareSide Left, CompareSide Right)? pair, ReviewCommentSide side)
    {
        if (pair is not { } sides) return null;
        return side == ReviewCommentSide.New ? sides.Right : sides.Left;
    }

    private static int CursorMove(KeyEvent key)
    {
        if (key.Key == TuiKey.Down || key.Is('j')) return 1;
        return key.Key == TuiKey.Up || key.Is('k') ? -1 : 0;
    }

    private void OnSendKey(KeyEvent key)
    {
        if (_busy) return;
        var plan = _plan!;
        if (key.Key == TuiKey.Esc || key.Is('q') || (key.Key == TuiKey.Char && key.Ctrl && key.Rune == 'c')) { _plan = null; return; }
        if (key.Key == TuiKey.Enter) { Deliver(plan, plan.Targets[_targetCursor]); return; }
        _targetCursor = Math.Clamp(_targetCursor + CursorMove(key), 0, plan.Targets.Count - 1);
    }

    private void Deliver(SendPlan plan, ReviewAgentTarget target)
    {
        _busy = true;
        background.Run(() => host.Send(target, plan.Prompt), problem =>
        {
            _busy = false;
            _plan = null;
            if (problem is not null)
            {
                view.Say($"{problem}. Your {(plan.Count == 1 ? "comment is" : "comments are")} kept", error: true);
                return;
            }
            _comments.Clear();
            Persist();
            Publish();
            view.Say($"Sent {Counted.Of(plan.Count, "comment")} to {target.Agent}, {target.Place}");
        });
    }

    // ---- Drawing ----------------------------------------------------------------------------------

    private int _editorTextWidth = EditorWidth - 4;

    public void RenderPopups(Frame f)
    {
        if (_editor is not null) DrawEditor(f);
        else if (_plan is not null) DrawSend(f, _plan);
    }

    private void DrawEditor(Frame f)
    {
        var theme = view.Theme;
        var editor = _editor!;
        int width = Math.Min(EditorWidth, f.Width - 4), inner = width - 4;
        _editorTextWidth = Math.Max(1, inner);
        var rows = TuiText.Wrap(editor.Text, _editorTextWidth);
        int textRows = Math.Clamp(rows.Count, 3, EditorMaxRows);
        int height = textRows + 4;
        int x = Math.Max(0, (f.Width - width) / 2), y = Math.Max(0, (f.Height - height) / 2);
        f.Fill(x, y, width, height, theme.Text, theme.Surface0);
        f.Text(x + 2, y + 1, inner, $"{(_draft!.EditingId is null ? "Comment on" : "Edit comment on")} {_draft.Title}", theme.Subtext, theme.Surface0, CellAttr.Bold);

        int caretRow = editor.RowOfCaret(_editorTextWidth);
        int top = Math.Clamp(caretRow - textRows + 1, 0, Math.Max(0, rows.Count - textRows));
        for (int r = 0; r < textRows && top + r < rows.Count; r++)
        {
            var (start, end) = rows[top + r];
            f.Text(x + 2, y + 2 + r, inner, editor.Text.AsSpan(start, end - start), theme.Text, theme.Surface0);
        }
        int caretX = x + 2 + TuiText.Width(editor.Text.AsSpan(rows[caretRow].Start, editor.Caret - rows[caretRow].Start));
        int caretY = y + 2 + caretRow - top;
        f.Put(Math.Min(caretX, x + width - 3), caretY, " ", theme.OnAccent, theme.Lavender);

        string hint = _discardArmed ? "Esc again discards this text  ·  Enter saves" : "Enter save  ·  Ctrl+N new line  ·  Esc cancel";
        f.Text(x + 2, y + height - 1, inner, hint, _discardArmed ? theme.Peach : theme.Overlay, theme.Surface0);
    }

    private void DrawSend(Frame f, SendPlan plan)
    {
        var theme = view.Theme;
        int width = Math.Min(SendWidth, f.Width - 4), inner = width - 4;
        var preview = plan.Prompt.Split('\n');
        int previewRows = Math.Min(Math.Min(SendPreviewRows, preview.Length), Math.Max(1, f.Height - plan.Targets.Count - 9));
        int height = plan.Targets.Count + previewRows + 7;
        int x = Math.Max(0, (f.Width - width) / 2), y = Math.Max(0, (f.Height - height) / 2);
        f.Fill(x, y, width, height, theme.Text, theme.Surface0);
        f.Text(x + 2, y + 1, inner, $"Send {Counted.Of(plan.Count, "comment")} to:", theme.Text, theme.Surface0, CellAttr.Bold);

        int row = y + 2;
        for (int i = 0; i < plan.Targets.Count; i++) DrawTarget(f, x, row++, width, plan.Targets[i], i == _targetCursor);

        row++;
        for (int i = 0; i < previewRows; i++)
        {
            bool cut = i == previewRows - 1 && preview.Length > previewRows;
            string line = cut ? $"… {preview.Length - previewRows + 1} more lines" : preview[i];
            f.Text(x + 2, row++, inner, line, theme.Overlay, theme.Surface0);
        }
        row++;
        string hint = _busy ? "Sending..." : "Enter paste it there and press Enter  ·  ↑↓ choose  ·  Esc keep them";
        f.Text(x + 2, row, inner, hint, theme.Lavender, theme.Surface0);
    }

    private void DrawTarget(Frame f, int x, int y, int width, ReviewAgentTarget target, bool selected)
    {
        var theme = view.Theme;
        uint bg = selected ? theme.Surface1 : theme.Surface0;
        f.Fill(x + 1, y, width - 2, 1, theme.Text, bg);
        if (selected) f.Put(x + 1, y, "▌", theme.Lavender, bg);
        int nx = f.Text(x + 2, y, width - 4, target.Agent, theme.Text, bg, CellAttr.Bold);
        nx = f.Text(nx + 2, y, Math.Max(0, x + width - nx - 4), target.State, theme.Subtext, bg);
        f.Text(nx + 2, y, Math.Max(0, x + width - nx - 4), target.Place, theme.Subtext, bg);
    }
}
