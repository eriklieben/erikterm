using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ErikTerm.Core;

/// <summary>
/// One row of the grid: its cells and the side data that doesn't belong in <see cref="Cell"/> (which stays 16 bytes).
/// <para>
/// Cell storage: a row on the screen holds exactly <see cref="Width"/> cells. A scrollback row can be trimmed
/// (<see cref="Trim"/>, done by <see cref="Terminal.CompactScrollback"/> while the terminal is idle): its trailing
/// default cells (no glyph, default colors, no attributes) are dropped, so <see cref="Cells"/> may be shorter than
/// <see cref="Width"/> and the missing cells read as <c>default(Cell)</c>. Most scrollback lines are much shorter than
/// the screen is wide, and a full-width <see cref="Cell"/>[] is 16 bytes per column. Readers loop to
/// <c>Cells.Length</c> (or use <see cref="CellAt"/> up to <see cref="Width"/> where the blank tail matters: word
/// selection, vim motions); writers only ever see full-width screen rows (a trimmed row that comes back onto the screen
/// is widened again). A scrollback row at rest can also be packed (<see cref="PackedCells"/>): <see cref="Cells"/> is
/// empty then, so a row only comes out of <see cref="Scrollback"/> unpacked.
/// </para>
/// <para>
/// Side data: grapheme clusters (<see cref="RowClusters"/>, held by value), SGR 58 underline colors
/// (<see cref="RowUnderlineColors"/>),
/// OSC 8 hyperlink spans (<see cref="Links"/>) and shell-integration marks. Everything is reset by <see cref="Clear"/>,
/// so recycled scrollback rows never carry stale metadata; packing leaves it where it is.
/// </para>
/// </summary>
public sealed class Row
{
    /// <summary>
    /// The row's cells by column. May be shorter than <see cref="Width"/> on a scrollback row (the rest is blank). A
    /// field, not a property: it is read on every print, and the hot paths measured slower through a getter. Replace it
    /// with <see cref="SetCells"/> (which keeps <see cref="Width"/> right).
    /// </summary>
#pragma warning disable S1104 // read on every print; the hot paths measured slower through a getter (see the summary above)
    public Cell[] Cells;
#pragma warning restore S1104
    private int _width;

#pragma warning disable S3459 // a mutable struct held by value: its methods change it through ref, which this rule doesn't see as assignment
    private RowClusters _clusters;          // by value: see RowClusters
#pragma warning restore S3459
    private uint[]? _underlineColors;       // RowUnderlineColors works on these two
    private bool _hasUnderlineColors;
    private byte[]? _packed;
    private int _packedLength;

    public Row(int columns) : this(new Cell[columns]) { }

    /// <summary>A row over <paramref name="cells"/> (its width is their length).</summary>
    public Row(Cell[] cells)
    {
        Cells = cells;
        _width = cells.Length;
    }

    /// <summary>The line continues on the next row (soft wrap) — needed for reflow and copy.</summary>
    public bool Wrapped { get; set; }

    /// <summary>
    /// Bumped by every change to what the row shows (cells, clusters, links, prompt marks), so renderers can
    /// cache per-row output keyed by (row, version). Writers outside Terminal that touch <see cref="Cells"/>
    /// directly must call <see cref="Touch"/>.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>Marks the row as changed (see <see cref="Version"/>).</summary>
    public void Touch() => Version++;

    public void Clear(uint bg)
    {
        Version++;
        Cells.AsSpan().Fill(Cell.Blank(bg));
        Wrapped = false;
        ClearMetadata();
        ForgetClusters();
        _hasUnderlineColors = false;
    }

    // Out of line, as the field version always was: inlined, it made Clear slower on the scroll path, which calls it
    // once per line (measured: up to 8% on DECSTBM scrolling).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ForgetClusters() => _clusters.Clear();

    // ---- Cell storage ---------------------------------------------------------------------------

    /// <summary>Columns the row spans (its width when it was written); cells past <c>Cells.Length</c> are blank.</summary>
    public int Width => Math.Max(_width, Cells.Length);

    /// <summary>The row's trailing blank cells aren't stored (see <see cref="Trim"/>).</summary>
    public bool IsTrimmed => Cells.Length < _width;

