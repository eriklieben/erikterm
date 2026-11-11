using System.Text;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class TerminalSearchTests
{
    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    [Test]
    public async Task ARegexThatTimesOutMarksTheCountIncomplete()
    {
        // Arrange: catastrophic backtracking on a line that nearly matches
        var t = Feed(new Terminal(120, 5), new string('a', 60) + "b");
        var search = new TerminalSearch();
        search.SetQuery("(a+)+$", false, true);

        // Act
        while (true) { search.Step(t, 100); if (search.Complete) break; }

        // Assert
        await Assert.That(search.Truncated).IsTrue();
    }

    private static TerminalSearch Run(Terminal t, string pattern, bool caseSensitive = false, bool regex = false)
    {
        var s = new TerminalSearch();
        s.SetQuery(pattern, caseSensitive, regex);
        while (true) { s.Step(t, 3); if (s.Complete) break; }
        return s;
    }

    [Test]
    public async Task FindsMatchesInScrollbackAndScreen()
    {
        // Arrange
        var t = new Terminal(20, 3, 100);
        Feed(t, "foo one\r\nbar\r\nfoo two\r\nbaz\r\nlast foo");

        // Act
        var s = Run(t, "foo");

        // Assert
        await Assert.That(s.Matches.Count).IsEqualTo(3);
        await Assert.That(s.Matches[0]).IsEqualTo(new SearchMatch(t.FirstLine, 0, t.FirstLine, 2));
        await Assert.That(s.Matches[2]).IsEqualTo(new SearchMatch(t.FirstLine + 4, 5, t.FirstLine + 4, 7)); // on screen
        await Assert.That(s.Matches[0].StartLine < t.ScreenTopLine).IsTrue(); // in scrollback
    }

    [Test]
    public async Task MatchesAcrossSoftWrappedRows()
    {
        // Arrange
        var t = new Terminal(5, 4, 100);
        Feed(t, "abcdefghij"); // wraps: "abcde" | "fghij"

        // Act
        var s = Run(t, "defg");

        // Assert
        var m = await Assert.That(s.Matches).HasSingleItem();
        await Assert.That(m).IsEqualTo(new SearchMatch(t.FirstLine, 3, t.FirstLine + 1, 1));
    }

    [Test]
    public async Task DoesNotMatchAcrossHardLineBreaks()
    {
        // Arrange
        var t = new Terminal(10, 4, 100);
        Feed(t, "abc\r\ndef");

        // Act
        var s = Run(t, "cd");

        // Assert
        await Assert.That(s.Matches).IsEmpty();
    }

    [Test]
    [Arguments(false, 3)]
    [Arguments(true, 1)]
    public async Task CaseSensitivityToggle(bool caseSensitive, int expected)
    {
        // Arrange
        var t = new Terminal(20, 3, 100);
        Feed(t, "Error error ERROR");

        // Act
        var s = Run(t, "error", caseSensitive: caseSensitive);

        // Assert
        await Assert.That(s.Matches.Count).IsEqualTo(expected);
    }

    [Test]
    public async Task RegexMatches()
    {
        // Arrange
        var t = new Terminal(30, 3, 100);
        Feed(t, "id=12 id=345 id=x");

        // Act
        var s = Run(t, @"id=\d+", regex: true);

        // Assert
        await Assert.That(s.Matches.Count).IsEqualTo(2);
        await Assert.That(s.Matches[1].EndCol).IsEqualTo(11);
    }

    [Test]
    public async Task RegexReportsErrors()
    {
        // Arrange
        var t = new Terminal(30, 3, 100);
        Feed(t, "id=12 id=345 id=x");

        // Act
        var bad = Run(t, "(", regex: true);

        // Assert
        await Assert.That(bad.Error).IsNotNull();
        await Assert.That(bad.Matches).IsEmpty();
    }

    [Test]
    public async Task WideCharactersMapToTheirCells()
    {
        // Arrange
        var t = new Terminal(20, 3, 100);
        Feed(t, "日本語 x");

        // Act
        var s = Run(t, "本");

        // Assert
        var m = await Assert.That(s.Matches).HasSingleItem();
        await Assert.That((m.StartCol, m.EndCol)).IsEqualTo((2, 3));
    }

    [Test]
    public async Task KeepsMatchesAsOutputArrivesAndRescansOnlyTheScreen()
    {
        // Arrange
        var t = new Terminal(20, 3, 100);
        Feed(t, "foo\r\nx\r\nx\r\nx\r\n");
        var s = Run(t, "foo");
        var before = s.Matches.ToList();

        // Act
        Feed(t, "foo again\r\nx\r\n");
        while (true) { s.Step(t); if (s.Complete) break; }
        string secondText = t.GetText(s.Matches[1].StartLine, 0, s.Matches[1].StartLine, 19);

        // Assert
        var first = await Assert.That(before).HasSingleItem();
        await Assert.That(s.Matches.Count).IsEqualTo(2);
        await Assert.That(s.Matches[0]).IsEqualTo(first); // absolute line numbers: the old match didn't move
        await Assert.That(secondText).IsEqualTo("foo again");
    }

    [Test]
    public async Task DropsMatchesThatLeaveTheScrollback()
    {
        // Arrange
        var t = new Terminal(10, 2, 3);
        Feed(t, "foo\r\n");
        var s = Run(t, "foo");
        var before = s.Matches.ToList();

        // Act
        Feed(t, "a\r\nb\r\nc\r\nd\r\ne\r\n");
        s.Step(t);

        // Assert
        await Assert.That(before).HasSingleItem();
        await Assert.That(s.Matches).IsEmpty();
    }

    [Test]
    public async Task ScreenChangesReplaceStaleMatches()
    {
        // Arrange
        var t = new Terminal(10, 3, 100);
        Feed(t, "foo");
        var s = Run(t, "foo");
        var before = s.Matches.ToList();

        // Act
        Feed(t, "\e[2J\e[Hbar");
        s.Step(t);

        // Assert
        await Assert.That(before).HasSingleItem();
        await Assert.That(s.Matches).IsEmpty();
    }

    [Test]
    public async Task SearchesTenThousandLinesInBoundedSteps()
    {
        // Arrange
        var t = new Terminal(80, 24, 10_000);
        var sb = new StringBuilder();
        for (int i = 0; i < 10_000; i++) sb.Append("line ").Append(i).Append(" lorem ipsum dolor\r\n");
        Feed(t, sb.ToString());
        var s = new TerminalSearch();
        s.SetQuery("9999 ", false, false);

        // Act
        // Step's budget is a row count, not a time: the number of steps is deterministic.
        int steps = 0;
        while (!s.Complete) { s.Step(t, 1500); steps++; }

        // Assert
        await Assert.That(steps).IsGreaterThanOrEqualTo(6);
        await Assert.That(s.Matches).HasSingleItem();
    }

    /// <summary>Matches on rows 0 ("a x"), 1 (two: "x b x") and 3 ("x").</summary>
    private static (Terminal T, TerminalSearch S, long Line0) Navigation()
    {
        var t = new Terminal(20, 5, 100);
        Feed(t, "a x\r\nx b x\r\nc\r\nx");
        return (t, Run(t, "x"), t.FirstLine);
    }

    [Test]
    public async Task NavigationFindsEveryMatch()
    {
        // Act
        var (_, s, _) = Navigation();

        // Assert
        await Assert.That(s.Matches.Count).IsEqualTo(4);
    }

    [Test]
    public async Task LowerBoundIsTheFirstMatchAtOrAfterALine()
    {
        // Arrange
        var (_, s, l0) = Navigation();

        // Act
        int index = s.LowerBound(l0 + 1);

        // Assert
        await Assert.That(index).IsEqualTo(1);
    }

    [Test]
    [Arguments(1, 4, 2)]
    [Arguments(0, 1, -1)]
    public async Task LastAtOrBeforeAPosition(int lineOffset, int col, int expected)
    {
        // Arrange
        var (_, s, l0) = Navigation();

        // Act
        int index = s.LastAtOrBefore(l0 + lineOffset, col);

        // Assert
        await Assert.That(index).IsEqualTo(expected);
    }

    [Test]
    public async Task IndexOfFindsAMatch()
    {
        // Arrange
        var (_, s, _) = Navigation();

        // Act
        int index = s.IndexOf(s.Matches[3]);

        // Assert
        await Assert.That(index).IsEqualTo(3);
    }
}

