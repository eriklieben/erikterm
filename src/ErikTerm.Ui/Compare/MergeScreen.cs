using System.Text;

namespace ErikTerm.Ui.Compare;

/// <summary>
/// The conflict resolver: two columns over the result, with file list and overview strip, or stacked cards in a
/// narrow pane. Pure: it draws a <see cref="Frame"/> and reacts to events; everything else goes through
/// <see cref="MergeSession"/> and <see cref="IMergeHost"/>.
/// </summary>
public sealed class MergeScreen : ITuiScreen
{
    public const int WideThreshold = 100;
    private const int MinRowsForWide = 16, FileListWidth = 26, CompactContext = 3;

    private const int ActPrev = 1, ActNext = 2, ActSave = 3, ActOurs = 4, ActTheirs = 5, ActBoth = 6, ActReset = 7,
        ActFile = 8, ActConflict = 9, ActOverview = 10, ActConfirmYes = 11, ActConfirmNo = 12, ActKeep = 13, ActDelete = 14, ActUndo = 15;

    private readonly MergeSession _session;
    private TuiTheme _theme = TuiTheme.Dark;
    private bool _forceCompact, _forceWide;
    private int _top, _outTop, _hScroll, _conflict, _compactTop, _fileTop;
    private bool _followCompact = true;
    private int _lastBodyRows = 20, _lastOutRows = 8, _lastCols = 80, _lastRows = 24;
    private Confirm? _confirm;

    private sealed record Confirm(string Text, string YesLabel, Action Yes, string NoLabel = "Keep working");

    public MergeScreen(MergeSession session) => _session = session;

    public bool Done { get; private set; }
    public int ExitCode => _session.ExitCode;
    public bool IsCompact(int cols, int rows) => !_forceWide && (_forceCompact || cols < WideThreshold || rows < MinRowsForWide);

    public void OnThemeColor(uint backgroundColor) => _theme = TuiTheme.ForBackground(backgroundColor);

    private MergeFile? File => _session.File;

    private int ConflictCount => File?.Conflicts.Count ?? 0;

    /// <summary>The chunk of the selected conflict, or -1.</summary>
    private int SelectedChunk
    {
        get
        {
            if (File is not { Conflicts.Count: > 0 } f) return -1;
            _conflict = Math.Clamp(_conflict, 0, f.Conflicts.Count - 1);
            return f.Conflicts[_conflict];
        }
    }

    // ---- Input -----------------------------------------------------------------------------------

    public void OnKey(KeyEvent key)
    {
        if (_confirm is { } c) OnConfirmKey(key, c);
        else if (key.Key != TuiKey.Char) OnSpecialKey(key.Key);
        else if (key.Ctrl) OnCtrlKey(key.Rune);
        else if (!key.Alt) OnLetterKey((char)key.Rune);
    }

    private void OnConfirmKey(KeyEvent key, Confirm confirm)
    {
        if (key.Is('y') || key.Key == TuiKey.Enter) { _confirm = null; confirm.Yes(); }
        else if (key.Is('n') || key.Key == TuiKey.Esc) _confirm = null;
    }

    private void OnCtrlKey(int rune)
    {
        if (rune == 's') RequestSave();
        else if (rune == 'c') RequestQuit();
        else if (rune == 'e' || rune == 'd') Scroll(_lastBodyRows / 2);
        else if (rune == 'y' || rune == 'u') Scroll(-_lastBodyRows / 2);
    }

    private void OnSpecialKey(TuiKey key)
    {
        switch (key)
        {
            case TuiKey.Esc: RequestQuit(); break;
            case TuiKey.Down: Scroll(1); break;
            case TuiKey.Up: Scroll(-1); break;
            case TuiKey.PageDown: Scroll(_lastBodyRows - 1); break;
            case TuiKey.PageUp: Scroll(-(_lastBodyRows - 1)); break;
            case TuiKey.Home: Jump(0); break;
            case TuiKey.End: Jump(int.MaxValue); break;
            case TuiKey.Left: Pan(-8); break;
            case TuiKey.Right: Pan(8); break;
            case TuiKey.Tab: SwitchFile(+1); break;
            case TuiKey.BackTab: SwitchFile(-1); break;
        }
    }

    private void OnLetterKey(char letter)
    {
        switch (letter)
        {
            case 'q': RequestQuit(); break;
            case 'n': StepConflict(+1); break;
            case 'p': StepConflict(-1); break;
            case 'o': Apply(ConflictChoice.Ours, FileChoice.Ours); break;
            case 't': Apply(ConflictChoice.Theirs, FileChoice.Theirs); break;
            case 'b': Apply(ConflictChoice.Both, null); break;
            case 'r': Apply(ConflictChoice.None, FileChoice.None); break;
            case 'k':
                if (File is { Kind: MergeFileKind.DeleteModify }) KeepFile();
                else Scroll(-1);
                break;
            case 'x': DeleteFile(); break;
            case 'u': Undo(); break;
            case 'e': EditCurrent(whole: false); break;
            case 'E': EditCurrent(whole: true); break;
            case 'w': ToggleView(); break;
            case 'j': Scroll(1); break;
            case 'g': Jump(0); break;
            case 'G': Jump(int.MaxValue); break;
            case 'h': Pan(-8); break;
            case 'l': Pan(8); break;
            case 'd': Scroll(_lastBodyRows / 2); break; // vim's half page (Ctrl+D also works)
        }
    }

    public void OnMouse(MouseEvent mouse, Frame? lastFrame)
    {
        if (lastFrame is null) return;
        switch (mouse.Kind)
        {
            case MouseKind.WheelUp: Wheel(mouse, -3); return;
            case MouseKind.WheelDown: Wheel(mouse, 3); return;
            case MouseKind.Press when mouse.Button == 0:
                if (lastFrame.HitAt(mouse.X, mouse.Y) is { } hit) Click(hit, mouse);
                return;
        }
    }

    private void Wheel(MouseEvent mouse, int delta)
    {
        bool overOutput = !IsCompact(_lastCols, _lastRows) && mouse.Y >= _outputTopRow;
        if (overOutput) { _outTop = Math.Max(0, _outTop + delta); return; }
        Scroll(delta);
    }

    private int _outputTopRow = int.MaxValue;

