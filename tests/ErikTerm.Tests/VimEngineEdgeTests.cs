using ErikTerm.Core;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.Tests;

/// <summary>Vim mode's other outcomes: marks, visual o / Y / Ctrl+C, the prompts' editing keys, history and empty input.</summary>
public class VimEngineEdgeTests
{
    private static (VimEngine Vim, FakeVimHost Host, Terminal T) Vim(string text = "alpha beta\r\ngamma delta\r\nepsilon", int row = 0, int col = 0)
    {
        var t = VimMotionTests.T(20, 5, text);
        var (vim, host) = VimMotionTests.Vim(t, row, col);
        return (vim, host, t);
    }

    [Test]
    public async Task MarksAreCopiedUpToTheSpace()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys("majmBjmc", host);
        var room = new long[2];

        // Act
        int copied = vim.CopyMarkLines(room);

        // Assert
        await Assert.That(copied).IsEqualTo(2);
        await Assert.That(vim.Marks.Count).IsEqualTo(3);
    }

    [Test]
    public async Task MarkLinesAreTheMarksLines()
    {
        // Arrange
        var (vim, host, t) = Vim();
        vim.FeedKeys("jmA", host);
        var room = new long[4];

        // Act
        int copied = vim.CopyMarkLines(room);

        // Assert
        await Assert.That(copied).IsEqualTo(1);
        await Assert.That(room[0]).IsEqualTo(t.ScreenTopLine + 1);
    }

    [Test]
    public async Task AMarkThatIsNoLetterIsRefused()
    {
        // Arrange
        var (vim, host, _) = Vim();

        // Act
        vim.FeedKeys("m1", host);

        // Assert
        await Assert.That(host.Messages).Contains("Invalid mark name");
        await Assert.That(vim.Marks).IsEmpty();
    }

    [Test]
    public async Task VisualOSwapsTheEnds()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys("vll", host);
        var (anchor, cursor) = (vim.Anchor, vim.Cursor);

        // Act
        vim.FeedKeys("o", host);

        // Assert
        await Assert.That((vim.Anchor, vim.Cursor)).IsEqualTo((cursor, anchor));
    }

    [Test]
    public async Task VisualCapitalYYanksWholeLines()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys("vl", host);

        // Act
        vim.FeedKeys("Y", host);

        // Assert: the whole line, not the two selected characters
        await Assert.That(host.Yanks.Single()).IsEqualTo("alpha beta");
    }

    [Test]
    public async Task VisualCtrlCYanksTheSelection()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys("vll", host);

        // Act
        vim.FeedKeys("<C-c>", host);

        // Assert
        await Assert.That(host.Yanks.Single()).IsEqualTo("alp");
    }

    [Test]
    public async Task YankCommandOutputWithoutShellIntegrationSaysSo()
    {
        // Arrange
        var (vim, host, _) = Vim();

        // Act
        vim.FeedKeys("yo", host);

        // Assert
        await Assert.That(host.Messages).Contains("No prompts marked (shell integration)");
        await Assert.That(host.Yanks).IsEmpty();
    }

    [Test]
    public async Task StarOnBlanksHasNoWord()
    {
        // Arrange: the cursor on the spaces between two words
        var (vim, host, _) = Vim("a     b", 0, 3);

        // Act
        vim.FeedKeys("*", host);

        // Assert
        await Assert.That(host.Messages).Contains("No string under cursor");
    }

    [Test]
    public async Task GtWithACountGoesToThatTab()
    {
        // Arrange
        var (vim, host, _) = Vim();

        // Act
        vim.FeedKeys("3gt", host);

        // Assert
        await Assert.That(host.Calls).Contains("tab:False:3:3");
    }

    [Test]
    public async Task PromptBackspaceOnNothingClosesIt()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys(":", host);

        // Act
        vim.FeedKeys("<BS>", host);

        // Assert
        await Assert.That(vim.Prompt).IsEqualTo(VimPromptKind.None);
        await Assert.That(host.Calls).Contains("prompt-cancel");
    }

    [Test]
    public async Task PromptBackspaceRemovesASurrogatePairAsOne()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys("/a\U0001F600", host);

        // Act
        vim.FeedKeys("<BS>", host);

        // Assert
        await Assert.That(vim.PromptText).IsEqualTo("a");
    }

    [Test]
    [Arguments("/foo bar<C-w>", "foo ")]
    [Arguments("/foo<C-w>", "")]          // no separator: all of it
    [Arguments("/a.b-c<C-w>", "a.b-")]
    [Arguments("/foo<C-u>", "")]
    [Arguments("/a<Del>", "a")]           // a named key is not typed
    [Arguments("/<lt>", "<")]             // a literal less-than is
    public async Task PromptEditingKeys(string keys, string text)
    {
        // Arrange
        var (vim, host, _) = Vim();

        // Act
        vim.FeedKeys(keys, host);

        // Assert
        await Assert.That(vim.PromptText).IsEqualTo(text);
    }

    [Test]
    public async Task PasteIntoThePromptFlattensLines()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys(":", host);

        // Act
        vim.PromptInsert("theme\r\nGruvbox", host);

        // Assert
        await Assert.That(vim.PromptText).IsEqualTo("theme Gruvbox");
    }

    [Test]
    public async Task PasteWithoutAPromptIsDropped()
    {
        // Arrange
        var (vim, host, _) = Vim();

        // Act
        vim.PromptInsert("text", host);

        // Assert
        await Assert.That(vim.PromptText).IsEqualTo("");
        await Assert.That(host.Calls).IsEmpty();
    }

    [Test]
    public async Task HistoryWithoutEntriesChangesNothing()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys("/ab", host);

        // Act
        vim.FeedKeys("<Up>", host);

        // Assert
        await Assert.That(vim.PromptText).IsEqualTo("ab");
    }

    [Test]
    public async Task SearchHistoryGoesBackAndForward()
    {
        // Arrange: two searches remembered
        var (vim, host, _) = Vim();
        vim.FeedKeys("/beta<CR>/delta<CR>/", host);

        // Act
        vim.FeedKeys("<Up><Up>", host);
        string oldest = vim.PromptText;
        vim.FeedKeys("<Down><Down>", host);

        // Assert: past the newest is an empty line again
        await Assert.That(oldest).IsEqualTo("beta");
        await Assert.That(vim.PromptText).IsEqualTo("");
    }

    [Test]
    public async Task ShiftTabCyclesCompletionsBackwards()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys(":theme ", host);

        // Act
        vim.FeedKeys("<S-Tab>", host);
        string last = vim.PromptText;
        vim.FeedKeys("<C-p>", host);

        // Assert
        await Assert.That(last).IsEqualTo("theme Catppuccin Mocha");
        await Assert.That(vim.PromptText).IsEqualTo("theme Gruvbox Dark");
    }

    [Test]
    public async Task TabWithNothingToCompleteChangesNothing()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys(":zzz", host);

        // Act
        vim.FeedKeys("<Tab>", host);

        // Assert
        await Assert.That(vim.PromptText).IsEqualTo("zzz");
    }

    [Test]
    public async Task AnEmptyExCommandJustClosesThePrompt()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys(":  ", host);

        // Act
        vim.FeedKeys("<CR>", host);

        // Assert
        await Assert.That(vim.Prompt).IsEqualTo(VimPromptKind.None);
        await Assert.That(host.Executed).IsEmpty();
    }

    [Test]
    public async Task AnEmptySearchWithoutAPreviousOneSaysSo()
    {
        // Arrange
        var (vim, host, _) = Vim();
        vim.FeedKeys("/", host);

        // Act
        vim.FeedKeys("<CR>", host);

        // Assert
        await Assert.That(host.Messages).Contains("No previous regular expression");
    }

    [Test]
    public async Task AnEmptySearchRepeatsThePreviousOne()
    {
        // Arrange
        var (vim, host, t) = Vim();
        vim.FeedKeys("/delta<CR>gg", host);

        // Act
        vim.FeedKeys("/<CR>", host);

        // Assert
        await Assert.That((vim.Cursor.Line - t.ScreenTopLine, vim.Cursor.Col)).IsEqualTo((1L, 6));
    }

    [Test]
    public async Task ASearchThatFindsNothingSaysSo()
    {
        // Arrange
        var (vim, host, _) = Vim();

        // Act
        vim.FeedKeys("/nowhere<CR>", host);

        // Assert
        await Assert.That(host.Messages).Contains("Pattern not found: nowhere");
    }

    [Test]
    public async Task GoToLineZeroIsTheFirstLine()
    {
        // Arrange
        var (vim, host, t) = Vim(row: 2);

        // Act
        vim.FeedKeys(":0<CR>", host);

        // Assert
        await Assert.That(vim.Cursor.Line).IsEqualTo(t.FirstLine);
    }

    [Test]
    public async Task ExHistoryKeepsTheLastHundred()
    {
        // Arrange: 101 different commands, so the first is forgotten
        var (vim, host, _) = Vim();
        for (int i = 1; i <= 101; i++) vim.FeedKeys($":{i}<CR>", host);
        vim.FeedKeys(":", host);

        // Act: 101 steps back stop at the oldest kept
        for (int i = 0; i < 101; i++) vim.FeedKeys("<Up>", host);

        // Assert
        await Assert.That(vim.PromptText).IsEqualTo("2");
    }
}
