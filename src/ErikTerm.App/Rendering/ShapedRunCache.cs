using System.Runtime.CompilerServices;

namespace ErikTerm.App.Rendering;

/// <summary>A glyph placed on the grid: drawn at cell <see cref="Col"/>, offset from the cell's pen position.</summary>
internal struct ShapedGlyph
{
    public int Col;
    public int Face;
    public uint Glyph;   // glyph index, or the codepoint for procedural (box) items
    public float Dx, Dy; // pixel offset from the cell's pen position (Dy up)
    public byte Kind;
    public byte Cells;   // cell span (2 for wide characters)
}

/// <summary>
/// HarfBuzz results by run. A run is the part of a row <see cref="GridText"/> shapes at once: output repeats its words
/// far more often than its rows, so most runs of a new row are already known. The key is the complete input of the
/// shaping call: the face, then for every code point its column relative to the run and whether the cell is wide.
/// The font size and features are outside the key: <see cref="GridText"/> clears the cache when they change.
/// Keys are compared in full, so a hash collision can't produce wrong glyphs. A direct-mapped table: a new run replaces
/// the run in its slot, and only runs up to <see cref="MaxKey"/> elements and <see cref="MaxGlyphs"/> glyphs are kept.
/// Arrays are reused by slot, so a lookup or an insert of a run that fits its slot allocates nothing. At the limit the
/// table holds <see cref="Slots"/> runs: about 2.5 MB for runs of ordinary words, 12 MB if every slot held a run of maximum size.
/// </summary>
internal sealed class ShapedRunCache
{
    public const int Slots = 1 << 12;
    /// <summary>Key length limit: one element per code point, plus the face.</summary>
    public const int MaxKey = 65;
    /// <summary>Glyph limit of a kept run (a run of longer output isn't kept).</summary>
    public const int MaxGlyphs = 96;

    private sealed class Entry
    {
        public ulong Hash;
        public int KeyLength, GlyphCount;
        public ulong[] Key = [];
        public ShapedGlyph[] Glyphs = [];
    }

    private readonly Entry?[] _slots = new Entry?[Slots];

    public long Hits { get; private set; }
    public long Misses { get; private set; }

    /// <summary>Forgets every run. The arrays stay, to be reused.</summary>
    public void Clear()
    {
        foreach (var e in _slots)
        {
            if (e is null) continue;
            e.KeyLength = 0;
            e.GlyphCount = 0;
            e.Hash = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong h, ulong v)
    {
        h = (h ^ v) * 0x9E3779B97F4A7C15UL;
        return h ^ (h >> 31);
    }

    /// <summary>Hash of a key, as passed to <see cref="TryGet"/> and <see cref="Store"/>.</summary>
    internal static ulong HashOf(ReadOnlySpan<ulong> key)
    {
        ulong h = 0xCBF29CE484222325UL ^ (ulong)(uint)key.Length;
        foreach (ulong v in key) h = Mix(h, v);
        return h;
    }

    private static int SlotOf(ulong hash) => (int)(hash >> 40) & (Slots - 1);

    /// <summary>The glyphs stored for <paramref name="key"/>; false when it isn't in the table.</summary>
    public bool TryGet(ReadOnlySpan<ulong> key, ulong hash, out ReadOnlySpan<ShapedGlyph> glyphs)
    {
        var e = _slots[SlotOf(hash)];
        if (e is { KeyLength: > 0 } && e.Hash == hash && key.SequenceEqual(e.Key.AsSpan(0, e.KeyLength)))
        {
            Hits++;
            glyphs = e.Glyphs.AsSpan(0, e.GlyphCount);
            return true;
        }
        Misses++;
        glyphs = default;
        return false;
    }

    /// <summary>
    /// Keeps a run's glyphs (<paramref name="baseCol"/> is subtracted from each column); replaces the run in its slot.
    /// A key or a result over the size limits isn't kept.
    /// </summary>
    public void Store(ReadOnlySpan<ulong> key, ulong hash, ReadOnlySpan<ShapedGlyph> glyphs, int baseCol)
    {
        if (key.Length > MaxKey || glyphs.Length > MaxGlyphs) return;
        ref var slot = ref _slots[SlotOf(hash)];
        var e = slot ??= new Entry();
        if (e.Key.Length < key.Length) e.Key = new ulong[Math.Max(key.Length, 16)];
        if (e.Glyphs.Length < glyphs.Length) e.Glyphs = new ShapedGlyph[Math.Max(glyphs.Length, 16)];
        key.CopyTo(e.Key);
        for (int i = 0; i < glyphs.Length; i++)
        {
            e.Glyphs[i] = glyphs[i];
            e.Glyphs[i].Col -= baseCol;
        }
        e.GlyphCount = glyphs.Length;
        e.KeyLength = key.Length;
        e.Hash = hash;
    }
}
