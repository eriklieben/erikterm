using System.Security.Cryptography;
using System.Text;
using ErikTerm.App;
using ErikTerm.App.Compare;
using ErikTerm.Config;
using ErikTerm.Ui;
using ErikTerm.Ui.Compare;
using static ErikTerm.Tests.CompareFixtures;

namespace ErikTerm.Tests;

/// <summary><c>erikterm review</c>: parsing git's output, the review rules, the screen behind a fake source, and real git in temp repositories.</summary>
public class ReviewParsingTests
{
    [Test]
    public async Task NameStatusReadsModifiedAddedDeleted()
    {
        // Arrange
        const string output = "M\0src/a.ts\0A\0new.txt\0D\0gone.txt\0";

        // Act
        var files = ReviewParser.NameStatus(output);

        // Assert
        await Assert.That(files.Select(f => f.Change)).IsEquivalentTo([ReviewChange.Modified, ReviewChange.Added, ReviewChange.Deleted], CollectionOrdering.Matching);
    }

    [Test]
    public async Task NameStatusReadsARenameWithBothPaths()
    {
        // Arrange
        const string output = "R100\0old name.txt\0new name.txt\0";

        // Act
        var file = ReviewParser.NameStatus(output).Single();

        // Assert
        await Assert.That(file.Change).IsEqualTo(ReviewChange.Renamed);
        await Assert.That(file.OldPath).IsEqualTo("old name.txt");
        await Assert.That(file.Path).IsEqualTo("new name.txt");
    }

    [Test]
    public async Task NameStatusKeepsPathsWithSpacesAndNewlines()
    {
        // Arrange
        const string output = "M\0a b\nc.txt\0";

        // Act
        var file = ReviewParser.NameStatus(output).Single();

        // Assert
        await Assert.That(file.Path).IsEqualTo("a b\nc.txt");
    }

    [Test]
    public async Task NameStatusLetsAnUnmergedEntryWinOverTheSecondEntryForThatPath()
    {
        // Arrange
        const string output = "U\0a.txt\0M\0a.txt\0";

        // Act
        var files = ReviewParser.NameStatus(output);

        // Assert
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0].Change).IsEqualTo(ReviewChange.Unmerged);
    }

    [Test]
    public async Task NumstatReadsCountsByPath()
    {
        // Arrange
        const string output = "12\t3\tsrc/a.ts\0";

        // Act
        var counts = ReviewParser.Numstat(output);

        // Assert
        await Assert.That(counts["src/a.ts"]).IsEqualTo((12, 3));
    }

    [Test]
    public async Task NumstatMarksBinaryFilesWithMinusOne()
    {
        // Arrange
        const string output = "-\t-\tpic.png\0";

        // Act
        var counts = ReviewParser.Numstat(output);

        // Assert
        await Assert.That(counts["pic.png"]).IsEqualTo((-1, -1));
    }

    [Test]
    public async Task NumstatReadsARenameByItsNewPath()
    {
        // Arrange
        const string output = "1\t2\t\0old.txt\0new.txt\0";

        // Act
        var counts = ReviewParser.Numstat(output);

        // Assert
        await Assert.That(counts["new.txt"]).IsEqualTo((1, 2));
    }

    [Test]
    public async Task WithCountsFlagsBinaryFiles()
    {
        // Arrange
        var files = new[] { new ReviewFile("pic.png", null, ReviewChange.Added) };
        var counts = new Dictionary<string, (int, int)> { ["pic.png"] = (-1, -1) };

        // Act
        var result = ReviewParser.WithCounts(files, counts).Single();

        // Assert
        await Assert.That(result.Binary).IsTrue();
    }

    [Test]
    public async Task CommitsReadsParentAndSubject()
    {
        // Arrange
        const string output = "aaa\0bbb ccc\0a1\0Ann\02 days ago\0Fix it\0";

        // Act
        var commit = ReviewParser.Commits(output).Single();

        // Assert
        await Assert.That(commit.Parent).IsEqualTo("bbb");
        await Assert.That(commit.Subject).IsEqualTo("Fix it");
    }

    [Test]
    public async Task CommitsGivesARootCommitNoParent()
    {
        // Arrange
        const string output = "aaa\0\0a1\0Ann\0now\0First\0";

        // Act
        var commit = ReviewParser.Commits(output).Single();

        // Assert
        await Assert.That(commit.Parent).IsNull();
    }
}

public class ReviewRulesTests
{
    private static ReviewFile F(string path, ReviewChange change = ReviewChange.Modified) => new(path, null, change);

    [Test]
    public async Task ChooseBaseUsesAnExplicitRequest()
    {
        // Act
        string? chosen = ReviewRules.ChooseBase("develop", "origin/main", _ => true);

        // Assert
        await Assert.That(chosen).IsEqualTo("develop");
    }

    [Test]
    public async Task ChooseBasePrefersTheLocalBranchOfTheDefault()
    {
        // Act
        string? chosen = ReviewRules.ChooseBase(null, "origin/main", name => name == "main");

        // Assert
        await Assert.That(chosen).IsEqualTo("main");
    }