    private void Click(HitRegion hit, MouseEvent mouse)
    {
        switch (hit.Action)
        {
            case ActPrev: StepConflict(-1); break;
            case ActNext: StepConflict(+1); break;
            case ActSave: RequestSave(); break;
            case ActOurs: Apply(ConflictChoice.Ours, FileChoice.Ours); break;
            case ActTheirs: Apply(ConflictChoice.Theirs, FileChoice.Theirs); break;
            case ActBoth: Apply(ConflictChoice.Both, null); break;
            case ActReset: Apply(ConflictChoice.None, FileChoice.None); break;
            case ActKeep: KeepFile(); break;
            case ActDelete: DeleteFile(); break;
            case ActUndo: Undo(); break;
            case ActFile: _session.Select(hit.Arg); ResetViewForFile(); break;
            case ActConflict: SelectConflict(hit.Arg); break;
            case ActOverview:
                if (File is { } f && f.Rows.Length > 0)
                {
                    int rows = Math.Max(1, hit.Height);
                    _top = Math.Clamp((mouse.Y - hit.Y) * f.Rows.Length / rows, 0, Math.Max(0, f.Rows.Length - _lastBodyRows));
                    SyncOutput();
                }
                break;
            case ActConfirmYes: { var c = _confirm; _confirm = null; c?.Yes(); break; }
            case ActConfirmNo: _confirm = null; break;
        }
    }

    private void Scroll(int delta)
    {
        if (File is not { } f) return;
        if (IsCompact(_lastCols, _lastRows))
        {
            _followCompact = false;
            _compactTop = Math.Max(0, _compactTop + delta);
            return;
        }
        _top = Math.Clamp(_top + delta, 0, Math.Max(0, f.Rows.Length - _lastBodyRows));
        SyncOutput();
    }

    private void Jump(int top)
    {
        if (File is not { } f) return;
        _top = Math.Clamp(top, 0, Math.Max(0, f.Rows.Length - _lastBodyRows));
        _compactTop = top == 0 ? 0 : int.MaxValue / 2;
        _followCompact = false;
        SyncOutput();
    }

    private void Pan(int delta) => _hScroll = Math.Max(0, _hScroll + delta);

    private void ToggleView()
    {
        if (IsCompact(_lastCols, _lastRows)) { _forceCompact = false; _forceWide = true; }
        else { _forceWide = false; _forceCompact = true; }
    }

    private void SelectConflict(int index)
    {
        if (ConflictCount == 0) return;
        _conflict = Math.Clamp(index, 0, ConflictCount - 1);
        CenterOnConflict();
    }

    private void StepConflict(int delta)
    {
        if (ConflictCount == 0) { _session.Status = "No conflicts in this file"; _session.StatusIsError = false; return; }
        _conflict = ((_conflict + delta) % ConflictCount + ConflictCount) % ConflictCount;
        CenterOnConflict();
    }

    private void CenterOnConflict()
    {
        if (File is not { } f) return;
        int chunk = SelectedChunk;
        if (chunk < 0) return;
        _top = Math.Clamp(f.FirstRow(chunk) - _lastBodyRows / 3, 0, Math.Max(0, f.Rows.Length - _lastBodyRows));
        _outTop = Math.Max(0, f.FirstOutputLine(chunk) - _lastOutRows / 3);
        _followCompact = true;
    }

    /// <summary>Columns drive the result pane: it follows the chunk at the top of the columns.</summary>
    private void SyncOutput()
    {
        if (File is not { } f || f.Rows.Length == 0) return;
        var row = f.Rows[Math.Clamp(_top, 0, f.Rows.Length - 1)];
        _outTop = Math.Max(0, f.FirstOutputLine(row.Chunk));
    }

    private void Apply(ConflictChoice choice, FileChoice? fileChoice)
    {
        if (File is not { } f) return;
        if (f.Kind != MergeFileKind.Text)
        {
            if (fileChoice is { } fc) ChooseFile(f, fc);
            return;
        }
        int chunk = SelectedChunk;
        if (chunk < 0) { _session.Status = "No conflicts in this file"; return; }
        if (f.WholeEdited) { _session.Fail("Edited as a whole file: press u to undo that edit first"); return; }
        f.Decide(chunk, choice);
        _session.StatusIsError = false;
        _session.Status = StatusLine(f);
    }

    private void ChooseFile(MergeFile f, FileChoice choice)
    {
        if (f.Kind == MergeFileKind.Binary && choice == FileChoice.Delete) return;
        if (f.Kind == MergeFileKind.DeleteModify && choice is FileChoice.Ours or FileChoice.Theirs)
            choice = f.DeletedSide == DeletedSide.Ours ? FileChoice.Theirs : FileChoice.Ours; // the only side there is
        f.Choose(choice);
        _session.StatusIsError = false;
        _session.Status = choice switch
        {
            FileChoice.Delete => "File will be deleted when saved",
            FileChoice.None => "Choice reset",
            _ => $"Keeping {(choice == FileChoice.Ours ? f.OursLabel : f.TheirsLabel)}'s version",
        };
    }

    private void KeepFile()
    {
        if (File is { Kind: MergeFileKind.DeleteModify } f) ChooseFile(f, f.DeletedSide == DeletedSide.Ours ? FileChoice.Theirs : FileChoice.Ours);
        else if (File is { Kind: MergeFileKind.Binary }) _session.Status = "Binary file: press o or t";
    }

    private void DeleteFile()
    {
        if (File is { Kind: MergeFileKind.DeleteModify } f) ChooseFile(f, FileChoice.Delete);
    }

    private void Undo()
    {
        if (File is not { } f) return;
        if (f.Undo()) { _session.Status = "Undone"; _session.StatusIsError = false; }
    }

    private void SwitchFile(int delta)
    {
        _session.SelectNext(delta);
        ResetViewForFile();
    }

    private void ResetViewForFile()
    {
        _conflict = 0;
        _top = 0;
        _outTop = 0;
        _hScroll = 0;
        _followCompact = true;
        if (File is { Conflicts.Count: > 0 }) CenterOnConflict();
    }

    private static string EntryBadge(MergeEntry e)
    {
        if (e.Done) return "done";
        if (e.File is not { } ef) return e.Error is null ? "..." : "!";
        int unresolved = ef.UnresolvedCount;
        if (unresolved > 0) return unresolved.ToString();
        return !ef.Dirty && ef.IsConflictFree ? "auto" : "ok";
    }

    private static string StatusLine(MergeFile f)
    {
        if (f.Kind == MergeFileKind.DeleteModify)
            return f.FileChoice switch
            {
                FileChoice.None => $"Deleted on {(f.DeletedSide == DeletedSide.Ours ? f.OursLabel : f.TheirsLabel)}, changed on {(f.DeletedSide == DeletedSide.Ours ? f.TheirsLabel : f.OursLabel)}",
                FileChoice.Delete => "Will be deleted",
                _ => "Keeping the changed version",
            };
        if (f.Kind == MergeFileKind.Binary) return f.FileChoice == FileChoice.None ? "Binary file: pick a side" : "Side picked";
        int unresolved = f.UnresolvedCount;
        string auto = f.AutoCount > 0 ? $", {f.AutoCount} merged automatically" : "";
        return unresolved == 0
            ? $"No conflicts left{auto}. Ctrl+S saves."
            : $"{MergeSession.Counted(unresolved, "conflict")} left{auto}";
    }

