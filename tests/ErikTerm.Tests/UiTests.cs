using System.Text;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class FuzzyTests
{
    [Test]
    public async Task MatchesSubsequenceCaseInsensitively()
    {
        // Arrange
        var pos = new List<int>();

        // Act
        bool matched = Fuzzy.Match("spr", "Split Right", out _, pos);

        // Assert
        await Assert.That(matched).IsTrue();
        await Assert.That(pos).IsEquivalentTo([0, 1, 6], CollectionOrdering.Matching); // S, p, R — prefers the word start over the 'r' in nothing earlier
    }

    [Test]
    public async Task RejectsNonSubsequence()
    {
        // Act
        bool matched = Fuzzy.Match("xyz", "Split Right", out _);

        // Assert
        await Assert.That(matched).IsFalse();
    }

    [Test]
    [Arguments("sr", "Split Right", "Scroll to Top")]                    // word starts
    [Arguments("close", "Close Pane", "Copy Last Output Scroll Edit")]   // a run
    public async Task WordStartsAndRunsScoreHigher(string pattern, string better, string worse)
    {
        // Act
        Fuzzy.Match(pattern, better, out int betterScore);
        Fuzzy.Match(pattern, worse, out int worseScore);

        // Assert
        await Assert.That(betterScore).IsGreaterThan(worseScore);
    }

    [Test]
    public async Task EmptyPatternMatchesEverything()
    {
        // Act
        bool matched = Fuzzy.Match("  ", "anything", out _);

        // Assert
        await Assert.That(matched).IsTrue();
    }
}

public class LayoutTreeTests
{
    private sealed record P(string Name);

    private static readonly RectI Bounds = new(0, 0, 1000, 600);

    private static Dictionary<string, RectI> Rects(LayoutTree<P> t, int gap = 10)
    {
        var list = new List<(P, RectI)>();
        t.Layout(Bounds, gap, list);
        return list.ToDictionary(x => x.Item1.Name, x => x.Item2);
    }

    /// <summary>a on the left; b above c on the right.</summary>
    private static (LayoutTree<P> Tree, P A, P B, P C) ThreePanes()
    {
        var a = new P("a"); var b = new P("b"); var c = new P("c");
        var t = new LayoutTree<P>(a);
        t.Split(a, b, SplitAxis.Horizontal);
        t.Split(b, c, SplitAxis.Vertical);
        return (t, a, b, c);
    }

    [Test]
    public async Task SplitsHorizontallyAndVertically()
    {
        // Arrange
        var a = new P("a"); var b = new P("b"); var c = new P("c");
        var t = new LayoutTree<P>(a);

        // Act
        t.Split(a, b, SplitAxis.Horizontal);
        t.Split(b, c, SplitAxis.Vertical);
        var r = Rects(t);

        // Assert
        await Assert.That(r["a"]).IsEqualTo(new RectI(0, 0, 495, 600));
        await Assert.That(r["b"]).IsEqualTo(new RectI(505, 0, 495, 295));
        await Assert.That(r["c"]).IsEqualTo(new RectI(505, 305, 495, 295));
    }

    [Test]
    [Arguments("a", Direction.Right, "b")]
    [Arguments("c", Direction.Left, "a")]
    [Arguments("b", Direction.Down, "c")]
    [Arguments("a", Direction.Left, null)]
    public async Task NeighborsFollowGeometry(string from, Direction direction, string? expected)
    {
        // Arrange
        var (t, a, b, c) = ThreePanes();
        var panes = new[] { a, b, c }.ToDictionary(p => p.Name);

        // Act
        var neighbor = t.Neighbor(panes[from], direction, Bounds, 10);

        // Assert
        await Assert.That(neighbor?.Name).IsEqualTo(expected);
    }

    [Test]
    public async Task RemovingAPaneGivesItsSpaceToTheSibling()
    {
        // A walk-through: each removal works on the tree the previous one left.
        // Arrange
        var (t, a, b, c) = ThreePanes();

        // Act
        var afterC = t.Remove(c);
        var bRect = Rects(t)["b"];
        var afterB = t.Remove(b);
        var aRect = Rects(t)["a"];
        var afterA = t.Remove(a);

        // Assert
        await Assert.That(afterC).IsEqualTo(b);
        await Assert.That(bRect).IsEqualTo(new RectI(505, 0, 495, 600));
        await Assert.That(afterB).IsEqualTo(a);
        await Assert.That(aRect).IsEqualTo(Bounds);
        await Assert.That(afterA).IsNull();
    }

