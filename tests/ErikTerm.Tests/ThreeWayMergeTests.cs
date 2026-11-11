using System.Diagnostics;
using ErikTerm.App.Compare;
using ErikTerm.Ui.Compare;
using static ErikTerm.Tests.CompareFixtures;

namespace ErikTerm.Tests;

/// <summary>The merge model: 3-way chunking, decisions, undo and what gets written.</summary>
public class ThreeWayMergeTests
{
    private static List<MergeChunk> Merge(string b, string o, string t, bool baseKnown = true) =>
        ThreeWayMerge.Merge(TextDocument.SplitLines(b), TextDocument.SplitLines(o), TextDocument.SplitLines(t), baseKnown);

    private static string Join(IEnumerable<string> lines) => string.Concat(lines);

    [Test]
    public async Task ChangesOnDifferentLinesMergeWithoutConflict()
    {
        // Arrange
        const string Base = "a\nb\nc\nd\ne\nf\n";
        const string Ours = "A\nb\nc\nd\ne\nf\n";
        const string Theirs = "a\nb\nc\nd\ne\nF\n";

        // Act
        var chunks = Merge(Base, Ours, Theirs);

        // Assert
        await Assert.That(chunks.Any(c => c.Kind == MergeChunkKind.Conflict)).IsFalse();
        await Assert.That(chunks.Any(c => c.Kind == MergeChunkKind.AutoOurs)).IsTrue();
        await Assert.That(chunks.Any(c => c.Kind == MergeChunkKind.AutoTheirs)).IsTrue();
    }

    [Test]
    public async Task IdenticalChangeOnBothSidesIsAutomatic()
    {
        // Arrange
        const string Base = "a\nb\nc\n", Both = "a\nX\nc\n";

        // Act
        var chunks = Merge(Base, Both, Both);

        // Assert
        await Assert.That(chunks.Single(c => c.Kind != MergeChunkKind.Stable).Kind).IsEqualTo(MergeChunkKind.AutoBoth);
    }

    [Test]
    public async Task DifferentChangesToTheSameLineConflict()
    {
        // Arrange
        const string Base = "a\nb\nc\n", Ours = "a\nours\nc\n", Theirs = "a\ntheirs\nc\n";

        // Act
        var chunks = Merge(Base, Ours, Theirs);

        // Assert
        var conflict = chunks.Single(c => c.Kind == MergeChunkKind.Conflict);
        await Assert.That(Join(conflict.Ours)).IsEqualTo("ours\n");
        await Assert.That(Join(conflict.Theirs)).IsEqualTo("theirs\n");
        await Assert.That(Join(conflict.Base)).IsEqualTo("b\n");
    }

    [Test]
    public async Task ChangesOnAdjacentLinesConflictLikeGit()
    {
        // Arrange — no stable line between the two changes
        const string Base = "a\nb\nc\n", Ours = "A\nb\nc\n", Theirs = "a\nB\nc\n";

        // Act
        var chunks = Merge(Base, Ours, Theirs);

        // Assert
        await Assert.That(chunks.Any(c => c.Kind == MergeChunkKind.Conflict)).IsTrue();
    }

    [Test]
    public async Task InsertionsAtTheSamePlaceConflict()
    {
        // Arrange
        const string Base = "a\nc\n", Ours = "a\nours\nc\n", Theirs = "a\ntheirs\nc\n";

        // Act
        var chunks = Merge(Base, Ours, Theirs);

        // Assert
        await Assert.That(chunks.Single(c => c.Kind == MergeChunkKind.Conflict).Base).IsEmpty();
    }

    [Test]
    public async Task DeleteVersusEditOfALineConflicts()
    {
        // Arrange
        const string Base = "a\nb\nc\n", Ours = "a\nc\n", Theirs = "a\nB\nc\n";

        // Act
        var chunks = Merge(Base, Ours, Theirs);

        // Assert
        var conflict = chunks.Single(c => c.Kind == MergeChunkKind.Conflict);
        await Assert.That(conflict.Ours).IsEmpty();
        await Assert.That(Join(conflict.Theirs)).IsEqualTo("B\n");
    }

    [Test]
    public async Task CommonLinesInsideAConflictAreSplitOut()
    {
        // Arrange — both rewrote the block, keeping one line the same
        const string Base = "1\n2\n3\n";
        const string Ours = "x\nsame\ny\n";
        const string Theirs = "p\nsame\nq\n";

        // Act
        var chunks = Merge(Base, Ours, Theirs);

        // Assert
        await Assert.That(chunks.Count(c => c.Kind == MergeChunkKind.Conflict)).IsEqualTo(2);
        await Assert.That(chunks.Any(c => c.Kind == MergeChunkKind.Stable && Join(c.Ours) == "same\n")).IsTrue();
    }

