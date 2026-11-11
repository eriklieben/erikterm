using ErikTerm.Core;

namespace ErikTerm.Ui;

/// <summary>What a tick on the scrollbar stands for (several can share a pixel).</summary>
[Flags]
public enum ScrollMarkKinds : byte
{
    None = 0,
    /// <summary>A shell prompt (OSC 133;A).</summary>
    Prompt = 1,
    /// <summary>The prompt of a command that failed (OSC 133;D with a non-zero status).</summary>
    Failed = 2,
    /// <summary>A search match, while the search bar (or vim's / search) is open on the pane.</summary>
    Search = 4,
    /// <summary>A vim-mode mark (m{a-z}).</summary>
    Mark = 8,
}

/// <summary>A tick <see cref="ScrollMarks.Find"/> found: the line it marks and what kind of mark it is (one kind).</summary>
public readonly record struct ScrollMarkHit(long Line, ScrollMarkKinds Kind);

/// <summary>
/// Scrollbar marks for one pane (ghostty / iTerm2 / VS Code style): where its prompts and failed commands are, as
/// absolute line numbers, so they stay put while output scrolls; and, per frame, which pixel rows of the scrollbar's
/// track carry a tick — prompts, failures, search matches, vim marks.
/// <para>Cheap by construction: a scrollback row is looked at once, when it scrolls into the history (marks are only
/// ever written to screen rows; a failed status that arrives later is patched through the finished command), the
/// screen rows on each update, and everything again only after a reflow. The pixel buckets are rebuilt only when
/// their inputs change. No allocation in steady state (the arrays grow, rarely).</para>
/// </summary>
public sealed class ScrollMarks
{
    private long[] _lines = new long[64];
    private bool[] _failed = new bool[64];
    private int _start, _count;              // live entries: [_start, _start + _count), sorted by line
    private bool _scanned;                   // the history has been looked at since the last reset
    private long _scannedEnd;                // history rows below this absolute line have been looked at
    private int _reflow = int.MinValue;
    private long _finished = -1;

    // Pixel buckets of the last Ticks call, and what they were built from.
    private byte[] _buckets = new byte[256];
    private int _bucketCount;
    private (long Signature, long First, long Total, int Height, int SearchCount, long SearchSignature, long MarkSignature) _built;
    private long _signature;

    /// <summary>Prompts known (in the history and on the screen).</summary>
    public int Count => _count;

    /// <summary>The <paramref name="i"/>-th prompt's absolute line (oldest first).</summary>
    public long LineAt(int i) => _lines[_start + i];

    /// <summary>Whether the <paramref name="i"/>-th prompt's command failed.</summary>
    public bool FailedAt(int i) => _failed[_start + i];

    /// <summary>Changes whenever the prompt list (or a failed status) does.</summary>
    public long Signature => _signature;

    /// <summary>Brings the prompt list up to date with the terminal (call with the terminal locked).</summary>
    public void Update(Terminal t)
    {
        if (t.IsAlternateScreen) return; // no scrollbar there; the main screen's marks are still valid afterwards

        if (t.ReflowCount != _reflow || !_scanned) RestartScan(t);
        DropPromptsAbove(t.FirstLine);
        ScanNewRows(t);
        RefreshFailedStatuses(t);

        long sig = _count;
        for (int i = _start; i < _start + _count; i++) sig = sig * 31 + _lines[i] * 2 + (_failed[i] ? 1 : 0);
        _signature = sig;
    }

    private void RestartScan(Terminal t)
    {
        _reflow = t.ReflowCount;
        _start = _count = 0;
        _scanned = true;
        _scannedEnd = t.FirstLine;
        _finished = t.CommandsFinished;
    }

    /// <summary>Lines dropped off the top of the history (or cleared) take their prompts with them.</summary>
    private void DropPromptsAbove(long first)
    {
        while (_count > 0 && _lines[_start] < first) { _start++; _count--; }
        if (_count == 0) _start = 0;
        if (_scannedEnd < first) _scannedEnd = first;
    }

