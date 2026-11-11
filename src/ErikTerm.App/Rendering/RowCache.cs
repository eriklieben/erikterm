using ErikTerm.App.Native;
using ErikTerm.Core;

namespace ErikTerm.App.Rendering;

/// <summary>
/// A pane's built rows: for each displayed <see cref="Row"/> the quads of its backgrounds and of its glyphs +
/// decorations, relative to the row's top-left corner, with colors resolved (and dimmed). A row is rebuilt only
/// when its <see cref="Row.Version"/> or the cursor column on it changed; everything it depends on beyond the row
/// (fonts, atlas, palette, theme, dimming, width) is the cache's <see cref="Config"/>, and changing it drops all
/// entries. Keyed by Row object, so rows that merely moved (output scrolling) are reused as they are. Entries not
/// displayed in a frame go back to a free pool, so memory stays bounded by the visible rows. Overlays (cursor,
/// selection, search highlights, link hover) are drawn on top and never invalidate rows.
/// </summary>
internal sealed class RowCache
{
    internal sealed class Entry
    {
        public Row Row = null!;
        public int Version, CursorCol, Columns, Frame;
        public ulong Hash;
        public bool HashValid;
        public Sdl.Vertex[] Bg = [], Fg = [];
        public int BgQuads, FgQuads;
        /// <summary>Where the quads were built (top-left of the row); replays move them by the difference.</summary>
        public float BuiltX, BuiltY;
        public bool Valid;
    }

    /// <summary>Everything outside the row that its built quads depend on.</summary>
    internal record struct Config(
        object? Fonts, uint GridPx, int CellWidth, int CellHeight, object? Atlas, int AtlasGeneration,
        object? Palette, int PaletteVersion, object? Theme, float DimH, float DimS, float DimB, int Columns);

    private readonly Dictionary<Row, Entry> _map = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _live = [];
    private readonly Stack<Entry> _free = new();
    private Config _config;
    private int _frame;

    public int Count => _live.Count;

    /// <summary>Starts a frame; drops every entry when the configuration changed.</summary>
    public void Begin(in Config config)
    {
        _frame++;
        if (config == _config) return;
        _config = config;
        Clear();
    }

    public void Clear()
    {
        foreach (var e in _live) { e.Valid = false; e.Row = null!; _free.Push(e); }
        _live.Clear();
        _map.Clear();
    }

    /// <summary>Rows found unchanged by content after a write (full-screen programs repaint identical rows).</summary>
    public long SameContent;

    /// <summary>The entry for a row (marked used this frame); <paramref name="fresh"/> is false when its quads are current.</summary>
    public Entry Get(Row row, int cursorCol, int columns, out bool fresh)
    {
        if (_map.TryGetValue(row, out var e))
        {
            e.Frame = _frame;
            bool sameLayout = e.Valid && e.CursorCol == cursorCol && e.Columns == columns;
            if (sameLayout && e.Version == row.Version) { fresh = false; return e; }
            if (sameLayout)
            {
                // Written since it was built. Rows rewritten in place (full-screen programs repaint) are hashed so an
                // identical repaint is replayed; the hash is only kept from the second write on, so streaming output
                // (new rows every frame) doesn't pay for it.
                ulong hash = ContentHash(row, columns);
                bool same = e.HashValid && hash == e.Hash;
                e.Hash = hash;
                e.HashValid = true;
                e.Version = row.Version;
                if (same) { SameContent++; fresh = false; return e; }
                fresh = true;
                return e;
            }
        }
        else
        {
            e = _free.Count > 0 ? _free.Pop() : new Entry();
            e.Row = row;
            e.Frame = _frame;
            _map[row] = e;
            _live.Add(e);
        }
        fresh = true;
        e.HashValid = false;
        e.Version = row.Version;
        e.CursorCol = cursorCol;
        e.Columns = columns;
        e.Valid = true;
        return e;
    }

    /// <summary>Hash of everything the built quads show: cells (code point, colors, attributes) and clusters.</summary>
    internal static ulong ContentHash(Row row, int columns)
    {
        var cells = row.Cells;
        int n = Math.Min(cells.Length, columns);
        ulong h = 0xCBF29CE484222325UL ^ (ulong)(uint)n;
        for (int x = 0; x < n; x++)
        {
            ref readonly var c = ref cells[x];
            h = Mix(h, (ulong)(uint)c.Rune | (ulong)(ushort)c.Flags << 32);
            h = Mix(h, (ulong)c.Fg << 32 | c.Bg);
            if ((c.Flags & CellAttributes.Cluster) != 0 && row.TryGetCluster(x, out var cluster))
                h = Mix(h, (ulong)(uint)string.GetHashCode(cluster) | (ulong)(uint)cluster.Length << 32);
        }
        return h;
    }

    private static ulong Mix(ulong h, ulong v)
    {
        h = (h ^ v) * 0x9E3779B97F4A7C15UL;
        return h ^ (h >> 31);
    }

    /// <summary>Ends a frame: rows not displayed in it are released.</summary>
    public void End()
    {
        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var e = _live[i];
            if (e.Frame == _frame) continue;
            _map.Remove(e.Row);
            e.Valid = false;
            e.Row = null!;
            _free.Push(e);
            _live[i] = _live[^1];
            _live.RemoveAt(_live.Count - 1);
        }
    }
}