public class CursorTests
{
    [Test]
    public async Task CursorBlinkStartsFromTheDefault()
    {
        // Act
        var t = new Terminal(10, 2) { DefaultCursorBlink = false };

        // Assert
        await Assert.That(t.CursorBlink).IsFalse();
    }

    [Test]
    public async Task Decscusr5SelectsABlinkingBar()
    {
        // Arrange
        var t = new Terminal(10, 2) { DefaultCursorBlink = false };

        // Act
        t.Feed("\e[5 q"u8);

        // Assert
        await Assert.That(t.CursorBlink).IsTrue();
        await Assert.That(t.CursorShape).IsEqualTo(CursorShape.Bar);
    }

    [Test]
    public async Task Decscusr2StopsBlinking()
    {
        // Arrange
        var t = new Terminal(10, 2) { DefaultCursorBlink = false };
        t.Feed("\e[5 q"u8);

        // Act
        t.Feed("\e[2 q"u8);

        // Assert
        await Assert.That(t.CursorBlink).IsFalse();
    }

    [Test]
    public async Task Decscusr0RestoresTheConfiguredBlink()
    {
        // Arrange
        var t = new Terminal(10, 2) { DefaultCursorBlink = false };
        t.Feed("\e[5 q"u8);
        t.Feed("\e[2 q"u8);
        t.DefaultCursorBlink = true;

        // Act
        t.Feed("\e[0 q"u8);

        // Assert
        await Assert.That(t.CursorBlink).IsTrue();
    }