    [Test]
    public async Task ChooseBaseFallsBackToTheRemoteTrackingRef()
    {
        // Act
        string? chosen = ReviewRules.ChooseBase(null, "origin/main", name => name == "origin/main");

        // Assert
        await Assert.That(chosen).IsEqualTo("origin/main");
    }

    [Test]
    public async Task ChooseBaseKeepsSlashesInALocalDefaultName()
    {
        // Act
        string? chosen = ReviewRules.ChooseBase(null, "origin/release/1.0", name => name == "release/1.0");

        // Assert
        await Assert.That(chosen).IsEqualTo("release/1.0");
    }

    [Test]
    public async Task ChooseBaseGivesNothingWithoutADefaultBranch()
    {
        // Act
        string? chosen = ReviewRules.ChooseBase(null, "HEAD", _ => false);

        // Assert
        await Assert.That(chosen).IsNull();
    }

    [Test]
    public async Task RemoteBaseOfALocalBranchIsOrigin()
    {
        // Act
        string? remote = ReviewRules.RemoteBaseOf("main", name => name == "origin/main");

        // Assert
        await Assert.That(remote).IsEqualTo("origin/main");
    }

    [Test]
    public async Task RemoteBaseOfARemoteRefIsNone()
    {
        // Act
        string? remote = ReviewRules.RemoteBaseOf("origin/main", _ => true);

        // Assert
        await Assert.That(remote).IsNull();
    }

    [Test]
    public async Task BehindNoteCountsCommits()
    {
        // Act
        string? note = ReviewRules.BehindNote("main", 3);

        // Assert
        await Assert.That(note).IsEqualTo("main is 3 commits behind origin");
    }

    [Test]
    public async Task BehindNoteSingularForOne()
    {
        // Act
        string? note = ReviewRules.BehindNote("main", 1);

        // Assert
        await Assert.That(note).IsEqualTo("main is 1 commit behind origin");
    }

    [Test]
    public async Task BehindNoteIsEmptyWhenUpToDate()
    {
        // Act
        string? note = ReviewRules.BehindNote("main", 0);

        // Assert
        await Assert.That(note).IsNull();
    }

    [Test]
    public async Task MergeUncommittedMarksChangedTrackedFiles()
    {
        // Arrange
        var uncommitted = new HashSet<string>(StringComparer.Ordinal) { "a.txt" };

        // Act
        var merged = ReviewRules.MergeUncommitted([F("a.txt"), F("b.txt")], [], uncommitted);

        // Assert
        await Assert.That(merged.Single(f => f.Path == "a.txt").Uncommitted).IsTrue();
        await Assert.That(merged.Single(f => f.Path == "b.txt").Uncommitted).IsFalse();
    }

    [Test]
    public async Task MergeUncommittedAddsUntrackedFiles()
    {
        // Act
        var merged = ReviewRules.MergeUncommitted([F("a.txt")], [F("new.txt", ReviewChange.Added)], new HashSet<string>(StringComparer.Ordinal));

        // Assert
        var added = merged.Single(f => f.Path == "new.txt");
        await Assert.That(added.Change).IsEqualTo(ReviewChange.Added);
        await Assert.That(added.Uncommitted).IsTrue();
    }

    [Test]
    public async Task MergeUncommittedTreatsAnUntrackedFileOverADeletionAsModified()
    {
        // Act
        var merged = ReviewRules.MergeUncommitted([F("kept.txt", ReviewChange.Deleted)], [F("kept.txt", ReviewChange.Added)], new HashSet<string>(StringComparer.Ordinal));

        // Assert
        await Assert.That(merged.Single().Change).IsEqualTo(ReviewChange.Modified);
    }

    [Test]
    public async Task EmptyMessageOnTheBaseSaysThereIsNothingToCompare()
    {
        // Arrange
        var snapshot = new ReviewSnapshot { State = ReviewState.Ok, OnBase = true, Base = "main", Branch = "main" };

        // Act
        string message = ReviewRules.EmptyMessage(snapshot);

        // Assert
        await Assert.That(message).Contains("nothing to compare");
    }
}

public class ReviewFileListTests
{
    private static ReviewFile F(string path) => new(path, null, ReviewChange.Modified);

