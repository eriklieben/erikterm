using System.Diagnostics;
using System.Text;
using ErikTerm.App;
using ErikTerm.App.Compare;
using ErikTerm.Ui.Compare;
using static ErikTerm.Tests.CompareFixtures;

namespace ErikTerm.Tests;

/// <summary>The side-by-side compare: noise rules per language, alignment under the rules, screen behaviour, git sources.</summary>
public class CompareScreenTests
{
    private sealed class InlineBackground : IBackground
    {
        public void Run<T>(Func<T> work, Action<T> done) => done(work());
    }

    private static readonly NoiseRules Off = new(false, false, false);
    private static readonly NoiseRules Spaces = new(Whitespace: true, false, false);
    private static readonly NoiseRules Comments = new(false, false, Comments: true);
    private static readonly NoiseRules Eols = new(false, LineEndings: true, false);

    private static CompareResult Compare(string left, string right, NoiseRules rules, string language = ".ts") =>
        CompareEngine.Compute(TextDocument.FromText(left), TextDocument.FromText(right), rules, LanguageSyntax.For("f" + language), LanguageSyntax.For("f" + language));

    private static string Key(string line, NoiseRules rules, string language = ".ts") =>
        new LineNormalizer(rules, LanguageSyntax.For("f" + language)).Normalize(line).Key;

    private static CompareScreen Screen(string left, string right, string title = "a.ts")
    {
        var screen = new CompareScreen(new InlineBackground(),
            () => new CompareSide(title, "main", TextDocument.FromText(left)),
            () => new CompareSide(title, "branch", TextDocument.FromText(right)));
        screen.Start();
        return screen;
    }

    // ---- Language table ------------------------------------------------------------------------------

    [Test]
    [Arguments("a.ts", "//")]
    [Arguments("a.py", "#")]
    [Arguments("a.sql", "--")]
    [Arguments("a.rs", "//")]
    [Arguments("a.cs", "//")]
    [Arguments("a.toml", "#")]
    [Arguments("a.lua", "--")]
    [Arguments("a.sh", "#")]
    [Arguments("Makefile", "#")]
    public async Task LineCommentTokenPerLanguage(string path, string token)
    {
        // Act
        var syntax = LanguageSyntax.For(path);

        // Assert
        await Assert.That(syntax).IsNotNull();
        await Assert.That(syntax!.LineComments).Contains(token);
    }

    [Test]
    public async Task UnknownExtensionHasNoSyntax()
    {
        // Act
        var syntax = LanguageSyntax.For("notes.xyz");

        // Assert
        await Assert.That(syntax).IsNull();
    }

    // ---- Normalization --------------------------------------------------------------------------------

    [Test]
    public async Task WhitespaceRuleRemovesWhitespaceOutsideStrings()
    {
        // Act
        string key = Key("  const  x  =  1 ;\n", Spaces);

        // Assert
        await Assert.That(key).IsEqualTo("constx=1;\n");
    }

    [Test]
    public async Task WhitespaceInsideStringsIsSignificant()
    {
        // Act
        string key = Key("  s = \"a  b\" ;\n", Spaces);

        // Assert
        await Assert.That(key).IsEqualTo("s=\"a  b\";\n");
    }

    [Test]
    public async Task CommentRuleStripsTrailingComment()
    {
        // Act
        string key = Key("  total += 1; // bump\n", Comments);

        // Assert
        await Assert.That(key).IsEqualTo("  total += 1;\n");
    }

    [Test]
    public async Task CommentRuleKeepsSlashesInStrings()
    {
        // Act
        string key = Key("url = \"http://example.com\";\n", Comments);

        // Assert
        await Assert.That(key).IsEqualTo("url = \"http://example.com\";\n");
    }

    [Test]
    public async Task CommentOnlyLineIsIgnorable()
    {
        // Act
        var line = new LineNormalizer(Comments, LanguageSyntax.For("f.ts")).Normalize("  // note\n");

        // Assert
        await Assert.That(line.Ignorable).IsTrue();
    }