    private const int Rate = 800;

    [Test]
    [Arguments(0L, 1f)]
    [Arguments(CursorMotion.BlinkPauseMs + 100L, 1f)]
    [Arguments(CursorMotion.BlinkPauseMs + Rate + 100L, 0f)]
    public async Task BlinkPausesAfterActivityThenTurnsOff(long elapsedMs, float expected)
    {
        // Act
        float opacity = CursorMotion.BlinkOpacity(elapsedMs, Rate);

        // Assert
        await Assert.That(opacity).IsEqualTo(expected);
    }

    [Test]
    public async Task BlinkFadesInsteadOfSwitching()
    {
        // Arrange
        long halfwayThroughTheFade = CursorMotion.BlinkPauseMs + Rate - (long)(CursorMotion.FadeMs(Rate) / 2);

        // Act
        float mid = CursorMotion.BlinkOpacity(halfwayThroughTheFade, Rate);

        // Assert
        await Assert.That(mid).IsBetween(0.2f, 0.8f); // fading, not a hard switch
    }

    [Test]
    public async Task NextChangeIsDueRightAwayDuringTheFade()
    {
        // Act
        int wait = CursorMotion.MsUntilChange(CursorMotion.BlinkPauseMs + Rate - 10, Rate);

        // Assert
        await Assert.That(wait).IsEqualTo(0);
    }

    [Test]
    public async Task NextChangeAfterActivityWaitsOutThePause()
    {
        // Act
        int wait = CursorMotion.MsUntilChange(0, Rate);

        // Assert
        await Assert.That(wait).IsGreaterThan(CursorMotion.BlinkPauseMs);
    }

    [Test]
    [Arguments(0L, 1f)]
    [Arguments(Rate, CursorMotion.PhaseFloor)]
    [Arguments(2L * Rate, 1f)]
    public async Task PhaseBlinkPulsesBetweenFullAndTheFloor(long t, float expected)
    {
        // Act
        float opacity = CursorMotion.BlinkOpacity(CursorMotion.BlinkPauseMs + t, Rate, pulse: true);

        // Assert
        await Assert.That(opacity).IsEqualTo(expected).Within(0.001f);
    }

    [Test]
    public async Task PhaseBlinkIsHalfwayAtAQuarterCycle()
    {
        // Act
        float opacity = CursorMotion.BlinkOpacity(CursorMotion.BlinkPauseMs + Rate / 2, Rate, pulse: true);

        // Assert — a continuous pulse, not an on/off switch
        await Assert.That(opacity).IsEqualTo(CursorMotion.PhaseFloor + (1 - CursorMotion.PhaseFloor) / 2).Within(0.001f);
    }

