using System.Text;
using ErikTerm.App;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>Shift+Enter for coding agents (SendAgentNewline) and the legacy Enter encodings around it.</summary>
public class AgentNewlineTests
{
    private const ushort Shift = 0x0001, Ctrl = 0x0040;

    [Test]
    public async Task ClaudeCodeGetsEscCrWhileTheKittyProtocolIsOff()
    {
        // Act
        string? seq = KeyEncoder.ShiftEnterForAgent("claude-code", KittyKeyboardModes.None);

        // Assert
        await Assert.That(seq).IsEqualTo("\e\r");
    }

    [Test]
    [Arguments(KittyKeyboardModes.Disambiguate)]
    [Arguments(KittyKeyboardModes.Disambiguate | KittyKeyboardModes.ReportAlternateKeys)] // what Claude Code pushes (CSI > 5 u)
    [Arguments(KittyKeyboardModes.ReportAllKeys)]
    public async Task WithTheKittyProtocolOnTheKeyIsEncodedAsUsual(KittyKeyboardModes flags)
    {
        // Act
        string? seq = KeyEncoder.ShiftEnterForAgent("claude-code", flags);

        // Assert
        await Assert.That(seq).IsNull();
    }

    [Test]
    [Arguments(null)]          // a shell
    [Arguments("codex")]       // an agent without the ESC CR convention
    [Arguments("gemini-cli")]
    public async Task OtherProgramsGetNothingSpecial(string? agentId)
    {
        // Act
        string? seq = KeyEncoder.ShiftEnterForAgent(agentId, KittyKeyboardModes.None);

        // Assert
        await Assert.That(seq).IsNull();
    }

    [Test]
    [Arguments((ushort)0, "\r")]
    [Arguments(Shift, "\r")]       // legacy encoding can't tell Shift+Enter from Enter: why agents need ESC CR
    [Arguments(Ctrl, "\r")]
    public async Task LegacyEnterIsACarriageReturn(ushort mod, string expected)
    {
        // Act
        string? seq = KeyEncoder.Encode(Sdl.K_RETURN, mod, applicationCursor: false);

        // Assert
        await Assert.That(seq).IsEqualTo(expected);
    }

    [Test]
    public async Task KittyShiftEnterIsCsi13Shift()
    {
        // Act
        string? seq = KeyEncoder.Encode(Sdl.K_RETURN, Shift, applicationCursor: false, KittyKeyboardModes.Disambiguate);

        // Assert
        await Assert.That(seq).IsEqualTo("\e[13;2u");
    }

    [Test]
    public async Task ShiftEnterIsBoundToSendAgentNewlineByDefault()
    {
        // Arrange
        KeyChord.TryParse("SHIFT+enter", out var chord, out _);

        // Act
        var binding = SettingsLoader.Parse("").Settings.EffectiveKeys().SingleOrDefault(b => b.Chord == chord);

        // Assert
        await Assert.That(binding.Action.Name).IsEqualTo("SendAgentNewline");
    }

    [Test]
    public async Task TheBindingCanBeTurnedOffInKeys()
    {
        // Arrange
        const string toml = """
            [keys]
            "SHIFT+enter" = "None"
            """;
        KeyChord.TryParse("SHIFT+enter", out var chord, out _);

        // Act
        var r = SettingsLoader.Parse(toml);
        bool bound = r.Settings.EffectiveKeys().Any(b => b.Chord == chord);

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(bound).IsFalse();
    }
}

/// <summary>window_close_confirmation: which panes make a close ask, and what the prompt says.</summary>
internal class CloseConfirmationTests
{
    private static readonly IReadOnlyList<string> Skip = Settings.DefaultSkipCloseConfirmation;

    [Test]
    [Arguments("zsh")]
    [Arguments("bash")]
    [Arguments("fish")]
    [Arguments("-zsh")]  // a login shell's argv[0]
    [Arguments("tmux")]
    [Arguments("pwsh")]  // "pwsh.exe" in the list
    public async Task ShellsAtTheirPromptDontAsk(string foreground)
    {
        // Act
        string? name = CloseConfirmation.RunningProgram(WindowCloseConfirmation.AlwaysPrompt, foreground, null, Skip);

        // Assert
        await Assert.That(name).IsNull();
    }

