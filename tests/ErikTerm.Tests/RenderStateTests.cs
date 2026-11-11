using System.Text;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>Renderer-side state that doesn't need a window: the row cache, smooth scrolling, reflow invalidation.</summary>
public class RenderStateTests
{
    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    private static RowCache.Config Config(int cols, int paletteVersion = 0) =>
        new(null, 16, 8, 16, null, 0, null, paletteVersion, null, 1, 1, 1, cols);

    // ---- Row cache ---------------------------------------------------------------------------

    /// <summary>One frame over the top <paramref name="rows"/> screen rows, no cursor: whether each row was (re)built.</summary>
    private static bool[] Frame(RowCache cache, Terminal t, int rows)
    {
        var fresh = new bool[rows];
        cache.Begin(Config(10));
        for (int y = 0; y < rows; y++) cache.Get(t.GetScreenRow(y), -1, 10, out fresh[y]);
        cache.End();
        return fresh;
    }

    [Test]
    public async Task RowCache_ReusesUnchangedRowsAndRebuildsWrittenOnes()
    {
        // Arrange
        var t = Feed(new Terminal(10, 3), "aaa\r\nbbb\r\nccc");
        var cache = new RowCache();
        bool[] firstFrame = Frame(cache, t, 3);
        Feed(t, "\e[2;1Hxyz");

        // Act
        bool[] secondFrame = Frame(cache, t, 3);

        // Assert
        await Assert.That(firstFrame).IsEquivalentTo([true, true, true], CollectionOrdering.Matching);
        await Assert.That(secondFrame).IsEquivalentTo([false, true, false], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RowCache_RowsThatScrolledAreReusedAndRecycledRowsRebuilt()
    {
        // Arrange
        var t = Feed(new Terminal(10, 3), "1\r\n2\r\n3");
        var cache = new RowCache();
        Frame(cache, t, 3);
        Feed(t, "\r\n4"); // "2" and "3" move up a row; the bottom row is new (or recycled and cleared)

        // Act
        bool[] fresh = Frame(cache, t, 3);
        int cached = cache.Count;

        // Assert
        await Assert.That(fresh).IsEquivalentTo([false, false, true], CollectionOrdering.Matching);
        await Assert.That(cached).IsEqualTo(3); // the row that scrolled out of view was released
    }

    [Test]
    public async Task RowCache_CursorColumnOnlyInvalidatesItsRow()
    {
        // Arrange
        var t = Feed(new Terminal(10, 2), "ab\r\ncd");
        var cache = new RowCache();
        cache.Begin(Config(10));
        cache.Get(t.GetScreenRow(0), -1, 10, out _);
        cache.Get(t.GetScreenRow(1), 2, 10, out _);
        cache.End();

        // Act
        cache.Begin(Config(10));
        cache.Get(t.GetScreenRow(0), -1, 10, out bool f0);
        cache.Get(t.GetScreenRow(1), 1, 10, out bool f1);
        cache.End();

        // Assert
        await Assert.That(f0).IsFalse();
        await Assert.That(f1).IsTrue();
    }

    [Test]
    public async Task RowCache_ConfigChangeDropsEverything()
    {
        // Arrange
        var t = Feed(new Terminal(10, 1), "ab");
        var cache = new RowCache();
        Frame(cache, t, 1);

        // Act
        cache.Begin(Config(10, paletteVersion: 1));
        cache.Get(t.GetScreenRow(0), -1, 10, out bool fresh);

        // Assert
        await Assert.That(fresh).IsTrue();
    }

    /// <summary>A full-screen program repainting the same text, then a frame: whether the row was rebuilt.</summary>
    private static bool RepaintSameText(Terminal t, RowCache cache)
    {
        Feed(t, "\r\e[31mabc\e[m");
        cache.Begin(Config(10));
        cache.Get(t.GetScreenRow(0), -1, 10, out bool fresh);
        cache.End();
        return fresh;
    }

    [Test]
    public async Task RowCache_IdenticalRepaintIsReplayed()
    {
        // Arrange
        var t = Feed(new Terminal(10, 1), "abc");
        var cache = new RowCache();
        RepaintSameText(t, cache);
        RepaintSameText(t, cache);

        // Act
        bool fresh = RepaintSameText(t, cache);

        // Assert
        await Assert.That(fresh).IsFalse();
    }

    [Test]
    public async Task RowCache_ChangeAfterReplayedRepaintsIsRebuilt()
    {
        // Arrange
        var t = Feed(new Terminal(10, 1), "abc");
        var cache = new RowCache();
        for (int i = 0; i < 3; i++) RepaintSameText(t, cache);
        Feed(t, "\rabd");

        // Act
        cache.Begin(Config(10));
        cache.Get(t.GetScreenRow(0), -1, 10, out bool changed);

        // Assert
        await Assert.That(changed).IsTrue();
    }

    [Test]
    public async Task RowCache_ContentHashSeesClusters()
    {
        // Arrange
        // "e" + U+0301 (acute) and "e" + U+0300 (grave): the same base character, different clusters.
        var a = Feed(new Terminal(10, 1), "e\u0301");
        var b = Feed(new Terminal(10, 1), "e\u0300");

        // Act
        var hashA = RowCache.ContentHash(a.GetScreenRow(0), 10);
        var hashB = RowCache.ContentHash(b.GetScreenRow(0), 10);

        // Assert
        await Assert.That(hashA).IsNotEqualTo(hashB);
    }

    // ---- Smooth scrolling --------------------------------------------------------------------

    private static Terminal History(int lines = 100)
    {
        var t = new Terminal(20, 10, scrollbackLines: 1000);
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++) sb.Append("line ").Append(i).Append("\r\n");
        return Feed(t, sb.ToString());
    }

    [Test]
    public async Task SmoothScroll_FollowingOutputNeverEases()
    {
        // Arrange
        var t = History();
        var s = new SmoothScroll(t);

        // Act
        float before = s.Update(true, 1000);
        Feed(t, "more\r\nand more\r\n");
        float after = s.Update(true, 1010);
        bool animating = s.IsAnimating(1010);

        // Assert
        await Assert.That(before).IsEqualTo(0f);
        await Assert.That(after).IsEqualTo(0f);
        await Assert.That(animating).IsFalse();
    }

    [Test]
    public async Task SmoothScroll_ViewJumpEasesFromTheOldPosition()
    {
        // Arrange
        var t = History();
        var s = new SmoothScroll(t);
        s.Update(true, 1000);

        // Act
        t.ScrollView(3); // wheel up a notch
        float start = s.Update(true, 1000);
        float mid = s.Update(true, 1000 + (long)(SmoothScroll.EaseMs / 2));
        float end = s.Update(true, 1000 + (long)SmoothScroll.EaseMs + 1);
        bool animating = s.IsAnimating(1000 + (long)SmoothScroll.EaseMs + 1);

        // Assert
        await Assert.That(start).IsEqualTo(-3f); // the rows are drawn where they were…
        await Assert.That(mid).IsBetween(-3f, 0f); // …and glide into place
        await Assert.That(mid).IsGreaterThan(-3f);
        await Assert.That(end).IsEqualTo(0f);
        await Assert.That(animating).IsFalse();
    }

    [Test]
    public async Task SmoothScroll_DisabledDoesNotEase()
    {
        // Arrange
        var t = History();
        var s = new SmoothScroll(t);
        s.Update(false, 1000);

        // Act
        t.ScrollView(5);
        float offset = s.Update(false, 1000);

        // Assert
        await Assert.That(offset).IsEqualTo(0f);
    }

    [Test]
    public async Task SmoothScroll_SnappedDoesNotEase()
    {
        // Arrange
        // The view is 5 lines up and already drawn there, so going back to the live screen is a jump that would ease.
        var t = History();
        var s = new SmoothScroll(t);
        t.ScrollView(5);
        s.Update(true, 2000);

        // Act
        t.ScrollView(5);
        s.Snap(); // typing: back to the live screen at once
        t.ResetView();
        float offset = s.Update(true, 2000);

        // Assert
        await Assert.That(offset).IsEqualTo(0f);
    }

    [Test]
    public async Task SmoothScroll_TrackpadFollowsByFractionsAndSettles()
    {
        // Arrange
        var t = History();
        var s = new SmoothScroll(t);
        s.Update(true, 1000);

        // Act
        s.Wheel(1, 0.1f, true, 1000); // 0.3 lines toward older
        int offsetAfterAFraction = t.ViewOffset;
        float shownAfterAFraction = s.Update(true, 1000);
        s.Wheel(1, 0.3f, true, 1010); // 1.2 lines in total
        int offsetAfterALine = t.ViewOffset;
        float shownAfterALine = s.Update(true, 1010);
        s.Tick(1010 + SmoothScroll.SettleMs + 1); // the gesture paused: settle on a whole line
        float fractionAfterSettling = s.Fraction;
        int offsetAfterSettling = t.ViewOffset;
        float shownWhileSettling = s.Update(true, 1010 + SmoothScroll.SettleMs + 1);
        float shownLater = s.Update(true, 5000);

        // Assert
        await Assert.That(offsetAfterAFraction).IsEqualTo(0);
        await Assert.That(shownAfterAFraction).IsEqualTo(0.3f).Within(0.0005f);
        await Assert.That(offsetAfterALine).IsEqualTo(1);
        await Assert.That(shownAfterALine).IsEqualTo(0.2f).Within(0.0005f); // no extra easing for the whole line just applied
        await Assert.That(fractionAfterSettling).IsEqualTo(0f);
        await Assert.That(offsetAfterSettling).IsEqualTo(1);
        await Assert.That(shownWhileSettling).IsEqualTo(0.2f).Within(0.0005f); // eases from where it was shown
        await Assert.That(shownLater).IsEqualTo(0f);
    }

    [Test]
    public async Task SmoothScroll_NoPartialRowBelowTheLiveScreen()
    {
        // Arrange
        var t = History();
        var s = new SmoothScroll(t);
        s.Update(true, 1000);

        // Act
        s.Wheel(-1, -0.2f, true, 1000);
        float fraction = s.Fraction;
        float shown = s.Update(true, 1000);

        // Assert
        await Assert.That(fraction).IsEqualTo(0f);
        await Assert.That(shown).IsEqualTo(0f);
    }

    [Test]
    public async Task SmoothScroll_NotchedWheelScrollsThreeLines()
    {
        // Arrange
        var t = History();
        var s = new SmoothScroll(t);

        // Act
        s.Wheel(1, 1f, true, 1000);

        // Assert
        await Assert.That(t.ViewOffset).IsEqualTo(SmoothScroll.LinesPerNotch);
    }

    // ---- Reflow invalidation -----------------------------------------------------------------

    [Test]
    public async Task Reflow_ClearsSelectionAndHoverOnce()
    {
        // Arrange
        var t = Feed(new Terminal(10, 4), "0123456789abcdef");
        int seen = t.ReflowCount;
        Selection? sel = new() { AnchorLine = t.ScreenTopLine, AnchorCol = 0, HeadLine = t.ScreenTopLine + 1, HeadCol = 3 };
        var links = new PaneLinks { HoveredUri = "http://x", GutterStart = 1, GutterEnd = 2, GutterPrompt = 0, FlashLine = 3 };
        links.Hovered.Add(new LineSegment(t.ScreenTopLine, 0, 3));

        // Act
        bool beforeAnyResize = ReflowInvalidation.Check(t, ref seen, ref sel, links);
        var selBeforeAnyResize = sel;
        t.Resize(10, 6); // rows only: no reflow, line numbers keep their meaning
        bool afterRowsOnly = ReflowInvalidation.Check(t, ref seen, ref sel, links);
        var selAfterRowsOnly = sel;
        t.Resize(5, 6);
        bool afterReflow = ReflowInvalidation.Check(t, ref seen, ref sel, links);
        var selAfterReflow = sel;
        var hoveredAfterReflow = links.Hovered.ToList();
        string? hoveredUriAfterReflow = links.HoveredUri;
        long gutterStartAfterReflow = links.GutterStart;
        long flashLineAfterReflow = links.FlashLine;
        sel = new Selection();
        bool checkedAgain = ReflowInvalidation.Check(t, ref seen, ref sel, links);
        var selAfterCheckingAgain = sel;

        // Assert
        await Assert.That(beforeAnyResize).IsFalse(); // nothing happened yet
        await Assert.That(selBeforeAnyResize).IsNotNull();
        await Assert.That(afterRowsOnly).IsFalse();
        await Assert.That(selAfterRowsOnly).IsNotNull();
        await Assert.That(afterReflow).IsTrue();
        await Assert.That(selAfterReflow).IsNull();
        await Assert.That(hoveredAfterReflow).IsEmpty();
        await Assert.That(hoveredUriAfterReflow).IsNull();
        await Assert.That(gutterStartAfterReflow).IsEqualTo(-1);
        await Assert.That(flashLineAfterReflow).IsEqualTo(-1);
        await Assert.That(checkedAgain).IsFalse(); // once per reflow
        await Assert.That(selAfterCheckingAgain).IsNotNull();
    }

    [Test]
    public async Task Reflow_RestartsSearchResults()
    {
        // Arrange
        var t = Feed(new Terminal(10, 4), "hello world, hello");
        var search = new TerminalSearch();
        search.SetQuery("hello", false, false);
        while (!search.Complete) search.Step(t);
        search.Step(t);
        int matchesBefore = search.Matches.Count;

        // Act
        t.Resize(6, 4);
        t.Resize(10, 4); // back to the old width between two steps: still renumbered
        search.Step(t);
        while (!search.Complete) search.Step(t);
        int matchesAfter = search.Matches.Count;
        var second = search.Matches[1];
        string secondText = t.GetText(second.StartLine, second.StartCol, second.EndLine, second.EndCol);
        int reflows = t.ReflowCount;

        // Assert
        await Assert.That(matchesBefore).IsEqualTo(2);
        await Assert.That(matchesAfter).IsEqualTo(2);
        await Assert.That(secondText).IsEqualTo("hello");
        await Assert.That(reflows).IsEqualTo(2);
    }
}
