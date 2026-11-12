using ErikTerm.Ui.Compare;

namespace ErikTerm.Tests;

/// <summary>The review comment editor's caret, editing keys and limits, and the wrapping it moves by.</summary>
public class CommentEditorTests
{
    private static readonly KeyEvent Left = new(TuiKey.Left), Right = new(TuiKey.Right), Up = new(TuiKey.Up), Down = new(TuiKey.Down);

    private static CommentEditor At(string text, int caret, int width = 20)
    {
        var editor = new CommentEditor(text);
        while (editor.Caret > caret) editor.OnKey(Left, width);
        return editor;
    }

    [Test]
    public async Task LeftStopsAtTheStart()
    {
        // Arrange
        var editor = At("ab", 0);

        // Act
        editor.OnKey(Left, 20);

        // Assert
        await Assert.That(editor.Caret).IsEqualTo(0);
    }

    [Test]
    public async Task RightStopsAtTheEnd()
    {
        // Arrange
        var editor = new CommentEditor("ab");

        // Act
        editor.OnKey(Right, 20);

        // Assert
        await Assert.That(editor.Caret).IsEqualTo(2);
    }

    [Test]
    public async Task RightStepsOverASurrogatePair()
    {
        // Arrange
        var editor = At("😀b", 0);

        // Act
        editor.OnKey(Right, 20);

        // Assert
        await Assert.That(editor.Caret).IsEqualTo(2);
    }

    [Test]
    public async Task DeleteRemovesTheCharacterAfterTheCaret()
    {
        // Arrange
        var editor = At("a😀b", 1);

        // Act
        editor.OnKey(new KeyEvent(TuiKey.Delete), 20);

        // Assert
        await Assert.That((editor.Text, editor.Caret)).IsEqualTo(("ab", 1));
    }

    [Test]
    public async Task DeleteAtTheEndRemovesNothing()
    {
        // Arrange
        var editor = new CommentEditor("ab");

        // Act
        editor.OnKey(new KeyEvent(TuiKey.Delete), 20);

        // Assert
        await Assert.That(editor.Text).IsEqualTo("ab");
        await Assert.That(editor.Changed).IsFalse();
    }

    [Test]
    public async Task BackspaceAtTheStartRemovesNothing()
    {
        // Arrange
        var editor = At("ab", 0);

        // Act
        editor.OnKey(new KeyEvent(TuiKey.Backspace), 20);

        // Assert
        await Assert.That(editor.Text).IsEqualTo("ab");
    }

    [Test]
    public async Task UpAndDownKeepTheColumnAcrossLines()
    {
        // Arrange: the caret after "ab" on the second line
        var editor = At("first\nabcdef", 8);

        // Act
        editor.OnKey(Up, 20);
        int up = editor.Caret;
        editor.OnKey(Down, 20);

        // Assert
        await Assert.That(up).IsEqualTo(2);
        await Assert.That(editor.Caret).IsEqualTo(8);
    }

    [Test]
    public async Task DownOntoAShorterLineStopsAtItsEnd()
    {
        // Arrange
        var editor = At("abcdef\nab", 5);

        // Act
        editor.OnKey(Down, 20);

        // Assert
        await Assert.That(editor.Caret).IsEqualTo(9);
    }

    [Test]
    public async Task UpOnTheFirstRowAndDownOnTheLastStay()
    {
        // Arrange
        var editor = At("one\ntwo", 1);

        // Act
        editor.OnKey(Up, 20);
        int afterUp = editor.Caret;
        editor.OnKey(Down, 20);
        editor.OnKey(Down, 20);

        // Assert
        await Assert.That(afterUp).IsEqualTo(1);
        await Assert.That(editor.Caret).IsEqualTo(5);
    }

    [Test]
    public async Task HomeAndEndGoToTheWrappedRowsEdges()
    {
        // Arrange: "hello world" at width 6 wraps after "hello "
        var editor = At("hello world", 8, width: 6);

        // Act
        editor.OnKey(new KeyEvent(TuiKey.Home), 6);
        int home = editor.Caret;
        editor.OnKey(new KeyEvent(TuiKey.End), 6);

        // Assert
        await Assert.That(home).IsEqualTo(6);
        await Assert.That(editor.Caret).IsEqualTo(11);
    }

    [Test]
    public async Task CtrlNInsertsANewline()
    {
        // Arrange
        var editor = At("ab", 1);

        // Act
        editor.OnKey(KeyEvent.CtrlOf('n'), 20);

        // Assert
        await Assert.That(editor.Text).IsEqualTo("a\nb");
    }

    [Test]
    [Arguments(TuiKey.Char, 'x', true, false)]    // Ctrl+x: not text
    [Arguments(TuiKey.Char, 'x', false, true)]    // Alt+x
    [Arguments(TuiKey.Char, 0x07, false, false)]  // a control character
    [Arguments(TuiKey.Tab, 0, false, false)]
    public async Task KeysThatArentEditingAreLeftToTheScreen(TuiKey key, int rune, bool ctrl, bool alt)
    {
        // Arrange
        var editor = new CommentEditor("ab");

        // Act
        bool handled = editor.OnKey(new KeyEvent(key, rune, ctrl, alt), 20);

        // Assert
        await Assert.That(handled).IsFalse();
        await Assert.That(editor.Text).IsEqualTo("ab");
    }

    [Test]
    public async Task InsertCleansTabsAndControlCharacters()
    {
        // Arrange
        var editor = new CommentEditor("");

        // Act
        editor.Insert("a\tb\r\nc\rd\u0007");

        // Assert
        await Assert.That(editor.Text).IsEqualTo("a    b\nc\nd");
    }

    [Test]
    public async Task InsertStopsAtTheLimit()
    {
        // Arrange
        var editor = new CommentEditor(new string('a', CommentEditor.MaxChars - 2));

        // Act
        editor.Insert("bcd");
        editor.Insert("e");

        // Assert
        await Assert.That(editor.Text.Length).IsEqualTo(CommentEditor.MaxChars);
        await Assert.That(editor.Text[^2..]).IsEqualTo("bc");
    }

    [Test]
    public async Task WrapBreaksAWordLongerThanTheRow()
    {
        // Act
        var rows = TuiText.Wrap("abcdefgh", 3);

        // Assert
        await Assert.That(rows).IsEquivalentTo(new[] { (0, 3), (3, 6), (6, 8) }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WrapBreaksAfterTheLastSpace()
    {
        // Act
        var rows = TuiText.Wrap("ab cd ef", 6);

        // Assert
        await Assert.That(rows).IsEquivalentTo(new[] { (0, 6), (6, 8) }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WrapCountsWideCharactersAsTwoCells()
    {
        // Act
        var rows = TuiText.Wrap("中文字", 4);

        // Assert
        await Assert.That(rows).IsEquivalentTo(new[] { (0, 2), (2, 3) }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WrapOfNothingIsOneEmptyRow()
    {
        // Act
        var rows = TuiText.Wrap("", 0);

        // Assert
        await Assert.That(rows).IsEquivalentTo(new[] { (0, 0) }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WidthCountsCells()
    {
        // Act
        int cells = TuiText.Width("a中😀");

        // Assert
        await Assert.That(cells).IsEqualTo(5);
    }
}
