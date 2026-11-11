using ErikTerm.Core.Images;

namespace ErikTerm.Core;

/// <summary>
/// Resizes the screen buffers for <see cref="Terminal.Resize"/>: rows that go into or come back from the scrollback,
/// rows made exactly as wide as the screen, and the re-wrapping of the main screen and scrollback when the column count
/// changes (reflow). Owns the scratch buffers reflow reuses. Works on what it is handed; the terminal keeps the cursor
/// and the rest of its state.
/// </summary>
internal sealed class ScreenResizer(Scrollback scrollback, CellArrayPool cellPool, ImageStore images)
{
    private readonly Scrollback _scrollback = scrollback;
    private readonly CellArrayPool _cellPool = cellPool;
    private readonly ImageStore _images = images;

    /// <summary>The main screen at a new height (same width): rows go into the scrollback or come back from it.</summary>
    public Row[] ResizeMain(Row[] old, int oldCols, int cols, int rows, ref int cursorY)
    {
        var list = new List<Row>(old);
        if (rows < list.Count)
        {
            int excess = list.Count - rows;
            // With the alternate screen active, cursorY can come from a saved cursor below the last row: a saved row
            // outlives a shorter screen and is clamped where it is used (RestoreCursor, ReflowMain). No row is below it then.
            // Blank rows below the cursor go first. Text below it stays when it can: the rows above the cursor scroll off instead.
            int roomBelow = Math.Clamp(list.Count - 1 - cursorY, 0, excess), blankBelow = 0;
            while (blankBelow < roomBelow && RowContent.IsBlankRow(list[list.Count - 1 - blankBelow])) blankBelow++;
            list.RemoveRange(list.Count - blankBelow, blankBelow);
            excess -= blankBelow;
            for (int scrolled = Math.Min(excess, Math.Max(0, cursorY)); scrolled > 0; scrolled--, excess--)
            {
                _scrollback.Push(list[0]); // the row it evicts, if any, isn't needed: the screen is shrinking
                list.RemoveAt(0);
                cursorY--;
            }
            if (excess > 0) list.RemoveRange(list.Count - excess, excess); // the cursor's row has to stay on the screen
        }
        else
        {
            int add = rows - list.Count;
            while (add > 0 && _scrollback.Count > 0)
            {
                list.Insert(0, _scrollback.PopNewest()!);
                cursorY++;
                add--;
            }
            while (add-- > 0) list.Add(new Row(cols));
        }
        foreach (var row in list) ResizeRow(row, oldCols, cols);
        return list.ToArray();
    }

    /// <summary>The alternate screen at a new size: rows cut off or added at the bottom.</summary>
    public Row[] ResizeSimple(Row[] old, int oldCols, int cols, int rows)
    {
        var result = new Row[rows];
        for (int i = 0; i < rows; i++)
        {
            result[i] = i < old.Length ? old[i] : new Row(cols);
            ResizeRow(result[i], oldCols, cols);
        }
        return result;
    }

    /// <summary>Makes a row exactly <paramref name="cols"/> wide (a screen row), widening a trimmed scrollback row.</summary>
    private void ResizeRow(Row row, int oldCols, int cols)
    {
        if (row.Cells.Length == cols)
        {
            if (row.Width != cols) row.SetCells(row.Cells, cols); // a trimmed row whose stored part happens to fit exactly
            return;
        }
        row.Touch();
        var old = row.Cells;
        var cells = new Cell[cols];
        int n = Math.Min(cols, old.Length);
        Array.Copy(old, cells, n);
        if (n == cols && (cells[n - 1].Flags & CellAttributes.WideLead) != 0) cells[n - 1] = default;
        row.SetCells(cells, cols);
        if (old.Length < oldCols) _cellPool.Return(old, oldCols); // trimmed storage: reusable by the next trimmed row
    }

    // Scratch buffers for reflow, kept between resizes (dragging a divider resizes continuously).
    private readonly List<Row> _reflowSrc = [], _reflowDst = [];
    private readonly List<int> _reflowStarts = [];
    private Cell[] _reflowCells = [];
    private string?[] _reflowClusters = [];
    private uint[] _reflowUnderline = []; // SGR 58 colors of the line being re-wrapped
    private readonly List<LinkSpan> _reflowLinks = []; // OSC 8 spans of the line being re-wrapped, in line offsets