    [Test]
    public async Task PhaseBlinkStaysSolidWhileTyping()
    {
        // Act
        float opacity = CursorMotion.BlinkOpacity(CursorMotion.BlinkPauseMs - 1, Rate, pulse: true);

        // Assert
        await Assert.That(opacity).IsEqualTo(1f);
    }

    [Test]
    public async Task PhaseBlinkNeedsFramesContinuouslyOnceThePauseEnds()
    {
        // Act
        int during = CursorMotion.MsUntilChange(CursorMotion.BlinkPauseMs + Rate / 3, Rate, pulse: true);
        int before = CursorMotion.MsUntilChange(100, Rate, pulse: true);

        // Assert
        await Assert.That(during).IsEqualTo(0);
        await Assert.That(before).IsEqualTo(CursorMotion.BlinkPauseMs - 100);
    }

    [Test]
    public async Task ZeroBlinkRateNeverBlinks()
    {
        // Act
        float opacity = CursorMotion.BlinkOpacity(5000, 0);

        // Assert
        await Assert.That(opacity).IsEqualTo(1f);
    }

    [Test]
    [Arguments(1, 0, 200L, true)]
    [Arguments(1, 0, 10L, false)]   // streaming / key repeat
    [Arguments(0, 5, 200L, false)]  // far jump
    [Arguments(40, 0, 200L, false)]
    public async Task GlidesOnlyOnShortUnhurriedMoves(int dx, int dy, long msSinceLastMove, bool expected)
    {
        // Act
        bool glides = CursorMotion.ShouldGlide(dx, dy, msSinceLastMove);

        // Assert
        await Assert.That(glides).IsEqualTo(expected);
    }

    [Test]
    [Arguments(1, 0, true, true)]
    [Arguments(2, 0, true, true)]     // a wide character
    [Arguments(1, 0, false, false)]   // arrow key over existing text
    [Arguments(-1, 0, true, false)]   // backspace
    [Arguments(1, 1, true, false)]    // the next line
    public async Task TreatsAMoveAsTypingOnlyWhenItLeavesNewTextBehind(int dx, int dy, bool leftCellChanged, bool expected)
    {
        // Act
        bool typing = CursorMotion.IsTyping(dx, dy, leftCellChanged);

        // Assert
        await Assert.That(typing).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0L, false)]
    [Arguments(CursorMotion.HideGraceMs - 1, false)]   // hidden for a redraw
    [Arguments(CursorMotion.HideGraceMs, true)]
    public async Task HidingTheCursorOnlyCountsWhenItLasts(long msSinceHidden, bool expected)
    {
        // Act
        bool hidden = CursorMotion.HideTakesEffect(msSinceHidden);

        // Assert
        await Assert.That(hidden).IsEqualTo(expected);
    }

    [Test]
    [Arguments(1000L, 1110L, 110)]
    [Arguments(1109L, 1110L, 1)]
    public async Task ASettleInProgressAsksForOneFrameWhenItEnds(long now, long until, int expected)
    {
        // Act
        int wait = CursorMotion.MsUntilSettled(now, until);

        // Assert
        await Assert.That(wait).IsEqualTo(expected);
    }

    [Test]
    [Arguments(1110L, 1110L)]   // the frame that ended it
    [Arguments(5000L, 1110L)]
    public async Task AFinishedSettleAsksForNoFrames(long now, long until)
    {
        // Act
        int wait = CursorMotion.MsUntilSettled(now, until);

        // Assert
        await Assert.That(wait).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task ThePulseNeverDimsBelowHalf()
    {
        // Arrange
        const int rate = 800;
        float lowest = 1;

        // Act
        for (long t = 0; t < 2 * rate; t += 10)
            lowest = MathF.Min(lowest, CursorMotion.BlinkOpacity(CursorMotion.BlinkPauseMs + t, rate, pulse: true));

        // Assert
        await Assert.That(lowest).IsGreaterThanOrEqualTo(0.5f);
    }

    [Test]
    public async Task BarHugsTheTextInATallCell()
    {
        // Arrange: 15 px font in a line_height 1.25 cell of 25 px, baseline at 18.

        // Act
        var (top, bottom) = CursorMotion.BarSpan(25, 18, 15, CursorMotion.DefaultBarHeightEm);

        // Assert
        await Assert.That(bottom - top).IsEqualTo(18f);
        await Assert.That(top).IsGreaterThan(0f);
        await Assert.That(bottom).IsLessThan(25f);
    }

    [Test]
    public async Task BarHeightZeroFillsTheCell()
    {
        // Act
        var span = CursorMotion.BarSpan(25, 18, 15, 0);

        // Assert
        await Assert.That(span).IsEqualTo((0f, 25f));
    }

    [Test]
    public async Task BarStaysInsideItsCellWhenTallerThanIt()
    {
        // Act
        var (top, bottom) = CursorMotion.BarSpan(20, 16, 15, 2);

        // Assert
        await Assert.That(top).IsGreaterThanOrEqualTo(0f);
        await Assert.That(bottom).IsLessThanOrEqualTo(20f);
    }

    [Test]
    [Arguments("cursor_height = 0.8", 0.8)]
    [Arguments("cursor_height = \"cell\"", 0.0)]
    public async Task ReadsCursorHeight(string toml, double expected)
    {
        // Act
        var r = ErikTerm.Config.SettingsLoader.Parse(toml);

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.CursorHeight).IsEqualTo(expected);
    }

    [Test]
    public async Task WarnsAboutAnUnreadableCursorHeight()
    {
        // Act
        var r = ErikTerm.Config.SettingsLoader.Parse("cursor_height = 9");

        // Assert
        await Assert.That(r.Diagnostics).IsNotEmpty();
        await Assert.That(r.Settings.CursorHeight).IsEqualTo(1.19);
    }

    [Test]
    [Arguments(0f, 7.5f, 1f)]    // in the middle of the line
    [Arguments(0f, 9f, 0f)]      // outside the arc
    [Arguments(0f, 4f, 0f)]      // inside the outline: left empty
    public async Task RoundedOutlineCoversOnlyTheLine(float dx, float dy, float expected)
    {
        // Act
        float coverage = RoundedOutline.Coverage(dx, dy, 8, 2);

        // Assert
        await Assert.That(coverage).IsEqualTo(expected);
    }

}

