using ErikTerm.Ui.Compare;
using static ErikTerm.Tests.CompareFixtures;

namespace ErikTerm.Tests;

/// <summary>
/// The conflict resolver's other keys: navigation (each named key does what its letter does), the confirm prompt's
/// no, file switching, the view toggle, and the outcomes of keys that have nothing to act on.
/// </summary>
public class MergeScreenKeyTests
{
    private const int Lines = 60, ConflictLine = 30;

    /// <summary>A 60-line file (lines "rNNx-padding") with one conflict at line 30.</summary>
    private static MergeFile Long()
    {
        string Text(Func<int, string> line) => string.Concat(Enumerable.Range(0, Lines).Select(i => line(i) + "\n"));
        string Row(int i) => $"r{i:00}x-padding";
        return Merged(Text(Row), Text(i => i == ConflictLine ? "ours-side" : Row(i)), Text(i => i == ConflictLine ? "theirs-side" : Row(i)));
    }

    private static MergeFile OneConflict(string path = "src/auth.ts") => Merged("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n", path);

    private static MergeFile AutoOnly(string path = "README.md") => Merged("a\nb\nc\n", "A\nb\nc\n", "a\nb\nC\n", path);

    private static (MergeScreen Screen, FakeMergeHost Host) Start(params MergeFile[] files)
    {
        var host = new FakeMergeHost();
        var screen = new MergeScreen(Session(host, files.Length == 0 ? [Long()] : files));
        Render(screen, 140, 30);
        return (screen, host);
    }

    private static string Text(MergeScreen screen, int cols = 140, int rows = 30) => Render(screen, cols, rows).AllText();

    private static void Keys(MergeScreen screen, params KeyEvent[] keys)
    {
        foreach (var k in keys) screen.OnKey(k);
    }

    public static IEnumerable<Func<(KeyEvent[] Before, KeyEvent Named, KeyEvent Letter)>> NamedKeys() =>
    [
        () => ([KeyEvent.OfChar('G')], new KeyEvent(TuiKey.Home), KeyEvent.OfChar('g')),
        () => ([], new KeyEvent(TuiKey.End), KeyEvent.OfChar('G')),
        () => ([], new KeyEvent(TuiKey.Down), KeyEvent.OfChar('j')),
        () => ([KeyEvent.OfChar('G')], new KeyEvent(TuiKey.Up), KeyEvent.OfChar('k')),
        () => ([], new KeyEvent(TuiKey.Right), KeyEvent.OfChar('l')),
        () => ([KeyEvent.OfChar('l'), KeyEvent.OfChar('l')], new KeyEvent(TuiKey.Left), KeyEvent.OfChar('h')),
        () => ([], KeyEvent.CtrlOf('d'), KeyEvent.OfChar('d')),
        () => ([], KeyEvent.CtrlOf('e'), KeyEvent.OfChar('d')),
        () => ([KeyEvent.OfChar('G')], KeyEvent.CtrlOf('u'), KeyEvent.CtrlOf('y')),
        () => ([], new KeyEvent(TuiKey.PageDown), KeyEvent.CtrlOf('d')),   // farther than half a page: compared below
    ];

    [Test]
    [MethodDataSource(nameof(NamedKeys))]
    public async Task NamedKeysMoveTheView(KeyEvent[] before, KeyEvent named, KeyEvent letter)
    {
        // Arrange
        var (a, _) = Start();
        var (b, _) = Start();
        Keys(a, before);
        Keys(b, before);
        string unmoved = Text(a);

        // Act
        a.OnKey(named);
        b.OnKey(letter);

        // Assert: the key moved the view, and like its letter (PageDown goes a page, the letter half of one)
        string viaNamed = Text(a), viaLetter = Text(b);
        await Assert.That(viaNamed).IsNotEqualTo(unmoved);
        if (named.Key != TuiKey.PageDown) await Assert.That(viaNamed).IsEqualTo(viaLetter);
        else await Assert.That(viaNamed).IsNotEqualTo(viaLetter);
    }