    /// <summary>Where one reflow found the cursor and the prompt mark, and where they ended up. Kept on the stack.</summary>
    private struct ReflowPass
    {
        public int Cols, CursorCol;
        public bool CursorPending, AutoWrap;
        public int CursorSource, PromptSource; // indexes into _reflowSrc (the prompt: -1 when it has scrolled away)
        public int PromptTarget, NewCursorRow; // indexes into _reflowDst, -1 until found
        public int NewCursorCol;
        public bool NewCursorPending;
    }

    /// <summary>A logical line joined into the scratch buffers (cells, clusters, underline colors, _reflowLinks).</summary>
    private struct JoinedLine
    {
        public int Length;
        public bool AnyClusters, AnyUnderline;
        public SemanticMarks Marks;
        public int InputAt; // line offset of the input mark, or -1
        public int ExitCode;
        public int CursorOffset;
    }

    /// <summary>
    /// Re-wraps scrollback + main screen to <paramref name="cols"/> columns. A logical line is a run of rows whose
    /// rows except the last are <see cref="Row.Wrapped"/>. Lines that fit on one row are kept as they are (same
    /// Row object, cells untouched — scrollback rows may be wider or narrower than the screen); longer lines are
    /// joined and split again, reusing the line's Row objects in order. Row metadata follows the text: clusters and
    /// OSC 8 link spans move (and split) with their cells, OSC 133 prompt/output/end marks and the prompt's exit code go
    /// to the line's first row, and the input mark goes to the row holding <see cref="Row.CommandColumn"/>. Wide
    /// characters never straddle rows: one that would is moved to the next row leaving a blank, which joining drops again.
    /// </summary>
    public Row[] ReflowMain(Row[] main, int oldCols, int cols, int rows, ref long promptLine, ref ResizeCursor cursor)
    {
        var src = _reflowSrc;
        var dst = _reflowDst;
        src.Clear();
        dst.Clear();

        int cy = Math.Clamp(cursor.Y, 0, main.Length - 1);
        int cursorSource = CollectReflowRows(main, cols, cy);
        long oldFirst = _scrollback.Dropped;
        var pass = new ReflowPass
        {
            Cols = cols,
            CursorCol = cursor.X,
            CursorPending = cursor.Pending,
            AutoWrap = cursor.AutoWrap,
            CursorSource = cursorSource,
            PromptSource = promptLine >= oldFirst ? (int)Math.Min(int.MaxValue, promptLine - oldFirst) : -1,
            PromptTarget = -1,
            NewCursorRow = -1,
        };

        for (int i = 0; i < src.Count;)
        {
            int j = i;
            while (j < src.Count - 1 && src[j].Wrapped) j++;
            Row first = src[i];
            if (i == j && i != pass.CursorSource && i != pass.PromptSource && !_images.HasInlineImages && FitsOnOneRow(first, cols))
            {
                // Most lines: nothing to join and no cursor or mark to carry, so ReflowLine's bookkeeping is skipped.
                first.Wrapped = false;
                dst.Add(first);
            }
            else ReflowLine(ref pass, i, j);
            i = j + 1;
        }

        if (pass.NewCursorRow < 0) { pass.NewCursorRow = Math.Max(0, dst.Count - 1); pass.NewCursorCol = 0; pass.NewCursorPending = false; }

        int top = Math.Clamp(dst.Count - rows, 0, pass.NewCursorRow);
        _scrollback.Reset(dst, top); // marks every row for CompactScrollback again
        // Absolute line k of the re-wrapped list is oldFirst + k (rows dropped from the old end count in Dropped).
        if (pass.PromptTarget >= 0) promptLine = oldFirst + pass.PromptTarget;
        var screen = ReflowedScreen(oldCols, cols, rows, top);

        cursor.X = pass.NewCursorCol;
        cursor.Y = pass.NewCursorRow - top;
        cursor.Pending = pass.NewCursorPending;
        src.Clear();
        dst.Clear();
        return screen;
    }

    private static bool FitsOnOneRow(Row row, int cols) => row.Cells.Length <= cols || RowContent.ContentLength(row) <= cols;

    /// <summary>Lists the scrollback and the used part of the screen in <see cref="_reflowSrc"/>; returns the cursor row's index in it.</summary>
    private int CollectReflowRows(Row[] main, int cols, int cy)
    {
        var src = _reflowSrc;
        _scrollback.CopyTo(src, _images.HasInlineImages ? 0 : cols); // inline images are found by looking at the cells
        int scrollbackRows = src.Count;
        int lastUsed = cy;
        for (int y = main.Length - 1; y > cy; y--)
        {
            if (main[y].Wrapped || RowContent.ContentLength(main[y]) > 0) { lastUsed = y; break; }
        }
        for (int y = 0; y <= lastUsed; y++) src.Add(main[y]);
        return scrollbackRows + cy;
    }

