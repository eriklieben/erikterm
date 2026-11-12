using ErikTerm.App.Native;
using ErikTerm.App.Search;
using ErikTerm.App.Ui;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

/// <summary>The "search all panes" list's field and selection: its option keys, deleting, moving over group headers.</summary>
// Internal: PaneSearchKey (an app type) is a test parameter.
internal class PaneSearchOverlayKeyTests
{
    private const ushort Ctrl = 0x0040, Alt = 0x0100, Shift = 0x0001;

    private static PaneSearchOverlay<string> Open(string query = "")
    {
        var overlay = new PaneSearchOverlay<string>();
        overlay.Open(new Theme(), 0);
        if (query.Length > 0) overlay.SetQuery(query);
        overlay.ConsumeQueryChange();
        return overlay;
    }

    private static PaneSearchHit Hit(int line) => new(new SearchMatch(line, 0, line, 3), "abc", 0, 3, 0, 0, false);

    /// <summary>Two panes' groups: "a" with hits on lines 1 and 2, "b" with a hit on line 3.</summary>
    private static PaneSearchResults<string> Results()
    {
        var term = new Terminal(10, 3);
        PaneSearchGroup<string> Group(string pane, params int[] lines) =>
            new(new PaneSearchTarget<string>(pane, term, pane, ""), [.. lines.Select(Hit)], false);
        return new([Group("a", 1, 2), Group("b", 3)], 3, 2, 2, true, false, false, false, null);
    }

    private static long SelectedLine(PaneSearchOverlay<string> overlay) => overlay.SelectedHit!.Value.Hit.Match.StartLine;

    [Test]
    public async Task AltCTogglesCase()
    {
        // Arrange
        var overlay = Open("x");

        // Act
        var result = overlay.HandleKey('c', Alt, 0);

        // Assert
        await Assert.That(result).IsEqualTo(PaneSearchKey.Handled);
        await Assert.That(overlay.Query.CaseSensitive).IsTrue();
        await Assert.That(overlay.ConsumeQueryChange()).IsTrue();
    }

    [Test]
    public async Task AltRTogglesRegex()
    {
        // Arrange
        var overlay = Open("x");

        // Act
        overlay.HandleKey('r', Alt, 0);

        // Assert
        await Assert.That(overlay.Query.Regex).IsTrue();
    }

    [Test]
    public async Task CtrlUClearsTheQuery()
    {
        // Arrange
        var overlay = Open("some words");

        // Act
        overlay.HandleKey('u', Ctrl, 0);

        // Assert
        await Assert.That(overlay.Query.Pattern).IsEqualTo("");
    }

    [Test]
    [Arguments('c', (ushort)(Ctrl | Alt))]   // Ctrl+Alt+C isn't the case toggle
    [Arguments('r', (ushort)(Ctrl | Alt))]
    [Arguments('u', (ushort)(Ctrl | Shift))]
    [Arguments('x', Ctrl)]
    public async Task OtherChordsAreNotTheLists(char key, ushort mod)
    {
        // Arrange
        var overlay = Open("q");

        // Act
        var result = overlay.HandleKey(key, mod, 0);

        // Assert
        await Assert.That(result).IsEqualTo(PaneSearchKey.NotHandled);
        await Assert.That(overlay.Query.Pattern).IsEqualTo("q");
    }

    [Test]
    [Arguments("abc", (ushort)0, "ab")]
    [Arguments("a😀", (ushort)0, "a")]                // a surrogate pair as one
    [Arguments("one two  ", Ctrl, "one ")]            // Ctrl: the last word
    [Arguments("word", Ctrl, "")]
    public async Task BackspaceDeletes(string query, ushort mod, string left)
    {
        // Arrange
        var overlay = Open(query);

        // Act
        overlay.HandleKey(Sdl.K_BACKSPACE, mod, 0);

        // Assert
        await Assert.That(overlay.Query.Pattern).IsEqualTo(left);
    }

    [Test]
    public async Task BackspaceOnNothingChangesNothing()
    {
        // Arrange
        var overlay = Open();

        // Act
        overlay.HandleKey(Sdl.K_BACKSPACE, 0, 0);

        // Assert
        await Assert.That(overlay.ConsumeQueryChange()).IsFalse();
    }

    [Test]
    public async Task TypedTextKeepsToOneLine()
    {
        // Arrange
        var overlay = Open("a");

        // Act
        overlay.Paste("b\r\nc", 0);

        // Assert
        await Assert.That(overlay.Query.Pattern).IsEqualTo("ab c");
    }

