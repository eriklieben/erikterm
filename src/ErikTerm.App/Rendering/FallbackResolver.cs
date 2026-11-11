namespace ErikTerm.App.Rendering;

/// <summary>
/// Which installed font file covers which code points: <c>fc-list --format '%{file}\t%{charset}\n'</c>, read once
/// (one process, ~0.1 s, off the UI thread). Lets <see cref="FallbackResolver"/> answer "no font has this" without
/// asking fontconfig per code point, and pick a covering file when fc-match's answer doesn't cover it.
/// </summary>
internal sealed class FontCoverage
{
    private readonly List<(string Path, int[] Ranges)> _files = []; // Ranges: start, end (inclusive) pairs, sorted
    private readonly Dictionary<string, int> _byPath = new(StringComparer.Ordinal);
    private int[] _all = []; // the union of every file's ranges

    public int FileCount => _files.Count;

    /// <summary>Parses fc-list output; lines that don't parse are skipped. Several lines for one file (a collection) merge.</summary>
    public static FontCoverage Parse(string fcList)
    {
        var coverage = new FontCoverage();
        var perFile = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        var order = new List<string>();
        var all = new List<(int, int)>();
        foreach (var rawLine in fcList.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            string path = line[..tab];
            if (!perFile.TryGetValue(path, out var ranges))
            {
                perFile[path] = ranges = [];
                order.Add(path);
            }
            AddRangeTokens(line[(tab + 1)..], ranges, all);
        }
        // Scalable formats first (FreeType draws them at any size); bitmap fonts only as a last resort.
        foreach (var path in order.OrderBy(p => IsScalable(p) ? 0 : 1))
        {
            coverage._byPath[path] = coverage._files.Count;
            coverage._files.Add((path, Merge(perFile[path])));
        }
        coverage._all = Merge(all);
        return coverage;
    }

    private static void AddRangeTokens(string list, List<(int, int)> ranges, List<(int, int)> all)
    {
        foreach (var token in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = token.IndexOf('-');
            if (dash < 0)
            {
                if (TryHex(token, out int cp)) { ranges.Add((cp, cp)); all.Add((cp, cp)); }
            }
            else if (TryHex(token[..dash], out int lo) && TryHex(token[(dash + 1)..], out int hi) && hi >= lo)
            {
                ranges.Add((lo, hi));
                all.Add((lo, hi));
            }
        }
    }

    /// <summary>
    /// An index from ranges read directly (macOS: FreeType's charmaps, no fontconfig), in the order given — preferred
    /// files first; <see cref="FirstCovering"/> walks them in that order.
    /// </summary>
    public static FontCoverage FromRanges(IEnumerable<(string Path, List<(int Lo, int Hi)> Ranges)> files)
    {
        var coverage = new FontCoverage();
        var all = new List<(int, int)>();
        foreach (var (path, ranges) in files)
        {
            if (coverage._byPath.ContainsKey(path)) continue;
            coverage._byPath[path] = coverage._files.Count;
            coverage._files.Add((path, Merge([.. ranges])));
            all.AddRange(ranges);
        }
        coverage._all = Merge(all);
        return coverage;
    }

    private static bool IsScalable(string path) =>
        path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otc", StringComparison.OrdinalIgnoreCase);

    private static bool TryHex(string s, out int value) =>
        int.TryParse(s, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out value);

    private static int[] Merge(List<(int Lo, int Hi)> ranges)
    {
        ranges.Sort((a, b) => a.Lo.CompareTo(b.Lo));
        var merged = new List<int>(ranges.Count * 2);
        foreach (var (lo, hi) in ranges)
        {
            if (merged.Count > 0 && lo <= merged[^1] + 1) merged[^1] = Math.Max(merged[^1], hi);
            else { merged.Add(lo); merged.Add(hi); }
        }
        return [.. merged];
    }

    private static bool InRanges(int[] ranges, int cp)
    {
        int lo = 0, hi = ranges.Length / 2 - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < ranges[2 * mid]) hi = mid - 1;
            else if (cp > ranges[2 * mid + 1]) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>Some installed font has a glyph for <paramref name="cp"/>.</summary>
    public bool AnyCovers(int cp) => InRanges(_all, cp);

    /// <summary>The file has <paramref name="cp"/>; false for a file the index doesn't know.</summary>
    public bool Covers(string path, int cp) => _byPath.TryGetValue(path, out int i) && InRanges(_files[i].Ranges, cp);

    /// <summary>A file that has <paramref name="cp"/> (scalable ones first), or null.</summary>
    public string? FirstCovering(int cp)
    {
        if (!AnyCovers(cp)) return null;
        foreach (var (path, ranges) in _files)
            if (InRanges(ranges, cp)) return path;
        return null;
    }
}

internal enum FallbackKind
{
    /// <summary>Any font with a glyph for the code point.</summary>
    Text,
    /// <summary>A color (emoji) font: the caller checks that what comes back is one.</summary>
    Color,
}