    [Test]
    public async Task PageUpUndoesPageDown()
    {
        // Arrange
        var (screen, _) = Start();
        Keys(screen, new KeyEvent(TuiKey.Home));
        string top = Text(screen);
        Keys(screen, new KeyEvent(TuiKey.PageDown));

        // Act
        Keys(screen, new KeyEvent(TuiKey.PageUp));

        // Assert
        await Assert.That(Text(screen)).IsEqualTo(top);
    }

    [Test]
    public async Task EndShowsTheLastLinesAndHomeTheFirst()
    {
        // Arrange
        var (screen, _) = Start();

        // Act
        Keys(screen, new KeyEvent(TuiKey.End));
        string atEnd = Text(screen);
        Keys(screen, new KeyEvent(TuiKey.Home));
        string atStart = Text(screen);

        // Assert
        await Assert.That(atEnd).Contains("r59x");
        await Assert.That(atEnd).DoesNotContain("r00x");
        await Assert.That(atStart).Contains("r00x");
    }

    [Test]
    public async Task EscWithAnUnsavedResultAsksAndNCancels()
    {
        // Arrange
        var (screen, _) = Start(OneConflict());
        Keys(screen, KeyEvent.OfChar('o'), new KeyEvent(TuiKey.Esc));
        string asking = Text(screen);

        // Act
        Keys(screen, KeyEvent.OfChar('n'));

        // Assert
        await Assert.That(asking).Contains("Quit without saving");
        await Assert.That(screen.Done).IsFalse();
        await Assert.That(Text(screen)).DoesNotContain("Quit without saving");
    }

    [Test]
    public async Task EscCancelsTheConfirmPrompt()
    {
        // Arrange
        var (screen, _) = Start(OneConflict());
        Keys(screen, KeyEvent.OfChar('o'), KeyEvent.OfChar('q'));

        // Act
        Keys(screen, new KeyEvent(TuiKey.Esc));

        // Assert
        await Assert.That(screen.Done).IsFalse();
    }

    [Test]
    public async Task OtherKeysLeaveTheConfirmPromptOpen()
    {
        // Arrange
        var (screen, _) = Start(OneConflict());
        Keys(screen, KeyEvent.OfChar('o'), KeyEvent.OfChar('q'));

        // Act
        Keys(screen, KeyEvent.OfChar('z'));

        // Assert
        await Assert.That(Text(screen)).Contains("Quit without saving");
    }

    [Test]
    public async Task EnterConfirms()
    {
        // Arrange
        var (screen, _) = Start(OneConflict());
        Keys(screen, KeyEvent.OfChar('o'), KeyEvent.OfChar('q'));

        // Act
        Keys(screen, new KeyEvent(TuiKey.Enter));

        // Assert
        await Assert.That(screen.Done).IsTrue();
    }

    [Test]
    public async Task CtrlCQuitsWhenNothingChanged()
    {
        // Arrange
        var (screen, _) = Start(OneConflict());

        // Act
        Keys(screen, KeyEvent.CtrlOf('c'));

        // Assert
        await Assert.That(screen.Done).IsTrue();
    }

    [Test]
    public async Task AltLettersAreNoCommands()
    {
        // Arrange
        var (screen, _) = Start(OneConflict());

        // Act
        Keys(screen, new KeyEvent(TuiKey.Char, 'q', Alt: true));

        // Assert
        await Assert.That(screen.Done).IsFalse();
    }

    [Test]
    public async Task TabAndBackTabSwitchFilesAndWrap()
    {
        // Arrange
        var (screen, _) = Start(OneConflict("src/a.ts"), AutoOnly("src/b.ts"));

        // Act
        Keys(screen, new KeyEvent(TuiKey.Tab));
        string second = Text(screen, 80, 24);
        Keys(screen, new KeyEvent(TuiKey.Tab));
        string wrapped = Text(screen, 80, 24);
        Keys(screen, new KeyEvent(TuiKey.BackTab));

        // Assert
        await Assert.That(second).Contains("file 2/2");
        await Assert.That(wrapped).Contains("file 1/2");
        await Assert.That(Text(screen, 80, 24)).Contains("file 2/2");
    }