    private void RequestSave()
    {
        if (File is not { } f) return;
        if (_session.SaveNeedsMarkers)
        {
            int n = f.UnresolvedCount;
            _confirm = new Confirm($"{MergeSession.Counted(n, "conflict")} left. Save this file with conflict markers? It won't be staged.", "Save with markers", () => SaveNow(withMarkers: true));
            return;
        }
        SaveNow(withMarkers: false);
    }

    private void SaveNow(bool withMarkers)
    {
        if (_session.Save(withMarkers))
        {
            if (!_session.Host.RepoMode && File is { UnresolvedCount: 0 }) Done = true; // git mergetool: one file per run, moves on
            else ResetViewForFile();
        }
    }

    private void RequestQuit()
    {
        if (!_session.AnyDirty) { Done = true; return; }
        _confirm = new Confirm("Quit and throw away the unsaved result?", "Quit without saving", () => Done = true);
    }

    private void EditCurrent(bool whole)
    {
        if (File is not { } f || _session.Entry is not { } entry || f.Kind != MergeFileKind.Text) return;
        int chunk = SelectedChunk;
        if (whole || chunk < 0 || f.WholeEdited)
        {
            string? editedWhole = _session.Host.EditExternally(entry, f.CurrentText(), 1);
            if (editedWhole is null) { _session.Fail("The editor failed; nothing changed"); return; }
            if (editedWhole != f.CurrentText())
            {
                f.ApplyWholeEdit(editedWhole);
                int left = f.UnresolvedCount;
                _session.Status = left == 0 ? "Whole-file edit applied" : $"Whole-file edit applied; {MergeSession.Counted(left, "conflict marker")} still in it";
                _session.StatusIsError = false;
            }
            return;
        }

        int line = f.FirstOutputLine(chunk) + 1;
        string seed = f.HunkText(chunk);
        string? edited = _session.Host.EditExternally(entry, seed, line);
        if (edited is null) { _session.Fail("The editor failed; nothing changed"); return; }
        if (edited == seed) return;
        if (!f.ApplyHunkEdit(chunk, edited)) { _session.Fail("The edit still has conflict markers; not applied"); return; }
        _session.StatusIsError = false;
        _session.Status = StatusLine(f);
    }

    // ---- Drawing ---------------------------------------------------------------------------------

    public void Render(Frame frame)
    {
        _lastCols = frame.Width;
        _lastRows = frame.Height;
        frame.Clear(_theme.Text, _theme.Base);
        if (IsCompact(frame.Width, frame.Height)) RenderCompact(frame);
        else RenderWide(frame);
        if (_confirm is not null) DrawConfirm(frame);
    }

    private int Button(Frame f, int x, int y, int maxX, FrameButton button)
    {
        int width = button.Label.Length + 2;
        if (x + width > maxX) return x;
        uint accent = button.Color ?? _theme.Lavender;
        bool filled = button.Primary || button.Active;
        uint bg = filled ? accent : _theme.Surface0;
        uint fg = filled ? _theme.OnAccent : _theme.Text;
        f.Fill(x, y, width, 1, fg, bg);
        f.Text(x + 1, y, width - 1, button.Label, fg, bg, button.Primary ? CellAttrs.Bold : CellAttrs.None);
        f.AddHit(x, y, width, 1, button.Action, button.Arg);
        return x + width + 1;
    }

    /// <summary>Widths of a row of buttons drawn right-aligned, so their start can be computed first.</summary>
    private static int ButtonsWidth(params string[] labels)
    {
        int width = 0;
        foreach (string l in labels) width += l.Length + 3;
        return width;
    }

    private void RenderWide(Frame f)
    {
        var mf = File;
        DrawHeader(f, mf);
        int footerY = f.Height - 1;
        DrawFooter(f, footerY, "n / p conflict", "o ours", "t theirs", "b both", "r reset", "u undo", "e edit", "Tab file", "w compact", "Ctrl+S save", "q quit");

        int bodyTop = 1, bodyBottom = footerY, bodyRows = bodyBottom - bodyTop;
        bool multi = _session.Entries.Count > 1;
        int listW = multi ? Math.Min(FileListWidth, f.Width / 5) : 0;
        if (multi) DrawFileList(f, 0, bodyTop, listW, bodyRows);

        int stripX = f.Width - 1;
        int colsX = listW + (multi ? 1 : 0), colsW = stripX - colsX;
        if (mf is null) { DrawMessage(f, colsX, bodyTop, colsW, bodyRows, _session.Entry?.Error ?? "Loading..."); return; }

        if (mf.Kind != MergeFileKind.Text) { DrawSpecial(f, mf, colsX, bodyTop, colsW, bodyRows, stripX); return; }

        int outRows = Math.Max(6, bodyRows * 36 / 100);
        int columnRows = bodyRows - outRows;
        int paneHeaderY = bodyTop;
        int rowsTop = paneHeaderY + 1;
        int rowsH = columnRows - 1;
        _lastBodyRows = Math.Max(1, rowsH);
        _lastOutRows = Math.Max(1, outRows - 1);
        _outputTopRow = rowsTop + rowsH;

        // Pane titles.
        int leftW = colsW / 2, rightX = colsX + leftW + 1, rightW = colsW - leftW - 1;
        DrawPaneTitle(f, colsX, paneHeaderY, leftW, mf.OursLabel, "yours", _theme.Blue);
        DrawPaneTitle(f, rightX, paneHeaderY, rightW, mf.TheirsLabel, "theirs", _theme.Peach);

        DrawRows(f, mf, new FrameRun(colsX, rowsTop, leftW), new FrameRun(rightX, rowsTop, rightW), rowsH);
        DrawOverview(f, mf, stripX, rowsTop, rowsH);
        DrawVerticalLine(f, colsX + leftW, paneHeaderY, columnRows);

        // Result pane.
        int outHeaderY = rowsTop + rowsH;
        DrawOutputHeader(f, mf, colsX, outHeaderY, colsW + 1);
        DrawOutput(f, mf, colsX, outHeaderY + 1, colsW, outRows - 1);
    }

