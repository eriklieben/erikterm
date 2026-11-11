using System.Text;
using ErikTerm.App.Cli;
using ErikTerm.App.Panes;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.Tests;

/// <summary>A host that records what vim mode asks for, searching with the real TerminalSearch.</summary>
internal sealed class FakeVimHost(Terminal t) : IVimHost
{
    public readonly List<string> Yanks = [];
    public readonly List<string> Messages = [];
    public readonly List<string> Calls = [];
    public readonly List<ExCommand> Executed = [];
    public bool? Left;
    public string? Pattern;
    public string? ExecuteError;
    public readonly TerminalSearch Search = new();

    public void Yank(string text) => Yanks.Add(text);
    public void Leave(bool toLive) => Left = toLive;
    public void Message(string text, bool error) => Messages.Add(text);
    public void SearchChanged(string pattern) => Pattern = pattern;
    public void SearchCancelled(string? previous) => Pattern = previous;

    public VimPos? FindMatch(VimPos from, bool forward)
    {
        if (Pattern is null) return null;
        Search.SetQuery(VimSearch.ToRegex(Pattern), VimSearch.CaseSensitive(Pattern), true);
        while (!Search.Complete) Search.Step(t);
        int i = VimSearch.Next(Search.Matches, from, forward);
        return i < 0 ? null : new VimPos(Search.Matches[i].StartLine, Search.Matches[i].StartCol);
    }

    public void ClearHighlight() => Calls.Add("noh");
    public void PromptChanged(string text) => Calls.Add("prompt:" + text);
    public void PromptCancelled() => Calls.Add("prompt-cancel");
    public string? Execute(ExCommand command) { Executed.Add(command); return ExecuteError; }
    public IEnumerable<string> ActionNames => ["SplitHorizontal", "ActivatePaneDirection", "QuickSelect"];
    public IEnumerable<string> Arguments(string command) => command == "theme" ? ["Tokyo Night", "Gruvbox Dark", "Catppuccin Mocha"] : [];
    public void Open(VimOpen what, VimPos at) => Calls.Add($"open:{what}:{at.Line},{at.Col}");
    public void Hints() => Calls.Add("hints");
    public void Window(char command, int count) => Calls.Add($"window:{command}:{count}");
    public void Tab(bool previous, int count, int index) => Calls.Add($"tab:{previous}:{count}:{index}");
    public void Help() => Calls.Add("help");
}

public class VimKeyParserTests
{
    private static List<VimCommand> Parse(string notation, bool visual = false)
    {
        var p = new VimKeyParser();
        var list = new List<VimCommand>();
        foreach (var key in VimKeys.Parse(notation))
            if (p.Feed(key, visual, out var cmd) == VimParse.Complete) list.Add(cmd);
        return list;
    }

    public static IEnumerable<Func<(string Notation, string[] Keys)>> Notations() =>
    [
        () => ("5j<C-v>jy<Esc><lt><Enter><Space>", ["5", "j", "<C-v>", "j", "y", "<Esc>", "<", "<CR>", " "]),
        () => ("<C-w><", ["<C-w>", "<"]),
        () => ("<x>", ["<", "x", ">"]), // not a key name: literal characters
    ];

    [Test]
    [MethodDataSource(nameof(Notations))]
    public async Task Notation_SplitsNamedKeysAndLiterals(string notation, string[] expected)
    {
        // Act
        var keys = VimKeys.Parse(notation);

        // Assert
        await Assert.That(keys).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("5j", 5, '\0', "j", '\0')]
    [Arguments("2y3w", 6, 'y', "w", '\0')]
    [Arguments("yy", 0, 'y', "yy", '\0')]
    [Arguments("3yy", 3, 'y', "yy", '\0')]
    [Arguments("yiw", 0, 'y', "iw", '\0')]
    [Arguments("yo", 0, 'y', "yo", '\0')]
    [Arguments("y$", 0, 'y', "$", '\0')]
    [Arguments("gg", 0, '\0', "gg", '\0')]
    [Arguments("10G", 10, '\0', "G", '\0')]
    [Arguments("0", 0, '\0', "0", '\0')]
    [Arguments("20l", 20, '\0', "l", '\0')]
    [Arguments("f,", 0, '\0', "f", ',')]
    [Arguments("ma", 0, '\0', "m", 'a')]
    [Arguments("zz", 0, '\0', "zz", '\0')]
    [Arguments("[[", 0, '\0', "[[", '\0')]
    public async Task Counts_OperatorsAndPrefixes(string notation, int count, char op, string key, char arg)
    {
        // Arrange
        var expected = new VimCommand(count, op, key, arg);

        // Act
        var commands = Parse(notation);

        // Assert
        var command = await Assert.That(commands).HasSingleItem();
        await Assert.That(command).IsEqualTo(expected);
    }

    [Test]
    [Arguments("5<C-w>>", 5, '>')]
    [Arguments("<C-w>5>", 5, '>')]
    [Arguments("<C-w><C-h>", 0, 'h')]
    [Arguments("<C-w><Right>", 0, 'l')]
    public async Task CtrlW_TakesACountBeforeOrAfter_AndCtrlLetters(string notation, int count, char arg)
    {
        // Arrange
        var expected = new VimCommand(count, '\0', "<C-w>", arg);

        // Act
        var commands = Parse(notation);

        // Assert
        var command = await Assert.That(commands).HasSingleItem();
        await Assert.That(command).IsEqualTo(expected);
    }

    [Test]
    public async Task TextObjects_NotInNormalMode()
    {
        // Act
        var keys = Parse("iw").Select(c => c.Key).ToList();

        // Assert
        await Assert.That(keys).IsEquivalentTo(["i", "w"], CollectionOrdering.Matching); // normal mode: i leaves vim mode, w is a motion
    }

    [Test]
    [Arguments("iw", "iw")]
    [Arguments("ap", "ap")]
    public async Task TextObjects_InVisualMode(string notation, string expectedKey)
    {
        // Act
        var commands = Parse(notation, visual: true);

        // Assert
        var command = await Assert.That(commands).HasSingleItem();
        await Assert.That(command.Key).IsEqualTo(expectedKey);
    }

    [Test]
    public async Task EscapeCancelsAPendingCommand()
    {
        // One walk-through: every key goes to the same parser, and what it does depends on what is pending.

        // Arrange
        var p = new VimKeyParser();

        // Act
        var afterCount = p.Feed("5", false, out _);
        var afterOperator = p.Feed("y", false, out _);
        string pending = p.Pending;
        var afterEscape = p.Feed("<Esc>", false, out _);
        bool pendingAfterEscape = p.IsPending;
        var afterSecondEscape = p.Feed("<Esc>", false, out var esc);

        // Assert
        await Assert.That(afterCount).IsEqualTo(VimParse.Pending);
        await Assert.That(afterOperator).IsEqualTo(VimParse.Pending);
        await Assert.That(pending).IsEqualTo("5y");
        await Assert.That(afterEscape).IsEqualTo(VimParse.Cancelled);
        await Assert.That(pendingAfterEscape).IsFalse();
        await Assert.That(afterSecondEscape).IsEqualTo(VimParse.Complete);
        await Assert.That(esc.Key).IsEqualTo("<Esc>");
    }

    [Test]
    public async Task InvalidSequencesReset()
    {
        // One walk-through: the key after the invalid one shows that the parser started over.

        // Arrange
        var p = new VimKeyParser();

        // Act
        var afterPrefix = p.Feed("g", false, out _);
        var afterInvalid = p.Feed("q", false, out _);
        var afterNext = p.Feed("j", false, out var j);

        // Assert
        await Assert.That(afterPrefix).IsEqualTo(VimParse.Pending);
        await Assert.That(afterInvalid).IsEqualTo(VimParse.Invalid);
        await Assert.That(afterNext).IsEqualTo(VimParse.Complete);
        await Assert.That(j.Key).IsEqualTo("j");
    }
}

public class VimMotionTests
{
    internal static Terminal T(int cols, int rows, string text, int scrollback = 100)
    {
        var t = new Terminal(cols, rows, scrollback);
        t.Feed(Encoding.UTF8.GetBytes(text));
        return t;
    }

    internal static (VimEngine Vim, FakeVimHost Host) Vim(Terminal t, int row, int col)
    {
        var vim = new VimEngine(t, new VimPos(t.ScreenTopLine + row, col));
        return (vim, new FakeVimHost(t));
    }

    private static (long, int) Rel(Terminal t, VimPos p) => (p.Line - t.ScreenTopLine, p.Col);

    private static (long, int) After(Terminal t, int row, int col, string keys)
    {
        var (vim, host) = Vim(t, row, col);
        vim.FeedKeys(keys, host);
        return Rel(t, vim.Cursor);
    }