    [Test]
    [Arguments("nvim")]
    [Arguments("cargo")]
    [Arguments("htop")]
    public async Task OtherProgramsAsk(string foreground)
    {
        // Act
        string? name = CloseConfirmation.RunningProgram(WindowCloseConfirmation.AlwaysPrompt, foreground, null, Skip);

        // Assert
        await Assert.That(name).IsEqualTo(foreground);
    }

    [Test]
    public async Task AnIdleCodingAgentAsksUnderItsName()
    {
        // Arrange
        // Claude Code's native binary is named after its version, so its comm is "2.1.319".

        // Act
        string? name = CloseConfirmation.RunningProgram(WindowCloseConfirmation.AlwaysPrompt, "2.1.319", "Claude", Skip);

        // Assert
        await Assert.That(name).IsEqualTo("Claude");
    }

    [Test]
    public async Task AStaleAgentRecordUnderAShellDoesNotAsk()
    {
        // Act
        string? name = CloseConfirmation.RunningProgram(WindowCloseConfirmation.AlwaysPrompt, "zsh", "Claude", Skip);

        // Assert
        await Assert.That(name).IsNull();
    }

    [Test]
    public async Task AnUnknownForegroundWithoutAnAgentDoesNotAsk()
    {
        // Act
        string? name = CloseConfirmation.RunningProgram(WindowCloseConfirmation.AlwaysPrompt, null, null, Skip);

        // Assert
        await Assert.That(name).IsNull();
    }

    [Test]
    public async Task NeverPromptNeverAsks()
    {
        // Act
        string? name = CloseConfirmation.RunningProgram(WindowCloseConfirmation.NeverPrompt, "nvim", "Claude", Skip);

        // Assert
        await Assert.That(name).IsNull();
    }

    [Test]
    public async Task TheSkipListIsTheUsers()
    {
        // Arrange
        string[] skip = ["zsh", "nvim"];

        // Act
        string? nvim = CloseConfirmation.RunningProgram(WindowCloseConfirmation.AlwaysPrompt, "nvim", null, skip);
        string? bash = CloseConfirmation.RunningProgram(WindowCloseConfirmation.AlwaysPrompt, "bash", null, skip);

        // Assert
        await Assert.That(nvim).IsNull();
        await Assert.That(bash).IsEqualTo("bash");
    }

    [Test]
    public async Task ANameCutAt15BytesStillMatches()
    {
        // Arrange
        string[] skip = ["my-very-long-shell-name"];

        // Act
        bool skipped = CloseConfirmation.IsSkipped("my-very-long-sh", skip);

        // Assert
        await Assert.That(skipped).IsTrue();
    }