    private void DrawHeader(Frame f, MergeFile? mf)
    {
        f.Fill(0, 0, f.Width, 1, _theme.Text, _theme.Mantle);
        int x = 1;
        x = f.Text(x, 0, f.Width, "Merging ", _theme.Text, _theme.Mantle, CellAttrs.Bold);
        if (mf is not null)
        {
            x = f.Text(x, 0, 24, mf.OursLabel, _theme.Blue, _theme.Mantle, CellAttrs.Bold);
            x = f.Text(x, 0, f.Width, " with ", _theme.Subtext, _theme.Mantle);
            x = f.Text(x, 0, 24, mf.TheirsLabel, _theme.Peach, _theme.Mantle, CellAttrs.Bold);
        }

        string saveLabel = _session.Host.RepoMode ? "Save and stage" : "Save";
        int buttonsW = ButtonsWidth("Previous conflict", "Next conflict", saveLabel);
        int bx = f.Width - buttonsW;
        int statusX = x + 2;
        string status = "";
        if (mf is not null) status = _session.Status.Length > 0 ? _session.Status : StatusLine(mf);
        uint statusColor = _session.StatusIsError ? _theme.Red : _theme.Subtext;
        f.Text(statusX, 0, Math.Max(0, bx - statusX - 1), status, statusColor, _theme.Mantle);

        bool ready = mf is not null && mf.UnresolvedCount == 0;
        bx = Button(f, bx, 0, f.Width, new("Previous conflict", ActPrev));
        bx = Button(f, bx, 0, f.Width, new("Next conflict", ActNext));
        Button(f, bx, 0, f.Width, new(saveLabel, ActSave, Primary: ready, Color: _theme.Green));
    }

    private void DrawFooter(Frame f, int y, params string[] items)
    {
        f.Fill(0, y, f.Width, 1, _theme.Subtext, _theme.Mantle);
        int x = 1;
        foreach (string item in items)
        {
            if (x + item.Length + 2 >= f.Width) break;
            x = f.Text(x, y, f.Width - x, item, _theme.Subtext, _theme.Mantle) + 2;
        }
    }

    private void DrawFileList(Frame f, int x, int y, int width, int height)
    {
        f.Fill(x, y, width, height, _theme.Text, _theme.Mantle);
        f.Text(x + 1, y, width - 2, "CONFLICTED FILES", _theme.Subtext, _theme.Mantle, CellAttrs.Bold);
        int visible = height - 1;
        int count = _session.Entries.Count;
        if (_session.Current < _fileTop) _fileTop = _session.Current;
        if (_session.Current >= _fileTop + visible) _fileTop = _session.Current - visible + 1;
        _fileTop = Math.Clamp(_fileTop, 0, Math.Max(0, count - visible));
        for (int row = 0; row < visible && _fileTop + row < count; row++)
        {
            int index = _fileTop + row;
            var e = _session.Entries[index];
            int ry = y + 1 + row;
            bool selected = index == _session.Current;
            uint bg = selected ? _theme.Surface0 : _theme.Mantle;
            f.Fill(x, ry, width, 1, _theme.Text, bg);

            string badge = EntryBadge(e);
            uint badgeColor;
            if (e.Done || badge == "auto" || badge == "ok") badgeColor = _theme.Green;
            else if (badge != "!" && e.File is null) badgeColor = _theme.Subtext;
            else badgeColor = _theme.Red;
            string name = TrimLeft(e.Path, width - badge.Length - 3);
            f.Text(x + 1, ry, width - badge.Length - 3, name, selected ? _theme.Text : _theme.Subtext, bg);
            f.Text(x + width - badge.Length - 1, ry, badge.Length, badge, badgeColor, bg);
            f.AddHit(x, ry, width, 1, ActFile, index);
        }
        DrawVerticalLine(f, x + width, y, height);
    }

    private void DrawMessage(Frame f, int x, int y, int width, int height, string text)
    {
        f.Text(x + 2, y + Math.Max(1, height / 2 - 1), Math.Max(0, width - 4), text, _theme.Subtext, _theme.Base);
    }

    private void DrawSpecial(Frame f, MergeFile mf, int x, int y, int width, int height, int stripX)
    {
        _lastBodyRows = height;
        _ = stripX;
        int row = y + 1;
        if (mf.Kind == MergeFileKind.Binary)
        {
            f.Text(x + 2, row++, width - 4, $"{mf.Path} is a binary file and can't be merged line by line.", _theme.Text, _theme.Base, CellAttrs.Bold);
            f.Text(x + 2, row++, width - 4, $"{mf.OursLabel}: {FormatSize(mf.OursBytes?.Length ?? 0)}    {mf.TheirsLabel}: {FormatSize(mf.TheirsBytes?.Length ?? 0)}", _theme.Subtext, _theme.Base);
            row++;
            int bx = x + 2;
            bx = Button(f, bx, row, f.Width, new($"Keep {mf.OursLabel}'s version", ActOurs, Active: mf.FileChoice == FileChoice.Ours, Color: _theme.Blue));
            Button(f, bx, row, f.Width, new($"Keep {mf.TheirsLabel}'s version", ActTheirs, Active: mf.FileChoice == FileChoice.Theirs, Color: _theme.Peach));
            return;
        }

        string deleter = mf.DeletedSide == DeletedSide.Ours ? mf.OursLabel : mf.TheirsLabel;
        string changer = mf.DeletedSide == DeletedSide.Ours ? mf.TheirsLabel : mf.OursLabel;
        f.Text(x + 2, row++, width - 4, $"{mf.Path} was deleted on {deleter} and changed on {changer}.", _theme.Text, _theme.Base, CellAttrs.Bold);
        f.Text(x + 2, row++, width - 4, $"Keep the version from {changer}, or delete the file.", _theme.Subtext, _theme.Base);
        row++;
        int b = x + 2;
        b = Button(f, b, row, f.Width, new("Keep the file", ActKeep, Active: mf.FileChoice is FileChoice.Ours or FileChoice.Theirs, Color: _theme.Green));
        Button(f, b, row, f.Width, new("Delete the file", ActDelete, Active: mf.FileChoice == FileChoice.Delete, Color: _theme.Red));
        row += 2;
        DrawVerticalLine(f, x, row, 0);
        f.Text(x + 2, row++, width - 4, $"The version on {changer}:", _theme.Subtext, _theme.Base);
        foreach (string line in TextDocument.SplitLines(mf.SurvivingText()).Take(Math.Max(0, y + height - row)))
        {
            DrawContentRow(f, new FrameRun(x + 2, row, width - 4), line, [], 0, new FrameLineStyle(_theme.Text, _theme.Base, 0), default);
            row++;
        }
    }

    private void DrawPaneTitle(Frame f, int x, int y, int width, string title, string sub, uint color)
    {
        f.Fill(x, y, width, 1, _theme.Text, _theme.Mantle);
        int nx = f.Text(x + 1, y, Math.Max(0, width - 2), title, color, _theme.Mantle, CellAttrs.Bold);
        f.Text(nx + 1, y, Math.Max(0, x + width - nx - 2), sub, _theme.Subtext, _theme.Mantle);
    }

