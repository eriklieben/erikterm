using System.Runtime.CompilerServices;

namespace ErikTerm.Core;

/// <summary>
/// Gives scrollback memory back once output stops (<see cref="Terminal.CompactScrollback"/>): lines enter the
/// scrollback whole, so scrolling stays cheap, and are packed or trimmed here later; rows whose cell arrays are on the
/// pinned heap (made while the scrollback filled, <see cref="Row.CellsPinned"/>) move to ordinary arrays. Nothing
/// visible changes. Also lets go of the spare rows a cleared scrollback keeps for the next flood.
/// </summary>
internal sealed class ScrollbackCompactor(Scrollback scrollback, CellArrayPool cellPool)
{
    /// <summary>A row with pinned cells was made since the last compaction finished (screen rows can hold them too).</summary>
    public bool PinnedScreenRows { get; set; }

    /// <summary>Bytes of cell storage released so far (monotonic).</summary>
    public long CompactedBytes { get; private set; }

    /// <summary>Scrollback rows wait to be compacted. Safe to read without the lock (a hint).</summary>
    public bool NeedsCompaction => scrollback.Uncompacted > 0 || scrollback.HasRowsToRepack;

    /// <summary>A cleared scrollback left rows for the next flood to reuse. Safe to read without the lock (a hint).</summary>
    public bool HasSpareRows => scrollback.SpareRows > 0;

    /// <summary>Lets go of the rows a cleared scrollback left behind, so that a full GC can return their memory.</summary>
    public void ReleaseSpareRows(int cols, Row[] main, Row[] alt)
    {
        CompactedBytes += (long)scrollback.ReleaseSpareRows() * cols * Unsafe.SizeOf<Cell>();
        if (PinnedScreenRows) MoveScreenRowsOffPinnedHeap(main, alt);
    }

    /// <summary>Compacts up to <paramref name="maxRows"/> scrollback rows, oldest first. Returns true while rows remain.</summary>
    public bool Compact(int maxRows, int cols, Row[] main, Row[] alt)
    {
        scrollback.RepackAfterReads();
        for (; maxRows > 0 && scrollback.NextToCompact(out bool repacked) is { } row; maxRows--)
            CompactRow(row, repacked, cols);
        if (scrollback.Uncompacted > 0 || scrollback.HasRowsToRepack) return true;
        if (PinnedScreenRows && !HasSpareRows) MoveScreenRowsOffPinnedHeap(main, alt); // spare rows can still scroll onto the screen
        return false;
    }

    /// <summary>Packs or trims one scrollback row, or at least moves it off the pinned heap.</summary>
    private void CompactRow(Row row, bool repacked, int cols)
    {
        row.ReleaseEmptySideTables();
        if (row.IsPacked) return;
        bool pinned = row.CellsPinned;
        if (row.Pack() is { } packed)
        {
            if (!repacked) CompactedBytes += (long)packed.Length * Unsafe.SizeOf<Cell>(); // a redraw's screenful isn't worth a full GC
            if (!pinned) cellPool.Return(packed, cols);
            return;
        }
        if (row.IsTrimmed) return;
        if (row.Trim(cellPool) is { } freed)
        {
            CompactedBytes += (long)(freed.Length - row.Cells.Length) * Unsafe.SizeOf<Cell>();
            if (!pinned) cellPool.Return(freed, cols);
        }
        else if (pinned)
        {
            CompactedBytes += row.MoveCellsOffPinnedHeap();
        }
    }

    /// <summary>The compaction reached the end: rows that scrolled in while filling the scrollback can leave the pinned heap too.</summary>
    private void MoveScreenRowsOffPinnedHeap(Row[] main, Row[] alt)
    {
        PinnedScreenRows = false;
        foreach (var row in main)
            if (row.CellsPinned) CompactedBytes += row.MoveCellsOffPinnedHeap();
        foreach (var row in alt)
            if (row.CellsPinned) CompactedBytes += row.MoveCellsOffPinnedHeap();
    }
}
