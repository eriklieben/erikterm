using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>The find bar's query editing, its modes, the current match, stepping, revealing, and vim's search.</summary>
[RunOn(OS.Linux | OS.MacOs)]
[NotInParallel(Isolation.Processes)]
public class SearchBarTests
{
    private const string Text = "foo bar\r\nbaz foo\r\nFoo end\r\n";

    private sealed class Harness : IDisposable
    {
        public readonly Pane Pane;
        public readonly SearchBar Bar = new();
        public Terminal Term => Pane.Term;

        public Harness(string text = Text, int rows = 6)
        {
            Pane = TestPanes.Spawn(40, rows, text);
            Bar.Open(Pane, null, new Theme(), Term.ViewTopLine + Term.Rows - 1, Term.Columns, 0);
        }

        public SearchKeyResult Key(uint key, ushort mod = 0) => Bar.HandleKey(key, mod, 0);
        public SearchMatch? Search(string query)
        {
            Bar.HandleText(query, 0);
            return Update();
        }
        public SearchMatch? Update()
        {
            var reveal = Bar.Update(Term, 0);
            Bar.CompleteScan(Term);
            return reveal;
        }
        public void Dispose() => Pane.Dispose();
    }

    [Test]
    public async Task TypingFindsEveryMatchIgnoringCase()
    {
        // Arrange
        using var h = new Harness();

        // Act
        h.Search("foo");

        // Assert
        await Assert.That(h.Bar.Engine.Matches.Count).IsEqualTo(3);
        await Assert.That(h.Bar.Query).IsEqualTo("foo");
    }

    [Test]
    public async Task TheCurrentMatchIsTheLastOneAboveTheCursor()
    {
        // Arrange
        using var h = new Harness();

        // Act
        h.Search("foo");

        // Assert: the anchor is the bottom of the screen, so the newest match.
        await Assert.That(h.Bar.Current).IsEqualTo(h.Bar.Engine.Matches[^1]);
    }

    [Test]
    public async Task AltCMakesItCaseSensitive()
    {
        // Arrange
        using var h = new Harness();
        h.Search("foo");

        // Act
        var result = h.Key('c', Sdl.KMOD_LALT);
        h.Update();

        // Assert
        await Assert.That(result).IsEqualTo(SearchKeyResult.Handled);
        await Assert.That(h.Bar.Engine.CaseSensitive).IsTrue();
        await Assert.That(h.Bar.Engine.Matches.Count).IsEqualTo(2);
    }

    [Test]
    public async Task AltRTurnsOnRegex()
    {
        // Arrange
        using var h = new Harness();
        h.Search("ba[rz]");

        // Act
        h.Key('r', Sdl.KMOD_LALT);
        h.Update();

        // Assert
        await Assert.That(h.Bar.Engine.UseRegex).IsTrue();
        await Assert.That(h.Bar.Engine.Matches.Count).IsEqualTo(2);
    }

    [Test]
    public async Task AnotherAltKeyIsNotHandled()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var result = h.Key('x', Sdl.KMOD_LALT);

