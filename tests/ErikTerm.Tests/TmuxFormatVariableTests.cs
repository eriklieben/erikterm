using ErikTerm.App.Ipc;
using ErikTerm.App.Tmux;

namespace ErikTerm.Tests;

/// <summary>
/// The tmux format variables one by one, and the target forms <see cref="TmuxSessionTests"/> doesn't reach: what each
/// answers for the sample session (<see cref="TmuxSessionTests.Sample"/>, seen from pane %2).
/// </summary>
public class TmuxFormatVariableTests
{
    private readonly TmuxSession _session = new(TmuxSessionTests.Sample(), callerPane: 2) { Socket = "/run/x/erikterm-9.sock", ServerPid = 4242 };

    private string Expand(string format, int paneId) => _session.Expand(format, _session.Panes.First(p => p.PaneId == paneId));

    [Test]
    // %3 sits bottom-right of tab 10 (80x30): its edges are the window's bottom and right ones.
    [Arguments("#{pane_bottom},#{pane_right} #{pane_at_top}#{pane_at_left}#{pane_at_bottom}#{pane_at_right}", 3, "29,79 0011")]
    // %1 fills the left column: top, left and bottom, but not right.
    [Arguments("#{pane_bottom},#{pane_right} #{pane_at_top}#{pane_at_left}#{pane_at_bottom}#{pane_at_right}", 1, "29,39 1110")]
    // %2 is top-right: not at the bottom.
    [Arguments("#{pane_at_top}#{pane_at_left}#{pane_at_bottom}#{pane_at_right}", 2, "1001")]
    public async Task PaneEdgesFollowItsPlaceInTheTab(string format, int paneId, string expected)
    {
        // Act
        string text = Expand(format, paneId);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments("pane_dead")]
    [Arguments("pane_in_mode")]
    [Arguments("pane_synchronized")]
    [Arguments("pane_input_off")]
    [Arguments("pane_marked")]
    [Arguments("pane_pipe")]
    [Arguments("pane_last")]
    [Arguments("history_bytes")]
    [Arguments("alternate_on")]
    [Arguments("window_bigger")]
    [Arguments("window_offset_x")]
    [Arguments("window_offset_y")]
    [Arguments("window_linked")]
    [Arguments("window_silence_flag")]
    [Arguments("window_bell_flag")]
    [Arguments("window_activity_flag")]
    [Arguments("session_many_attached")]
    [Arguments("session_grouped")]
    [Arguments("client_control_mode")]
    [Arguments("client_readonly")]
    [Arguments("client_prefix")]
    public async Task StatesErikTermDoesNotHaveAnswerOff(string name)
    {
        // Act
        string text = Expand("#{" + name + "}", 2);

        // Assert
        await Assert.That(text).IsEqualTo("0");
    }

    [Test]
    [Arguments("pane_mode")]
    [Arguments("pane_start_command")]
    [Arguments("window_layout")]
    [Arguments("window_visible_layout")]
    [Arguments("session_path")]
    public async Task ValuesErikTermDoesNotTrackAreEmpty(string name)
    {
        // Act
        string text = Expand("<#{" + name + "}>", 2);

        // Assert
        await Assert.That(text).IsEqualTo("<>");
    }

    [Test]
    public async Task CursorAndHistoryComeFromThePane()
    {
        // Arrange
        var pane = _session.Panes[1];
        pane.CursorX = 7;
        pane.CursorY = 3;
        pane.HistorySize = 120;
        pane.HistoryLimit = 5000;

        // Act
        string text = _session.Expand("#{cursor_x},#{cursor_y} #{history_size}/#{history_limit}", pane);

        // Assert
        await Assert.That(text).IsEqualTo("7,3 120/5000");
    }

    [Test]
    public async Task PaneTitleFallsBackToTheTerminalTitle()
    {
        // Act: %3 has no select-pane -T title
        string text = Expand("#{pane_title}", 3);

        // Assert
        await Assert.That(text).IsEqualTo("cat");
    }

    [Test]
    public async Task PaneWithoutTtyOrProgramAnswersEmpty()
    {
        // Act: %2 has no tty, foreground program or directory
        string text = Expand("<#{pane_tty}|#{pane_current_command}|#{pane_current_path}|#{pane_path}>", 2);

        // Assert
        await Assert.That(text).IsEqualTo("<|||>");
    }