    [Test]
    public async Task AdjustMovesTheNearestDivider()
    {
        // A walk-through: each adjustment moves the divider the previous one left.
        // Arrange
        var a = new P("a"); var b = new P("b");
        var t = new LayoutTree<P>(a);
        t.Split(a, b, SplitAxis.Horizontal);

        // Act
        bool moved = t.Adjust(a, Direction.Right, 100, Bounds, 10, 50);
        int widthAfterMove = Rects(t)["a"].W;
        bool clamped = t.Adjust(b, Direction.Right, 1000, Bounds, 10, 50);
        int widthAfterClamp = Rects(t)["a"].W;
        bool movedUp = t.Adjust(a, Direction.Up, 10, Bounds, 10, 50);

        // Assert
        await Assert.That(moved).IsTrue();
        await Assert.That(widthAfterMove).IsEqualTo(595);
        await Assert.That(clamped).IsTrue();
        await Assert.That(widthAfterClamp).IsEqualTo(940); // clamped by minSize
        await Assert.That(movedUp).IsFalse();              // no vertical divider
    }
}

public class KeyBindingTests
{
    [Test]
    [Arguments("CTRL+SHIFT+p", KeyMods.Ctrl | KeyMods.Shift, "p")]
    [Arguments("ctrl|shift|P", KeyMods.Ctrl | KeyMods.Shift, "p")]
    [Arguments("CTRL+SHIFT+%", KeyMods.Ctrl | KeyMods.Shift, "5")]
    [Arguments("CTRL++", KeyMods.Ctrl | KeyMods.Shift, "=")]
    [Arguments("ALT+Enter", KeyMods.Alt, "enter")]
    [Arguments("SUPER+LeftArrow", KeyMods.Super, "left")]
    [Arguments("F11", KeyMods.None, "f11")]
    public async Task ParsesChords(string text, KeyMods mods, string key)
    {
        // Act
        bool ok = KeyChord.TryParse(text, out var chord, out var error);

        // Assert
        await Assert.That(ok).IsTrue().Because(error);
        await Assert.That(chord).IsEqualTo(new KeyChord(mods, key));
    }

    [Test]
    [Arguments("HYPER+a")]
    [Arguments("CTRL+nosuchkey")]
    public async Task RejectsBadChords(string text)
    {
        // Act
        bool ok = KeyChord.TryParse(text, out _, out _);

        // Assert
        await Assert.That(ok).IsFalse();
    }

    [Test]
    [Arguments("SplitHorizontal", "SplitHorizontal", null)]
    [Arguments("ActivatePaneDirection(Left)", "ActivatePaneDirection", "Left")]
    [Arguments("AdjustPaneSize(Left, 3)", "AdjustPaneSize", "Left, 3")]
    [Arguments("DisableDefaultAssignment", "None", null)]
    public async Task ParsesActions(string text, string name, string? arg)
    {
        // Act
        bool ok = ActionSpec.TryParse(text, out var action, out _);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(action).IsEqualTo(new ActionSpec(name, arg));
    }

    [Test]
    [Arguments("CopyTo 'Clipboard'")]
    public async Task RejectsBadActions(string text)
    {
        // Act
        bool ok = ActionSpec.TryParse(text, out _, out _);

        // Assert
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task ConfigKeysOverrideAndDisableDefaults()
    {
        // Arrange
        const string toml = """
            [keys]
            "CTRL+h" = "ActivatePaneDirection(Left)"
            "CTRL+SHIFT+p" = "None"
            "CTRL+x" = "Explode"
            """;

        // Act
        var r = SettingsLoader.Parse(toml, "cfg.toml");
        var keys = r.Settings.EffectiveKeys().ToDictionary(k => k.Chord, k => k.Action);

        // Assert
        await Assert.That(r.Diagnostics).IsEquivalentTo(["cfg.toml:4: keys.\"CTRL+x\": unknown action 'Explode'"], CollectionOrdering.Matching);
        await Assert.That(keys[new KeyChord(KeyMods.Ctrl, "h")]).IsEqualTo(new ActionSpec("ActivatePaneDirection", "Left"));
        await Assert.That(keys).DoesNotContainKey(new KeyChord(KeyMods.Ctrl | KeyMods.Shift, "p"));
        await Assert.That(keys).ContainsKey(new KeyChord(KeyMods.Ctrl | KeyMods.Shift, "c")); // other defaults remain
    }

    [Test]
    public async Task DefaultBindingsAllParse()
    {
        // Act
        int built = DefaultKeys.Build(macOS: false).Count; // the macOS extras are counted in MacOsKeyTests

        // Assert
        await Assert.That(built).IsEqualTo(DefaultKeys.Table.Length);
    }

    [Test]
    public async Task DefaultBindingsDoNotCollide()
    {
        // Act
        var dupes = DefaultKeys.Build().GroupBy(b => b.Chord).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(" / ", g.Select(b => b.Action))}").ToList();

        // Assert
        await Assert.That(dupes).IsEmpty();
    }
}