    [Test]
    public async Task WTogglesBetweenColumnsAndCards()
    {
        // Arrange
        var (screen, _) = Start(OneConflict());

        // Act
        Keys(screen, KeyEvent.OfChar('w'));
        string wideAsCards = Text(screen, 140, 30);
        Keys(screen, KeyEvent.OfChar('w'));
        string wideAgain = Text(screen, 140, 30);

        // Assert
        await Assert.That(wideAsCards).DoesNotContain("OUTPUT");
        await Assert.That(wideAgain).Contains("OUTPUT");
    }

    [Test]
    public async Task WInANarrowPaneShowsColumns()
    {
        // Arrange
        var (screen, _) = Start(OneConflict());
        Render(screen, 80, 24);

        // Act
        Keys(screen, KeyEvent.OfChar('w'));

        // Assert
        await Assert.That(Text(screen, 80, 24)).Contains("OUTPUT");
    }

    [Test]
    [Arguments('n')]
    [Arguments('o')]
    public async Task ConflictKeysInAFileWithoutConflictsSaySo(char key)
    {
        // Arrange
        var (screen, _) = Start(AutoOnly());

        // Act
        Keys(screen, KeyEvent.OfChar(key));

        // Assert
        await Assert.That(Text(screen)).Contains("No conflicts in this file");
    }

    [Test]
    public async Task BothTakesBothSides()
    {
        // Arrange
        var file = OneConflict();
        var (screen, _) = Start(file);

        // Act
        Keys(screen, KeyEvent.OfChar('b'));

        // Assert
        await Assert.That(file.Output.Select(l => l.Raw)).Contains("ours\n");
        await Assert.That(file.Output.Select(l => l.Raw)).Contains("theirs\n");
        await Assert.That(file.UnresolvedCount).IsEqualTo(0);
    }

    [Test]
    public async Task ResetUndecidesTheConflict()
    {
        // Arrange
        var file = OneConflict();
        var (screen, _) = Start(file);
        Keys(screen, KeyEvent.OfChar('o'));

        // Act
        Keys(screen, KeyEvent.OfChar('r'));

        // Assert
        await Assert.That(file.UnresolvedCount).IsEqualTo(1);
    }

    [Test]
    public async Task PreviousWrapsToTheLastConflict()
    {
        // Arrange: two conflicts
        var file = Merged("a\nb\nc\nd\ne\n", "a\nB1\nc\nD1\ne\n", "a\nB2\nc\nD2\ne\n");
        var (screen, _) = Start(file);

        // Act
        Keys(screen, KeyEvent.OfChar('p'), KeyEvent.OfChar('o'));

        // Assert: the second conflict took ours, the first is still open
        await Assert.That(file.Output.Select(l => l.Raw)).Contains("D1\n");
        await Assert.That(file.UnresolvedCount).IsEqualTo(1);
    }

    [Test]
    public async Task KOnATextFileScrollsUp()
    {
        // Arrange
        var (a, _) = Start();
        var (b, _) = Start();
        Keys(a, KeyEvent.OfChar('G'));
        Keys(b, KeyEvent.OfChar('G'));
        string atEnd = Text(a);

        // Act
        Keys(a, KeyEvent.OfChar('k'));
        Keys(b, new KeyEvent(TuiKey.Up));

        // Assert
        await Assert.That(Text(a)).IsNotEqualTo(atEnd);
        await Assert.That(Text(a)).IsEqualTo(Text(b));
    }

