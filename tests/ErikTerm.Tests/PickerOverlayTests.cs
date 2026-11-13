using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>Quick select's labels: which key picks which match, pasting, opening, and vim's hints.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class QuickSelectTests
{
    private static QuickSelect.Item Item(string text) => new() { Text = text, Kind = MatchKind.Path, Segments = [], Cells = [] };

    private static (QuickSelect Select, List<QuickSelect.Item> Items, Pane Pane) Open(int count, QuickSelectPurpose purpose = QuickSelectPurpose.Copy)
    {
        var pane = TestPanes.Spawn();
        var items = Enumerable.Range(0, count).Select(i => Item("match" + i)).ToList();
        var select = new QuickSelect();
        select.Open(pane, items, new Theme(), 0, purpose);
        return (select, items, pane);
    }

    [Test]
    public async Task TheNewestMatchGetsTheFirstLabel()
    {
        // Arrange
        var (_, items, pane) = Open(3);
        using var _ = pane;

        // Act
        var labels = items.Select(i => i.Label).ToList();

        // Assert
        await Assert.That(labels).IsEquivalentTo(["d", "s", "a"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TypingALabelPicksItsMatchAndCloses()
    {
        // Arrange
        var (select, items, pane) = Open(3);
        using var _ = pane;

        // Act
        var chosen = select.HandleKey('s', shift: false, 0);

        // Assert
        await Assert.That(chosen!.Value.Item).IsSameReferenceAs(items[1]);
        await Assert.That(chosen.Value.Paste).IsFalse();
        await Assert.That(select.IsOpen).IsFalse();
        await Assert.That(select.Pane).IsSameReferenceAs(pane);
    }

    [Test]
    public async Task ShiftAlsoPastes()
    {
        // Arrange
        var (select, _, pane) = Open(1);
        using var _ = pane;

        // Act
        var chosen = select.HandleKey('a', shift: true, 0);

        // Assert
        await Assert.That(chosen!.Value.Paste).IsTrue();
        await Assert.That(select.ChosenToOpen).IsFalse();
    }

    [Test]
    public async Task CtrlOpensInsteadOfPasting()
    {
        // Arrange
        var (select, _, pane) = Open(1);
        using var _ = pane;

        // Act
        var chosen = select.HandleKey('a', shift: true, 0, ctrl: true);

        // Assert
        await Assert.That(chosen!.Value.Paste).IsFalse();
        await Assert.That(select.ChosenToOpen).IsTrue();
    }

    [Test]
    public async Task AKeyThatIsNoLabelIsIgnored()
    {
        // Arrange
        var (select, _, pane) = Open(2);
        using var _ = pane;

        // Act
        var letter = select.HandleKey('z', shift: false, 0);
        var digit = select.HandleKey('1', shift: false, 0);

        // Assert
        await Assert.That(letter).IsNull();
        await Assert.That(digit).IsNull();
        await Assert.That(select.IsOpen).IsTrue();
    }

    [Test]
    public async Task TwoLetterLabelsWaitForTheSecondLetter()
    {
        // Arrange: more matches than letters.
        var (select, items, pane) = Open(30);
        using var _ = pane;
        string label = items.First(i => i.Label.Length == 2).Label;

        // Act
        var first = select.HandleKey(label[0], shift: false, 0);
        var second = select.HandleKey(label[1], shift: false, 0);

        // Assert
        await Assert.That(first).IsNull();
        await Assert.That(second!.Value.Item.Label).IsEqualTo(label);
    }

    [Test]
    public async Task BackspaceUndoesALetterThenCloses()
    {
        // Arrange
        var (select, items, pane) = Open(30);
        using var _ = pane;
        string label = items.First(i => i.Label.Length == 2).Label;
        select.HandleKey(label[0], shift: false, 0);

        // Act
        select.HandleKey(Sdl.K_BACKSPACE, shift: false, 0);
        bool openAfterOne = select.IsOpen;
        select.HandleKey(Sdl.K_BACKSPACE, shift: false, 0);

        // Assert
        await Assert.That(openAfterOne).IsTrue();
        await Assert.That(select.IsOpen).IsFalse();
    }

    [Test]
    public async Task EscapeCancels()
    {
        // Arrange
        var (select, _, pane) = Open(1);
        using var _ = pane;

        // Act
        var result = select.HandleKey(Sdl.K_ESCAPE, shift: false, 0);

        // Assert
        await Assert.That(result).IsNull();
        await Assert.That(select.IsOpen).IsFalse();
        await Assert.That(select.HandleKey('a', shift: false, 0)).IsNull();
    }

    [Test]
    [Arguments(Sdl.K_RETURN, false)]
    [Arguments((uint)'o', true)]
    public async Task InVimHintsEnterOrCtrlOArmsOpening(uint key, bool ctrl)
    {
        // Arrange
        var (select, _, pane) = Open(1, QuickSelectPurpose.VimHints);
        using var _ = pane;

        // Act
        select.HandleKey(key, shift: false, 0, ctrl);
        var chosen = select.HandleKey('a', shift: false, 0);

        // Assert
        await Assert.That(select.Purpose).IsEqualTo(QuickSelectPurpose.VimHints);
        await Assert.That(chosen).IsNotNull();
        await Assert.That(select.ChosenToOpen).IsTrue();
    }

    [Test]
    public async Task ArmingTwiceDisarms()
    {
        // Arrange
        var (select, _, pane) = Open(1, QuickSelectPurpose.VimHints);
        using var _ = pane;

        // Act
        select.HandleKey(Sdl.K_RETURN, shift: false, 0);
        select.HandleKey(Sdl.K_KP_ENTER, shift: false, 0);
        select.HandleKey('a', shift: false, 0);

        // Assert
        await Assert.That(select.ChosenToOpen).IsFalse();
    }

    [Test]
    public async Task EnterIsNoArmKeyWhenCopying()
    {
        // Arrange
        var (select, _, pane) = Open(1);
        using var _ = pane;

        // Act
        select.HandleKey(Sdl.K_RETURN, shift: false, 0);
        select.HandleKey('a', shift: false, 0);

        // Assert
        await Assert.That(select.ChosenToOpen).IsFalse();
    }

    [Test]
    public async Task AClickOnAMatchPicksIt()
    {
        // Arrange
        var (select, items, pane) = Open(2);
        using var _ = pane;
        items[0].Hit.Add(new RectF(10, 10, 50, 20));

        // Act
        int hit = select.HitTest(20, 15);
        var chosen = select.HandleMouseDown(20, 15, shift: true, 0);

        // Assert
        await Assert.That(hit).IsEqualTo(0);
        await Assert.That(chosen!.Value.Item).IsSameReferenceAs(items[0]);
        await Assert.That(chosen.Value.Paste).IsTrue();
    }

    [Test]
    public async Task AClickElsewhereCancels()
    {
        // Arrange
        var (select, _, pane) = Open(2);
        using var _ = pane;

        // Act
        var chosen = select.HandleMouseDown(500, 500, shift: false, 0);

        // Assert
        await Assert.That(chosen).IsNull();
        await Assert.That(select.IsOpen).IsFalse();
        await Assert.That(select.HandleMouseDown(1, 1, shift: false, 0)).IsNull();
    }

    [Test]
    public async Task ItAnimatesWhileTheLabelsPopIn()
    {
        // Arrange
        var (select, _, pane) = Open(2);
        using var _ = pane;

        // Act
        bool opening = select.IsAnimating(1);
        bool later = select.IsAnimating(60_000);

        // Assert
        await Assert.That(opening).IsTrue();
        await Assert.That(later).IsFalse();
    }
}

/// <summary>The pane picker: a letter per pane, single and multiple picks.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class PaneSelectTests
{
    [Test]
    public async Task ALetterPicksItsPane()
    {
        // Arrange
        using var a = TestPanes.Spawn();
        using var b = TestPanes.Spawn();
        var select = new PaneSelect();
        Pane? picked = null;
        select.Selected = p => picked = p;
        select.Open([a, b], new Theme(), 0);

        // Act: the second pane's letter.
        bool handled = select.HandleKey('s', 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(picked).IsSameReferenceAs(b);
        await Assert.That(select.IsOpen).IsFalse();
    }

    [Test]
    public async Task OtherKeysAreSwallowed()
    {
        // Arrange
        using var a = TestPanes.Spawn();
        var select = new PaneSelect();
        select.Open([a], new Theme(), 0);

        // Act
        bool handled = select.HandleKey('z', 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(select.IsOpen).IsTrue();
        await Assert.That(select.IsVisible(0)).IsTrue();
    }

    [Test]
    public async Task EscapeCancelsAndAClosedPickerTakesNothing()
    {
        // Arrange
        using var a = TestPanes.Spawn();
        var select = new PaneSelect();
        select.Open([a], new Theme(), 0);

        // Act
        select.HandleKey(Sdl.K_ESCAPE, 0);
        bool key = select.HandleKey('a', 0);
        bool click = select.HandleMouseDown(1, 1, 0);

        // Assert
        await Assert.That(select.IsOpen).IsFalse();
        await Assert.That(key || click).IsFalse();
    }

    [Test]
    public async Task SeveralPanesAreToggledAndEnterHandsThemOver()
    {
        // Arrange
        using var a = TestPanes.Spawn();
        using var b = TestPanes.Spawn();
        using var c = TestPanes.Spawn();
        var select = new PaneSelect();
        List<Pane>? chosen = null;
        select.OpenMulti([a, b, c], new Theme(), 0, list => chosen = list);

        // Act: a and c on, b on and off again.
        foreach (char key in "asds") select.HandleKey(key, 0);
        select.HandleKey(Sdl.K_RETURN, 0);

        // Assert: by identity. Equivalence compares member by member, and a live pane's members (its process's name)
        // can change between two reads of the same pane.
        await Assert.That(chosen!.Count).IsEqualTo(2);
        await Assert.That(chosen[0]).IsSameReferenceAs(a);
        await Assert.That(chosen[1]).IsSameReferenceAs(c);
        await Assert.That(select.IsOpen).IsFalse();
    }

    [Test]
    public async Task AClickOnAPanePicksIt()
    {
        // Arrange
        using var a = TestPanes.Spawn();
        using var b = TestPanes.Spawn();
        a.Target = new RectI(0, 0, 100, 100);
        b.Target = new RectI(100, 0, 100, 100);
        var select = new PaneSelect();
        Pane? picked = null;
        select.Selected = p => picked = p;
        select.Open([a, b], new Theme(), 0);

        // Act
        select.HandleMouseDown(150, 50, 0);

        // Assert
        await Assert.That(picked).IsSameReferenceAs(b);
    }

    [Test]
    public async Task AClickTogglesInAMultiplePick()
    {
        // Arrange
        using var a = TestPanes.Spawn();
        a.Target = new RectI(0, 0, 100, 100);
        var select = new PaneSelect();
        List<Pane>? chosen = null;
        select.OpenMulti([a], new Theme(), 0, list => chosen = list);

        // Act
        select.HandleMouseDown(50, 50, 0);
        select.HandleKey(Sdl.K_KP_ENTER, 0);

        // Assert (by identity: see SeveralPanesAreToggledAndEnterHandsThemOver)
        await Assert.That(chosen!.Count).IsEqualTo(1);
        await Assert.That(chosen[0]).IsSameReferenceAs(a);
    }

    [Test]
    public async Task AClickBesideEveryPaneCancels()
    {
        // Arrange
        using var a = TestPanes.Spawn();
        a.Target = new RectI(0, 0, 100, 100);
        var select = new PaneSelect();
        bool picked = false;
        select.Selected = _ => picked = true;
        select.Open([a], new Theme(), 0);

        // Act
        bool handled = select.HandleMouseDown(500, 500, 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(picked).IsFalse();
        await Assert.That(select.IsOpen).IsFalse();
    }
}

/// <summary>The context menu's keys: stepping over separators and disabled items, and running one.</summary>
public class ContextMenuTests
{
    private static (ContextMenu Menu, List<string> Ran) Open(params ContextMenu.Item[] items)
    {
        var menu = new ContextMenu();
        menu.Open([.. items], 10, 10, new Theme(), 0);
        return (menu, []);
    }

    private static ContextMenu.Item Item(List<string> ran, string label, bool enabled = true) => new(label, 0, null, () => ran.Add(label), enabled);

    [Test]
    public async Task DownThenEnterRunsTheFirstItem()
    {
        // Arrange
        var ran = new List<string>();
        var menu = new ContextMenu();
        menu.Open([Item(ran, "Copy"), Item(ran, "Paste")], 10, 10, new Theme(), 0);

        // Act
        menu.HandleKey(Sdl.K_DOWN, 0);
        menu.HandleKey(Sdl.K_RETURN, 0);

        // Assert
        await Assert.That(ran).IsEquivalentTo(["Copy"]);
        await Assert.That(menu.IsOpen).IsFalse();
    }

    [Test]
    public async Task SteppingSkipsSeparatorsAndDisabledItems()
    {
        // Arrange
        var ran = new List<string>();
        var menu = new ContextMenu();
        menu.Open([Item(ran, "Copy"), ContextMenu.Item.Separator, Item(ran, "Off", enabled: false), Item(ran, "Paste")], 10, 10, new Theme(), 0);

        // Act
        menu.HandleKey(Sdl.K_TAB, 0);
        menu.HandleKey(Sdl.K_DOWN, 0);
        menu.HandleKey(Sdl.K_SPACE, 0);

        // Assert
        await Assert.That(ran).IsEquivalentTo(["Paste"]);
    }

    [Test]
    public async Task UpFromNothingGoesToTheLastItemAndWraps()
    {
        // Arrange
        var ran = new List<string>();
        var menu = new ContextMenu();
        menu.Open([Item(ran, "Copy"), Item(ran, "Paste")], 10, 10, new Theme(), 0);

        // Act
        menu.HandleKey(Sdl.K_UP, 0);
        menu.HandleKey(Sdl.K_UP, 0);
        menu.HandleKey(Sdl.K_UP, 0);
        menu.HandleKey(Sdl.K_KP_ENTER, 0);

        // Assert
        await Assert.That(ran).IsEquivalentTo(["Paste"]);
    }

    [Test]
    public async Task EnterWithNothingHighlightedRunsNothing()
    {
        // Arrange
        var ran = new List<string>();
        var menu = new ContextMenu();
        menu.Open([Item(ran, "Copy")], 10, 10, new Theme(), 0);

        // Act
        bool handled = menu.HandleKey(Sdl.K_RETURN, 0);

        // Assert
        await Assert.That(handled).IsTrue();
        await Assert.That(ran).IsEmpty();
        await Assert.That(menu.IsOpen).IsTrue();
    }

    [Test]
    public async Task EscapeClosesAndAClosedMenuTakesNothing()
    {
        // Arrange
        var (menu, _) = Open(new ContextMenu.Item("Copy", 0, null, null));

        // Act
        menu.HandleKey(Sdl.K_ESCAPE, 0);

        // Assert
        await Assert.That(menu.IsOpen).IsFalse();
        await Assert.That(menu.HandleKey(Sdl.K_DOWN, 0)).IsFalse();
        await Assert.That(menu.HandleMouseMove(1, 1, 0)).IsFalse();
        await Assert.That(menu.HandleMouseDown(1, 1, 0)).IsFalse();
        await Assert.That(menu.HandleMouseUp(1, 1, 0)).IsFalse();
    }

    [Test]
    public async Task AClickOutsideClosesAndLetsTheClickThrough()
    {
        // Arrange: not drawn, so every point is outside.
        var (menu, _) = Open(new ContextMenu.Item("Copy", 0, null, null));

        // Act
        bool taken = menu.HandleMouseDown(500, 500, 0);

        // Assert
        await Assert.That(taken).IsFalse();
        await Assert.That(menu.IsOpen).IsFalse();
    }

    [Test]
    public async Task AnEmptyMenuStepsNowhere()
    {
        // Arrange
        var (menu, _) = Open();

        // Act
        menu.HandleKey(Sdl.K_DOWN, 0);
        bool moved = menu.HandleMouseMove(1, 1, 0);
        bool up = menu.HandleMouseUp(1, 1, 0);

        // Assert
        await Assert.That(moved && up).IsTrue();
        await Assert.That(menu.IsOpen).IsTrue();
    }

    [Test]
    public async Task ASeparatorIsKnownAsOne()
    {
        // Act + Assert
        await Assert.That(ContextMenu.Item.Separator.IsSeparator).IsTrue();
        await Assert.That(new ContextMenu.Item("", 0, null, null, false).IsSeparator).IsFalse();
    }
}

/// <summary>Toasts: duplicates replace each other, and a toast's action runs from a click on it.</summary>
public class ToastsTests
{
    [Test]
    public async Task ToastsAnimateWhileShownAndAClickMissesWhenNothingIsDrawn()
    {
        // Arrange
        var toasts = new Toasts();
        bool ran = false;
        toasts.Show(ToastKind.Info, "Saved", null, new Theme(), 0, "Open", () => ran = true);

        // Act: never drawn, so it has no rectangle to click.
        bool clicked = toasts.HandleMouseDown(5, 5, new Theme(), 10);

        // Assert
        await Assert.That(toasts.IsAnimating()).IsTrue();
        await Assert.That(clicked).IsFalse();
        await Assert.That(ran).IsFalse();
    }

    [Test]
    public async Task AToastWithoutAnActionIsNotClickable()
    {
        // Arrange
        var toasts = new Toasts();
        toasts.Show(ToastKind.Error, "Failed", "detail", new Theme(), 0, "Retry", null);

        // Act
        bool clicked = toasts.HandleMouseDown(0, 0, new Theme(), 10);

        // Assert
        await Assert.That(clicked).IsFalse();
    }
}