    [Test]
    public async Task CurrentPathPrefersTheForegroundProgramsDirectory()
    {
        // Arrange
        var pane = _session.Panes[1];
        pane.Cwd = "file:///home/shell";
        pane.CurrentPath = "/home/program";

        // Act
        string text = _session.Expand("#{pane_current_path} #{pane_path}", pane);

        // Assert
        await Assert.That(text).IsEqualTo("/home/program /home/shell");
    }

    [Test]
    public async Task CurrentPathFallsBackToTheShellsDirectory()
    {
        // Arrange
        var pane = _session.Panes[1];
        pane.Cwd = "file:///home/shell";

        // Act
        string text = _session.Expand("#{pane_current_path}", pane);

        // Assert
        await Assert.That(text).IsEqualTo("/home/shell");
    }

    [Test]
    [Arguments(2, "*")]   // tab 10 holds the focused pane
    [Arguments(4, "")]    // tab 20 is in the background
    public async Task WindowFlagsMarkTheCurrentWindow(int paneId, string expected)
    {
        // Act
        string text = Expand("#{window_flags}", paneId);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task WindowFlagsMarkAZoomedWindow()
    {
        // Arrange
        var panes = TmuxSessionTests.Sample();
        panes[3].IsZoomed = true;
        var session = new TmuxSession(panes, callerPane: 2);

        // Act
        string text = session.Expand("#{window_flags} #{window_zoomed_flag}", session.Panes[3]);

        // Assert
        await Assert.That(text).IsEqualTo("Z 1");
    }

    [Test]
    public async Task WindowNameFallsBackToTheTitleWithoutAForegroundProgram()
    {
        // Arrange: one pane, no tab title and no foreground program
        var session = new TmuxSession([new PaneInfo { TabId = 1, PaneId = 9, IsActive = true, Title = "vim notes" }], callerPane: null);

        // Act
        string text = session.Expand("#{window_name}", session.Current);

        // Assert
        await Assert.That(text).IsEqualTo("vim notes");
    }

    [Test]
    public async Task SessionVariablesDescribeTheOneSession()
    {
        // Act
        string text = Expand("#{session_id} #{session_windows} #{session_attached} #{client_session}", 2);

        // Assert
        await Assert.That(text).IsEqualTo("$0 2 1 erikterm");
    }

    [Test]
    public async Task ClientWithoutTtyIsNamedErikTerm()
    {
        // Act
        string text = Expand("<#{client_tty}> #{client_name} #{client_termname} #{client_utf8} #{client_width}x#{client_height}", 2);

        // Assert
        await Assert.That(text).IsEqualTo("<> erikterm xterm-256color 1 80x30");
    }

    [Test]
    public async Task ClientIsNamedAfterTheCallersTty()
    {
        // Arrange
        var panes = TmuxSessionTests.Sample();
        panes[1].TtyName = "/dev/pts/4";
        var session = new TmuxSession(panes, callerPane: 2);

        // Act
        string text = session.Expand("#{client_tty} #{client_name}", session.Current);

        // Assert
        await Assert.That(text).IsEqualTo("/dev/pts/4 /dev/pts/4");
    }

    [Test]
    public async Task ClientFeaturesIncludeSynchronizedOutput()
    {
        // Act
        string features = Expand("#{client_termfeatures}", 2);

        // Assert
        await Assert.That(features.Split(',')).Contains("sync");
    }

    [Test]
    public async Task ServerVariablesDescribeThisErikTerm()
    {
        // Act
        string text = Expand("#{version} #{pid} #{socket_path}", 2);

        // Assert
        await Assert.That(text).IsEqualTo("3.5 4242 /run/x/erikterm-9.sock");
    }

    [Test]
    public async Task HostIsTheMachineAndItsShortName()
    {
        // Act
        string text = Expand("#{host}|#{host_short}", 2);

        // Assert
        await Assert.That(text).IsEqualTo(Environment.MachineName + "|" + Environment.MachineName.Split('.')[0]);
    }

    [Test]
    [Arguments("mouse", "1")]               // on
    [Arguments("renumber-windows", "0")]    // off
    [Arguments("history-limit", "10000")]   // other values as they are
    [Arguments("no-such-option", "")]
    public async Task OptionsAreNumericWhenTheyAreSwitches(string name, string expected)
    {
        // Act
        string text = Expand("#{" + name + "}", 2);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }
}

/// <summary>Target forms: special window and pane tokens, ids and names, and the errors for what isn't there.</summary>
public class TmuxTargetFormTests
{
    private readonly TmuxSession _session = new(TmuxSessionTests.Sample(), callerPane: 2);

