using ErikTerm.Ui.Compare;

namespace ErikTerm.Tests;

/// <summary>The diff, intraline and alignment layers of compare and merge.</summary>
public class CompareDiffTests
{
    private static int[] Ids(string text) => [.. text.Select(c => (int)c)];

    /// <summary>Applying the hunks must rebuild b from a; a wrong edit script can't be mistaken for a good diff.</summary>
    private static int[] Apply(int[] a, int[] b, List<DiffHunk> hunks)
    {
        var result = new List<int>();
        int i = 0;
        foreach (var h in hunks)
        {
            result.AddRange(a[i..h.AStart]);
            result.AddRange(b[h.BStart..h.BEnd]);
            i = h.AEnd;
        }
        result.AddRange(a[i..]);
        return [.. result];
    }

    [Test]
    public async Task IdenticalSequencesHaveNoHunks()
    {
        // Arrange
        int[] a = Ids("abcdef");

        // Act
        var hunks = SequenceDiff.Diff(a, a);

        // Assert
        await Assert.That(hunks).IsEmpty();
    }

    [Test]
    public async Task InsertionIsOneHunkWithNoRemovedLines()
    {
        // Arrange
        int[] a = Ids("abcd"), b = Ids("abXcd");

        // Act
        var hunks = SequenceDiff.Diff(a, b);

        // Assert
        await Assert.That(hunks).IsEquivalentTo([new DiffHunk(2, 0, 2, 1)]);
    }

    [Test]
    public async Task DeletionIsOneHunkWithNoAddedLines()
    {
        // Arrange
        int[] a = Ids("abcd"), b = Ids("acd");

        // Act
        var hunks = SequenceDiff.Diff(a, b);

        // Assert
        await Assert.That(hunks).IsEquivalentTo([new DiffHunk(1, 1, 1, 0)]);
    }

    [Test]
    public async Task ReplacementCoversChangedLinesOnly()
    {
        // Arrange
        int[] a = Ids("abcde"), b = Ids("abXYe");

        // Act
        var hunks = SequenceDiff.Diff(a, b);

        // Assert
        await Assert.That(hunks).IsEquivalentTo([new DiffHunk(2, 2, 2, 2)]);
    }

    [Test]
    public async Task EmptySideIsAOneHunkDiff()
    {
        // Arrange
        int[] a = Ids(""), b = Ids("xyz");

        // Act
        var hunks = SequenceDiff.Diff(a, b);

        // Assert
        await Assert.That(hunks).IsEquivalentTo([new DiffHunk(0, 0, 0, 3)]);
    }

    [Test]
    public async Task RandomEditsAreReconstructedExactly()
    {
        // Arrange — deterministic pseudo-random sequences over a small alphabet, the worst case for a Myers search
        var random = new Random(1234);
        for (int round = 0; round < 200; round++)
        {
            int[] a = [.. Enumerable.Range(0, random.Next(1, 200)).Select(_ => random.Next(8))];
            int[] b = [.. Enumerable.Range(0, random.Next(1, 200)).Select(_ => random.Next(8))];

            // Act
            var hunks = SequenceDiff.Diff(a, b);

            // Assert
            await Assert.That(Apply(a, b, hunks)).IsEquivalentTo(b, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task HugeMostlySameFileDiffsQuickly()
    {
        // Arrange — 100k lines, one changed in every 200, and the rest shared
        var a = Enumerable.Range(0, 100_000).Select(i => i).ToArray();
        var b = (int[])a.Clone();
        for (int i = 100; i < b.Length; i += 200) b[i] = -1 - i;
        int[] shifted = [.. b.Select(v => v + 200_000)];
        List<DiffHunk> hunks = [];

        // Act — CPU time of this thread, so a busy CI runner doesn't count against it
        var cpu = ThreadCpuTime.Measure(() => hunks = SequenceDiff.Diff(a, shifted));

        // Assert — the bound that matters for "never freeze"
        await Assert.That(hunks.Count).IsGreaterThan(0);
        await Assert.That(cpu.TotalMilliseconds).IsLessThan(5000);
    }

    [Test]
    public async Task AdversarialInputStaysBounded()
    {
        // Arrange — 200k tokens from a 2-letter alphabet: every line matches every other line many times
        var random = new Random(5);
        int[] a = [.. Enumerable.Range(0, 100_000).Select(_ => random.Next(2))];
        int[] b = [.. Enumerable.Range(0, 100_000).Select(_ => random.Next(2))];
        List<DiffHunk> hunks = [];

        // Act — CPU time of this thread, so a busy CI runner doesn't count against it
        var cpu = ThreadCpuTime.Measure(() => hunks = SequenceDiff.Diff(a, b));

        // Assert
        await Assert.That(Apply(a, b, hunks)).IsEquivalentTo(b, CollectionOrdering.Matching);
        await Assert.That(cpu.TotalMilliseconds).IsLessThan(15000);
    }

    [Test]
    public async Task InsertedBlockSlidesToEndOnBlankLine()
    {
        // Arrange — the classic ambiguous insertion of a function after a blank line
        var interner = new LineInterner();
        var a = interner.Ids(["foo", "", "baz"]);
        var b = interner.Ids(["foo", "", "bar", "", "baz"]);

        // Act
        var hunks = SequenceDiff.Diff(a, b, interner);

        // Assert — the block reads as "bar" plus its blank line
        await Assert.That(hunks).IsEquivalentTo([new DiffHunk(2, 0, 2, 2)]);
    }

    [Test]
    public async Task IntralineMarksChangedWordsOnly()
    {
        // Arrange
        const string a = "if (!u) return \"/login\";";
        const string b = "if (!u) return \"/signin\";";

        // Act
        var changes = Intraline.Compute(a, b);

        // Assert
        await Assert.That(a.Substring(changes.Left[0].Start, changes.Left[0].Length)).IsEqualTo("login");
        await Assert.That(b.Substring(changes.Right[0].Start, changes.Right[0].Length)).IsEqualTo("signin");
    }

    [Test]
    public async Task IntralineOfIdenticalLinesIsEmpty()
    {
        // Act
        var changes = Intraline.Compute("same", "same");

        // Assert
        await Assert.That(changes.Left).IsEmpty();
        await Assert.That(changes.Right).IsEmpty();
    }

    [Test]
    public async Task LineAlignerPairsSimilarLinesAndLeavesAddedOnesAsGaps()
    {
        // Arrange — one edited line followed by two added comment lines
        string[] a = ["  items.reduce((a, i) => a + i.price, 0);"];
        string[] b = ["  items.reduce((a, i) => a + i.price * i.qty, 0);", "  // qty defaults to 1", "  // see store.ts"];
        int budget = LineAligner.NewBudget();

        // Act
        var pairs = LineAligner.Align(a, b, ref budget);

        // Assert
        await Assert.That(pairs).IsEquivalentTo([(0, 0), (-1, 1), (-1, 2)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task LineAlignerPrefersTheSimilarLineOverPositional()
    {
        // Arrange — the second line of a is the one that pairs with the only line of b
        string[] a = ["completely unrelated header text", "export const total = (items) => items.reduce((a, i) => a + i.price, 0);"];
        string[] b = ["export const total = (items) => items.reduce((a, i) => a + i.price * 2, 0);"];
        int budget = LineAligner.NewBudget();

        // Act
        var pairs = LineAligner.Align(a, b, ref budget);

        // Assert
        await Assert.That(pairs).IsEquivalentTo([(0, -1), (1, 0)], CollectionOrdering.Matching);
    }
}