    [Test]
    public async Task AddAddWithoutBaseConflictsOnlyWhereTheyDiffer()
    {
        // Arrange — both sides added the file independently
        const string Ours = "same\nmine\nend\n", Theirs = "same\nyours\nend\n";

        // Act
        var chunks = Merge("", Ours, Theirs, baseKnown: false);

        // Assert
        var conflict = chunks.Single(c => c.Kind == MergeChunkKind.Conflict);
        await Assert.That(Join(conflict.Ours)).IsEqualTo("mine\n");
        await Assert.That(Join(conflict.Theirs)).IsEqualTo("yours\n");
    }

    [Test]
    public async Task DecisionsBuildTheOutput()
    {
        // Arrange
        var file = Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n");
        int conflict = file.Conflicts[0];

        // Act
        file.Decide(conflict, ConflictChoice.Both);

        // Assert
        await Assert.That(file.Serialize(withMarkers: false).Text()).IsEqualTo("a\nours\ntheirs\nc\n");
        await Assert.That(file.UnresolvedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ResetBringsTheConflictBack()
    {
        // Arrange
        var file = Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n");
        int conflict = file.Conflicts[0];
        file.Decide(conflict, ConflictChoice.Ours);

        // Act
        file.Decide(conflict, ConflictChoice.None);

        // Assert
        await Assert.That(file.UnresolvedCount).IsEqualTo(1);
    }

    [Test]
    public async Task UndoStepsBackThroughDecisions()
    {
        // Arrange
        var file = Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n");
        int conflict = file.Conflicts[0];
        file.Decide(conflict, ConflictChoice.Ours);
        file.Decide(conflict, ConflictChoice.Theirs);

        // Act
        file.Undo();

        // Assert
        await Assert.That(file.DecisionOf(conflict).Choice).IsEqualTo(ConflictChoice.Ours);
    }

    [Test]
    public async Task UnresolvedOutputHasAPlaceholderThatIsNeverSaved()
    {
        // Arrange
        var file = Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n");

        // Act
        byte[] bytes = file.Serialize(withMarkers: false)!;

        // Assert
        await Assert.That(file.Output.Count(l => l.Origin == OutputOrigin.Unresolved)).IsEqualTo(1);
        await Assert.That(bytes.Text()).DoesNotContain("unresolved");
    }

    [Test]
    public async Task SavingWithMarkersWritesTheConflictAndBothSides()
    {
        // Arrange
        var file = Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n");

        // Act
        string text = file.Serialize(withMarkers: true).Text();

        // Assert
        await Assert.That(text).IsEqualTo("a\n<<<<<<< main\nours\n||||||| base\nb\n=======\ntheirs\n>>>>>>> feature/x\nc\n");
    }

    [Test]
    public async Task HunkEditBecomesTheDecision()
    {
        // Arrange
        var file = Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n");

        // Act
        bool applied = file.ApplyHunkEdit(file.Conflicts[0], "combined\n");

        // Assert
        await Assert.That(applied).IsTrue();
        await Assert.That(file.Serialize(false).Text()).IsEqualTo("a\ncombined\nc\n");
    }

    [Test]
    public async Task HunkEditThatKeepsMarkersIsRefused()
    {
        // Arrange
        var file = Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n");
        string marked = file.HunkText(file.Conflicts[0]);

        // Act
        bool applied = file.ApplyHunkEdit(file.Conflicts[0], marked + "edited\n");

        // Assert
        await Assert.That(applied).IsFalse();
        await Assert.That(file.UnresolvedCount).IsEqualTo(1);
    }

    [Test]
    public async Task WholeFileEditCountsLeftoverMarkersAsUnresolved()
    {
        // Arrange
        var file = Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n");

        // Act
        file.ApplyWholeEdit("a\n<<<<<<< x\nours\n=======\ntheirs\n>>>>>>> y\nc\n");

        // Assert
        await Assert.That(file.UnresolvedCount).IsEqualTo(1);
    }

    [Test]
    public async Task WholeFileEditWithoutMarkersIsResolved()
    {
        // Arrange
        var file = Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n");

        // Act
        file.ApplyWholeEdit("hand written\n");

        // Assert
        await Assert.That(file.UnresolvedCount).IsEqualTo(0);
        await Assert.That(file.Serialize(false).Text()).IsEqualTo("hand written\n");
    }

    [Test]
    public async Task LineEndingsSurviveARoundTrip()
    {
        // Arrange — a CRLF file with a final unterminated line
        const string Crlf = "one\r\ntwo\r\nlast";
        var doc = TextDocument.FromText(Crlf);
        var file = MergeFile.FromText("f", "a", "b", doc, doc, doc, baseKnown: true);

        // Act
        string saved = file.Serialize(false).Text();

        // Assert
        await Assert.That(saved).IsEqualTo(Crlf);
    }

    [Test]
    public async Task NonUtf8BytesSurviveARoundTrip()
    {
        // Arrange — Latin-1 "café" with a changed line elsewhere
        byte[] latin = [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n', (byte)'x', (byte)'\n'];
        var doc = TextDocument.Decode(latin);

        // Act
        var file = MergeFile.FromText("f", "a", "b", doc, doc, doc, baseKnown: true);
        byte[] saved = file.Serialize(false)!;

        // Assert
        await Assert.That(doc.Codec).IsEqualTo(TextCodec.Latin1);
        await Assert.That(saved).IsEquivalentTo(latin, CollectionOrdering.Matching);
    }

    [Test]
    public async Task BinaryContentIsDetected()
    {
        // Arrange
        byte[] bytes = [1, 2, 0, 3];

        // Act
        var doc = TextDocument.Decode(bytes);

        // Assert
        await Assert.That(doc.Problem).IsEqualTo(DocumentProblem.Binary);
    }

    [Test]
    public async Task Utf8BomIsKept()
    {
        // Arrange
        byte[] withBom = [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\n'];
        var doc = TextDocument.Decode(withBom);
        var file = MergeFile.FromText("f", "a", "b", doc, doc, doc, baseKnown: true);

        // Act
        byte[] saved = file.Serialize(false)!;

        // Assert
        await Assert.That(doc.Codec).IsEqualTo(TextCodec.Utf8Bom);
        await Assert.That(saved).IsEquivalentTo(withBom, CollectionOrdering.Matching);
    }

    // ---- The verdicts must agree with git for the cases that decide whether a merge needs a person. ----------

    [Test]
    [NotInParallel(Isolation.Processes)]
    [Arguments("a\nb\nc\nd\ne\nf\ng\n", "A\nb\nc\nd\ne\nf\ng\n", "a\nb\nc\nd\ne\nf\nG\n", false)]
    [Arguments("a\nb\nc\n", "a\nX\nc\n", "a\nX\nc\n", false)]
    [Arguments("a\nb\nc\n", "a\nO\nc\n", "a\nT\nc\n", true)]
    [Arguments("a\nb\nc\n", "A\nb\nc\n", "a\nB\nc\n", true)]
    [Arguments("a\nc\n", "a\nb\nc\n", "a\nc\nd\n", false)]
    [Arguments("x\ny\nz\n", "x\ny\nz\nappended\n", "prepended\nx\ny\nz\n", false)]
    public async Task ConflictVerdictMatchesGitMergeFile(string b, string o, string t, bool expectedConflict)
    {
        // Arrange
        using var dir = new TempRepo();
        dir.Write("base", b);
        dir.Write("ours", o);
        dir.Write("theirs", t);

        // Act — git's answer, and ours
        string merged = dir.Git("merge-file", "-p", "ours", "base", "theirs");
        bool gitConflict = dir.LastExit > 0;
        var chunks = ThreeWayMerge.Merge(TextDocument.SplitLines(b), TextDocument.SplitLines(o), TextDocument.SplitLines(t));
        bool oursConflict = chunks.Any(c => c.Kind == MergeChunkKind.Conflict);

        // Assert
        await Assert.That(gitConflict).IsEqualTo(expectedConflict);
        await Assert.That(oursConflict).IsEqualTo(gitConflict);
        if (!gitConflict) await Assert.That(CompareFixtures.Merged(b, o, t).Serialize(false).Text()).IsEqualTo(merged);
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task HugeCleanMergeMatchesGitAndIsFast()
    {
        // Arrange — 100k lines, widely separated edits on each side
        var baseLines = Enumerable.Range(0, 100_000).Select(i => $"line {i}\n").ToArray();
        var ours = (string[])baseLines.Clone();
        var theirs = (string[])baseLines.Clone();
        for (int i = 100; i < baseLines.Length; i += 1000) ours[i] = $"ours {i}\n";
        for (int i = 600; i < baseLines.Length; i += 1000) theirs[i] = $"theirs {i}\n";
        using var dir = new TempRepo();
        dir.Write("base", Join(baseLines));
        dir.Write("ours", Join(ours));
        dir.Write("theirs", Join(theirs));
        string gitMerged = dir.Git("merge-file", "-p", "ours", "base", "theirs");
        var stopwatch = Stopwatch.StartNew();

        // Act
        var file = MergeFile.FromText("big", "a", "b", TextDocument.FromText(Join(baseLines)), TextDocument.FromText(Join(ours)), TextDocument.FromText(Join(theirs)), baseKnown: true);
        var saved = file.Serialize(false).Text();
        stopwatch.Stop();

        // Assert
        await Assert.That(file.Conflicts.Count).IsEqualTo(0);
        await Assert.That(saved).IsEqualTo(gitMerged);
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(5000);
    }
}