    private void DrawVerticalLine(Frame f, int x, int y, int height)
    {
        for (int r = y; r < y + height; r++) f.Put(x, r, "│", _theme.Surface1, _theme.Base);
    }

    /// <summary>The two columns' rows, from the first row of each, <paramref name="rows"/> of them.</summary>
    private void DrawRows(Frame f, MergeFile mf, FrameRun left, FrameRun right, int rows)
    {
        int selected = SelectedChunk;
        for (int r = 0; r < rows; r++)
        {
            int index = _top + r;
            if (index >= mf.Rows.Length) break;
            var row = mf.Rows[index];
            var chunk = mf.Chunks[row.Chunk];
            var changes = mf.Changes(index);
            bool isSelected = row.Chunk == selected;
            DrawColumnCell(f, left.Down(r), row, chunk, true, isSelected, changes.Left);
            DrawColumnCell(f, right.Down(r), row, chunk, false, isSelected, changes.Right);
            if (chunk.Kind == MergeChunkKind.Conflict)
            {
                int conflictIndex = IndexOfConflict(mf, row.Chunk);
                f.AddHit(left.X, left.Y + r, left.Width, 1, ActConflict, conflictIndex);
                f.AddHit(right.X, right.Y + r, right.Width, 1, ActConflict, conflictIndex);
            }
        }
    }

    private static int IndexOfConflict(MergeFile mf, int chunk)
    {
        for (int i = 0; i < mf.Conflicts.Count; i++) if (mf.Conflicts[i] == chunk) return i;
        return 0;
    }

    /// <summary>One side of <paramref name="row"/> (of <paramref name="chunk"/>): <paramref name="ours"/> is the left column.</summary>
    private void DrawColumnCell(Frame f, FrameRun at, MergeRow row, MergeChunk chunk, bool ours, bool selected, TextSpan[] spans)
    {
        var (x, y, width) = at;
        string? text = ours ? row.Ours : row.Theirs;
        int number = ours ? row.OursNo : row.TheirsNo;
        var (bg, spanBg, fg, dimmed) = ColumnColors(chunk, ours, row.Chunk);
        bool marked = selected && chunk.Kind == MergeChunkKind.Conflict;
        uint markerColor = ours ? _theme.Blue : _theme.Peach;
        if (text is null)
        {
            f.Fill(x, y, width, 1, _theme.GapFg, _theme.Base);
            for (int i = 0; i < width; i++) f.Put(x + i, y, "╱", _theme.GapFg, _theme.Base);
            if (marked) f.Put(x, y, "▌", markerColor, _theme.Base);
            return;
        }
        uint gutterFg = marked ? markerColor : _theme.Overlay;
        CellAttrs attr = dimmed ? CellAttrs.Dim : CellAttrs.None;
        DrawContentRow(f, at, text, spans, _hScroll, new FrameLineStyle(fg, bg, spanBg, attr), new ContentGutter(GutterWidth, number, gutterFg, marked ? "▌" : " "));
    }

    private (uint Bg, uint SpanBg, uint Fg, bool Dimmed) ColumnColors(MergeChunk chunk, bool ours, int chunkIndex) => chunk.Kind switch
    {
        MergeChunkKind.AutoOurs => AutoColumnColors(ours),
        MergeChunkKind.AutoTheirs => AutoColumnColors(!ours),
        MergeChunkKind.AutoBoth => (_theme.AutoBg, _theme.Base, _theme.Text, false),
        MergeChunkKind.Conflict => ConflictColumnColors(ours, chunkIndex),
        _ => (_theme.Base, _theme.Base, _theme.Text, false),
    };

    private (uint Bg, uint SpanBg, uint Fg, bool Dimmed) AutoColumnColors(bool changedSide) => changedSide
        ? (_theme.AutoBg, Blend(_theme.AutoBg, _theme.Green), _theme.Text, false)
        : (_theme.Base, _theme.Base, _theme.Subtext, false);

    private (uint Bg, uint SpanBg, uint Fg, bool Dimmed) ConflictColumnColors(bool ours, int chunkIndex)
    {
        var d = File!.DecisionOf(chunkIndex);
        bool kept = ours ? d.Choice is ConflictChoice.Ours or ConflictChoice.Both : d.Choice is ConflictChoice.Theirs or ConflictChoice.Both;
        bool dimmed = d.Choice != ConflictChoice.None && !kept;
        return (ours ? _theme.OursBg : _theme.TheirsBg, ours ? _theme.OursHi : _theme.TheirsHi, _theme.Text, dimmed);
    }

    private const int GutterWidth = 6;

    /// <summary>
    /// A content row's gutter: <see cref="Width"/> cells (0: none) with the line <see cref="Number"/> (0: blank) in
    /// <see cref="Fg"/> (0: the overlay colour) after the <see cref="Marker"/>.
    /// </summary>
    private readonly record struct ContentGutter(int Width, int Number, uint Fg = 0, string Marker = " ");

    /// <summary>A content row: gutter with a number, then the line from <paramref name="hscroll"/>, spans in their color.</summary>
    private void DrawContentRow(Frame f, FrameRun at, string raw, TextSpan[] spans, int hscroll, in FrameLineStyle style, in ContentGutter gutter)
    {
        var (x, y, width) = at;
        f.Fill(x, y, width, 1, style.Fg, style.Bg);
        if (gutter.Width > 0)
        {
            f.Put(x, y, gutter.Marker, gutter.Fg, style.Bg);
            string num = gutter.Number > 0 ? gutter.Number.ToString() : "";
            f.Text(x + gutter.Width - 1 - num.Length, y, num.Length, num, gutter.Fg == 0 ? _theme.Overlay : gutter.Fg, style.Bg);
        }
        CompareRender.Line(f, new FrameRun(x + gutter.Width, y, width - gutter.Width), TextDocument.Content(raw), hscroll, spans, style);
    }

    private void DrawOverview(Frame f, MergeFile mf, int x, int y, int height)
    {
        if (height <= 0) return;
        var cells = Overview.Compute(mf.Rows.Length, height, i => Severity(mf, mf.Rows[i]));
        int thumbStart = mf.Rows.Length == 0 ? 0 : _top * height / mf.Rows.Length;
        int thumbEnd = mf.Rows.Length == 0 ? height : Math.Min(height, (_top + _lastBodyRows) * height / mf.Rows.Length + 1);
        for (int r = 0; r < height; r++)
        {
            uint bg = r >= thumbStart && r < thumbEnd ? _theme.Surface1 : _theme.Mantle;
            uint color = cells[r] switch { 3 => _theme.Red, 2 => _theme.Blue, 1 => _theme.Green, _ => bg };
            f.Put(x, y + r, cells[r] == 0 ? " " : "█", color, bg);
        }
        f.AddHit(x, y, 1, height, ActOverview);
    }