    /// <summary>The screen after a reflow: the rows from <paramref name="top"/> on, resized to exactly <paramref name="cols"/>.</summary>
    private Row[] ReflowedScreen(int oldCols, int cols, int rows, int top)
    {
        var dst = _reflowDst;
        int total = dst.Count;
        var screen = new Row[rows];
        for (int y = 0; y < rows; y++)
        {
            int idx = top + y;
            screen[y] = idx < total ? dst[idx] : new Row(cols);
            screen[y].Unpack(); // a scrollback row moved up onto the screen
            ResizeRow(screen[y], oldCols, cols);
        }
        if (top + rows < total) screen[rows - 1].Wrapped = false; // its continuation was cut off below the screen
        return screen;
    }

    /// <summary>Moves logical line [i, j] of <see cref="_reflowSrc"/> to <see cref="_reflowDst"/>, carrying the cursor and prompt mark if they're on it.</summary>
    private void ReflowLine(ref ReflowPass pass, int i, int j)
    {
        var src = _reflowSrc;
        var dst = _reflowDst;
        bool hasCursor = pass.CursorSource >= i && pass.CursorSource <= j;
        int lineStart = dst.Count;
        _reflowStarts.Clear();
        int cursorOffset = 0;

        Row first = src[i];
        if (pass.PromptSource >= i && pass.PromptSource <= j) pass.PromptTarget = lineStart; // the prompt mark ends up on the line's first row
        if (_images.HasInlineImages && HasImageCells(src, i, j))
        {
            KeepImageRows(ref pass, i, j);
            return;
        }
        if (i == j && FitsOnOneRow(first, pass.Cols))
        {
            // Fits on one row: keep it untouched (the screen part is resized to exactly cols below).
            first.Wrapped = false;
            dst.Add(first);
            _reflowStarts.Add(0);
            cursorOffset = pass.CursorCol;
        }
        else
        {
            var line = new JoinedLine { InputAt = -1, ExitCode = Row.NoExitCode };
            JoinRows(in pass, i, j, ref line);
            SplitLine(pass.Cols, i, j, ref line);
            cursorOffset = line.CursorOffset;
        }

        if (hasCursor)
        {
            MapCursor(cursorOffset, in pass, out int r, out pass.NewCursorCol, out pass.NewCursorPending);
            pass.NewCursorRow = lineStart + r;
        }
    }

    /// <summary>
    /// iTerm2 / sixel tiles: the rows stay as they are (truncated on a narrower screen, never re-wrapped), so a
    /// picture keeps its shape instead of breaking up like text (Images/CellImages.cs).
    /// </summary>
    private void KeepImageRows(ref ReflowPass pass, int i, int j)
    {
        var dst = _reflowDst;
        int lineStart = dst.Count;
        for (int k = i; k <= j; k++) dst.Add(_reflowSrc[k]);
        if (pass.PromptSource >= i && pass.PromptSource <= j) pass.PromptTarget = lineStart + (pass.PromptSource - i);
        if (pass.CursorSource >= i && pass.CursorSource <= j)
        {
            pass.NewCursorRow = lineStart + (pass.CursorSource - i);
            pass.NewCursorCol = Math.Min(pass.CursorCol, pass.Cols - 1);
            pass.NewCursorPending = pass.CursorPending && pass.CursorCol >= pass.Cols - 1;
        }
    }

    /// <summary>Joins rows [i, j] into the scratch buffers. Metadata goes to line offsets; the rows' own copies are reset and re-assigned by <see cref="SplitLine"/>.</summary>
    private void JoinRows(in ReflowPass pass, int i, int j, ref JoinedLine line)
    {
        var src = _reflowSrc;
        _reflowLinks.Clear();
        for (int k = i; k <= j; k++)
        {
            Row r = src[k];
            int take = k < j ? r.Width : RowContent.ContentLength(r);
            if (k < j && take > 0 && RowContent.IsWidePadding(r, src[k + 1])) take--;
            if (k == pass.CursorSource) line.CursorOffset = line.Length + pass.CursorCol;
            if (r.Links is { Count: > 0 } spans) AppendLinks(spans, take, line.Length);
            if (r.Marks != SemanticMarks.None) MergeMarks(r, ref line);
            r.ResetMetadata();
            EnsureReflowCapacity(line.Length + take);
            JoinCells(r, take, line.Length);
            if (r.HasClusters)
            {
                line.AnyClusters = true;
                SaveClusters(r, take, line.Length);
            }
            JoinUnderlineColors(r, take, ref line);
            line.Length += take;
        }
    }