    [Test]
    [Arguments(0, 0, "3l", 0, 3)]
    [Arguments(0, 0, "99l", 0, 4)]      // not past the last character
    [Arguments(0, 4, "j", 1, 1)]        // clamped on the short line…
    [Arguments(0, 4, "jj", 2, 4)]       // …but the wanted column comes back
    [Arguments(2, 4, "2k", 0, 4)]
    [Arguments(0, 4, "3h", 0, 1)]
    [Arguments(0, 0, "$jj", 2, 10)]     // $ sticks to line ends
    public async Task HjklAndCounts_StayOnTheText(int row, int col, string keys, int expectedRow, int expectedCol)
    {
        // Arrange
        var t = T(20, 5, "hello\r\nab\r\nlonger line");

        // Act
        var cursor = After(t, row, col, keys);

        // Assert
        await Assert.That(cursor).IsEqualTo((expectedRow, expectedCol));
    }

    [Test]
    [Arguments(0, 10, "^", 0, 3)]
    [Arguments(0, 10, "0", 0, 0)]
    [Arguments(0, 0, "$", 0, 16)]
    public async Task LineMotions(int row, int col, string keys, int expectedRow, int expectedCol)
    {
        // Arrange
        var t = T(20, 3, "   indented  text  ");

        // Act
        var cursor = After(t, row, col, keys);

        // Assert
        await Assert.That(cursor).IsEqualTo((expectedRow, expectedCol));
    }

    [Test]
    [Arguments(0, 0, "w", 0, 3)]
    [Arguments(0, 0, "2w", 0, 4)]
    [Arguments(0, 0, "3w", 0, 9)]
    [Arguments(0, 0, "W", 0, 9)]
    [Arguments(0, 0, "e", 0, 2)]
    [Arguments(0, 0, "E", 0, 6)]
    [Arguments(0, 13, "b", 0, 9)]
    [Arguments(0, 9, "B", 0, 0)]
    [Arguments(0, 9, "ge", 0, 6)]
    [Arguments(0, 13, "ge", 0, 11)]
    public async Task Words_ClassesAndBigWords(int row, int col, string keys, int expectedRow, int expectedCol)
    {
        // Arrange
        var t = T(30, 3, "foo.bar  baz qux");

        // Act
        var cursor = After(t, row, col, keys);

        // Assert
        await Assert.That(cursor).IsEqualTo((expectedRow, expectedCol));
    }

    [Test]
    [Arguments(0, 0, "w", 1, 0)]
    [Arguments(0, 0, "2w", 2, 0)]
    [Arguments(0, 0, "e", 0, 2)]
    [Arguments(0, 0, "2e", 2, 2)]       // e skips the empty line
    [Arguments(2, 0, "b", 1, 0)]
    [Arguments(2, 0, "2b", 0, 0)]
    [Arguments(2, 4, "999w", 2, 6)]     // stops at the end of the buffer
    public async Task Words_CrossLinesAndStopAtEmptyLines(int row, int col, string keys, int expectedRow, int expectedCol)
    {
        // Arrange
        var t = T(20, 5, "abc\r\n\r\ndef ghi");

        // Act
        var cursor = After(t, row, col, keys);

        // Assert
        await Assert.That(cursor).IsEqualTo((expectedRow, expectedCol));
    }

    [Test]
    [Arguments(0, 0, "w", 0, 6)]
    [Arguments(0, 0, "2w", 1, 6)]       // "worl|dwide" is one word
    [Arguments(0, 6, "e", 1, 4)]
    [Arguments(1, 6, "b", 0, 6)]
    [Arguments(1, 2, "b", 0, 6)]
    public async Task Words_SoftWrapsJoinAWord(int row, int col, string keys, int expectedRow, int expectedCol)
    {
        // Arrange
        var t = T(10, 4, "hello worldwide end");
        bool firstRowWraps = t.GetScreenRow(0).Wrapped;

        // Act
        var cursor = After(t, row, col, keys);

        // Assert
        await Assert.That(firstRowWraps).IsTrue();
        await Assert.That(cursor).IsEqualTo((expectedRow, expectedCol));
    }

    /// <summary>Two wide CJK characters on the first line; an "e" with a combining acute (one cluster) on the second.</summary>
    private static Terminal Wide() => T(20, 3, "a中文b c\r\ne\u0301x y");

    [Test]
    [Arguments(0, 0, "l", 0, 1)]
    [Arguments(0, 0, "2l", 0, 3)]
    [Arguments(0, 0, "3l", 0, 5)]
    [Arguments(0, 5, "h", 0, 3)]
    [Arguments(0, 0, "w", 0, 7)]        // CJK counts as word characters
    [Arguments(0, 0, "$", 0, 7)]
    [Arguments(1, 0, "l", 1, 1)]        // é is one cell
    [Arguments(1, 4, "k", 0, 3)]        // lands on a wide character's first cell (col 4 is 文's second)
    public async Task WideCharactersAndClustersAreOnePosition(int row, int col, string keys, int expectedRow, int expectedCol)
    {
        // Arrange
        var t = Wide();

        // Act
        var cursor = After(t, row, col, keys);

        // Assert
        await Assert.That(cursor).IsEqualTo((expectedRow, expectedCol));
    }

    [Test]
    [Arguments(1, 0, "yiw", "e\u0301x")]
    [Arguments(0, 1, "vly", "中文")]
    public async Task WideCharactersAndClustersAreYankedWhole(int row, int col, string keys, string expected)
    {
        // Arrange
        var (vim, host) = Vim(Wide(), row, col);

        // Act
        vim.FeedKeys(keys, host);

        // Assert
        var yank = await Assert.That(host.Yanks).HasSingleItem();
        await Assert.That(yank).IsEqualTo(expected);
    }

    [Test]
    [Arguments(0, 0, "f,", 0, 1)]
    [Arguments(0, 0, "f,;", 0, 3)]
    [Arguments(0, 0, "3f,", 0, 5)]
    [Arguments(0, 0, "f,;,", 0, 1)]
    [Arguments(0, 0, "t,", 0, 0)]       // already next to it: stays
    [Arguments(0, 0, "t,;", 0, 2)]      // ; looks past it
    [Arguments(0, 6, "T,", 0, 6)]
    [Arguments(0, 6, "T,;", 0, 4)]
    [Arguments(0, 6, "F,;", 0, 3)]
    [Arguments(0, 0, "fz", 0, 0)]       // not found: no move
    public async Task FindChar_RepeatAndReverse(int row, int col, string keys, int expectedRow, int expectedCol)
    {
        // Arrange
        var t = T(20, 3, "a,b,c,d");

        // Act
        var cursor = After(t, row, col, keys);

        // Assert
        await Assert.That(cursor).IsEqualTo((expectedRow, expectedCol));
    }

    [Test]
    [Arguments(0, 1, 0, 7)]
    [Arguments(0, 7, 0, 1)]
    [Arguments(0, 3, 0, 5)]
    [Arguments(0, 0, 0, 7)]             // the first bracket after the cursor
    [Arguments(1, 0, 2, 4)]
    [Arguments(2, 4, 1, 0)]
    public async Task MatchingBrackets_AcrossLines(int row, int col, int expectedRow, int expectedCol)
    {
        // Arrange
        var t = T(20, 4, "f(a[b]c)\r\n{\r\n  x }");

        // Act
        var cursor = After(t, row, col, "%");

        // Assert
        await Assert.That(cursor).IsEqualTo((expectedRow, expectedCol));
    }

    [Test]
    public async Task ScrollbackBounds_TopBottomAndCounts()
    {
        // One walk-through: every motion starts where the previous one left the cursor (k at the very top after gg,
        // G from there back down to the prompt, then the numbered jumps).

        // Arrange
        var sb = new StringBuilder();
        for (int i = 0; i < 20; i++) sb.Append($"line {i}\r\n");
        var t = T(20, 5, sb.ToString() + "$ ");
        var (vim, host) = Vim(t, 4, 0);
        long firstLine = t.FirstLine;
        long promptLine = t.ScreenTopLine + t.CursorY;

        // Act
        vim.FeedKeys("gg", host);
        var afterGg = vim.Cursor;
        long viewAfterGg = t.ViewTopLine;
        vim.FeedKeys("k", host);
        long afterK = vim.Cursor.Line;
        vim.FeedKeys("G", host);
        long afterG = vim.Cursor.Line;
        vim.FeedKeys("5G", host);
        long after5G = vim.Cursor.Line;
        string textAt5G = t.GetText(after5G, 0, after5G, 19);
        vim.FeedKeys(":12<CR>", host);
        long afterEx12 = vim.Cursor.Line;

        // Assert
        await Assert.That(afterGg).IsEqualTo(new VimPos(firstLine, 0));
        await Assert.That(viewAfterGg).IsEqualTo(firstLine);         // the view follows
        await Assert.That(afterK).IsEqualTo(firstLine);
        await Assert.That(afterG).IsEqualTo(promptLine); // the prompt: the last line with anything on it
        await Assert.That(after5G).IsEqualTo(firstLine + 4);
        await Assert.That(textAt5G).IsEqualTo("line 4");
        await Assert.That(afterEx12).IsEqualTo(firstLine + 11);
    }

