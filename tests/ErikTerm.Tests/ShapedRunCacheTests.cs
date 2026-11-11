using ErikTerm.App.Rendering;

namespace ErikTerm.Tests;

/// <summary>The cache of shaped runs: lookups compare whole keys, the table is bounded, nothing allocates once warm.</summary>
public class ShapedRunCacheTests
{
    private static ulong[] Key(int face, params int[] codepoints)
    {
        var key = new ulong[codepoints.Length + 1];
        key[0] = (uint)face;
        for (int i = 0; i < codepoints.Length; i++) key[i + 1] = (uint)codepoints[i] | (ulong)(uint)i << 32 | 1UL << 56;
        return key;
    }

    private static ShapedGlyph[] Glyphs(int baseCol, params uint[] glyphs)
    {
        var result = new ShapedGlyph[glyphs.Length];
        for (int i = 0; i < glyphs.Length; i++) result[i] = new ShapedGlyph { Col = baseCol + i, Face = 3, Glyph = glyphs[i], Cells = 1 };
        return result;
    }

    /// <summary>A key different from <paramref name="key"/> whose hash maps to the same table slot.</summary>
    private static ulong[] KeyInSameSlot(ulong[] key)
    {
        int slot = (int)(ShapedRunCache.HashOf(key) >> 40) & (ShapedRunCache.Slots - 1);
        for (int cp = 'A'; ; cp++)
        {
            var other = Key(1, cp);
            if (ShapedRunCache.HashOf(other) != ShapedRunCache.HashOf(key)
                && ((int)(ShapedRunCache.HashOf(other) >> 40) & (ShapedRunCache.Slots - 1)) == slot)
                return other;
        }
    }

    [Test]
    public async Task Stored_run_is_found_with_columns_relative_to_the_run()
    {
        // Arrange
        var cache = new ShapedRunCache();
        ulong[] key = Key(1, 'a', 'b');
        ulong hash = ShapedRunCache.HashOf(key);
        cache.Store(key, hash, Glyphs(10, 5, 6), baseCol: 10);

        // Act
        bool found = cache.TryGet(key, hash, out var glyphs);
        int[] columns = glyphs.ToArray().Select(g => g.Col).ToArray();
        uint[] indexes = glyphs.ToArray().Select(g => g.Glyph).ToArray();

        // Assert
        await Assert.That(found).IsTrue();
        await Assert.That(columns).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
        await Assert.That(indexes).IsEquivalentTo([5u, 6u], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_different_face_or_text_is_not_found()
    {
        // Arrange
        var cache = new ShapedRunCache();
        ulong[] key = Key(1, 'a', 'b');
        cache.Store(key, ShapedRunCache.HashOf(key), Glyphs(0, 5, 6), 0);
        ulong[] otherFace = Key(2, 'a', 'b');
        ulong[] otherText = Key(1, 'a', 'c');

        // Act
        bool face = cache.TryGet(otherFace, ShapedRunCache.HashOf(otherFace), out _);
        bool text = cache.TryGet(otherText, ShapedRunCache.HashOf(otherText), out _);

        // Assert
        await Assert.That(face).IsFalse();
        await Assert.That(text).IsFalse();
    }

    [Test]
    public async Task Equal_hash_with_a_different_key_is_a_miss()
    {
        // Arrange: the stored key claims the probe key's hash, so only the key compare can tell them apart
        var cache = new ShapedRunCache();
        ulong[] key = Key(1, 'a', 'b');
        ulong[] probe = Key(1, 'x', 'y');
        ulong hash = ShapedRunCache.HashOf(probe);
        cache.Store(key, hash, Glyphs(0, 5, 6), 0);

        // Act
        bool found = cache.TryGet(probe, hash, out _);

        // Assert
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task A_new_run_replaces_the_run_in_its_slot()
    {
        // Arrange
        var cache = new ShapedRunCache();
        ulong[] first = Key(1, 'a', 'b');
        ulong[] second = KeyInSameSlot(first);
        cache.Store(first, ShapedRunCache.HashOf(first), Glyphs(0, 5, 6), 0);

        // Act
        cache.Store(second, ShapedRunCache.HashOf(second), Glyphs(0, 9), 0);
        bool firstFound = cache.TryGet(first, ShapedRunCache.HashOf(first), out _);
        bool secondFound = cache.TryGet(second, ShapedRunCache.HashOf(second), out var glyphs);
        int count = glyphs.Length;

        // Assert
        await Assert.That(firstFound).IsFalse();
        await Assert.That(secondFound).IsTrue();
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task Clear_forgets_every_run()
    {
        // Arrange
        var cache = new ShapedRunCache();
        ulong[] key = Key(1, 'a');
        cache.Store(key, ShapedRunCache.HashOf(key), Glyphs(0, 5), 0);

        // Act
        cache.Clear();
        bool found = cache.TryGet(key, ShapedRunCache.HashOf(key), out _);

        // Assert
        await Assert.That(found).IsFalse();
    }

    [Test]
    public async Task Runs_over_the_limits_are_not_kept()
    {
        // Arrange
        var cache = new ShapedRunCache();
        ulong[] longKey = Key(1, new int[ShapedRunCache.MaxKey]);
        ulong[] shortKey = Key(1, 'a');

        // Act
        cache.Store(longKey, ShapedRunCache.HashOf(longKey), Glyphs(0, 5), 0);
        cache.Store(shortKey, ShapedRunCache.HashOf(shortKey), new ShapedGlyph[ShapedRunCache.MaxGlyphs + 1], 0);
        bool longFound = cache.TryGet(longKey, ShapedRunCache.HashOf(longKey), out _);
        bool shortFound = cache.TryGet(shortKey, ShapedRunCache.HashOf(shortKey), out _);

        // Assert
        await Assert.That(longFound).IsFalse();
        await Assert.That(shortFound).IsFalse();
    }

    [Test]
    public async Task Lookups_and_replacing_a_run_allocate_nothing()
    {
        // Arrange: warmed with a run the replacing runs fit in
        var cache = new ShapedRunCache();
        ulong[] key = Key(1, 'a', 'b');
        ulong[] other = KeyInSameSlot(key);
        var glyphs = Glyphs(0, 5, 6);
        ulong keyHash = ShapedRunCache.HashOf(key), otherHash = ShapedRunCache.HashOf(other);
        cache.Store(key, keyHash, glyphs, 0);

        void Churn()
        {
            for (int i = 0; i < 1000; i++)
            {
                cache.TryGet(key, keyHash, out _);
                cache.Store(other, otherHash, glyphs, 0);
                cache.TryGet(other, otherHash, out _);
                cache.Store(key, keyHash, glyphs, 0);
            }
        }
        Churn(); // the runtime may allocate while it compiles this the first time

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        Churn();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        await Assert.That(allocated).IsEqualTo(0L);
    }
}