public class SelectionTextTests
{
    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    [Test]
    public async Task JoinsSoftWrappedLinesAndTrimsHardOnes()
    {
        // Arrange
        var t = Feed(new Terminal(5, 4), "abcdefgh\r\nxy   \r\nz");
        long top = t.ScreenTopLine;

        // Act
        string all = t.GetText(top, 0, top + 3, 4);
        string acrossTheWrap = t.GetText(top, 2, top + 1, 1);

        // Assert
        await Assert.That(all).IsEqualTo("abcdefgh\nxy\nz");
        await Assert.That(acrossTheWrap).IsEqualTo("cdefg");
    }

    [Test]
    public async Task LinesKeepTheirNumbersWhenScrolledIntoHistory()
    {
        // A walk-through: the same line number is read after scrolling, then after eviction.
        // Arrange
        var t = Feed(new Terminal(10, 2, scrollbackLines: 2), "one\r\ntwo");
        long two = t.ScreenTopLine + 1;

        // Act
        Feed(t, "\r\nthree\r\nfour\r\nfive");
        string inScrollback = t.GetText(two, 0, two, 9);
        Feed(t, "\r\nsix\r\nseven");
        string evicted = t.GetText(two, 0, two, 9);

        // Assert
        await Assert.That(inScrollback).IsEqualTo("two"); // now in scrollback, same line number
        await Assert.That(evicted).IsEqualTo("");         // evicted
    }

    [Test]
    [Arguments(10, 3, 20)]
    [Arguments(0, 0, 1)]
    public async Task WordBoundaries(int col, int start, int end)
    {
        // Arrange
        var t = Feed(new Terminal(40, 2), "ls ~/src/erik-term.cs  next");
        long line = t.ScreenTopLine;

        // Act
        var word = t.WordAt(line, col);

        // Assert
        await Assert.That(word).IsEqualTo((start, end));
    }

    [Test]
    public async Task UserVarsFromOsc1337()
    {
        // Arrange
        var t = new Terminal(10, 2);

        // Act
        Feed(t, "\e]1337;SetUserVar=IS_NVIM=dHJ1ZQ==\a");

        // Assert
        await Assert.That(t.UserVars["IS_NVIM"]).IsEqualTo("true");
    }

    [Test]
    public async Task MouseEncodingSgr()
    {
        // Arrange
        var t = Feed(new Terminal(80, 24), "\e[?1000h\e[?1006h");

        // Act
        string? press = MouseEncoder.Encode(t, MouseEventKind.Press, MouseButton.Left, 4, 2, MouseModifiers.None);
        string? release = MouseEncoder.Encode(t, MouseEventKind.Release, MouseButton.Left, 4, 2, MouseModifiers.None);
        string? wheel = MouseEncoder.Encode(t, MouseEventKind.Press, MouseButton.WheelUp, 0, 0, MouseModifiers.None);
        bool wantsDrags = MouseEncoder.Wants(t, MouseEventKind.Motion, buttonHeld: true);

        // Assert
        await Assert.That(press).IsEqualTo("\e[<0;5;3M");
        await Assert.That(release).IsEqualTo("\e[<0;5;3m");
        await Assert.That(wheel).IsEqualTo("\e[<64;1;1M");
        await Assert.That(wantsDrags).IsFalse();
    }

    [Test]
    public async Task MouseEncodingLegacyWithButtonMotion()
    {
        // Arrange
        var t = Feed(new Terminal(80, 24), "\e[?1000h\e[?1006h");
        Feed(t, "\e[?1006l\e[?1002h");

        // Act
        string? release = MouseEncoder.Encode(t, MouseEventKind.Release, MouseButton.Left, 2, 1, MouseModifiers.None);
        bool wantsDrags = MouseEncoder.Wants(t, MouseEventKind.Motion, buttonHeld: true);

        // Assert
        await Assert.That(release).IsEqualTo("\e[M##\"");
        await Assert.That(wantsDrags).IsTrue();
    }
}