    [Test]
    public async Task KOnADeleteModifyFileKeepsTheSurvivingSide()
    {
        // Arrange
        var file = MergeFile.DeleteModify("gone.txt", "main", "feature/x", DeletedSide.Ours, "still here\n"u8.ToArray());
        var (screen, _) = Start(file);

        // Act
        Keys(screen, KeyEvent.OfChar('k'));

        // Assert
        await Assert.That(file.FileChoice).IsEqualTo(FileChoice.Theirs);
        await Assert.That(Text(screen)).Contains("Keeping feature/x's version");
    }

    [Test]
    public async Task TakingASideOfADeleteModifyFileKeepsTheSideThatExists()
    {
        // Arrange
        var file = MergeFile.DeleteModify("gone.txt", "main", "feature/x", DeletedSide.Theirs, "still here\n"u8.ToArray());
        var (screen, _) = Start(file);

        // Act
        Keys(screen, KeyEvent.OfChar('t'));

        // Assert
        await Assert.That(file.FileChoice).IsEqualTo(FileChoice.Ours);
    }

    [Test]
    public async Task ResetOfAFileChoiceSaysSo()
    {
        // Arrange
        var file = MergeFile.Binary("logo.png", "main", "feature/x", [1], [2]);
        var (screen, _) = Start(file);
        Keys(screen, KeyEvent.OfChar('o'));

        // Act
        Keys(screen, KeyEvent.OfChar('r'));

        // Assert
        await Assert.That(file.FileChoice).IsEqualTo(FileChoice.None);
        await Assert.That(Text(screen)).Contains("Choice reset");
    }

    [Test]
    public async Task BinaryFilesCantBeDeletedOrTakenFromBoth()
    {
        // Arrange
        var file = MergeFile.Binary("logo.png", "main", "feature/x", [1], [2]);
        var (screen, _) = Start(file);

        // Act
        Keys(screen, KeyEvent.OfChar('x'), KeyEvent.OfChar('b'));

        // Assert
        await Assert.That(file.FileChoice).IsEqualTo(FileChoice.None);
    }

    [Test]
    public async Task DeleteOnATextFileDoesNothing()
    {
        // Arrange
        var file = OneConflict();
        var (screen, _) = Start(file);

        // Act
        Keys(screen, KeyEvent.OfChar('x'));

        // Assert
        await Assert.That(file.FileChoice).IsEqualTo(FileChoice.None);
        await Assert.That(file.UnresolvedCount).IsEqualTo(1);
    }

    [Test]
    public async Task UndoWithNothingToUndoChangesNothing()
    {
        // Arrange
        var (screen, _) = Start(OneConflict());
        string before = Text(screen);

        // Act
        Keys(screen, KeyEvent.OfChar('u'));

        // Assert
        await Assert.That(Text(screen)).IsEqualTo(before);
    }

    [Test]
    public async Task WholeFileEditResolvesEverything()
    {
        // Arrange
        var file = OneConflict();
        var (screen, host) = Start(file);
        host.Editor = _ => "a\nhand\nc\n";

        // Act
        Keys(screen, KeyEvent.OfChar('E'));

        // Assert
        await Assert.That(file.UnresolvedCount).IsEqualTo(0);
        await Assert.That(Text(screen)).Contains("Whole-file edit applied");
    }

    [Test]
    public async Task WholeFileEditThatKeepsMarkersCountsThem()
    {
        // Arrange
        var file = OneConflict();
        var (screen, host) = Start(file);
        host.Editor = text => "edited\n" + text;

        // Act
        Keys(screen, KeyEvent.OfChar('E'));

        // Assert
        await Assert.That(Text(screen)).Contains("still in it");
    }

    [Test]
    public async Task WholeFileEditLeftUnchangedAppliesNothing()
    {
        // Arrange
        var file = OneConflict();
        var (screen, host) = Start(file);
        host.Editor = text => text;

        // Act
        Keys(screen, KeyEvent.OfChar('E'));

        // Assert
        await Assert.That(file.WholeEdited).IsFalse();
    }