    /// <summary>The screen part of the last update is looked at again; history rows that arrived since are looked at once.</summary>
    private void ScanNewRows(Terminal t)
    {
        while (_count > 0 && _lines[_start + _count - 1] >= _scannedEnd) _count--;
        long screenTop = t.ScreenTopLine;
        for (long line = _scannedEnd; line < screenTop; line++) Consider(t, line);
        _scannedEnd = Math.Max(_scannedEnd, screenTop);
        long end = t.EndLine;
        for (long line = screenTop; line < end; line++) Consider(t, line);
    }

    /// <summary>A command's failed status lands on its prompt row when it finishes, which may be in the history by then.</summary>
    private void RefreshFailedStatuses(Terminal t)
    {
        long finished = t.CommandsFinished;
        if (finished == _finished) return;
        if (finished - _finished == 1) Refresh(t, t.LastFinishedCommand.PromptLine);
        else for (int i = 0; i < _count; i++) _failed[_start + i] = t.GetLine(_lines[_start + i])?.CommandFailed == true;
        _finished = finished;
    }

    private void Consider(Terminal t, long line)
    {
        if (t.GetLine(line) is not { } row || (row.Marks & SemanticMarks.Prompt) == 0) return;
        if (_start + _count == _lines.Length)
        {
            if (_start > 0)
            {
                Array.Copy(_lines, _start, _lines, 0, _count);
                Array.Copy(_failed, _start, _failed, 0, _count);
                _start = 0;
            }
            if (_count == _lines.Length)
            {
                Array.Resize(ref _lines, _lines.Length * 2);
                Array.Resize(ref _failed, _failed.Length * 2);
            }
        }
        _lines[_start + _count] = line;
        _failed[_start + _count] = row.CommandFailed;
        _count++;
    }

    private void Refresh(Terminal t, long line)
    {
        int i = IndexOf(line);
        if (i >= 0) _failed[_start + i] = t.GetLine(line)?.CommandFailed == true;
    }