    /// <summary>Copies a row's cells to the joined line; a trimmed row's blank tail is cleared there.</summary>
    private void JoinCells(Row r, int take, int offset)
    {
        int stored = Math.Min(take, r.Cells.Length);
        Array.Copy(r.Cells, 0, _reflowCells, offset, stored);
        if (stored < take) Array.Clear(_reflowCells, offset + stored, take - stored);
    }

    /// <summary>Adds a row's underline colors to the joined line; the table only exists from the first row that has colors.</summary>
    private void JoinUnderlineColors(Row r, int take, ref JoinedLine line)
    {
        if (r.HasUnderlineColors)
        {
            if (!line.AnyUnderline) Array.Clear(_reflowUnderline, 0, line.Length);
            line.AnyUnderline = true;
            for (int x = 0; x < take; x++) _reflowUnderline[line.Length + x] = r.UnderlineColorAt(x);
        }
        else if (line.AnyUnderline) Array.Clear(_reflowUnderline, line.Length, take);
    }

    /// <summary>Adds a row's OSC 8 spans to <see cref="_reflowLinks"/>, merging one that continues the previous row's.</summary>
    private void AppendLinks(List<LinkSpan> spans, int take, int offset)
    {
        var links = _reflowLinks;
        foreach (var span in spans)
        {
            int e = Math.Min(span.End, take);
            if (e <= span.Start) continue;
            int ls = offset + span.Start, le = offset + e;
            if (links.Count > 0 && links[^1].Id == span.Id && links[^1].End == ls) links[^1] = new LinkSpan(links[^1].Start, le, span.Id);
            else links.Add(new LinkSpan(ls, le, span.Id));
        }
    }

    private static void MergeMarks(Row r, ref JoinedLine line)
    {
        line.Marks |= r.Marks & ~SemanticMarks.Input;
        if ((r.Marks & SemanticMarks.Input) != 0 && line.InputAt < 0) line.InputAt = line.Length + Math.Max(0, r.CommandColumn);
        if (line.ExitCode == Row.NoExitCode) line.ExitCode = r.ExitCode;
    }

    private void SaveClusters(Row r, int take, int offset)
    {
        for (int x = 0; x < take; x++)
            if ((r.Cells[x].Flags & CellAttributes.Cluster) != 0) _reflowClusters[offset + x] = r.GetClusterString(x);
    }

    /// <summary>Splits a joined line back into rows of <paramref name="cols"/>, reusing rows [i, j] in order.</summary>
    private void SplitLine(int cols, int i, int j, ref JoinedLine line)
    {
        var src = _reflowSrc;
        var dst = _reflowDst;
        int len = line.Length;
        int pos = 0, reuse = i;
        do
        {
            int take = Math.Min(cols, len - pos);
            if (take == cols && cols > 1 && pos + take < len && (_reflowCells[pos + take - 1].Flags & CellAttributes.WideLead) != 0)
                take--;
            Row nr = reuse <= j ? src[reuse++] : new Row(cols);
            // A piece that wraps is full width; the line's last one only stores its text (the scrollback keeps it
            // that way; one that lands on the screen is widened below).
            PrepareReflowRow(nr, cols, pos + take >= len ? take : cols);
            nr.ClearClusters();
            nr.ClearUnderlineColors();
            Array.Copy(_reflowCells, pos, nr.Cells, 0, take);
            if (line.AnyUnderline) RestoreUnderlineColors(nr, pos, take);
            if (line.AnyClusters) RestoreClusters(nr, pos, take);
            nr.Wrapped = true;
            RestoreRowMetadata(nr, pos, take, cols, ref line);
            _reflowStarts.Add(pos);
            dst.Add(nr);
            pos += take;
        } while (pos < len);
        dst[^1].Wrapped = false;
        if (line.AnyClusters) Array.Clear(_reflowClusters, 0, len);
    }

