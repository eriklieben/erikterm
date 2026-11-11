using System.Text;
using ErikTerm.Config;
using ErikTerm.Core;

namespace ErikTerm.Tests;

public class ShellIntegrationTests
{
    private static Terminal Feed(Terminal t, string s)
    {
        t.Feed(Encoding.UTF8.GetBytes(s));
        return t;
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    // ---- OSC 8 --------------------------------------------------------------------------------

    [Test]
    public async Task Osc8_TagsPrintedCellsWithTheLink()
    {
        // Arrange
        var t = new Terminal(20, 3);

        // Act
        Feed(t, "ab\e]8;;https://example.com\e\\link\e]8;;\e\\cd");
        var row = t.GetScreenRow(0);

        // Assert
        await Assert.That(row.LinkIdAt(1)).IsEqualTo(0);
        await Assert.That(t.GetHyperlink(row.LinkIdAt(2))?.Uri).IsEqualTo("https://example.com");
        await Assert.That(row.LinkIdAt(5)).IsEqualTo(row.LinkIdAt(2));
        await Assert.That(row.LinkIdAt(6)).IsEqualTo(0);
        await Assert.That(row.Links!).HasSingleItem();
        await Assert.That(t.HyperlinkAt(t.ScreenTopLine, 3)?.Uri).IsEqualTo("https://example.com");
    }

    [Test]
    public async Task Osc8_BelTerminatorAndIdParameter()
    {
        // Arrange
        var t = new Terminal(20, 3);

        // Act
        Feed(t, "\e]8;id=x:foo=bar;file:///tmp/a\aé\e]8;;\a");
        var link = t.HyperlinkAt(t.ScreenTopLine, 0);

        // Assert
        var found = await Assert.That(link).IsNotNull();
        await Assert.That(found.Uri).IsEqualTo("file:///tmp/a");
        await Assert.That(found.Key).IsEqualTo("x");
    }

    [Test]
    public async Task Osc8_SameTargetReusesId_AndSpansWrapAcrossRows()
    {
        // Arrange
        var t = new Terminal(5, 3);

        // Act
        Feed(t, "\e]8;;http://a\e\\abcdefg\e]8;;\e\\ \e]8;;http://a\e\\h");
        int id = t.GetScreenRow(0).LinkIdAt(0);

        // Assert
        await Assert.That(id).IsNotEqualTo(0);
        await Assert.That(t.GetScreenRow(0).LinkIdAt(4)).IsEqualTo(id);
        await Assert.That(t.GetScreenRow(1).LinkIdAt(1)).IsEqualTo(id);
        await Assert.That(t.GetScreenRow(1).LinkIdAt(2)).IsEqualTo(0);
        await Assert.That(t.GetScreenRow(1).LinkIdAt(3)).IsEqualTo(id); // same URI, same table entry
    }

    [Test]
    public async Task Osc8_OverwritingRemovesTheLinkFromThoseCells()
    {
        // Arrange
        var t = Feed(new Terminal(20, 3), "\e]8;;http://a\e\\abcdef\e]8;;\e\\");

        // Act
        Feed(t, "\r\e[2CXY"); // overwrite c,d without a link
        var row = t.GetScreenRow(0);

        // Assert
        await Assert.That(row.LinkIdAt(1)).IsNotEqualTo(0);
        await Assert.That(row.LinkIdAt(2)).IsEqualTo(0);
        await Assert.That(row.LinkIdAt(3)).IsEqualTo(0);
        await Assert.That(row.LinkIdAt(4)).IsNotEqualTo(0);
    }

    [Test]
    public async Task Osc8_ErasingRemovesTheLink()
    {
        // Arrange
        var t = Feed(new Terminal(20, 3), "\e]8;;http://a\e\\abcdef\e]8;;\e\\");
        Feed(t, "\r\e[2CXY");
        var row = t.GetScreenRow(0);

        // Act
        Feed(t, "\r\e[K");

        // Assert
        await Assert.That(row.HasLinks).IsFalse();
    }

    [Test]
    public async Task RowClear_ResetsMetadata_WhenScrolledRowsAreRecycled()
    {
        // Arrange
        var t = new Terminal(10, 2, scrollbackLines: 1);

        // Act
        Feed(t, "\e]133;A\e\\\e]8;;http://a\e\\xx\e]8;;\e\\\r\n\r\n\r\n\r\n");

        // Assert
        for (int y = 0; y < 2; y++)
        {
            await Assert.That(t.GetScreenRow(y).HasLinks).IsFalse();
            await Assert.That(t.GetScreenRow(y).Marks).IsEqualTo(SemanticMarks.None);
        }
    }

    [Test]
    public async Task RowSetLink_SplitsSpans()
    {
        // Arrange
        var row = new Row(20);
        row.SetLink(0, 10, 1);

        // Act
        row.SetLink(3, 2, 2);
        var ids = Enumerable.Range(0, 6).Select(row.LinkIdAt).ToList();

        // Assert
        await Assert.That(ids).IsEquivalentTo([1, 1, 1, 2, 2, 1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RowSetLink_FillingTheHoleMergesSpans()
    {
        // Arrange
        var row = new Row(20);
        row.SetLink(0, 10, 1);
        row.SetLink(3, 2, 2);

        // Act
        row.SetLink(3, 2, 1); // fill the hole: back to one span

        // Assert
        await Assert.That(row.Links!).HasSingleItem();
        await Assert.That(row.Links![0].End).IsEqualTo(10);
    }

    [Test]
    public async Task RowSetLink_ClearingTheRowRemovesEveryLink()
    {
        // Arrange
        var row = new Row(20);
        row.SetLink(0, 10, 1);
        row.SetLink(3, 2, 2);
        row.SetLink(3, 2, 1);

        // Act
        row.SetLink(0, 20, 0);

        // Assert
        await Assert.That(row.HasLinks).IsFalse();
    }

    // ---- OSC 52 -------------------------------------------------------------------------------

    /// <summary>A terminal that records its OSC 52 clipboard requests and the bytes it answers with.</summary>
    private static (Terminal T, List<(ClipboardTarget Target, string Text)> Requests, List<byte[]> Responses) Clipboard()
    {
        var t = new Terminal(10, 2);
        var requests = new List<(ClipboardTarget, string)>();
        var responses = new List<byte[]>();
        t.ClipboardRequested += (target, text) => requests.Add((target, text));
        t.Response = d => responses.Add(d.ToArray());
        return (t, requests, responses);
    }

    [Test]
    [Arguments("c", "\a", ClipboardTarget.Clipboard, "héllo")]
    [Arguments("p", "\e\\", ClipboardTarget.Primary, "prim")]
    [Arguments("", "\a", ClipboardTarget.Clipboard, "default")]
    public async Task Osc52_RaisesClipboardRequests(string selection, string terminator, ClipboardTarget target, string text)
    {
        // Arrange
        var (t, requests, _) = Clipboard();

        // Act
        Feed(t, $"\e]52;{selection};{B64(text)}{terminator}");

        // Assert
        await Assert.That(requests).IsEquivalentTo([(target, text)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Osc52_QueryIsDenied()
    {
        // Arrange
        var (t, requests, responses) = Clipboard();

        // Act
        Feed(t, "\e]52;c;?\a");

        // Assert
        await Assert.That(responses).IsEmpty();
        await Assert.That(requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Osc52_InvalidBase64IsIgnored()
    {
        // Arrange
        var (t, requests, _) = Clipboard();

        // Act
        Feed(t, "\e]52;c;not base64!!\a");

        // Assert
        await Assert.That(requests.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Osc52_WritesCanBeDisabled()
    {
        // Arrange
        var (t, requests, _) = Clipboard();
        t.AllowClipboardWrite = false;

        // Act
        Feed(t, $"\e]52;c;{B64("x")}\a");

        // Assert
        await Assert.That(requests.Count).IsEqualTo(0);
    }

    // ---- OSC 133 ------------------------------------------------------------------------------

    private const string A = "\e]133;A\e\\", B = "\e]133;B\e\\", C = "\e]133;C\e\\";
    private const string D0 = "\e]133;D;0\e\\";
    private static string D(int code) => $"\e]133;D;{code}\e\\";

    /// <summary>Two commands: `ls` (ok, two lines of output) and `false` (exit 1, no output), then a fresh prompt.</summary>
    private static Terminal Session()
    {
        var t = new Terminal(20, 10, 100);
        Feed(t, $"{A}$ {B}ls\r\n{C}one\r\ntwo\r\n{D(0)}{A}$ {B}false\r\n{C}{D(1)}{A}$ {B}");
        return t;
    }

    [Test]
    public async Task Osc133_MarksRowsAndRecordsExitCodes()
    {
        // Act
        var t = Session();
        var r0 = t.GetScreenRow(0);

        // Assert
        await Assert.That(t.HasPromptMarks).IsTrue();
        await Assert.That(r0.Marks).IsEqualTo(SemanticMarks.Prompt | SemanticMarks.Input);
        await Assert.That(r0.CommandColumn).IsEqualTo(2);
        await Assert.That(r0.ExitCode).IsEqualTo(0);
        await Assert.That(t.GetScreenRow(1).Marks).IsEqualTo(SemanticMarks.Output);
        await Assert.That(t.GetScreenRow(3).CommandFailed).IsTrue();
        await Assert.That(t.GetScreenRow(4).ExitCode).IsEqualTo(Row.NoExitCode); // still at the prompt
        await Assert.That(t.LastExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task Osc133_EndWithoutCommandDoesNotRecordAStatus()
    {
        // Arrange
        var t = new Terminal(20, 5);

        // Act
        Feed(t, $"{A}$ {B}\r\n{D(0)}{A}$ {B}");

        // Assert
        await Assert.That(t.GetScreenRow(0).ExitCode).IsEqualTo(Row.NoExitCode);
        await Assert.That(t.LastExitCode).IsNull();
    }

    [Test]
    [Arguments(0, 1, 3)]
    [Arguments(3, 1, 4)]
    [Arguments(4, 1, null)]
    [Arguments(4, -1, 3)]
    [Arguments(0, -1, null)]
    public async Task FindPrompt_WalksMarksBothWays(int fromRow, int direction, int? expectedRow)
    {
        // Arrange
        var t = Session();
        long top = t.ScreenTopLine;
        long? expected = expectedRow is { } row ? top + row : null;

        // Act
        long? found = t.FindPrompt(top + fromRow, direction);

        // Assert
        await Assert.That(found).IsEqualTo(expected);
    }

    [Test]
    public async Task CommandAt_FindsOutputOfTheCommandUnderALine()
    {
        // Arrange
        var t = Session();
        long top = t.ScreenTopLine;

        // Act
        var block = t.CommandAt(top + 1);
        string? output = block is { } b ? t.GetText(b.OutputStart, 0, b.OutputEnd, 19) : null;

        // Assert
        await Assert.That(block).IsNotNull();
        await Assert.That((block!.Value.PromptLine, block.Value.OutputStart, block.Value.OutputEnd)).IsEqualTo((top, top + 1, top + 2));
        await Assert.That(block.Value.Command).IsEqualTo("ls");
        await Assert.That(block.Value.ExitCode).IsEqualTo(0);
        await Assert.That(output).IsEqualTo("one\ntwo");
    }

    [Test]
    public async Task CommandAt_TheCurrentPromptFallsBackToTheLastCommandWithOutput()
    {
        // Arrange
        var t = Session();

        // Act
        var block = t.CommandAt(t.ScreenTopLine + t.CursorY);

        // Assert
        await Assert.That(block?.PromptLine).IsEqualTo(t.ScreenTopLine);
    }

    [Test]
    public async Task CommandAt_ACommandWithoutOutputFallsBackToTheLastCommandWithOutput()
    {
        // Arrange
        var t = Session();
        long top = t.ScreenTopLine;

        // Act
        var block = t.CommandAt(top + 3); // `false`

        // Assert
        await Assert.That(block?.PromptLine).IsEqualTo(top);
    }

    [Test]
    public async Task CommandAt_RunningCommandEndsAtTheCursor()
    {
        // Arrange
        var t = new Terminal(20, 6);

        // Act
        Feed(t, $"{A}$ {B}make\r\n{C}building\r\nlinking");
        var block = t.CommandAt(t.ScreenTopLine + 2);

        // Assert
        await Assert.That((block?.OutputStart, block?.OutputEnd)).IsEqualTo((t.ScreenTopLine + 1, t.ScreenTopLine + 2));
        await Assert.That(t.CommandRunning).IsTrue();
    }

    [Test]
    public async Task CommandEndClearsCommandRunning()
    {
        // Arrange
        var t = Feed(new Terminal(20, 6), $"{A}$ {B}make\r\n{C}building\r\nlinking");

        // Act
        Feed(t, $"\r\n{D(0)}{A}$ {B}");

        // Assert
        await Assert.That(t.CommandRunning).IsFalse();
    }

    [Test]
    // A transient prompt (oh-my-posh) redraws the prompt shorter and erases the row that had the B mark;
    // the shell's cmdline_url still names the command.
    [Arguments($"{A}\r\n~/src\r\n> {B}\e[2A\r\e[J> git log | head\r\n\e]133;C;cmdline_url=git%20log%20%7C%20head%20%C3%A9\e\\x\r\n{D0}{A}$ {B}", "git log | head é")]
    // Raw kitty form, which may contain ';'.
    [Arguments($"{A}$ {B}a\r\n\e]133;C;cmdline=echo a; echo b\e\\x\r\n{D0}", "echo a; echo b")]
    // Multi-line commands are shown on one line.
    [Arguments($"{A}$ {B}a\r\n\e]133;C;cmdline_url=for%20x%20in%201%0Ado%20:%0Adone\e\\x\r\n{D0}", "for x in 1 do : done")]
    // Without it, the text after B.
    [Arguments($"{A}$ {B}ls -l\r\n{C}x\r\n{D0}", "ls -l")]
    public async Task CommandAt_PrefersTheCommandLineReportedWithC(string output, string expected)
    {
        // Arrange
        var t = new Terminal(30, 8);

        // Act
        Feed(t, output);
        string? command = t.CommandAt(t.ScreenTopLine + 1)?.Command;

        // Assert
        await Assert.That(command).IsEqualTo(expected);
    }

    [Test]
    public async Task CommandAt_OutputMarkOfAnEmptyCommandOnTheNextPromptRowIsNotItsOutput()
    {
        // Arrange
        // `true` prints nothing: its C, D and the next A share a row. The next command's output starts below.
        var t = Feed(new Terminal(30, 8), $"{A}$ {B}true\r\n{C}{D(0)}{A}$ {B}ls\r\n{C}a\r\nb\r\n{D(0)}{A}$ {B}");
        long top = t.ScreenTopLine;

        // Act
        var block = t.CommandAt(top + 2);

        // Assert
        await Assert.That((block?.PromptLine, block?.OutputStart, block?.OutputEnd)).IsEqualTo((top + 1, top + 2, top + 3));
        await Assert.That(block?.Command).IsEqualTo("ls");
    }

    [Test]
    public async Task ScrollViewToLine_PutsPromptAtTheTop()
    {
        // Arrange
        var t = new Terminal(20, 3, 100);
        for (int i = 0; i < 5; i++) Feed(t, $"{A}$ {B}cmd{i}\r\n{C}out\r\n{D(0)}");
        long? prev = t.FindPrompt(t.ViewTopLine, -1);

        // Act
        if (prev is { } line) t.ScrollViewToLine(line);
        string top = t.GetText(t.ViewTopLine, 0, t.ViewTopLine, 19);

        // Assert
        await Assert.That(prev).IsNotNull();
        await Assert.That(t.ViewTopLine).IsEqualTo(prev!.Value);
        await Assert.That(top).StartsWith("$ cmd");
    }

    [Test]
    public async Task Osc133_IgnoredOnTheAlternateScreen()
    {
        // Arrange
        var t = new Terminal(20, 3);

        // Act
        Feed(t, $"\e[?1049h{A}x");

        // Assert
        await Assert.That(t.HasPromptMarks).IsFalse();
    }

    // ---- LineText -----------------------------------------------------------------------------

    [Test]
    public async Task LineText_JoinsSoftWrapsAndMapsBackToCells()
    {
        // Arrange
        var t = Feed(new Terminal(5, 4), "abcdefg\r\n中x");
        var lt = new LineText();

        // Act
        lt.Build(t, t.ScreenTopLine, t.ScreenTopLine + 2);

        // Assert
        await Assert.That(lt.Text).IsEqualTo("abcdefg\n中x\n");
        await Assert.That(lt[6]).IsEqualTo(new CellPos(t.ScreenTopLine + 1, 1));
        await Assert.That(lt[9]).IsEqualTo(new CellPos(t.ScreenTopLine + 2, 2)); // 'x' after the wide char
    }

    [Test]
    public async Task LineText_BuildsOneLogicalLine()
    {
        // Arrange
        var t = Feed(new Terminal(5, 4), "abcdefg\r\n中x");
        var lt = new LineText();
        lt.Build(t, t.ScreenTopLine, t.ScreenTopLine + 2);

        // Act
        lt.BuildLogicalLine(t, t.ScreenTopLine + 1);

        // Assert
        await Assert.That(lt.Text).IsEqualTo("abcdefg\n");
        await Assert.That(lt.IndexOf(new CellPos(t.ScreenTopLine + 1, 0))).IsEqualTo(5);
    }
}

public class ShellIntegrationConfigTests
{
    [Test]
    public async Task Osc52_AsksByDefault()
    {
        // Act
        var settings = new Settings();

        // Assert
        await Assert.That(settings.Osc52).IsEqualTo(Osc52Policy.Ask);
        await Assert.That(settings.EnableOsc52).IsTrue();
    }

    [Test]
    public async Task EnableOsc52False_IsACompatibilityAliasForDeny()
    {
        // Act
        var r = SettingsLoader.Parse("enable_osc52 = false");

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.EnableOsc52).IsFalse();
        await Assert.That(r.Settings.Osc52).IsEqualTo(Osc52Policy.Deny);
    }

    [Test]
    [Arguments("enable_osc52 = true", Osc52Policy.Allow)]
    // osc52 wins over the alias, in either order.
    [Arguments("osc52 = \"ask\"\nenable_osc52 = true", Osc52Policy.Ask)]
    [Arguments("enable_osc52 = true\nosc52 = \"DENY\"", Osc52Policy.Deny)]
    public async Task EnableOsc52_IsACompatibilityAliasForOsc52(string toml, Osc52Policy expected)
    {
        // Act
        var policy = SettingsLoader.Parse(toml).Settings.Osc52;

        // Assert
        await Assert.That(policy).IsEqualTo(expected);
    }

    [Test]
    public async Task BadOsc52ValueIsReportedAndKeepsTheDefault()
    {
        // Act
        var bad = SettingsLoader.Parse("osc52 = \"1\"");

        // Assert
        await Assert.That(bad.Diagnostics).HasSingleItem();
        await Assert.That(bad.Settings.Osc52).IsEqualTo(Osc52Policy.Ask);
    }

    [Test]
    public async Task PasteSanitizeIsOnByDefault()
    {
        // Act
        var settings = new Settings();

        // Assert
        await Assert.That(settings.PasteSanitize).IsTrue();
    }

    [Test]
    public async Task PasteSanitizeParses()
    {
        // Act
        var r = SettingsLoader.Parse("paste_sanitize = false");

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.PasteSanitize).IsFalse();
    }

    [Test]
    public async Task DefaultKeys_HaveNoCollisions()
    {
        // Act
        var keys = DefaultKeys.Build();

        // Assert
        await Assert.That(keys.Select(k => k.Chord).Distinct().Count()).IsEqualTo(keys.Count);
    }

    [Test]
    public async Task DefaultKeys_BindShellActions()
    {
        // Act
        var keys = DefaultKeys.Build();

        // Assert
        await Assert.That(keys).Contains(k => k.Action.Name == "QuickSelect" && k.Chord.ToString() == "Ctrl+Shift+Space");
        await Assert.That(keys).Contains(k => k.Action.ToString() == "ScrollToPrompt(-1)" && k.Chord.ToString() == "Shift+↑");
        await Assert.That(keys).Contains(k => k.Action.ToString() == "ActivatePaneDirection(Up)" && k.Chord.ToString() == "Ctrl+Shift+↑");
    }

    [Test]
    [Arguments("ScrollToPrompt(1)", true)]
    [Arguments("ScrollToPrompt(2)", false)]
    public async Task ScrollToPromptTakesOneStepEitherWay(string spec, bool valid)
    {
        // Act
        bool parsed = ActionSpec.TryParse(spec, out _, out _);

        // Assert
        await Assert.That(parsed).IsEqualTo(valid);
    }
}
