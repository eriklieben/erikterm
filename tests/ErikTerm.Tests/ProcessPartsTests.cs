using ErikTerm.App.Host;
using ErikTerm.App.Ipc;
using ErikTerm.App.Session;
using ErikTerm.App.Tmux;
using ErikTerm.App.Ui;
using ErikTerm.Config;

namespace ErikTerm.Tests;

/// <summary>The process's parts that App holds and that work without a window: session saving, recent commands, the
/// control socket and tmux shim, the launch forwarding request and the configuration.</summary>
public class SessionKeeperTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "et-keeper-" + Guid.NewGuid().ToString("N")[..8]);
    private string SessionPath => Path.Combine(_dir, "session.json");

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A session whose one window is in <paramref name="cwd"/>.</summary>
    private static SessionFile In(string cwd) => new()
    {
        Windows = [new SessionWindow { Tabs = [new SessionTab { Layout = new SessionNode { Pane = new SessionPane { Cwd = cwd } } }] }],
    };

    private static SessionFile? Saved(string path)
    {
        if (!File.Exists(path)) return null;
        var file = SessionStore.Load(path, out _);
        return file;
    }

    /// <summary>Waits for the keeper's background write: it lands within moments.</summary>
    private static async Task<SessionFile?> SavedSoon(string path)
    {
        for (int i = 0; i < 100 && !File.Exists(path); i++) await Task.Delay(20);
        return Saved(path);
    }

    [Test]
    public async Task NothingIsCapturedBeforeStartupFinished()
    {
        // Arrange
        int captures = 0;
        var keeper = new SessionKeeper(SessionPath, _ => { captures++; return In("/a"); });

        // Act
        keeper.Tick(10_000);

        // Assert
        await Assert.That(captures).IsEqualTo(0);
    }

    [Test]
    public async Task AChangeIsSavedOnceItHasBeenQuietForTwoSeconds()
    {
        // Arrange
        string cwd = "/a";
        var keeper = new SessionKeeper(SessionPath, _ => In(cwd)) { Started = true };
        keeper.Tick(1_000); // the state it started in: not saved
        cwd = "/b";

        // Act
        keeper.Tick(2_000); // the change is seen
        bool savedEarly = File.Exists(SessionPath);
        keeper.Tick(3_000);
        keeper.Tick(4_000); // two seconds after the change

        // Assert
        await Assert.That(savedEarly).IsFalse();
        var saved = await SavedSoon(SessionPath);
        await Assert.That(saved?.Windows[0].Tabs[0].Layout.Pane?.Cwd).IsEqualTo("/b");
    }

    [Test]
    public async Task ExitingTheLastShellForgetsTheSavedSession()
    {
        // Arrange
        Directory.CreateDirectory(_dir);
        SessionStore.Save(SessionPath, In("/old"));
        var keeper = new SessionKeeper(SessionPath, _ => In("/a")) { Started = true, ClosedLastPane = true };

        // Act
        keeper.SaveOnExit();

        // Assert
        await Assert.That(File.Exists(SessionPath)).IsFalse();
    }

    [Test]
    public async Task ForgetSavedSessionStopsSavingOnExit()
    {
        // Arrange
        var keeper = new SessionKeeper(SessionPath, _ => In("/a")) { Started = true };
        var commands = new List<Command>();
        var toasts = new List<string>();
        keeper.AddCommands(commands, (_, title, _) => toasts.Add(title));

        // Act
        commands.Single(c => c.Id == "session-forget").Run!();
        keeper.SaveOnExit();

        // Assert
        await Assert.That(toasts).IsEquivalentTo(["Saved session forgotten"]);
        await Assert.That(File.Exists(SessionPath)).IsFalse();
    }

    [Test]
    public async Task SaveSessionNowWritesTheWindowsAndSaysWhere()
    {
        // Arrange
        var keeper = new SessionKeeper(SessionPath, _ => In("/now")) { Started = true };
        var commands = new List<Command>();
        var toasts = new List<(ToastKind Kind, string Title)>();
        keeper.AddCommands(commands, (kind, title, _) => toasts.Add((kind, title)));

        // Act
        commands.Single(c => c.Id == "session-save").Run!();

        // Assert
        await Assert.That(toasts).IsEquivalentTo([(ToastKind.Success, "Session saved")]);
        await Assert.That(Saved(SessionPath)?.Windows[0].Tabs[0].Layout.Pane?.Cwd).IsEqualTo("/now");
    }
}