    [Test]
    public async Task GroupsFilesUnderFoldersWithRootFilesLast()
    {
        // Act
        var list = new ReviewFileList([F("README.md"), F("src/b.ts"), F("src/a.ts"), F("tests/x.ts")]);

        // Assert
        await Assert.That(list.Rows.Select(r => r.Text)).IsEquivalentTo(["src/", "a.ts", "b.ts", "tests/", "x.ts", "README.md"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task StepStopsAtTheEnd()
    {
        // Arrange
        var list = new ReviewFileList([F("a"), F("b")]);

        // Act
        int next = list.Step(1, +1);

        // Assert
        await Assert.That(next).IsEqualTo(-1);
    }

    [Test]
    public async Task IndexOfPathFindsFilesInDisplayOrder()
    {
        // Arrange
        var list = new ReviewFileList([F("z.ts"), F("src/a.ts")]);

        // Act
        int index = list.IndexOfPath("z.ts");

        // Assert
        await Assert.That(index).IsEqualTo(1);
    }

    [Test]
    public async Task WideLayoutAtNormalWidths()
    {
        // Act
        var layout = ReviewLayout.Compute(140, 40, hasNote: false);

        // Assert
        await Assert.That(layout.Mode).IsEqualTo(ReviewLayoutMode.Wide);
    }

    [Test]
    public async Task StackedLayoutWhenNarrow()
    {
        // Act
        var layout = ReviewLayout.Compute(80, 40, hasNote: false);

        // Assert
        await Assert.That(layout.Mode).IsEqualTo(ReviewLayoutMode.Stacked);
    }

    [Test]
    public async Task CompactLayoutWhenSmall()
    {
        // Act
        var layout = ReviewLayout.Compute(70, 12, hasNote: false);

        // Assert
        await Assert.That(layout.Mode).IsEqualTo(ReviewLayoutMode.Compact);
    }

    [Test]
    public async Task TabReuseFindsTheSameWorktree()
    {
        // Act
        int? pane = ReviewTabs.Find([new ReviewPaneRef(4, "/repo/a"), new ReviewPaneRef(7, "/repo/b")], "/repo/b/");

        // Assert
        await Assert.That(pane).IsEqualTo(7);
    }

    [Test]
    public async Task TabReuseOpensANewTabForAnotherWorktree()
    {
        // Act
        int? pane = ReviewTabs.Find([new ReviewPaneRef(4, "/repo/a")], "/repo/b");

        // Assert
        await Assert.That(pane).IsNull();
    }

    [Test]
    public async Task TabReuseIgnoresAFinishedReview()
    {
        // Act
        int? pane = ReviewTabs.Find([new ReviewPaneRef(4, "")], "");

        // Assert
        await Assert.That(pane).IsNull();
    }
}

public class ReviewScreenTests
{
    private sealed class InlineBackground : IBackground
    {
        public void Run<T>(Func<T> work, Action<T> done) => done(work());
    }

    private sealed class FakeSource : IReviewSource
    {
        public ReviewSnapshot Snapshot { get; set; } = NewSnapshot();
        public int Loads, Fetches, Finishes;
        public string? LastBase;
        public string? FinishProblem;
        public ConflictPrediction Prediction { get; set; } = new(true, true, null, [], null);

        public ReviewSnapshot Load(string? baseOverride)
        {
            Loads++;
            LastBase = baseOverride;
            return Snapshot;
        }

        public (CompareSide Left, CompareSide Right) Sides(ReviewSnapshot snapshot, ReviewFile file, ReviewScope scope)
        {
            if (file.Binary) return (new CompareSide(file.Path, "main", TextDocument.Decode("\0\0"u8)), new CompareSide(file.Path, "x", TextDocument.Decode("\0\0\0"u8)));
            const string Left = "1\n2\n3\n4\n5\n6\n7\n8\n9\n", Right = "1\nb\n3\n4\n5\n6\n7\nh\n9\n";
            return (new CompareSide(file.Path, "main", TextDocument.FromText(Left)), new CompareSide(file.Path, "branch", TextDocument.FromText(Right)));
        }

        public IReadOnlyList<ReviewFile> CommitFiles(ReviewSnapshot snapshot, ReviewCommit commit) =>
            [new ReviewFile("only-in-commit.ts", null, ReviewChange.Modified)];

        public ConflictPrediction Predict(ReviewSnapshot snapshot) => Prediction;
        public IReadOnlyList<string> Branches(ReviewSnapshot snapshot) => ["develop", "main"];

        public string? Fetch(ReviewSnapshot snapshot)
        {
            Fetches++;
            return null;
        }

        public string? RequestFinish(ReviewSnapshot snapshot)
        {
            Finishes++;
            return FinishProblem;
        }
    }

    private static ReviewFile F(string path, bool uncommitted = false) => new(path, null, ReviewChange.Modified, 2, 2, Uncommitted: uncommitted);

    private static ReviewSnapshot NewSnapshot() => new()
    {
        State = ReviewState.Ok,
        Root = "/repo",
        Branch = "feature",
        HeadOid = "1111111",
        Base = "main",
        BaseOid = "2222222",
        MergeBase = "3333333",
        Committed = [F("a.ts"), F("b.ts")],
        WithUncommitted = [F("a.ts"), F("b.ts"), F("c.ts", uncommitted: true)],
        UncommittedCount = 1,
        Commits = [new ReviewCommit("deadbeef", "cafe", "deadbee", "Ann", "now", "Fix the bug")],
    };

    private static (ReviewScreen Screen, FakeSource Source) Start(ReviewSnapshot? snapshot = null)
    {
        var source = new FakeSource();
        if (snapshot is not null) source.Snapshot = snapshot;
        var screen = new ReviewScreen(new InlineBackground(), source);
        screen.Start();
        return (screen, source);
    }

    private static void Press(ReviewScreen screen, char c) => screen.OnKey(KeyEvent.OfChar(c));

    [Test]
    public async Task OpensOnTheFirstFile()
    {
        // Act
        var (screen, _) = Start();

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("a.ts");
    }

    [Test]
    public async Task ShowsUncommittedFilesByDefault()
    {
        // Act
        var (screen, _) = Start();

        // Assert
        await Assert.That(screen.Files.Select(f => f.Path)).Contains("c.ts");
    }

    [Test]
    public async Task UncommittedToggleHidesWorkTreeFiles()
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        Press(screen, 'u');

        // Assert
        await Assert.That(screen.Files.Select(f => f.Path)).DoesNotContain("c.ts");
    }

    [Test]
    public async Task HeaderSaysUncommittedIsOn()
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        var text = Render(screen, 160, 40).AllText();

        // Assert
        await Assert.That(text).Contains("uncommitted on");
    }

    [Test]
    public async Task NextChangeStaysInTheFileWhileItHasMore()
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        Press(screen, ']');

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("a.ts");
    }

