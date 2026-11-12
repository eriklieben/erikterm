using ErikTerm.Ui;
using ErikTerm.Ui.Compare;

namespace ErikTerm.Tests;

/// <summary>The diff pane on its own: which keys it takes, the noise-rules popup, the filter cycle and the messages for what it can't compare.</summary>
public class ComparePaneTests
{
    private sealed class InlineBackground : IBackground
    {
        public void Run<T>(Func<T> work, Action<T> done) => done(work());
    }

    private static CompareSide Text(string title, string text) => new(title, "", TextDocument.FromText(text));

    private static CompareSide Binary(string title, int bytes) => new(title, "", TextDocument.Decode(new byte[bytes]));

    private static ComparePane Shown(CompareSide? left = null, CompareSide? right = null)
    {
        var pane = new ComparePane(new InlineBackground());
        pane.Show(left ?? Text("old", "1\n2\n3\n"), right ?? Text("new", "1\nb\n3\n"));
        return pane;
    }

    private static string Rendered(ComparePane pane)
    {
        var frame = new Frame(100, 20);
        pane.Render(frame, 0, 0, 100, 20);
        return frame.AllText();
    }

    [Test]
    [Arguments(TuiKey.Down)]
    [Arguments(TuiKey.Up)]
    [Arguments(TuiKey.PageDown)]
    [Arguments(TuiKey.PageUp)]
    [Arguments(TuiKey.Home)]
    [Arguments(TuiKey.End)]
    [Arguments(TuiKey.Left)]
    [Arguments(TuiKey.Right)]
    public async Task NavigationKeysAreThePanes(TuiKey key)
    {
        // Arrange
        var pane = Shown();

        // Act
        bool taken = pane.OnKey(new KeyEvent(key));

        // Assert
        await Assert.That(taken).IsTrue();
    }

    [Test]
    [Arguments(TuiKey.Tab, 0, false)]
    [Arguments(TuiKey.Esc, 0, false)]
    [Arguments(TuiKey.Char, 'z', false)]
    [Arguments(TuiKey.Char, 'z', true)]   // Ctrl+z
    public async Task OtherKeysAreLeftToTheHost(TuiKey key, int rune, bool ctrl)
    {
        // Arrange
        var pane = Shown();

        // Act
        bool taken = pane.OnKey(new KeyEvent(key, rune, ctrl));

        // Assert
        await Assert.That(taken).IsFalse();
    }

    [Test]
    [Arguments('d')]
    [Arguments('u')]
    public async Task CtrlDAndCtrlUScrollHalfAPage(char letter)
    {
        // Arrange
        var pane = Shown();

        // Act
        bool taken = pane.OnKey(KeyEvent.CtrlOf(letter));

        // Assert
        await Assert.That(taken).IsTrue();
    }

    [Test]
    public async Task RightThenLeftPansBack()
    {
        // Arrange
        var pane = Shown(Text("old", "abcdefghijklmnop\n"), Text("new", "abcdefghijklmnoX\n"));
        string start = Rendered(pane);
        pane.OnKey(KeyEvent.OfChar('l'));
        string panned = Rendered(pane);

        // Act
        pane.OnKey(new KeyEvent(TuiKey.Left));

        // Assert
        await Assert.That(panned).IsNotEqualTo(start);
        await Assert.That(Rendered(pane)).IsEqualTo(start);
    }