    [Test]
    public async Task BlockCommentSpansLines()
    {
        // Arrange
        var normalizer = new LineNormalizer(Comments, LanguageSyntax.For("f.ts"));

        // Act
        var first = normalizer.Normalize("a /* start\n");
        var inside = normalizer.Normalize("still comment\n");
        var last = normalizer.Normalize("end */ b\n");

        // Assert
        await Assert.That(first.Key).IsEqualTo("a\n");
        await Assert.That(inside.Ignorable).IsTrue();
        await Assert.That(last.Key).IsEqualTo(" b\n");
    }

    [Test]
    public async Task HashCommentInPython()
    {
        // Act
        string key = Key("x = 1  # set x\n", Comments, ".py");

        // Assert
        await Assert.That(key).IsEqualTo("x = 1\n");
    }

    [Test]
    public async Task HashInsideARustStringIsNotAComment()
    {
        // Act
        string key = Key("let s = \"// not\"; // yes\n", Comments, ".rs");

        // Assert
        await Assert.That(key).IsEqualTo("let s = \"// not\";\n");
    }

    [Test]
    public async Task RustLifetimeIsNotAStringOpener()
    {
        // Act
        string key = Key("fn f<'a>(x: &'a str) {} // done\n", Comments, ".rs");

        // Assert
        await Assert.That(key).IsEqualTo("fn f<'a>(x: &'a str) {}\n");
    }

    [Test]
    public async Task SqlCommentIsDoubleDash()
    {
        // Act
        string key = Key("select 1 -- pick\n", Comments, ".sql");

        // Assert
        await Assert.That(key).IsEqualTo("select 1\n");
    }

    [Test]
    public async Task LineEndingRuleDropsTheTerminator()
    {
        // Act
        string key = Key("line\r\n", Eols);

        // Assert
        await Assert.That(key).IsEqualTo("line");
    }

    [Test]
    public async Task WithoutLineEndingRuleTheTerminatorIsPartOfTheKey()
    {
        // Act
        string key = Key("line\r\n", Off);

        // Assert
        await Assert.That(key).IsEqualTo("line\r\n");
    }

    [Test]
    public async Task UnknownLanguageStillTrimsWhitespace()
    {
        // Act
        string key = Key("a  b\n", Spaces, ".xyz");

        // Assert
        await Assert.That(key).IsEqualTo("ab\n");
    }

    // ---- Compare results ------------------------------------------------------------------------------

    [Test]
    public async Task IdenticalFilesHaveNoDifferences()
    {
        // Act
        var result = Compare("a\nb\n", "a\nb\n", NoiseRules.Default);

        // Assert
        await Assert.That(result.Differences).IsEqualTo(0);
        await Assert.That(result.Rows.All(r => r.Kind == RowKind.Equal)).IsTrue();
    }

    [Test]
    public async Task InsertedLineMakesAGapRowOnTheOtherSide()
    {
        // Act
        var result = Compare("a\nc\n", "a\nb\nc\n", Off);

        // Assert
        await Assert.That(result.Rows.Single(r => r.Kind != RowKind.Equal)).IsEqualTo(new CompareRow(-1, 1, RowKind.Important, 0));
    }

    [Test]
    public async Task ChangedLineIsOneRowWithBothSides()
    {
        // Act
        var result = Compare("x\nold line here\ny\n", "x\nnew line here\ny\n", Off);

        // Assert
        var changed = result.Rows.Single(r => r.Kind != RowKind.Equal);
        await Assert.That(changed.Left).IsEqualTo(1);
        await Assert.That(changed.Right).IsEqualTo(1);
    }

    [Test]
    public async Task WhitespaceOnlyDifferenceIsUnimportantWithTheRule()
    {
        // Act
        var result = Compare("a\n  x;\nb\n", "a\n    x;\nb\n", Spaces);

        // Assert
        await Assert.That(result.Rows[1].Kind).IsEqualTo(RowKind.Unimportant);
        await Assert.That(result.Unimportant).IsEqualTo(1);
    }

