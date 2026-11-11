using ErikTerm.Core;

namespace ErikTerm.App.Search;

internal readonly record struct PaneSearchQuery(string Pattern, bool CaseSensitive, bool Regex);

/// <summary>One pane to search: where it is, for the group header, and its terminal.</summary>
internal sealed record PaneSearchTarget<TPane>(TPane Pane, Terminal Term, string Title, string Place) where TPane : class;

/// <summary>
/// One match. <see cref="Match"/> is in absolute line numbers, which survive old lines being evicted; the rest is what
/// a jump needs to tell whether the text is still there (<see cref="PaneSearchScan{TPane}.Check"/>).
/// </summary>
internal sealed record PaneSearchHit(SearchMatch Match, string Preview, int PreviewIndex, int PreviewLength, int TextHash, int Reflow, bool Alternate);

internal sealed record PaneSearchGroup<TPane>(PaneSearchTarget<TPane> Target, PaneSearchHit[] Hits, bool Capped) where TPane : class;

internal enum PaneSearchJump { Go, Stale, Hidden }

internal readonly record struct PaneSearchLimits(int ChunkRows, int PerPane, int Total, int MaxTimeouts)
{
    public static readonly PaneSearchLimits Default = new(ChunkRows: 512, PerPane: 500, Total: 5000, MaxTimeouts: 8);
}

/// <summary>What a scan has found so far, immutable: the UI thread reads it while the worker keeps going.</summary>
internal sealed record PaneSearchResults<TPane>(
    PaneSearchGroup<TPane>[] Groups, int Total, int Searched, int PaneCount, bool Done, bool Capped, bool TimedOut, bool Changed, string? Error)
    where TPane : class
{
    public static readonly PaneSearchResults<TPane> Empty = new([], 0, 0, 0, true, false, false, false, null);

    /// <summary>The list is not the whole story.</summary>
    public bool Incomplete => Capped || TimedOut || Changed;

    public string StatusText()
    {
        if (Error is not null) return "Invalid regex";
        if (!Done)
        {
            int pane = Math.Min(Searched + 1, PaneCount);
            return Total == 0 ? $"Searching pane {pane} of {PaneCount}…" : $"{Total} so far, searching pane {pane} of {PaneCount}…";
        }
        string plus = Capped ? "+" : "", panes = Groups.Length == 1 ? "pane" : "panes";
        string found = Total == 0 ? "No results" : $"{Total}{plus} matches in {Groups.Length} {panes}";
        return found + (Capped ? " · too many, showing the newest" : "") + (TimedOut ? " · regex timed out on some lines" : "")
            + (Changed ? " · a pane changed during the search" : "");
    }
}

/// <summary>
/// One run of "search every pane", meant for a worker thread (<see cref="Run"/>); <see cref="Snapshot"/> reads it from
/// any thread. Each pane is read newest line first, so when the results are capped the matches that go are the oldest.
/// <para>
/// Terminals are only touched under the process's terminal lock, and only to copy a chunk of rows into a private buffer
/// (rows get reused once evicted, so they can't be read later): the matching, including a regex that runs into its
/// timeout on a line, happens with the lock released and neither the UI nor the pty readers wait for it.
/// </para>
/// </summary>
internal sealed class PaneSearchScan<TPane> where TPane : class
{
    private const int PreviewBefore = 40, PreviewAfter = 110;

    private enum Chunk { Copied, Finished, Changed }

    private readonly record struct Shape(int Columns, int Rows, bool Alternate, int Reflow);

    private sealed class Group(PaneSearchTarget<TPane> target)
    {
        public readonly PaneSearchTarget<TPane> Target = target;
        public readonly List<PaneSearchHit> Hits = [];
        public bool Capped;
    }

    private readonly Lock _gate;
    private readonly IReadOnlyList<PaneSearchTarget<TPane>> _targets;
    private readonly PaneSearchLimits _limits;
    private readonly CancellationToken _cancel;
    private readonly SearchLineMatcher _matcher = new();
    private readonly SearchLineMatcher.MatchFound _onMatch;
    private readonly LogicalLineBuffer _buffer = new();
    private readonly List<LogicalLineSpan> _lines = [];
    private readonly List<(int Index, int Length)> _lineMatches = [];
    private readonly List<PaneSearchHit> _chunkHits = [];