    [Test]
    public async Task ScreenAndScrollMotions()
    {
        // One walk-through: every key scrolls or moves from the view and cursor the previous key left, so the
        // expected positions add up along the way.

        // Arrange
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++) sb.Append($"line {i}\r\n");
        var t = T(20, 10, sb.ToString());
        var (vim, host) = Vim(t, 9, 0);
        long top = t.ViewTopLine;
        long lastLine = VimBuffer.LastLine(t);

        // Act
        vim.FeedKeys("H", host);
        long afterH = vim.Cursor.Line;
        vim.FeedKeys("L", host);
        long afterL = vim.Cursor.Line;
        vim.FeedKeys("<C-u>", host);
        long viewAfterCtrlU = t.ViewTopLine, afterCtrlU = vim.Cursor.Line;
        vim.FeedKeys("<C-b>", host);
        long viewAfterCtrlB = t.ViewTopLine, afterCtrlB = vim.Cursor.Line;
        vim.FeedKeys("<C-y>", host);
        long viewAfterCtrlY = t.ViewTopLine;
        vim.FeedKeys("zt", host);
        long viewAfterZt = t.ViewTopLine, afterZt = vim.Cursor.Line;
        vim.FeedKeys("zb", host);
        long viewAfterZb = t.ViewTopLine, afterZb = vim.Cursor.Line;
        vim.FeedKeys("M", host);
        long viewAfterM = t.ViewTopLine, afterM = vim.Cursor.Line;
        vim.FeedKeys("<C-e>", host);
        long viewAfterCtrlE = t.ViewTopLine;
        vim.FeedKeys("<C-d>", host);
        long viewAfterCtrlD = t.ViewTopLine;

        // Assert
        await Assert.That(afterH).IsEqualTo(top);
        await Assert.That(afterL).IsEqualTo(lastLine);
        await Assert.That(viewAfterCtrlU).IsEqualTo(top - 5);
        await Assert.That(afterCtrlU).IsEqualTo(lastLine - 5);
        await Assert.That(viewAfterCtrlB).IsEqualTo(top - 5 - 8);
        await Assert.That(afterCtrlB).IsLessThan(viewAfterCtrlB + t.Rows);
        await Assert.That(viewAfterCtrlY).IsEqualTo(top - 14);
        await Assert.That(viewAfterZt).IsEqualTo(afterZt);
        await Assert.That(viewAfterZb + t.Rows - 1).IsEqualTo(afterZb);
        await Assert.That(afterM).IsEqualTo(viewAfterM + 4);
        await Assert.That(viewAfterCtrlE).IsEqualTo(viewAfterM + 1);
        await Assert.That(viewAfterCtrlD).IsEqualTo(viewAfterCtrlE + 5);
    }

    [Test]
    public async Task Eviction_ClampsTheCursorAndDropsMarks()
    {
        // Arrange
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++) sb.Append($"old {i}\r\n");
        var t = T(20, 3, sb.ToString(), scrollback: 5);
        var (vim, host) = Vim(t, 0, 0);
        vim.FeedKeys("gg", host);
        long first = t.FirstLine;
        vim.FeedKeys("ma", host);
        long markedLine = vim.Marks['a'].Line;

        // Act
        for (int i = 0; i < 10; i++) t.Feed(Encoding.UTF8.GetBytes($"new {i}\r\n"));
        long firstAfterOutput = t.FirstLine;
        vim.Clamp();
        long cursorLine = vim.Cursor.Line;
        bool markKept = vim.Marks.ContainsKey('a');
        vim.FeedKeys("'a", host);

        // Assert
        await Assert.That(markedLine).IsEqualTo(first);
        await Assert.That(firstAfterOutput).IsGreaterThan(first);
        await Assert.That(cursorLine).IsEqualTo(firstAfterOutput);
        await Assert.That(markKept).IsFalse();
        await Assert.That(host.Messages).Contains("Mark not set");
    }

    [Test]
    public async Task OutputArrivingDoesNotMoveTheCursor()
    {
        // Arrange
        var t = T(20, 4, "a\r\nb\r\nc\r\n");
        var (vim, host) = Vim(t, 1, 0);
        long line = vim.Cursor.Line;

        // Act
        t.Feed(Encoding.UTF8.GetBytes("d\r\ne\r\nf\r\n"));
        vim.Clamp();
        long lineAfterOutput = vim.Cursor.Line;
        string textUnderCursor = t.GetText(lineAfterOutput, 0, lineAfterOutput, 19);
        vim.FeedKeys("yy", host);

        // Assert
        await Assert.That(lineAfterOutput).IsEqualTo(line);
        await Assert.That(textUnderCursor).IsEqualTo("b");
        var yank = await Assert.That(host.Yanks).HasSingleItem();
        await Assert.That(yank).IsEqualTo("b");
    }

    [Test]
    public async Task Marks_LineAndExactJumps_AndBackTick()
    {
        // One walk-through: '' goes back to where the previous jump started, so each step depends on the jumps
        // before it.

        // Arrange
        var t = T(20, 6, "  first\r\nsecond\r\nthird");
        var (vim, host) = Vim(t, 0, 5);
        vim.FeedKeys("mbG", host);

        // Act
        vim.FeedKeys("`b", host);
        var afterBackTick = Rel(t, vim.Cursor);
        vim.FeedKeys("G'b", host);
        var afterQuote = Rel(t, vim.Cursor);
        vim.FeedKeys("G''", host);                     // back to where the last jump (G) started
        var afterJumpBack = Rel(t, vim.Cursor);
        vim.FeedKeys("''", host);                      // '' is a jump too: it toggles
        var afterToggle = Rel(t, vim.Cursor);

        // Assert
        await Assert.That(afterBackTick).IsEqualTo((0, 5));
        await Assert.That(afterQuote).IsEqualTo((0, 2));
        await Assert.That(afterJumpBack).IsEqualTo((0, 2));
        await Assert.That(afterToggle.Item1).IsEqualTo(2);
    }
}

public class VimPromptBlockTests
{
    private const string A = "\e]133;A\e\\", B = "\e]133;B\e\\", C = "\e]133;C\e\\";
    private static string D(int code) => $"\e]133;D;{code}\e\\";

    /// <summary>`ls` (two lines), `cat x` (one line), then a fresh prompt.</summary>
    private static Terminal Session() => VimMotionTests.T(20, 10,
        $"{A}$ {B}ls\r\n{C}one\r\ntwo\r\n{D(0)}{A}$ {B}cat x\r\n{C}hello\r\n{D(0)}{A}$ {B}");

    [Test]
    public async Task BracketBrackets_JumpBetweenPromptsOnTheCommand()
    {
        // One walk-through: every jump starts from the prompt the previous one landed on.

        // Arrange
        var t = Session();
        var (vim, host) = VimMotionTests.Vim(t, 5, 2);
        long top = t.ScreenTopLine;

        // Act
        vim.FeedKeys("[[", host);
        var onPreviousPrompt = vim.Cursor;
        vim.FeedKeys("[[", host);
        var onFirstPrompt = vim.Cursor;
        vim.FeedKeys("2]]", host);
        long twoPromptsDown = vim.Cursor.Line;

        // Assert
        await Assert.That(onPreviousPrompt).IsEqualTo(new VimPos(top + 3, 2));
        await Assert.That(onFirstPrompt).IsEqualTo(new VimPos(top, 2));
        await Assert.That(twoPromptsDown).IsEqualTo(top + 5);
    }

    [Test]
    public async Task Braces_JumpBetweenCommandOutputs()
    {
        // One walk-through: every jump starts from the output the previous one landed on.

        // Arrange
        var t = Session();
        var (vim, host) = VimMotionTests.Vim(t, 0, 0);
        long top = t.ScreenTopLine;

        // Act
        vim.FeedKeys("}", host);
        long firstOutput = vim.Cursor.Line;
        vim.FeedKeys("}", host);
        long secondOutput = vim.Cursor.Line;
        vim.FeedKeys("{", host);
        long backToFirst = vim.Cursor.Line;

        // Assert
        await Assert.That(firstOutput).IsEqualTo(top + 1);
        await Assert.That(secondOutput).IsEqualTo(top + 4);
        await Assert.That(backToFirst).IsEqualTo(top + 1);
    }