    [Test]
    [Arguments(":{start}", 10)]
    [Arguments(":^", 10)]
    [Arguments(":{end}", 20)]
    [Arguments(":$", 20)]
    [Arguments(":{next}", 20)]
    [Arguments(":+", 20)]
    [Arguments(":{previous}", 20)]   // wraps around from the first window
    [Arguments(":-", 20)]
    [Arguments(":1", 20)]
    [Arguments(":logs", 20)]
    [Arguments(":lo", 20)]           // a name prefix
    [Arguments("erikterm:", 10)]     // the session alone: its current window
    [Arguments("@20", 20)]
    [Arguments("%4", 20)]            // a pane picks its window
    [Arguments("1", 20)]             // a bare number is a window index
    [Arguments("logs", 20)]          // a bare name
    [Arguments("erikterm", 10)]      // a bare session name: the current window
    public async Task WindowTargets(string target, int tabId)
    {
        // Act
        var window = _session.ResolveWindow(target);

        // Assert
        await Assert.That(window.TabId).IsEqualTo(tabId);
    }

    [Test]
    [Arguments(":7")]
    [Arguments(":nothing")]
    public async Task MissingWindowsInOurSessionAreErrors(string target)
    {
        // Act
        var act = () => _session.ResolveWindow(target);

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxTargetException>();
    }

    [Test]
    [Arguments("%99")]
    [Arguments("@99")]
    [Arguments("nothing")]
    [Arguments("other:1")]
    public async Task WindowTargetsElsewhereAreNotOurs(string target)
    {
        // Act
        var act = () => _session.ResolveWindow(target);

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxNotOursException>();
    }

    [Test]
    [Arguments(".{top}", 1)]
    [Arguments(".{left}", 1)]
    [Arguments(".{top-left}", 1)]
    [Arguments(".{bottom}", 3)]
    [Arguments(".{right}", 3)]
    [Arguments(".{bottom-right}", 3)]
    [Arguments(".{last}", 3)]
    [Arguments(".{next}", 2)]        // after the active pane, %1
    [Arguments(".+", 2)]
    [Arguments(".{previous}", 3)]    // wraps around
    [Arguments(".-", 3)]
    [Arguments(".2", 3)]
    [Arguments(":0.%3", 3)]
    [Arguments("@20.0", 4)]
    [Arguments("1", 2)]              // a bare number is a pane index
    [Arguments("erikterm", 2)]       // a bare session name: the current pane
    [Arguments("logs", 4)]           // a bare window name: its active pane
    [Arguments(":1", 4)]             // a window: its active pane
    public async Task PaneTargets(string target, int paneId)
    {
        // Act
        var pane = _session.ResolvePane(target);

        // Assert
        await Assert.That(pane.PaneId).IsEqualTo(paneId);
    }

    [Test]
    [Arguments(".5")]
    [Arguments(".{middle}")]
    [Arguments(":0.%4")]             // %4 exists, but in another window
    [Arguments(":0.123456789")]      // too long to be an index
    public async Task MissingPanesInOurSessionAreErrors(string target)
    {
        // Act
        var act = () => _session.ResolvePane(target);

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxTargetException>();
    }

    [Test]
    [Arguments("%")]
    [Arguments("%x")]
    [Arguments("nothing")]
    [Arguments("other:0.1")]
    public async Task PaneTargetsElsewhereAreNotOurs(string target)
    {
        // Act
        var act = () => _session.ResolvePane(target);

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxNotOursException>();
    }

    [Test]
    [Arguments("@10")]
    [Arguments(":1")]
    [Arguments("erik")]      // a prefix of the session name
    public async Task SessionTargetsThatNameOurSession(string target)
    {
        // Act
        var act = () => _session.ResolveSession(target);

        // Assert
        await Assert.That(act).ThrowsNothing();
    }

    [Test]
    public async Task SessionTargetForAMissingWindowIdIsNotOurs()
    {
        // Act
        var act = () => _session.ResolveSession("@99");

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxNotOursException>();
    }

    [Test]
    public async Task NoPanesIsNoSession()
    {
        // Act
        var act = () => new TmuxSession([], callerPane: null);

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxTargetException>();
    }