    [Test]
    [Arguments(Sdl.K_RETURN)]
    [Arguments(Sdl.K_KP_ENTER)]
    public async Task EnterWithoutAMatchDoesNothing(uint key)
    {
        // Arrange
        var overlay = Open("x");

        // Act
        var result = overlay.HandleKey(key, 0, 0);

        // Assert
        await Assert.That(result).IsEqualTo(PaneSearchKey.Handled);
    }

    [Test]
    public async Task EnterWithAMatchActivatesIt()
    {
        // Arrange
        var overlay = Open("x");
        overlay.SetResults(Results());

        // Act
        var result = overlay.HandleKey(Sdl.K_RETURN, 0, 0);

        // Assert
        await Assert.That(result).IsEqualTo(PaneSearchKey.Activate);
    }

    [Test]
    public async Task EscapeCloses()
    {
        // Arrange
        var overlay = Open("x");

        // Act
        var result = overlay.HandleKey(Sdl.K_ESCAPE, 0, 0);

        // Assert
        await Assert.That(result).IsEqualTo(PaneSearchKey.Close);
    }

    [Test]
    public async Task ResultsSelectTheFirstMatchNotItsHeader()
    {
        // Arrange
        var overlay = Open("x");

        // Act
        overlay.SetResults(Results());

        // Assert
        await Assert.That(SelectedLine(overlay)).IsEqualTo(1);
    }

    [Test]
    public async Task DownSkipsTheNextGroupsHeader()
    {
        // Arrange
        var overlay = Open("x");
        overlay.SetResults(Results());

        // Act
        overlay.HandleKey(Sdl.K_DOWN, 0, 0);
        overlay.HandleKey(Sdl.K_DOWN, 0, 0);

        // Assert
        await Assert.That(SelectedLine(overlay)).IsEqualTo(3);
    }

    [Test]
    public async Task PageKeysStopAtTheEnds()
    {
        // Arrange
        var overlay = Open("x");
        overlay.SetResults(Results());

        // Act
        overlay.HandleKey(Sdl.K_PAGEDOWN, 0, 0);
        long last = SelectedLine(overlay);
        overlay.HandleKey(Sdl.K_PAGEUP, 0, 0);

        // Assert
        await Assert.That(last).IsEqualTo(3);
        await Assert.That(SelectedLine(overlay)).IsEqualTo(1);
    }

    [Test]
    public async Task TheWheelMovesTheSelection()
    {
        // Arrange
        var overlay = Open("x");
        overlay.SetResults(Results());

        // Act: one notch down
        overlay.HandleWheel(-1);

        // Assert
        await Assert.That(SelectedLine(overlay)).IsEqualTo(2);
    }

    [Test]
    public async Task MovingWithoutResultsDoesNothing()
    {
        // Arrange
        var overlay = Open("x");

        // Act
        overlay.HandleKey(Sdl.K_UP, 0, 0);

        // Assert
        await Assert.That(overlay.SelectedHit).IsNull();
    }

    [Test]
    public async Task NewResultsKeepTheSelectedMatch()
    {
        // Arrange
        var overlay = Open("x");
        var results = Results();
        overlay.SetResults(results);
        overlay.HandleKey(Sdl.K_DOWN, 0, 0);

        // Act: the same hits again, as a rescan of unchanged panes gives
        overlay.SetResults(results with { Searched = 2 });

        // Assert
        await Assert.That(SelectedLine(overlay)).IsEqualTo(2);
    }

    [Test]
    public async Task SelectFirstOnNextResultsStartsOver()
    {
        // Arrange
        var overlay = Open("x");
        var results = Results();
        overlay.SetResults(results);
        overlay.HandleKey(Sdl.K_DOWN, 0, 0);
        overlay.SelectFirstOnNextResults();

        // Act
        overlay.SetResults(results);

        // Assert
        await Assert.That(SelectedLine(overlay)).IsEqualTo(1);
    }

    [Test]
    public async Task AClickOutsideTheListCloses()
    {
        // Arrange: never drawn, so no point is on the panel
        var overlay = Open("x");

        // Act
        var result = overlay.HandleMouseDown(5, 5, 0);

        // Assert
        await Assert.That(result).IsEqualTo(PaneSearchKey.Close);
    }

    [Test]
    public async Task ClosingTwiceIsHarmless()
    {
        // Arrange
        var overlay = Open("x");
        overlay.Close(0);

        // Act
        overlay.Close(10);

        // Assert
        await Assert.That(overlay.IsOpen).IsFalse();
    }
}