public class TabListTests
{
    private sealed record T(string Name);

    private static (TabList<T> List, T A, T B, T C) Three()
    {
        var list = new TabList<T>();
        T a = new("a"), b = new("b"), c = new("c");
        list.Add(a); list.Add(b); list.Add(c);
        return (list, a, b, c);
    }

    [Test]
    public async Task AddActivatesTheNewTab()
    {
        // Act
        var (list, _, _, c) = Three();

        // Assert
        await Assert.That(list.Count).IsEqualTo(3);
        await Assert.That(list.Active).IsSameReferenceAs(c);
    }

    [Test]
    public async Task ActivateByIndex()
    {
        // Arrange
        var (list, a, _, _) = Three();

        // Act
        bool activated = list.Activate(list.Resolve(0));

        // Assert
        await Assert.That(activated).IsTrue();
        await Assert.That(list.Active).IsSameReferenceAs(a);
    }

    [Test]
    [Arguments(-1, 2)]
    [Arguments(7, -1)]
    public async Task ResolveCountsFromTheEndAndRejectsOutOfRange(int n, int expected)
    {
        // Arrange
        var (list, _, _, _) = Three();

        // Act
        int index = list.Resolve(n);

        // Assert
        await Assert.That(index).IsEqualTo(expected);
    }

    [Test]
    public async Task ActivatingAnInvalidIndexFails()
    {
        // Arrange
        var (list, a, _, _) = Three();
        list.Activate(a);

        // Act
        bool activated = list.Activate(-1);

        // Assert
        await Assert.That(activated).IsFalse();
    }

    [Test]
    public async Task ActivateFromTheEnd()
    {
        // Arrange
        var (list, a, _, c) = Three();
        list.Activate(a);

        // Act
        list.Activate(list.Resolve(-1));

        // Assert
        await Assert.That(list.Active).IsSameReferenceAs(c);
    }

