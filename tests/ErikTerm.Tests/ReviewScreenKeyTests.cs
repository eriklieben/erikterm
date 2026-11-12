using ErikTerm.Ui;
using ErikTerm.Ui.Compare;

namespace ErikTerm.Tests;

/// <summary>
/// The review screen's keys in the file list, the commit list and the base picker, the wrap-around of next/previous
/// change, and what keys say when there is nothing for them to act on.
/// </summary>
public class ReviewScreenKeyTests
{
    private sealed class InlineBackground : IBackground
    {
        public void Run<T>(Func<T> work, Action<T> done) => done(work());
    }

    private sealed class FakeSource : IReviewSource
    {
        public ReviewSnapshot Snapshot { get; set; } = NewSnapshot();
        public int Loads;
        public string? LastBase;

        public ReviewSnapshot Load(string? baseOverride)
        {
            Loads++;
            LastBase = baseOverride;
            return Snapshot;
        }

        public (CompareSide Left, CompareSide Right) Sides(ReviewSnapshot snapshot, ReviewFile file, ReviewScope scope)
        {
            const string Left = "1\n2\n3\n4\n5\n6\n7\n8\n9\n", Right = "1\nb\n3\n4\n5\n6\n7\nh\n9\n";
            return (new CompareSide(file.Path, "main", TextDocument.FromText(Left)), new CompareSide(file.Path, "branch", TextDocument.FromText(Right)));
        }

        public IReadOnlyList<ReviewFile> CommitFiles(ReviewSnapshot snapshot, ReviewCommit commit) =>
            [new ReviewFile("only-in-commit.ts", null, ReviewChange.Modified)];

        public ConflictPrediction Predict(ReviewSnapshot snapshot) => new(true, true, null, [], null);
        public IReadOnlyList<string> Branches(ReviewSnapshot snapshot) => ["develop", "main"];
        public string? Fetch(ReviewSnapshot snapshot) => null;
        public string? RequestFinish(ReviewSnapshot snapshot) => null;
    }

    private static ReviewFile F(string path, bool uncommitted = false) => new(path, null, ReviewChange.Modified, 2, 2, Uncommitted: uncommitted);

    private static ReviewSnapshot NewSnapshot(bool commits = true, bool files = true) => new()
    {
        State = ReviewState.Ok,
        Root = "/repo",
        Branch = "feature",
        HeadOid = "1111111",
        Base = "main",
        BaseOid = "2222222",
        MergeBase = "3333333",
        Committed = files ? [F("a.ts"), F("b.ts")] : [],
        WithUncommitted = files ? [F("a.ts"), F("b.ts"), F("c.ts", uncommitted: true)] : [],
        UncommittedCount = files ? 1 : 0,
        Commits = commits ? [new ReviewCommit("deadbeef", "cafe", "deadbee", "Ann", "now", "Fix the bug")] : [],
    };

    private static (ReviewScreen Screen, FakeSource Source) Start(ReviewSnapshot? snapshot = null)
    {
        var source = new FakeSource();
        if (snapshot is not null) source.Snapshot = snapshot;
        var screen = new ReviewScreen(new InlineBackground(), source);
        screen.Start();
        return (screen, source);
    }

    private static void Keys(ReviewScreen screen, params KeyEvent[] keys)
    {
        foreach (var k in keys) screen.OnKey(k);
    }

    private static KeyEvent K(TuiKey key) => new(key);
    private static KeyEvent C(char c) => KeyEvent.OfChar(c);

    // ---- The file list ---------------------------------------------------------------------------

    public static IEnumerable<Func<(KeyEvent[] Keys, string Path)>> ListKeys() =>
    [
        () => ([C('j')], "b.ts"),
        () => ([K(TuiKey.Down)], "b.ts"),
        () => ([C('j'), C('k')], "a.ts"),
        () => ([C('j'), K(TuiKey.Up)], "a.ts"),
        () => ([C('G')], "c.ts"),
        () => ([K(TuiKey.End)], "c.ts"),
        () => ([C('G'), C('g')], "a.ts"),
        () => ([C('G'), K(TuiKey.Home)], "a.ts"),
        () => ([K(TuiKey.PageDown)], "c.ts"),   // clamped to the last file
        () => ([C('G'), K(TuiKey.PageUp)], "a.ts"),
    ];

    [Test]
    [MethodDataSource(nameof(ListKeys))]
    public async Task ListKeysMoveTheSelection(KeyEvent[] keys, string path)
    {
        // Arrange: focus on the file list
        var (screen, _) = Start();
        Keys(screen, K(TuiKey.Tab));

        // Act
        Keys(screen, keys);

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo(path);
    }