    [Test]
    public async Task SameDifferenceIsImportantWithoutTheRule()
    {
        // Act
        var result = Compare("a\n  x;\nb\n", "a\n    x;\nb\n", Off);

        // Assert
        await Assert.That(result.Blocks[0].Important).IsTrue();
        await Assert.That(result.Unimportant).IsEqualTo(0);
    }

    [Test]
    public async Task IgnoredDifferencesStayOnScreen()
    {
        // Arrange — only whitespace differs; the rule marks it, it never removes it
        var result = Compare("a\n b\nc\n", "a\n  b\nc\n", Spaces);

        // Act
        var kinds = result.Rows.Select(r => r.Kind).ToArray();

        // Assert — the row is there, and still shows both sides
        await Assert.That(kinds.Length).IsEqualTo(3);
        await Assert.That(result.Rows[1].Left).IsEqualTo(1);
        await Assert.That(result.Rows[1].Right).IsEqualTo(1);
    }

    [Test]
    public async Task CommentOnlyAddedLinesAreUnimportantUnderCommentRule()
    {
        // Act
        var result = Compare("a\nb\n", "a\n// note\nb\n", Comments);

        // Assert
        await Assert.That(result.Blocks.Single().Important).IsFalse();
        await Assert.That(result.Rows[1].Right).IsEqualTo(1);
    }

    [Test]
    public async Task LineEndingDifferenceCountsOnlyWithoutTheRule()
    {
        // Act
        var with = Compare("a\r\nb\r\n", "a\nb\n", Eols);
        var without = Compare("a\r\nb\r\n", "a\nb\n", Off);

        // Assert
        await Assert.That(with.Blocks.All(b => !b.Important)).IsTrue();
        await Assert.That(without.Blocks.Any(b => b.Important)).IsTrue();
    }

    [Test]
    public async Task SummaryCountsImportantAndUnimportantSeparately()
    {
        // Arrange — one real change, then a comment change, then a whitespace-only change, each separated by equal lines
        const string Left = "real1\nx\n// old note\ny\n  pad\nz\n";
        const string Right = "real2\nx\n// new note\ny\n    pad\nz\n";

        // Act
        var result = Compare(Left, Right, NoiseRules.Default);

        // Assert
        await Assert.That(result.Differences).IsEqualTo(3);
        await Assert.That(result.Unimportant).IsEqualTo(2);
        await Assert.That(result.Summary).IsEqualTo("3 differences, 2 unimportant");
    }

    [Test]
    public async Task ReindentedBlockDoesNotDisturbTheRealChangeUnderWhitespaceRule()
    {
        // Arrange — every line indented; only one line really changed
        const string Left = "a\nb\nc\nd\ne\n";
        const string Right = "  a\n  b\n  CHANGED\n  d\n  e\n";

        // Act
        var result = Compare(Left, Right, Spaces);

        // Assert
        await Assert.That(result.Blocks.Count(b => b.Important)).IsEqualTo(1);
        await Assert.That(result.Rows.Count(r => r.Kind == RowKind.Important)).IsEqualTo(1);
    }

    // ---- The screen -----------------------------------------------------------------------------------

    [Test]
    public async Task ScreenShowsBothColumnsAndSummary()
    {
        // Arrange
        var screen = Screen("alpha\nbeta\n", "alpha\nBETA\n");

        // Act
        var frame = Render(screen, 140, 30);

        // Assert
        await Assert.That(frame.AllText()).Contains("alpha");
        await Assert.That(frame.AllText()).Contains("BETA");
        await Assert.That(frame.AllText()).Contains("1 difference");
    }

    [Test]
    public async Task NextDifferenceKeyMovesTheCursor()
    {
        // Arrange
        var screen = Screen("a\nb\nc\nd\ne\nf\n", "A\nb\nc\nd\ne\nF\n");
        Render(screen, 140, 30);

        // Act
        screen.OnKey(KeyEvent.OfChar(']'));
        var frame = Render(screen, 140, 30);

        // Assert — the detail pane shows the row of the second difference
        await Assert.That(frame.AllText()).Contains("F");
    }