    private readonly Lock _published = new(); // guards _groups' lists and the fields below; never the terminals
    private readonly List<Group> _groups = [];
    private int _total, _searched, _revision;
    private bool _done, _capped, _timedOut, _changed;

    private Shape _shape;
    private int _timeouts;

    public PaneSearchScan(Lock gate, IReadOnlyList<PaneSearchTarget<TPane>> targets, PaneSearchQuery query, PaneSearchLimits limits, CancellationToken cancel)
    {
        _gate = gate;
        _targets = targets;
        _limits = limits;
        _cancel = cancel;
        _onMatch = OnMatch;
        _matcher.SetQuery(query.Pattern, query.CaseSensitive, query.Regex);
    }

    /// <summary>Bumped when <see cref="Snapshot"/> would return something new.</summary>
    public int Revision => Volatile.Read(ref _revision);

    public PaneSearchResults<TPane> Snapshot()
    {
        lock (_published)
        {
            var groups = new List<PaneSearchGroup<TPane>>(_groups.Count);
            foreach (var g in _groups)
                if (g.Hits.Count > 0) groups.Add(new PaneSearchGroup<TPane>(g.Target, [.. g.Hits], g.Capped));
            return new PaneSearchResults<TPane>([.. groups], _total, _searched, _targets.Count, _done, _capped, _timedOut, _changed, _matcher.Error);
        }
    }