    [Test]
    public async Task EnterInTheListGoesBackToTheDiff()
    {
        // Arrange
        var (screen, _) = Start();
        Keys(screen, K(TuiKey.BackTab));
        bool onList = screen.FocusOnList;

        // Act
        Keys(screen, K(TuiKey.Enter));

        // Assert
        await Assert.That(onList).IsTrue();
        await Assert.That(screen.FocusOnList).IsFalse();
    }

    [Test]
    [Arguments('}', "b.ts")]
    [Arguments('J', "b.ts")]
    public async Task FileKeysWorkFromTheDiff(char key, string path)
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        Keys(screen, C(key));

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo(path);
    }

    [Test]
    public async Task PreviousFileKeyStopsAtTheFirst()
    {
        // Arrange
        var (screen, _) = Start();
        Keys(screen, C('}'));

        // Act
        Keys(screen, C('{'), C('{'));

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("a.ts");
    }

    [Test]
    public async Task NextChangeAfterTheLastFileWrapsToTheFirst()
    {
        // Arrange: the last change of the last file
        var (screen, _) = Start();
        Keys(screen, K(TuiKey.Tab), K(TuiKey.End), K(TuiKey.Tab), C('n'));

        // Act
        Keys(screen, C('n'));

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("a.ts");
        await Assert.That(screen.Status).IsEqualTo("Back at the first file");
    }

    [Test]
    public async Task PreviousChangeBeforeTheFirstFileWrapsToTheLast()
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        Keys(screen, C('p'));

        // Assert
        await Assert.That(screen.Selected!.Path).IsEqualTo("c.ts");
        await Assert.That(screen.Status).IsEqualTo("Back at the last file");
    }

    [Test]
    public async Task ChangeKeysWithNoFilesDoNothing()
    {
        // Arrange
        var (screen, _) = Start(NewSnapshot(files: false));

        // Act
        Keys(screen, C('n'), C('J'));

        // Assert
        await Assert.That(screen.Selected).IsNull();
        await Assert.That(screen.Status).IsEqualTo("");
    }

    [Test]
    public async Task RefreshLoadsAgain()
    {
        // Arrange
        var (screen, source) = Start();
        int before = source.Loads;

        // Act
        Keys(screen, C('r'));

        // Assert
        await Assert.That(source.Loads).IsEqualTo(before + 1);
    }

    [Test]
    public async Task CtrlCLeaves()
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        Keys(screen, KeyEvent.CtrlOf('c'));

        // Assert
        await Assert.That(screen.Done).IsTrue();
    }

    // ---- The commit list -------------------------------------------------------------------------

    [Test]
    public async Task CommitsWithoutAnySaysSo()
    {
        // Arrange
        var (screen, _) = Start(NewSnapshot(commits: false));

        // Act
        Keys(screen, C('c'));

        // Assert
        await Assert.That(screen.CommitsOpen).IsFalse();
        await Assert.That(screen.Status).IsEqualTo("No commits on this branch against its base");
    }

    public static IEnumerable<Func<(KeyEvent[] Keys, bool OnCommit)>> CommitKeys() =>
    [
        () => ([C('j')], true),
        () => ([K(TuiKey.Down)], true),
        () => ([K(TuiKey.End)], true),
        () => ([K(TuiKey.PageDown)], true),
        () => ([C('j'), C('j')], true),               // stays on the last row
        () => ([C('j'), C('k')], false),
        () => ([C('j'), K(TuiKey.Up)], false),
        () => ([C('j'), K(TuiKey.Home)], false),
        () => ([C('j'), K(TuiKey.PageUp)], false),
        () => ([C('k')], false),                       // stays on the first row: the whole branch
    ];

    [Test]
    [MethodDataSource(nameof(CommitKeys))]
    public async Task CommitKeysMoveTheCursorAndEnterChooses(KeyEvent[] keys, bool onCommit)
    {
        // Arrange
        var (screen, _) = Start();
        Keys(screen, C('c'));

        // Act
        Keys(screen, keys);
        Keys(screen, K(TuiKey.Enter));

        // Assert
        await Assert.That(screen.CommitsOpen).IsFalse();
        await Assert.That(screen.Commit is not null).IsEqualTo(onCommit);
    }

    [Test]
    public async Task AChosenCommitShowsItsFiles()
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        Keys(screen, C('c'), C('j'), K(TuiKey.Enter));

        // Assert
        await Assert.That(screen.Files.Select(f => f.Path)).IsEquivalentTo(new[] { "only-in-commit.ts" });
    }

    [Test]
    [Arguments(TuiKey.Esc)]
    [Arguments(TuiKey.Char)]   // c again
    public async Task CommitListCloses(TuiKey key)
    {
        // Arrange
        var (screen, _) = Start();
        Keys(screen, C('c'));

        // Act
        Keys(screen, key == TuiKey.Char ? C('c') : K(key));

        // Assert
        await Assert.That(screen.CommitsOpen).IsFalse();
        await Assert.That(screen.Done).IsFalse();
    }

    [Test]
    public async Task ReopeningTheCommitListStartsOnTheChosenCommit()
    {
        // Arrange
        var (screen, _) = Start();
        Keys(screen, C('c'), C('j'), K(TuiKey.Enter));

        // Act: open again and choose the row under the cursor
        Keys(screen, C('c'), K(TuiKey.Enter));

        // Assert
        await Assert.That(screen.Commit!.Sha).IsEqualTo("deadbeef");
    }

    [Test]
    public async Task EscFromACommitGoesBackToTheBranch()
    {
        // Arrange
        var (screen, _) = Start();
        Keys(screen, C('c'), C('j'), K(TuiKey.Enter));

        // Act
        Keys(screen, K(TuiKey.Esc));

        // Assert
        await Assert.That(screen.Commit).IsNull();
        await Assert.That(screen.Done).IsFalse();
    }

    [Test]
    public async Task UncommittedInACommitIsRefused()
    {
        // Arrange
        var (screen, _) = Start();
        Keys(screen, C('c'), C('j'), K(TuiKey.Enter));

        // Act
        Keys(screen, C('u'));

        // Assert
        await Assert.That(screen.Status).IsEqualTo("A single commit has no uncommitted changes");
        await Assert.That(screen.Uncommitted).IsTrue();
    }

    // ---- The base picker -------------------------------------------------------------------------

    public static IEnumerable<Func<(KeyEvent[] Keys, string? Base)>> PickerKeys() =>
    [
        () => ([K(TuiKey.Enter)], "develop"),
        () => ([K(TuiKey.Down), K(TuiKey.Enter)], "main"),
        () => ([K(TuiKey.Down), K(TuiKey.Down), K(TuiKey.Enter)], "main"),   // stays on the last
        () => ([K(TuiKey.Down), K(TuiKey.Up), K(TuiKey.Up), K(TuiKey.Enter)], "develop"),
        () => ([C('m'), C('a'), K(TuiKey.Enter)], "main"),
        () => ([C('m'), K(TuiKey.Backspace), K(TuiKey.Enter)], "develop"),
        () => ([K(TuiKey.Backspace), K(TuiKey.Enter)], "develop"),            // nothing to erase
        () => ([C('z'), K(TuiKey.Enter)], null),                               // no match: nothing chosen
        () => ([new KeyEvent(TuiKey.Char, 'm', Alt: true), K(TuiKey.Enter)], "develop"),   // Alt+m isn't typed
        () => ([K(TuiKey.Esc), K(TuiKey.Enter)], null),
    ];

    [Test]
    [MethodDataSource(nameof(PickerKeys))]
    public async Task PickerKeysFilterAndChoose(KeyEvent[] keys, string? expectedBase)
    {
        // Arrange
        var (screen, source) = Start();
        Keys(screen, C('b'));

        // Act
        Keys(screen, keys);

        // Assert
        await Assert.That(source.LastBase).IsEqualTo(expectedBase);
    }

    [Test]
    public async Task APasteIntoThePickerFilters()
    {
        // Arrange
        var (screen, source) = Start();
        Keys(screen, C('b'));

        // Act
        screen.OnPaste(" ma\n");
        Keys(screen, K(TuiKey.Enter));

        // Assert
        await Assert.That(source.LastBase).IsEqualTo("main");
    }

    [Test]
    public async Task APasteWithNothingOpenDoesNothing()
    {
        // Arrange
        var (screen, source) = Start();

        // Act
        screen.OnPaste("q");

        // Assert
        await Assert.That(screen.Done).IsFalse();
        await Assert.That(source.LastBase).IsNull();
    }

    [Test]
    public async Task CtrlCInThePickerLeaves()
    {
        // Arrange
        var (screen, _) = Start();
        Keys(screen, C('b'));

        // Act
        Keys(screen, KeyEvent.CtrlOf('c'));

        // Assert
        await Assert.That(screen.Done).IsTrue();
    }

    [Test]
    public async Task ChoosingABaseLeavesTheCommit()
    {
        // Arrange
        var (screen, _) = Start();
        Keys(screen, C('c'), C('j'), K(TuiKey.Enter));

        // Act
        Keys(screen, C('b'), K(TuiKey.Enter));

        // Assert
        await Assert.That(screen.Commit).IsNull();
    }
}