public class RecentKeeperTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "et-recent-" + Guid.NewGuid().ToString("N")[..8]);

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Test]
    public async Task RememberCommandsOffForgetsTheListInMemory()
    {
        // Arrange
        var keeper = new RecentKeeper(Path.Combine(_dir, "history.json"));
        keeper.Apply(remember: true);
        var loaded = keeper.Recent;

        // Act
        keeper.Apply(remember: false);

        // Assert
        await Assert.That(loaded).IsNotNull();
        await Assert.That(keeper.Recent).IsNull();
        await Assert.That(keeper.Forget()).IsTrue();
    }

    [Test]
    public async Task TurningItOnAgainKeepsTheListItHas()
    {
        // Arrange
        var keeper = new RecentKeeper(Path.Combine(_dir, "history.json"));
        keeper.Apply(remember: true);
        var first = keeper.Recent;

        // Act
        keeper.Apply(remember: true);

        // Assert
        await Assert.That(keeper.Recent).IsSameReferenceAs(first);
    }
}

public class ControlSocketTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "et-sock-" + Guid.NewGuid().ToString("N")[..8]);

    [After(Test)]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* gone already */ }
    }

    private string PrivateDir()
    {
        IpcPaths.EnsureDirectory(_dir);
        return _dir;
    }

    [Test]
    public async Task PanesGetTheSocketAndTheirIdOnceItRuns()
    {
        // Arrange
        var socket = new ControlSocket(() => { });
        socket.Start(PrivateDir);
        var env = new Dictionary<string, string>();

        // Act
        socket.AddEnvironment(env, tmuxEmulation: false, paneId: 7);
        string? path = socket.SocketPath;
        socket.Stop();

        // Assert
        await Assert.That(path).IsEqualTo(IpcPaths.SocketFor(_dir, Environment.ProcessId));
        await Assert.That(env[IpcPaths.SocketVariable]).IsEqualTo(path!);
        await Assert.That(env[IpcPaths.PaneVariable]).IsEqualTo("7");
        await Assert.That(socket.Running).IsFalse();
    }

    [Test]
    public async Task WithoutASocketPanesDontInheritTheLaunchingInstances()
    {
        // Arrange
        var socket = new ControlSocket(() => { });
        var env = new Dictionary<string, string> { [IpcPaths.SocketVariable] = "/elsewhere.sock", [IpcPaths.PaneVariable] = "3" };

        // Act
        socket.AddEnvironment(env, tmuxEmulation: true, paneId: 1);

        // Assert
        await Assert.That(env.ContainsKey(IpcPaths.SocketVariable)).IsFalse();
        await Assert.That(env.ContainsKey(IpcPaths.PaneVariable)).IsFalse();
        await Assert.That(socket.TmuxShimAvailable).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // the Windows shim is a hard link or a copy of the executable, not a symlink
    public async Task TheTmuxShimLinksToThisExecutableUntilTheSocketStops()
    {
        // Arrange
        var socket = new ControlSocket(() => { });
        socket.Start(PrivateDir);
        string link = Path.Combine(TmuxEnvironment.ShimDirectory(_dir, Environment.ProcessId), TmuxEnvironment.ShimFileName);
        var env = new Dictionary<string, string>();

        // Act
        socket.AddEnvironment(env, tmuxEmulation: true, paneId: 4);
        string? target = new FileInfo(link).LinkTarget;
        bool available = socket.TmuxShimAvailable;
        socket.Stop();

        // Assert
        await Assert.That(available).IsTrue();
        await Assert.That(target).IsEqualTo(Environment.ProcessPath);
        await Assert.That(env.ContainsKey("TMUX")).IsTrue();
        await Assert.That(Directory.Exists(Path.GetDirectoryName(link))).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task AShimLeftBehindByAGoneInstanceIsSweptAway()
    {
        // Arrange
        string stale = TmuxEnvironment.ShimDirectory(PrivateDir(), int.MaxValue - 1); // no such process
        Directory.CreateDirectory(stale);
        var shim = new TmuxShimDirectory();

        // Act
        shim.Start(_dir);
        shim.Stop();

        // Assert
        await Assert.That(Directory.Exists(stale)).IsFalse();
    }

    [Test]
    public async Task ALaunchAsksForAWindowWithWhatTheInstanceMustMatch()
    {
        // Arrange
        ConfigOverrides.TryParse("font_size=14", out var o, out _);

        // Act
        var request = LaunchForwarder.Request(["htop"], "/work", "/cfg/erikterm.toml", [o!], "dropdown");
        var bare = LaunchForwarder.Request(null, "/work", "/cfg/erikterm.toml", [], null);

        // Assert
        await Assert.That(request.Command).IsEqualTo("new-window");
        await Assert.That(request.Argv).IsEquivalentTo(["htop"]);
        await Assert.That((request.Cwd, request.ConfigFile, request.WindowClass)).IsEqualTo(("/work", "/cfg/erikterm.toml", "dropdown"));
        await Assert.That(request.Overrides).IsEquivalentTo([o!.Text]);
        await Assert.That(bare.Overrides).IsNull();
    }
}

public class ProcessConfigTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "et-config-" + Guid.NewGuid().ToString("N")[..8]);
    private string ConfigPath => Path.Combine(_dir, "erikterm.toml");

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class Owner : IConfigOwner
    {
        public int Wakes, Applied;
        public readonly List<(ToastKind Kind, string Title)> Toasts = [];
        public readonly List<string> FontWarnings = [];

        public void Wake() => Wakes++;
        public void ApplyReloadedSettings(Settings old) => Applied++;
        public void ToastAll(ToastKind kind, string title, string? detail) => Toasts.Add((kind, title));
        public void AddFontWarnings(List<string> problems) => problems.AddRange(FontWarnings.Where(w => !problems.Contains(w)));
    }

    private ProcessConfig Loaded(string toml, Owner owner, IReadOnlyList<ConfigOverride>? overrides = null)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, toml);
        var config = new ProcessConfig(ConfigPath, overrides ?? [], owner, schemeDirectories: [Path.Combine(_dir, "colors")]); // not the user's scheme files
        config.LoadInitial();
        return config;
    }

    /// <summary>Lets the change sit out its 100 ms quiet period, then reloads.</summary>
    private static async Task ReloadAfterQuiet(ProcessConfig config)
    {
        await Task.Delay(150);
        config.ReloadIfDue();
    }

    [Test]
    public async Task AReloadAppliesTheFileToEveryWindowAndSaysSo()
    {
        // Arrange
        var owner = new Owner();
        var config = Loaded("font_size = 12\n", owner);
        File.WriteAllText(ConfigPath, "font_size = 15\n");

        // Act
        config.OnConfigChanged();
        bool pending = config.ReloadPending;
        await ReloadAfterQuiet(config);

        // Assert
        await Assert.That(pending).IsTrue();
        await Assert.That(config.Settings.FontSize).IsEqualTo(15);
        await Assert.That(owner.Applied).IsEqualTo(1);
        await Assert.That(owner.Wakes).IsEqualTo(1);
        await Assert.That(owner.Toasts).IsEquivalentTo([(ToastKind.Success, "Configuration reloaded")]);
        await Assert.That(config.ReloadPending).IsFalse();
    }

    [Test]
    public async Task AHalfSavedFileKeepsTheSettingsTheWindowsHave()
    {
        // Arrange
        var owner = new Owner();
        var config = Loaded("font_size = 12\n", owner);
        config.SchemeOverride = ColorSchemes.All[0];
        File.WriteAllText(ConfigPath, "font_size = \n");

        // Act
        config.OnConfigChanged();
        await ReloadAfterQuiet(config);

        // Assert
        await Assert.That(config.Kept).IsTrue();
        await Assert.That(config.Settings.FontSize).IsEqualTo(12);
        await Assert.That(owner.Applied).IsEqualTo(0);
        await Assert.That(config.SchemeOverride).IsNotNull(); // only a reload that takes drops the palette's theme
        await Assert.That(owner.Toasts[0].Title).StartsWith("Config not reloaded: previous settings kept");
    }

    [Test]
    public async Task ASetOverrideLastsThroughLaterReloads()
    {
        // Arrange
        var owner = new Owner();
        ConfigOverrides.TryParse("font_size=20", out var o, out _);
        var config = Loaded("font_size = 12\n", owner);

        // Act
        config.AddSessionOverride(o!);
        await ReloadAfterQuiet(config);
        double afterSet = config.Settings.FontSize;
        File.WriteAllText(ConfigPath, "font_size = 13\nline_height = 1.2\n");
        config.OnConfigChanged();
        await ReloadAfterQuiet(config);

        // Assert
        await Assert.That(afterSet).IsEqualTo(20);
        await Assert.That(config.Settings.FontSize).IsEqualTo(20);
        await Assert.That(config.Settings.LineHeight).IsEqualTo(1.2);
    }

    [Test]
    public async Task ProblemsListTheFileFirstThenTheWindowsFontWarnings()
    {
        // Arrange
        var owner = new Owner();
        owner.FontWarnings.Add("font 'Nope' not found");
        var config = Loaded("font_sise = 12\n", owner);

        // Act
        var problems = config.Problems();

        // Assert
        await Assert.That(problems.Count).IsEqualTo(2);
        await Assert.That(problems[0]).Contains("font_sise");
        await Assert.That(problems[1]).IsEqualTo("font 'Nope' not found");
    }
}
