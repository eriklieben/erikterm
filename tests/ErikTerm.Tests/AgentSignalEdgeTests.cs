using System.Text;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>The agent signal parsers' refusals and edge values: malformed records, empty parts and limits.</summary>
public class AgentSignalEdgeTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static (bool Ok, ProgramStatusRecord? Record, bool Clear, string? ClearId) Status(string arg)
    {
        bool ok = AgentSignals.TryParseProgramStatus(B(arg), out var record, out bool clear, out string? clearId);
        return (ok, record, clear, clearId);
    }

    [Test]
    [Arguments("", false)]
    [Arguments("?", true)]
    [Arguments("state=working", false)]
    public async Task OnlyAQuestionMarkIsTheProbe(string arg, bool expected)
    {
        // Act
        bool query = AgentSignals.IsProgramStatusQuery(B(arg));

        // Assert
        await Assert.That(query).IsEqualTo(expected);
    }

    [Test]
    [Arguments("app=claude-code")]               // no state
    [Arguments("state=busy:app=x")]              // a state the protocol doesn't have
    [Arguments("state=wörking")]                 // not ASCII
    [Arguments("state=workingworkingworking")]   // longer than any state
    [Arguments("state=")]
    public async Task RecordsWithoutAKnownStateAreRefused(string arg)
    {
        // Act
        var (ok, _, _, _) = Status(arg);

        // Assert
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task ClearWithAnIdDropsThatRecord()
    {
        // Act
        var (ok, record, clear, clearId) = Status("state=clear:id=task-7");

        // Assert
        await Assert.That((ok, clear, clearId)).IsEqualTo((true, true, "task-7"));
        await Assert.That(record).IsNull();
    }

    [Test]
    public async Task ClearWithoutAnIdDropsAll()
    {
        // Act
        var (_, _, clear, clearId) = Status("state=clear");

        // Assert
        await Assert.That((clear, clearId)).IsEqualTo((true, (string?)null));
    }

    [Test]
    public async Task IdleKeepsNeitherProgressNorKind()
    {
        // Act
        var (_, record, _, _) = Status("state=idle:app=claude-code:progress=40:kind=permission");

        // Assert
        await Assert.That(record).IsEqualTo(new ProgramStatusRecord("claude-code", "", ProgramState.Idle, null, null, null, null));
    }

    [Test]
    public async Task BlockedKeepsProgressAndKind()
    {
        // Act
        var (_, record, _, _) = Status("state=blocked:kind=permission:progress=250");

        // Assert
        await Assert.That(record).IsEqualTo(new ProgramStatusRecord("", "", ProgramState.Blocked, "permission", 100, null, null));
    }

    [Test]
    [Arguments("done", ProgramState.Done)]
    [Arguments("error", ProgramState.Error)]
    public async Task FinalStatesParse(string state, ProgramState expected)
    {
        // Act
        var (_, record, _, _) = Status("state=" + state);

        // Assert
        await Assert.That(record!.State).IsEqualTo(expected);
    }

    [Test]
    public async Task FieldsWithoutAValueAndUnknownKeysAreIgnored()
    {
        // Act
        var (_, record, _, _) = Status("junk:=x:color=red:state=working:progress=abc");

        // Assert
        await Assert.That(record).IsEqualTo(new ProgramStatusRecord("", "", ProgramState.Working, null, null, null, null));
    }

    [Test]
    public async Task TitleAndMessageAreDecoded()
    {
        // Act
        var (_, record, _, _) = Status($"state=done:title={B64("Build")}:msg={B64("all green")}");

        // Assert
        await Assert.That((record!.Title, record.Message)).IsEqualTo(("Build", "all green"));
    }

    [Test]
    [Arguments("not base64!")]
    [Arguments("")]
    public async Task TitleThatIsNoBase64IsDropped(string title)
    {
        // Act
        var (_, record, _, _) = Status("state=done:title=" + title);

        // Assert
        await Assert.That(record!.Title).IsNull();
    }

    [Test]
    public async Task TitleWithControlCharactersIsDropped()
    {
        // Act
        var (_, record, _, _) = Status("state=done:title=" + B64("evil\e[2J"));

        // Assert
        await Assert.That(record!.Title).IsNull();
    }

    [Test]
    public async Task TitleThatIsNoUtf8IsDropped()
    {
        // Act
        var (_, record, _, _) = Status("state=done:title=" + Convert.ToBase64String([0xFF, 0xFE]));

        // Assert
        await Assert.That(record!.Title).IsNull();
    }

    [Test]
    [Arguments("4")]        // ConEmu progress
    [Arguments("1;x")]      // another ConEmu sub-command
    [Arguments("7")]
    [Arguments("")]
    [Arguments("  \t")]     // nothing to show
    public async Task Osc9WithoutNotificationText(string arg)
    {
        // Act
        var n = AgentSignals.ParseOsc9(B(arg));

        // Assert
        await Assert.That(n).IsNull();
    }

    [Test]
    public async Task Osc9StartingWithADigitAndTextIsANotification()
    {
        // Act
        var n = AgentSignals.ParseOsc9(B("42 tests passed"));

        // Assert
        await Assert.That(n).IsEqualTo(new TerminalNotification(null, "42 tests passed", "osc9"));
    }

    [Test]
    [Arguments("4", 0, 0)]
    [Arguments("4;", 0, 0)]
    [Arguments("4;1;150", 1, 100)]
    [Arguments("4;2;abc", 2, 0)]
    [Arguments("4;3", 3, 0)]
    public async Task ProgressStatesAndValues(string arg, int state, int value)
    {
        // Act
        var progress = AgentSignals.ParseOsc9Progress(B(arg));

        // Assert
        await Assert.That(progress).IsEqualTo((state, value));
    }

    [Test]
    [Arguments("4;x;10")]
    [Arguments("4;5;10")]
    [Arguments("4;-1")]
    [Arguments("5;1")]
    [Arguments("40;1")]
    public async Task ProgressThatIsNoProgressIsRefused(string arg)
    {
        // Act
        var progress = AgentSignals.ParseOsc9Progress(B(arg));

        // Assert
        await Assert.That(progress).IsNull();
    }

    [Test]
    [Arguments("notify;Build done", null, "Build done")]   // a title alone is the body
    [Arguments("notify;;All green", null, "All green")]
    [Arguments("notify;CI;All green", "CI", "All green")]
    public async Task Osc777TitleAndBody(string arg, string? title, string body)
    {
        // Act
        var n = AgentSignals.ParseOsc777(B(arg));

        // Assert
        await Assert.That(n).IsEqualTo(new TerminalNotification(title, body, "osc777"));
    }

    [Test]
    [Arguments("notify;;")]
    [Arguments("notify; ; ")]
    [Arguments("other;x;y")]
    public async Task Osc777WithoutTextIsNothing(string arg)
    {
        // Act
        var n = AgentSignals.ParseOsc777(B(arg));

        // Assert
        await Assert.That(n).IsNull();
    }

    [Test]
    [Arguments("p=?;")]
    [Arguments("i=1:p=close;")]
    [Arguments("i=1:p=alive;")]
    public async Task KittyQueriesAndCloseRequestsShowNothing(string arg)
    {
        // Arrange
        var kitty = new AgentSignals.KittyNotifications();

        // Act
        var n = kitty.Feed(B(arg));

        // Assert
        await Assert.That(n).IsNull();
    }

    [Test]
    public async Task KittyBase64PartsAreDecoded()
    {
        // Arrange
        var kitty = new AgentSignals.KittyNotifications();

        // Act
        var n = kitty.Feed(B("e=1:a=focus;" + B64("Deploy finished")));

        // Assert
        await Assert.That(n).IsEqualTo(new TerminalNotification(null, "Deploy finished", "osc99", Focus: true));
    }

    [Test]
    public async Task KittyBadBase64IsNoText()
    {
        // Arrange
        var kitty = new AgentSignals.KittyNotifications();

        // Act
        var n = kitty.Feed(B("e=1;@@@"));

        // Assert
        await Assert.That(n).IsNull();
    }

    [Test]
    public async Task KittyPartsOtherThanTitleAndBodyAreIgnored()
    {
        // Arrange
        var kitty = new AgentSignals.KittyNotifications();
        kitty.Feed(B("i=5:d=0:p=icon;bell.png"));

        // Act
        var n = kitty.Feed(B("i=5:p=body;Done"));

        // Assert
        await Assert.That(n).IsEqualTo(new TerminalNotification(null, "Done", "osc99"));
    }

    [Test]
    public async Task KittyHeaderWithoutPayloadIsNothing()
    {
        // Arrange
        var kitty = new AgentSignals.KittyNotifications();

        // Act
        var n = kitty.Feed(B("i=1:d=1"));

        // Assert
        await Assert.That(n).IsNull();
    }

    [Test]
    public async Task KittyIdOrPartThatIsNoAsciiFallsBackToTheDefaults()
    {
        // Arrange: a bad id is id 0 and a bad part is the title, so these two parts make one notification
        var kitty = new AgentSignals.KittyNotifications();
        kitty.Feed(B("i=ä:d=0:p=bödy;Heads up"));

        // Act
        var n = kitty.Feed(B("i=0:p=body;Done"));

        // Assert
        await Assert.That(n).IsEqualTo(new TerminalNotification("Heads up", "Done", "osc99"));
    }

    [Test]
    public async Task KittyForgetsUnfinishedNotificationsAfterSixteen()
    {
        // Arrange: notification 0 is started, then sixteen others that never finish
        var kitty = new AgentSignals.KittyNotifications();
        kitty.Feed(B("i=first:d=0;Started"));
        for (int i = 0; i < 16; i++) kitty.Feed(B($"i=n{i}:d=0;x"));

        // Act
        var n = kitty.Feed(B("i=first:p=body;Done"));

        // Assert: its title was dropped with the others
        await Assert.That(n).IsEqualTo(new TerminalNotification(null, "Done", "osc99"));
    }

    [Test]
    public async Task KittyPartsPastTheLimitAreDropped()
    {
        // Arrange
        var kitty = new AgentSignals.KittyNotifications();
        kitty.Feed(B("i=1:d=0:p=body;start"));

        // Act
        var n = kitty.Feed(B("i=1:p=body;" + new string('x', AgentSignals.MaxMessageChars)));

        // Assert
        await Assert.That(n!.Body).IsEqualTo("start");
    }

    [Test]
    public async Task CleanDropsBidiControls()
    {
        // Act
        string? text = AgentSignals.Clean("a‮b⁦c‏d؜e", 100);

        // Assert
        await Assert.That(text).IsEqualTo("abcde");
    }

    [Test]
    public async Task CleanCapsLongTextWithAnEllipsis()
    {
        // Act
        string? text = AgentSignals.Clean("abcdefgh", 5);

        // Assert
        await Assert.That(text).IsEqualTo("abcd…");
    }

    [Test]
    public async Task CleanCollapsesWhitespaceAndDropsItAtTheEdges()
    {
        // Act
        string? text = AgentSignals.Clean("\t a \r\n  b  ", 100);

        // Assert
        await Assert.That(text).IsEqualTo("a b");
    }
}