    private static int Severity(MergeFile mf, MergeRow row)
    {
        var kind = mf.Chunks[row.Chunk].Kind;
        return kind switch
        {
            MergeChunkKind.Conflict => mf.DecisionOf(row.Chunk).Choice == ConflictChoice.None ? 3 : 2,
            MergeChunkKind.Stable => 0,
            _ => 1,
        };
    }

    private void DrawOutputHeader(Frame f, MergeFile mf, int x, int y, int width)
    {
        f.Fill(x, y, width, 1, _theme.Text, _theme.Mantle);
        int nx = f.Text(x + 1, y, width, "OUTPUT", _theme.Text, _theme.Mantle, CellAttrs.Bold);
        string oursLabel = $"Take {Short(mf.OursLabel)}", theirsLabel = $"Take {Short(mf.TheirsLabel)}";
        const string BothLabel = "Take both", ResetLabel = "Reset";
        string label = $"Conflict {_conflict + 1}:";
        int total = ConflictCount == 0 ? 0 : label.Length + 1 + ButtonsWidth(oursLabel, theirsLabel, BothLabel, ResetLabel, "Undo");
        nx = f.Text(nx + 1, y, Math.Max(0, x + width - total - nx - 3), $"{TrimLeft(mf.Path, 40)}, what gets saved", _theme.Subtext, _theme.Mantle);

        if (ConflictCount == 0) { f.Text(nx + 2, y, Math.Max(0, x + width - nx - 3), "No conflicts", _theme.Green, _theme.Mantle); return; }
        var d = mf.WholeEdited ? default : mf.DecisionOf(SelectedChunk);
        int bx = Math.Max(nx + 2, x + width - total);
        bx = f.Text(bx, y, label.Length + 1, label, _theme.Subtext, _theme.Mantle) + 1;
        bx = Button(f, bx, y, x + width, new(oursLabel, ActOurs, Active: d.Choice == ConflictChoice.Ours, Color: _theme.Blue));
        bx = Button(f, bx, y, x + width, new(theirsLabel, ActTheirs, Active: d.Choice == ConflictChoice.Theirs, Color: _theme.Peach));
        bx = Button(f, bx, y, x + width, new(BothLabel, ActBoth, Active: d.Choice == ConflictChoice.Both));
        bx = Button(f, bx, y, x + width, new(ResetLabel, ActReset));
        Button(f, bx, y, x + width, new("Undo", ActUndo));
    }

    private static string Short(string label) => label.Length > 18 ? label[..17] + "…" : label;

    private void DrawOutput(Frame f, MergeFile mf, int x, int y, int width, int rows)
    {
        var output = mf.Output;
        _outTop = Math.Clamp(_outTop, 0, Math.Max(0, output.Count - rows));
        int number = 1;
        for (int i = 0; i < _outTop && i < output.Count; i++) if (output[i].Origin != OutputOrigin.Unresolved) number++;
        int selected = SelectedChunk;
        for (int r = 0; r < rows && _outTop + r < output.Count; r++)
        {
            var line = output[_outTop + r];
            uint bg = OutputBackground(line.Origin);
            if (line.Origin == OutputOrigin.Unresolved)
            {
                DrawUnresolvedOutput(f, mf, new FrameRun(x, y + r, width), line, bg, line.Chunk == selected);
                continue;
            }
            DrawContentRow(f, new FrameRun(x, y + r, width), line.Raw, [], _hScroll, new FrameLineStyle(_theme.Text, bg, 0), new ContentGutter(GutterWidth, number++, _theme.Overlay));
            if (line.Chunk >= 0 && mf.Chunks[line.Chunk].Kind == MergeChunkKind.Conflict) f.AddHit(x, y + r, width, 1, ActConflict, IndexOfConflict(mf, line.Chunk));
        }
    }

    private uint OutputBackground(OutputOrigin origin) => origin switch
    {
        OutputOrigin.AutoOurs or OutputOrigin.AutoTheirs or OutputOrigin.AutoBoth => _theme.AutoBg,
        OutputOrigin.Ours => _theme.OursBg,
        OutputOrigin.Theirs => _theme.TheirsBg,
        OutputOrigin.Custom => _theme.CustomBg,
        OutputOrigin.Unresolved => _theme.ConflictBg,
        _ => _theme.Base,
    };

    private void DrawUnresolvedOutput(Frame f, MergeFile mf, FrameRun at, OutputLine line, uint bg, bool selected)
    {
        var (x, y, width) = at;
        f.Fill(x, y, width, 1, _theme.Text, bg);
        f.Put(x, y, selected ? "▌" : " ", _theme.Red, bg);
        f.Text(x + GutterWidth, y, Math.Max(0, width - GutterWidth), "  " + TextDocument.Content(line.Raw).ToString() + "  ", _theme.Red, _theme.ConflictHi, CellAttrs.Bold);
        f.AddHit(x, y, width, 1, ActConflict, IndexOfConflict(mf, line.Chunk));
    }

    private void DrawConfirm(Frame f)
    {
        var c = _confirm!;
        int y = Math.Max(0, f.Height - 1);
        f.Fill(0, y, f.Width, 1, _theme.Text, _theme.ConflictBg);
        string yes = c.YesLabel + " (y)", no = c.NoLabel + " (n)";
        int bw = ButtonsWidth(yes, no);
        int bx = Math.Max(1, f.Width - bw);
        f.Text(1, y, Math.Max(0, bx - 2), c.Text, _theme.Text, _theme.ConflictBg, CellAttrs.Bold);
        bx = Button(f, bx, y, f.Width, new(yes, ActConfirmYes, Primary: true, Color: _theme.Red));
        Button(f, bx, y, f.Width, new(no, ActConfirmNo));
    }

    // ---- Compact (stacked cards) ------------------------------------------------------------------