    [Test]
    [Arguments(new[] { "nvim" }, "nvim")]
    [Arguments(new[] { "nvim", "Claude" }, "nvim and Claude")]
    [Arguments(new[] { "nvim", "Claude", "cargo" }, "nvim, Claude and cargo")]
    [Arguments(new[] { "nvim", "Claude", "cargo", "htop", "less" }, "nvim, Claude, cargo and 2 more")]
    [Arguments(new[] { "nvim", "Claude", "nvim" }, "nvim (2) and Claude")]
    public async Task NamesListEachProgramOnce(string[] names, string expected)
    {
        // Act
        string text = CloseConfirmation.Names(names);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments(CloseScope.Pane, new[] { "nvim" }, "Close this pane? nvim is still running")]
    [Arguments(CloseScope.Tab, new[] { "nvim", "Claude" }, "Close this tab? nvim and Claude are still running")]
    [Arguments(CloseScope.Window, new[] { "cargo" }, "Close this window? cargo is still running")]
    [Arguments(CloseScope.Quit, new[] { "nvim", "nvim" }, "Quit erikterm? nvim (2) are still running")]
    public async Task TheQuestionNamesWhatRuns(CloseScope scope, string[] names, string expected)
    {
        // Act
        string text = CloseConfirmation.Text(scope, names);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task SettingsDefaultToWeztermsBehaviour()
    {
        // Act
        var s = SettingsLoader.Parse("").Settings;

        // Assert
        await Assert.That(s.WindowCloseConfirmation).IsEqualTo(WindowCloseConfirmation.AlwaysPrompt);
        await Assert.That(s.SkipCloseConfirmationForProcessesNamed).Contains("zsh");
    }

    [Test]
    public async Task SettingsAreRead()
    {
        // Arrange
        const string toml = """
            window_close_confirmation = "NeverPrompt"
            skip_close_confirmation_for_processes_named = ["zsh", "nvim"]
            """;

        // Act
        var r = SettingsLoader.Parse(toml);

        // Assert
        await Assert.That(r.Diagnostics).IsEmpty();
        await Assert.That(r.Settings.WindowCloseConfirmation).IsEqualTo(WindowCloseConfirmation.NeverPrompt);
        await Assert.That(r.Settings.SkipCloseConfirmationForProcessesNamed).IsEquivalentTo(["zsh", "nvim"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnUnknownPolicyIsReported()
    {
        // Act
        var r = SettingsLoader.Parse("window_close_confirmation = \"Sometimes\"");

        // Assert
        await Assert.That(r.Diagnostics.Single()).Contains("AlwaysPrompt");
        await Assert.That(r.Settings.WindowCloseConfirmation).IsEqualTo(WindowCloseConfirmation.AlwaysPrompt);
    }
}

/// <summary>`erikterm --version` / `erikterm cli --version`.</summary>
public class VersionTextTests
{
    [Test]
    public async Task VersionCommitAndBuildDate()
    {
        // Act
        string text = AboutFormat.VersionText("0.1.0", "354fb54", false, "2026-11-07 21:40 UTC");

        // Assert
        await Assert.That(text).IsEqualTo("erikterm 0.1.0 (354fb54), built 2026-11-07 21:40 UTC");
    }

    [Test]
    public async Task AModifiedCheckoutSaysSo()
    {
        // Act
        string text = AboutFormat.VersionText("0.1.0", "354fb54", true, null);

        // Assert
        await Assert.That(text).IsEqualTo("erikterm 0.1.0 (354fb54, modified)");
    }

    [Test]
    public async Task WithoutACommitJustTheVersion()
    {
        // Act
        string text = AboutFormat.VersionText("0.1.0", null, false, null);

        // Assert
        await Assert.That(text).IsEqualTo("erikterm 0.1.0");
    }
}

/// <summary>ERIKTERM_LOG_UNHANDLED: what the terminal logs when it ignores a sequence, and how often.</summary>
public class UnhandledLogTests
{
    private static readonly DateTime Stamp = new(2026, 11, 7, 22, 14, 20, 123);

    private sealed class Clock { public long Ms; }

    private static (Terminal Term, StringWriter Out, Clock Clock) Setup()
    {
        var output = new StringWriter();
        var clock = new Clock();
        var term = new Terminal(20, 4) { Unhandled = new UnhandledLog(output, () => clock.Ms, () => Stamp) };
        return (term, output, clock);
    }

    private static string[] Lines(StringWriter w) => w.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Test]
    [Arguments("\e[?1015h", "CSI ?1015h")]                 // an unknown DEC mode (urxvt mouse)
    [Arguments("\e[?1000;1015h", "CSI ?1015h")]            // only the unknown one of several
    [Arguments("\e[?25n", "CSI ?25n")]
    [Arguments("\e[>4;2m", "CSI >4;2m")]                   // modifyOtherKeys
    [Arguments("\e[3 z", "CSI 3 z")]
    [Arguments("\e[15n", "CSI 15n")]
    [Arguments("\e]5;0;?\e\\", "OSC 5;0;?")]
    [Arguments("\e]105\a", "OSC 105")]
    [Arguments("\eP=1s\e\\", "DCS =1s")]                   // the old DCS synchronized-update form
    [Arguments("\e_Zhello\e\\", "APC Zhello")]
    [Arguments("\e#3", "ESC #3")]
    public async Task IgnoredSequencesAreLogged(string input, string expected)
    {
        // Arrange
        var (term, output, _) = Setup();

        // Act
        term.Feed(Encoding.UTF8.GetBytes(input));

        // Assert
        await Assert.That(Lines(output)).IsEquivalentTo([$"2026-11-07T22:14:20.123 [{Environment.ProcessId}] unhandled {expected}"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("hello\r\n")]
    [Arguments("\e[1;31mred\e[0m")]
    [Arguments("\e[?2004h\e[?1049h\e[2J\e[H")]
    [Arguments("\e]0;title\a\e]7;file:///tmp\a")]
    [Arguments("\e[?u\e[>1u\e[<u")]
    [Arguments("\e[2 q\e7\e8\e(B")]
    public async Task HandledSequencesAreNotLogged(string input)
    {
        // Arrange
        var (term, output, _) = Setup();

        // Act
        term.Feed(Encoding.UTF8.GetBytes(input));

        // Assert
        await Assert.That(output.ToString()).IsEmpty();
    }

    [Test]
    public async Task EachDistinctSequenceIsLoggedOnce()
    {
        // Arrange
        var (term, output, _) = Setup();

        // Act
        term.Feed(Encoding.UTF8.GetBytes("\e[?1015h\e[?1015h\e[?1015l\e[?1015h"));

        // Assert
        await Assert.That(Lines(output).Length).IsEqualTo(2); // ?1015h and ?1015l
    }

    [Test]
    public async Task ControlBytesAndLongPayloadsAreMadePrintable()
    {
        // Arrange
        string payload = new('x', UnhandledLog.MaxPayloadChars + 50);

        // Act
        string text = UnhandledLog.FormatString("OSC", Encoding.UTF8.GetBytes("1\x01" + payload));

        // Assert
        await Assert.That(text).IsEqualTo("OSC 1\\x01" + new string('x', UnhandledLog.MaxPayloadChars - 5) + "…");
    }

    [Test]
    public async Task MoreThanTheLimitPerSecondIsCountedAndReported()
    {
        // Arrange
        var (term, output, clock) = Setup();
        int extra = 5;

        // Act
        for (int i = 0; i < UnhandledLog.MaxLinesPerSecond + extra; i++) term.Feed(Encoding.ASCII.GetBytes($"\e[?{3000 + i}h"));
        int inFirstSecond = Lines(output).Length;
        clock.Ms += 1000;
        term.Feed("\e[?4000h"u8);
        var lines = Lines(output);

        // Assert
        await Assert.That(inFirstSecond).IsEqualTo(UnhandledLog.MaxLinesPerSecond);
        await Assert.That(lines[UnhandledLog.MaxLinesPerSecond]).EndsWith($"… {extra} more unhandled sequences not logged (over {UnhandledLog.MaxLinesPerSecond}/s)");
        await Assert.That(lines[^1]).EndsWith("unhandled CSI ?4000h");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("0")]
    public async Task OffUnlessTheVariableIsSet(string? value)
    {
        // Act
        var log = UnhandledLog.FromEnvironment(value);

        // Assert
        await Assert.That(log).IsNull();
    }

    /// <summary>The log's lines while the log is still open (File.ReadAllLines refuses a file open for writing on Windows).</summary>
    private static string[] ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Test]
    public async Task TheLogFileCanBeDeletedWhileItIsOpen()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), $"erikterm-unhandled-{Guid.NewGuid():N}.log");
        var term = new Terminal(20, 4) { Unhandled = UnhandledLog.FromEnvironment(path) };
        term.Feed("\e[?1015h"u8);

        // Act
        File.Delete(path);
        bool exists = File.Exists(path);

        // Assert
        await Assert.That(exists).IsFalse();
    }

    [Test]
    public async Task APathAppendsToThatFile()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), $"erikterm-unhandled-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, "earlier\n");
        var term = new Terminal(20, 4) { Unhandled = UnhandledLog.FromEnvironment(path) };

        // Act
        term.Feed("\e[?1015h"u8);
        string[] lines = ReadSharedLines(path);
        File.Delete(path);

        // Assert
        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[1]).EndsWith("unhandled CSI ?1015h");
    }
}