    [Test]
    [Arguments('e')]
    [Arguments('E')]
    public async Task AFailedEditorChangesNothing(char key)
    {
        // Arrange
        var file = OneConflict();
        var (screen, host) = Start(file);
        host.Editor = _ => null;

        // Act
        Keys(screen, KeyEvent.OfChar(key));

        // Assert
        await Assert.That(Text(screen)).Contains("The editor failed; nothing changed");
        await Assert.That(file.UnresolvedCount).IsEqualTo(1);
    }

    [Test]
    public async Task AHunkEditThatKeepsMarkersIsRefused()
    {
        // Arrange
        var file = OneConflict();
        var (screen, host) = Start(file);
        host.Editor = _ => "<<<<<<< main\nours\n=======\ntheirs\n>>>>>>> feature/x\n";

        // Act
        Keys(screen, KeyEvent.OfChar('e'));

        // Assert
        await Assert.That(Text(screen)).Contains("still has conflict markers");
        await Assert.That(file.UnresolvedCount).IsEqualTo(1);
    }

    [Test]
    public async Task AfterAWholeFileEditTakingASideIsRefused()
    {
        // Arrange
        var file = OneConflict();
        var (screen, host) = Start(file);
        host.Editor = text => "edited\n" + text;
        Keys(screen, KeyEvent.OfChar('E'));

        // Act
        Keys(screen, KeyEvent.OfChar('o'));

        // Assert
        await Assert.That(Text(screen)).Contains("press u to undo that edit first");
    }

    [Test]
    public async Task EditingABinaryFileDoesNothing()
    {
        // Arrange
        var file = MergeFile.Binary("logo.png", "main", "feature/x", [1], [2]);
        var (screen, host) = Start(file);
        bool asked = false;
        host.Editor = text => { asked = true; return text; };

        // Act
        Keys(screen, KeyEvent.OfChar('e'));

        // Assert
        await Assert.That(asked).IsFalse();
    }

    [Test]
    public async Task MouseWithoutAFrameOrWithTheRightButtonDoesNothing()
    {
        // Arrange
        var (screen, _) = Start();
        var frame = Render(screen, 140, 30);
        string before = frame.AllText();

        // Act
        screen.OnMouse(new MouseEvent(MouseKind.WheelDown, 10, 10), null);
        screen.OnMouse(new MouseEvent(MouseKind.Press, 10, 10, Button: 2), frame);
        screen.OnMouse(new MouseEvent(MouseKind.Release, 10, 10), frame);

        // Assert
        await Assert.That(Text(screen)).IsEqualTo(before);
    }

    [Test]
    public async Task TheWheelOverTheColumnsScrollsThem()
    {
        // Arrange
        var (a, _) = Start();
        var (b, _) = Start();
        var frame = Render(a, 140, 30);
        Render(b, 140, 30);

        // Act
        a.OnMouse(new MouseEvent(MouseKind.WheelDown, 10, 3), frame);
        Keys(b, KeyEvent.OfChar('j'), KeyEvent.OfChar('j'), KeyEvent.OfChar('j'));

        // Assert
        await Assert.That(Text(a)).IsEqualTo(Text(b));
    }

    [Test]
    public async Task TheWheelOverTheOutputScrollsOnlyIt()
    {
        // Arrange
        var (screen, _) = Start();
        var frame = Render(screen, 140, 30);
        string before = frame.AllText();

        // Act: the bottom row of the screen is in the output pane
        screen.OnMouse(new MouseEvent(MouseKind.WheelDown, 10, 27), frame);
        string scrolled = Text(screen);
        screen.OnMouse(new MouseEvent(MouseKind.WheelUp, 10, 27), frame);

        // Assert: down moved the output, up brought it back
        await Assert.That(scrolled).IsNotEqualTo(before);
        await Assert.That(Text(screen)).IsEqualTo(before);
    }
}