    [Test]
    public async Task NextChangePastTheLastGoesToTheNextFile()
    {
        // Arrange
        var (screen, _) = Start();
        Press(screen, ']');

        // Act
        Press(screen, ']');

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("b.ts");
    }

    [Test]
    public async Task PreviousChangeBeforeTheFirstGoesToThePreviousFile()
    {
        // Arrange
        var (screen, _) = Start();
        Press(screen, 'J');

        // Act
        Press(screen, '[');

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("a.ts");
    }

    [Test]
    public async Task NextChangeAtTheVeryEndWrapsAndSaysSo()
    {
        // Arrange
        var (screen, _) = Start();
        Press(screen, 'G'); // the last row: nothing after it in this file
        Press(screen, 'J');
        Press(screen, 'J');
        Press(screen, 'G');

        // Act
        Press(screen, ']');

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("a.ts");
        await Assert.That(screen.Status).Contains("first file");
    }

    [Test]
    public async Task ListFocusMovesTheSelectionWithJ()
    {
        // Arrange
        var (screen, _) = Start();
        screen.OnKey(new KeyEvent(TuiKey.Tab));

        // Act
        Press(screen, 'j');

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("b.ts");
    }

    [Test]
    public async Task ClickingAFileSelectsIt()
    {
        // Arrange
        var (screen, _) = Start();
        var frame = Render(screen, 160, 40);
        var hit = frame.Hits.First(h => h.Action == ComparePane.FirstHostAction && h.Arg == 1);

        // Act
        screen.OnMouse(new MouseEvent(MouseKind.Press, hit.X + 1, hit.Y), frame);

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("b.ts");
    }

    [Test]
    public async Task CommitsKeyOpensTheListWithoutChangingFiles()
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        Press(screen, 'c');

