using ErikTerm.Core;
using ErikTerm.Ui.VimMode;

namespace ErikTerm.Tests;

/// <summary>Vim notation: every key name, Ctrl forms and what isn't a name.</summary>
public class VimNotationTests
{
    [Test]
    [Arguments("<Esc>", VimKeys.Esc)]
    [Arguments("<ESC>", VimKeys.Esc)]
    [Arguments("<C-[>", VimKeys.Esc)]
    [Arguments("<c-[>", VimKeys.Esc)]
    [Arguments("<CR>", VimKeys.Enter)]
    [Arguments("<Enter>", VimKeys.Enter)]
    [Arguments("<Return>", VimKeys.Enter)]
    [Arguments("<BS>", VimKeys.Backspace)]
    [Arguments("<Tab>", VimKeys.Tab)]
    [Arguments("<S-Tab>", VimKeys.ShiftTab)]
    [Arguments("<Up>", VimKeys.Up)]
    [Arguments("<down>", VimKeys.Down)]
    [Arguments("<Left>", VimKeys.Left)]
    [Arguments("<Right>", VimKeys.Right)]
    [Arguments("<Home>", VimKeys.Home)]
    [Arguments("<End>", VimKeys.End)]
    [Arguments("<PageUp>", VimKeys.PageUp)]
    [Arguments("<PageDown>", VimKeys.PageDown)]
    [Arguments("<Del>", VimKeys.Delete)]
    [Arguments("<lt>", "<")]
    [Arguments("<Space>", " ")]
    [Arguments("<Bar>", "|")]
    [Arguments("<Bslash>", "\\")]
    [Arguments("<C-U>", "<C-u>")]
    [Arguments("<c-w>", "<C-w>")]
    public async Task NamedKeysAreOneKey(string notation, string key)
    {
        // Act
        var keys = VimKeys.Parse(notation);

        // Assert
        await Assert.That(keys).IsEquivalentTo(new[] { key }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("<C-1>", 5)]   // Ctrl with a digit is no key name
    [Arguments("<X-a>", 5)]
    [Arguments("<>", 2)]      // nothing between the brackets
    [Arguments("<Esc", 4)]    // never closed
    public async Task WhatIsNoKeyNameIsItsCharacters(string notation, int count)
    {
        // Act
        var keys = VimKeys.Parse(notation);

        // Assert
        await Assert.That(keys.Count).IsEqualTo(count);
    }

    [Test]
    public async Task ASurrogatePairIsOneKey()
    {
        // Act
        var keys = VimKeys.Parse("a\U0001F600b");

        // Assert
        await Assert.That(keys).IsEquivalentTo(new[] { "a", "\U0001F600", "b" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ALoneHighSurrogateAtTheEndIsItself()
    {
        // Act
        var keys = VimKeys.Parse("a\uD83D");

        // Assert
        await Assert.That(keys).IsEquivalentTo(new[] { "a", "\uD83D" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("<C-u>", true, 'u')]
    [Arguments("<C-uu>", false, '\0')]
    [Arguments("<S-u>", false, '\0')]
    [Arguments("<C-u)", false, '\0')]
    public async Task IsCtrlOnlyForCtrlLetterKeys(string key, bool expected, char letter)
    {
        // Act
        bool isCtrl = VimKeys.IsCtrl(key, out char c);

        // Assert
        await Assert.That(isCtrl).IsEqualTo(expected);
        await Assert.That(c).IsEqualTo(letter);
    }

    [Test]
    public async Task FormatShowsASpaceAsItsName()
    {
        // Act
        string text = VimKeys.Format(["2", " ", "g"]);

        // Assert
        await Assert.That(text).IsEqualTo("2<Space>g");
    }
}

/// <summary>The parser's second keys: every g / z / [ ] / text-object form, and what each prefix refuses.</summary>
public class VimPrefixTests
{
    private static (VimParse Result, VimCommand Command) Last(string notation, bool visual = false)
    {
        var p = new VimKeyParser();
        VimParse result = VimParse.Pending;
        VimCommand command = default;
        foreach (var key in VimKeys.Parse(notation)) result = p.Feed(key, visual, out command);
        return (result, command);
    }

    [Test]
    [Arguments("ge", "ge")]
    [Arguments("gE", "gE")]
    [Arguments("gx", "gx")]
    [Arguments("gf", "gf")]
    [Arguments("go", "go")]
    [Arguments("gO", "gO")]
    [Arguments("gh", "gh")]
    [Arguments("gt", "gt")]
    [Arguments("gT", "gT")]
    [Arguments("g?", "g?")]
    [Arguments("g_", "g_")]
    [Arguments("gm", "gm")]
    [Arguments("z<CR>", "zt")]
    [Arguments("z.", "zz")]
    [Arguments("z-", "zb")]
    [Arguments("zt", "zt")]
    [Arguments("zb", "zb")]
    [Arguments("]]", "]]")]
    [Arguments("yaw", "aw")]
    [Arguments("yiW", "iW")]
    [Arguments("yip", "ip")]
    [Arguments("yas", "as")]
    public async Task SecondKeysCompleteTheCommand(string notation, string key)
    {
        // Act
        var (result, command) = Last(notation);

        // Assert
        await Assert.That(result).IsEqualTo(VimParse.Complete);
        await Assert.That(command.Key).IsEqualTo(key);
    }

    [Test]
    [Arguments("zq")]
    [Arguments("[]")]
    [Arguments("yiq")]
    [Arguments("f<Up>")]
    [Arguments("][")]
    [Arguments("<C-w><Tab>")]
    public async Task SecondKeysThatMeanNothingReportInvalid(string notation)
    {
        // Act
        var (result, _) = Last(notation);

        // Assert
        await Assert.That(result).IsEqualTo(VimParse.Invalid);
    }

    [Test]
    [Arguments("<C-w><Left>", 'h')]
    [Arguments("<C-w><Down>", 'j')]
    [Arguments("<C-w><Up>", 'k')]
    [Arguments("<C-w>0", '0')]       // without a count a 0 is the command's character
    public async Task CtrlWArrowsAreTheirLetters(string notation, char arg)
    {
        // Act
        var (result, command) = Last(notation);

        // Assert
        await Assert.That(result).IsEqualTo(VimParse.Complete);
        await Assert.That(command.Arg).IsEqualTo(arg);
    }

    [Test]
    public async Task CtrlWCountTakesAZeroAfterADigit()
    {
        // Act
        var (_, command) = Last("<C-w>10>");

        // Assert
        await Assert.That(command.Count).IsEqualTo(10);
    }

    [Test]
    [Arguments("iw", "iw")]
    [Arguments("ap", "ap")]
    public async Task InVisualModeIAndAStartTextObjects(string notation, string key)
    {
        // Act
        var (result, command) = Last(notation, visual: true);

        // Assert
        await Assert.That(result).IsEqualTo(VimParse.Complete);
        await Assert.That(command.Key).IsEqualTo(key);
    }

    [Test]
    public async Task InVisualModeYIsNoOperator()
    {
        // Act
        var (result, command) = Last("y", visual: true);

        // Assert
        await Assert.That(result).IsEqualTo(VimParse.Complete);
        await Assert.That(command.Operator).IsEqualTo('\0');
    }

    [Test]
    public async Task YankWithAMotionThatIsNoTextObjectTakesTheMotion()
    {
        // Act
        var (result, command) = Last("yj");

        // Assert
        await Assert.That(result).IsEqualTo(VimParse.Complete);
        await Assert.That((command.Operator, command.Key)).IsEqualTo(('y', "j"));
    }

    [Test]
    public async Task CountsAreCappedAtFiveDigits()
    {
        // Act
        var (_, command) = Last("1234567j");

        // Assert
        await Assert.That(command.Count).IsEqualTo(99_999);
    }
}

/// <summary>The keys vim mode takes as aliases of hjkl and friends move the cursor like them.</summary>
public class VimKeyAliasTests
{
    private static Terminal Text() => VimMotionTests.T(20, 5, "alpha beta\r\ngamma delta\r\nepsilon\r\nzeta eta");

    private static (long Line, int Col) After(string keys, int row = 1, int col = 3)
    {
        var t = Text();
        var (vim, host) = VimMotionTests.Vim(t, row, col);
        vim.FeedKeys(keys, host);
        return (vim.Cursor.Line - t.ScreenTopLine, vim.Cursor.Col);
    }

    [Test]
    [Arguments("<Left>", "h")]
    [Arguments("<BS>", "h")]
    [Arguments("<C-h>", "h")]
    [Arguments("<Right>", "l")]
    [Arguments("<Space>", "l")]
    [Arguments("<Down>", "j")]
    [Arguments("<C-n>", "j")]
    [Arguments("<C-j>", "j")]
    [Arguments("<Up>", "k")]
    [Arguments("<C-p>", "k")]
    [Arguments("<CR>", "+")]
    [Arguments("<Home>", "0")]
    [Arguments("<End>", "$")]
    public async Task AliasMovesLikeItsKey(string alias, string key)
    {
        // Arrange
        var expected = After(key);

        // Act
        var moved = After(alias);

        // Assert
        await Assert.That(moved).IsEqualTo(expected);
    }

    [Test]
    public async Task AliasesMoveAtAll()
    {
        // Act: right, then down, from row 1 col 3
        var moved = After("<Right><Down>");

        // Assert
        await Assert.That(moved).IsEqualTo((2L, 4));
    }

    [Test]
    [Arguments("<PageUp>", "<C-b>")]
    [Arguments("<PageDown>", "<C-f>")]
    public async Task PageKeysScrollLikeCtrlBAndCtrlF(string alias, string key)
    {
        // Arrange
        // Each engine scrolls its own terminal's view.
        static Terminal Lines() => VimMotionTests.T(10, 3, string.Join("\r\n", Enumerable.Range(0, 30).Select(i => "line" + i)));
        var (a, ah) = VimMotionTests.Vim(Lines(), 1, 0);
        var (b, bh) = VimMotionTests.Vim(Lines(), 1, 0);
        a.FeedKeys("gg", ah);
        b.FeedKeys("gg", bh);
        a.FeedKeys("<C-f><C-f>", ah);
        b.FeedKeys("<C-f><C-f>", bh);

        // Act
        a.FeedKeys(alias, ah);
        b.FeedKeys(key, bh);

        // Assert
        await Assert.That(a.Cursor).IsEqualTo(b.Cursor);
    }
}