    [Test]
    public async Task FilterKeyCyclesViews()
    {
        // Arrange
        var screen = Screen("alpha\nb\nc\nd\ne\nf\ng\nh\ni\n", "alpha\nb\nc\nd\nX\nf\ng\nh\ni\n");
        Render(screen, 140, 30);

        // Act
        screen.OnKey(KeyEvent.OfChar('d'));
        var diffsOnly = Render(screen, 140, 30).AllText();
        screen.OnKey(KeyEvent.OfChar('d'));
        var context = Render(screen, 140, 30).AllText();

        // Assert
        await Assert.That(screen.Filter).IsEqualTo(CompareFilter.Context);
        await Assert.That(diffsOnly).DoesNotContain("alpha");
        await Assert.That(context).Contains("equal lines");
    }

    [Test]
    public async Task RulesPopupTogglesWhitespaceAndRecomputes()
    {
        // Arrange
        var screen = Screen("a\n  x\nb\n", "a\n    x\nb\n");
        Render(screen, 140, 30);
        int before = screen.Result!.Unimportant;

        // Act
        screen.OnKey(KeyEvent.OfChar('i'));
        screen.OnKey(KeyEvent.OfChar('1'));

        // Assert — off now, so the whitespace change is a real one
        await Assert.That(before).IsEqualTo(1);
        await Assert.That(screen.Rules.Whitespace).IsFalse();
        await Assert.That(screen.Result!.Unimportant).IsEqualTo(0);
    }

    [Test]
    public async Task ClickingAFilterButtonSwitchesView()
    {
        // Arrange
        var screen = Screen("a\nb\nc\n", "a\nX\nc\n");
        var frame = Render(screen, 140, 30);
        var hit = frame.Hits.First(h => h.Y == 0 && frame.RowText(0).AsSpan(h.X, Math.Min(h.Width, frame.RowText(0).Length - h.X)).Contains("Differences", StringComparison.Ordinal));

        // Act
        screen.OnMouse(new MouseEvent(MouseKind.Press, hit.X + 1, hit.Y), frame);

        // Assert
        await Assert.That(screen.Filter).IsEqualTo(CompareFilter.Differences);
    }

    [Test]
    public async Task LineEndingDifferenceIsNamedOnTheRow()
    {
        // Arrange — same text, different terminator, with the rule off
        var screen = new CompareScreen(new InlineBackground(),
            () => new CompareSide("a", "", TextDocument.FromText("x\r\n")),
            () => new CompareSide("b", "", TextDocument.FromText("x\n")));
        screen.Start();
        screen.OnKey(KeyEvent.OfChar('i'));
        screen.OnKey(KeyEvent.OfChar('i'));

        // Act
        var frame = Render(screen, 140, 30);

        // Assert
        await Assert.That(frame.AllText()).Contains("CRLF");
    }

    [Test]
    public async Task BinaryFilesShowAMessage()
    {
        // Arrange
        var screen = new CompareScreen(new InlineBackground(),
            () => new CompareSide("a.bin", "", TextDocument.Decode([1, 0, 2])),
            () => new CompareSide("b.bin", "", TextDocument.Decode([1, 0, 3])));
        screen.Start();

        // Act
        var frame = Render(screen, 140, 30);

        // Assert
        await Assert.That(frame.AllText()).Contains("Binary files");
    }

    [Test]
    public async Task HugeFileShowsAMessageInsteadOfHanging()
    {
        // Arrange
        var screen = new CompareScreen(new InlineBackground(),
            () => new CompareSide("big.log", "", TextDocument.TooLarge(TextDocument.MaxBytes + 1)),
            () => new CompareSide("b", "", TextDocument.FromText("x\n")));
        screen.Start();

        // Act
        var frame = Render(screen, 140, 30);

        // Assert
        await Assert.That(frame.AllText()).Contains("too large");
    }