        // Assert
        await Assert.That(screen.CommitsOpen).IsTrue();
        await Assert.That(screen.Files.Select(f => f.Path)).Contains("a.ts");
    }

    [Test]
    public async Task ChoosingACommitShowsJustIt()
    {
        // Arrange
        var (screen, _) = Start();
        Press(screen, 'c');
        Press(screen, 'j');

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(screen.Commit!.ShortSha).IsEqualTo("deadbee");
        await Assert.That(screen.Files.Single().Path).IsEqualTo("only-in-commit.ts");
    }

    [Test]
    public async Task EscFromACommitReturnsToTheWholeRange()
    {
        // Arrange
        var (screen, _) = Start();
        Press(screen, 'c');
        Press(screen, 'j');
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Esc));

        // Assert
        await Assert.That(screen.Commit).IsNull();
        await Assert.That(screen.Done).IsFalse();
        await Assert.That(screen.Files.Select(f => f.Path)).Contains("a.ts");
    }

    [Test]
    public async Task EscWithNothingOpenCloses()
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Esc));

        // Assert
        await Assert.That(screen.Done).IsTrue();
    }

    [Test]
    public async Task BehindNoteOffersFetch()
    {
        // Arrange
        var (screen, _) = Start(NewSnapshot() with { BehindCount = 3, HasOrigin = true, RemoteBase = "origin/main" });

        // Act
        var text = Render(screen, 160, 40).AllText();

        // Assert
        await Assert.That(text).Contains("main is 3 commits behind origin");
        await Assert.That(text).Contains("F fetch");
    }

    [Test]
    public async Task NeverFetchesByItself()
    {
        // Arrange
        var (screen, source) = Start(NewSnapshot() with { BehindCount = 3, HasOrigin = true });

        // Act
        Render(screen, 160, 40);
        screen.OnFocus(true);

        // Assert
        await Assert.That(source.Fetches).IsEqualTo(0);
    }

    [Test]
    public async Task FetchKeyFetches()
    {
        // Arrange
        var (screen, source) = Start(NewSnapshot() with { BehindCount = 3, HasOrigin = true });

        // Act
        Press(screen, 'F');

        // Assert
        await Assert.That(source.Fetches).IsEqualTo(1);
    }

    [Test]
    public async Task FetchWithoutOriginSaysSoAndDoesNothing()
    {
        // Arrange
        var (screen, source) = Start();

        // Act
        Press(screen, 'F');

        // Assert
        await Assert.That(source.Fetches).IsEqualTo(0);
        await Assert.That(screen.Status).Contains("no origin");
    }

    [Test]
    public async Task PredictedConflictsShowInTheHeader()
    {
        // Arrange
        var source = new FakeSource { Prediction = new ConflictPrediction(true, false, "tree", ["a.ts", "b.ts"], null) };
        var screen = new ReviewScreen(new InlineBackground(), source);
        screen.Start();

        // Act
        var text = Render(screen, 160, 40).AllText();

        // Assert
        await Assert.That(text).Contains("2 predicted conflicts");
    }

    [Test]
    public async Task FinishKeyAsksTheTerminalOnALinkedWorktree()
    {
        // Arrange
        var (screen, source) = Start(NewSnapshot() with { LinkedWorktree = true });

        // Act
        Press(screen, 'f');

        // Assert
        await Assert.That(source.Finishes).IsEqualTo(1);
    }

    [Test]
    public async Task FinishKeyInTheMainWorktreeSaysWhyNot()
    {
        // Arrange
        var (screen, source) = Start();

        // Act
        Press(screen, 'f');

        // Assert
        await Assert.That(source.Finishes).IsEqualTo(0);
        await Assert.That(screen.Status).Contains("linked worktree");
    }

    [Test]
    public async Task FinishProblemIsShown()
    {
        // Arrange
        var (screen, source) = Start(NewSnapshot() with { LinkedWorktree = true });
        source.FinishProblem = "Finish needs an erikterm pane";

        // Act
        Press(screen, 'f');

        // Assert
        await Assert.That(screen.Status).Contains("erikterm pane");
    }

    [Test]
    public async Task OnTheBaseWithNothingSaysSoAndOffersABase()
    {
        // Arrange
        var (screen, _) = Start(NewSnapshot() with { OnBase = true, Branch = "main", Committed = [], WithUncommitted = [], Commits = [] });

        // Act
        var text = Render(screen, 160, 40).AllText();

        // Assert
        await Assert.That(text).Contains("nothing to compare");
        await Assert.That(text).Contains("compare with another branch");
    }

    [Test]
    public async Task BasePickerReloadsAgainstTheChosenBranch()
    {
        // Arrange
        var (screen, source) = Start();
        Press(screen, 'b');
        Press(screen, 'd');

        // Act
        screen.OnKey(new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(source.LastBase).IsEqualTo("develop");
    }

    [Test]
    public async Task BinaryFileShowsAMessageNotAnError()
    {
        // Arrange
        var snapshot = NewSnapshot() with { Committed = [], WithUncommitted = [new ReviewFile("pic.png", null, ReviewChange.Modified, Binary: true)] };
        var (screen, _) = Start(snapshot);

        // Act
        var text = Render(screen, 160, 40).AllText();

        // Assert
        await Assert.That(text).Contains("Binary files");
    }

    [Test]
    public async Task GainingFocusReloads()
    {
        // Arrange
        var (screen, source) = Start();
        await Task.Delay(1600); // past the minimum refresh interval

        // Act
        screen.OnFocus(true);

        // Assert
        await Assert.That(source.Loads).IsEqualTo(2);
    }

    [Test]
    public async Task ReloadingKeepsTheSelectedFile()
    {
        // Arrange
        var (screen, _) = Start();
        Press(screen, 'J');

        // Act
        Press(screen, 'r');

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("b.ts");
    }

    [Test]
    public async Task NarrowWidthStacksTheListAboveTheDiff()
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        var frame = Render(screen, 80, 40);

        // Assert
        await Assert.That(frame.AllText()).Contains("3 FILES");
    }
}

public class ReviewIntegrationTests
{
    private sealed class InlineBackground : IBackground
    {
        public void Run<T>(Func<T> work, Action<T> done) => done(work());
    }

    private static TempRepo Repo(Action<TempRepo> baseFiles)
    {
        var repo = new TempRepo();
        baseFiles(repo);
        repo.CommitAll("base");
        return repo;
    }

    private static void OnFeature(TempRepo repo, Action<TempRepo> change, string message = "feature")
    {
        repo.Git("switch", "-q", "-c", "feature");
        change(repo);
        repo.CommitAll(message);
    }