    private void RenderCompact(Frame f)
    {
        var mf = File;
        f.Fill(0, 0, f.Width, 1, _theme.Text, _theme.Mantle);
        if (mf is null)
        {
            f.Text(1, 0, f.Width - 2, _session.Entry?.Path ?? "", _theme.Text, _theme.Mantle, CellAttrs.Bold);
            DrawMessage(f, 0, 1, f.Width, f.Height - 2, _session.Entry?.Error ?? "Loading...");
            return;
        }

        DrawCompactTitle(f, mf);
        DrawFooter(f, f.Height - 1, "o ours", "t theirs", "b both", "u undo", "e edit", "n next", "w wide", "Tab file", "^S save", "q quit");

        int top = 1, height = f.Height - 2;
        if (height <= 0) return;
        _lastBodyRows = height;
        if (mf.Kind != MergeFileKind.Text) { DrawSpecial(f, mf, 0, top, f.Width, height, f.Width); return; }

        var rows = BuildCompactRows(mf, f.Width);
        int maxTop = Math.Max(0, rows.Count - height);
        if (_followCompact)
        {
            int first = rows.FindIndex(r => r.ConflictIndex == _conflict && r.Style != CompactStyle.Context);
            if (first >= 0) _compactTop = Math.Max(0, first - height / 3);
        }
        _compactTop = Math.Clamp(_compactTop, 0, maxTop);

        for (int r = 0; r < height && _compactTop + r < rows.Count; r++) DrawCompactRow(f, rows[_compactTop + r], top + r);
    }

    private void DrawCompactTitle(Frame f, MergeFile mf)
    {
        int nx = f.Text(1, 0, f.Width - 2, TrimLeft(mf.Path, Math.Max(10, f.Width / 2)), _theme.Text, _theme.Mantle, CellAttrs.Bold);
        string counter;
        if (ConflictCount != 0) counter = $"conflict {_conflict + 1} of {ConflictCount}";
        else counter = mf.Kind == MergeFileKind.Text ? "no conflicts" : "choose a version";
        nx = f.Text(nx + 2, 0, Math.Max(0, f.Width - nx - 3), counter, _theme.Subtext, _theme.Mantle);
        if (_session.Entries.Count > 1) f.Text(nx + 2, 0, Math.Max(0, f.Width - nx - 3), $"file {_session.Current + 1}/{_session.Entries.Count}", _theme.Subtext, _theme.Mantle);
    }

    private void DrawCompactRow(Frame f, CompactRow row, int y)
    {
        uint bg = row.Style switch
        {
            CompactStyle.OursHeader or CompactStyle.OursLine => _theme.OursBg,
            CompactStyle.TheirsHeader or CompactStyle.TheirsLine => _theme.TheirsBg,
            CompactStyle.AutoLine => _theme.AutoBg,
            _ => _theme.Base,
        };
        uint fg = row.Style == CompactStyle.Context ? _theme.Subtext : _theme.Text;
        if (row.Style is CompactStyle.OursHeader or CompactStyle.TheirsHeader or CompactStyle.Collapsed)
        {
            uint hfg = row.Style switch
            {
                CompactStyle.OursHeader => _theme.Blue,
                CompactStyle.TheirsHeader => _theme.Peach,
                _ => _theme.Overlay,
            };
            f.Fill(0, y, f.Width, 1, hfg, bg);
            f.Text(2, y, f.Width - 3, row.Text, hfg, bg, CellAttrs.Bold);
        }
        else
        {
            uint spanBg = row.Style is CompactStyle.OursLine ? _theme.OursHi : _theme.TheirsHi;
            DrawContentRow(f, new FrameRun(0, y, f.Width), row.Text, row.Spans, _hScroll, new FrameLineStyle(fg, bg, spanBg), new ContentGutter(GutterWidth, row.Number));
        }
        if (row.ConflictIndex >= 0) f.AddHit(0, y, f.Width, 1, ActConflict, row.ConflictIndex);
    }

    private enum CompactStyle { Context, Collapsed, AutoLine, OursHeader, OursLine, TheirsHeader, TheirsLine }

    private sealed record CompactRow(CompactStyle Style, string Text, int Number = 0, int ConflictIndex = -1, TextSpan[]? SpansOrNull = null)
    {
        public TextSpan[] Spans => SpansOrNull ?? [];
    }

    private static List<CompactRow> BuildCompactRows(MergeFile mf, int width)
    {
        _ = width;
        var rows = new List<CompactRow>();
        int conflictIndex = 0;
        int lineNo = 1;
        for (int i = 0; i < mf.Chunks.Count; i++)
        {
            if (mf.Chunks[i].Kind == MergeChunkKind.Conflict) AddConflictCard(rows, mf, i, conflictIndex++, ref lineNo);
            else AddStretchRows(rows, mf, i, ref lineNo);
        }
        return rows;
    }

    private static void AddConflictCard(List<CompactRow> rows, MergeFile mf, int chunk, int conflict, ref int lineNo)
    {
        var d = mf.DecisionOf(chunk);
        if (d.Choice == ConflictChoice.None)
        {
            AddUnresolvedCard(rows, mf, mf.Chunks[chunk], conflict);
            return;
        }
        string took = d.Choice switch
        {
            ConflictChoice.Ours => $"took {mf.OursLabel}",
            ConflictChoice.Theirs => $"took {mf.TheirsLabel}",
            ConflictChoice.Both => "took both",
            _ => "edited",
        };
        rows.Add(new CompactRow(CompactStyle.Collapsed, $"✓ {took}", 0, conflict));
        foreach (var l in OutputForChunk(mf, chunk)) { rows.Add(new CompactRow(CompactStyle.AutoLine, l.Raw, lineNo++, conflict)); }
    }

    private static void AddUnresolvedCard(List<CompactRow> rows, MergeFile mf, MergeChunk c, int conflict)
    {
        var pairs = AlignedForCard(c);
        rows.Add(new CompactRow(CompactStyle.OursHeader, $"  {mf.OursLabel}", 0, conflict));
        foreach (var p in pairs.OursRows) rows.Add(new CompactRow(CompactStyle.OursLine, p.Text, 0, conflict, p.Spans));
        rows.Add(new CompactRow(CompactStyle.TheirsHeader, $"  {mf.TheirsLabel}", 0, conflict));
        foreach (var p in pairs.TheirsRows) rows.Add(new CompactRow(CompactStyle.TheirsLine, p.Text, 0, conflict, p.Spans));
    }