    [Test]
    [Arguments(2, 1, 0)]  // c → a
    [Arguments(0, -1, 2)] // a → c
    [Arguments(2, -4, 1)] // c → b
    public async Task ActivateRelativeWraps(int from, int delta, int expected)
    {
        // Arrange
        var (list, a, b, c) = Three();
        T[] tabs = [a, b, c];
        list.Activate(from);

        // Act
        bool moved = list.ActivateRelative(delta);

        // Assert
        await Assert.That(moved).IsTrue();
        await Assert.That(list.Active).IsSameReferenceAs(tabs[expected]);
    }

    [Test]
    public async Task MoveRelativeKeepsTheActiveTab()
    {
        // Arrange
        var (list, a, b, c) = Three();
        list.Activate(a);

        // Act
        bool moved = list.MoveRelative(1);

        // Assert
        await Assert.That(moved).IsTrue();
        await Assert.That(list.Items).IsEquivalentTo([b, a, c], CollectionOrdering.Matching);
        await Assert.That(list.Active).IsSameReferenceAs(a);
    }

    [Test]
    public async Task MoveRelativeClampsToTheEnd()
    {
        // Arrange
        var (list, a, b, c) = Three();
        list.Activate(a);
        list.MoveRelative(1); // b a c

        // Act
        bool moved = list.MoveRelative(5);

        // Assert
        await Assert.That(moved).IsTrue();
        await Assert.That(list.Items).IsEquivalentTo([b, c, a], CollectionOrdering.Matching);
    }

    [Test]
    public async Task MoveRelativePastTheEndFails()
    {
        // Arrange
        var (list, a, _, _) = Three();
        list.Activate(a);
        list.MoveRelative(1);
        list.MoveRelative(5); // b c a

        // Act
        bool moved = list.MoveRelative(1);

        // Assert
        await Assert.That(moved).IsFalse();
    }

    [Test]
    public async Task MovingAnInactiveTabKeepsTheActiveOne()
    {
        // Arrange
        var (list, a, b, c) = Three();
        list.Activate(a);
        list.MoveRelative(1);
        list.MoveRelative(5); // b c a

        // Act
        bool moved = list.Move(0, 2);

        // Assert
        await Assert.That(moved).IsTrue();
        await Assert.That(list.Items).IsEquivalentTo([c, a, b], CollectionOrdering.Matching);
        await Assert.That(list.Active).IsSameReferenceAs(a);
    }

    [Test]
    public async Task RemovingTheActiveTabActivatesItsRightNeighbour()
    {
        // Arrange
        var (list, _, b, c) = Three();
        list.Activate(b);

        // Act
        var next = list.Remove(b);

        // Assert
        await Assert.That(next).IsSameReferenceAs(c);
    }

    [Test]
    public async Task RemovingTheLastTabFallsBackToTheLeft()
    {
        // Arrange
        var (list, a, b, c) = Three();
        list.Activate(b);
        list.Remove(b); // c is active and last

        // Act
        var next = list.Remove(c);

        // Assert
        await Assert.That(next).IsSameReferenceAs(a);
    }

    [Test]
    public async Task RemovingTheOnlyTabLeavesNoneActive()
    {
        // Arrange
        var (list, a, b, c) = Three();
        list.Activate(b);
        list.Remove(b);
        list.Remove(c);

        // Act
        var next = list.Remove(a);

        // Assert
        await Assert.That(next).IsNull();
        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(list.Active).IsNull();
    }

    [Test]
    public async Task RemovingAnInactiveTabKeepsTheActiveOne()
    {
        // Arrange
        var (list, a, _, c) = Three();

        // Act
        var next = list.Remove(a);

        // Assert
        await Assert.That(next).IsSameReferenceAs(c);
        await Assert.That(list.ActiveIndex).IsEqualTo(1);
    }

    [Test]
    public async Task RemovingAnotherInactiveTabStillKeepsTheActiveOne()
    {
        // Arrange
        var (list, a, b, c) = Three();
        list.Remove(a);

        // Act
        var next = list.Remove(b);

        // Assert
        await Assert.That(next).IsSameReferenceAs(c);
    }
}