    /// <summary>The cell at a column, blank past the stored cells (and outside the row).</summary>
    public Cell CellAt(int col) => (uint)col < (uint)Cells.Length ? Cells[col] : default;

    /// <summary>Replaces the stored cells; the row spans <paramref name="width"/> columns (at least their length).</summary>
    public void SetCells(Cell[] cells, int width)
    {
        if (!ReferenceEquals(cells, Cells)) CellsPinned = false;
        _packed = null;
        Cells = cells;
        _width = Math.Max(width, cells.Length);
    }

    /// <summary>
    /// Drops the trailing default cells, moving the rest into an array from <paramref name="pool"/>. Soft-wrapped rows
    /// are left whole (their text runs to the edge; reflow and copy rely on their width), and so are rows that wouldn't
    /// get smaller. Returns the previous array, free for the caller to reuse, or null when nothing changed. A blank cell
    /// with a background color, an underline (any style or color) or any other attribute isn't default and is kept.
    /// What the row shows doesn't change, so <see cref="Version"/> stays.
    /// </summary>
    internal Cell[]? Trim(CellArrayPool pool)
    {
        if (Wrapped) return null;
        var cells = Cells;
        int n = StoredLength(cells);
        int size = CellArrayPool.SizeFor(n);
        if (size >= cells.Length) return null;
        var small = pool.Rent(size, cells.Length);
        cells.AsSpan(0, n).CopyTo(small);
        small.AsSpan(n).Clear();
        Cells = small;
        CellsPinned = false;
        return cells;
    }

    /// <summary>
    /// The cell array is on the pinned object heap. Rows made while the scrollback fills are (<see cref="NewForScrollback"/>):
    /// a generational GC never has to copy their cells during a flood. <see cref="Terminal.CompactScrollback"/> moves
    /// them to ordinary arrays once output stops, so the pinned regions can be released.
    /// </summary>
    internal bool CellsPinned { get; private set; }

    /// <summary>A full-width row whose cells don't go through generation promotion (see <see cref="CellsPinned"/>).</summary>
    internal static Row NewForScrollback(int columns) => new(GC.AllocateArray<Cell>(columns, pinned: true)) { CellsPinned = true };

    /// <summary>Replaces a pinned cell array with an ordinary copy of it; returns the bytes of pinned storage let go.</summary>
    internal long MoveCellsOffPinnedHeap()
    {
        var cells = new Cell[Cells.Length];
        Cells.AsSpan().CopyTo(cells);
        Cells = cells;
        CellsPinned = false;
        return (long)cells.Length * Unsafe.SizeOf<Cell>();
    }

    /// <summary>
    /// Length of <paramref name="cells"/> without its trailing default cells. A vectorized scan for the last non-zero
    /// byte: struct padding is always zero in practice (cells are built with <c>new</c> / <c>default</c>), and if it
    /// ever weren't, that cell would merely be kept.
    /// </summary>
    internal static int StoredLength(ReadOnlySpan<Cell> cells)
    {
        int last = MemoryMarshal.AsBytes(cells).LastIndexOfAnyExcept((byte)0);
        return last < 0 ? 0 : last / Unsafe.SizeOf<Cell>() + 1;
    }

    // ---- Packing (PackedCells.cs) ---------------------------------------------------------------

    public bool IsPacked => _packed is not null;

    /// <summary>A packed row that is its own line and, once unpacked, would be no wider than <paramref name="columns"/>.</summary>
    internal bool PackedFits(int columns) => !Wrapped && _packedLength <= columns;

    /// <summary>Packs the row when that makes it smaller. Returns the previous array, free for the caller to reuse, or null when nothing changed.</summary>
    internal Cell[]? Pack()
    {
        if (_packed is not null) return null;
        var cells = Cells;
        int n = StoredLength(cells);
        if (n == 0 || n > ushort.MaxValue) return null;
        if (PackedCells.Pack(cells.AsSpan(0, n)) is not { } data) return null;
        _packedLength = Wrapped ? cells.Length : Math.Min(CellArrayPool.SizeFor(n), cells.Length); // what Trim would have left
        _packed = data;
        Cells = [];
        CellsPinned = false;
        return cells;
    }