    /// <summary>A stable or automatic stretch: a little context around conflicts, the rest folded.</summary>
    private static void AddStretchRows(List<CompactRow> rows, MergeFile mf, int chunk, ref int lineNo)
    {
        var c = mf.Chunks[chunk];
        bool beforeConflict = chunk + 1 < mf.Chunks.Count && mf.Chunks[chunk + 1].Kind == MergeChunkKind.Conflict;
        bool afterConflict = chunk > 0 && mf.Chunks[chunk - 1].Kind == MergeChunkKind.Conflict;
        var lines = c.Kind == MergeChunkKind.AutoTheirs ? c.Theirs : c.Ours;
        bool auto = c.Kind != MergeChunkKind.Stable;
        int head = afterConflict ? Math.Min(CompactContext, lines.Length) : 0;
        int tail = beforeConflict ? Math.Min(CompactContext, lines.Length - head) : 0;
        if (mf.Conflicts.Count == 0 || auto) { head = lines.Length; tail = 0; } // automatic changes stay visible, tinted

        var style = auto ? CompactStyle.AutoLine : CompactStyle.Context;
        for (int k = 0; k < head; k++) rows.Add(new CompactRow(style, lines[k], lineNo + k));
        int folded = lines.Length - head - tail;
        if (folded > 0) rows.Add(new CompactRow(CompactStyle.Collapsed, $"⋯ {folded} lines"));
        for (int k = lines.Length - tail; k < lines.Length; k++) rows.Add(new CompactRow(style, lines[k], lineNo + k));
        lineNo += lines.Length;
    }

    private readonly record struct CardText(string Text, TextSpan[] Spans);
    private sealed record CardRows(List<CardText> OursRows, List<CardText> TheirsRows);

    /// <summary>The lines of a conflict card with word-level highlights where lines of the two sides pair up.</summary>
    private static CardRows AlignedForCard(MergeChunk c)
    {
        var ours = new List<CardText>();
        var theirs = new List<CardText>();
        int budget = LineAligner.NewBudget();
        var pairs = LineAligner.Align(c.Ours, c.Theirs, ref budget);
        var spansO = new TextSpan[c.Ours.Length][];
        var spansT = new TextSpan[c.Theirs.Length][];
        foreach (var (a, b) in pairs)
        {
            if (a < 0 || b < 0) continue;
            var changes = Intraline.Compute(TextDocument.ContentString(c.Ours[a]), TextDocument.ContentString(c.Theirs[b]));
            spansO[a] = changes.Left;
            spansT[b] = changes.Right;
        }
        for (int i = 0; i < c.Ours.Length; i++) ours.Add(new CardText(c.Ours[i], spansO[i] ?? []));
        for (int j = 0; j < c.Theirs.Length; j++) theirs.Add(new CardText(c.Theirs[j], spansT[j] ?? []));
        return new CardRows(ours, theirs);
    }

    private static IEnumerable<OutputLine> OutputForChunk(MergeFile mf, int chunk)
    {
        foreach (var line in mf.Output) if (line.Chunk == chunk) yield return line;
    }

    private static string TrimLeft(string text, int max)
    {
        if (text.Length <= max) return text;
        return max <= 1 ? "…" : "…" + text[(text.Length - max + 1)..];
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB",
    };

    private static uint Blend(uint a, uint b)
    {
        uint r = (((a >> 16) & 0xFF) + ((b >> 16) & 0xFF)) / 2;
        uint g = (((a >> 8) & 0xFF) + ((b >> 8) & 0xFF)) / 2;
        uint bl = ((a & 0xFF) + (b & 0xFF)) / 2;
        return (r << 16) | (g << 8) | bl;
    }
}

/// <summary>Maps a long list of rows to a column of cells: each cell takes the highest severity of its rows.</summary>
public static class Overview
{
    public static int[] Compute(int rowCount, int height, Func<int, int> severity)
    {
        var cells = new int[Math.Max(0, height)];
        if (rowCount == 0 || height <= 0) return cells;
        // Cap the work: a huge file samples a bounded number of rows per cell, always including where severity is high.
        int stride = Math.Max(1, rowCount / (height * 64));
        for (int i = 0; i < rowCount; i += stride)
        {
            int cell = Math.Min(height - 1, i * height / rowCount);
            int last = Math.Min(height - 1, Math.Max(cell, (i + 1) * height / rowCount - 1)); // short files: a row covers several cells
            int s = severity(i);
            for (int c = cell; c <= last; c++) if (s > cells[c]) cells[c] = s;
        }
        return cells;
    }
}

/// <summary>Drawing helpers shared by the screens.</summary>
public static class CompareRender
{
    /// <summary>
    /// Draws a line's content with tab expansion, horizontal scroll and highlighted spans; the rest of the row stays as
    /// filled. Control characters show as a dot (<see cref="Frame.Text"/> does the same for names).
    /// </summary>
    public static void Line(Frame f, FrameRun at, ReadOnlySpan<char> text, int hscroll, TextSpan[] spans, in FrameLineStyle style)
    {
        var (x, y, width) = at;
        if (width <= 0) return;
        uint bg = style.Bg, spanBg = style.SpanBg;
        var row = new LineRow(f, x, y, width, hscroll, style.Fg, style.Attr);
        int col = 0, spanIndex = 0, charIndex = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            while (spanIndex < spans.Length && charIndex >= spans[spanIndex].End) spanIndex++;
            uint cellBg = InSpan(spans, spanIndex, charIndex) ? spanBg : bg;
            int w = CellWidth(rune.Value, col);
            charIndex += rune.Utf16SequenceLength;
            if (w == 0) { row.DrawZeroWidth(rune, col, cellBg); continue; }
            row.Draw(rune, col, w, cellBg);
            col += w;
            if (col >= hscroll + width && spanIndex >= spans.Length) break;
        }
    }

    private static bool InSpan(TextSpan[] spans, int spanIndex, int charIndex) =>
        spanIndex < spans.Length && charIndex >= spans[spanIndex].Start && charIndex < spans[spanIndex].End;

    private static int CellWidth(int cp, int col)
    {
        if (cp == '\t') return Frame.TabAdvance(col);
        return Frame.IsControl(cp) ? 1 : ErikTerm.Core.CharWidth.Of(cp);
    }

    /// <summary>The row a line is drawn on: columns count from the line's start, the first shown one is HScroll.</summary>
    private readonly record struct LineRow(Frame F, int X, int Y, int Width, int HScroll, uint Fg, CellAttrs Attr)
    {
        public void DrawZeroWidth(System.Text.Rune rune, int col, uint bg)
        {
            if (col > HScroll && col - HScroll - 1 < Width) F.Text(X + col - HScroll - 1, Y, 1, rune.ToString(), Fg, bg, Attr);
        }

        public void Draw(System.Text.Rune rune, int col, int w, uint bg)
        {
            if (col + w <= HScroll || col >= HScroll + Width) return;
            int cp = rune.Value;
            if (cp != '\t' && col >= HScroll && col + w <= HScroll + Width)
            {
                F.Text(X + col - HScroll, Y, w, Frame.IsControl(cp) ? "·" : rune.ToString(), Fg, bg, Attr);
                return;
            }
            for (int k = Math.Max(col, HScroll); k < Math.Min(col + w, HScroll + Width); k++) F.Put(X + k - HScroll, Y, " ", Fg, bg, Attr);
        }
    }
}
