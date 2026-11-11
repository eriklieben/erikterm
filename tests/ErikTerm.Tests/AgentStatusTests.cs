using System.Text;
using ErikTerm.App.Cli;
using ErikTerm.App.Ipc;
using ErikTerm.Core;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.Tests;

public class AgentIdentifyTests
{
    [Test]
    [Arguments("claude", null, "claude-code")]
    [Arguments("2.1.319", "/home/u/.local/share/claude/versions/2.1.319", "claude-code")]
    [Arguments("codex", null, "codex")]
    [Arguments("node", "node|/usr/lib/node_modules/@openai/codex/bin/codex.js", "codex")]
    [Arguments("node", "node|/usr/local/bin/claude", "claude-code")]
    [Arguments("node", "node|/opt/node_modules/@google/gemini-cli/dist/index.js", "gemini-cli")]
    [Arguments("python3", "python3|-m|aider", "aider")]
    [Arguments("bun", "bun|/x/node_modules/@sourcegraph/amp/dist/main.js", "amp")]
    [Arguments("cursor-agent", null, "cursor-agent")]
    [Arguments("node", @"C:\Program Files\nodejs\node.exe|C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js", "claude-code")]
    [Arguments("node", @"node.exe|C:\Users\me\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js", "codex")]
    [Arguments("2.1.319", @"C:\Users\me\.local\share\claude\versions\2.1.319.exe", "claude-code")]
    [Arguments("claude", @"C:\Users\me\.local\bin\claude.exe", "claude-code")]
    [Arguments("node", "node|server.js", null)]
    [Arguments("nvim", "nvim|claude.md", null)]
    [Arguments("zsh", "-zsh", null)]
    public async Task FromProcess(string comm, string? argv, string? expected)
    {
        // Act
        var def = AgentCatalog.Identify(comm, argv?.Split('|'));

        // Assert
        await Assert.That(def?.Id).IsEqualTo(expected);
    }

    [Test]
    public async Task FromStatusAppReturnsTheCatalogDefinition()
    {
        // Act
        var claude = AgentCatalog.ByStatusApp("claude-code");
        var codex = AgentCatalog.ByStatusApp("codex");

        // Assert
        await Assert.That(claude).IsSameReferenceAs(AgentCatalog.Claude);
        await Assert.That(codex).IsSameReferenceAs(AgentCatalog.Codex);
    }

    [Test]
    public async Task FromStatusAppUnknownAppIsNull()
    {
        // Act
        var def = AgentCatalog.ByStatusApp("vim");

        // Assert
        await Assert.That(def).IsNull();
    }

    [Test]
    [Arguments("◐ Fix the parser", AgentState.Working)]
    [Arguments("◑ Fix the parser", AgentState.Working)]
    [Arguments("⠋ codex", AgentState.Working)]
    [Arguments("✳ Fix the parser", AgentState.Idle)]
    [Arguments("zsh", AgentState.None)]
    [Arguments("", AgentState.None)]
    public async Task TitleSpinners(string title, AgentState expected)
    {
        // Act
        var state = AgentTitle.Parse(title);

        // Assert
        await Assert.That(state).IsEqualTo(expected);
    }
}

public class AgentScreenRuleTests
{
    // Claude Code 2.x screens, as they look in an 80-column terminal.
    public static readonly string[] ClaudeWorking =
    [
        "⏺ I'll look at the parser first.",
        "",
        "⏺ Read(src/ErikTerm.Core/VtParser.cs)",
        "  ⎿  Read 420 lines (ctrl+o to expand)",
        "",
        "✻ Cogitating… (12s · ↓ 1.2k tokens · esc to interrupt)",
        "",
        "────────────────────────────────────────────────────────────────────────────────",
        "❯ ",
        "────────────────────────────────────────────────────────────────────────────────",
        "  ⏵⏵ accept edits on (shift+tab to cycle)",
    ];

    public static readonly string[] ClaudePermission =
    [
        "⏺ Bash(rm -rf build)",
        "  ⎿  Running…",
        "",
        "╭──────────────────────────────────────────────────────────────────────────────╮",
        "│ Bash command                                                                 │",
        "│                                                                              │",
        "│   rm -rf build                                                               │",
        "│   Remove the build directory                                                 │",
        "│                                                                              │",
        "│ Do you want to proceed?                                                      │",
        "│ ❯ 1. Yes                                                                     │",
        "│   2. Yes, and don't ask again for rm commands in this project               │",
        "│   3. No, and tell Claude what to do differently (esc)                        │",
        "╰──────────────────────────────────────────────────────────────────────────────╯",
    ];

    public static readonly string[] ClaudeIdle =
    [
        "✻ Thinking…",
        "",
        "⏺ Done — the parser now answers OSC 7501 probes and the tests pass.",
        "",
        "────────────────────────────────────────────────────────────────────────────────",
        "❯ ",
        "────────────────────────────────────────────────────────────────────────────────",
        "  ? for shortcuts",
    ];