public class TabAndCursorConfigTests
{
    [Test]
    public async Task ParsesCursorAndTabBarSettings()
    {
        // Arrange
        const string toml = """
            default_cursor_style = "BlinkingBar"
            cursor_blink_rate = 500
            cursor_animation = false
            enable_tab_bar = false
            hide_tab_bar_if_only_one_tab = false
            """;

        // Act
        var r = ErikTerm.Config.SettingsLoader.Parse(toml);

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.CursorBlinks).IsTrue();
        await Assert.That(r.Settings.CursorBlinkRate).IsEqualTo(500);
        await Assert.That(r.Settings.CursorAnimation).IsFalse();
        await Assert.That(r.Settings.EnableTabBar).IsFalse();
        await Assert.That(r.Settings.HideTabBarIfOnlyOneTab).IsFalse();
    }

    [Test]
    [Arguments("cursor_thickness = 1.5", 1.5, 0.0)]
    [Arguments("cursor_thickness = \"1px\"", 1.0, 0.0)]
    [Arguments("cursor_thickness = \"0.1cell\"", 0.0, 0.1)]
    [Arguments("cursor_thickness = \"25%\"", 0.0, 0.25)]
    public async Task ParsesCursorThickness(string toml, double points, double cells)
    {
        // Act
        var r = ErikTerm.Config.SettingsLoader.Parse(toml);

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.CursorThickness.Points).IsEqualTo(points);
        await Assert.That(r.Settings.CursorThickness.Cells).IsEqualTo(cells);
    }

    [Test]
    [Arguments("cursor_thickness = 0")]
    [Arguments("cursor_thickness = \"2cell\"")]
    [Arguments("cursor_thickness = \"thin\"")]
    public async Task RejectsBadCursorThickness(string toml)
    {
        // Act
        var r = ErikTerm.Config.SettingsLoader.Parse(toml);

        // Assert
        await Assert.That(r.Diagnostics).Count().IsEqualTo(1);
        await Assert.That(r.Settings.CursorThickness.IsAuto).IsTrue();
    }

    [Test]
    public async Task ParsesPhaseBlinkStyle()
    {
        // Act
        var r = ErikTerm.Config.SettingsLoader.Parse("cursor_blink_style = \"phase\"");

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.CursorBlinkStyle).IsEqualTo(ErikTerm.Config.CursorBlinkStyle.Phase);
    }

    [Test]
    public async Task BlinkStyleDefaultsToFade()
    {
        // Act
        var settings = new ErikTerm.Config.Settings();

        // Assert
        await Assert.That(settings.CursorBlinkStyle).IsEqualTo(ErikTerm.Config.CursorBlinkStyle.Fade);
    }

    [Test]
    public async Task CursorDoesNotBlinkByDefault()
    {
        // Act
        var settings = new ErikTerm.Config.Settings();

        // Assert
        await Assert.That(settings.CursorBlinks).IsFalse();
    }

    [Test]
    [Arguments("Ctrl+Shift+F", "Search")]
    [Arguments("Ctrl+Shift+T", "SpawnTab")]
    [Arguments("Ctrl+Tab", "TabSwitcher(1)")]
    [Arguments("Ctrl+Shift+Tab", "TabSwitcher(-1)")]
    [Arguments("Ctrl+PgDn", "ActivateTabRelative(1)")]
    [Arguments("Ctrl+PgUp", "ActivateTabRelative(-1)")]
    [Arguments("Ctrl+Shift+1", "ActivateTab(0)")]
    [Arguments("Ctrl+Shift+9", "ActivateTab(-1)")]
    [Arguments("Ctrl+Shift+W", "CloseCurrentPane")]
    public async Task DefaultBindingsForSearchAndTabs(string chord, string action)
    {
        // Act
        var keys = new ErikTerm.Config.Settings().EffectiveKeys().ToDictionary(b => b.Chord.ToString(), b => b.Action.ToString());

        // Assert
        await Assert.That(keys[chord]).IsEqualTo(action);
    }

    [Test]
    [Arguments("SpawnTab(CurrentPaneDomain)")]
    [Arguments("MoveTabRelative(-1)")]
    public async Task TabActionsParse(string spec)
    {
        // Act
        bool parsed = ErikTerm.Config.ActionSpec.TryParse(spec, out _, out _);

        // Assert
        await Assert.That(parsed).IsTrue();
    }
}