        // Assert
        await Assert.That(result).IsEqualTo(SearchKeyResult.NotHandled);
    }

    [Test]
    public async Task CtrlRCyclesInsensitiveCaseSensitiveRegex()
    {
        // Arrange
        using var h = new Harness();
        h.Search("foo");
        var modes = new List<(bool Case, bool Regex)>();

        // Act
        for (int i = 0; i < 3; i++)
        {
            h.Key('r', Sdl.KMOD_CTRL);
            h.Update();
            modes.Add((h.Bar.Engine.CaseSensitive, h.Bar.Engine.UseRegex));
        }

        // Assert
        await Assert.That(modes).IsEquivalentTo([(true, false), (true, true), (false, false)], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(Sdl.K_ESCAPE, (ushort)0, "Close")]
    [Arguments(Sdl.K_RETURN, (ushort)0, "Previous")]
    [Arguments(Sdl.K_KP_ENTER, Sdl.KMOD_SHIFT, "Next")]
    [Arguments(Sdl.K_UP, (ushort)0, "Previous")]
    [Arguments(Sdl.K_DOWN, (ushort)0, "Next")]
    [Arguments((uint)'n', Sdl.KMOD_CTRL, "Next")]
    [Arguments((uint)'p', Sdl.KMOD_CTRL, "Previous")]
    [Arguments((uint)'x', Sdl.KMOD_CTRL, "NotHandled")]
    [Arguments((uint)'n', (ushort)(Sdl.KMOD_CTRL | Sdl.KMOD_SHIFT), "NotHandled")]
    [Arguments((uint)'a', (ushort)0, "NotHandled")]
    public async Task KeysSayWhatTheWindowShouldDo(uint key, ushort mod, string expected)
    {
        // Arrange
        using var h = new Harness();

        // Act
        var result = h.Key(key, mod);

        // Assert
        await Assert.That(result.ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task AClosedBarHandlesNoKeysAndNoText()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.Close(0);

        // Act
        var result = h.Key(Sdl.K_ESCAPE);
        h.Bar.HandleText("x", 0);

        // Assert
        await Assert.That(result).IsEqualTo(SearchKeyResult.NotHandled);
        await Assert.That(h.Bar.Query).IsEqualTo("");
        await Assert.That(h.Bar.IsActive).IsFalse();
    }

    [Test]
    public async Task BackspaceDeletesOneCharacter()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.HandleText("foox", 0);

        // Act
        h.Key(Sdl.K_BACKSPACE);

        // Assert
        await Assert.That(h.Bar.Query).IsEqualTo("foo");
    }

    [Test]
    public async Task BackspaceTakesASurrogatePairWhole()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.HandleText("a\U0001F600", 0);

        // Act
        h.Key(Sdl.K_BACKSPACE);

        // Assert
        await Assert.That(h.Bar.Query).IsEqualTo("a");
    }

    [Test]
    public async Task CtrlBackspaceDeletesTheLastWord()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.HandleText("foo bar  ", 0);

        // Act
        h.Key(Sdl.K_BACKSPACE, Sdl.KMOD_CTRL);

        // Assert
        await Assert.That(h.Bar.Query).IsEqualTo("foo ");
    }

    [Test]
    public async Task CtrlBackspaceOnOneWordEmptiesTheQuery()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.HandleText("foo", 0);

        // Act
        h.Key(Sdl.K_BACKSPACE, Sdl.KMOD_CTRL);
        h.Key(Sdl.K_BACKSPACE);

        // Assert
        await Assert.That(h.Bar.Query).IsEqualTo("");
    }

    [Test]
    public async Task CtrlUClearsTheQuery()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.HandleText("foo", 0);

        // Act
        var result = h.Key('u', Sdl.KMOD_CTRL);

        // Assert
        await Assert.That(result).IsEqualTo(SearchKeyResult.Handled);
        await Assert.That(h.Bar.Query).IsEqualTo("");
    }

    [Test]
    public async Task PastedLineBreaksBecomeSpaces()
    {
        // Arrange
        using var h = new Harness();

        // Act
        h.Bar.Paste("foo\r\nbar", 0);

        // Assert
        await Assert.That(h.Bar.Query).IsEqualTo("foo bar");
    }

    [Test]
    public async Task StepGoesToAnOlderMatch()
    {
        // Arrange
        using var h = new Harness();
        h.Search("foo");

        // Act
        var older = h.Bar.Step(-1, 0);

        // Assert
        await Assert.That(older).IsEqualTo(h.Bar.Engine.Matches[1]);
        await Assert.That(h.Bar.Current).IsEqualTo(older);
    }

    [Test]
    public async Task StepPastTheNewestWrapsToTheOldest()
    {
        // Arrange: the newest match is current.
        using var h = new Harness();
        h.Search("foo");

        // Act
        var wrapped = h.Bar.Step(1, 0);

        // Assert
        await Assert.That(wrapped).IsEqualTo(h.Bar.Engine.Matches[0]);
    }

    [Test]
    public async Task StepWithoutMatchesReturnsNothing()
    {
        // Arrange
        using var h = new Harness();
        h.Search("nothing like it");

        // Act
        var step = h.Bar.Step(1, 0);

        // Assert
        await Assert.That(step).IsNull();
        await Assert.That(h.Bar.Current).IsNull();
    }

    [Test]
    public async Task AMatchOffScreenIsRevealedOnce()
    {
        // Arrange: the only match scrolled off the top of a three-row screen.
        string text = "needle\r\n" + string.Concat(Enumerable.Range(0, 20).Select(i => $"line {i}\r\n"));
        using var h = new Harness(text, rows: 3);

        // Act
        h.Bar.HandleText("needle", 0);
        SearchMatch? first = null;
        for (int i = 0; i < 10 && first is null; i++) first = h.Bar.Update(h.Term, 0);
        var again = h.Bar.Update(h.Term, 0);

        // Assert
        await Assert.That(first).IsNotNull();
        await Assert.That(first!.Value.StartCol).IsEqualTo(0);
        await Assert.That(again).IsNull();
    }

    [Test]
    public async Task AMatchOnScreenIsNotRevealed()
    {
        // Arrange
        using var h = new Harness();

        // Act
        var reveal = h.Search("end");

        // Assert
        await Assert.That(reveal).IsNull();
        await Assert.That(h.Bar.Current).IsNotNull();
    }

    [Test]
    public async Task OpenAtShowsTheGivenMatchAtOnce()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.Close(0);
        var match = new SearchMatch(0, 0, 0, 2);

        // Act
        h.Bar.OpenAt(h.Pane, "foo", caseSensitive: true, regex: false, match, new Theme(), 0);

        // Assert
        await Assert.That(h.Bar.Current).IsEqualTo(match);
        await Assert.That(h.Bar.Query).IsEqualTo("foo");
        await Assert.That(h.Bar.Engine.CaseSensitive).IsTrue();
    }

    [Test]
    public async Task OpenAtKeepsItsMatchWhileTheScanCatchesUp()
    {
        // Arrange
        using var h = new Harness();
        var match = new SearchMatch(0, 0, 0, 2);
        h.Bar.OpenAt(h.Pane, "foo", caseSensitive: false, regex: false, match, new Theme(), 0);

        // Act
        h.Update();
        h.Bar.Update(h.Term, 0);

        // Assert: the outside pick is a real match here, so it stays current.
        await Assert.That(h.Bar.Current).IsEqualTo(match);
    }

    [Test]
    public async Task AReflowForgetsTheCurrentMatchUntilTheNextUpdate()
    {
        // Arrange
        using var h = new Harness();
        h.Search("foo");

        // Act
        h.Bar.OnReflow();
        var before = h.Bar.Current;
        h.Update();

        // Assert
        await Assert.That(before).IsNull();
        await Assert.That(h.Bar.Current).IsNotNull();
    }

    [Test]
    public async Task DetachingItsPaneClosesTheBar()
    {
        // Arrange
        using var h = new Harness();

        // Act
        h.Bar.Detach(h.Pane);

        // Assert
        await Assert.That(h.Bar.Pane).IsNull();
        await Assert.That(h.Bar.IsOpen).IsFalse();
        await Assert.That(h.Bar.IsVisible(0)).IsFalse();
    }

    [Test]
    public async Task DetachingAnotherPaneChangesNothing()
    {
        // Arrange
        using var h = new Harness();
        using var other = TestPanes.Spawn();

        // Act
        h.Bar.Detach(other);

        // Assert
        await Assert.That(h.Bar.Pane).IsSameReferenceAs(h.Pane);
        await Assert.That(h.Bar.IsOpen).IsTrue();
    }

    [Test]
    public async Task VimSearchHidesTheBarAndHighlightsItsPattern()
    {
        // Arrange
        using var h = new Harness();

        // Act
        h.Bar.BeginVim(h.Pane, "Foo", new Theme(), 0);
        h.Bar.Update(h.Term, 0);
        h.Bar.CompleteScan(h.Term);

        // Assert: smartcase, an upper-case letter makes it case-sensitive.
        await Assert.That(h.Bar.IsOpen).IsFalse();
        await Assert.That(h.Bar.IsVim).IsTrue();
        await Assert.That(h.Bar.IsActive).IsTrue();
        await Assert.That(h.Bar.Engine.Matches.Count).IsEqualTo(1);
    }

    [Test]
    public async Task VimSearchKeepsTheBarsQuery()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.HandleText("bar", 0);

        // Act
        h.Bar.BeginVim(h.Pane, "foo", new Theme(), 0);
        h.Bar.EndVim(0);

        // Assert
        await Assert.That(h.Bar.Query).IsEqualTo("bar");
        await Assert.That(h.Bar.IsVim).IsFalse();
    }

    [Test]
    public async Task AVimPatternErrorIsReportedOnlyInVim()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.BeginVim(h.Pane, "foo(", new Theme(), 0);

        // Act
        h.Bar.Update(h.Term, 0);
        string? during = h.Bar.VimError;
        h.Bar.EndVim(0);

        // Assert
        await Assert.That(during).IsNotNull();
        await Assert.That(h.Bar.VimError).IsNull();
    }

    [Test]
    public async Task VimSetsItsOwnCurrentMatch()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.BeginVim(h.Pane, "foo", new Theme(), 0);
        h.Bar.CompleteScan(h.Term);
        var match = h.Bar.Engine.Matches[1];

        // Act
        h.Bar.SetCurrent(match, 0);
        h.Bar.Update(h.Term, 0);

        // Assert
        await Assert.That(h.Bar.Current).IsEqualTo(match);
    }

    [Test]
    public async Task SettingNoCurrentMatchClearsIt()
    {
        // Arrange
        using var h = new Harness();
        h.Search("foo");

        // Act
        h.Bar.SetCurrent(null, 0);

        // Assert
        await Assert.That(h.Bar.Current).IsNull();
    }

    [Test]
    public async Task UpdateDoesNothingWhileInactive()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.HandleText("foo", 0);
        h.Bar.Close(0);

        // Act
        var reveal = h.Bar.Update(h.Term, 0);
        h.Bar.CompleteScan(h.Term);

        // Assert
        await Assert.That(reveal).IsNull();
        await Assert.That(h.Bar.Engine.Matches.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(0L, 600)]
    [Arguments(100L, 500)]
    [Arguments(700L, 360)]
    public async Task TheCaretBlinksOnAFixedRhythm(long now, int expected)
    {
        // Arrange
        using var h = new Harness();

        // Act
        int ms = h.Bar.MsUntilCaretChange(now);

        // Assert
        await Assert.That(ms).IsEqualTo(expected);
    }

    [Test]
    public async Task AClosedBarNeedsNoCaretWake()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.Close(0);

        // Act
        int ms = h.Bar.MsUntilCaretChange(100);

        // Assert
        await Assert.That(ms).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task ThePointerIsOnlyOverTheBarWhereItWasDrawn()
    {
        // Arrange: nothing drawn yet.
        using var h = new Harness();

        // Act
        bool over = h.Bar.HandleMouseMove(5, 5);
        var click = h.Bar.HandleMouseDown(5, 5, 0);

        // Assert
        await Assert.That(over).IsFalse();
        await Assert.That(click).IsNull();
        await Assert.That(h.Bar.Contains(5, 5)).IsFalse();
    }

    [Test]
    public async Task ToggleCaseAndRegexFlipTheModes()
    {
        // Arrange
        using var h = new Harness();
        h.Bar.HandleText("foo", 0);

        // Act
        h.Bar.ToggleCase(0);
        h.Bar.ToggleRegex(0);
        h.Update();

        // Assert
        await Assert.That(h.Bar.Engine.CaseSensitive).IsTrue();
        await Assert.That(h.Bar.Engine.UseRegex).IsTrue();
    }
}