    [Test]
    public async Task DCyclesThroughTheFilters()
    {
        // Arrange
        var pane = Shown();
        var seen = new List<CompareFilter> { pane.Filter };

        // Act
        for (int i = 0; i < 3; i++)
        {
            pane.OnKey(KeyEvent.OfChar('d'));
            seen.Add(pane.Filter);
        }

        // Assert
        await Assert.That(seen).IsEquivalentTo(
            new[] { CompareFilter.All, CompareFilter.Differences, CompareFilter.Context, CompareFilter.All }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments('1')]
    [Arguments('w')]
    public async Task RulesPopupTogglesWhitespace(char key)
    {
        // Arrange
        var pane = Shown();
        pane.OnKey(KeyEvent.OfChar('i'));
        bool before = pane.Rules.Whitespace;

        // Act
        pane.OnKey(KeyEvent.OfChar(key));

        // Assert
        await Assert.That(pane.Rules.Whitespace).IsEqualTo(!before);
        await Assert.That(pane.RulesOpen).IsTrue();
    }

    [Test]
    [Arguments('2')]
    [Arguments('e')]
    public async Task RulesPopupTogglesLineEndings(char key)
    {
        // Arrange
        var pane = Shown();
        pane.OnKey(KeyEvent.OfChar('i'));
        bool before = pane.Rules.LineEndings;

        // Act
        pane.OnKey(KeyEvent.OfChar(key));

        // Assert
        await Assert.That(pane.Rules.LineEndings).IsEqualTo(!before);
    }

    [Test]
    [Arguments('3')]
    [Arguments('c')]
    public async Task RulesPopupTogglesComments(char key)
    {
        // Arrange
        var pane = Shown();
        pane.OnKey(KeyEvent.OfChar('i'));
        bool before = pane.Rules.Comments;

        // Act
        pane.OnKey(KeyEvent.OfChar(key));

        // Assert
        await Assert.That(pane.Rules.Comments).IsEqualTo(!before);
    }

    [Test]
    [Arguments(TuiKey.Esc, 0)]
    [Arguments(TuiKey.Enter, 0)]
    [Arguments(TuiKey.Char, 'i')]
    [Arguments(TuiKey.Char, 'q')]
    public async Task RulesPopupCloses(TuiKey key, int rune)
    {
        // Arrange
        var pane = Shown();
        pane.OnKey(KeyEvent.OfChar('i'));

        // Act
        bool taken = pane.OnKey(new KeyEvent(key, rune));

        // Assert
        await Assert.That(taken).IsTrue();
        await Assert.That(pane.RulesOpen).IsFalse();
    }

    [Test]
    public async Task RulesPopupSwallowsOtherNamedKeys()
    {
        // Arrange
        var pane = Shown();
        pane.OnKey(KeyEvent.OfChar('i'));

        // Act
        bool taken = pane.OnKey(new KeyEvent(TuiKey.Tab));

        // Assert
        await Assert.That(taken).IsTrue();
        await Assert.That(pane.RulesOpen).IsTrue();
    }

    [Test]
    public async Task RulesPopupLetsOtherLettersActNormally()
    {
        // Arrange
        var pane = Shown();
        pane.OnKey(KeyEvent.OfChar('i'));

        // Act: z is no key of the popup nor of the pane
        bool taken = pane.OnKey(KeyEvent.OfChar('z'));

        // Assert
        await Assert.That(taken).IsFalse();
        await Assert.That(pane.RulesOpen).IsTrue();
    }

    [Test]
    [Arguments(512L, "512 B")]
    [Arguments(2048L, "2.0 KB")]
    [Arguments(3L * 1024 * 1024, "3.0 MB")]
    public async Task SizesAreHumanReadable(long bytes, string expected)
    {
        // Act
        string text = ComparePane.FormatSize(bytes);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task ANewBinaryFileSaysSo()
    {
        // Arrange
        var pane = Shown(new CompareSide("old", "", TextDocument.FromText(""), Absent: true), Binary("new.png", 10));

        // Act
        string text = Rendered(pane);

        // Assert
        await Assert.That(text).Contains("new.png is a new binary file (10 B).");
    }

    [Test]
    public async Task ADeletedBinaryFileSaysSo()
    {
        // Arrange
        var pane = Shown(Binary("old.png", 10), new CompareSide("new", "", TextDocument.FromText(""), Absent: true));

        // Act
        string text = Rendered(pane);

        // Assert
        await Assert.That(text).Contains("old.png is a binary file that was deleted (10 B).");
    }

    [Test]
    public async Task BinaryOnOneSideAndTextOnTheOther()
    {
        // Arrange
        var pane = Shown(Text("old.txt", "text\n"), Binary("new.txt", 4));

        // Act
        string text = Rendered(pane);

        // Assert
        await Assert.That(text).Contains("new.txt is binary and old.txt is text.");
    }

    [Test]
    public async Task TextOnTheLeftAndBinaryOnTheRight()
    {
        // Arrange
        var pane = Shown(Binary("old.txt", 4), Text("new.txt", "text\n"));

        // Act
        string text = Rendered(pane);

        // Assert
        await Assert.That(text).Contains("old.txt is binary and new.txt is text.");
    }

    [Test]
    public async Task TwoBinaryFilesShowTheirSizes()
    {
        // Arrange
        var pane = Shown(Binary("a", 4), Binary("b", 2048));

        // Act
        string text = Rendered(pane);

        // Assert
        await Assert.That(text).Contains("Binary files: 4 B and 2.0 KB.");
    }

    [Test]
    public async Task ATooLargeFileSaysWhatTheLimitIs()
    {
        // Arrange
        var pane = Shown(new CompareSide("huge.log", "", TextDocument.TooLarge(200L * 1024 * 1024)), Text("new", "x\n"));

        // Act
        string text = Rendered(pane);

        // Assert
        await Assert.That(text).Contains("huge.log is too large to compare here (200.0 MB, the limit is 128.0 MB).");
    }

    [Test]
    public async Task ASideThatFailedToLoadShowsItsError()
    {
        // Arrange
        var pane = Shown(CompareSide.Failed("old", "no such revision"), Text("new", "x\n"));

        // Act
        string text = Rendered(pane);

        // Assert
        await Assert.That(text).Contains("old: no such revision");
    }

    [Test]
    public async Task AMarkWithoutAnyRowsSelectsNothing()
    {
        // Arrange: identical sides in the differences filter have no rows to stand on
        var pane = Shown(Text("old", "same\n"), Text("new", "same\n"));
        pane.OnKey(KeyEvent.OfChar('d'));

        // Act
        pane.ToggleMark();

        // Assert
        await Assert.That(pane.SelectedRows()).IsNull();
    }
}
