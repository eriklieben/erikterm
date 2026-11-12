using System.Text;
using ErikTerm.App.Ipc;
using ErikTerm.App.Tmux;
using ErikTerm.Core;
using ErikTerm.Core.Images;
using ErikTerm.Ui;

namespace ErikTerm.Tests;

public class TmuxParserTests
{
    /// <summary>A command line as tmux gets it; tests write it space-separated (no argument here contains a space).</summary>
    private static string[] Argv(string line) => line.Length == 0 ? [] : line.Split(' ');

    [Test]
    public async Task ClaudeCodesFirstTeammateSplit()
    {
        // Act
        // createTeammatePaneWithLeader: tmux -S <socket> split-window -d -t %1 -h -l 70% -P -F #{pane_id} -- cat
        var inv = TmuxParser.Parse(["-S", "/run/x/erikterm-9.sock", "split-window", "-d", "-t", "%1", "-h", "-l", "70%", "-P", "-F", "#{pane_id}", "--", "cat"]);

        // Assert
        await Assert.That(inv.SocketPath).IsEqualTo("/run/x/erikterm-9.sock");
        var c = await Assert.That(inv.Commands).HasSingleItem();
        await Assert.That(c.Name).IsEqualTo("split-window");
        await Assert.That(c.Has('d') && c.Has('h') && c.Has('P')).IsTrue();
        await Assert.That((c.Target, c.Value('l'), c.Value('F'))).IsEqualTo(("%1", "70%", "#{pane_id}"));
        await Assert.That(c.Args).IsEquivalentTo(["cat"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ClusteredAndAttachedFlags()
    {
        // Act
        var commands = TmuxParser.Parse(["splitw", "-dhP", "-t%3", "-c/tmp", "htop", "-d", "5"]).Commands;

        // Assert
        var c = await Assert.That(commands).HasSingleItem();
        await Assert.That(c.Name).IsEqualTo("split-window");
        await Assert.That(c.Flags).IsEquivalentTo(['d', 'h', 'P'], CollectionOrdering.Matching);
        await Assert.That((c.Target, c.Value('c'))).IsEqualTo(("%3", "/tmp"));
        await Assert.That(c.Args).IsEquivalentTo(["htop", "-d", "5"], CollectionOrdering.Matching); // parsing stops at the first argument
    }

    public static IEnumerable<Func<string[]>> ClaudeCodeCommands() =>
    [
        () => ["-V"],
        () => ["display-message", "-p", "#{pane_id}"],
        () => ["display-message", "-t", "%1", "-p", "#{window_id}"],
        () => ["display-message", "-p", "-t", "%1", "#{session_name}:#{window_id}.#{pane_id}"],
        () => ["display-message", "-p", "-t", "%1", "#{client_termtype}\t#{allow-passthrough}\t#{focus-events}\t#{pane_top}\t#{pane_left}\t#{status}\t#{status-position}\t#{window_bigger}"],
        () => ["display-message", "-p", "#{client_control_mode}"],
        () => ["list-panes", "-t", "@1", "-F", "#{pane_id}"],
        () => ["split-window", "-d", "-t", "%2", "-v", "-P", "-F", "#{pane_id}", "--", "cat"],
        () => ["set-option", "-p", "-t", "%2", "window-style", "bg=default,fg=blue"],
        () => ["set-option", "-p", "-t", "%2", "pane-border-style", "fg=blue"],
        () => ["set-option", "-p", "-t", "%2", "pane-active-border-style", "fg=blue"],
        () => ["select-pane", "-t", "%2", "-T", "researcher"],
        () => ["set-option", "-p", "-t", "%2", "pane-border-format", "#[fg=blue,bold] #{pane_title} #[default]"],
        () => ["set-option", "-w", "-t", "@1", "pane-border-status", "top"],
        () => ["select-layout", "-t", "@1", "main-vertical"],
        () => ["resize-pane", "-t", "%1", "-x", "30%"],
        () => ["set-option", "-p", "-t", "%2", "remain-on-exit", "failed"],
        () => ["respawn-pane", "-k", "-t", "%2", "--", "cd '/x' && env A=1 claude --agent-id a@t"],
        () => ["kill-pane", "-t", "%2"],
        () => ["has-session", "-t", "claude-swarm"],
        () => ["new-session", "-d", "-s", "claude-swarm"],
        () => ["new-window", "-t", "claude-swarm", "-n", "teammate-x", "-P", "-F", "#{pane_id}", "--", "cat"],
        () => ["show", "-Av", "mouse"],
        () => ["show", "-gv", "focus-events"],
        () => ["show-options", "-g", "prefix"],
        () => ["show-environment", "-g"],
        () => ["set", "-as", "terminal-features", ",*:RGB"],
        () => ["load-buffer", "-w", "-"],
        () => ["switch-client", "-t", "repo_feature"],
        () => ["new-session", "-d", "-s", "app", "-x", "120", "-y", "40", "./myapp"],
        () => ["send-keys", "-t", "app", "s"],
        () => ["capture-pane", "-t", "app", "-p"],
        () => ["kill-session", "-t", "app"],
    ];

    [Test]
    [MethodDataSource(nameof(ClaudeCodeCommands))]
    public async Task EveryClaudeCodeCommandParses(string[] argv)
    {
        // Act
        var inv = TmuxParser.Parse(argv);

        // Assert
        await Assert.That(inv.UnknownCommand).IsNull();
    }

    [Test]
    [Arguments("display -p x", "display-message")]
    [Arguments("lsp", "list-panes")]
    [Arguments("show", "show-options")]
    [Arguments("send-k x", "send-keys")] // unambiguous prefix
    public async Task AliasesAndPrefixes(string line, string name)
    {
        // Act
        var commands = TmuxParser.Parse(Argv(line)).Commands;

        // Assert
        var c = await Assert.That(commands).HasSingleItem();
        await Assert.That(c.Name).IsEqualTo(name);
    }

    [Test]
    public async Task AmbiguousPrefixIsAnError()
    {
        // Act
        var act = () => TmuxParser.Parse(["s"]);

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxUsageException>();
    }

    [Test]
    public async Task SemicolonSeparatesCommands()
    {
        // Act
        var inv = TmuxParser.Parse(["select-pane", "-t", "%1", ";", "send-keys", "ls", "Enter"]);

        // Assert
        await Assert.That(inv.Commands.Select(c => c.Name)).IsEquivalentTo(["select-pane", "send-keys"], CollectionOrdering.Matching);
        await Assert.That(inv.Commands[1].Args).IsEquivalentTo(["ls", "Enter"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task VersionFlag()
    {
        // Act
        var inv = TmuxParser.Parse(["-V"]);

        // Assert
        await Assert.That(inv.Version).IsTrue();
    }

    [Test]
    public async Task SocketNameFlag()
    {
        // Act
        var inv = TmuxParser.Parse(["-L", "work", "ls"]);

        // Assert
        await Assert.That(inv.SocketName).IsEqualTo("work");
    }

    [Test]
    [Arguments("-CC attach")]
    [Arguments("-f /dev/null")]
    public async Task ControlModeAndConfigFilesNeedTheRealServer(string line)
    {
        // Act
        var inv = TmuxParser.Parse(Argv(line));

        // Assert
        await Assert.That(inv.NeedsRealServer).IsTrue();
    }

    [Test]
    public async Task UnknownCommandIsReported()
    {
        // Act
        var inv = TmuxParser.Parse(["frobnicate", "-x"]);

        // Assert
        await Assert.That(inv.UnknownCommand).IsEqualTo("frobnicate");
    }

    [Test]
    public async Task NoArgumentsMeansNoCommands()
    {
        // Act
        var inv = TmuxParser.Parse([]);

        // Assert
        await Assert.That(inv.Commands).IsEmpty();
    }

    [Test]
    [Arguments("split-window -Q")]
    [Arguments("split-window -t")]
    [Arguments("has-session extra")]
    [Arguments("load-buffer")]
    public async Task BadFlagsAndArgumentCounts(string line)
    {
        // Act
        var act = () => TmuxParser.Parse(Argv(line));

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxUsageException>();
    }

    [Test]
    public async Task RepeatedValuesAreKept()
    {
        // Act
        var commands = TmuxParser.Parse(["new-window", "-e", "A=1", "-e", "B=2"]).Commands;

        // Assert
        var c = await Assert.That(commands).HasSingleItem();
        await Assert.That(c.AllValues.Where(v => v.Flag == 'e').Select(v => v.Value)).IsEquivalentTo(["A=1", "B=2"], CollectionOrdering.Matching);
    }
}

public class TmuxProcessNameTests
{
    [Test]
    [Arguments("tmux", true)]
    [Arguments("tmux: client", true)] // tmux 3.x renames its processes
    [Arguments("tmux: server", true)]
    [Arguments("erikterm-tmux", false)] // the shim
    [Arguments("tmuxinator", false)]
    [Arguments(null, false)]
    public async Task RealTmuxNames(string? name, bool tmux)
    {
        // Act
        bool isTmux = TmuxSession.IsTmuxProcessName(name);

        // Assert
        await Assert.That(isTmux).IsEqualTo(tmux);
    }
}

public class TmuxFormatTests
{
    private static readonly Dictionary<string, string> Vars = new()
    {
        ["pane_id"] = "%3", ["pane_index"] = "1", ["pane_active"] = "1", ["window_index"] = "0", ["session_name"] = "erikterm",
        ["pane_title"] = "researcher", ["empty"] = "", ["zero"] = "0", ["pane_current_path"] = "/home/me/src/",
    };

    private static string X(string f) => TmuxFormat.Expand(f, k => Vars.GetValueOrDefault(k));

    [Test]
    public async Task Variables()
    {
        // Act
        string text = X("#{session_name}:#{window_index}.#{pane_index} #{pane_id}");

        // Assert
        await Assert.That(text).IsEqualTo("erikterm:0.1 %3");
    }

    [Test]
    public async Task UnknownVariablesAreEmpty()
    {
        // Act
        string text = X("[#{nope}]");

        // Assert
        await Assert.That(text).IsEqualTo("[]");
    }

    [Test]
    public async Task Aliases()
    {
        // Act
        string text = X("#S #D #P #T");

        // Assert
        await Assert.That(text).IsEqualTo("erikterm %3 1 researcher");
    }

    [Test]
    public async Task Escapes()
    {
        // Act
        string text = X("##a#,b#}");

        // Assert
        await Assert.That(text).IsEqualTo("#a,b}");
    }

    [Test]
    [Arguments("#{pane_id}#{?pane_active, (active),}", "%3 (active)")]
    [Arguments("#{?zero,yes,no}", "no")]
    [Arguments("#{?empty,yes,no}", "no")]
    [Arguments("#{?zero,a,pane_active,b,c}", "b")] // else-if chain
    [Arguments("#{?pane_active,nested #{pane_id},x}", "nested %3")]
    public async Task Conditionals(string format, string expected)
    {
        // Act
        string text = X(format);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments("#{==:#{pane_id},%3}", "1")]
    [Arguments("#{!=:#{pane_id},%3}", "0")]
    [Arguments("#{?#{==:#{session_name},erikterm},yes,no}", "yes")]
    [Arguments("#{||:zero,pane_active}", "1")]
    [Arguments("#{&&:zero,pane_active}", "0")]
    public async Task Comparisons(string format, string expected)
    {
        // Act
        string text = X(format);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    [Arguments("#{=3:pane_title}", "res")]
    [Arguments("#{=-3:pane_title}", "her")]
    [Arguments("#{b:pane_current_path}", "src")]
    [Arguments("#{n:pane_title}", "10")]
    [Arguments("#{l:#{x}}", "#{x}")]
    public async Task Modifiers(string format, string expected)
    {
        // Act
        string text = X(format);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task StylesAreKept()
    {
        // Act
        string text = X("#[fg=blue,bold] #{pane_title} #[default]");

        // Assert
        await Assert.That(text).IsEqualTo("#[fg=blue,bold] researcher #[default]");
    }

    [Test]
    public async Task UnterminatedBraceIsLiteral()
    {
        // Act
        string text = X("a #{pane_id");

        // Assert
        await Assert.That(text).IsEqualTo("a #{pane_id");
    }
}

public class TmuxKeysTests
{
    [Test]
    [Arguments("Enter", "\r")]
    [Arguments("enter", "\r")]
    [Arguments("Escape", "\e")]
    [Arguments("C-c", "\x03")]
    [Arguments("C-a", "\x01")]
    [Arguments("^D", "\x04")]
    [Arguments("C-Space", "\0")]
    [Arguments("M-x", "\ex")]
    [Arguments("C-M-a", "\e\x01")]
    [Arguments("Tab", "\t")]
    [Arguments("BTab", "\e[Z")]
    [Arguments("BSpace", "\x7f")]
    [Arguments("Space", " ")]
    [Arguments("Up", "\e[A")]
    [Arguments("C-Left", "\e[1;5D")]
    [Arguments("S-Up", "\e[1;2A")]
    [Arguments("M-Right", "\e[1;3C")]
    [Arguments("PageUp", "\e[5~")]
    [Arguments("NPage", "\e[6~")]
    [Arguments("C-PageDown", "\e[6;5~")]
    [Arguments("DC", "\e[3~")]
    [Arguments("F1", "\eOP")]
    [Arguments("F5", "\e[15~")]
    [Arguments("S-F1", "\e[1;2P")]
    [Arguments("Home", "\e[H")]
    public async Task KeyNames(string key, string bytes)
    {
        // Act
        string sent = TmuxKeys.Translate(key);

        // Assert
        await Assert.That(sent).IsEqualTo(bytes);
    }

    [Test]
    [Arguments("bun run watch")]
    [Arguments("y")]
    [Arguments("Enterprise")]
    [Arguments("C-")]
    [Arguments("hello world")]
    public async Task TextIsTypedAsIs(string text)
    {
        // Act
        string sent = TmuxKeys.Translate(text);

        // Assert
        await Assert.That(sent).IsEqualTo(text);
    }

    [Test]
    [Arguments("Up", "\eOA")]
    [Arguments("C-Up", "\e[1;5A")]
    public async Task ApplicationCursorKeys(string key, string bytes)
    {
        // Act
        string sent = TmuxKeys.Translate(key, appCursor: true);

        // Assert
        await Assert.That(sent).IsEqualTo(bytes);
    }

    [Test]
    public async Task Hex()
    {
        // Act
        string sent = TmuxKeys.FromHex(["61", "62", "0d"]);

        // Assert
        await Assert.That(sent).IsEqualTo("ab\r");
    }
}

public class TmuxSessionTests
{
    /// <summary>Two tabs: tab 10 (index 0) has %1 (left, active, focused) and %2 / %3 stacked right; tab 20 has %4.</summary>
    internal static List<PaneInfo> Sample() =>
    [
        new() { TabId = 10, TabIndex = 0, PaneId = 1, IsActive = true, IsFocused = true, Size = new() { Cols = 40, Rows = 30 }, Title = "zsh", ForegroundProcessName = "claude", Pid = 100 },
        new() { TabId = 10, TabIndex = 0, PaneId = 2, Left = 41, Size = new() { Cols = 39, Rows = 15 }, Title = "cat", CreatedByTmux = true, PaneTitle = "researcher" },
        new() { TabId = 10, TabIndex = 0, PaneId = 3, Left = 41, Top = 16, Size = new() { Cols = 39, Rows = 14 }, Title = "cat" },
        new() { TabId = 20, TabIndex = 1, PaneId = 4, IsActive = true, TabTitle = "logs", Size = new() { Cols = 80, Rows = 30 }, Title = "tail" },
    ];

    // Per test (TUnit runs this class's tests in parallel): the sample session as pane %2 sees it.
    private readonly TmuxSession _session = new(Sample(), callerPane: 2);

    /// <summary><see cref="Sample"/> plus a second erikterm window whose tab index 0 collides with the caller's.</summary>
    private static List<PaneInfo> TwoWindows()
    {
        var panes = Sample();
        panes.Add(new() { WindowId = 2, TabId = 30, TabIndex = 0, PaneId = 5, IsActive = true, TabActive = true, Size = new() { Cols = 80, Rows = 30 }, Title = "vim" });
        return panes;
    }

    [Test]
    public async Task CurrentIsTheCallersPane()
    {
        // Act
        int current = _session.Current.PaneId;

        // Assert
        await Assert.That(current).IsEqualTo(2);
    }

    [Test]
    [Arguments(null)]
    [Arguments(99)]
    public async Task WithoutAKnownCallerCurrentIsTheFocusedPane(int? caller)
    {
        // Act
        int current = new TmuxSession(Sample(), caller).Current.PaneId;

        // Assert
        await Assert.That(current).IsEqualTo(1);
    }

    [Test]
    public async Task SessionIsTheCallersWindow()
    {
        // Act
        var s = new TmuxSession(TwoWindows(), callerPane: 2);

        // Assert
        await Assert.That(s.Windows.Count).IsEqualTo(2);
        await Assert.That(s.Panes.Count).IsEqualTo(4);
    }

    [Test]
    public async Task IndexesStayInTheCallersWindow()
    {
        // Arrange
        var s = new TmuxSession(TwoWindows(), callerPane: 2);

        // Act
        int pane = s.ResolvePane(":0.0").PaneId;

        // Assert
        await Assert.That(pane).IsEqualTo(1);
    }

    [Test]
    [Arguments("%5")]  // pane ids are process-wide
    [Arguments("@30")]
    public async Task IdsReachOtherWindows(string target)
    {
        // Arrange
        var s = new TmuxSession(TwoWindows(), callerPane: 2);

        // Act
        int pane = s.ResolvePane(target).PaneId;

        // Assert
        await Assert.That(pane).IsEqualTo(5);
    }

    [Test]
    public async Task WindowOfAPaneInAnotherWindow()
    {
        // Arrange
        var s = new TmuxSession(TwoWindows(), callerPane: 2);

        // Act
        int tab = s.ResolveWindow("%5").TabId;

        // Assert
        await Assert.That(tab).IsEqualTo(30);
    }

    [Test]
    public async Task ACallerInTheOtherWindowSeesOnlyItsOwnTabs()
    {
        // Act
        var s = new TmuxSession(TwoWindows(), callerPane: 5);

        // Assert
        await Assert.That(s.Windows).HasSingleItem();
    }

    [Test]
    [Arguments(null, 2)]
    [Arguments("%3", 3)]
    [Arguments("@20", 4)]
    [Arguments("@10.%3", 3)]
    [Arguments("erikterm:0.2", 3)]
    [Arguments("erikterm:1", 4)]
    [Arguments(":0.0", 1)]
    [Arguments(":1.0", 4)]
    [Arguments("$0:1", 4)]
    [Arguments("0", 1)]       // a bare number is a pane index in the current window
    [Arguments("2", 3)]
    [Arguments("erikterm", 2)]
    [Arguments("logs", 4)]    // a window name
    [Arguments(":{end}", 4)]
    [Arguments(":0.{bottom}", 3)]
    public async Task PaneTargets(string? target, int pane)
    {
        // Act
        int resolved = _session.ResolvePane(target).PaneId;

        // Assert
        await Assert.That(resolved).IsEqualTo(pane);
    }

    [Test]
    [Arguments("%99")]
    [Arguments("app")]
    [Arguments("claude-swarm:0.0")]
    [Arguments("@99")]
    public async Task TargetsOutsideTheSessionAreNotOurs(string target)
    {
        // Act
        var act = () => _session.ResolvePane(target);

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxNotOursException>();
    }

    [Test]
    [Arguments(":5")]
    [Arguments(":0.7")]
    [Arguments("9")]
    public async Task MissingPartsOfOurSessionAreErrors(string target)
    {
        // Act
        var act = () => _session.ResolvePane(target);

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxTargetException>();
    }

    [Test]
    [Arguments("@20", 20)]
    [Arguments("%3", 10)]
    [Arguments("1", 20)]
    [Arguments(null, 10)]
    public async Task WindowTargets(string? target, int tab)
    {
        // Act
        int resolved = _session.ResolveWindow(target).TabId;

        // Assert
        await Assert.That(resolved).IsEqualTo(tab);
    }

    [Test]
    [Arguments("erikterm")]
    [Arguments("$0")]
    [Arguments("%1")]
    public async Task SessionTargetsInOurSessionResolve(string target)
    {
        // Act
        var act = () => _session.ResolveSession(target);

        // Assert
        await Assert.That(act).ThrowsNothing();
    }

    [Test]
    public async Task OtherSessionsAreNotOurs()
    {
        // Act
        var act = () => _session.ResolveSession("claude-swarm");

        // Assert
        await Assert.That(act).ThrowsExactly<TmuxNotOursException>();
    }

    [Test]
    [Arguments("#{pane_id} #{pane_index} #{window_id} #{window_index} #{session_name} #{pane_title}", 1, "%2 1 @10 0 erikterm researcher")]
    [Arguments("#{pane_width}x#{pane_height} #{pane_left},#{pane_top} #{pane_active}", 1, "39x15 41,0 0")]
    [Arguments("#{pane_current_command} #{pane_pid}", 0, "claude 100")]
    [Arguments("#{window_panes} #{window_width}x#{window_height} #{window_active}", 1, "3 80x30 1")]
    [Arguments("#{window_name}", 3, "logs")]
    // Claude Code's tmux image probe: termtype, passthrough, focus events, top, left, status, position, bigger.
    // Options are numeric in formats; Claude Code takes "1" for on.
    [Arguments("#{client_termtype}\t#{allow-passthrough}\t#{focus-events}\t#{pane_top}\t#{pane_left}\t#{status}\t#{status-position}\t#{window_bigger}", 1,
        "erikterm 0.1\t1\t1\t0\t41\t0\tbottom\t0")]
    [Arguments("#{client_control_mode}", 1, "0")]
    public async Task FormatVariables(string format, int paneIndex, string expected)
    {
        // Arrange
        var pane = _session.Panes[paneIndex];

        // Act
        string text = _session.Expand(format, pane);

        // Assert
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task PaneAddressHasTheShapeClaudeCodeExpects()
    {
        // Act
        string address = _session.Expand("#{session_name}:#{window_id}.#{pane_id}", _session.Panes[1]);

        // Assert
        await Assert.That(address).Matches(@"^[A-Za-z0-9_.-]{1,64}:@?\d{1,6}\.%?\d{1,6}$");
    }

    [Test]
    public async Task TitleFromClaudeCodesAgentCommand()
    {
        // Arrange
        string cmd = "sh -c '{ echo $PPID > \"$1\"/cgroup.procs; } 2>/dev/null' sh /x; cd '/repo' && env CLAUDECODE=1 '/bin/claude' --agent-id r@team --agent-name researcher --team-name team";

        // Act
        string? title = TmuxSession.TitleFromCommand(["/bin/zsh", "-c", cmd]);

        // Assert
        await Assert.That(title).IsEqualTo("researcher");
    }

    [Test]
    public async Task TitleFromAQuotedAgentName()
    {
        // Act
        string? title = TmuxSession.TitleFromCommand(["x", "--agent-name 'my agent' --team-name t"]);

        // Assert
        await Assert.That(title).IsEqualTo("my agent");
    }

    [Test]
    public async Task NoTitleWithoutAnAgentName()
    {
        // Act
        string? title = TmuxSession.TitleFromCommand(["cat"]);

        // Assert
        await Assert.That(title).IsNull();
    }
}

// Internal: TmuxRoute (an [Arguments] parameter) is internal to the app.
// The FakePath tests set Unix file modes; they are [ExcludeOn(OS.Windows)], which the platform analyzer can't see.
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal class TmuxHandoffTests
{
    private const string Fake = "/run/user/1000/erikterm/erikterm-42.sock";

    private static TmuxRoute Route(string line, string? fake = Fake, bool real = true) =>
        TmuxHandoff.Decide(TmuxParser.Parse(line.Length == 0 ? [] : line.Split(' ')), fake, real).Route;

    [Test]
    [Arguments("")]
    [Arguments("new")]
    [Arguments("new-session -A -s main")]
    [Arguments("a")]
    [Arguments("attach-session -t work")]
    [Arguments("kill-server")]
    [Arguments("ls")]
    public async Task InteractiveUseIsRealTmux(string line)
    {
        // Act
        var route = Route(line);

        // Assert
        await Assert.That(route).IsEqualTo(TmuxRoute.Real);
    }

    [Test]
    public async Task ListingWithoutRealTmuxIsEmulated()
    {
        // Act
        var route = Route("ls", real: false);

        // Assert
        await Assert.That(route).IsEqualTo(TmuxRoute.Emulate); // nothing else to list
    }

    [Test]
    [Arguments("-L claude-swarm-123 has-session -t claude-swarm", TmuxRoute.Real)]
    [Arguments("-S /tmp/tmux-1000/default list-panes", TmuxRoute.Real)]
    [Arguments("-S " + Fake + " list-panes", TmuxRoute.Emulate)]
    [Arguments("-CC attach", TmuxRoute.Real)]
    public async Task OtherServersAreRealTmux(string line, TmuxRoute expected)
    {
        // Act
        var route = Route(line);

        // Assert
        await Assert.That(route).IsEqualTo(expected);
    }

    [Test]
    [Arguments("split-window")]
    [Arguments("-V")]
    public async Task WithoutTheFakeSessionEverythingIsReal(string line)
    {
        // Act
        var route = Route(line, fake: null);

        // Assert
        await Assert.That(route).IsEqualTo(TmuxRoute.Real);
    }

    [Test]
    [Arguments("-V", true, TmuxRoute.Emulate)]
    [Arguments("split-window -h", true, TmuxRoute.Emulate)]
    [Arguments("display-message -p #{pane_id}", true, TmuxRoute.Emulate)]
    [Arguments("has-session -t claude-swarm", true, TmuxRoute.Emulate)] // the target decides later
    [Arguments("choose-tree", true, TmuxRoute.Real)]                    // not emulated: real tmux, if any
    [Arguments("choose-tree", false, TmuxRoute.Emulate)]                // …else "unknown command"
    public async Task FakeSessionCommandsAreEmulated(string line, bool real, TmuxRoute expected)
    {
        // Act
        var route = Route(line, real: real);

        // Assert
        await Assert.That(route).IsEqualTo(expected);
    }

    [Test]
    public async Task WithoutARunningRealServerUnsupportedCommandsAreEmulated()
    {
        // Act
        var route = TmuxHandoff.Decide(TmuxParser.Parse(["choose-tree"]), Fake, true, realServerRunning: false).Route;

        // Assert
        await Assert.That(route).IsEqualTo(TmuxRoute.Emulate);
    }

    /// <summary>
    /// A PATH with erikterm's shim dir, another erikterm symlink named tmux, and a real tmux, in a fresh temp dir.
    /// </summary>
    private sealed class FakePath : IDisposable
    {
        private readonly string _root = ShortTemp.Path("et-tmux-");
        public string Exe { get; }
        public string RealTmux { get; }
        public string Full { get; }       // shim:other:bin
        public string ShimOnly { get; }   // shim:other

        public FakePath()
        {
            string shim = Path.Combine(_root, "erikterm", "shim-77"), bin = Path.Combine(_root, "bin"), self = Path.Combine(_root, "self");
            Directory.CreateDirectory(shim);
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(self);
            Exe = Path.Combine(self, "erikterm");
            File.WriteAllText(Exe, "");
            File.SetUnixFileMode(Exe, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.CreateSymbolicLink(Path.Combine(shim, "tmux"), Exe);
            string other = Path.Combine(_root, "other");
            Directory.CreateDirectory(other);
            File.CreateSymbolicLink(Path.Combine(other, "tmux"), Exe); // erikterm under another name: skipped too
            RealTmux = Path.Combine(bin, "tmux");
            File.WriteAllText(RealTmux, "#!/bin/sh\n");
            File.SetUnixFileMode(RealTmux, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Full = string.Join(':', shim, other, bin);
            ShimOnly = string.Join(':', shim, other);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    [Test]
    [ExcludeOn(OS.Windows)] // no shim there
    public async Task FindsRealTmuxButNotTheShim()
    {
        // Arrange
        using var path = new FakePath();

        // Act
        string? found = TmuxHandoff.FindReal(path.Full, path.Exe);

        // Assert
        await Assert.That(found).IsEqualTo(path.RealTmux);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task OnlyErikTermOnThePathFindsNothing()
    {
        // Arrange
        using var path = new FakePath();

        // Act
        string? found = TmuxHandoff.FindReal(path.ShimOnly, path.Exe);

        // Assert
        await Assert.That(found).IsNull();
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task ANonExecutableTmuxIsSkipped()
    {
        // Arrange
        using var path = new FakePath();
        File.SetUnixFileMode(path.RealTmux, UnixFileMode.UserRead); // not executable

        // Act
        string? found = TmuxHandoff.FindReal(path.Full, path.Exe);

        // Assert
        await Assert.That(found).IsNull();
    }
}

public class TmuxShimFilesTests
{
    [Test]
    public async Task A_framework_dependent_build_gives_the_shim_its_assemblies()
    {
        // Arrange: a Debug build's output folder on Windows.
        string[] files =
        [
            "erikterm.exe", "erikterm.dll", "erikterm.pdb", "erikterm.deps.json", "erikterm.runtimeconfig.json",
            "ErikTerm.Core.dll", "ErikTerm.Ui.dll", "SDL3.dll", "freetype.dll", "conpty.dll", "OpenConsole.exe",
        ];

        // Act
        var needed = TmuxShimDirectory.AppFilesForShim("erikterm.exe", files);

        // Assert: the managed app, not the native libraries.
        await Assert.That(needed).IsEquivalentTo(["erikterm.dll", "erikterm.deps.json", "erikterm.runtimeconfig.json", "ErikTerm.Core.dll", "ErikTerm.Ui.dll"]);
    }

    [Test]
    public async Task A_single_file_build_needs_nothing_beside_the_shim()
    {
        // Act
        var needed = TmuxShimDirectory.AppFilesForShim("erikterm.exe", ["erikterm.exe", "SDL3.dll", "conpty.dll"]);

        // Assert
        await Assert.That(needed).IsEmpty();
    }
}

public class TmuxEnvironmentTests
{
    private const string Sock = "/run/user/1000/erikterm/erikterm-42.sock";
    private const string Shim = "/run/user/1000/erikterm/shim-42";
    /// <summary>What separates PATH entries here: ':' on Unix, ';' on Windows — the paths themselves don't matter.</summary>
    private static readonly char Sep = Path.PathSeparator;

    [Test]
    public async Task ApplySetsTmuxLikeVariables()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["PATH"] = $"/usr/bin{Sep}/bin" };

        // Act
        TmuxEnvironment.Apply(env, Shim, Sock, 42, 7);

        // Assert
        await Assert.That(env["PATH"]).IsEqualTo($"{Shim}{Sep}/usr/bin{Sep}/bin");
        await Assert.That(env["TMUX"]).IsEqualTo($"{Sock},42,0");
        await Assert.That(env["TMUX_PANE"]).IsEqualTo("%7");
    }

    [Test]
    public async Task AppliedTmuxIsFakeAndNamesTheSocket()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
        TmuxEnvironment.Apply(env, Shim, Sock, 42, 7);

        // Act
        bool fake = TmuxEnvironment.IsFake(env["TMUX"]);
        string? socket = TmuxEnvironment.SocketOf(env["TMUX"]);

        // Assert
        await Assert.That(fake).IsTrue();
        await Assert.That(socket).IsEqualTo(Sock);
    }

    [Test]
    public async Task ApplyKeepsClaudeCodeOnTruecolor()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin" };

        // Act
        TmuxEnvironment.Apply(env, Shim, Sock, 42, 7);

        // Assert
        // Without this Claude Code drops to 256 colors whenever $TMUX is set.
        await Assert.That(env[TmuxEnvironment.ClaudeTruecolorVariable]).IsEqualTo("1");
    }

    [Test]
    public async Task RealTmuxDoesNotInheritTheTruecolorOverride()
    {
        // Arrange
        var current = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin", ["TMUX"] = $"{Sock},42,0", ["TMUX_PANE"] = "%3", [TmuxEnvironment.ClaudeTruecolorVariable] = "1",
        };

        // Act
        var env = TmuxEnvironment.ForRealTmux(current);

        // Assert
        await Assert.That(env).DoesNotContainKey(TmuxEnvironment.ClaudeTruecolorVariable);
    }

    [Test]
    public async Task RemovingTheEmulationDropsTheTruecolorOverride()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["TMUX"] = $"{Sock},42,0", ["TMUX_PANE"] = "%3", [TmuxEnvironment.ClaudeTruecolorVariable] = "1" };

        // Act
        TmuxEnvironment.Remove(env);

        // Assert
        await Assert.That(env).DoesNotContainKey(TmuxEnvironment.ClaudeTruecolorVariable);
    }

    [Test]
    public async Task NestedErikTermReplacesTheOuterShim()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["PATH"] = $"/run/user/1000/erikterm/shim-9{Sep}/usr/bin", ["TMUX"] = "/run/user/1000/erikterm/erikterm-9.sock,9,0" };

        // Act
        TmuxEnvironment.Apply(env, Shim, Sock, 42, 1);

        // Assert
        await Assert.That(env["PATH"]).IsEqualTo($"{Shim}{Sep}/usr/bin");
    }

    [Test]
    public async Task RemoveKeepsARealTmux()
    {
        // Arrange
        var real = new Dictionary<string, string> { ["PATH"] = $"{Shim}{Sep}/usr/bin", ["TMUX"] = "/tmp/tmux-1000/default,1234,0", ["TMUX_PANE"] = "%0" };

        // Act
        TmuxEnvironment.Remove(real);

        // Assert
        await Assert.That(real["PATH"]).IsEqualTo("/usr/bin");
        await Assert.That(real["TMUX"]).IsEqualTo("/tmp/tmux-1000/default,1234,0");
        await Assert.That(real["TMUX_PANE"]).IsEqualTo("%0");
    }

    [Test]
    public async Task RemoveDropsTheFakeTmux()
    {
        // Arrange
        var fake = new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["TMUX"] = $"{Sock},42,0", ["TMUX_PANE"] = "%3" };

        // Act
        TmuxEnvironment.Remove(fake);

        // Assert
        await Assert.That(fake).DoesNotContainKey("TMUX");
        await Assert.That(fake).DoesNotContainKey("TMUX_PANE");
    }

    [Test]
    public async Task RealTmuxGetsAScrubbedEnvironment()
    {
        // Arrange
        var parent = new Dictionary<string, string>
        {
            ["PATH"] = $"{Shim}{Sep}/usr/local/bin{Sep}/tmp/erikterm-me/shim-3{Sep}/usr/bin", ["TMUX"] = $"{Sock},42,0", ["TMUX_PANE"] = "%3", ["HOME"] = "/home/me",
        };

        // Act
        var env = TmuxEnvironment.ForRealTmux(parent);

        // Assert
        await Assert.That(env["PATH"]).IsEqualTo($"/usr/local/bin{Sep}/usr/bin");
        await Assert.That(env).DoesNotContainKey("TMUX");
        await Assert.That(env).DoesNotContainKey("TMUX_PANE");
        await Assert.That(env["HOME"]).IsEqualTo("/home/me");
    }

    [Test]
    [Arguments("/run/user/1000/erikterm/shim-42", true)]
    [Arguments("/run/user/1000/erikterm/shim-42/", true)]
    [Arguments("/tmp/erikterm-erik/shim-1", true)]
    [Arguments("/home/me/shim-42", false)]
    [Arguments("/run/user/1000/erikterm/shim-x", false)]
    [Arguments("/usr/bin", false)]
    public async Task ShimDirectories(string entry, bool shim)
    {
        // Act
        bool isShim = TmuxEnvironment.IsShimDirectory(entry);

        // Assert
        await Assert.That(isShim).IsEqualTo(shim);
    }

    [Test]
    [Arguments("/tmp/tmux-1000/default,1234,0", false)]
    [Arguments("/run/user/1000/erikterm/erikterm-5.sock,5,0", true)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task FakeTmux(string? tmux, bool fake)
    {
        // Act
        bool isFake = TmuxEnvironment.IsFake(tmux);

        // Assert
        await Assert.That(isFake).IsEqualTo(fake);
    }
}

public class TmuxPassthroughTests
{
    private static string Wrap(string s) => "\ePtmux;" + s.Replace("\e", "\e\e") + "\e\\";

    private static Terminal Term() => new(20, 5, 10);

    private static string TopLine(Terminal t) => t.GetText(t.ScreenTopLine, 0, t.ScreenTopLine, 19).TrimEnd();

    [Test]
    public async Task WrappedSequencesRunAsIfSentDirectly()
    {
        // Arrange
        var t = Term();

        // Act
        t.Feed(Encoding.UTF8.GetBytes("ab" + Wrap("\e[1;10Hx\e]2;hello\a") + "y"));

        // Assert
        await Assert.That(t.Title).IsEqualTo("hello");
        await Assert.That(TopLine(t)).IsEqualTo("ab       xy");
    }

    [Test]
    public async Task SplitAcrossReads()
    {
        // Arrange
        var t = Term();
        string s = Wrap("\e[3Gz");

        // Act
        for (int i = 0; i < s.Length; i++) t.Feed(Encoding.UTF8.GetBytes(s[i].ToString()));

        // Assert
        await Assert.That(TopLine(t)).IsEqualTo("  z");
    }

    [Test]
    public async Task SplitAtTheDoubledEscape()
    {
        // Arrange
        var t = Term();
        string s = Wrap("\e[3Gz");

        // Act
        t.Feed(Encoding.UTF8.GetBytes(s[..9]));
        t.Feed(Encoding.UTF8.GetBytes(s[9..]));

        // Assert
        await Assert.That(TopLine(t)).IsEqualTo("  z");
    }

    [Test]
    public async Task NestedPassthroughIsUnwrappedTwice()
    {
        // Arrange
        var t = Term();

        // Act
        t.Feed(Encoding.UTF8.GetBytes(Wrap(Wrap("\e]2;deep\a"))));

        // Assert
        await Assert.That(t.Title).IsEqualTo("deep");
    }

    [Test]
    public async Task OtherDcsRequestsAreStillAnswered()
    {
        // Arrange
        var t = Term();
        var replies = new List<string>();
        t.Response = d => replies.Add(Encoding.UTF8.GetString(d));

        // Act
        t.Feed("\e[>q"u8); // XTVERSION, answered with a DCS
        t.Feed("\eP$qm\e\\"u8); // DECRQSS

        // Assert
        await Assert.That(replies).Contains(r => r.Contains("erikterm"));
    }

    [Test]
    public async Task ADcsNamedTmuxWithoutTheSemicolonIsAnOrdinaryDcs()
    {
        // Arrange
        var t = Term();

        // Act
        t.Feed("\ePtmux\e\\after"u8); // not "tmux;": an ordinary (ignored) DCS

        // Assert
        await Assert.That(TopLine(t)).IsEqualTo("after");
    }

    [Test]
    public async Task EscapeOtherThanStEndsIt()
    {
        // Arrange
        var t = Term();

        // Act
        t.Feed(Encoding.UTF8.GetBytes("\ePtmux;\e\e]2;t\a\e[2Gq")); // no ST: ESC [ starts a CSI

        // Assert
        await Assert.That(t.Title).IsEqualTo("t");
        await Assert.That(TopLine(t)).IsEqualTo(" q");
    }

    [Test]
    public async Task ClipboardThroughTmux()
    {
        // Arrange
        var t = Term();
        var copies = new List<string>();
        t.ClipboardRequested += (_, text) => copies.Add(text);
        string b64 = Convert.ToBase64String("copied"u8.ToArray());
        // Claude Code inside tmux: the raw OSC 52, then the same wrapped for tmux.
        string osc = $"\e]52;c;{b64}\a";

        // Act
        t.Feed(Encoding.UTF8.GetBytes(osc + Wrap(osc)));

        // Assert
        await Assert.That(copies).IsEquivalentTo(["copied", "copied"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task KittyImageInsidePassthrough()
    {
        // Arrange
        var t = new Terminal(40, 10, 100) { CellPixelWidth = 10, CellPixelHeight = 20 };
        var replies = new List<string>();
        t.Response = d => replies.Add(Encoding.UTF8.GetString(d));
        string b64 = Convert.ToBase64String(PngTests.EncodeRgba(60, 40, (x, y) => (uint)(x << 24 | y << 16 | 0xFF)));
        uint id = 0x123456;
        // Claude Code's Yb(): several APC chunks per DCS, each ESC doubled.
        var chunks = new StringBuilder();
        for (int i = 0; i < b64.Length; i += 64)
        {
            string m = i + 64 >= b64.Length ? "m=0" : "m=1";
            string control = i == 0 ? $"a=T,U=1,q=2,f=100,i={id},c=6,r=2,{m}" : $"{m},q=2";
            chunks.Append($"\e_G{control};{b64.Substring(i, Math.Min(64, b64.Length - i))}\e\\");
        }
        string all = chunks.ToString();
        int half = all.IndexOf("\e_G", all.Length / 2, StringComparison.Ordinal);

        // Act
        t.Feed(Encoding.UTF8.GetBytes(Wrap(all[..half]) + Wrap(all[half..])));
        var vp = t.Images.FindVirtual(id, 0, false);

        // Assert
        await Assert.That(replies).IsEmpty();
        var placement = await Assert.That(vp).IsNotNull();
        await Assert.That((placement.Image.Width, placement.Image.Height, placement.Columns, placement.Rows)).IsEqualTo((60, 40, 6, 2));
    }

    [Test]
    public async Task ITermImageInsidePassthrough()
    {
        // Arrange
        var t = new Terminal(40, 10, 100) { CellPixelWidth = 10, CellPixelHeight = 20 };
        string b64 = Convert.ToBase64String(PngTests.EncodeRgba(20, 20, (_, _) => 0xFF0000FF));
        // Claude Code's Vb(): ESC 7, cursor move, File=…, ESC 8 in one passthrough.
        string seq = $"\e7\e[3;5H\e]1337;File=inline=1;size=1;width=4;height=2;preserveAspectRatio=1;doNotMoveCursor=1:{b64}\a\e8";

        // Act
        t.Feed(Encoding.UTF8.GetBytes(Wrap(seq)));
        var flags = t.GetScreenRow(2).Cells[4].Flags;

        // Assert
        await Assert.That(t.Images.Images).HasSingleItem();
        await Assert.That((t.CursorX, t.CursorY)).IsEqualTo((0, 0));
        await Assert.That((int)(flags & CellAttributes.Image)).IsNotEqualTo(0);
    }
}

public class LayoutPresetTests
{
    private static readonly RectI Bounds = new(0, 0, 1000, 600);

    private static Dictionary<string, RectI> Rects(LayoutTree<string> tree)
    {
        var list = new List<(string Pane, RectI Rect)>();
        tree.Layout(Bounds, 0, list);
        return list.ToDictionary(p => p.Pane, p => p.Rect);
    }

    private static LayoutTree<string> Tree(int n)
    {
        // LayoutTree compares panes by reference: the same string instances throughout.
        var names = Enumerable.Range(0, n).Select(i => string.Intern("p" + i)).ToArray();
        var tree = new LayoutTree<string>(names[0]);
        for (int i = 1; i < n; i++) tree.Split(names[i - 1], names[i], SplitAxis.Horizontal);
        return tree;
    }

    [Test]
    public async Task MainVerticalPutsTheFirstPaneLeftAndStacksTheRest()
    {
        // Arrange
        var tree = Tree(4);

        // Act
        LayoutPresets.Apply(tree, LayoutPreset.MainVertical);
        var r = Rects(tree);

        // Assert
        await Assert.That(r["p0"]).IsEqualTo(new RectI(0, 0, 500, 600));
        await Assert.That((r["p1"].X, r["p1"].Y, r["p1"].H)).IsEqualTo((500, 0, 200));
        await Assert.That((r["p2"].X, r["p2"].Y)).IsEqualTo((500, 200));
        await Assert.That((r["p3"].X, r["p3"].Y)).IsEqualTo((500, 400));
        await Assert.That(tree.Leaves()).IsEquivalentTo(["p0", "p1", "p2", "p3"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResizingTheMainPaneAfterMainVertical()
    {
        // Arrange
        var tree = Tree(4);
        LayoutPresets.Apply(tree, LayoutPreset.MainVertical);

        // Act
        // Claude Code then: resize-pane -t <leader> -x 30%
        bool resized = LayoutPresets.Resize(tree, "p0", SplitAxis.Horizontal, 300, Bounds, 0, 10);
        var r = Rects(tree);

        // Assert
        await Assert.That(resized).IsTrue();
        await Assert.That(r["p0"].W).IsEqualTo(300);
        await Assert.That(r["p1"].W).IsEqualTo(700);
    }

    [Test]
    public async Task TiledMakesAGrid()
    {
        // Arrange
        var tree = Tree(5);

        // Act
        LayoutPresets.Apply(tree, LayoutPreset.Tiled);
        var r = Rects(tree);

        // Assert
        // 5 panes: 3 columns, 2 rows; the last row's two panes share its width.
        await Assert.That((r["p0"].X, r["p0"].Y)).IsEqualTo((0, 0));
        await Assert.That(r["p0"].W).IsBetween(332, 334); // 333 ± 1
        await Assert.That(r["p3"].Y).IsEqualTo(300);
        await Assert.That(r["p3"].W).IsEqualTo(500);
    }

    [Test]
    public async Task EvenVerticalStacksEqualRows()
    {
        // Arrange
        var tree = Tree(4);

        // Act
        LayoutPresets.Apply(tree, LayoutPreset.EvenVertical);
        var rects = Rects(tree).Values;

        // Assert
        await Assert.That(rects).All(rect => rect.H == 150);
    }

    [Test]
    public async Task EvenHorizontalMakesEqualColumns()
    {
        // Arrange
        var tree = Tree(4);
        LayoutPresets.Apply(tree, LayoutPreset.EvenVertical); // from another preset, as a user switching layouts does

        // Act
        LayoutPresets.Apply(tree, LayoutPreset.EvenHorizontal);
        var rects = Rects(tree).Values;

        // Assert
        await Assert.That(rects).All(rect => rect.W == 250);
    }

    [Test]
    public async Task EqualizeEvensOutNestedHalves()
    {
        // Arrange
        var skewed = Tree(3); // p0 | (p1 | p2): halves, so p0 gets 50%

        // Act
        LayoutPresets.Equalize(skewed);
        int width = Rects(skewed)["p0"].W;

        // Assert
        await Assert.That(width).IsBetween(332, 334); // 333 ± 1
    }

    [Test]
    public async Task ResizeWithoutASplitOnThatAxis()
    {
        // Arrange
        var tree = Tree(2);

        // Act
        bool resized = LayoutPresets.Resize(tree, "p0", SplitAxis.Vertical, 100, Bounds, 0, 10);

        // Assert
        await Assert.That(resized).IsFalse();
    }
}

/// <summary>The shim against a control socket served by a fake erikterm, replaying what Claude Code runs.</summary>
[NotInParallel(Isolation.Sockets)]
public class TmuxShimEndToEndTests
{
    private readonly string _dir = ShortTemp.Path("et-tmux-");
    private string Socket => Path.Combine(_dir, "erikterm-4242.sock");

    [After(Test)]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeReal(string? path) : IRealTmux
    {
        public readonly List<(List<string> Args, Dictionary<string, string> Env)> Execs = [];
        public readonly List<List<string>> Runs = [];
        public Func<IReadOnlyList<string>, int> RunResult = _ => 1;
        public string? Path { get; } = path;
        public bool ServerRunning { get; set; } = path is not null;

        public int Exec(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, TextWriter stderr)
        {
            Execs.Add((args.ToList(), env.ToDictionary()));
            return 0;
        }

        public int Run(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
        {
            Runs.Add(args.ToList());
            return RunResult(args);
        }
    }

    /// <summary>A tiny erikterm: one tab with pane 1, growing as the shim splits. Handled under <c>lock (this)</c>.</summary>
    private sealed class FakeTerm
    {
        public readonly List<PaneInfo> Panes = [new() { TabId = 1, TabIndex = 0, PaneId = 1, IsActive = true, IsFocused = true, Size = new() { Cols = 120, Rows = 40 }, Title = "claude" }];
        public readonly List<IpcRequest> Seen = [];
        public string? Layout;
        private int _next = 2;

        public IpcResponse Handle(IpcRequest r)
        {
            lock (Seen) Seen.Add(r);
            switch (r.Command)
            {
                case "list": return new IpcResponse { Ok = true, Panes = Panes.Select(Clone).ToList() };
                case "split-pane":
                {
                    if (Panes.All(p => p.PaneId != r.PaneId)) return IpcResponse.Failure($"no pane with id {r.PaneId}");
                    var p = new PaneInfo { TabId = 1, TabIndex = 0, PaneId = _next++, Size = new() { Cols = 60, Rows = 40 }, Title = "cat", CreatedByTmux = r.Tmux, Parked = r.Teammate };
                    Panes.Add(p);
                    return new IpcResponse { Ok = true, PaneId = p.PaneId };
                }
                case "set-pane-title": Panes.First(p => p.PaneId == r.PaneId).PaneTitle = r.Title; return IpcResponse.Success();
                case "select-layout": Layout = r.Layout; return IpcResponse.Success();
                case "kill-pane": Panes.RemoveAll(p => p.PaneId == r.PaneId); return IpcResponse.Success();
                case "get-text": return new IpcResponse { Ok = true, Output = r.NoJoin ? "row1\nrow2\n" : "joined\n" };
                default: return IpcResponse.Success();
            }
        }

        public List<IpcRequest> SeenSnapshot()
        {
            lock (Seen) return Seen.ToList();
        }

        public List<int> PaneIds()
        {
            lock (this) return Panes.Select(p => p.PaneId).ToList();
        }

        public string? PaneTitle(int index)
        {
            lock (this) return Panes[index].PaneTitle;
        }

        private static PaneInfo Clone(PaneInfo p) => new()
        {
            TabId = p.TabId, TabIndex = p.TabIndex, PaneId = p.PaneId, IsActive = p.IsActive, IsFocused = p.IsFocused,
            Size = p.Size, Title = p.Title, PaneTitle = p.PaneTitle, CreatedByTmux = p.CreatedByTmux, RealTmux = p.RealTmux, Parked = p.Parked,
            Left = p.Left, Top = p.Top,
        };
    }

    /// <summary>The control socket served for <see cref="FakeTerm"/>, with a "main thread" draining requests like App does.</summary>
    private sealed class Served(IpcServer server, CancellationTokenSource stop) : IDisposable
    {
        public void Dispose()
        {
            stop.Cancel();
            stop.Dispose();
            server.Dispose();
        }
    }

    private Served Serve(FakeTerm term)
    {
        var wake = new SemaphoreSlim(0);
        var queue = new RequestQueue<IpcRequest, IpcResponse>(() => wake.Release());
        var server = new IpcServer(Socket, queue.Post);
        server.Start();
        var stop = new CancellationTokenSource();
        // The token, not the source: Served.Dispose disposes the source, and the thread may only get to run after that.
        var stopping = stop.Token;
        new Thread(() =>
        {
            try
            {
                while (true)
                {
                    wake.Wait(stopping);
                    lock (term) queue.Drain(term.Handle);
                }
            }
            catch (OperationCanceledException) { queue.Close(); }
        }) { IsBackground = true }.Start();
        return new Served(server, stop);
    }

    /// <summary>
    /// The pane's $SHELL, which the emulation runs a one-argument command with (Tmux/TmuxShell.cs). On Windows it
    /// only takes a $SHELL that is really there, so a program that is stands in for /bin/sh.
    /// </summary>
    private static readonly string Shell = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
        : "/bin/sh";

    private Dictionary<string, string> PaneEnv(int pane = 1) => new()
    {
        ["TMUX"] = $"{Socket},4242,0", ["TMUX_PANE"] = $"%{pane}", ["SHELL"] = Shell,
        ["PATH"] = $"/run/user/1000/erikterm/shim-4242{Path.PathSeparator}/usr/bin",
        ["ERIKTERM_SOCKET"] = Socket, ["ERIKTERM_PANE"] = pane.ToString(),
    };

    private static (int Code, string Out, string Err) Tmux(Dictionary<string, string> env, IRealTmux real, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = TmuxShim.Run(args, new StringReader(""), o, e, env, null, real);
        return (code, o.ToString(), e.ToString());
    }

    /// <summary>A command line written space-separated (none of these arguments contains a space).</summary>
    private static string[] Argv(string line) => line.Length == 0 ? [] : line.Split(' ');

    [Test]
    public async Task ClaudeCodeAgentTeamSequence()
    {
        // One test on purpose: every step acts on the panes the previous steps made (Claude Code's agent-team flow).
        // The Act runs the whole sequence and captures each result and the state right after the step that changed it.
        // The stateless startup probes are in StartupProbes.

        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var env = PaneEnv();
        var real = new FakeReal("/usr/bin/tmux");
        string[] S = ["-S", Socket];
        string cmd = "cd '/repo' && env CLAUDECODE=1 '/bin/claude' --agent-id r@t --agent-name researcher";

        // Act
        var panesBefore = Tmux(env, real, [.. S, "list-panes", "-t", "@1", "-F", "#{pane_id}"]);
        // First teammate: 70% to the right of the leader, focus stays on the leader.
        var firstSplit = Tmux(env, real, [.. S, "split-window", "-d", "-t", "%1", "-h", "-l", "70%", "-P", "-F", "#{pane_id}", "--", "cat"]);
        var firstSplitRequest = term.SeenSnapshot().Last(r => r.Command == "split-pane");
        int styleCode = Tmux(env, real, [.. S, "set-option", "-p", "-t", "%2", "window-style", "bg=default,fg=blue"]).Code;
        int titleCode = Tmux(env, real, [.. S, "select-pane", "-t", "%2", "-T", "researcher"]).Code;
        string? teammateTitle = term.PaneTitle(1);
        int borderCode = Tmux(env, real, [.. S, "set-option", "-p", "-t", "%2", "pane-border-format", "#[fg=blue,bold] #{pane_title} #[default]"]).Code;
        // Second teammate: split the middle teammate down, then main-vertical with the leader at 30%.
        var secondSplit = Tmux(env, real, [.. S, "split-window", "-d", "-t", "%2", "-v", "-P", "-F", "#{pane_id}", "--", "cat"]);
        var panesAfter = Tmux(env, real, [.. S, "list-panes", "-t", "@1", "-F", "#{pane_id}"]);
        int layoutCode = Tmux(env, real, [.. S, "select-layout", "-t", "@1", "main-vertical"]).Code;
        string? layout;
        lock (term) layout = term.Layout;
        int resizeCode = Tmux(env, real, [.. S, "resize-pane", "-t", "%1", "-x", "30%"]).Code;
        // Start the agent: remain-on-exit, then respawn-pane -k with the command line as one shell string.
        int remainCode = Tmux(env, real, [.. S, "set-option", "-p", "-t", "%2", "remain-on-exit", "failed"]).Code;
        int respawnCode = Tmux(env, real, [.. S, "respawn-pane", "-k", "-t", "%2", "--", cmd]).Code;
        // Clean up a teammate.
        int killCode = Tmux(env, real, [.. S, "kill-pane", "-t", "%3"]).Code;
        var paneIds = term.PaneIds();
        var seen = term.SeenSnapshot();

        // Assert
        await Assert.That(panesBefore).IsEqualTo((0, "%1\n", ""));
        await Assert.That(firstSplit).IsEqualTo((0, "%2\n", ""));
        await Assert.That((firstSplitRequest.Direction, firstSplitRequest.Percent, firstSplitRequest.NoFocus, firstSplitRequest.Tmux))
            .IsEqualTo(("Right", 70, true, true));
        await Assert.That(firstSplitRequest.Argv!).IsEquivalentTo([Shell, "-c", "cat"], CollectionOrdering.Matching);
        await Assert.That(styleCode).IsEqualTo(0);
        await Assert.That(titleCode).IsEqualTo(0);
        await Assert.That(teammateTitle).IsEqualTo("researcher");
        await Assert.That(borderCode).IsEqualTo(0);
        await Assert.That(secondSplit).IsEqualTo((0, "%3\n", ""));
        await Assert.That(panesAfter).IsEqualTo((0, "%1\n%2\n%3\n", ""));
        await Assert.That(layoutCode).IsEqualTo(0);
        await Assert.That(layout).IsEqualTo("main-vertical");
        await Assert.That(resizeCode).IsEqualTo(0);
        await Assert.That(remainCode).IsEqualTo(0);
        await Assert.That(respawnCode).IsEqualTo(0);
        await Assert.That(killCode).IsEqualTo(0);
        await Assert.That(paneIds).DoesNotContain(3);
        await Assert.That(real.Execs).IsEmpty(); // nothing went to real tmux
        var respawn = await Assert.That(seen).HasSingleItem(r => r.Command == "respawn-pane");
        await Assert.That((respawn.PaneId, respawn.Kill)).IsEqualTo((2, true));
        await Assert.That(respawn.Argv!).IsEquivalentTo([Shell, "-c", cmd], CollectionOrdering.Matching);
        var resize = await Assert.That(seen).HasSingleItem(r => r.Command == "resize-pane");
        await Assert.That((resize.PaneId, resize.WidthPercent)).IsEqualTo((1, 30));
    }

    /// <summary>Probes Claude Code runs (at startup and before its first split), each answered without changing anything.</summary>
    [Test]
    [Arguments(false, "-V", "tmux 3.5 (erikterm)\n")]
    [Arguments(true, "display-message -p #{pane_id}", "%1\n")]
    [Arguments(true, "display-message -t %1 -p #{window_id}", "@1\n")]
    [Arguments(false, "display-message -p -t %1 #{session_name}:#{window_id}.#{pane_id}", "erikterm:@1.%1\n")]
    [Arguments(false, "show -gv allow-passthrough", "on\n")]
    [Arguments(false, "show -gv focus-events", "on\n")]
    [Arguments(false, "show -Av mouse", "on\n")]
    [Arguments(false, "show-options -g prefix", "prefix C-b\n")]
    [Arguments(false, "show-environment -g", "")]
    [Arguments(false, "display-message -p #{client_control_mode}", "0\n")]
    public async Task StartupProbes(bool viaSocketFlag, string line, string expected)
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        string[] args = viaSocketFlag ? ["-S", Socket, .. Argv(line)] : Argv(line);

        // Act
        var result = Tmux(PaneEnv(), new FakeReal("/usr/bin/tmux"), args);

        // Assert
        await Assert.That(result).IsEqualTo((0, expected, ""));
    }

    [Test]
    public async Task TerminalFeaturesCanBeSet()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);

        // Act
        int code = Tmux(PaneEnv(), new FakeReal("/usr/bin/tmux"), "set", "-as", "terminal-features", ",*:RGB").Code;

        // Assert
        await Assert.That(code).IsEqualTo(0);
    }

    [Test]
    public async Task SendKeys()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var real = new FakeReal("/usr/bin/tmux");

        // Act
        int[] codes =
        [
            Tmux(PaneEnv(), real, "send-keys", "-t", "%1", "bun run watch", "Enter").Code,
            Tmux(PaneEnv(), real, "send-keys", "C-c").Code,
            Tmux(PaneEnv(), real, "send-keys", "-l", "Enter").Code,
        ];
        var sends = term.SeenSnapshot().Where(r => r.Command == "send-text").Select(r => (r.Text, r.NoPaste)).ToList();

        // Assert
        await Assert.That(codes).IsEquivalentTo([0, 0, 0], CollectionOrdering.Matching);
        await Assert.That(sends).IsEquivalentTo(new (string?, bool)[] { ("bun run watch\r", true), ("\x03", true), ("Enter", true) }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CapturePane()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var real = new FakeReal("/usr/bin/tmux");

        // Act
        var visible = Tmux(PaneEnv(), real, "capture-pane", "-p", "-t", "%1");
        var history = Tmux(PaneEnv(), real, "capture-pane", "-pJ", "-S", "-", "-E", "-");
        var captures = term.SeenSnapshot().Where(r => r.Command == "get-text").ToList();

        // Assert
        await Assert.That(visible).IsEqualTo((0, "row1\nrow2\n", ""));
        await Assert.That(history).IsEqualTo((0, "joined\n", ""));
        await Assert.That(captures[0].NoJoin).IsTrue();
        await Assert.That((captures[1].StartLine!.Value, captures[1].EndLine, captures[1].NoJoin)).IsEqualTo((int.MinValue / 2, (int?)null, false));
    }

    [Test]
    [Arguments("send-keys -t %99 x", 1, "can't find pane: %99\n")]
    [Arguments("has-session -t claude-swarm", 1, "can't find session: claude-swarm\n")]
    [Arguments("frobnicate", 1, "unknown command: frobnicate\n")]
    [Arguments("show -gv nope", 1, "invalid option: nope\n")]
    [Arguments("show -gqv nope", 0, "")]
    public async Task ErrorsLookLikeTmux(string line, int code, string stderr)
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);

        // Act
        var result = Tmux(PaneEnv(), new FakeReal(null), Argv(line));

        // Assert
        await Assert.That(result).IsEqualTo((code, "", stderr));
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task APosixCommandWithoutAPosixShellSaysSo()
    {
        // Arrange
        // No $SHELL, no git on PATH and nothing under Program Files: only cmd.exe is left, and it can't run
        // Claude Code's `cat` placeholder or its `cd '<dir>' && env … '<claude>' --agent-id …`, so the shim
        // refuses the command instead of handing cmd.exe a line it would mangle.
        var term = new FakeTerm();
        using var served = Serve(term);
        var env = PaneEnv();
        env.Remove("SHELL");
        env["PATH"] = "";
        env["CLAUDECODE"] = "1";
        env["COMSPEC"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

        // Act
        var result = Tmux(env, new FakeReal(null), "split-window", "--", "cat");

        // Assert
        await Assert.That(result.Code).IsEqualTo(1);
        await Assert.That(result.Err).Contains("Git for Windows");
        await Assert.That(term.SeenSnapshot()).DoesNotContain(r => r.Command == "split-pane");
    }

    [Test]
    public async Task UnknownFlagIsAnError()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);

        // Act
        int code = Tmux(PaneEnv(), new FakeReal(null), "split-window", "-Q").Code;

        // Assert
        await Assert.That(code).IsEqualTo(1);
    }

    [Test]
    public async Task PlainTmuxWithoutRealTmuxSaysItIsMissing()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);

        // Act
        var noTmux = Tmux(PaneEnv(), new FakeReal(null));

        // Assert
        await Assert.That(noTmux.Code).IsEqualTo(1);
        await Assert.That(noTmux.Err).Contains("tmux isn't installed");
    }

    /// <summary>Commands for sessions only real tmux has: plain tmux, a real session, a target in it, other servers.</summary>
    private int[] HandOffToRealTmux(FakeReal real) =>
    [
        Tmux(PaneEnv(), real).Code, // plain `tmux`
        Tmux(PaneEnv(), real, "new-session", "-d", "-s", "app", "-x", "120", "-y", "40", "./myapp").Code,
        Tmux(PaneEnv(), real, "send-keys", "-t", "app", "s").Code,      // a real session's target
        Tmux(PaneEnv(), real, "has-session", "-t", "claude-swarm").Code,
        Tmux(PaneEnv(), real, "-L", "claude-swarm-1", "list-panes").Code,
    ];

    [Test]
    public async Task RealTmuxHandoff()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var real = new FakeReal("/usr/bin/tmux");

        // Act
        int[] codes = HandOffToRealTmux(real);
        var seen = term.SeenSnapshot();

        // Assert
        await Assert.That(codes).IsEquivalentTo([0, 0, 0, 0, 0], CollectionOrdering.Matching);
        await Assert.That(real.Execs.Count).IsEqualTo(5);
        await Assert.That(real.Execs[0].Args).IsEmpty();
        await Assert.That(real.Execs[2].Args).IsEquivalentTo(["send-keys", "-t", "app", "s"], CollectionOrdering.Matching);
        await Assert.That(seen).DoesNotContain(r => r.Command == "send-text");
    }

    [Test]
    public async Task RealTmuxRunsWithoutTheFakeTmuxVariables()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var real = new FakeReal("/usr/bin/tmux");

        // Act
        HandOffToRealTmux(real);

        // Assert
        await Assert.That(real.Execs.Count).IsEqualTo(5);
        foreach (var (_, env) in real.Execs)
        {
            await Assert.That(env).DoesNotContainKey("TMUX");
            await Assert.That(env).DoesNotContainKey("TMUX_PANE");
            await Assert.That(env["PATH"]).IsEqualTo("/usr/bin");
            await Assert.That(env["ERIKTERM_SOCKET"]).IsEqualTo(Socket); // erikterm's own variables stay
        }
    }

    // Without a real server there's nobody else to ask: tmux's own error, and unknown commands stay unknown.
    [Test]
    [Arguments("send-keys -t app s", "can't find pane: app\n")]
    [Arguments("choose-tree", "unknown command: choose-tree\n")]
    public async Task WithoutARealServerNothingGoesToRealTmux(string line, string stderr)
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var real = new FakeReal("/usr/bin/tmux") { ServerRunning = false };

        // Act
        var result = Tmux(PaneEnv(), real, Argv(line));

        // Assert
        await Assert.That(result).IsEqualTo((1, "", stderr));
        await Assert.That(real.Execs).IsEmpty();
    }

    [Test]
    public async Task KillSessionGoesToRealTmux()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var real = new FakeReal("/usr/bin/tmux");

        // Act
        int code = Tmux(PaneEnv(), real, "kill-session", "-t", "app").Code;

        // Assert
        await Assert.That(code).IsEqualTo(0);
        var exec = await Assert.That(real.Execs).HasSingleItem();
        await Assert.That(exec.Args).IsEquivalentTo(["kill-session", "-t", "app"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheFakeSocketFlagIsDroppedForRealTmux()
    {
        // -S <fake socket> is dropped on the way to real tmux, which would try to talk to erikterm's socket.

        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var real = new FakeReal("/usr/bin/tmux");

        // Act
        int code = Tmux(PaneEnv(), real, "-S", Socket, "new-session", "-d").Code;

        // Assert
        await Assert.That(code).IsEqualTo(0);
        var exec = await Assert.That(real.Execs).HasSingleItem();
        await Assert.That(exec.Args).IsEquivalentTo(["new-session", "-d"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WithTheFakeSocketFlagOutsideTargetsAreErrors()
    {
        // With -S <fake socket> a target outside the session is an error, not real tmux's.

        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var real = new FakeReal("/usr/bin/tmux");

        // Act
        var named = Tmux(PaneEnv(), real, "-S", Socket, "send-keys", "-t", "app", "x");

        // Assert
        await Assert.That((named.Code, named.Err)).IsEqualTo((1, "can't find pane: app\n"));
    }

    [Test]
    public async Task KillPaneOnAPaneRealTmuxOwnsGoesToRealTmux()
    {
        // Claude Code's separate-window mode makes panes in a real "claude-swarm" session, whose ids can
        // collide with erikterm's: %1 here is the user's pane, not made by the shim, and real tmux has a %1.

        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var real = new FakeReal("/usr/bin/tmux") { RunResult = args => args.Contains("%1") ? 0 : 1 };

        // Act
        int code = Tmux(PaneEnv(2), real, "kill-pane", "-t", "%1").Code;
        var paneIds = term.PaneIds();

        // Assert
        await Assert.That(code).IsEqualTo(0);
        var exec = await Assert.That(real.Execs).HasSingleItem();
        await Assert.That(exec.Args).IsEquivalentTo(["kill-pane", "-t", "%1"], CollectionOrdering.Matching);
        await Assert.That(paneIds).Contains(1);
    }

    [Test]
    public async Task KillPaneOnAPaneTheShimMadeStaysInErikTerm()
    {
        // A pane the shim made is erikterm's, whatever real tmux has.

        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var real = new FakeReal("/usr/bin/tmux") { RunResult = args => args.Contains("%1") ? 0 : 1 };
        // The shim makes %2 (its result is asserted below: the precondition), then real tmux claims every pane.
        var split = Tmux(PaneEnv(), real, "split-window", "-P", "-F", "#{pane_id}");
        real.RunResult = _ => 0;

        // Act
        int code = Tmux(PaneEnv(), real, "kill-pane", "-t", "%2").Code;
        var paneIds = term.PaneIds();

        // Assert
        await Assert.That(split).IsEqualTo((0, "%2\n", ""));
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(paneIds).DoesNotContain(2);
    }

    [Test]
    public async Task PanesRunningRealTmuxAreLeftAlone()
    {
        // Arrange
        var term = new FakeTerm();
        term.Panes[0].RealTmux = true;
        using var served = Serve(term);

        // Act
        var r = Tmux(PaneEnv(), new FakeReal(null), "send-keys", "-t", "%1", "x");

        // Assert
        await Assert.That(r.Code).IsEqualTo(1);
        await Assert.That(r.Err).Contains("running real tmux");
    }

    [Test]
    public async Task LogsEveryInvocation()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        string log = Path.Combine(_dir, "tmux.log");
        var env = PaneEnv();
        env["ERIKTERM_TMUX_LOG"] = log;

        // Act
        Tmux(env, new FakeReal(null), "display-message", "-p", "#{pane_id}");
        Tmux(env, new FakeReal(null), "choose-tree");
        string text = File.ReadAllText(log);

        // Assert
        await Assert.That(text).Contains("tmux display-message -p #{pane_id} => emulated (exit 0)");
        await Assert.That(text).Contains("tmux choose-tree => unsupported (exit 1)");
    }
    // ---- Agent-team teammates as pills (TmuxTeammates.cs, Teammates/WindowTeammates.cs) ----------------------------------

    /// <summary>The environment Claude Code 2.1's tmux calls carry (no CLAUDECODE: that is only for its tools' commands).</summary>
    private Dictionary<string, string> ClaudeEnv()
    {
        var env = PaneEnv();
        env["AI_AGENT"] = "claude-code_2-1-319_harness";
        env["CLAUDE_CODE_ENTRYPOINT"] = "cli";
        return env;
    }

    [Test]
    public async Task ClaudeCodeTeammateSplitIsFlagged()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);

        // Act
        var result = Tmux(ClaudeEnv(), new FakeReal(null), "-S", Socket, "split-window", "-d", "-t", "%1", "-h", "-l", "70%", "-P", "-F", "#{pane_id}", "--", "cat");
        var split = term.SeenSnapshot().Single(r => r.Command == "split-pane");

        // Assert
        await Assert.That(result).IsEqualTo((0, "%2\n", ""));
        await Assert.That((split.Teammate, split.CallerPaneId)).IsEqualTo((true, (int?)1));
    }

    [Test]
    public async Task SplitOutsideClaudeCodeIsNotFlagged()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);

        // Act
        Tmux(PaneEnv(), new FakeReal(null), "split-window", "-d", "-h", "--", "cat");
        var split = term.SeenSnapshot().Single(r => r.Command == "split-pane");

        // Assert
        await Assert.That(split.Teammate).IsFalse();
    }

    [Test]
    public async Task PaneBorderStyleColorsTheTeammate()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var env = ClaudeEnv();
        Tmux(env, new FakeReal(null), "split-window", "-d", "-h", "--", "cat");

        // Act
        var result = Tmux(env, new FakeReal(null), "set-option", "-p", "-t", "%2", "pane-border-style", "fg=green");
        var color = term.SeenSnapshot().Single(r => r.Command == "set-pane-color");

        // Assert
        await Assert.That(result.Code).IsEqualTo(0);
        await Assert.That((color.PaneId, color.Color)).IsEqualTo(((int?)2, "green"));
    }

    [Test]
    public async Task OtherPaneOptionsSendNothing()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);

        // Act
        var result = Tmux(ClaudeEnv(), new FakeReal(null), "set-option", "-p", "-t", "%1", "remain-on-exit", "failed");
        var seen = term.SeenSnapshot();

        // Assert
        await Assert.That(result.Code).IsEqualTo(0);
        await Assert.That(seen).IsEmpty();
    }