    /// <summary>A hash over the state git would change: index and every object file.</summary>
    private static string RepoFingerprint(string gitDir)
    {
        var sb = new StringBuilder();
        foreach (string path in Directory.EnumerateFiles(gitDir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (path.EndsWith(".lock", StringComparison.Ordinal)) continue;
            sb.Append(path).Append(':').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Append('\n');
        }
        return sb.ToString();
    }

    [Test]
    public async Task MergeBaseDiffCountsOnlyTheBranchsChanges()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "one\n"));
        OnFeature(repo, r => r.Write("b.txt", "feature\n"));
        repo.Git("switch", "-q", "main");
        repo.Write("main-only.txt", "m\n");
        repo.CommitAll("on main");
        repo.Git("switch", "-q", "feature");

        // Act
        var snapshot = new GitReviewSource(repo.Dir).Load(null);

        // Assert
        await Assert.That(snapshot.State).IsEqualTo(ReviewState.Ok);
        await Assert.That(snapshot.Committed.Select(f => f.Path)).IsEquivalentTo(["b.txt"]);
    }

    [Test]
    public async Task DetectsARename()
    {
        // Arrange
        using var repo = Repo(r => r.Write("old.txt", string.Concat(Enumerable.Range(0, 50).Select(i => $"line {i}\n"))));
        OnFeature(repo, r =>
        {
            File.Move(Path.Combine(r.Dir, "old.txt"), Path.Combine(r.Dir, "new.txt"));
            r.Git("add", "-A");
        });

        // Act
        var file = new GitReviewSource(repo.Dir).Load(null).Committed.Single();

        // Assert
        await Assert.That(file.Change).IsEqualTo(ReviewChange.Renamed);
        await Assert.That(file.OldPath).IsEqualTo("old.txt");
    }

    [Test]
    public async Task UncommittedIncludesStagedUnstagedAndUntracked()
    {
        // Arrange
        using var repo = Repo(r => { r.Write("a.txt", "1\n"); r.Write("b.txt", "1\n"); });
        repo.Git("switch", "-q", "-c", "feature");
        repo.Write("a.txt", "staged\n");
        repo.Git("add", "a.txt");
        repo.Write("b.txt", "unstaged\n");
        repo.Write("new file.txt", "untracked\n");

        // Act
        var snapshot = new GitReviewSource(repo.Dir).Load(null);

        // Assert
        await Assert.That(snapshot.WithUncommitted.Select(f => f.Path)).IsEquivalentTo(["a.txt", "b.txt", "new file.txt"]);
        await Assert.That(snapshot.UncommittedCount).IsEqualTo(3);
        await Assert.That(snapshot.Committed).IsEmpty();
    }

    [Test]
    public async Task UntrackedFilesIgnoreGitignore()
    {
        // Arrange
        using var repo = Repo(r => r.Write(".gitignore", "*.log\n"));
        repo.Write("noise.log", "x\n");

        // Act
        var snapshot = new GitReviewSource(repo.Dir).Load(null);

        // Assert
        await Assert.That(snapshot.WithUncommitted.Select(f => f.Path)).DoesNotContain("noise.log");
    }

    [Test]
    public async Task SidesShowBaseAndWorkTreeWithSpacesInThePath()
    {
        // Arrange
        using var repo = Repo(r => r.Write("dir name/a b.txt", "base\n"));
        repo.Git("switch", "-q", "-c", "feature");
        repo.Write("dir name/a b.txt", "work\n");
        var source = new GitReviewSource(repo.Dir);
        var snapshot = source.Load(null);
        var file = snapshot.WithUncommitted.Single();

        // Act
        var (left, right) = source.Sides(snapshot, file, new ReviewScope(ReviewScopeKind.Uncommitted));

        // Assert
        await Assert.That(left.Document!.RawLines[0]).IsEqualTo("base\n");
        await Assert.That(right.Document!.RawLines[0]).IsEqualTo("work\n");
        await Assert.That(right.Subtitle).IsEqualTo("working tree");
    }

    [Test]
    public async Task CommitsListsTheBranchsCommits()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));
        OnFeature(repo, r => r.Write("b.txt", "1\n"), "add b");
        repo.Write("c.txt", "1\n");
        repo.CommitAll("add c");

        // Act
        var commits = new GitReviewSource(repo.Dir).Load(null).Commits;

        // Assert
        await Assert.That(commits.Select(c => c.Subject)).IsEquivalentTo(["add c", "add b"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CommitFilesShowsOnlyThatCommit()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));
        OnFeature(repo, r => r.Write("b.txt", "1\n"), "add b");
        repo.Write("c.txt", "1\n");
        repo.CommitAll("add c");
        var source = new GitReviewSource(repo.Dir);
        var snapshot = source.Load(null);
        var addB = snapshot.Commits.Single(c => c.Subject == "add b");

        // Act
        var files = source.CommitFiles(snapshot, addB);

        // Assert
        await Assert.That(files.Single().Path).IsEqualTo("b.txt");
    }

    [Test]
    public async Task CommitFilesWorksForARootCommit()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));
        var source = new GitReviewSource(repo.Dir);
        var snapshot = source.Load("main");
        var root = new ReviewCommit(repo.Git("rev-parse", "HEAD").Trim(), null, "root", "x", "now", "base");

        // Act
        var files = source.CommitFiles(snapshot, root);

        // Assert
        await Assert.That(files.Single().Path).IsEqualTo("a.txt");
    }

    [Test]
    public async Task PredictFindsAConflict()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "base\n"));
        OnFeature(repo, r => r.Write("a.txt", "feature\n"));
        repo.Git("switch", "-q", "main");
        repo.Write("a.txt", "main\n");
        repo.CommitAll("on main");
        repo.Git("switch", "-q", "feature");
        var source = new GitReviewSource(repo.Dir);

        // Act
        var prediction = source.Predict(source.Load(null));

        // Assert
        await Assert.That(prediction.Known).IsTrue();
        await Assert.That(prediction.Clean).IsFalse();
        await Assert.That(prediction.Files.Single()).IsEqualTo("a.txt");
    }

    [Test]
    public async Task NothingInTheRepositoryChanges()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "base\n"));
        OnFeature(repo, r => r.Write("a.txt", "feature\n"));
        repo.Git("switch", "-q", "main");
        repo.Write("a.txt", "main\n");
        repo.CommitAll("on main");
        repo.Git("switch", "-q", "feature");
        File.SetLastWriteTimeUtc(Path.Combine(repo.Dir, "a.txt"), DateTime.UtcNow.AddMinutes(-5)); // a stale stat that a refresh would rewrite the index for
        repo.Write("dirty.txt", "new\n");
        var source = new GitReviewSource(repo.Dir);
        string before = RepoFingerprint(Path.Combine(repo.Dir, ".git"));

        // Act
        var snapshot = source.Load(null);
        source.Predict(snapshot);
        foreach (var file in snapshot.WithUncommitted) source.Sides(snapshot, file, new ReviewScope(ReviewScopeKind.Uncommitted));
        source.Branches(snapshot);

        // Assert
        await Assert.That(RepoFingerprint(Path.Combine(repo.Dir, ".git"))).IsEqualTo(before);
    }

    [Test]
    public async Task BehindCountComesFromTheLocalRemoteRef()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));
        repo.Write("b.txt", "2\n");
        repo.CommitAll("second");
        repo.Git("update-ref", "refs/remotes/origin/main", "HEAD");
        repo.Git("reset", "-q", "--hard", "HEAD~1");
        repo.Git("switch", "-q", "-c", "feature");

        // Act
        var snapshot = new GitReviewSource(repo.Dir).Load(null);

        // Assert
        await Assert.That(snapshot.BehindCount).IsEqualTo(1);
    }

    [Test]
    public async Task FetchUpdatesTheRemoteRefFromATempOrigin()
    {
        // Arrange
        using var upstream = Repo(r => r.Write("a.txt", "1\n"));
        using var repo = new TempRepo();
        repo.Git("remote", "add", "origin", upstream.Dir);
        repo.Git("fetch", "-q", "origin", "main");
        repo.Git("reset", "-q", "--hard", "origin/main");
        repo.Git("switch", "-q", "-c", "feature");
        upstream.Write("b.txt", "2\n");
        upstream.CommitAll("new on origin");
        var source = new GitReviewSource(repo.Dir);
        var snapshot = source.Load(null);

        // Act
        string? problem = source.Fetch(snapshot);

        // Assert
        await Assert.That(problem).IsNull();
        await Assert.That(repo.Git("rev-parse", "origin/main").Trim()).IsEqualTo(upstream.Git("rev-parse", "HEAD").Trim());
    }

    [Test]
    public async Task RepositoryWithoutOriginHasNoFetchOrNote()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));
        repo.Git("switch", "-q", "-c", "feature");

        // Act
        var snapshot = new GitReviewSource(repo.Dir).Load(null);

        // Assert
        await Assert.That(snapshot.HasOrigin).IsFalse();
        await Assert.That(snapshot.BehindCount).IsEqualTo(0);
    }

    [Test]
    public async Task OnMainItselfIsReportedAsOnTheBase()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));

        // Act
        var snapshot = new GitReviewSource(repo.Dir).Load(null);

        // Assert
        await Assert.That(snapshot.OnBase).IsTrue();
        await Assert.That(snapshot.Committed).IsEmpty();
    }

    [Test]
    public async Task WorksFromASubdirectory()
    {
        // Arrange
        using var repo = Repo(r => r.Write("src/a.txt", "1\n"));
        repo.Git("switch", "-q", "-c", "feature");
        repo.Write("src/a.txt", "2\n");

        // Act
        var snapshot = new GitReviewSource(Path.Combine(repo.Dir, "src")).Load(null);

        // Assert
        await Assert.That(snapshot.WithUncommitted.Single().Path).IsEqualTo("src/a.txt");
    }

    [Test]
    public async Task LinkedWorktreeIsDetected()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));
        string path = repo.Dir + "-wt";
        repo.Git("worktree", "add", "-q", "-b", "feature", path);
        try
        {
            // Act
            var snapshot = new GitReviewSource(path).Load(null);

            // Assert
            await Assert.That(snapshot.LinkedWorktree).IsTrue();
        }
        finally
        {
            repo.Git("worktree", "remove", "--force", path);
        }
    }

    [Test]
    public async Task MainWorktreeIsNotLinked()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));

        // Act
        var snapshot = new GitReviewSource(repo.Dir).Load(null);

        // Assert
        await Assert.That(snapshot.LinkedWorktree).IsFalse();
    }

    [Test]
    public async Task MidMergeShowsTheUnmergedFileAndTheOperation()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "base\n"));
        OnFeature(repo, r => r.Write("a.txt", "feature\n"));
        repo.Git("switch", "-q", "main");
        repo.Write("a.txt", "main\n");
        repo.CommitAll("on main");
        repo.Git("switch", "-q", "feature");
        repo.Git("merge", "-q", "--no-edit", "main");

        // Act
        var snapshot = new GitReviewSource(repo.Dir).Load(null);

        // Assert
        await Assert.That(snapshot.WithUncommitted.Single().Change).IsEqualTo(ReviewChange.Unmerged);
        await Assert.That(snapshot.Operation).IsNotNull();
    }

    [Test]
    public async Task UnknownBaseIsReported()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));

        // Act
        var snapshot = new GitReviewSource(repo.Dir).Load("no-such-branch");

        // Assert
        await Assert.That(snapshot.State).IsEqualTo(ReviewState.UnknownBase);
    }

    [Test]
    public async Task BinaryFileLoadsAsAProblemWithoutHanging()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));
        repo.Git("switch", "-q", "-c", "feature");
        File.WriteAllBytes(Path.Combine(repo.Dir, "pic.bin"), [0, 1, 2, 0]);
        var source = new GitReviewSource(repo.Dir);
        var snapshot = source.Load(null);
        var file = snapshot.WithUncommitted.Single(f => f.Path == "pic.bin");

        // Act
        var (_, right) = source.Sides(snapshot, file, new ReviewScope(ReviewScopeKind.Uncommitted));

        // Assert
        await Assert.That(file.Binary).IsTrue();
        await Assert.That(right.Document!.Problem).IsEqualTo(DocumentProblem.Binary);
    }

    [Test]
    public async Task RunsAsATuiProgramOverAFakeTerminal()
    {
        // Arrange
        using var repo = Repo(r => r.Write("a.txt", "1\n"));
        repo.Git("switch", "-q", "-c", "feature");
        repo.Write("a.txt", "2\n");
        var terminal = new FakeTerminal(140, 30, ("a.txt", "q"u8.ToArray()));

        // Act
        int code = ReviewTool.Run(terminal, null, repo.Dir);

        // Assert
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(terminal.OutputText).Contains("uncommitted");
    }

    [Test]
    public async Task ComparePaneKeepsDrawingTheOldFileUntilTheNewResultArrives()
    {
        // Arrange: a background that holds its work back, and a second, shorter file being computed
        var background = new QueuedBackground();
        var pane = new ComparePane(background);
        pane.Show(new CompareSide("a", "", TextDocument.FromText("1\n2\n3\n4\n")), new CompareSide("a", "", TextDocument.FromText("1\n2\n3\n5\n")));
        background.RunPending();
        pane.Show(new CompareSide("b", "", TextDocument.FromText("x\n")), new CompareSide("b", "", TextDocument.FromText("y\n")));
        var frame = new Frame(120, 30);

        // Act: a frame drawn while the new result is still pending
        pane.Render(frame, 0, 0, 120, 28);

        // Assert
        await Assert.That(frame.AllText()).Contains("4");
    }

    private sealed class QueuedBackground : IBackground
    {
        private readonly Queue<Action> _pending = new();
        public void Run<T>(Func<T> work, Action<T> done) => _pending.Enqueue(() => done(work()));
        public void RunPending() { while (_pending.TryDequeue(out var a)) a(); }
    }

    [Test]
    public async Task EntryCommandRoutesReview()
    {
        // Act
        var entry = EntryCommand.Classify(["review"], invokedAsTmux: false, "--helper");

        // Assert
        await Assert.That(entry).IsEqualTo(EntryKind.Review);
    }

    [Test]
    public async Task CompareWithMainIsABindableAction()
    {
        // Act
        bool ok = ActionSpec.TryParse("CompareWithMain", out var spec, out _);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(spec.Name).IsEqualTo("CompareWithMain");
    }

    [Test]
    public async Task FocusReportIsDecoded()
    {
        // Arrange
        var decoder = new TuiInputDecoder();
        var events = new List<TuiEvent>();

        // Act
        decoder.Feed("\e[I"u8, events);

        // Assert
        await Assert.That(events.Single().Focused == true).IsTrue();
    }
}