    public static readonly string[] ClaudeQuestion =
    [
        "☐ Storage",
        "",
        "Where should the session file live?",
        "",
        "❯ 1. $XDG_STATE_HOME",
        "     The XDG state directory",
        "  2. $XDG_CONFIG_HOME",
        "  3. Type something.",
        "",
        "Enter to select · ↑/↓ to navigate · Esc to cancel",
    ];

    private static readonly string[] CodexWorking = ["• Explored", "  └ Read VtParser.cs", "", "• Working (8s • esc to interrupt)", "", "› Ask Codex to do anything", "", "  100% context left"];
    private static readonly string[] CodexWaiting = ["Would you like to run the following command?", "", "  $ cargo test", "", "› 1. Yes, proceed", "  2. No, and tell Codex what to do differently", "", "  Press enter to confirm or esc to cancel"];
    private static readonly string[] CodexIdle = ["• Done.", "", "› Ask Codex to do anything", "", "  98% context left · ? for shortcuts"];

    private static ScreenRule? Classify(AgentDefinition def, string[] rows) => ScreenClassifier.Classify(def.Rules, new StringScreen(rows));

    [Test]
    public async Task ClaudeWorkingScreenIsWorking()
    {
        // Act
        var rule = Classify(AgentCatalog.Claude, ClaudeWorking);

        // Assert
        await Assert.That(rule?.State).IsEqualTo(AgentState.Working);
    }

    [Test]
    public async Task ClaudePermissionDialogIsWaitingForPermission()
    {
        // Act
        var waiting = Classify(AgentCatalog.Claude, ClaudePermission);

        // Assert
        await Assert.That(waiting?.State).IsEqualTo(AgentState.Waiting);
        await Assert.That(waiting?.Kind).IsEqualTo("permission");
    }

    [Test]
    public async Task ClaudeIdleScreenIsIdle()
    {
        // Act
        var rule = Classify(AgentCatalog.Claude, ClaudeIdle);

        // Assert
        await Assert.That(rule?.State).IsEqualTo(AgentState.Idle); // "✻ Thinking…" in the transcript isn't the spinner
    }

    [Test]
    public async Task ClaudeQuestionIsWaitingWithAQuestion()
    {
        // Act
        var q = Classify(AgentCatalog.Claude, ClaudeQuestion);

        // Assert
        await Assert.That(q?.State).IsEqualTo(AgentState.Waiting);
        await Assert.That(q?.Kind).IsEqualTo("question");
    }

    [Test]
    public async Task OldPromptLinesScrolledUpDontCount()
    {
        // Arrange
        // A permission question far above the bottom (the transcript quoting it) isn't a live dialog.
        var rows = new List<string> { "Do you want to proceed?", "❯ 1. Yes" };
        for (int i = 0; i < 30; i++) rows.Add("⏺ text");
        rows.AddRange(ClaudeIdle);

        // Act
        var rule = Classify(AgentCatalog.Claude, [.. rows]);

        // Assert
        await Assert.That(rule?.State).IsEqualTo(AgentState.Idle);
    }

    [Test]
    public async Task CodexWorkingScreenIsWorking()
    {
        // Act
        var rule = Classify(AgentCatalog.Codex, CodexWorking);

        // Assert
        await Assert.That(rule?.State).IsEqualTo(AgentState.Working);
    }

    [Test]
    public async Task CodexCommandApprovalIsWaiting()
    {
        // Act
        var rule = Classify(AgentCatalog.Codex, CodexWaiting);

        // Assert
        await Assert.That(rule?.State).IsEqualTo(AgentState.Waiting);
    }

    [Test]
    public async Task CodexIdleScreenIsIdle()
    {
        // Act
        var rule = Classify(AgentCatalog.Codex, CodexIdle);

        // Assert
        await Assert.That(rule?.State).IsEqualTo(AgentState.Idle);
    }

    [Test]
    public async Task NoRuleMatchesAPlainScreen()
    {
        // Act
        var rule = Classify(AgentCatalog.Claude, ["$ ls", "a b c", "$ "]);

        // Assert
        await Assert.That(rule).IsNull();
    }

    [Test]
    [Arguments("   ✽ Booping…", true)] // the glyph leads the row (after indentation)
    [Arguments("x ✽ Booping…", false)] // text before the glyph
    public async Task LeadingGlyphsCompareRunes(string row, bool expected)
    {
        // Arrange
        var clause = new ScreenClause(["…"], LeadingGlyphs: "✻✽");

        // Act
        bool holds = ScreenClassifier.Holds(clause, new StringScreen(row));

        // Assert
        await Assert.That(holds).IsEqualTo(expected);
    }