/// <summary>
/// Finds font files for code points the fallback chain doesn't cover, on a thread of its own: asking fontconfig costs a
/// process per code point (~10 ms), which in the frame froze every window for seconds on a page of rare scripts.
/// <see cref="TryGet"/> answers at once — the file, "none", or "not yet" (the glyph shows as .notdef / blank until then);
/// when an answer arrives, <see cref="Version"/> changes and <see cref="Wake"/> runs, and the fonts redraw.
/// With the coverage index (<see cref="IFontLocator.BuildCoverage"/>), code points no font has are answered without a
/// process, and files found earlier are reused for their neighbours. Without it (Windows, no fc-list), a 128-code-point
/// block stops being looked up after a few misses.
/// </summary>
internal sealed class FallbackResolver
{
    private const int MaxQueued = 4096;
    private const int BlockMissLimit = 3;

    private static FallbackResolver? _shared;
    /// <summary>The process-wide resolver for <see cref="FontLocator.Current"/>; answers are the same for every FontSet.</summary>
    public static FallbackResolver Shared => _shared ??= new FallbackResolver(FontLocator.Current);

    private readonly IFontLocator _locator;
    private readonly object _sync = new();
    private readonly Dictionary<long, string?> _done = [];
    private readonly HashSet<long> _queued = [];
    private readonly Queue<long> _queue = new();
    private readonly List<string> _found = [];
    private readonly Dictionary<int, int> _blockMisses = [];
    private FontCoverage? _coverage;
    private bool _coverageBuilt;
    private Thread? _thread;
    private int _version, _lookups;

    public FallbackResolver(IFontLocator locator) => _locator = locator;

    /// <summary>Called (from the resolver's thread) when an answer arrives: App sets its loop wake-up.</summary>
    public Action? Wake { get; set; }

    /// <summary>Bumped for every answer that arrives.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Times the locator was asked for a code point (FindForCodepoint).</summary>
    public int Lookups => Volatile.Read(ref _lookups);

    /// <summary>
    /// True with the answer (<paramref name="path"/> null: no font has it) if it is known; otherwise queues the lookup
    /// and returns false. Never blocks on fontconfig, never allocates once the answer is known.
    /// </summary>
    public bool TryGet(int codepoint, FallbackKind kind, out string? path)
    {
        long key = (long)kind << 32 | (uint)codepoint;
        lock (_sync)
        {
            if (_done.TryGetValue(key, out path)) return true;
            if (_queued.Contains(key)) return false;
            if (_queued.Count >= MaxQueued) { path = null; return true; } // flooded: give up on this one (not remembered)
            _queued.Add(key);
            _queue.Enqueue(key);
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "font-fallback" };
                _thread.Start();
            }
            else Monitor.Pulse(_sync);
            return false;
        }
    }

    /// <summary>Waits until every queued lookup is answered (tests). False on timeout.</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_sync)
        {
            while (_queued.Count > 0)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0) return false;
                Monitor.Wait(_sync, (int)left);
            }
            return true;
        }
    }

    #pragma warning disable S2190 // the font lookup thread is a background worker for the process's lifetime; there is never a reason to stop it first
    private void Run()
    #pragma warning restore S2190
    {
        while (true)
        {
            long key;
            lock (_sync)
            {
                while (_queue.Count == 0) Monitor.Wait(_sync);
                key = _queue.Peek();
            }
            string? path;
            try
            {
                path = Lookup((int)(key & 0xFFFFFFFF), (FallbackKind)(key >> 32));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                path = null;
            }
            lock (_sync)
            {
                _queue.Dequeue();
                _queued.Remove(key);
                _done[key] = path;
                if (path is not null && !_found.Contains(path)) _found.Add(path);
                Interlocked.Increment(ref _version);
                Monitor.PulseAll(_sync);
            }
            Wake?.Invoke();
        }
    }

    private string? Lookup(int cp, FallbackKind kind)
    {
        if (!_coverageBuilt)
        {
            _coverage = _locator.BuildCoverage();
            _coverageBuilt = true;
        }
        var coverage = _coverage;
        if (coverage is not null)
        {
            if (!coverage.AnyCovers(cp)) return null; // no installed font has it: no process needed
            if (kind == FallbackKind.Text && FirstFoundCovering(coverage, cp) is { } known) return known; // a neighbour of a code point found before
        }
        else if (_blockMisses.GetValueOrDefault(cp >> 7) >= BlockMissLimit) return null;

        Interlocked.Increment(ref _lookups);
        string? path = _locator.FindForCodepoint(cp);
        if (coverage is not null && kind == FallbackKind.Text && (path is null || !coverage.Covers(path, cp)))
            path = coverage.FirstCovering(cp); // fc-match's best guess (e.g. NimbusSans) lacks it; take a file that has it
        if (path is null && coverage is null) _blockMisses[cp >> 7] = _blockMisses.GetValueOrDefault(cp >> 7) + 1;
        return path;
    }

    private string? FirstFoundCovering(FontCoverage coverage, int cp)
    {
        lock (_sync) return _found.FirstOrDefault(f => coverage.Covers(f, cp));
    }
}