    /// <summary>Restores the cells, as an array the length <see cref="Trim"/> would have left.</summary>
    internal void Unpack()
    {
        if (_packed is not { } packed) return;
        Cells = PackedCells.Unpack(packed, _packedLength);
        _packed = null;
    }

    // ---- Grapheme clusters (RowClusters.cs) -------------------------------------------------------

    /// <summary>
    /// Longest cluster kept, in code points (the longest real ones, family emoji ZWJ sequences, have 11). Further
    /// combining marks are dropped: a stream of them would otherwise make every append copy an ever longer string.
    /// </summary>
    public const int MaxClusterCodePoints = 32;

    /// <summary>True if any cell may carry a cluster (cheap check before scanning flags).</summary>
    public bool HasClusters => _clusters.Count > 0;

    /// <summary>
    /// The full text of a multi-code-point cluster at <paramref name="col"/> (e.g. "é", "👍🏽", "🇳🇱"),
    /// or false for an ordinary single-code-point cell (use <see cref="Cell.Rune"/>).
    /// </summary>
    public bool TryGetCluster(int col, out ReadOnlySpan<char> text)
    {
        if (GetClusterString(col) is { } s)
        {
            text = s;
            return true;
        }
        text = default;
        return false;
    }

    internal string? GetClusterString(int col) =>
        (uint)col < (uint)Cells.Length && (Cells[col].Flags & CellAttributes.Cluster) != 0
            ? _clusters.Get(col) : null;

    /// <summary>Stores a cluster for a cell and flags it.</summary>
    internal void SetCluster(int col, string text)
    {
        Version++;
        _clusters.Store(col, text);
        Cells[col].Flags |= CellAttributes.Cluster;
    }

    /// <summary>
    /// Appends a code point to the cluster at <paramref name="col"/> (starting one from the cell's base), taking the
    /// string from <paramref name="pool"/>. Returns false (and changes nothing) when the cluster already has
    /// <see cref="MaxClusterCodePoints"/> code points.
    /// </summary>
    internal bool AppendToCluster(int col, int cp, ClusterStringPool pool)
    {
        ref var c = ref Cells[col];
        string? current = GetClusterString(col);
        if (current is not null && CountCodePoints(current) >= MaxClusterCodePoints) return false;
        Span<char> buffer = stackalloc char[2 * MaxClusterCodePoints + 2];
        int n;
        if (current is null) n = new System.Text.Rune(c.Rune).EncodeToUtf16(buffer);
        else { current.AsSpan().CopyTo(buffer); n = current.Length; }
        n += new System.Text.Rune(cp).EncodeToUtf16(buffer[n..]);
        SetCluster(col, pool.Get(buffer[..n]));
        return true;
    }

    private static int CountCodePoints(string s)
    {
        int n = 0;
        foreach (char ch in s)
            n += char.IsLowSurrogate(ch) ? 0 : 1;
        return n;
    }

    internal void ClearClusters()
    {
        Version++;
        _clusters.Clear();
    }

    /// <summary>
    /// Moves cluster entries for columns at or after <paramref name="from"/> by <paramref name="delta"/>, matching
    /// an Array.Copy of the cells (insert/delete characters). Entries that move off the row are dropped.
    /// </summary>
    internal void ShiftClusters(int from, int delta)
    {
        if (_clusters.Count == 0) return;
        Version++;
        _clusters.Shift(from, delta, Cells.Length);
    }

    // ---- Underline colors (RowUnderlineColors.cs) ---------------------------------------------------

    /// <summary>True if some cell may carry an underline color (cheap check before <see cref="UnderlineColorAt"/>).</summary>
    public bool HasUnderlineColors => _hasUnderlineColors;

    /// <summary>The SGR 58 color of the underline at <paramref name="col"/> (a <see cref="TermColor"/>; 0 = the text color).</summary>
    public uint UnderlineColorAt(int col) => RowUnderlineColors.At(_underlineColors, _hasUnderlineColors, col);