    [Test]
    public async Task RespawnFromClaudeCodeIsFlagged()
    {
        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var env = ClaudeEnv();
        Tmux(env, new FakeReal(null), "split-window", "-d", "-h", "--", "cat");

        // Act
        Tmux(env, new FakeReal(null), "respawn-pane", "-k", "-t", "%2", "--", "env CLAUDECODE=1 claude --agent-id a@t --agent-name a --team-name t");
        var respawn = term.SeenSnapshot().Single(r => r.Command == "respawn-pane");

        // Assert
        await Assert.That((respawn.Teammate, respawn.CallerPaneId)).IsEqualTo((true, (int?)1));
    }

    [Test]
    public async Task ParkedPaneAnswersLikeAPane()
    {
        // Answers about a parked teammate come from its place in the layout as if it were shown: one walk-through of
        // what Claude Code asks about a teammate it can't see.

        // Arrange
        var term = new FakeTerm();
        using var served = Serve(term);
        var env = ClaudeEnv();
        var real = new FakeReal(null);
        Tmux(env, real, "split-window", "-d", "-t", "%1", "-h", "-l", "70%", "--", "cat");
        lock (term) { term.Panes[1].Left = 43; term.Panes[1].Size = new() { Cols = 97, Rows = 40 }; }

        // Act
        var list = Tmux(env, real, "list-panes", "-t", "@1", "-F", "#{pane_id}");
        var geometry = Tmux(env, real, "display-message", "-p", "-t", "%2", "#{pane_width}x#{pane_height}@#{pane_left},#{pane_top}");
        var keys = Tmux(env, real, "send-keys", "-t", "%2", "hi", "Enter");
        var capture = Tmux(env, real, "capture-pane", "-p", "-t", "%2");
        var title = Tmux(env, real, "select-pane", "-t", "%2", "-T", "alpha");
        var has = Tmux(env, real, "has-session", "-t", "erikterm");
        var kill = Tmux(env, real, "kill-pane", "-t", "%2");
        var after = Tmux(env, real, "list-panes", "-F", "#{pane_id}");
        var seen = term.SeenSnapshot();

        // Assert
        await Assert.That(list).IsEqualTo((0, "%1\n%2\n", ""));
        await Assert.That(geometry).IsEqualTo((0, "97x40@43,0\n", ""));
        await Assert.That(keys.Code).IsEqualTo(0);
        await Assert.That(seen).Contains(r => r.Command == "send-text" && r.PaneId == 2 && r.Text == "hi\r");
        await Assert.That(capture).IsEqualTo((0, "row1\nrow2\n", ""));
        await Assert.That(title.Code).IsEqualTo(0);
        await Assert.That(has.Code).IsEqualTo(0);
        await Assert.That(kill.Code).IsEqualTo(0);
        await Assert.That(after).IsEqualTo((0, "%1\n", ""));
    }
}

