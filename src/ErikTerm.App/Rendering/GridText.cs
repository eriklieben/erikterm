using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ErikTerm.App.Native;
using ErikTerm.Core;

namespace ErikTerm.App.Rendering;

/// <summary>
/// Emits the glyphs of grid rows. Each row is split into runs of cells with the same font face and
/// attributes, shaped with HarfBuzz (ligatures / contextual alternates per <c>harfbuzz_features</c>),
/// and the shaped glyphs are mapped back onto the monospace grid: a glyph is drawn at its cluster's
/// cell, so cell positions never move and a ligature simply spans the cells it covers. Box drawing,
/// blocks, braille and powerline separators break runs and are drawn procedurally to fill the cell.
///
/// Grapheme clusters (<see cref="CellAttributes.Cluster"/>: base + combining marks, VS16, skin tones, ZWJ sequences,
/// flags) feed all their code points into the run, so HarfBuzz positions marks on their base and the color
/// emoji font's ligatures turn a sequence into its single glyph, drawn in the cluster's cell(s).
///
/// Shaped rows are cached by a hash of the row's content (plus the cursor column, since the cursor
/// cell is shaped on its own so a ligature under the cursor splits into its characters). The cache is
/// a fixed pool of entries, so redrawing unchanged rows costs a hash and a dictionary lookup and
/// allocates nothing. Below that, HarfBuzz results are cached by run (<see cref="ShapedRunCache"/>):
/// a new row usually repeats runs seen before.
///
/// Usage per row: <see cref="BeginRow"/>, <see cref="SetColor"/> for each cell that shows a glyph, then
/// <see cref="EndRow"/>.
/// </summary>
#pragma warning disable S6640 // measured per-frame path: pointers over HarfBuzz's glyph arrays allocate nothing and skip bounds checks
internal sealed unsafe class GridText
#pragma warning restore S6640
{
    private const uint NoColor = 0xFFFFFFFF;
    /// <summary>Shaped rows kept (PaneRenderer's RowCache replays unchanged rows; this catches rows rebuilt with known content).</summary>
    private const int Capacity = 256;
    private const byte KindBox = 1; // 0: a glyph

    private sealed class Entry
    {
        public ulong Key;
        public ShapedGlyph[] Items = new ShapedGlyph[16];
        public int Count;

        public void Add(in ShapedGlyph item)
        {
            if (Count == Items.Length) Array.Resize(ref Items, Items.Length * 2);
            Items[Count++] = item;
        }
    }

    private readonly Dictionary<ulong, Entry> _cache = new(Capacity);
    private readonly Entry?[] _pool = new Entry?[Capacity];
    private int _poolCount, _hand;

    // Configuration the cache depends on.
    private FontSet? _fonts;
    private uint _px;
    private int _fontGeneration;
    private IReadOnlyList<string>? _featureSource;
    private HarfBuzz.Feature* _features;
    private int _featureCount;
    private bool _shape;
    private nint _buffer;

    // Current row.
    private Row? _row;
    private Cell[] _cells = [];
    private int _n, _cursor;
    private uint[] _colors = new uint[256];

    // Shaping scratch.
    private uint[] _text = new uint[256];
    private int[] _textCol = new int[256];
    private uint[] _direct = new uint[256];
    private ulong[] _runKey = new ulong[257];
    private readonly ShapedRunCache _runs = new();

    /// <summary>Starts a row: <paramref name="cursorCol"/> is the cursor's column on this row, or -1.</summary>
    public void BeginRow(DrawList dl, IReadOnlyList<string> features, Row row, int n, int cursorCol)
    {
        Sync(dl.Fonts, features);
        _row = row;
        _cells = row.Cells;
        _n = n;
        _cursor = cursorCol;
        if (_colors.Length < n) _colors = new uint[Math.Max(n, _colors.Length * 2)];
        _colors.AsSpan(0, n).Fill(NoColor);
    }

    /// <summary>Marks cell <paramref name="x"/> as visible with foreground <paramref name="rgb"/>.</summary>
    public void SetColor(int x, uint rgb)
    {
        if ((uint)x < (uint)_n) _colors[x] = rgb & 0xFFFFFF;
    }

    /// <summary>
    /// Draws the row's glyphs; (<paramref name="ox"/>, <paramref name="py"/>) is the row's top-left corner.
    /// Color (emoji) glyphs are drawn with <paramref name="colorTint"/> (white = untinted; gray dims them).
    /// </summary>
    public void EndRow(DrawList dl, float ox, float py, float alpha, uint colorTint = 0xFFFFFF) =>
        Emit(dl, ox, py, alpha, colorTint, 0, int.MaxValue, NoColor);

    /// <summary>
    /// Draws the glyphs of cells [<paramref name="firstCol"/>, <paramref name="lastCol"/>] of a row in one color
    /// (text under a block cursor, selected text with selection_fg), shaped exactly as the row is, clusters included.
    /// Color (emoji) glyphs are drawn with <paramref name="colorTint"/> when given, else tinted <paramref name="rgb"/> too.
    /// </summary>
    public void DrawCells(DrawList dl, IReadOnlyList<string> features, Row row, int n, int cursorCol, int firstCol, int lastCol,
        float ox, float py, uint rgb, float alpha, uint colorTint = NoColor)
    {
        BeginRow(dl, features, row, n, cursorCol);
        for (int x = Math.Max(0, firstCol); x <= Math.Min(lastCol, n - 1); x++)
        {
            var f = _cells[x].Flags;
            if (_cells[x].Rune > 0x20 && (f & (CellAttributes.WideTrail | CellAttributes.Hidden)) == 0) _colors[x] = 0;
        }
        Emit(dl, ox, py, alpha, colorTint == NoColor ? rgb & 0xFFFFFF : colorTint & 0xFFFFFF, firstCol, lastCol, rgb & 0xFFFFFF);
    }

    private void Emit(DrawList dl, float ox, float py, float alpha, uint colorTint, int firstCol, int lastCol, uint overrideFg)
    {
        var entry = Lookup(dl.Fonts);
        // Not kept for later: a row's cells can be on the pinned heap, where one live array holds on to all the memory below it.
        _row = null;
        _cells = [];
        var fonts = dl.Fonts;
        var atlas = dl.Atlas;
        int cw = fonts.CellWidth, baseline = fonts.Baseline;
        var items = entry.Items;
        for (int i = 0; i < entry.Count; i++)
        {
            ref readonly var it = ref items[i];
            if (it.Col < firstCol || it.Col > lastCol) continue;
            uint fg = _colors[it.Col];
            if (fg == NoColor) continue;
            if (overrideFg != NoColor) fg = overrideFg;
            float cx = ox + it.Col * cw;
            if (it.Kind == KindBox) EmitBox(dl, atlas, (int)it.Glyph, cx, py, fg, alpha);
            else EmitGlyph(dl, atlas, in it, cx, py, baseline, fg, colorTint, alpha);
        }
    }

    private static void EmitBox(DrawList dl, GlyphAtlas atlas, int codepoint, float cx, float py, uint fg, float alpha)
    {
        var b = atlas.GetBox(codepoint);
        if (!b.Empty) dl.Quad(cx, py, cx + b.Width, py + b.Height, b.U0, b.V0, b.U1, b.V1, fg, alpha);
    }

    private static void EmitGlyph(DrawList dl, GlyphAtlas atlas, in ShapedGlyph it, float cx, float py, int baseline, uint fg, uint colorTint, float alpha)
    {
        var g = atlas.GetGlyph(it.Face, it.Glyph, it.Cells);
        if (g.Empty) return;
        float gx = cx + MathF.Round(it.Dx) + g.Left, gy = py + baseline - MathF.Round(it.Dy) - g.Top;
        dl.Quad(gx, gy, gx + g.Width, gy + g.Height, g.U0, g.V0, g.U1, g.V1, g.Color ? colorTint : fg, alpha);
    }

    // ---- Configuration ------------------------------------------------------------------------

    private void Sync(FontSet fonts, IReadOnlyList<string> features)
    {
        if (!ReferenceEquals(features, _featureSource))
        {
            _featureSource = features;
            var errors = new List<string>();
            var parsed = FontFeatures.Parse(features, errors);
            foreach (var e in errors) Console.Error.WriteLine("erikterm: " + e);
            if (_features != null) NativeMemory.Free(_features);
            _features = (HarfBuzz.Feature*)NativeMemory.Alloc((nuint)Math.Max(1, parsed.Length), (nuint)sizeof(HarfBuzz.Feature));
            for (int i = 0; i < parsed.Length; i++)
                _features[i] = new HarfBuzz.Feature { Tag = parsed[i].Tag, Value = parsed[i].Value, Start = 0, End = uint.MaxValue };
            _featureCount = parsed.Length;
            // An empty list turns shaping off entirely (HarfBuzz would otherwise apply its defaults).
            _shape = parsed.Length > 0 && HarfBuzz.Available;
            if (_shape && _buffer == 0) _buffer = HarfBuzz.BufferCreate();
            _cache.Clear();
            _runs.Clear();
        }
        if (!ReferenceEquals(fonts, _fonts) || fonts.GridPx != _px || fonts.Generation != _fontGeneration)
        {
            _fonts = fonts;
            _px = fonts.GridPx;
            _fontGeneration = fonts.Generation; // a fallback font arrived (FallbackResolver): shaped runs may now use it
            _cache.Clear();
            _runs.Clear();
        }
    }

    // ---- Cache --------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong h, ulong v)
    {
        h = (h ^ v) * 0x9E3779B97F4A7C15UL;
        return h ^ (h >> 31);
    }

    private const CellAttributes ShapeFlags = CellAttributes.Bold | CellAttributes.Italic | CellAttributes.Inverse | CellAttributes.WideLead | CellAttributes.WideTrail;

    private ulong RowKey()
    {
        ulong h = Mix(0xCBF29CE484222325UL, (ulong)(uint)_n << 32 | (uint)(_cursor + 1));
        var cells = _cells;
        for (int x = 0; x < _n; x++)
        {
            ref readonly var c = ref cells[x];
            h = Mix(h, (ulong)(uint)c.Rune | (ulong)(ushort)(c.Flags & ShapeFlags) << 32);
            if (c.Rune > 0x20) h = Mix(h, c.Fg); // runs split on color changes
            if ((c.Flags & CellAttributes.Cluster) != 0 && _row!.TryGetCluster(x, out var cluster))
                h = Mix(h, (ulong)(uint)string.GetHashCode(cluster) | (ulong)(uint)cluster.Length << 32);
        }
        return h;
    }

    private Entry Lookup(FontSet fonts)
    {
        ulong key = RowKey();
        if (_cache.TryGetValue(key, out var entry)) return entry;

        if (_poolCount < Capacity) entry = _pool[_poolCount++] = new Entry();
        else
        {
            entry = _pool[_hand]!;
            _hand = (_hand + 1) % Capacity;
            _cache.Remove(entry.Key);
        }
        entry.Key = key;
        entry.Count = 0;
        // Room for a glyph per cell plus marks/ligature parts up front: recycled entries then never grow mid-frame.
        if (entry.Items.Length < _n + 16) entry.Items = new ShapedGlyph[_n + _n / 4 + 16];
        Shape(fonts, entry);
        _cache[key] = entry;
        return entry;
    }

    // ---- Shaping ------------------------------------------------------------------------------

    private static FontStyle StyleOf(CellAttributes f) =>
        (FontStyle)(((f & CellAttributes.Bold) != 0 ? 1 : 0) | ((f & CellAttributes.Italic) != 0 ? 2 : 0));

    private void Shape(FontSet fonts, Entry entry)
    {
        var cells = _cells;
        var row = _row!;
        int n = _n;
        int x = 0;
        while (x < n)
        {
            ref readonly var c = ref cells[x];
            if ((c.Flags & CellAttributes.WideTrail) != 0 || c.Rune <= 0x20) { x++; continue; }
            bool isCluster = IsClusterAt(row, x, c.Flags);
            if (!isCluster && BoxDrawing.Handles(c.Rune))
            {
                entry.Add(new ShapedGlyph { Col = x, Glyph = (uint)c.Rune, Kind = KindBox, Cells = 1 });
                x++;
                continue;
            }
            x = ShapeRunFrom(fonts, entry, row, x, n, in c, isCluster);
        }
    }

    private static bool IsClusterAt(Row row, int x, CellAttributes flags) =>
        (flags & CellAttributes.Cluster) != 0 && row.TryGetCluster(x, out _);

    /// <summary>
    /// Collects and shapes a run from cell <paramref name="x"/>: same face, same attributes, no spaces or procedural
    /// glyphs, and the cursor cell always on its own. Without shaping, a cluster is a run of its own (it still gets
    /// shaped). Returns the cell after the run.
    /// </summary>
    private int ShapeRunFrom(FontSet fonts, Entry entry, Row row, int x, int n, in Cell c, bool isCluster)
    {
        var attrs = c.Flags & RunAttributes;
        uint fg = c.Fg;
        int len = 0;
        int face = AddCell(fonts, row, x, ref len);
        bool alone = x == _cursor || (isCluster && !_shape);
        bool hasCluster = isCluster;
        x++;
        if (!alone) x = ExtendRun(fonts, row, x, n, face, attrs, fg, ref len, ref hasCluster);
        ShapeRun(fonts, entry, face, len, hasCluster);
        return x;
    }

    private const CellAttributes RunAttributes = CellAttributes.Bold | CellAttributes.Italic | CellAttributes.Inverse;

    /// <summary>Adds the cells that continue a run to the run text; returns the first cell that doesn't.</summary>
    private int ExtendRun(FontSet fonts, Row row, int x, int n, int face, CellAttributes attrs, uint fg, ref int len, ref bool hasCluster)
    {
        var cells = _cells;
        while (x < n)
        {
            ref readonly var d = ref cells[x];
            if ((d.Flags & CellAttributes.WideTrail) != 0) { x++; continue; }
            if (d.Rune <= 0x20 || x == _cursor || d.Fg != fg || (d.Flags & RunAttributes) != attrs) break;
            bool cluster = IsClusterAt(row, x, d.Flags);
            if (cluster ? !_shape : BoxDrawing.Handles(d.Rune)) break;
            int mark = len;
            if (AddCell(fonts, row, x, ref len) != face) { len = mark; break; }
            hasCluster |= cluster;
            x++;
        }
        return x;
    }

    /// <summary>Appends a cell's code points (one, or all of its cluster) to the run text; returns the cell's face.</summary>
    private int AddCell(FontSet fonts, Row row, int x, ref int len)
    {
        ref readonly var c = ref _cells[x];
        var style = StyleOf(c.Flags);
        if ((c.Flags & CellAttributes.Cluster) != 0 && row.TryGetCluster(x, out var cluster))
        {
            int face = fonts.ResolveClusterFace(cluster, style);
            int first = len;
            foreach (var rune in cluster.EnumerateRunes())
            {
                EnsureText(len + 1);
                _text[len] = (uint)rune.Value; _textCol[len] = x; _direct[len] = len == first ? fonts.GlyphIndex(face, rune.Value) : 0;
                len++;
            }
            return face;
        }
        int f = fonts.ResolveFace(c.Rune, style, out uint glyph);
        EnsureText(len + 1);
        _text[len] = (uint)c.Rune; _textCol[len] = x; _direct[len] = glyph;
        len++;
        return f;
    }

    private void EnsureText(int n)
    {
        if (_text.Length >= n) return;
        int size = Math.Max(n, _text.Length * 2);
        Array.Resize(ref _text, size);
        Array.Resize(ref _textCol, size);
        Array.Resize(ref _direct, size);
        Array.Resize(ref _runKey, size + 1);
    }

    private void ShapeRun(FontSet fonts, Entry entry, int face, int len, bool hasCluster)
    {
        bool shape = (_shape && len > 1) || (hasCluster && HarfBuzz.Available);
        if (shape && _buffer == 0) _buffer = HarfBuzz.BufferCreate();
        nint font = shape ? fonts.ShapingFont(face) : 0;
        if (font == 0)
        {
            AddUnshaped(entry, face, len);
            return;
        }

        var key = RunKey(face, len);
        ulong hash = ShapedRunCache.HashOf(key);
        int baseCol = _textCol[0];
        if (_runs.TryGet(key, hash, out var known))
        {
            foreach (ref readonly var glyph in known)
            {
                entry.Add(glyph);
                entry.Items[entry.Count - 1].Col += baseCol;
            }
            return;
        }

        HarfBuzz.BufferClearContents(_buffer);
        fixed (uint* text = _text) HarfBuzz.BufferAddCodepoints(_buffer, text, len, 0, len);
        HarfBuzz.BufferSetDirection(_buffer, HarfBuzz.DIRECTION_LTR);
        HarfBuzz.BufferGuessSegmentProperties(_buffer);
        HarfBuzz.Shape(font, _buffer, _features, (uint)_featureCount);

        int first = entry.Count;
        AddShaped(entry, face, len, fonts.IsColorFace(face));
        _runs.Store(key, hash, entry.Items.AsSpan(first, entry.Count - first), baseCol);
    }

    /// <summary>
    /// The run in the scratch arrays as a <see cref="ShapedRunCache"/> key: everything the shaped glyphs depend on
    /// besides the fonts and features. The face first, then per code point its value, its column relative to the run
    /// and the wide flag of its cell (two code points in one cell are a cluster, so clusters are part of the key).
    /// </summary>
    private ReadOnlySpan<ulong> RunKey(int face, int len)
    {
        var key = _runKey.AsSpan(0, len + 1);
        key[0] = (uint)face;
        int baseCol = _textCol[0];
        for (int i = 0; i < len; i++)
            key[i + 1] = _text[i] | (ulong)(uint)(_textCol[i] - baseCol) << 32 | (ulong)SpanAt(_textCol[i]) << 56;
        return key;
    }

    /// <summary>Unshaped: one glyph per cell (a cluster shows its base).</summary>
    private void AddUnshaped(Entry entry, int face, int len)
    {
        int prevCol = -1;
        for (int i = 0; i < len; i++)
        {
            if (_textCol[i] == prevCol) continue;
            prevCol = _textCol[i];
            entry.Add(new ShapedGlyph { Col = _textCol[i], Face = face, Glyph = _direct[i], Cells = SpanAt(_textCol[i]) });
        }
    }

    /// <summary>
    /// The shaped glyphs of the buffer. Color (bitmap emoji) faces: one glyph per cell, centred by the atlas; their
    /// advances are in strike pixels.
    /// </summary>
    private void AddShaped(Entry entry, int face, int len, bool color)
    {
        uint count, posCount;
        var infos = HarfBuzz.BufferGetGlyphInfos(_buffer, &count);
        var pos = HarfBuzz.BufferGetGlyphPositions(_buffer, &posCount);
        int prevCell = -1;
        int pen = 0, prevAdvance = 0;
        for (uint i = 0; i < count; i++)
        {
            uint cluster = infos[i].Cluster;
            if (cluster >= (uint)len) continue;
            int col = _textCol[cluster];
            // Glyphs of one cell follow each other from the cell's pen position (a combining mark's offset is
            // relative to the pen after its base); a new cell starts at its own position.
            bool sameCell = col == prevCell;
            if (color && sameCell) continue;
            pen = sameCell ? pen + prevAdvance : 0;
            prevCell = col;
            var p = i < posCount ? pos[i] : default;
            prevAdvance = p.XAdvance;
            entry.Add(new ShapedGlyph
            {
                Col = col,
                Face = face,
                Glyph = infos[i].Codepoint,
                Dx = color ? 0 : (pen + p.XOffset) / 64f,
                Dy = color ? 0 : p.YOffset / 64f,
                Cells = SpanAt(col),
            });
        }
    }

    private byte SpanAt(int col) => (byte)((_cells[col].Flags & CellAttributes.WideLead) != 0 ? 2 : 1);
}