    [Test]
    public async Task Braces_WithoutShellIntegrationAreParagraphs()
    {
        // One walk-through: every jump starts from the blank line the previous one landed on.

        // Arrange
        var t = VimMotionTests.T(20, 8, "a\r\nb\r\n\r\nc\r\nd\r\n\r\ne");
        var (vim, host) = VimMotionTests.Vim(t, 0, 0);
        long top = t.ScreenTopLine;

        // Act
        vim.FeedKeys("}", host);
        long firstBlank = vim.Cursor.Line;
        vim.FeedKeys("}", host);
        long secondBlank = vim.Cursor.Line;
        vim.FeedKeys("{", host);
        long backToFirst = vim.Cursor.Line;

        // Assert
        await Assert.That(firstBlank).IsEqualTo(top + 2);
        await Assert.That(secondBlank).IsEqualTo(top + 5);
        await Assert.That(backToFirst).IsEqualTo(top + 2);
    }

    [Test]
    public async Task YankOutput_AndCommandLine()
    {
        // Arrange
        var t = Session();
        var (vim, host) = VimMotionTests.Vim(t, 1, 0);

        // Act
        vim.FeedKeys("yo", host);
        vim.FeedKeys("yc", host);
        vim.FeedKeys("4Gyo", host);

        // Assert
        await Assert.That(host.Yanks).IsEquivalentTo(["one\ntwo", "ls", "hello"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("ggvipy", "one\ntwo")]
    [Arguments("gg2jvapy", "$ ls\none\ntwo")]
    public async Task InnerAndAroundParagraph_AreTheOutputAndTheWholeCommand(string keys, string expected)
    {
        // Arrange
        var (vim, host) = VimMotionTests.Vim(Session(), 1, 0);

        // Act
        vim.FeedKeys(keys, host);

        // Assert
        var yank = await Assert.That(host.Yanks).HasSingleItem();
        await Assert.That(yank).IsEqualTo(expected);
    }

    [Test]
    public async Task NoMarks_ExplainsInsteadOfYanking()
    {
        // Arrange
        var t = VimMotionTests.T(20, 4, "plain");
        var (vim, host) = VimMotionTests.Vim(t, 0, 0);

        // Act
        vim.FeedKeys("yo[[", host);

        // Assert
        await Assert.That(host.Yanks).IsEmpty();
        await Assert.That(host.Messages.Count).IsEqualTo(2);
    }
}

public class VimVisualYankTests
{
    private static (VimEngine, FakeVimHost) Vim(string text, int row = 0, int col = 0, int cols = 30) =>
        VimMotionTests.Vim(VimMotionTests.T(cols, 6, text), row, col);

    [Test]
    [Arguments("vey", "hello")]
    [Arguments("Vjy", "hello world\nsecond line")]
    [Arguments("<C-v>jlly", "hel\nsec")]
    [Arguments("wvjy", "world\nsecond ")]
    public async Task Charwise_Linewise_Block(string keys, string expected)
    {
        // Arrange
        var (vim, host) = Vim("hello world\r\nsecond line\r\nthird");

        // Act
        vim.FeedKeys(keys, host);

        // Assert
        var yank = await Assert.That(host.Yanks).HasSingleItem();
        await Assert.That(yank).IsEqualTo(expected);
        await Assert.That(vim.Mode).IsEqualTo(VimMode.Normal);
    }

    [Test]
    [Arguments("<C-v>jj5ly", "ab\ncdefgh\nij")]
    [Arguments("l<C-v>jlly", "b\ndef")] // columns 1-3
    public async Task Block_TrimsEachRowAndCanReachPastTheText(string keys, string expected)
    {
        // Arrange
        var (vim, host) = Vim("ab\r\ncdefgh\r\nij");

        // Act
        vim.FeedKeys(keys, host);

        // Assert
        var yank = await Assert.That(host.Yanks).HasSingleItem();
        await Assert.That(yank).IsEqualTo(expected);
    }

    [Test]
    public async Task Visual_SwapEnds()
    {
        // Arrange
        var (vim, host) = Vim("abcdef");
        vim.FeedKeys("lvll", host);
        long line = vim.Cursor.Line;
        var selection = vim.Selection;

        // Act
        vim.FeedKeys("o", host);

        // Assert
        await Assert.That(selection).IsEqualTo(new VimRange(new VimPos(line, 1), new VimPos(line, 3)));
        await Assert.That(vim.Cursor.Col).IsEqualTo(1);
        await Assert.That(vim.Anchor.Col).IsEqualTo(3);
    }

    [Test]
    public async Task Visual_SwitchModes()
    {
        // One walk-through: V and <C-v> switch from the visual mode the previous key chose; the same key again ends it.

        // Arrange
        var (vim, host) = Vim("abcdef");
        vim.FeedKeys("lvll", host);

        // Act
        vim.FeedKeys("V", host);
        var afterV = vim.Mode;
        vim.FeedKeys("<C-v>", host);
        var afterCtrlV = vim.Mode;
        vim.FeedKeys("<C-v>", host);
        var afterSecondCtrlV = vim.Mode;

        // Assert
        await Assert.That(afterV).IsEqualTo(VimMode.VisualLine);
        await Assert.That(afterCtrlV).IsEqualTo(VimMode.VisualBlock);
        await Assert.That(afterSecondCtrlV).IsEqualTo(VimMode.Normal);
    }

    [Test]
    public async Task Visual_EscapeEndsVisualModeBeforeLeavingVimMode()
    {
        // One walk-through: the first <Esc> only ends visual mode, so the second one is what leaves vim mode.

        // Arrange
        var (vim, host) = Vim("abcdef");

        // Act
        vim.FeedKeys("v<Esc>", host);
        var modeAfterEscape = vim.Mode;
        bool? leftAfterFirstEscape = host.Left;
        vim.FeedKeys("<Esc>", host);
        bool? leftAfterSecondEscape = host.Left;

        // Assert
        await Assert.That(modeAfterEscape).IsEqualTo(VimMode.Normal);
        await Assert.That(leftAfterFirstEscape).IsNull();
        await Assert.That(leftAfterSecondEscape).IsFalse();
    }

    public static IEnumerable<Func<(string Text, int Col, string[] Keys, string[] Yanks)>> WordObjects() =>
    [
        () => ("foo bar baz", 5, ["viwy", "wvawy"], ["bar", " baz"]), // baz is last: aw takes the blank before it
        // vawy: no blank after, so the one before; lyiW: y left the cursor on the blank
        () => ("foo bar", 5, ["vawy", "lyiW"], [" bar", "bar"]),
        () => ("a.b c", 0, ["yiw", "yiW"], ["a", "a.b"]),
    ];

    [Test]
    [MethodDataSource(nameof(WordObjects))]
    public async Task TextObjects_Words(string text, int col, string[] keys, string[] expected)
    {
        // Arrange
        var (vim, host) = Vim(text, 0, col);

        // Act
        foreach (var k in keys) vim.FeedKeys(k, host);

        // Assert
        await Assert.That(host.Yanks).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("y$", "bar baz")]
    [Arguments("yw", "bar ")]
    [Arguments("ye", "bar")]
    [Arguments("yy", "foo bar baz")]
    [Arguments("2yy", "foo bar baz\nnext line")]
    [Arguments("Y", "foo bar baz")]
    [Arguments("yj", "foo bar baz\nnext line")]
    [Arguments("fbyw", "baz")]                   // the last word of the line: up to its end
    public async Task NormalModeYanks(string keys, string expected)
    {
        // Arrange
        var (vim, host) = Vim("foo bar baz\r\nnext line\r\nlast", 0, 4);

        // Act
        vim.FeedKeys(keys, host);

        // Assert
        var yank = await Assert.That(host.Yanks).HasSingleItem();
        await Assert.That(yank).IsEqualTo(expected);
    }

    [Test]
    public async Task NormalModeYank_WithAMotionThatFailsYanksNothing()
    {
        // Arrange
        var (vim, host) = Vim("foo bar baz\r\nnext line\r\nlast", 0, 4);

        // Act
        vim.FeedKeys("yfq", host);                   // no q: nothing

        // Assert
        await Assert.That(host.Yanks).IsEmpty();
    }

    [Test]
    public async Task YankBackwardMovesTheCursorToTheStart()
    {
        // Arrange
        var (vim, host) = Vim("foo bar", 0, 4);

        // Act
        vim.FeedKeys("yb", host);

        // Assert
        var yank = await Assert.That(host.Yanks).HasSingleItem();
        await Assert.That(yank).IsEqualTo("foo ");
        await Assert.That(vim.Cursor.Col).IsEqualTo(0);
        await Assert.That(vim.LastYank).IsEqualTo("foo ");
    }

    [Test]
    [Arguments("i", true)]
    [Arguments("q", false)]
    [Arguments("vi", null)]                       // in visual, i starts a text object
    public async Task LeavingKeys(string keys, bool? expectedLeft)
    {
        // Arrange
        var (vim, host) = Vim("x");

        // Act
        vim.FeedKeys(keys, host);

        // Assert
        await Assert.That(host.Left).IsEqualTo(expectedLeft);
    }

    [Test]
    public async Task WindowTabAndOpenCommandsGoToTheHost()
    {
        // Arrange
        var (vim, host) = Vim("see https://example.com now");
        long line = vim.Cursor.Line; // none of the keys moves the cursor
        string[] expected = ["window:v:0", "window:>:3", "window:x:0", "window:T:0", "tab:False:2:2", "tab:True:1:0",
            "open:Url:" + line + ",0", "open:File:" + line + ",0", "open:Output:" + line + ",0",
            "open:Scrollback:" + line + ",0", "help", "hints"];

        // Act
        vim.FeedKeys("<C-w>v3<C-w>><C-w>x<C-w>T2gtgTgxgfgogOg?gh", host);
        long lineAfter = vim.Cursor.Line;

        // Assert
        await Assert.That(lineAfter).IsEqualTo(line);
        await Assert.That(host.Calls).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Semicolon_RepeatsAFindElseShowsHints()
    {
        // One walk-through: what ; does depends on the command right before it.

        // Arrange
        var (vim, host) = Vim("a,b,c");

        // Act
        vim.FeedKeys(";", host);
        List<string> callsWithoutAFind = [.. host.Calls];
        vim.FeedKeys("f,;", host);
        int colAfterRepeat = vim.Cursor.Col;
        List<string> callsAfterRepeat = [.. host.Calls];
        vim.FeedKeys("0;", host);                       // another command broke the chain
        int callsAfterBrokenChain = host.Calls.Count;

        // Assert
        await Assert.That(callsWithoutAFind).IsEquivalentTo(["hints"], CollectionOrdering.Matching);
        await Assert.That(colAfterRepeat).IsEqualTo(3);
        await Assert.That(callsAfterRepeat).HasSingleItem();
        await Assert.That(callsAfterBrokenChain).IsEqualTo(2);
    }
}

public class VimSearchTests
{
    private static readonly List<SearchMatch> Matches =
    [
        new(10, 0, 10, 2), new(10, 8, 10, 10), new(12, 4, 12, 6), new(20, 0, 20, 2),
    ];

    [Test]
    [Arguments(10, 0, true, false, 1)]
    [Arguments(10, 0, true, true, 0)]
    [Arguments(11, 0, true, false, 2)]
    [Arguments(20, 0, true, false, 0)]    // wraps to the top
    [Arguments(10, 0, false, false, 3)]   // wraps to the bottom
    [Arguments(12, 4, false, false, 1)]
    [Arguments(12, 4, false, true, 2)]
    public async Task Next_WrapsBothWays(int line, int col, bool forward, bool inclusive, int expected)
    {
        // Act
        int next = VimSearch.Next(Matches, new VimPos(line, col), forward, inclusive);

        // Assert
        await Assert.That(next).IsEqualTo(expected);
    }

    [Test]
    public async Task Next_WithoutMatchesIsMinusOne()
    {
        // Act
        int next = VimSearch.Next([], new VimPos(0, 0), true);

        // Assert
        await Assert.That(next).IsEqualTo(-1);
    }

    [Test]
    [Arguments(0, 20, 0, true)]
    [Arguments(2, 11, 0, false)]
    public async Task Wrapped_TellsWhetherTheMatchIsPastTheEnd(int index, int line, int col, bool expected)
    {
        // Act
        bool wrapped = VimSearch.Wrapped(Matches, index, new VimPos(line, col), true);

        // Assert
        await Assert.That(wrapped).IsEqualTo(expected);
    }

    [Test]
    [Arguments("error", false)]
    [Arguments("Error", true)]
    [Arguments(@"\Sfoo", false)]
    [Arguments(@"foo\", false)] // a trailing backslash escapes nothing
    [Arguments(@"\\A", true)] // an escaped backslash, then a real capital
    public async Task Smartcase(string pattern, bool expected)
    {
        // Act
        bool caseSensitive = VimSearch.CaseSensitive(pattern);

        // Assert
        await Assert.That(caseSensitive).IsEqualTo(expected);
    }

    [Test]
    public async Task Patterns_WordBoundariesBecomeRegex()
    {
        // Act
        string regex = VimSearch.ToRegex(@"\<word\>");

        // Assert
        await Assert.That(regex).IsEqualTo(@"\bword\b");
    }

    [Test]
    [Arguments("foo_bar", @"\bfoo_bar\b")]
    [Arguments("-a.b-", @"-a\.b-")]
    [Arguments("...", @"\.\.\.")]
    public async Task Patterns_WordPatternEscapesAndBoundsWords(string word, string expected)
    {
        // Act
        string pattern = VimSearch.WordPattern(word);

        // Assert
        await Assert.That(pattern).IsEqualTo(expected);
    }

    [Test]
    public async Task SlashNAndStar()
    {
        // One walk-through: n, N, * and # act on the last search and on the cursor the previous step left.

        // Arrange
        var t = VimMotionTests.T(30, 5, "alpha beta\r\nalpha gamma\r\nbeta");
        var (vim, host) = VimMotionTests.Vim(t, 0, 0);
        long top = t.ScreenTopLine;

        // Act
        vim.FeedKeys("/beta<CR>", host);
        var afterSearch = vim.Cursor;
        vim.FeedKeys("n", host);
        var afterN = vim.Cursor;
        vim.FeedKeys("n", host);
        var afterWrappingN = vim.Cursor;
        vim.FeedKeys("N", host);
        var afterShiftN = vim.Cursor;
        vim.FeedKeys("?alpha<CR>", host);
        var afterBackwardSearch = vim.Cursor;
        vim.FeedKeys("n", host);                            // ? searches backward, so n does too
        var afterBackwardN = vim.Cursor;
        vim.FeedKeys("*", host);
        var afterStar = vim.Cursor;
        string starSearch = vim.LastSearch;
        vim.FeedKeys("#", host);
        var afterHash = vim.Cursor;
        vim.FeedKeys("/Beta<CR>", host);                    // smartcase: no match
        var afterNoMatch = vim.Cursor;

        // Assert
        await Assert.That(afterSearch).IsEqualTo(new VimPos(top, 6));
        await Assert.That(afterN).IsEqualTo(new VimPos(top + 2, 0));
        await Assert.That(afterWrappingN).IsEqualTo(new VimPos(top, 6));
        await Assert.That(afterShiftN).IsEqualTo(new VimPos(top + 2, 0));
        await Assert.That(afterBackwardSearch).IsEqualTo(new VimPos(top + 1, 0));
        await Assert.That(afterBackwardN).IsEqualTo(new VimPos(top, 0));
        await Assert.That(afterStar).IsEqualTo(new VimPos(top + 1, 0));
        await Assert.That(starSearch).IsEqualTo(@"\balpha\b");
        await Assert.That(afterHash).IsEqualTo(new VimPos(top, 0));
        await Assert.That(host.Messages).Contains("Pattern not found: Beta");
        await Assert.That(afterNoMatch).IsEqualTo(new VimPos(top, 0));
    }

    [Test]
    public async Task Incremental_PreviewsAndEscapeRestores()
    {
        // One walk-through: each step edits or closes the prompt the previous step left, and the final n shows
        // that the cancelled search was not kept.

        // Arrange
        var t = VimMotionTests.T(30, 5, "alpha beta\r\nalpha gamma\r\nbeta");
        var (vim, host) = VimMotionTests.Vim(t, 0, 0);
        long top = t.ScreenTopLine;

        // Act
        vim.FeedKeys("/gam", host);
        var promptWhileTyping = vim.Prompt;
        string textWhileTyping = vim.PromptText;
        var preview = vim.Cursor;
        vim.FeedKeys("<BS><BS><BS>", host);
        var afterErasing = vim.Cursor;
        vim.FeedKeys("beta<Esc>", host);
        var promptAfterEscape = vim.Prompt;
        var afterEscape = vim.Cursor;
        string? patternAfterEscape = host.Pattern;
        vim.FeedKeys("/<BS>", host);                         // backspace on an empty prompt closes it
        var promptAfterEmptyBackspace = vim.Prompt;
        vim.FeedKeys("n", host);

        // Assert
        await Assert.That(promptWhileTyping).IsEqualTo(VimPromptKind.SearchForward);
        await Assert.That(textWhileTyping).IsEqualTo("gam");
        await Assert.That(preview).IsEqualTo(new VimPos(top + 1, 6));
        await Assert.That(afterErasing).IsEqualTo(new VimPos(top, 0));
        await Assert.That(promptAfterEscape).IsEqualTo(VimPromptKind.None);
        await Assert.That(afterEscape).IsEqualTo(new VimPos(top, 0));
        await Assert.That(patternAfterEscape).IsNull();                           // no earlier search: highlights go
        await Assert.That(promptAfterEmptyBackspace).IsEqualTo(VimPromptKind.None);
        await Assert.That(host.Messages).Contains("No previous regular expression");
    }
}

public class ExCommandTests
{
    private static readonly string[] Actions = ["SplitHorizontal", "ActivatePaneDirection", "AdjustPaneSize"];

    private static ExCommand? P(string s) => Ex.Parse(s, Actions, out _);
    private static string? Err(string s) { Ex.Parse(s, Actions, out var e); return e; }

    private static IEnumerable<string> ThemeArguments(string cmd) =>
        cmd == "theme" ? ["Tokyo Night", "Gruvbox Dark", "Catppuccin Mocha"] : [];

    [Test]
    [Arguments("sp", "split")]
    [Arguments(":split", "split")]
    [Arguments("vs", "vsplit")]
    [Arguments("q", "quit")]
    [Arguments("tabnew", "tabnew")]
    [Arguments("tabn", "tabnext")]
    [Arguments("tabp", "tabprevious")]
    [Arguments("tabN", "tabprevious")]
    [Arguments("on", "only")]
    [Arguments("noh", "nohlsearch")]
    [Arguments("h", "help")]
    [Arguments("colo gruvbox", "theme")]
    public async Task AliasesBecomeCanonicalNames(string input, string expected)
    {
        // Act
        string? name = P(input)?.Name;

        // Assert
        await Assert.That(name).IsEqualTo(expected);
    }

    public static IEnumerable<Func<(string Input, ExCommand Expected)>> ParsedCommands() =>
    [
        () => ("42", new ExCommand("goto", Number: 42)),
        () => ("$", new ExCommand("goto", Number: int.MaxValue)),
        () => ("tabn 3", new ExCommand("tabnext", "3", false, 3)),
        () => ("w! /tmp/out.txt", new ExCommand("write", "/tmp/out.txt", true)),
        () => ("w ~/log", new ExCommand("write", "~/log", false)),
        () => ("set font_size=13", new ExCommand("set", "font_size=13")),
        () => ("se font_size 13", new ExCommand("set", "font_size=13")),
        () => ("theme Tokyo Night", new ExCommand("theme", "Tokyo Night")),
        () => ("send claude", new ExCommand("send", "claude")),
        () => ("layout tiled", new ExCommand("layout", "tiled")),
        () => ("only!", new ExCommand("only", "", true)),
        () => ("sp htop", new ExCommand("split", "htop")),
    ];

    [Test]
    [MethodDataSource(nameof(ParsedCommands))]
    public async Task ArgumentsBangsAndNumbers(string input, ExCommand expected)
    {
        // Act
        var command = P(input);

        // Assert
        await Assert.That(command).IsEqualTo(expected);
    }

    [Test]
    [Arguments("SplitHorizontal", "SplitHorizontal")]
    [Arguments("activatepanedirection Left", "ActivatePaneDirection(Left)")]
    [Arguments("AdjustPaneSize(Left, 5)", "AdjustPaneSize(Left, 5)")]
    public async Task ActionsWorkAsCommands(string input, string expectedAction)
    {
        // Arrange
        var expected = new ExCommand("action", expectedAction);

        // Act
        var command = P(input);

        // Assert
        await Assert.That(command).IsEqualTo(expected);
    }

    [Test]
    [Arguments("w", "No file name")]
    [Arguments("theme", "Argument required")]
    public async Task Errors_MissingArgument(string input, string expected)
    {
        // Act
        string? error = Err(input);

        // Assert
        await Assert.That(error).IsEqualTo(expected);
    }

    [Test]
    [Arguments("bogus", "Not an editor command")]
    [Arguments("#$", "Not an editor command")]
    [Arguments("noh now", "Trailing characters")]
    [Arguments("tabn x", "Invalid argument")]
    [Arguments("set font_size", "Expected KEY=VALUE")]
    public async Task Errors_StartWithTheReason(string input, string expectedStart)
    {
        // Act
        string? error = Err(input);

        // Assert
        await Assert.That(error).StartsWith(expectedStart);
    }

    [Test]
    public async Task Errors_EmptyInputIsNeitherACommandNorAnError()
    {
        // Act
        var command = P("");
        string? error = Err("");

        // Assert
        await Assert.That(command).IsNull();
        await Assert.That(error).IsNull();
    }

    [Test]
    [Arguments("vs", "vsplit")]
    [Arguments("sp", "split")]
    [Arguments("SplitH", "SplitHorizontal")]
    public async Task Completion_CommandsAndActions(string input, string expected)
    {
        // Act
        string first = Ex.Complete(input, Actions, null)[0];

        // Assert
        await Assert.That(first).IsEqualTo(expected);
    }

    [Test]
    public async Task Completion_EmptyInputOffersTheCommands()
    {
        // Act
        var suggestions = Ex.Complete("", Actions, null);

        // Assert
        await Assert.That(suggestions).Contains("help");
    }

    [Test]
    [Arguments("theme gru", "theme Gruvbox Dark")]
    [Arguments("colo mocha", "colo Catppuccin Mocha")]
    public async Task Completion_Arguments(string input, string expected)
    {
        // Act
        string first = Ex.Complete(input, Actions, ThemeArguments)[0];

        // Assert
        await Assert.That(first).IsEqualTo(expected);
    }

    [Test]
    public async Task Completion_EveryArgumentAfterTheCommand()
    {
        // Act
        var suggestions = Ex.Complete("theme ", Actions, ThemeArguments);

        // Assert
        await Assert.That(suggestions.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Completion_NoArgumentMatches()
    {
        // Act
        var suggestions = Ex.Complete("theme zzz", Actions, ThemeArguments);

        // Assert
        await Assert.That(suggestions).IsEmpty();
    }

    [Test]
    public async Task Prompt_TabCyclesErrorsStayInline()
    {
        // One walk-through: the prompt stays open across the edits (<C-u> clears what the step before typed), and
        // <Up> recalls the history the earlier steps built.

        // Arrange
        var t = VimMotionTests.T(20, 5, "x");
        var (vim, host) = VimMotionTests.Vim(t, 0, 0);

        // Act
        vim.FeedKeys(":theme gr", host);
        var promptWhileTyping = vim.Prompt;
        string firstSuggestion = vim.Suggestions[0];
        List<string> callsWhileTyping = [.. host.Calls];
        vim.FeedKeys("<Tab>", host);
        string afterTab = vim.PromptText;
        vim.FeedKeys("<C-u>the<Tab>", host);
        string afterCommandTab = vim.PromptText;
        vim.FeedKeys("<C-u>bogus<CR>", host);
        var promptAfterBadCommand = vim.Prompt;
        string? badCommandError = vim.PromptError;
        vim.FeedKeys("<C-u>sp<CR>", host);
        var promptAfterGoodCommand = vim.Prompt;
        List<ExCommand> executed = [.. host.Executed];
        host.ExecuteError = "File exists (add ! to override)";
        vim.FeedKeys(":w /tmp/x<CR>", host);
        string? hostError = vim.PromptError;
        vim.FeedKeys("<Esc>", host);
        List<string> callsAfterEscape = [.. host.Calls];
        vim.FeedKeys(":noh<CR>:help<CR>", host);
        List<string> callsAfterNohAndHelp = [.. host.Calls];
        vim.FeedKeys(":<Up>", host);                           // history
        string recalled = vim.PromptText;

        // Assert
        await Assert.That(promptWhileTyping).IsEqualTo(VimPromptKind.Ex);
        await Assert.That(firstSuggestion).IsEqualTo("theme Gruvbox Dark");
        await Assert.That(callsWhileTyping).Contains("prompt:theme gr");
        await Assert.That(afterTab).IsEqualTo("theme Gruvbox Dark");
        await Assert.That(afterCommandTab).IsEqualTo("theme");
        await Assert.That(promptAfterBadCommand).IsEqualTo(VimPromptKind.Ex);           // still open, with the error
        await Assert.That(badCommandError).StartsWith("Not an editor command");
        await Assert.That(promptAfterGoodCommand).IsEqualTo(VimPromptKind.None);
        var split = await Assert.That(executed).HasSingleItem();
        await Assert.That(split.Name).IsEqualTo("split");
        await Assert.That(hostError).IsEqualTo("File exists (add ! to override)");
        await Assert.That(callsAfterEscape).Contains("prompt-cancel");
        await Assert.That(callsAfterNohAndHelp).Contains("noh");
        await Assert.That(callsAfterNohAndHelp).Contains("help");
        await Assert.That(recalled).IsEqualTo("help");
    }

    [Test]
    public async Task Prompt_CapturesTheVisualSelectionForSend()
    {
        // Arrange
        var t = VimMotionTests.T(20, 5, "hello world");
        var (vim, host) = VimMotionTests.Vim(t, 0, 0);

        // Act
        vim.FeedKeys("ve:", host);
        string? selection = vim.PromptSelection;
        var mode = vim.Mode;
        vim.FeedKeys("send 3<CR>", host);

        // Assert
        await Assert.That(selection).IsEqualTo("hello");
        await Assert.That(mode).IsEqualTo(VimMode.Normal);
        var executed = await Assert.That(host.Executed).HasSingleItem();
        await Assert.That(executed).IsEqualTo(new ExCommand("send", "3"));
    }
}

public class VimSelectionAndLayoutTests
{
    [Test]
    public async Task BlockSelection_RangeAndText()
    {
        // Arrange
        var t = VimMotionTests.T(10, 4, "abcdef\r\nghijkl\r\nmn");
        long top = t.ScreenTopLine;
        var sel = new Selection { AnchorLine = top + 2, AnchorCol = 3, HeadLine = top, HeadCol = 1, Mode = SelectionMode.Block };

        // Act
        var range = sel.Range(t);
        var text = sel.Text(t);

        // Assert
        await Assert.That(range).IsEqualTo((top, 1, top + 2, 3));
        await Assert.That(text).IsEqualTo("bcd\nhij\nn");
    }

    private sealed record P(string Name);

    private static string Order(LayoutTree<P> tree) => string.Join("", tree.LeafList.Select(p => p.Name));

    private static LayoutTree<P> Three(out P a, out P b, out P c)
    {
        a = new P("a"); b = new P("b"); c = new P("c");
        var tree = new LayoutTree<P>(a);
        tree.Split(a, b, SplitAxis.Horizontal);
        tree.Split(b, c, SplitAxis.Vertical);
        return tree;
    }

    [Test]
    public async Task Swap_ExchangesTwoPanes()
    {
        // Arrange
        var tree = Three(out var a, out _, out var c);

        // Act
        bool swapped = tree.Swap(a, c);
        string order = Order(tree);

        // Assert
        await Assert.That(swapped).IsTrue();
        await Assert.That(order).IsEqualTo("cba");
    }

    [Test]
    public async Task Swap_WithItselfIsRefused()
    {
        // Arrange
        var tree = Three(out var a, out _, out _);

        // Act
        bool swapped = tree.Swap(a, a);

        // Assert
        await Assert.That(swapped).IsFalse();
    }

    [Test]
    public async Task SwapWithNext_WrapsAndGoesBothWays()
    {
        // One walk-through: every swap moves a on from where the previous one put it.

        // Arrange
        var tree = Three(out var a, out var b, out var c);
        tree.Swap(a, c); // c b a

        // Act
        var wrappedPartner = tree.SwapWithNext(a);
        string afterWrap = Order(tree);
        var nextPartner = tree.SwapWithNext(a);
        string afterNext = Order(tree);
        var previousPartner = tree.SwapWithNext(a, -1);
        string afterPrevious = Order(tree);

        // Assert
        await Assert.That(wrappedPartner).IsEqualTo(c); // a is last: wraps to the first
        await Assert.That(afterWrap).IsEqualTo("abc");
        await Assert.That(nextPartner).IsEqualTo(b);
        await Assert.That(afterNext).IsEqualTo("bac");
        await Assert.That(previousPartner).IsEqualTo(b);
        await Assert.That(afterPrevious).IsEqualTo("abc");
    }

    [Test]
    public async Task SwapWithNext_ALonePaneHasNoNeighbour()
    {
        // Arrange
        var a = new P("a");
        var tree = new LayoutTree<P>(a);

        // Act
        var partner = tree.SwapWithNext(a);

        // Assert
        await Assert.That(partner).IsNull();
    }

    [Test]
    public async Task Rotate_KeepsTheShape()
    {
        // Arrange
        var tree = Three(out _, out _, out _);
        var bounds = new RectI(0, 0, 1000, 600);
        var rects = new List<(P, RectI)>();
        tree.Layout(bounds, 10, rects);
        var shape = rects.Select(r => r.Item2).ToList();

        // Act
        bool rotated = tree.Rotate(clockwise: true);
        string order = Order(tree);
        tree.Layout(bounds, 10, rects);
        var rotatedShape = rects.Select(r => r.Item2).ToList();

        // Assert
        await Assert.That(rotated).IsTrue();
        await Assert.That(order).IsEqualTo("cab");
        await Assert.That(rotatedShape).IsEquivalentTo(shape, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Rotate_CounterClockwise()
    {
        // Arrange
        var tree = Three(out _, out _, out _);
        tree.Rotate(clockwise: true); // c a b

        // Act
        tree.Rotate(clockwise: false);
        tree.Rotate(clockwise: false);
        string order = Order(tree);

        // Assert
        await Assert.That(order).IsEqualTo("bca");
    }

    [Test]
    public async Task Rotate_ALonePaneStays()
    {
        // Arrange
        var tree = new LayoutTree<P>(new P("x"));

        // Act
        bool rotated = tree.Rotate(true);

        // Assert
        await Assert.That(rotated).IsFalse();
    }

    [Test]
    public async Task Equalize_ResetsTheRatios()
    {
        // Arrange
        var tree = Three(out _, out var b, out _);
        tree.Root.Ratio = 0.8;
        tree.Find(b)!.Parent!.Ratio = 0.1;

        // Act
        tree.Equalize();
        double rootRatio = tree.Root.Ratio;
        double innerRatio = tree.Find(b)!.Parent!.Ratio;

        // Assert
        await Assert.That(rootRatio).IsEqualTo(0.5).Within(0.0005);
        await Assert.That(innerRatio).IsEqualTo(0.5).Within(0.0005);
    }

    [Test]
    public async Task Equalize_SharesBySidesPanes()
    {
        // Arrange
        var tree = Three(out var a, out _, out _);
        tree.Split(a, new P("d"), SplitAxis.Horizontal); // a | d | (b / c): three columns
        var rects = new List<(P Pane, RectI Rect)>();

        // Act
        tree.Equalize();
        tree.Layout(new RectI(0, 0, 900, 600), 0, rects);
        var widths = rects.Where(r => r.Pane.Name is "a" or "d").Select(r => r.Rect.W).ToList();

        // Assert
        foreach (int width in widths)
            await Assert.That(width).IsEqualTo(300);
    }

    [Test]
    public async Task MoveToEdge_Down_SpansTheWidth()
    {
        // Arrange
        var tree = Three(out var a, out var b, out _);
        var rects = new List<(P Pane, RectI Rect)>();

        // Act
        bool moved = tree.MoveToEdge(a, Direction.Down);
        tree.Layout(new RectI(0, 0, 1000, 600), 0, rects);
        var ra = rects.Single(r => r.Pane == a).Rect;
        var rb = rects.Single(r => r.Pane == b).Rect;
        int count = tree.Count;

        // Assert
        await Assert.That(moved).IsTrue();
        await Assert.That(ra.W).IsEqualTo(1000);                    // full width at the bottom
        await Assert.That(ra.Bottom).IsEqualTo(600);
        await Assert.That(rb.Bottom).IsLessThanOrEqualTo(ra.Y);
        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task MoveToEdge_Left_SpansTheHeight()
    {
        // Arrange
        var tree = Three(out var a, out _, out var c);
        tree.MoveToEdge(a, Direction.Down); // b and c above, a across the bottom
        var rects = new List<(P Pane, RectI Rect)>();

        // Act
        bool moved = tree.MoveToEdge(c, Direction.Left);
        tree.Layout(new RectI(0, 0, 1000, 600), 0, rects);
        var rc = rects.Single(r => r.Pane == c).Rect;
        string order = Order(tree);
        var greatGrandparent = tree.Find(a)!.Parent!.Parent!.Parent;

        // Assert
        await Assert.That(moved).IsTrue();
        await Assert.That((rc.X, rc.Y, rc.H)).IsEqualTo((0, 0, 600));
        await Assert.That(order).IsEqualTo("cba");
        await Assert.That(greatGrandparent).IsNull();
    }

    [Test]
    public async Task MoveToEdge_ALonePaneStays()
    {
        // Arrange
        var a = new P("a");
        var tree = new LayoutTree<P>(a);

        // Act
        bool moved = tree.MoveToEdge(a, Direction.Left);

        // Assert
        await Assert.That(moved).IsFalse();
    }
}

public class VimActionsAndCliTests
{
    private static string[] Argv(string line) => line.Split(' ');

    [Test]
    public async Task DefaultKeyEntersVimMode()
    {
        // Act
        var binding = DefaultKeys.Build().Single(b => b.Action.Name == "ActivateCopyMode");

        // Assert
        await Assert.That(binding.Chord).IsEqualTo(new KeyChord(KeyMods.Ctrl | KeyMods.Shift, "x"));
    }

    [Test]
    [Arguments("VimMode")]
    [Arguments("SwapPane")]
    [Arguments("SwapPane(Previous)")]
    [Arguments("RotatePanes(Clockwise)")]
    [Arguments("RotatePanes(CounterClockwise)")]
    [Arguments("EqualizePanes")]
    [Arguments("MovePaneToNewTab")]
    [Arguments("MovePaneToEdge(Left)")]
    [Arguments("PaneLayout(tiled)")]
    [Arguments("PaneLayout(main-vertical)")]
    public async Task TheActionsParse(string action)
    {
        // Act
        bool parsed = ActionSpec.TryParse(action, out _, out var error);

        // Assert
        await Assert.That(parsed).IsTrue().Because(error);
    }

    [Test]
    [Arguments("PaneLayout(bogus)")]
    [Arguments("RotatePanes(Sideways)")]
    public async Task TheActionsRejectUnknownArguments(string action)
    {
        // Act
        bool parsed = ActionSpec.TryParse(action, out _, out _);

        // Assert
        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task Help_EverySectionHasEntries()
    {
        // Act
        var sections = VimHelp.Sections;

        // Assert
        foreach (var s in sections)
            await Assert.That(s.Entries).IsNotEmpty();
    }

    [Test]
    [Arguments("gg")]
    [Arguments("Ctrl-v")]
    [Arguments("yo")]
    [Arguments("gx")]
    [Arguments("gf")]
    [Arguments("go")]
    [Arguments("Ctrl-w")]
    [Arguments(":theme")]
    [Arguments("m{a-z}")]
    [Arguments("[[")]
    [Arguments("{ }")]
    [Arguments("* #")]
    public async Task HelpCoversTheKeymap(string key)
    {
        // Act
        var keys = string.Join(" ", VimHelp.Sections.SelectMany(s => s.Entries).Select(e => e.Keys));

        // Assert
        await Assert.That(keys).Contains(key);
    }

    [Test]
    public async Task Cli_SwapPaneWithAnother()
    {
        // Act
        var swap = CliParser.Parse(["swap-pane", "--with", "3"]).Request;

        // Assert
        await Assert.That((swap.Command, swap.OtherPaneId, swap.Direction)).IsEqualTo(("swap-pane", 3, "Next"));
    }

    [Test]
    [Arguments("swap-pane --previous --pane-id 2", "Previous")]
    [Arguments("rotate-panes", "Clockwise")]
    [Arguments("rotate-panes --counter-clockwise", "CounterClockwise")]
    public async Task Cli_SwapAndRotateDirections(string line, string expected)
    {
        // Arrange
        var argv = Argv(line);

        // Act
        var direction = CliParser.Parse(argv).Request.Direction;

        // Assert
        await Assert.That(direction).IsEqualTo(expected);
    }

    [Test]
    public async Task Cli_SelectLayoutForATab()
    {
        // Act
        var layout = CliParser.Parse(["select-layout", "Tiled", "--tab-id", "4"]).Request;

        // Assert
        await Assert.That((layout.Command, layout.Layout, layout.TabId)).IsEqualTo(("select-layout", "tiled", 4));
    }

    [Test]
    public async Task Cli_SelectLayoutEven()
    {
        // Act
        var layout = CliParser.Parse(["select-layout", "even"]).Request.Layout;

        // Assert
        await Assert.That(layout).IsEqualTo("even");
    }

    [Test]
    [Arguments("swap-pane --with 3 --previous")]
    [Arguments("select-layout spiral")]
    [Arguments("select-layout")]
    public async Task Cli_RejectsBadArrangements(string line)
    {
        // Arrange
        var argv = Argv(line);

        // Act
        var act = () => CliParser.Parse(argv);

        // Assert
        await Assert.That(act).ThrowsExactly<CliUsageException>();
    }

    [Test]
    public async Task Cli_MovePaneToNewTab()
    {
        // Act
        var command = CliParser.Parse(["move-pane-to-new-tab", "--pane-id", "7"]).Request.Command;

        // Assert
        await Assert.That(command).IsEqualTo("move-pane-to-new-tab");
    }

    [Test]
    public async Task Cli_UsageListsSwapPane()
    {
        // Act
        string usage = CliParser.Usage;

        // Assert
        await Assert.That(usage).Contains("swap-pane");
    }
}

public class VimBlockMouseAndResizeTests
{
    private sealed record P(string Name);

    [Test]
    public async Task Block_CursorStopsAtTheLongestLineItSpans()
    {
        // Arrange
        var t = VimMotionTests.T(30, 5, "ab\r\ncdefgh\r\nij");
        var (vim, host) = VimMotionTests.Vim(t, 0, 0);

        // Act
        vim.FeedKeys("<C-v>20l", host);
        int alone = vim.Cursor.Col;
        vim.FeedKeys("j20l", host);
        int spanning = vim.Cursor.Col;
        vim.FeedKeys("j", host);
        int pastShortLine = vim.Cursor.Col;

        // Assert
        await Assert.That(alone).IsEqualTo(1);          // only "ab" in the block
        await Assert.That(spanning).IsEqualTo(5);       // "cdefgh" is the longest line spanned
        await Assert.That(pastShortLine).IsEqualTo(5);  // "ij" is shorter: virtualedit=block keeps the column
    }

    [Test]
    public async Task LeavingBlockMode_PutsTheCursorBackOnTheText()
    {
        // Arrange
        var t = VimMotionTests.T(30, 5, "ab\r\ncdefgh\r\nij");
        var (vim, host) = VimMotionTests.Vim(t, 1, 0);
        vim.FeedKeys("<C-v>$j", host);

        // Act
        vim.FeedKeys("<Esc>", host);

        // Assert
        await Assert.That(vim.Mode).IsEqualTo(VimMode.Normal);
        await Assert.That((vim.Cursor.Line, vim.Cursor.Col)).IsEqualTo((t.ScreenTopLine + 2, 1));
    }

    [Test]
    public async Task Click_LeavesVisualModeAndClampsToTheText()
    {
        // Arrange
        var t = VimMotionTests.T(30, 5, "hello\r\nworld wide");
        var (vim, host) = VimMotionTests.Vim(t, 1, 0);
        vim.FeedKeys("vl", host);

        // Act
        vim.Click(new VimPos(t.ScreenTopLine, 20));

        // Assert
        await Assert.That(vim.Mode).IsEqualTo(VimMode.Normal);
        await Assert.That(vim.Cursor).IsEqualTo(new VimPos(t.ScreenTopLine, 4));
    }

    [Test]
    public async Task Pending_IsCachedUntilTheNextKey()
    {
        // Arrange
        var parser = new VimKeyParser();
        parser.Feed("2", false, out _);

        // Act
        string first = parser.Pending, again = parser.Pending;
        parser.Feed("y", false, out _);
        string withOperator = parser.Pending;
        parser.Feed("y", false, out _);

        // Assert
        await Assert.That(again).IsSameReferenceAs(first);
        await Assert.That(first).IsEqualTo("2");
        await Assert.That(withOperator).IsEqualTo("2y");
        await Assert.That(parser.Pending).IsEqualTo("");
    }

    [Test]
    public async Task Grow_WidensThePaneWhicheverSideItsDividerIs()
    {
        // Arrange
        P a = new("a"), b = new("b");
        var tree = new LayoutTree<P>(a);
        tree.Split(a, b, SplitAxis.Horizontal);
        var bounds = new RectI(0, 0, 1000, 600);
        int Width(P p)
        {
            var rects = new List<(P Pane, RectI Rect)>();
            tree.Layout(bounds, 0, rects);
            return rects.Single(r => r.Pane == p).Rect.W;
        }

        // Act
        bool grewRight = tree.Grow(b, SplitAxis.Horizontal, 100, bounds, 0, 40);
        int bWider = Width(b);
        tree.Grow(a, SplitAxis.Horizontal, 50, bounds, 0, 40);
        int aWider = Width(a);
        tree.Grow(a, SplitAxis.Horizontal, -20, bounds, 0, 40);
        int aNarrower = Width(a);
        bool noVerticalSplit = tree.Grow(a, SplitAxis.Vertical, 10, bounds, 0, 40);

        // Assert
        await Assert.That(grewRight).IsTrue();
        await Assert.That(bWider).IsEqualTo(600);
        await Assert.That(aWider).IsEqualTo(450);
        await Assert.That(aNarrower).IsEqualTo(430);
        await Assert.That(noVerticalSplit).IsFalse();
    }
}