    [Test]
    [Arguments("🌟 Working…", true)] // a glyph outside the basic plane
    [Arguments("🌠 Working…", false)] // shares its high surrogate with the listed glyph
    [Arguments("✽ Working…", true)]
    public async Task LeadingGlyphsMatchWholeSurrogatePairs(string row, bool expected)
    {
        // Arrange
        var clause = new ScreenClause(["…"], LeadingGlyphs: "🌟✽");

        // Act
        bool holds = ScreenClassifier.Holds(clause, new StringScreen(row));

        // Assert
        await Assert.That(holds).IsEqualTo(expected);
    }
}

public class AgentOscTests
{
    private static (Terminal Term, List<byte[]> Replies) New()
    {
        var t = new Terminal(80, 24);
        var replies = new List<byte[]>();
        t.Response = d => replies.Add(d.ToArray());
        return (t, replies);
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    /// <summary>Claude Code's main record: blocked on a permission prompt, with progress, title and message.</summary>
    private static byte[] BlockedOnPermission() =>
        Encoding.ASCII.GetBytes($"\e]7501;state=blocked:app=claude-code:kind=permission:progress=40:title={B64("Fix parser")}:msg={B64("Claude needs your permission to use Bash")}\a");

    [Test]
    public async Task ProbeIsAnswered()
    {
        // Arrange
        var (t, replies) = New();

        // Act
        t.Feed("\e]7501;?\e\\"u8);
        var texts = replies.Select(r => Encoding.ASCII.GetString(r)).ToList();

        // Assert
        var s = await Assert.That(texts).HasSingleItem();
        await Assert.That(s).StartsWith("\e]7501;?"); // Claude Code accepts any OSC 7501 reply whose data starts with '?'
        await Assert.That(s).Contains("states=idle,working,done,blocked,error");
        await Assert.That(s).EndsWith("\e\\");
    }

    [Test]
    public async Task ProbeIgnoredWhenDisabled()
    {
        // Arrange
        var (t, replies) = New();
        t.ProgramStatusEnabled = false;

        // Act
        t.Feed("\e]7501;?\a"u8);

        // Assert
        await Assert.That(replies).IsEmpty();
    }

    [Test]
    public async Task RecordsTheReportedStatus()
    {
        // Arrange
        var (t, _) = New();
        int v0 = t.AgentSignalVersion;

        // Act
        t.Feed(BlockedOnPermission());

        // Assert
        var r = await Assert.That(t.ProgramStatus).HasSingleItem();
        await Assert.That((r!.App, r.Id, r.State, r.Kind, r.Progress)).IsEqualTo(("claude-code", "", ProgramState.Blocked, "permission", 40));
        await Assert.That(r.Title).IsEqualTo("Fix parser");
        await Assert.That(r.Message).IsEqualTo("Claude needs your permission to use Bash");
        await Assert.That(t.AgentSignalVersion).IsNotEqualTo(v0);
    }

    [Test]
    public async Task ClearDropsTheRecordWithThatIdThenTheRest()
    {
        // Arrange
        var (t, _) = New();
        t.Feed(BlockedOnPermission());

        // Act
        t.Feed("\e]7501;state=working:app=claude-code:id=task-1\a"u8);
        var withTask = t.ProgramStatus.ToList();
        t.Feed("\e]7501;state=clear:id=task-1\a"u8);
        var afterClearingTheTask = t.ProgramStatus.ToList();
        t.Feed("\e]7501;state=clear\a"u8);
        var afterClear = t.ProgramStatus.ToList();

        // Assert
        await Assert.That(withTask.Count).IsEqualTo(2);
        await Assert.That(afterClearingTheTask).HasSingleItem();
        await Assert.That(afterClear).IsEmpty();
    }

    [Test]
    public async Task ControlCharactersInAMessageAreRejected()
    {
        // Arrange
        byte[] arg = Encoding.ASCII.GetBytes($"state=done:app=x:msg={B64("bad\e[31m")}");

        // Act
        bool parsed = AgentSignals.TryParseProgramStatus(arg, out var r, out _, out _);

        // Assert
        await Assert.That(parsed).IsTrue();
        await Assert.That(r!.Message).IsNull(); // rejected, not rendered
    }

    [Test]
    public async Task BidiOverridesInAMessageAreDropped()
    {
        // Arrange
        // U+202E RIGHT-TO-LEFT OVERRIDE would make the rest read backwards.
        byte[] arg = Encoding.ASCII.GetBytes($"state=done:app=x:msg={B64("a\u202Etxt.exe")}");

        // Act
        bool parsed = AgentSignals.TryParseProgramStatus(arg, out var r, out _, out _);

        // Assert
        await Assert.That(parsed).IsTrue();
        await Assert.That(r!.Message).IsEqualTo("atxt.exe");
    }

    [Test]
    public async Task UnknownStateIsNotAStatus()
    {
        // Act
        bool parsed = AgentSignals.TryParseProgramStatus("state=dancing:app=x"u8, out _, out _, out _);

        // Assert
        await Assert.That(parsed).IsFalse();
    }

    [Test]
    public async Task IdleCarriesNoProgressOrKind()
    {
        // Act
        bool parsed = AgentSignals.TryParseProgramStatus("state=idle:app=x:progress=50:kind=auth"u8, out var r, out _, out _);

        // Assert
        await Assert.That(parsed).IsTrue();
        await Assert.That(r!.Progress).IsNull(); // only working/blocked carry progress
        await Assert.That(r.Kind).IsNull();
    }

    [Test]
    public async Task PromptDropsInFlightRecords()
    {
        // Arrange
        var (t, _) = New();
        t.Feed("\e]7501;state=working:app=codex\a\e]7501;state=done:app=codex:id=a\a"u8);

        // Act
        t.Feed("\e]133;A\a"u8);

        // Assert
        var left = await Assert.That(t.ProgramStatus).HasSingleItem();
        await Assert.That(left!.State).IsEqualTo(ProgramState.Done);
    }

    [Test]
    public async Task KittyNotificationWaitsForItsLastPart()
    {
        // Arrange
        var (t, _) = New();
        t.Feed("\e]9;Claude Code: Claude needs your permission to use Bash\a"u8);
        t.Feed("\e]777;notify;Claude Code;Claude is waiting for your input\e\\"u8);

        // Act
        t.Feed("\e]99;i=42:d=0:p=title;Claude Code\e\\\e]99;i=42:d=0:p=body;Done\e\\"u8);

        // Assert
        await Assert.That(t.PendingNotificationCount).IsEqualTo(2); // kitty waits for its last part
    }

    [Test]
    public async Task NotificationsAreDrainedInOrder()
    {
        // Arrange
        var (t, _) = New();
        t.Feed("\e]9;Claude Code: Claude needs your permission to use Bash\a"u8);
        t.Feed("\e]777;notify;Claude Code;Claude is waiting for your input\e\\"u8);
        t.Feed("\e]99;i=42:d=0:p=title;Claude Code\e\\\e]99;i=42:d=0:p=body;Done\e\\"u8);
        t.Feed("\e]99;i=42:d=1:a=focus;\e\\"u8);
        var list = new List<TerminalNotification>();

        // Act
        t.DrainNotifications(list);

        // Assert
        await Assert.That(list.Count).IsEqualTo(3);
        await Assert.That((list[0].Title, list[0].Body, list[0].Source)).IsEqualTo((null, "Claude Code: Claude needs your permission to use Bash", "osc9"));
        await Assert.That((list[1].Title, list[1].Body)).IsEqualTo(("Claude Code", "Claude is waiting for your input"));
        await Assert.That((list[2].Title, list[2].Body, list[2].Focus)).IsEqualTo(("Claude Code", "Done", true));
        await Assert.That(t.PendingNotificationCount).IsEqualTo(0);
    }

    [Test]
    public async Task ConEmuProgressIsNotANotification()
    {
        // Arrange
        var (t, _) = New();

        // Act
        t.Feed("\e]9;4;3;0\a"u8);

        // Assert
        await Assert.That(t.PendingNotificationCount).IsEqualTo(0);
        await Assert.That(t.Progress).IsEqualTo((3, 0));
    }

    [Test]
    public async Task ConEmuProgressUpdatesTheProgress()
    {
        // Arrange
        var (t, _) = New();
        t.Feed("\e]9;4;3;0\a"u8);

        // Act
        t.Feed("\e]9;4;1;40\a"u8);

        // Assert
        await Assert.That(t.Progress).IsEqualTo((1, 40));
    }

    [Test]
    public async Task OtherConEmuCommandsAreNotNotifications()
    {
        // Act
        var n = AgentSignals.ParseOsc9("1;x"u8);

        // Assert
        await Assert.That(n).IsNull();
    }

    [Test]
    public async Task KittyBase64AndSingleShot()
    {
        // Arrange
        var k = new AgentSignals.KittyNotifications();

        // Act
        var n = k.Feed(Encoding.ASCII.GetBytes($"i=1:e=1;{B64("Hello")}"));

        // Assert
        await Assert.That(n!.Body).IsEqualTo("Hello");
    }

    [Test]
    public async Task KittyQueryIsNotANotification()
    {
        // Arrange
        var k = new AgentSignals.KittyNotifications();

        // Act
        var n = k.Feed("i=2:p=?;"u8);

        // Assert
        await Assert.That(n).IsNull();
    }

    [Test]
    public async Task ResetForgetsStatus()
    {
        // Arrange
        var (t, _) = New();
        t.Feed("\e]7501;state=working:app=claude-code\a"u8);

        // Act
        t.Feed("\ec"u8);

        // Assert
        await Assert.That(t.ProgramStatus).IsEmpty();
    }
}

public class AgentTrackerTests
{
    private static AgentObservation Obs(AgentState heuristic, bool seen, long output = 0, long input = 0, bool agent = true) =>
        new(agent, heuristic == AgentState.None ? default : new AgentReport(heuristic, AgentSource.Heuristic, 0), AgentState.None, output, input, seen);

    [Test]
    public async Task FinishedOutOfSightIsDoneUntilSeen()
    {
        // Arrange
        var t = new AgentStatusTracker();

        // Act
        var started = t.Update(Obs(AgentState.Working, seen: false), 1000);
        var settling = t.Update(Obs(AgentState.Idle, seen: false), 5000);
        var finished = t.Update(Obs(AgentState.Idle, seen: false), 5000 + AgentStatusTracker.SettleMs);
        var later = t.Update(Obs(AgentState.Idle, seen: false), 9000);
        var seen = t.Update(Obs(AgentState.Idle, seen: true), 9500);

        // Assert
        await Assert.That(started?.To).IsEqualTo(AgentState.Working);
        await Assert.That(settling).IsNull(); // settling
        await Assert.That(finished?.To).IsEqualTo(AgentState.Done);
        await Assert.That(later).IsNull(); // stays done
        await Assert.That(seen?.To).IsEqualTo(AgentState.Idle);
    }

    [Test]
    public async Task FinishedWhileWatchedIsIdle()
    {
        // Arrange
        var t = new AgentStatusTracker();
        t.Update(Obs(AgentState.Working, seen: true), 1000);
        t.Update(Obs(AgentState.Idle, seen: true), 5000);

        // Act
        var settled = t.Update(Obs(AgentState.Idle, seen: true), 6000);

        // Assert
        await Assert.That(settled?.To).IsEqualTo(AgentState.Idle);
    }

    [Test]
    public async Task BriefFlickerIsNotDone()
    {
        // Arrange
        var t = new AgentStatusTracker();
        t.Update(Obs(AgentState.Idle, seen: false), 0);
        t.Update(Obs(AgentState.Working, seen: false), 1000);

        // Act
        var flicker = t.Update(Obs(AgentState.Idle, seen: false), 1300);
        var backToWork = t.Update(Obs(AgentState.Working, seen: false), 1500);
        t.Update(Obs(AgentState.Idle, seen: false), 1600);
        var finished = t.Update(Obs(AgentState.Idle, seen: false), 3000);

        // Assert
        await Assert.That(flicker).IsNull();
        await Assert.That(backToWork).IsNull(); // back to work before settling
        await Assert.That(finished?.To).IsEqualTo(AgentState.Done); // whole turn ≥ MinWorkMs
    }

    [Test]
    public async Task ShortTurnIsJustIdle()
    {
        // Arrange
        var u = new AgentStatusTracker();
        u.Update(Obs(AgentState.Working, seen: false), 1000);
        u.Update(Obs(AgentState.Idle, seen: false), 1200);

        // Act
        var settled = u.Update(Obs(AgentState.Idle, seen: false), 2100);

        // Assert
        await Assert.That(settled?.To).IsEqualTo(AgentState.Idle); // 200 ms of "work": just idle
    }

    [Test]
    public async Task ExplicitBeatsHeuristics()
    {
        // Arrange
        var t = new AgentStatusTracker();

        // Act
        t.Report(new AgentReport(AgentState.Waiting, AgentSource.Osc7501, 100, "Claude needs your permission to use Bash", "permission"));
        var tr = t.Update(Obs(AgentState.Working, seen: false), 200);
        var sourceWhileWaiting = t.Source;
        t.Report(new AgentReport(AgentState.Done, AgentSource.Osc7501, 300));
        var done = t.Update(Obs(AgentState.Working, seen: false), 300);
        t.ClearProgramStatus();
        var afterClear = t.Update(Obs(AgentState.Working, seen: false), 400);

        // Assert
        await Assert.That(tr?.To).IsEqualTo(AgentState.Waiting);
        await Assert.That(tr?.Kind).IsEqualTo("permission");
        await Assert.That(sourceWhileWaiting).IsEqualTo(AgentSource.Osc7501);
        await Assert.That(done?.To).IsEqualTo(AgentState.Done); // explicit: no settling
        await Assert.That(afterClear?.To).IsEqualTo(AgentState.Working);
    }

    [Test]
    public async Task HookStatesAndStaleWorking()
    {
        // Arrange
        var t = new AgentStatusTracker();

        // Act
        t.Report(new AgentReport(AgentState.Working, AgentSource.Hook, 0));
        var working = t.Update(Obs(AgentState.None, seen: false), 0);
        // Interrupted turn: no Stop hook, the screen is idle and quiet for a while.
        var quietBriefly = t.Update(Obs(AgentState.Idle, seen: false, output: 100), 2000);
        t.Update(Obs(AgentState.Idle, seen: false, output: 100), 100 + AgentStatusTracker.StaleHookMs + 1);
        var stale = t.Update(Obs(AgentState.Idle, seen: false, output: 100), 100 + AgentStatusTracker.StaleHookMs + 1000);
        t.Report(new AgentReport(AgentState.Done, AgentSource.Hook, 9000));
        var hookDone = t.Update(Obs(AgentState.Idle, seen: false), 9000);

        // Assert
        await Assert.That(working?.To).IsEqualTo(AgentState.Working);
        await Assert.That(quietBriefly).IsNull();
        await Assert.That(stale?.To).IsEqualTo(AgentState.Done);
        await Assert.That(hookDone).IsNull();
    }

    [Test]
    public async Task NotificationWaitsUntilTheAgentMovesOn()
    {
        // Arrange
        var t = new AgentStatusTracker();
        t.Update(Obs(AgentState.Idle, seen: false), 0);

        // Act
        t.Report(new AgentReport(AgentState.Waiting, AgentSource.Notification, 1000, "Claude is waiting for your input"));
        var tr = t.Update(Obs(AgentState.Idle, seen: false, output: 900), 1000);
        var looked = t.Update(Obs(AgentState.Idle, seen: true), 2000);
        var typed = t.Update(Obs(AgentState.Idle, seen: true, input: 2500), 2600);

        // Assert
        await Assert.That((tr?.To, tr?.Message)).IsEqualTo((AgentState.Waiting, "Claude is waiting for your input"));
        await Assert.That(looked).IsNull(); // looking at it doesn't answer it
        await Assert.That(typed?.To).IsEqualTo(AgentState.Idle); // typing does
    }

    [Test]
    public async Task ActivityFallback()
    {
        // Arrange
        var t = new AgentStatusTracker();

        // Act
        var first = t.Update(Obs(AgentState.None, seen: false, output: 1000), 1000);
        var second = t.Update(Obs(AgentState.None, seen: false, output: 1000), 2000);

        // Assert
        await Assert.That(first?.To).IsEqualTo(AgentState.Working);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task ActivityFallbackIgnoresTheEchoOfTyping()
    {
        // Arrange
        // Echo of typing isn't work.
        var e = new AgentStatusTracker();

        // Act
        var tr = e.Update(Obs(AgentState.None, seen: true, output: 1100, input: 1000), 1100);

        // Assert
        await Assert.That(tr?.To).IsEqualTo(AgentState.Idle);
    }

    [Test]
    public async Task AgentGoneResets()
    {
        // Arrange
        var t = new AgentStatusTracker();
        t.Update(Obs(AgentState.Working, seen: false), 0);

        // Act
        t.Reset(100);
        var stateAfterReset = t.State;
        var next = t.Update(Obs(AgentState.None, seen: false, agent: false), 200);

        // Assert
        await Assert.That(stateAfterReset).IsEqualTo(AgentState.None);
        await Assert.That(next).IsNull();
    }

    [Test]
    [Arguments("waiting", AgentState.Waiting)]
    [Arguments("WORKING", AgentState.Working)]
    [Arguments("done", AgentState.Done)]
    [Arguments("clear", AgentState.None)]
    [Arguments("bogus", null)]
    public async Task StateWords(string s, AgentState? expected)
    {
        // Act
        var state = AgentStateText.Parse(s);

        // Assert
        await Assert.That(state).IsEqualTo(expected);
    }

    [Test]
    [Arguments(AgentState.Waiting, "permission", "needs permission")]
    [Arguments(AgentState.Waiting, "question", "has a question")]
    [Arguments(AgentState.Done, "permission", "done")]
    public async Task Descriptions(AgentState state, string kind, string expected)
    {
        // Act
        string description = AgentStateText.Describe(state, kind);

        // Assert
        await Assert.That(description).IsEqualTo(expected);
    }
}

public class AgentCliTests
{
    private static CliInvocation Parse(params string[] args) => CliParser.Parse(args);

    /// <summary>
    /// A process environment with no erikterm in it: XDG_RUNTIME_DIR names a directory that doesn't exist and nothing
    /// else is set, so socket discovery can neither find the user's live instance nor clean up anything on disk.
    /// </summary>
    private static Func<string, string?> NoErikterm()
    {
        string runtime = Path.Combine(Path.GetTempPath(), "erikterm-none-" + Guid.NewGuid());
        return name => name == "XDG_RUNTIME_DIR" ? runtime : null;
    }

    [Test]
    public async Task SetAgentStatus()
    {
        // Act
        var inv = Parse("set-agent-status", "--state", "waiting", "--message", "needs a decision", "--kind", "question", "--pane-id", "3");

        // Assert
        await Assert.That((inv.Request.Command, inv.Request.State, inv.Request.Message, inv.Request.Kind, inv.Request.PaneId)).IsEqualTo(("set-agent-status", "waiting", "needs a decision", "question", 3));
    }

    [Test]
    public async Task SetAgentStatusTakesAPositionalState()
    {
        // Act
        var inv = Parse("set-agent-status", "Done");

        // Assert
        await Assert.That(inv.Request.State).IsEqualTo("done");
    }

    [Test]
    public async Task SetAgentStatusHookInput()
    {
        // Act
        var inv = Parse("set-agent-status", "--state=working", "--hook-input");

        // Assert
        await Assert.That(inv.HookInput).IsTrue();
    }

    [Test]
    [Arguments("set-agent-status")] // no state
    [Arguments("set-agent-status", "--state", "sleepy")] // not a state
    public async Task SetAgentStatusRejectsBadUsage(params string[] args)
    {
        // Act
        var act = () => Parse(args);

        // Assert
        await Assert.That(act).ThrowsExactly<CliUsageException>();
    }

    [Test]
    public async Task AgentsTakesAFormat()
    {
        // Act
        var inv = Parse("agents", "--format", "json");

        // Assert
        await Assert.That(inv.Format).IsEqualTo("json");
    }

    [Test]
    public async Task StatuslineReportsUsageAndKeepsThePassthrough()
    {
        // Act
        var s = Parse("statusline", "--passthrough", "bash ~/.claude/statusline-command.sh");

        // Assert
        await Assert.That(s.Request.Command).IsEqualTo("report-usage");
        await Assert.That(s.Passthrough).IsEqualTo("bash ~/.claude/statusline-command.sh");
    }

    [Test]
    public async Task StatuslineTakesNoFormat()
    {
        // Act
        var act = () => Parse("statusline", "--format", "json");

        // Assert
        await Assert.That(act).ThrowsExactly<CliUsageException>();
    }

    // The shape Claude Code 2.1.x pipes to the status line command.
    private const string Payload = """
        {
          "session_id": "3f2c9d1e-aaaa-bbbb-cccc-1234567890ab",
          "transcript_path": "/home/u/.claude/projects/x/3f2c.jsonl",
          "cwd": "/home/u/src/erikterm",
          "model": { "id": "claude-opus-5-5", "display_name": "Opus 5.5" },
          "workspace": { "current_dir": "/home/u/src/erikterm", "project_dir": "/home/u/src/erikterm", "added_dirs": [] },
          "version": "2.1.319",
          "output_style": { "name": "default" },
          "cost": { "total_cost_usd": 0.4213, "total_duration_ms": 61000, "total_api_duration_ms": 30000, "total_lines_added": 12, "total_lines_removed": 3 },
          "context_window": { "total_input_tokens": 76000, "total_output_tokens": 4100, "context_window_size": 200000,
                              "current_usage": { "input_tokens": 10, "output_tokens": 50, "cache_creation_input_tokens": 900, "cache_read_input_tokens": 75000 },
                              "used_percentage": 38, "remaining_percentage": 62 },
          "exceeds_200k_tokens": false,
          "rate_limits": { "five_hour": { "used_percentage": 12, "resets_at": 1767225600 } }
        }
        """;

    [Test]
    public async Task StatuslinePayload()
    {
        // Act
        var u = AgentCli.ParseStatusline(Payload)!;
        string line = AgentCli.FormatLine(u);

        // Assert
        await Assert.That((u.Model, u.CostUsd!.Value, u.ContextPercent!.Value, u.ContextTokens!.Value, u.ContextSize!.Value)).IsEqualTo(("Opus 5.5", 0.4213, 38.0, 76000L, 200000L));
        await Assert.That(u.Cwd).IsEqualTo("/home/u/src/erikterm");
        await Assert.That(u.SessionId).IsEqualTo("3f2c9d1e-aaaa-bbbb-cccc-1234567890ab");
        await Assert.That(line).IsEqualTo("Opus 5.5 · $0.42 · 38% ctx");
    }

    [Test]
    public async Task OlderStatuslinePayloadComputesThePercentFromTheTokens()
    {
        // Act
        // Older payloads without used_percentage: computed from the tokens.
        var old = AgentCli.ParseStatusline("""{"model":{"display_name":"Sonnet"},"context_window":{"total_input_tokens":50000,"context_window_size":200000}}""")!;
        string line = AgentCli.FormatLine(old);

        // Assert
        await Assert.That(old.ContextPercent).IsEqualTo(25.0);
        await Assert.That(line).IsEqualTo("Sonnet · 25% ctx");
    }

    [Test]
    [Arguments("not json")]
    [Arguments("[1,2]")]
    public async Task StatuslinePayloadMustBeAJsonObject(string json)
    {
        // Act
        var u = AgentCli.ParseStatusline(json);

        // Assert
        await Assert.That(u).IsNull();
    }

    [Test]
    [Arguments(0.0712, "$0.07")]
    [Arguments(12.6, "$13")]
    [Arguments(null, null)]
    public async Task CostFormatting(double? usd, string? expected)
    {
        // Act
        string? cost = AgentUsage.Cost(usd);

        // Assert
        await Assert.That(cost).IsEqualTo(expected);
    }

    [Test]
    public async Task ContextIsCappedAtAHundredPercent()
    {
        // Act
        string? context = AgentUsage.Context(104);

        // Assert
        await Assert.That(context).IsEqualTo("100% ctx");
    }

    [Test]
    public async Task StatuslineWithoutErikterm()
    {
        // Arrange
        var stdout = new StringWriter();

        // Act
        int code = CliMain.Run(["statusline"], new StringReader(Payload), stdout, new StringWriter(), NoErikterm());
        string printed = stdout.ToString();

        // Assert
        await Assert.That(code).IsEqualTo(0); // never fails: the status line must still show
        await Assert.That(printed).IsEqualTo("Opus 5.5 · $0.42 · 38% ctx\n");
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [NotInParallel(Isolation.Processes)]
    public async Task StatuslinePassthrough()
    {
        // Arrange
        var inv = Parse("statusline", "--passthrough", "read -r line; echo \"custom ${#line}\"");
        var stdout = new StringWriter();

        // Act
        AgentCli.Statusline(inv, new StringReader("{\"a\":1}\n"), stdout, NoErikterm());
        string printed = stdout.ToString();

        // Assert
        await Assert.That(printed).IsEqualTo("custom 7\n");
    }

    [Test]
    [Arguments("""{"hook_event_name":"Notification","message":"Claude needs your permission to use Bash","notification_type":"permission_prompt"}""", "Claude needs your permission to use Bash")]
    [Arguments("""{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"ls"}}""", "Running Bash")]
    [Arguments("""{"hook_event_name":"UserPromptSubmit","prompt":"fix the parser\nplease"}""", "fix the parser please")]
    [Arguments("", null)]
    public async Task HookInputMessage(string json, string? expected)
    {
        // Act
        string? message = AgentCli.HookMessage(json);

        // Assert
        await Assert.That(message).IsEqualTo(expected);
    }

    [Test]
    [Arguments("""{"notification_type":"permission_prompt"}""", "permission")]
    [Arguments("", null)]
    public async Task HookInputKind(string json, string? expected)
    {
        // Act
        string? kind = AgentCli.HookKind(json);

        // Assert
        await Assert.That(kind).IsEqualTo(expected);
    }

    /// <summary>A response listing one Claude pane that has been waiting for 65 seconds.</summary>
    private static IpcResponse AgentsResponse() =>
        new() { Ok = true, Agents = [new AgentInfo { PaneId = 1, Agent = "claude-code", Name = "Claude", State = "waiting", SecondsInState = 65 }] };

    [Test]
    public async Task UsageRequestRoundTrip()
    {
        // Arrange
        var req = new IpcRequest { Command = "report-usage", PaneId = 2, Usage = new UsageInfo { Model = "Opus 5.5", CostUsd = 0.42, ContextPercent = 38 } };

        // Act
        string line = IpcJson.Encode(req);
        var back = IpcJson.DecodeRequest(line)!;

        // Assert
        await Assert.That(line).Contains("\"usage\":{\"model\":\"Opus 5.5\",\"cost_usd\":0.42,\"context_percent\":38}");
        await Assert.That(back.Usage!.CostUsd).IsEqualTo(0.42);
    }

    [Test]
    public async Task SetAgentStatusRequestDecodes()
    {
        // Act
        var status = IpcJson.DecodeRequest("""{"command":"set-agent-status","state":"waiting","message":"m","kind":"permission"}""")!;

        // Assert
        await Assert.That((status.State, status.Message, status.Kind)).IsEqualTo(("waiting", "m", "permission"));
    }

    [Test]
    public async Task AgentsResponseRoundTrip()
    {
        // Arrange
        var resp = AgentsResponse();

        // Act
        var r2 = IpcJson.DecodeResponse(IpcJson.Encode(resp))!;

        // Assert
        await Assert.That(r2.Agents![0].Agent).IsEqualTo("claude-code");
    }

    [Test]
    public async Task AgentsTableShowsTheNameAndTimeInState()
    {
        // Arrange
        var agents = IpcJson.DecodeResponse(IpcJson.Encode(AgentsResponse()))!.Agents!;

        // Act
        string table = AgentCli.FormatAgents(agents);

        // Assert
        await Assert.That(table).Contains("Claude");
        await Assert.That(table).Contains("1m");
    }

    [Test]
    public async Task PaneInfoCarriesTheAgentStatus()
    {
        // Arrange
        var pane = new PaneInfo { Agent = "codex", AgentStatus = "working" };

        // Act
        string json = System.Text.Json.JsonSerializer.Serialize(pane, IpcJson.Default.PaneInfo);

        // Assert
        await Assert.That(json).Contains("\"agent_status\":\"working\"");
    }
}