    private void RestoreUnderlineColors(Row nr, int pos, int take)
    {
        for (int x = 0; x < take; x++)
            if ((nr.Cells[x].Flags & CellAttributes.Underline) != 0 && _reflowUnderline[pos + x] != 0)
                nr.SetUnderlineColor(x, 1, _reflowUnderline[pos + x]);
    }

    private void RestoreClusters(Row nr, int pos, int take)
    {
        for (int x = 0; x < take; x++)
        {
            if ((nr.Cells[x].Flags & CellAttributes.Cluster) == 0) continue;
            if (_reflowClusters[pos + x] is { } text) nr.SetCluster(x, text);
            else nr.Cells[x].Flags &= ~CellAttributes.Cluster;
        }
    }

    /// <summary>Gives a re-wrapped piece its marks and link spans: the line's marks on its first piece, the input mark on the piece holding it.</summary>
    private void RestoreRowMetadata(Row nr, int pos, int take, int cols, ref JoinedLine line)
    {
        if (pos == 0)
        {
            nr.Marks = line.Marks;
            nr.ExitCode = line.ExitCode;
        }
        bool lastPiece = pos + take >= line.Length;
        if (line.InputAt >= pos && (line.InputAt < pos + take || lastPiece))
        {
            nr.Marks |= SemanticMarks.Input;
            nr.CommandColumn = Math.Min(line.InputAt - pos, cols - 1);
            line.InputAt = -1;
        }
        foreach (var span in _reflowLinks)
        {
            int s0 = Math.Max(span.Start, pos), s1 = Math.Min(span.End, pos + take);
            if (s1 > s0) nr.SetLink(s0 - pos, s1 - s0, span.Id);
        }
    }

    /// <summary>Maps a cursor at logical offset <paramref name="co"/> within the line just split (see <see cref="_reflowStarts"/>).</summary>
    private void MapCursor(int co, in ReflowPass pass, out int row, out int col, out bool pendingOut)
    {
        bool pending = pass.CursorPending;
        int cols = pass.Cols;
        var starts = _reflowStarts;
        int target = co + (pending ? 1 : 0);
        int r = starts.Count - 1;
        while (r > 0 && starts[r] > target) r--;
        int c = target - starts[r];
        pendingOut = false;
        if (pending)
        {
            if (c == 0 && r > 0 && starts[r] - starts[r - 1] == cols) { r--; c = cols - 1; pendingOut = true; }
            else if (c >= cols) { c = cols - 1; pendingOut = true; }
        }
        else if (c >= cols)
        {
            // Just past a now-full last row: the next character wraps, so make that a pending wrap.
            pendingOut = c == cols && pass.AutoWrap;
            c = cols - 1;
        }
        row = r;
        col = c;
    }

    /// <summary>Whether rows [i, j] (one logical line) hold any image tile.</summary>
    private static bool HasImageCells(List<Row> rows, int i, int j)
    {
        for (int k = i; k <= j; k++)
            foreach (ref readonly var c in rows[k].Cells.AsSpan())
                if ((c.Flags & CellAttributes.Image) != 0) return true;
        return false;
    }

    private void EnsureReflowCapacity(int n)
    {
        if (_reflowCells.Length >= n) return;
        int size = Math.Max(n, _reflowCells.Length * 2);
        Array.Resize(ref _reflowCells, size);
        Array.Resize(ref _reflowClusters, size);
        Array.Resize(ref _reflowUnderline, size);
    }

    /// <summary>
    /// Readies a reused (or new) row for a re-wrapped piece: <paramref name="stored"/> zeroed cells, width
    /// <paramref name="cols"/>. Its old array goes back to the pool when it can't be reused.
    /// </summary>
    private void PrepareReflowRow(Row row, int cols, int stored)
    {
        int size = stored >= cols ? cols : Math.Min(cols, CellArrayPool.SizeFor(stored));
        var cells = row.Cells;
        if (cells.Length == size) Array.Clear(cells);
        else
        {
            if (!row.CellsPinned) _cellPool.Return(cells, cols);
            cells = size == cols ? _cellPool.RentFull(cols) : _cellPool.Rent(size, cols);
            Array.Clear(cells);
        }
        row.SetCells(cells, cols);
    }
}

/// <summary>The terminal's cursor across a resize: where it was, and after <see cref="ScreenResizer.ReflowMain"/> where it is.</summary>
internal struct ResizeCursor
{
    public int X, Y;
    public bool Pending;
    /// <summary>DECAWM: a cursor just past a now-full last row becomes a pending wrap only with auto-wrap on.</summary>
    public bool AutoWrap;
}