    /// <summary>The entry for <paramref name="line"/>, or -1.</summary>
    private int IndexOf(long line)
    {
        int lo = 0, hi = _count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            long v = _lines[_start + mid];
            if (v == line) return mid;
            if (v < line) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>The first prompt at or after <paramref name="line"/> (an entry index, or <see cref="Count"/>).</summary>
    private int LowerBound(long line)
    {
        int lo = 0, hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_lines[_start + mid] < line) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // ---- Ticks ---------------------------------------------------------------------------------

    /// <summary>
    /// The track's pixel rows with what they carry, for lines [<paramref name="first"/>, <paramref name="first"/> +
    /// <paramref name="total"/>) drawn over <paramref name="height"/> pixels: line L sits at
    /// <c>(L - first) * height / total</c>, the same proportion as the scrollbar's thumb. <paramref name="search"/> are
    /// the open search's matches (sorted, may be empty), <paramref name="marks"/> vim marks' lines. Rebuilt only when
    /// something changed; the span is valid until the next call.
    /// </summary>
    public ReadOnlySpan<byte> Ticks(long first, long total, int height, IReadOnlyList<SearchMatch> search, long searchSignature,
        ReadOnlySpan<long> marks)
    {
        if (height <= 0 || total <= 0) return default;
        long markSig = marks.Length;
        foreach (long m in marks) markSig = markSig * 31 + m;
        var key = (_signature, first, total, height, search.Count, searchSignature, markSig);
        if (key == _built && _bucketCount == height) return _buckets.AsSpan(0, height);
        _built = key;
        if (_buckets.Length < height) _buckets = new byte[Math.Max(height, _buckets.Length * 2)];
        _bucketCount = height;
        var b = _buckets.AsSpan(0, height);
        b.Clear();

        AddPromptTicks(b, first, total);
        AddSearchTicks(b, first, total, search);
        foreach (long m in marks)
            if (Bucket(m, first, total, height) is var y and >= 0) b[y] |= (byte)ScrollMarkKinds.Mark;
        return b;
    }

    private void AddPromptTicks(Span<byte> b, long first, long total)
    {
        for (int i = _start; i < _start + _count; i++)
            if (Bucket(_lines[i], first, total, b.Length) is var y and >= 0)
                b[y] |= (byte)(_failed[i] ? ScrollMarkKinds.Failed : ScrollMarkKinds.Prompt);
    }

    private static void AddSearchTicks(Span<byte> b, long first, long total, IReadOnlyList<SearchMatch> search)
    {
        int height = b.Length;
        if (search.Count <= height * 2)
        {
            for (int i = 0; i < search.Count; i++)
                if (Bucket(search[i].StartLine, first, total, height) is var y and >= 0) b[y] |= (byte)ScrollMarkKinds.Search;
            return;
        }
        // Many matches: per pixel row, is there one in its lines (a binary search each)?
        for (int y = 0; y < height; y++)
        {
            var (from, to) = LinesOfRow(y, first, total, height);
            int k = SearchLowerBound(search, from);
            if (k < search.Count && search[k].StartLine < to) b[y] |= (byte)ScrollMarkKinds.Search;
        }
    }

    /// <summary>The pixel row (0 … height-1) of <paramref name="line"/>, or -1 when it's outside the range.</summary>
    public static int Bucket(long line, long first, long total, int height)
    {
        if (line < first || line >= first + total || total <= 0) return -1;
        return (int)Math.Min(height - 1, (line - first) * height / total);
    }

    /// <summary>The lines [From, To) that <see cref="Bucket"/> puts on pixel row <paramref name="y"/> (empty when there are more rows than lines).</summary>
    private static (long From, long To) LinesOfRow(int y, long first, long total, int height) =>
        (first + (y * total + height - 1) / height, first + ((y + 1) * total + height - 1) / height);

    /// <summary>
    /// The mark nearest to pixel row <paramref name="y"/> (within <paramref name="slop"/> rows) of the last
    /// <see cref="Ticks"/> call: its line and kind. Failed commands win over search matches, vim marks and prompts at
    /// the same distance; at the same distance the row above wins over the row below. False when there's no tick there.
    /// </summary>
    public bool Find(int y, int slop, long first, long total, IReadOnlyList<SearchMatch> search, ReadOnlySpan<long> marks,
        out ScrollMarkHit hit)
    {
        hit = default;
        if (_bucketCount <= 0 || total <= 0) return false;
        for (int d = 0; d <= slop; d++)
        {
            if (FindInRow(y - d, first, total, search, marks, out hit)) return true;
            if (d > 0 && FindInRow(y + d, first, total, search, marks, out hit)) return true;
        }
        return false;
    }

    private bool FindInRow(int row, long first, long total, IReadOnlyList<SearchMatch> search, ReadOnlySpan<long> marks,
        out ScrollMarkHit hit)
    {
        hit = default;
        int height = _bucketCount;
        if (row < 0 || row >= height || _buckets[row] == 0) return false;
        var flags = (ScrollMarkKinds)_buckets[row];
        var (from, to) = LinesOfRow(row, first, total, height);
        foreach (var k in (ReadOnlySpan<ScrollMarkKinds>)[ScrollMarkKinds.Failed, ScrollMarkKinds.Search, ScrollMarkKinds.Mark, ScrollMarkKinds.Prompt])
        {
            if ((flags & k) == 0) continue;
            if (FindIn(k, from, to, search, marks) is { } l) { hit = new ScrollMarkHit(l, k); return true; }
        }
        return false;
    }

    private long? FindIn(ScrollMarkKinds kind, long from, long to, IReadOnlyList<SearchMatch> search, ReadOnlySpan<long> marks)
    {
        switch (kind)
        {
            case ScrollMarkKinds.Prompt or ScrollMarkKinds.Failed:
                return FirstPrompt(kind == ScrollMarkKinds.Failed, from, to);
            case ScrollMarkKinds.Search:
                int k = SearchLowerBound(search, from);
                return k < search.Count && search[k].StartLine < to ? search[k].StartLine : null;
            default:
                foreach (long m in marks) if (m >= from && m < to) return m;
                return null;
        }
    }

    private long? FirstPrompt(bool failed, long from, long to)
    {
        for (int i = LowerBound(from); i < _count && _lines[_start + i] < to; i++)
            if (_failed[_start + i] == failed) return _lines[_start + i];
        return null;
    }

    private static int SearchLowerBound(IReadOnlyList<SearchMatch> matches, long line)
    {
        int lo = 0, hi = matches.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (matches[mid].StartLine < line) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