    [Test]
    public async Task UnknownCallerFallsBackToTheFocusedPane()
    {
        // Act
        var session = new TmuxSession(TmuxSessionTests.Sample(), callerPane: 77);

        // Assert
        await Assert.That(session.Current.PaneId).IsEqualTo(1);
    }

    [Test]
    public async Task WithoutCallerOrFocusTheFirstPaneIsCurrent()
    {
        // Arrange
        var panes = TmuxSessionTests.Sample();
        panes[0].IsFocused = false;

        // Act
        var session = new TmuxSession(panes, callerPane: null);

        // Assert
        await Assert.That(session.Current.PaneId).IsEqualTo(1);
    }

    [Test]
    public async Task WindowWithoutActivePaneTakesItsFirst()
    {
        // Arrange
        var panes = TmuxSessionTests.Sample();
        panes[0].IsActive = false;
        var session = new TmuxSession(panes, callerPane: 2);

        // Act
        var active = session.CurrentWindow.Active;

        // Assert
        await Assert.That(active.PaneId).IsEqualTo(1);
    }
}

/// <summary>send-keys names with modifiers that <see cref="TmuxKeysTests"/> doesn't try.</summary>
public class TmuxKeyModifierTests
{
    [Test]
    [Arguments("M-Enter", "\e\r")]
    [Arguments("M-Space", "\e ")]
    [Arguments("C-BSpace", "\b")]
    [Arguments("M-BSpace", "\e\x7f")]
    [Arguments("M-Escape", "\e\e")]
    [Arguments("S-Tab", "\e[Z")]
    [Arguments("M-Tab", "\e\t")]
    [Arguments("C-BTab", "\e[Z")]
    [Arguments("C-M-S-Up", "\e[1;8A")]
    [Arguments("S-F5", "\e[15;2~")]
    [Arguments("s-c", "C")]          // shift on a character is its capital
    [Arguments("C-S-a", "\x01")]
    public async Task ModifiedNamedKeys(string key, string bytes)
    {
        // Act
        string sent = TmuxKeys.Translate(key);

        // Assert
        await Assert.That(sent).IsEqualTo(bytes);
    }

    [Test]
    [Arguments("C-@", "\0")]
    [Arguments("C-2", "\0")]
    [Arguments("C-[", "\e")]
    [Arguments("C-3", "\e")]
    [Arguments("C-\\", "\x1c")]
    [Arguments("C-4", "\x1c")]
    [Arguments("C-]", "\x1d")]
    [Arguments("C-5", "\x1d")]
    [Arguments("C-^", "\x1e")]
    [Arguments("C-6", "\x1e")]
    [Arguments("C-~", "\x1e")]
    [Arguments("C-_", "\x1f")]
    [Arguments("C-7", "\x1f")]
    [Arguments("C-/", "\x1f")]
    [Arguments("C-?", "\x7f")]
    [Arguments("C-8", "\x7f")]
    [Arguments("C-A", "\x01")]
    public async Task ControlPunctuationFollowsXterm(string key, string bytes)
    {
        // Act
        string sent = TmuxKeys.Translate(key);

        // Assert
        await Assert.That(sent).IsEqualTo(bytes);
    }

    [Test]
    [Arguments("C-1")]
    [Arguments("C-!")]
    public async Task ControlWithoutAControlCodeIsText(string key)
    {
        // Act
        bool isKey = TmuxKeys.TryTranslateKey(key, appCursor: false, out _);

        // Assert
        await Assert.That(isKey).IsFalse();
    }

    [Test]
    public async Task EmptyArgumentIsNoKey()
    {
        // Act
        bool isKey = TmuxKeys.TryTranslateKey("", appCursor: false, out _);

        // Assert
        await Assert.That(isKey).IsFalse();
    }

    [Test]
    [Arguments("Home", "\eOH")]
    [Arguments("End", "\eOF")]
    [Arguments("Down", "\eOB")]
    [Arguments("PageUp", "\e[5~")]   // not a cursor key: the same in either mode
    [Arguments("Enter", "\r")]
    public async Task ApplicationCursorModeChangesOnlyCursorKeys(string key, string bytes)
    {
        // Act
        string sent = TmuxKeys.Translate(key, appCursor: true);

        // Assert
        await Assert.That(sent).IsEqualTo(bytes);
    }

    [Test]
    public async Task HexThatIsNoByteIsAUsageError()
    {
        // Act
        var act = () => TmuxKeys.FromHex(["61", "zz"]);

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxUsageException>();
    }
}