public class TmuxShellTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "et-shell-" + Guid.NewGuid().ToString("N"));

    [After(Test)]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Make(string relative)
    {
        string path = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private static Func<string, string?> Env(Dictionary<string, string> env) => k => env.GetValueOrDefault(k);

    [Test]
    public async Task TheShimIsCalledTmuxExeOnWindows()
    {
        // Act
        string name = TmuxEnvironment.ShimFileName;

        // Assert
        // Windows needs the extension: CreateProcess and the PATH searches built on it only run executables.
        await Assert.That(name).IsEqualTo(OperatingSystem.IsWindows() ? "tmux.exe" : "tmux");
    }

    [Test]
    public async Task AShellInTheEnvironmentComesFirst()
    {
        // Arrange
        string sh = Make("custom/sh.exe");
        var env = new Dictionary<string, string> { ["SHELL"] = sh };

        // Act
        var candidates = TmuxShell.WindowsCandidates(Env(env), "").ToList();

        // Assert
        await Assert.That(candidates[0]).IsEqualTo(sh);
    }

    [Test]
    public async Task GitForWindowsIsFoundNextToGitExe()
    {
        // Arrange
        // git.exe lives in <Git>\cmd; the shell that puts /usr/bin on PATH is <Git>\bin\sh.exe, not <Git>\usr\bin\sh.exe.
        Make("Git/cmd/git.exe");
        string sh = Make("Git/bin/sh.exe");

        // Act
        var candidates = TmuxShell.WindowsCandidates(Env([]), Path.Combine(_dir, "Git", "cmd"))
            .Where(File.Exists).Select(Path.GetFullPath).ToList();

        // Assert
        await Assert.That(candidates).Contains(Path.GetFullPath(sh));
    }

    [Test]
    public async Task TheWslLauncherIsNotAShell()
    {
        // Arrange
        // bash.exe under WindowsApps (and System32) starts a Linux distribution, with its own file system.
        Make("WindowsApps/bash.exe");
        var env = new Dictionary<string, string> { ["SystemRoot"] = Path.Combine(_dir, "Windows") };

        // Act
        var candidates = TmuxShell.WindowsCandidates(Env(env), Path.Combine(_dir, "WindowsApps")).Where(File.Exists);

        // Assert
        await Assert.That(candidates).IsEmpty();
    }

    [Test]
    public async Task TheGitShellInUsrBinIsNotOffered()
    {
        // Arrange
        // <Git>\usr\bin\sh.exe is the same binary but misses the wrapper that puts /usr/bin on PATH, so `cat`
        // and `env` — the two things Claude Code's pane commands need — are not found when it runs them.
        Make("Git/cmd/git.exe");
        Make("Git/usr/bin/sh.exe");

        // Act
        var candidates = TmuxShell.WindowsCandidates(Env([]), Path.Combine(_dir, "Git", "cmd"))
            .Select(c => { try { return Path.GetFullPath(c); } catch (ArgumentException) { return c; } }).ToList();

        // Assert
        await Assert.That(candidates).DoesNotContain(Path.GetFullPath(Path.Combine(_dir, "Git", "usr", "bin", "sh.exe")));
    }

    [Test]
    public async Task WithoutAPosixShellTheCommandProcessorRuns()
    {
        // Arrange
        string cmd = Make("Windows/System32/cmd.exe");
        var env = new Dictionary<string, string> { ["COMSPEC"] = cmd };

        // Act
        var shell = TmuxShell.ForCommand(Env(env));

        // Assert
        // Off Windows there is always /bin/sh; on it cmd.exe is the last resort — /c, not -c, and not POSIX.
        await Assert.That((shell.Path, shell.Flag, shell.Posix))
            .IsEqualTo(OperatingSystem.IsWindows() ? (cmd, "/c", false) : ("/bin/sh", "-c", true));
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task AComspecThatIsNotThereFallsBackToSystem32()
    {
        // Arrange
        string cmd = Make("Windows/System32/cmd.exe");
        var env = new Dictionary<string, string>
        {
            ["COMSPEC"] = Path.Combine(_dir, "gone", "cmd.exe"), ["SystemRoot"] = Path.Combine(_dir, "Windows"),
        };

        // Act
        var shell = TmuxShell.ForCommand(Env(env));

        // Assert
        await Assert.That((shell.Path, shell.Flag)).IsEqualTo((cmd, "/c"));
    }

}
