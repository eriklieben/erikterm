using System.Text;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Search;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>Searching every pane at once: the off-thread scan over real terminals, and the list and jump around it.</summary>
public class PaneSearchTests
{
    private static readonly PaneSearchLimits Roomy = new(ChunkRows: 64, PerPane: 1000, Total: 10000, MaxTimeouts: 8);

    private static Terminal Term(int cols, int rows, int scrollback, params string[] lines)
    {
        var t = new Terminal(cols, rows, scrollback);
        Feed(t, lines);
        return t;
    }

    private static void Feed(Terminal t, params string[] lines) =>
        t.Feed(Encoding.UTF8.GetBytes(string.Join("", lines.Select(l => l + "\r\n"))));

    private static PaneSearchTarget<string> Target(string name, Terminal t) => new(name, t, name, "Tab 1");

    private static PaneSearchResults<string> Scan(string pattern, params PaneSearchTarget<string>[] targets) =>
        Scan(new PaneSearchQuery(pattern, false, false), Roomy, targets);

    private static PaneSearchResults<string> Scan(PaneSearchQuery query, PaneSearchLimits limits, params PaneSearchTarget<string>[] targets)
    {
        var scan = new PaneSearchScan<string>(new Lock(), targets, query, limits, CancellationToken.None);
        scan.Run();
        return scan.Snapshot();
    }

    private static string TextOf(Terminal t, SearchMatch m) => t.GetText(m.StartLine, m.StartCol, m.EndLine, m.EndCol);

    // ---- Matching --------------------------------------------------------------------------------------------------

