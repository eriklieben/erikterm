namespace ErikTerm.Core;

/// <summary>
/// Free lists of the arrays trimmed scrollback rows use (lengths rounded up to <see cref="Granularity"/>), plus spare
/// full-width rows. A line pushed into a full scrollback evicts the oldest one: its trimmed array comes here and its
/// row gets a full-width one back for the screen; compacting a row does the opposite. With output trickling in (an
/// agent, a shell session) the two balance, so nothing is allocated. Owned by one Terminal; not thread-safe.
/// </summary>
internal sealed class CellArrayPool
{
    /// <summary>Trimmed lengths are multiples of this: fewer distinct sizes reuse better, at ≤ 7 spare cells a row.</summary>
    public const int Granularity = 8;

    private const int MaxClass = 256;   // ≤ 2048 cells; longer trimmed rows aren't pooled
    private const int PerClass = 64;    // arrays kept per size
    private const int SearchUp = 2;     // a miss may take an array up to this many classes larger
    private const int FullKept = 32;    // spare full-width arrays

    private readonly Cell[][]?[] _free = new Cell[][]?[MaxClass + 1];
    private readonly int[] _count = new int[MaxClass + 1];
    private readonly Cell[][] _full = new Cell[FullKept][];
    private int _fullCount;

    /// <summary>Arrays that had to be allocated rather than recycled (tests, diagnostics).</summary>
    public long Misses { get; private set; }

    /// <summary>Array length used for <paramref name="n"/> stored cells.</summary>
    public static int SizeFor(int n) => (n + Granularity - 1) & ~(Granularity - 1);

    /// <summary>
    /// An array of <paramref name="size"/> (a multiple of <see cref="Granularity"/>) or a little larger, but shorter
    /// than <paramref name="below"/>. Contents are stale.
    /// </summary>
    public Cell[] Rent(int size, int below)
    {
        if (size == 0) return [];
        int cls = size / Granularity;
        int last = Math.Min(Math.Min(cls + SearchUp, MaxClass), (below - 1) / Granularity);
        for (int c = cls; c <= last; c++)
        {
            if (_count[c] == 0) continue;
            var list = _free[c]!;
            var a = list[--_count[c]];
            list[_count[c]] = null!;
            return a;
        }
        Misses++;
        return new Cell[size];
    }

    /// <summary>A full-width array for a screen row (stale contents), or a new one.</summary>
    public Cell[] RentFull(int columns)
    {
        while (_fullCount > 0)
        {
            var a = _full[--_fullCount];
            _full[_fullCount] = null!;
            if (a.Length == columns) return a;
        }
        Misses++;
        return new Cell[columns];
    }

    /// <summary>Gives an array back. Full-width ones (<paramref name="columns"/>) are kept apart; others by size class.</summary>
    public void Return(Cell[] a, int columns)
    {
        if (a.Length == 0) return;
        if (a.Length == columns)
        {
            if (_fullCount < FullKept) _full[_fullCount++] = a;
            return;
        }
        if (a.Length % Granularity != 0) return;
        int cls = a.Length / Granularity;
        if (cls > MaxClass || _count[cls] >= PerClass) return;
        var list = _free[cls] ??= new Cell[PerClass][];
        list[_count[cls]++] = a;
    }
}