    /// <summary>
    /// Records the underline color of cells [col, col + count). A no-op for 0 on a row without colors, so printing
    /// with the default underline color costs nothing.
    /// </summary>
    public void SetUnderlineColor(int col, int count, uint color)
    {
        if (count <= 0 || col < 0) return;
        if (!HasUnderlineColors && color == 0) return;
        RowUnderlineColors.Set(ref _underlineColors, ref _hasUnderlineColors, col, count, color, Cells.Length);
        Version++;
    }

    /// <summary>Drops every underline color (the table is kept for reuse).</summary>
    internal void ClearUnderlineColors() => _hasUnderlineColors = false;

    /// <summary>
    /// Moves underline colors with the cells: the same Array.Copy as the cells got (insert/delete characters), from
    /// column <paramref name="from"/> to <paramref name="to"/>, <paramref name="count"/> columns.
    /// </summary>
    internal void CopyUnderlineColors(int from, int to, int count) =>
        RowUnderlineColors.Copy(_underlineColors, _hasUnderlineColors, from, to, count);

    // ---- Hyperlinks (LinkSpans.cs) and shell-integration marks --------------------------------------

    /// <summary>Sentinel for <see cref="ExitCode"/>: no command result recorded for this prompt.</summary>
    public const int NoExitCode = int.MinValue;

    /// <summary>OSC 8 spans, sorted and non-overlapping; null until a link is printed on the row.</summary>
    public List<LinkSpan>? Links { get; set; }

    public SemanticMarks Marks { get; set; }

    /// <summary>Column of OSC 133;B on this row (where the typed command begins), or -1.</summary>
    public int CommandColumn { get; set; } = -1;

    /// <summary>On a prompt row: exit status of the command entered at this prompt (OSC 133;D).</summary>
    public int ExitCode { get; set; } = NoExitCode;

    /// <summary>On a prompt row: the command line the shell reported with OSC 133;C (<c>cmdline_url=</c> / <c>cmdline=</c>), or null.</summary>
    public string? CommandLine { get; set; }

    public bool HasLinks => Links is { Count: > 0 };

    /// <summary>The prompt on this row ran a command that failed.</summary>
    public bool CommandFailed => ExitCode != NoExitCode && ExitCode != 0;

    /// <summary>A side table is allocated, in use or not (tests, diagnostics).</summary>
    internal bool HasSideTables => _clusters.Allocated || _underlineColors is not null || Links is not null;

    /// <summary>
    /// Lets go of side tables with nothing in them. <see cref="Clear"/> keeps them so that a row in use doesn't allocate
    /// again, but a recycled row can reach the scrollback with those of a line it no longer shows: after a flood of
    /// combining marks, one array for each of the scrollback's rows, all empty.
    /// </summary>
    internal void ReleaseEmptySideTables()
    {
        _clusters.ReleaseIfEmpty();
        if (!_hasUnderlineColors) _underlineColors = null;
        if (Links is { Count: 0 }) Links = null;
    }

    private void ClearMetadata()
    {
        Links?.Clear();
        Marks = SemanticMarks.None;
        CommandColumn = -1;
        ExitCode = NoExitCode;
        CommandLine = null;
    }

    /// <summary>Hyperlink id at a column, 0 if none.</summary>
    public int LinkIdAt(int col) => Links is { Count: > 0 } links ? LinkSpans.IdAt(links, col) : 0;

    /// <summary>Marks [start, start + length) as link <paramref name="id"/>; id 0 removes links from the range.</summary>
    public void SetLink(int start, int length, int id)
    {
        if (length <= 0) return;
        Version++;
        var links = Links;
        if (links is null)
        {
            if (id == 0) return;
            links = Links = new List<LinkSpan>(2);
        }
        LinkSpans.Set(links, start, start + length, id);
    }

    /// <summary>Resets links and prompt marks (reflow re-assigns them to the re-wrapped rows).</summary>
    internal void ResetMetadata()
    {
        Version++;
        ClearMetadata();
    }

    /// <summary>Drops links from <paramref name="col"/> to the end of the row (after characters were shifted).</summary>
    public void ClearLinksFrom(int col)
    {
        if (HasLinks) SetLink(col, int.MaxValue / 2, 0);
    }
}