    /// <summary>Scans every pane; returns when done, capped or cancelled.</summary>
    public void Run()
    {
        try
        {
            if (!_matcher.HasQuery) return;
            for (int i = 0; i < _targets.Count && !_cancel.IsCancellationRequested; i++)
            {
                var group = new Group(_targets[i]);
                lock (_published) _groups.Add(group);
                _timeouts = 0;
                bool goOn = ScanPane(group);
                lock (_published) { _searched++; Volatile.Write(ref _revision, _revision + 1); }
                if (!goOn) break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Off-thread nothing would catch this: an unexpected failure leaves a partial list and says so.
            Console.Error.WriteLine($"erikterm: search across panes failed: {ex.GetType().Name}: {ex.Message}");
            lock (_published) _changed = true;
        }
        finally
        {
            lock (_published) { _done = true; Volatile.Write(ref _revision, _revision + 1); }
        }
    }

    /// <summary>Returns false when the whole scan should stop (cancelled, or the total cap reached).</summary>
    private bool ScanPane(Group group)
    {
        long upper = -1;
        int restarts = 0;
        while (!_cancel.IsCancellationRequested)
        {
            var chunk = CopyChunk(group.Target.Term, ref upper);
            if (chunk == Chunk.Finished) return true;
            if (chunk == Chunk.Changed)
            {
                // Resized or switched screens mid-scan: line numbers changed, so what's in the list is worthless.
                Forget(group);
                if (++restarts > 2)
                {
                    lock (_published) _changed = true;
                    return true;
                }
                upper = -1;
                continue;
            }
            if (!MatchChunk(group, out bool stopAll)) return !stopAll;
        }
        return false;
    }

    private void Forget(Group group)
    {
        lock (_published)
        {
            _total -= group.Hits.Count;
            group.Hits.Clear();
            group.Capped = false;
            Volatile.Write(ref _revision, _revision + 1);
        }
    }

    /// <summary>Copies the rows just above <paramref name="upper"/> into the buffer, whole logical lines, under the lock.</summary>
    private Chunk CopyChunk(Terminal t, ref long upper)
    {
        lock (_gate)
        {
            var shape = new Shape(t.Columns, t.Rows, t.IsAlternateScreen, t.ReflowCount);
            if (upper < 0)
            {
                upper = t.EndLine;
                _shape = shape;
            }
            else if (shape != _shape) return Chunk.Changed;

            long first = t.FirstLine; // lines may have been evicted since the last chunk: their numbers are simply gone
            if (upper <= first) return Chunk.Finished;
            long lower = LogicalLineBuffer.LogicalStart(t, Math.Max(first, upper - _limits.ChunkRows));
            _buffer.Clear();
            _lines.Clear();
            for (long line = lower; line < upper;)
            {
                line += _buffer.AppendLine(t, line, upper, out var span);
                if (span.Length > 0) _lines.Add(span);
            }
            upper = lower;
            return Chunk.Copied;
        }
    }

    /// <summary>Matches the copied lines, newest first, and publishes the hits. False when this pane is finished early.</summary>
    private bool MatchChunk(Group group, out bool stopAll)
    {
        stopAll = false;
        _chunkHits.Clear();
        bool more = true;
        for (int i = _lines.Count - 1; i >= 0 && more; i--)
        {
            if (_cancel.IsCancellationRequested) { stopAll = true; return false; }
            var span = _lines[i];
            _lineMatches.Clear();
            if (!_matcher.Find(_buffer.TextOf(span), _onMatch))
            {
                lock (_published) _timedOut = true;
                more = ++_timeouts < _limits.MaxTimeouts; // an unusable regex shouldn't grind through 100k lines
            }
            more &= CollectLine(group, span, ref stopAll);
        }
        lock (_published)
        {
            group.Hits.AddRange(_chunkHits);
            _total += _chunkHits.Count;
            Volatile.Write(ref _revision, _revision + 1);
        }
        return more;
    }

    private bool OnMatch(int index, int length)
    {
        _lineMatches.Add((index, length));
        return _lineMatches.Count <= _limits.PerPane; // one over the cap, so the cap can tell there was more
    }

    private bool CollectLine(Group group, LogicalLineSpan span, ref bool stopAll)
    {
        for (int j = _lineMatches.Count - 1; j >= 0; j--)
        {
            bool paneFull = group.Hits.Count + _chunkHits.Count >= _limits.PerPane;
            bool allFull = _total + _chunkHits.Count >= _limits.Total;
            if (paneFull || allFull)
            {
                lock (_published) { group.Capped = true; _capped = true; }
                stopAll = allFull;
                return false;
            }
            var (index, length) = _lineMatches[j];
            _chunkHits.Add(MakeHit(span, index, length));
        }
        return true;
    }

    private PaneSearchHit MakeHit(LogicalLineSpan span, int index, int length)
    {
        var text = _buffer.TextOf(span);
        int start = Math.Max(0, index - PreviewBefore), end = Math.Min(text.Length, index + length + PreviewAfter);
        while (start < index && text[start] == ' ') start++;
        if (start > 0 && char.IsLowSurrogate(text[start])) start++;
        if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
        return new PaneSearchHit(_buffer.Map(span, index, length), new string(text[start..end]), index - start, Math.Min(length, end - index),
            string.GetHashCode(text.Slice(index, length)), _shape.Reflow, _shape.Alternate);
    }

    /// <summary>
    /// Whether a jump to <paramref name="hit"/> can still land on its text: not if the line was evicted, the pane
    /// reflowed or switched screens, or the row was rewritten. Call with the terminal locked.
    /// </summary>
    public static PaneSearchJump Check(Terminal t, PaneSearchHit hit)
    {
        var m = hit.Match;
        if (t.ReflowCount != hit.Reflow || t.IsAlternateScreen != hit.Alternate) return PaneSearchJump.Stale;
        if (m.StartLine < t.FirstLine || m.EndLine >= t.EndLine) return PaneSearchJump.Stale;

        var buffer = new LogicalLineBuffer();
        buffer.AppendLine(t, LogicalLineBuffer.LogicalStart(t, m.StartLine), t.EndLine, out var span);
        if (buffer.Locate(span, m) is not { } at || at.Index + at.Length > span.Length) return PaneSearchJump.Stale;
        if (string.GetHashCode(buffer.TextOf(span).Slice(at.Index, at.Length)) != hit.TextHash) return PaneSearchJump.Stale;
        return t.IsAlternateScreen && m.StartLine < t.ScreenTopLine ? PaneSearchJump.Hidden : PaneSearchJump.Go;
    }
}