    [Test]
    public async Task HundredThousandLinesComputeAndRenderQuickly()
    {
        // Arrange — a change every 500 lines plus a whitespace noise line every 100
        var left = new StringBuilder();
        var right = new StringBuilder();
        for (int i = 0; i < 100_000; i++)
        {
            left.Append("line ").Append(i).Append('\n');
            if (i % 500 == 0) right.Append("line ").Append(i).Append(" changed\n");
            else if (i % 100 == 0) right.Append("  line ").Append(i).Append('\n');
            else right.Append("line ").Append(i).Append('\n');
        }
        var stopwatch = Stopwatch.StartNew();
        var screen = new CompareScreen(new InlineBackground(),
            () => new CompareSide("big.txt", "", TextDocument.FromText(left.ToString())),
            () => new CompareSide("big.txt", "", TextDocument.FromText(right.ToString())));

        // Act
        screen.Start();
        Render(screen, 160, 50);
        stopwatch.Stop();

        // Assert
        await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(5000);
        await Assert.That(screen.Result!.Differences).IsGreaterThan(100);
    }

    [Test]
    public async Task SpecWithColonAndNoFileIsTreatedAsGit()
    {
        // Act
        bool git = CompareTool.LooksLikeGitObject("main:src/a.ts");

        // Assert
        await Assert.That(git).IsTrue();
    }

    [Test]
    public async Task WindowsDrivePathIsNotAGitObject()
    {
        // Act
        bool git = CompareTool.LooksLikeGitObject(@"C:\work\a.ts");

        // Assert
        await Assert.That(git).IsFalse();
    }

    [Test]
    public async Task CompareIsItsOwnEntry()
    {
        // Act
        var entry = EntryCommand.Classify(["compare", "a", "b"], invokedAsTmux: false, "--helper");

        // Assert
        await Assert.That(entry).IsEqualTo(EntryKind.Compare);
    }

    // ---- Real git (temp repositories only) -------------------------------------------------------

    private static (string, byte[]) Keys(string waitFor, string keys) => (waitFor, Encoding.UTF8.GetBytes(keys));

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task ComparesTwoGitRevisionsOfAFile()
    {
        // Arrange
        using var repo = new TempRepo();
        repo.Write("src/a.ts", "first\nvalue = 1;\nlast\n");
        repo.CommitAll("one");
        repo.Write("src/a.ts", "first\nvalue = 2;\nlast\n");
        repo.CommitAll("two");
        var terminal = new FakeTerminal(140, 30, Keys("1 difference", "q"));

        // Act
        int exit = CompareTool.Run(terminal, "HEAD~1:src/a.ts", "HEAD:src/a.ts", repo.Dir);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(terminal.OutputText).Contains("1 difference");
        await Assert.That(terminal.OutputText).Contains("HEAD~1");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task FileOnOneSideAndGitObjectOnTheOtherWorks()
    {
        // Arrange
        using var repo = new TempRepo();
        repo.Write("a.txt", "one\nchanged\n");
        repo.CommitAll("one");
        repo.Write("copy.txt", "one\nchanged\n");
        var terminal = new FakeTerminal(140, 30, Keys("No differences", "q"));

        // Act
        int exit = CompareTool.Run(terminal, "HEAD:a.txt", "copy.txt", repo.Dir);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(terminal.OutputText).Contains("No differences");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task FolderIsRefusedWithAMessage()
    {
        // Arrange
        using var repo = new TempRepo();
        Directory.CreateDirectory(Path.Combine(repo.Dir, "sub"));
        repo.Write("f.txt", "x\n");
        var terminal = new FakeTerminal(140, 30, Keys("is a folder", "q"));

        // Act
        int exit = CompareTool.Run(terminal, "sub", "f.txt", repo.Dir);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(terminal.OutputText).Contains("is a folder");
    }

    [Test]
    [NotInParallel(Isolation.Processes)]
    public async Task PathWithSpacesComparesThroughGit()
    {
        // Arrange
        using var repo = new TempRepo();
        repo.Write("my dir/a b.txt", "one\n");
        repo.CommitAll("one");
        repo.Write("my dir/a b.txt", "two\n");
        var terminal = new FakeTerminal(140, 30, Keys("1 difference", "q"));

        // Act
        int exit = CompareTool.Run(terminal, "HEAD:my dir/a b.txt", "my dir/a b.txt", repo.Dir);

        // Assert
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(terminal.OutputText).Contains("1 difference");
    }
}