    [Test]
    public async Task Scan_FindsMatchesInEveryPane_GroupedByPane()
    {
        // Arrange
        var first = Term(40, 6, 100, "alpha needle", "beta", "needle gamma");
        var second = Term(40, 6, 100, "nothing here");
        var third = Term(40, 6, 100, "another NEEDLE");

        // Act
        var results = Scan("needle", Target("first", first), Target("second", second), Target("third", third));

        // Assert
        await Assert.That(results.Done).IsTrue();
        await Assert.That(results.Incomplete).IsFalse();
        await Assert.That(results.Total).IsEqualTo(3);
        await Assert.That(results.Groups.Select(g => g.Target.Pane).ToList()).IsEquivalentTo(new[] { "first", "third" }, CollectionOrdering.Matching);
        await Assert.That(results.Groups[0].Hits.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Scan_ListsNewestLineFirst_AndMapsMatchesBackToTheirText()
    {
        // Arrange
        var t = Term(40, 6, 100, "needle one", "needle two");

        // Act
        var results = Scan("needle", Target("p", t));

        // Assert
        var hits = results.Groups[0].Hits;
        await Assert.That(hits[0].Preview).IsEqualTo("needle two");
        await Assert.That(hits[1].Preview).IsEqualTo("needle one");
        await Assert.That(TextOf(t, hits[0].Match)).IsEqualTo("needle");
    }

    [Test]
    public async Task Scan_FindsAWordBrokenAcrossASoftWrap()
    {
        // Arrange: a 10-column pane wraps inside the word
        var t = Term(10, 4, 100, "xxxxxxxneedle");

        // Act
        var results = Scan("needle", Target("p", t));

        // Assert
        var hit = results.Groups[0].Hits.Single();
        await Assert.That(hit.Match.EndLine).IsEqualTo(hit.Match.StartLine + 1);
        await Assert.That(TextOf(t, hit.Match)).IsEqualTo("needle");
    }

    [Test]
    public async Task Scan_ReadsTrimmedScrollbackRows()
    {
        // Arrange: short lines that scrolled off, compacted to fewer cells than the row is wide
        var t = Term(80, 4, 100, Enumerable.Range(0, 30).Select(i => $"short {i} needle").ToArray());
        t.CompactScrollback(1000);
        var trimmed = Enumerable.Range(0, 10).Select(i => t.GetLine(t.FirstLine + i)!).ToList();
        await Assert.That(trimmed.All(r => r.IsTrimmed)).IsTrue();

        // Act
        var results = Scan("needle", Target("p", t));

        // Assert
        await Assert.That(results.Total).IsEqualTo(30);
        var oldest = results.Groups[0].Hits[^1];
        await Assert.That(oldest.Preview).IsEqualTo("short 0 needle");
        await Assert.That(TextOf(t, oldest.Match)).IsEqualTo("needle");
    }

    [Test]
    public async Task Scan_MatchIsSameAsThePerPaneSearch_IncludingCaseAndRegex()
    {
        // Arrange
        var t = Term(40, 8, 100, "Error: disk full", "error: retry", "warn 42");
        var single = new TerminalSearch();
        single.SetQuery("^err", caseSensitive: false, regex: true);
        while (!single.Complete) single.Step(t);

        // Act
        var regex = Scan(new PaneSearchQuery("^err", false, true), Roomy, Target("p", t));
        var cased = Scan(new PaneSearchQuery("Error", true, false), Roomy, Target("p", t));

        // Assert
        await Assert.That(regex.Groups[0].Hits.Select(h => h.Match).OrderBy(m => m.StartLine).ToList())
            .IsEquivalentTo(single.Matches.OrderBy(m => m.StartLine).ToList(), CollectionOrdering.Matching);
        await Assert.That(cased.Total).IsEqualTo(1);
    }

    [Test]
    public async Task Scan_InvalidRegex_ReportsTheErrorWithoutRows()
    {
        // Arrange
        var t = Term(40, 4, 100, "text");

        // Act
        var results = Scan(new PaneSearchQuery("(", false, true), Roomy, Target("p", t));

        // Assert
        await Assert.That(results.Error).IsNotNull();
        await Assert.That(results.Total).IsEqualTo(0);
        await Assert.That(results.StatusText()).IsEqualTo("Invalid regex");
    }

    // ---- Timeouts and the lock -------------------------------------------------------------------------------------

    private static readonly string Pathological = new string('a', 34) + "!";

    [Test]
    public async Task Scan_RegexTimeout_MarksTheListIncomplete_AndGivesUpOnThePane()
    {
        // Arrange: this backtracks far past the 250 ms limit, line after line
        var t = Term(200, 8, 100, Pathological, Pathological, Pathological, Pathological);
        var limits = Roomy with { MaxTimeouts = 2 };
        var query = new PaneSearchQuery("(a+)+$", false, true);

        // Act
        var results = Scan(query, limits, Target("p", t));

        // Assert
        await Assert.That(results.Done).IsTrue();
        await Assert.That(results.TimedOut).IsTrue();
        await Assert.That(results.Incomplete).IsTrue();
        await Assert.That(results.StatusText()).Contains("timed out");
    }

    [Test]
    public async Task Scan_DoesNotHoldTheTerminalLockWhileMatching()
    {
        // Arrange: each slow line keeps the matcher busy for 250 ms
        var t = Term(200, 8, 100, Pathological, Pathological, Pathological);
        var gate = new Lock();
        var scan = new PaneSearchScan<string>(gate, [Target("p", t)], new PaneSearchQuery("(a+)+$", false, true), Roomy with { MaxTimeouts = 3 }, CancellationToken.None);

        // Act
        var worker = Task.Run(scan.Run);
        await Task.Delay(100); // well inside the first timeout
        bool free = gate.TryEnter(TimeSpan.FromMilliseconds(100));
        if (free) gate.Exit();
        await worker;

        // Assert
        await Assert.That(free).IsTrue();
    }

    // ---- Cancellation ----------------------------------------------------------------------------------------------

    [Test]
    public async Task Scan_Cancelled_StopsWithoutResults()
    {
        // Arrange
        var gate = new Lock();
        var a = Term(40, 6, 100, "needle");
        var b = Term(40, 6, 100, "needle");
        using var cancel = new CancellationTokenSource();
        var scan = new PaneSearchScan<string>(gate, [Target("a", a), Target("b", b)], new PaneSearchQuery("needle", false, false), Roomy, cancel.Token);

        // Act: the worker stops at the terminal lock, the query is replaced, then it gets the lock
        Task worker;
        lock (gate)
        {
            worker = Task.Run(scan.Run);
            Thread.Sleep(50);
            cancel.Cancel();
        }
        await worker.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        var results = scan.Snapshot();
        await Assert.That(results.Done).IsTrue();
        await Assert.That(results.Total).IsEqualTo(0);
    }

    [Test]
    public async Task Window_NewQuery_CancelsTheScanBefore()
    {
        // Arrange: the first scan is held up until after the second query
        var window = new FakeWindow();
        var pane = window.AddPane("p", "needle");
        var runs = new List<Action>();
        var search = new WindowPaneSearch<FakePane>(window, window.Gate, runs.Add, Roomy);
        search.Open(0);
        search.TextInput("n", 0);

        // Act
        search.TextInput("e", 0);
        foreach (var run in runs) run();
        search.Tick();

        // Assert
        await Assert.That(runs.Count).IsEqualTo(2);
        await Assert.That(window.Toasts.Count).IsEqualTo(0);
        await Assert.That(search.KeyDown(Sdl.K_RETURN, 0, 0)).IsTrue();
        await Assert.That(window.Reveals.Single().Pane).IsEqualTo(pane);
        await Assert.That(window.Reveals.Single().Query.Pattern).IsEqualTo("ne");
    }

    // ---- The cap ---------------------------------------------------------------------------------------------------

    [Test]
    public async Task Scan_CapsPerPane_KeepsTheNewest_AndSaysSo()
    {
        // Arrange
        var t = Term(40, 6, 1000, Enumerable.Range(0, 300).Select(i => $"needle {i}").ToArray());
        var limits = Roomy with { PerPane = 20 };

        // Act
        var results = Scan(new PaneSearchQuery("needle", false, false), limits, Target("p", t));

        // Assert
        await Assert.That(results.Total).IsEqualTo(20);
        await Assert.That(results.Capped).IsTrue();
        await Assert.That(results.Groups[0].Capped).IsTrue();
        await Assert.That(results.Groups[0].Hits[0].Preview).IsEqualTo("needle 299");
        await Assert.That(results.StatusText()).Contains("20+ matches").And.Contains("too many");
    }

    [Test]
    public async Task Scan_ExactlyAtTheCap_IsNotCapped()
    {
        // Arrange
        var t = Term(40, 6, 1000, Enumerable.Range(0, 20).Select(i => $"needle {i}").ToArray());

        // Act
        var results = Scan(new PaneSearchQuery("needle", false, false), Roomy with { PerPane = 20 }, Target("p", t));

        // Assert
        await Assert.That(results.Total).IsEqualTo(20);
        await Assert.That(results.Capped).IsFalse();
    }

    [Test]
    public async Task Scan_TotalCap_StopsAndLeavesRemainingPanesOut()
    {
        // Arrange
        var a = Term(40, 6, 1000, Enumerable.Range(0, 30).Select(i => $"needle {i}").ToArray());
        var b = Term(40, 6, 1000, "needle");
        var limits = Roomy with { PerPane = 25, Total = 25 };

        // Act
        var results = Scan(new PaneSearchQuery("needle", false, false), limits, Target("a", a), Target("b", b));

        // Assert
        await Assert.That(results.Total).IsEqualTo(25);
        await Assert.That(results.Capped).IsTrue();
        await Assert.That(results.Groups.Length).IsEqualTo(1);
    }

    // ---- Jump targets ----------------------------------------------------------------------------------------------

    [Test]
    public async Task Check_MatchSurvivesEvictionOfOlderLines()
    {
        // Arrange: search, then let enough output through that older lines are evicted
        var t = Term(40, 4, 50, Enumerable.Range(0, 40).Select(i => $"needle {i}").ToArray());
        var results = Scan("needle", Target("p", t));
        var surviving = results.Groups[0].Hits[0]; // the newest of the matches
        var firstBefore = t.FirstLine;

        // Act
        Feed(t, Enumerable.Range(0, 30).Select(i => $"later {i}").ToArray());

        // Assert: its line number still names the same text, and the first old line really went
        await Assert.That(t.FirstLine).IsGreaterThan(firstBefore);
        await Assert.That(PaneSearchScan<string>.Check(t, surviving)).IsEqualTo(PaneSearchJump.Go);
        await Assert.That(TextOf(t, surviving.Match)).IsEqualTo("needle");
        await Assert.That(t.GetText(surviving.Match.StartLine, 0, surviving.Match.StartLine, 39)).IsEqualTo(surviving.Preview);
    }

    [Test]
    public async Task Check_EvictedMatch_IsStale()
    {
        // Arrange
        var t = Term(40, 4, 50, Enumerable.Range(0, 40).Select(i => $"needle {i}").ToArray());
        var oldest = Scan("needle", Target("p", t)).Groups[0].Hits[^1];

        // Act
        Feed(t, Enumerable.Range(0, 100).Select(i => $"later {i}").ToArray());

        // Assert
        await Assert.That(oldest.Match.StartLine).IsLessThan(t.FirstLine);
        await Assert.That(PaneSearchScan<string>.Check(t, oldest)).IsEqualTo(PaneSearchJump.Stale);
    }

    [Test]
    public async Task Check_RewrittenScreenRow_IsStale()
    {
        // Arrange
        var t = new Terminal(40, 4, 50);
        t.Feed("needle"u8);
        var hit = Scan("needle", Target("p", t)).Groups[0].Hits.Single();

        // Act: the program rewrites the row in place
        t.Feed("\r\e[2Knoodle"u8);

        // Assert
        await Assert.That(PaneSearchScan<string>.Check(t, hit)).IsEqualTo(PaneSearchJump.Stale);
    }

    [Test]
    public async Task Check_AfterReflow_IsStale()
    {
        // Arrange: resizing renumbers the rows
        var t = Term(40, 4, 50, Enumerable.Range(0, 20).Select(i => $"needle {i}").ToArray());
        var hit = Scan("needle", Target("p", t)).Groups[0].Hits[0];

        // Act
        t.Resize(25, 4);

        // Assert
        await Assert.That(PaneSearchScan<string>.Check(t, hit)).IsEqualTo(PaneSearchJump.Stale);
    }

    [Test]
    public async Task Check_MatchAboveAFullScreenProgram_IsHidden()
    {
        // Arrange: scrollback holds the match while a program has the alternate screen
        var t = Term(40, 4, 50, Enumerable.Range(0, 20).Select(i => $"needle {i}").ToArray());
        t.Feed("\e[?1049h"u8);
        var hit = Scan("needle", Target("p", t)).Groups[0].Hits[0];

        // Act
        var jump = PaneSearchScan<string>.Check(t, hit);

        // Assert
        await Assert.That(jump).IsEqualTo(PaneSearchJump.Hidden);
    }

    // ---- The window --------------------------------------------------------------------------------------------------

    private sealed class FakePane(string name, Terminal term) : IPaneView
    {
        public int Id => Label.GetHashCode();
        public Terminal Term { get; } = term;
        public string Label { get; } = name;
        public bool LayoutHidden => false;
    }

    private sealed class FakeWindow : IPaneSearchWindow<FakePane>
    {
        public readonly Lock Gate = new();
        public readonly List<FakePane> Panes = [];
        public readonly List<(FakePane Pane, PaneSearchQuery Query, SearchMatch Match)> Reveals = [];
        public readonly List<string> Toasts = [];
        public int Closed;

        public FakePane AddPane(string name, params string[] lines)
        {
            var pane = new FakePane(name, Term(40, 6, 100, lines));
            Panes.Add(pane);
            return pane;
        }

        public Theme Theme { get; } = new();
        public bool SuppressText { get; set; }
        public void Redraw() { }
        public void Toast(ToastKind kind, string title, string? detail = null) => Toasts.Add(title);
        public Dictionary<KeyChord, ActionSpec> Keys { get; } = [];
        public List<PaneSearchTarget<FakePane>> PaneSearchTargets() => Panes.Select(p => new PaneSearchTarget<FakePane>(p, p.Term, p.Label, "Tab 1")).ToList();
        public string? SelectionForSearch() => null;
        public string PasteText(string? arg) => "";
        public void CloseOverlaysForPaneSearch(long now) => Closed++;
        public bool Revealable { get; set; } = true;
        public bool RevealSearchHit(FakePane pane, PaneSearchQuery query, SearchMatch match)
        {
            Reveals.Add((pane, query, match));
            return Revealable;
        }
    }

    private static WindowPaneSearch<FakePane> OpenSearch(FakeWindow window, string query)
    {
        var search = new WindowPaneSearch<FakePane>(window, window.Gate, run => run(), Roomy);
        search.Open(0);
        search.TextInput(query, 0);
        search.Tick();
        return search;
    }

    [Test]
    public async Task Window_Enter_JumpsToTheSelectedMatch_AndClosesTheList()
    {
        // Arrange
        var window = new FakeWindow();
        window.AddPane("quiet", "nothing");
        var pane = window.AddPane("noisy", "first needle", "second needle");
        var search = OpenSearch(window, "needle");

        // Act
        search.KeyDown(Sdl.K_DOWN, 0, 0);
        search.KeyDown(Sdl.K_RETURN, 0, 0);

        // Assert
        var reveal = window.Reveals.Single();
        await Assert.That(reveal.Pane).IsEqualTo(pane);
        await Assert.That(reveal.Query.Pattern).IsEqualTo("needle");
        await Assert.That(pane.Term.GetText(reveal.Match.StartLine, 0, reveal.Match.StartLine, 39)).IsEqualTo("first needle");
        await Assert.That(search.IsOpen).IsFalse();
    }

    [Test]
    public async Task Window_EvictedSelection_RefreshesInsteadOfJumping()
    {
        // Arrange
        var window = new FakeWindow();
        var pane = window.AddPane("p", "needle");
        var search = OpenSearch(window, "needle");
        Feed(pane.Term, Enumerable.Range(0, 300).Select(i => $"later {i}").ToArray());

        // Act
        search.KeyDown(Sdl.K_RETURN, 0, 0);
        search.Tick();

        // Assert
        await Assert.That(window.Reveals.Count).IsEqualTo(0);
        await Assert.That(window.Toasts.Single()).Contains("gone");
        await Assert.That(search.IsOpen).IsTrue();
    }

    [Test]
    public async Task Window_PaneClosedBeforeTheJump_SaysSo()
    {
        // Arrange
        var window = new FakeWindow { Revealable = false };
        window.AddPane("p", "needle");
        var search = OpenSearch(window, "needle");

        // Act
        search.KeyDown(Sdl.K_RETURN, 0, 0);

        // Assert
        await Assert.That(window.Toasts.Single()).Contains("gone");
    }

    [Test]
    public async Task Window_Escape_ClosesAndKeysDoNotReachTheTerminal()
    {
        // Arrange
        var window = new FakeWindow();
        var search = OpenSearch(window, "x");

        // Act
        bool took = search.KeyDown(Sdl.K_ESCAPE, 0, 0);
        bool afterClose = search.KeyDown(Sdl.K_ESCAPE, 0, 0);

        // Assert
        await Assert.That(took).IsTrue();
        await Assert.That(search.IsOpen).IsFalse();
        await Assert.That(afterClose).IsFalse();
    }

    [Test]
    public async Task Default_KeyIsBound_AndTheActionIsKnown()
    {
        // Arrange / Act
        var binding = DefaultKeys.Build(macOS: false).Single(b => b.Action.Name == "SearchAllPanes");

        // Assert
        await Assert.That(binding.Chord.ToString()).IsEqualTo("Ctrl+Shift+S");
    }
}
