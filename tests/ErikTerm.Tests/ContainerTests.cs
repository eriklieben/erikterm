using System.Diagnostics;
using ErikTerm.App;
using ErikTerm.App.Cli;
using ErikTerm.App.Containers;
using ErikTerm.App.Session;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary>
/// Dev containers: recognising a container exec in a pane, what inspect says, paths between the container and the host,
/// <c>erikterm devcontainer</c>'s command line, setup and exec, and the session entry. Nothing here needs docker or
/// podman; the shell scripts run in a real /bin/sh against temporary directories.
/// </summary>
public class ContainerTests
{
    private static readonly IReadOnlyList<ContainerMount> WorkspaceMounts =
    [
        new("/home/me/src/app", "/workspaces/app"),
        new("/home/me/src/app/data", "/data"),
    ];

    private static ContainerInfo Info(string? localFolder = "/home/me/src/app", IReadOnlyList<ContainerMount>? mounts = null) =>
        new("0123456789abcdef0123", "app_devcontainer", true, "2026-11-10T08:00:00Z", localFolder, "vscode", "/", mounts ?? WorkspaceMounts);

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "et-container-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ---- Recognising a container exec ----------------------------------------------------------------

    [Test]
    public async Task ADockerExecNamesItsContainer()
    {
        // Act
        var t = ContainerExec.Parse(["docker", "exec", "-it", "app_devcontainer", "zsh"]);

        // Assert
        await Assert.That(t).IsNotNull();
        await Assert.That(t!.Engine).IsEqualTo("docker");
        await Assert.That(t.Container).IsEqualTo("app_devcontainer");
        await Assert.That(t.ViaDevcontainerCli).IsFalse();
    }

    [Test]
    public async Task ClusteredShortOptionsTakeTheirValues()
    {
        // Act
        var t = ContainerExec.Parse(["/usr/bin/podman", "exec", "-itu", "root", "-w/src", "--env=A=1", "box", "bash", "-l"]);

        // Assert
        await Assert.That(t!.Container).IsEqualTo("box");
        await Assert.That(t.User).IsEqualTo("root");
        await Assert.That(t.Workdir).IsEqualTo("/src");
    }

    [Test]
    public async Task LongOptionsTakeTheNextWord()
    {
        // Act
        var t = ContainerExec.Parse(["docker", "container", "exec", "--interactive", "--tty", "--user", "dev", "--workdir", "/w", "-e", "X=1", "c1", "sh"]);

        // Assert
        await Assert.That(t!.Container).IsEqualTo("c1");
        await Assert.That(t.User).IsEqualTo("dev");
        await Assert.That(t.Workdir).IsEqualTo("/w");
    }

    [Test]
    [Arguments("-d")]
    [Arguments("-itd")]
    [Arguments("--detach")]
    [Arguments("--latest")]
    public async Task ADetachedExecOrOneWithoutAContainerIsntAShell(string option)
    {
        // Act
        var t = ContainerExec.Parse(["podman", "exec", option, "box", "sleep", "9"]);

        // Assert
        await Assert.That(t).IsNull();
    }

    [Test]
    public async Task GlobalOptionsThatPickTheDaemonAreKept()
    {
        // Act
        var t = ContainerExec.Parse(["docker", "--context", "remote", "--log-level", "debug", "exec", "-it", "c", "zsh"]);

        // Assert
        await Assert.That(t!.EngineOptions).IsEquivalentTo(new[] { "--context", "remote" }, CollectionOrdering.Matching);
        await Assert.That(t.Container).IsEqualTo("c");
    }

    [Test]
    [Arguments("docker", "ps")]
    [Arguments("nvim", "exec")]
    [Arguments("docker", "run")]
    public async Task OtherCommandsArentContainerShells(string program, string subcommand)
    {
        // Act
        var t = ContainerExec.Parse([program, subcommand, "-it", "x"]);

        // Assert
        await Assert.That(t).IsNull();
    }

    [Test]
    public async Task TheDevcontainerCliNamesItsFolder()
    {
        // Act
        var t = ContainerExec.Parse(["node", "/usr/lib/node_modules/@devcontainers/cli/devcontainer.js", "exec", "--workspace-folder", "/home/me/src/app", "--docker-path=podman", "zsh"]);

        // Assert
        await Assert.That(t!.ViaDevcontainerCli).IsTrue();
        await Assert.That(t.WorkspaceFolder).IsEqualTo("/home/me/src/app");
        await Assert.That(t.Engine).IsEqualTo("podman");
        await Assert.That(t.Container).IsNull();
    }

    [Test]
    public async Task TheDevcontainerCliWithAContainerIdNeedsNoFolder()
    {
        // Act
        var t = ContainerExec.Parse(["devcontainer", "exec", "--container-id", "abc", "bash"]);

        // Assert
        await Assert.That(t!.Container).IsEqualTo("abc");
        await Assert.That(t.WorkspaceFolder).IsNull();
        await Assert.That(t.Engine).IsEqualTo("docker");
    }

    [Test]
    [Arguments("docker", "compose")]
    [Arguments("podman", "compose")]
    public async Task AComposeExecNamesItsService(string engine, string compose)
    {
        // Act
        var t = ContainerExec.Parse([engine, compose, "-f", "dev.yml", "-p", "shop", "exec", "-u", "node", "--workdir=/app", "web", "bash"]);

        // Assert
        await Assert.That(t!.Engine).IsEqualTo(engine);
        await Assert.That(t.ComposeService).IsEqualTo("web");
        await Assert.That(t.ComposeProject).IsEqualTo("shop");
        await Assert.That(t.User).IsEqualTo("node");
        await Assert.That(t.Workdir).IsEqualTo("/app");
        await Assert.That(t.Container).IsNull();
    }

    [Test]
    public async Task StandaloneComposeRunsOnItsEngine()
    {
        // Act
        var dockerCompose = ContainerExec.Parse(["/usr/bin/docker-compose", "exec", "-T", "db", "psql"]);
        var podmanCompose = ContainerExec.Parse(["/usr/bin/python3", "/usr/bin/podman-compose", "exec", "web", "sh"]);

        // Assert
        await Assert.That(dockerCompose!.Engine).IsEqualTo("docker");
        await Assert.That(dockerCompose.ComposeService).IsEqualTo("db");
        await Assert.That(podmanCompose!.Engine).IsEqualTo("podman");
        await Assert.That(podmanCompose.ComposeService).IsEqualTo("web");
    }

    [Test]
    [Arguments("up")]
    [Arguments("exec -d web sleep 9")]
    [Arguments("logs -f web")]
    public async Task OtherComposeCommandsArentShells(string rest)
    {
        // Act
        var t = ContainerExec.Parse(["docker", "compose", .. rest.Split(' ')]);

        // Assert
        await Assert.That(t).IsNull();
    }

    [Test]
    public async Task AComposeExecTalksToTheProjectThatHoldsItsDirectory()
    {
        // Arrange
        ContainerInfo Web(string id, string dir) => Info() with { Id = id, ComposeWorkingDir = dir };
        var candidates = new[] { Web("a", "/home/me/shop"), Web("b", "/home/me/blog"), Web("c", "/home/me/shop/admin") };

        // Act
        var shop = ContainerInfo.PickCompose(candidates, "/home/me/shop/src");
        var admin = ContainerInfo.PickCompose(candidates, "/home/me/shop/admin");
        var elsewhere = ContainerInfo.PickCompose(candidates, "/tmp");

        // Assert
        await Assert.That(shop!.Id).IsEqualTo("a");
        await Assert.That(admin!.Id).IsEqualTo("c");
        await Assert.That(elsewhere).IsNull();
    }

    [Test]
    public async Task OneComposeContainerIsTheOne()
    {
        // Act
        var only = ContainerInfo.PickCompose([Info()], "/anywhere");

        // Assert
        await Assert.That(only).IsNotNull();
    }

    [Test]
    public async Task InspectReadsComposesProjectDirectory()
    {
        // Act
        var info = ContainerInfo.ParseInspect("""[{"Id":"x","Config":{"Labels":{"com.docker.compose.project.working_dir":"/home/me/shop"}}}]""");

        // Assert
        await Assert.That(info!.ComposeWorkingDir).IsEqualTo("/home/me/shop");
    }

    [Test]
    public async Task ErikTermsOwnCommandIsWaitedFor()
    {
        // Act
        bool byName = ContainerExec.IsDevcontainerCommand(["/usr/bin/erikterm", "devcontainer", "shell"], null);
        bool bySelf = ContainerExec.IsDevcontainerCommand(["/opt/x/et", "devcontainer"], "/opt/x/et");
        bool other = ContainerExec.IsDevcontainerCommand(["/usr/bin/erikterm", "review"], null);

        // Assert
        await Assert.That(byName).IsTrue();
        await Assert.That(bySelf).IsTrue();
        await Assert.That(other).IsFalse();
    }

    // ---- Inspect -------------------------------------------------------------------------------------------

    private const string DockerInspect = """
        [{
          "Id": "0123456789abcdef0123456789abcdef",
          "Name": "/app_devcontainer-app-1",
          "State": { "Running": true, "StartedAt": "2026-11-10T08:00:00Z" },
          "Config": {
            "WorkingDir": "",
            "Labels": {
              "devcontainer.local_folder": "/home/me/src/app",
              "devcontainer.metadata": "[{\"remoteUser\":\"root\"},{\"containerUser\":\"node\"},{\"remoteUser\":\"vscode\"}]"
            }
          },
          "Mounts": [
            { "Type": "bind", "Source": "/home/me/src/app", "Destination": "/workspaces/app" },
            { "Type": "volume", "Source": "/var/lib/docker/volumes/x/_data", "Destination": "/home/vscode/.cache" }
          ]
        }]
        """;

    [Test]
    public async Task InspectGivesTheContainerAndItsDevContainerLabels()
    {
        // Act
        var info = ContainerInfo.ParseInspect(DockerInspect);

        // Assert
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Name).IsEqualTo("app_devcontainer-app-1");
        await Assert.That(info.ShortId).IsEqualTo("0123456789ab");
        await Assert.That(info.Running).IsTrue();
        await Assert.That(info.LocalFolder).IsEqualTo("/home/me/src/app");
        await Assert.That(info.RemoteUser).IsEqualTo("vscode");
        await Assert.That(info.DisplayName).IsEqualTo("app");
    }

    [Test]
    public async Task OnlyBindMountsMapPaths()
    {
        // Act
        var info = ContainerInfo.ParseInspect(DockerInspect);

        // Assert
        await Assert.That(info!.Mounts.Count).IsEqualTo(1);
        await Assert.That(info.WorkspaceFolder).IsEqualTo("/workspaces/app");
    }

    [Test]
    [Arguments("[]")]
    [Arguments("{\"nope\": 1}")]
    [Arguments("not json")]
    public async Task InspectWithoutAContainerGivesNothing(string json)
    {
        // Act
        var info = ContainerInfo.ParseInspect(json);

        // Assert
        await Assert.That(info).IsNull();
    }

    [Test]
    public async Task TheContainerUserCountsWhenNoRemoteUserIsSet()
    {
        // Act
        string? user = ContainerInfo.RemoteUserOf("[{\"containerUser\":\"node\"}]");

        // Assert
        await Assert.That(user).IsEqualTo("node");
    }

    // ---- Paths ----------------------------------------------------------------------------------------------

    [Test]
    [Arguments("/workspaces/app/src/x.cs", "/home/me/src/app/src/x.cs")]
    [Arguments("/workspaces/app", "/home/me/src/app")]
    [Arguments("/data/rows.csv", "/home/me/src/app/data/rows.csv")]
    public async Task AContainerPathMapsThroughItsLongestMount(string inside, string host)
    {
        // Act
        string? mapped = ContainerPaths.ToHost(WorkspaceMounts, inside);

        // Assert
        await Assert.That(mapped).IsEqualTo(host);
    }

    [Test]
    [Arguments("/workspaces/application")]
    [Arguments("/etc/passwd")]
    [Arguments("relative/path")]
    public async Task APathNoMountHoldsDoesntMap(string inside)
    {
        // Act
        string? mapped = ContainerPaths.ToHost(WorkspaceMounts, inside);

        // Assert
        await Assert.That(mapped).IsNull();
    }

    [Test]
    public async Task AHostPathMapsIntoTheContainer()
    {
        // Act
        string? mapped = ContainerPaths.ToContainer(WorkspaceMounts, "/home/me/src/app/tests");

        // Assert
        await Assert.That(mapped).IsEqualTo("/workspaces/app/tests");
    }

    [Test]
    public async Task TheShellsReportCountsWhateverTheContainerCallsItself()
    {
        // Arrange
        var c = new PaneContainer(ContainerExec.Parse(["docker", "exec", "-it", "c", "zsh"])!, Info(), "file://laptop/home/me/src/app");

        // Act
        string dir = c.ContainerDirectory("file://0123456789ab/workspaces/app/src");
        string? host = c.HostDirectory("file://0123456789ab/workspaces/app/src", _ => true);

        // Assert
        await Assert.That(dir).IsEqualTo("/workspaces/app/src");
        await Assert.That(host).IsEqualTo("/home/me/src/app/src");
    }

    [Test]
    public async Task TheHostShellsLastReportIsntADirectoryInTheContainer()
    {
        // Arrange
        string hostReport = "file://laptop/home/me/elsewhere";
        var c = new PaneContainer(ContainerExec.Parse(["docker", "exec", "-it", "c", "zsh"])!, Info(), hostReport);

        // Act
        string dir = c.ContainerDirectory(hostReport);

        // Assert
        await Assert.That(dir).IsEqualTo("/workspaces/app");
    }

    [Test]
    public async Task APrintedContainerPathOpensOnTheHost()
    {
        // Arrange
        var c = new PaneContainer(ContainerExec.Parse(["docker", "exec", "-it", "c", "zsh"])!, Info(), null);

        // Act
        string absolute = c.ToHostPath("/workspaces/app/src/x.cs");
        string relative = c.ToHostPath("src/x.cs");

        // Assert
        await Assert.That(absolute).IsEqualTo("/home/me/src/app/src/x.cs");
        await Assert.That(relative).IsEqualTo("src/x.cs");
    }

    [Test]
    public async Task ASplitRunsAnotherShellInTheSameContainerAndDirectory()
    {
        // Arrange
        var c = new PaneContainer(ContainerExec.Parse(["podman", "--connection", "box", "exec", "-it", "-u", "dev", "c", "zsh"])!, Info(), null);

        // Act
        string[] argv = c.ShellCommand("/usr/bin/erikterm", "/workspaces/app/src", shellIntegration: true);

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[]
        {
            "/usr/bin/erikterm", "devcontainer", "shell", "--engine", "podman", "--engine-option", "--connection", "--engine-option", "box",
            "--container", "0123456789abcdef0123", "--workspace-folder", "/home/me/src/app", "--user", "dev",
            "--workdir", "/workspaces/app/src", "--fallback-shell",
        }, CollectionOrdering.Matching);
    }

    // ---- erikterm devcontainer --------------------------------------------------------------------------------

    [Test]
    public async Task TheCommandLineIsParsed()
    {
        // Act
        var o = DevcontainerCommand.Parse(["up", "--rebuild", "--workspace-folder", "/w", "--engine", "podman", "--engine-option", "--remote", "--", "bash", "-l"]);

        // Assert
        await Assert.That(o.Error).IsNull();
        await Assert.That(o.Mode).IsEqualTo(DevcontainerCommand.Mode.Up);
        await Assert.That(o.Rebuild).IsTrue();
        await Assert.That(o.WorkspaceFolder).IsEqualTo("/w");
        await Assert.That(o.Engine).IsEqualTo("podman");
        await Assert.That(o.EngineOptions).IsEquivalentTo(new[] { "--remote" }, CollectionOrdering.Matching);
        await Assert.That(o.Command).IsEquivalentTo(new[] { "bash", "-l" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("--rebuild")]
    [Arguments("--engine", "kubectl")]
    [Arguments("--container")]
    [Arguments("--frobnicate")]
    public async Task AWrongCommandLineIsAnError(string first, string? second = null)
    {
        // Act
        var o = DevcontainerCommand.Parse(second is null ? [first] : [first, second]);

        // Assert
        await Assert.That(o.Error).IsNotNull();
    }

    [Test]
    public async Task EngineOptionsAddUpAndTheOptionsAfterThemStillCount()
    {
        // Act
        var o = DevcontainerCommand.Parse(["--engine-option", "--context", "--engine-option", "remote", "--user", "root", "--no-shell-integration"]);

        // Assert
        await Assert.That(o.Error).IsNull();
        await Assert.That(o.EngineOptions).IsEquivalentTo(new[] { "--context", "remote" }, CollectionOrdering.Matching);
        await Assert.That(o.User).IsEqualTo("root");
        await Assert.That(o.ShellIntegration).IsFalse();
    }

    [Test]
    public async Task AnOptionWithoutItsValueSaysSo()
    {
        // Act
        var o = DevcontainerCommand.Parse(["--frobnicate", "--engine-option"]);

        // Assert
        await Assert.That(o.Error).IsEqualTo("--engine-option needs a value");
        await Assert.That(o.EngineOptions).IsEmpty();
    }

    [Test]
    public async Task ThePayloadGoesWhereOnlyRootCanWriteInTheContainer()
    {
        // Act
        string dir = DevcontainerCommand.ContainerDirectory("0123456789abcdef");

        // Assert
        await Assert.That(dir).IsEqualTo("/usr/local/share/erikterm-0123456789abcdef");
        await Assert.That(dir.StartsWith("/tmp/", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task TheSetupsOutputIsRead()
    {
        // Act
        var s = DevcontainerCommand.ParseSetupOutput("noise\nERIKTERM_TERMINFO_OK\r\nERIKTERM_INTEGRATION_OK\nERIKTERM_SHELL=/bin/zsh\nERIKTERM_HOME=/home/vscode\n");

        // Assert
        await Assert.That(s).IsEqualTo(new DevcontainerCommand.Setup(true, true, "/bin/zsh", "/home/vscode"));
    }

    [Test]
    public async Task ZshInTheContainerGetsTheIntegrationThroughItsEnvironment()
    {
        // Arrange
        var engine = new ContainerEngine("/usr/bin/docker", []);
        var setup = new DevcontainerCommand.Setup(true, true, "/usr/bin/zsh", "/home/vscode");
        var env = new Dictionary<string, string> { ["TERM"] = "xterm-erikterm", ["TERM_PROGRAM"] = "erikterm" };

        // Act
        var argv = DevcontainerCommand.ExecArgv(engine, Info(), "vscode", "/workspaces/app/src", setup, "/tmp/erikterm-abc", true, env, [], tty: true);

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[]
        {
            "/usr/bin/docker", "exec", "-it", "-u", "vscode",
            "-e", "TERM=xterm-erikterm", "-e", "TERM_PROGRAM=erikterm", "-e", "ZDOTDIR=/tmp/erikterm-abc/zsh",
            "0123456789abcdef0123", "/bin/sh", "-c", DevcontainerCommand.LaunchScript, "erikterm-shell",
            "/tmp/erikterm-abc/erikterm.zsh", "/workspaces/app/src", "/workspaces/app", "/usr/bin/zsh", "/usr/bin/zsh",
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task BashInTheContainerStartsInPosixModeWithItsHistoryKept()
    {
        // Arrange
        var engine = new ContainerEngine("podman", []);
        var setup = new DevcontainerCommand.Setup(false, true, "/bin/bash", "/root");

        // Act
        var argv = DevcontainerCommand.ExecArgv(engine, Info(), null, "/w", setup, "/tmp/erikterm-abc", true, new Dictionary<string, string>(), [], tty: true);

        // Assert
        await Assert.That(argv).Contains("ENV=/tmp/erikterm-abc/bash/erikterm-inject.bash");
        await Assert.That(argv).Contains("HISTFILE=/root/.bash_history");
        await Assert.That(argv).DoesNotContain("HOME=/root");
        await Assert.That(argv[^2..]).IsEquivalentTo(new[] { "/bin/bash", "--posix" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WithoutTheScriptsThereThePlainShellRuns()
    {
        // Arrange
        var engine = new ContainerEngine("docker", []);
        var setup = new DevcontainerCommand.Setup(false, false, "/bin/zsh", "/root");

        // Act
        var argv = DevcontainerCommand.ExecArgv(engine, Info(), null, "/w", setup, "/tmp/erikterm-abc", true, new Dictionary<string, string>(), [], tty: true);

        // Assert
        await Assert.That(argv.Any(a => a.StartsWith("ZDOTDIR=", StringComparison.Ordinal))).IsFalse();
        await Assert.That(argv[^5..]).IsEquivalentTo(new[] { "", "/w", "/workspaces/app", "/bin/zsh", "/bin/zsh" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ACommandRunsAsGivenInTheWorkdir()
    {
        // Arrange
        var engine = new ContainerEngine("docker", []);
        var setup = new DevcontainerCommand.Setup(true, true, "/bin/zsh", "/root");

        // Act
        var argv = DevcontainerCommand.ExecArgv(engine, Info(), null, "/w", setup, "/tmp/x", true, new Dictionary<string, string>(), ["ls", "-la"], tty: false);

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "docker", "exec", "-i", "-w", "/w", "0123456789abcdef0123", "ls", "-la" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("xterm-erikterm", true, "xterm-erikterm")]
    [Arguments("xterm-erikterm", false, "xterm-256color")]
    [Arguments("xterm-direct", false, "xterm-direct")]
    [Arguments(null, false, "xterm-256color")]
    public async Task TermIsErikTermsEntryOnlyWhenItIsInstalledInside(string? hostTerm, bool installed, string expected)
    {
        // Act
        var env = DevcontainerCommand.TerminalEnvironment(name => name == "TERM" ? hostTerm : null, installed);

        // Assert
        await Assert.That(env["TERM"]).IsEqualTo(expected);
        await Assert.That(env["TERM_PROGRAM"]).IsEqualTo("erikterm");
    }

    [Test]
    public async Task TheShellStartsWhereYouAreWhenAMountHoldsIt()
    {
        // Arrange
        var o = DevcontainerCommand.Parse([]);

        // Act
        string inMount = DevcontainerCommand.ShellWorkdir(o, Info(), "/home/me/src/app/tests");
        string outside = DevcontainerCommand.ShellWorkdir(o, Info(), "/home/me");

        // Assert
        await Assert.That(inMount).IsEqualTo("/workspaces/app/tests");
        await Assert.That(outside).IsEqualTo("/workspaces/app");
    }

    [Test]
    public async Task ASetupIsRememberedPerStartOfTheContainer()
    {
        // Arrange
        string cache = Path.Combine(TempDir(), "container-setup");
        var info = Info();
        var setup = new DevcontainerCommand.Setup(true, true, "/bin/zsh", "/home/vscode");
        string key = DevcontainerCommand.CacheKey("tag1", info, "vscode");
        DevcontainerCommand.Store(cache, key, setup);

        // Act
        var same = DevcontainerCommand.Lookup(cache, key);
        var restarted = DevcontainerCommand.Lookup(cache, DevcontainerCommand.CacheKey("tag1", info with { StartedAt = "later" }, "vscode"));

        // Assert
        await Assert.That(same).IsEqualTo(setup);
        await Assert.That(restarted).IsNull();
    }

    [Test]
    public async Task ANewStartOfTheContainerReplacesItsOldEntry()
    {
        // Arrange
        string cache = Path.Combine(TempDir(), "container-setup");
        var setup = new DevcontainerCommand.Setup(true, true, "/bin/zsh", "/h");
        DevcontainerCommand.Store(cache, DevcontainerCommand.CacheKey("t", Info(), null), setup);

        // Act
        DevcontainerCommand.Store(cache, DevcontainerCommand.CacheKey("t", Info() with { StartedAt = "later" }, null), setup);

        // Assert
        await Assert.That(File.ReadAllLines(cache).Length).IsEqualTo(1);
    }

    [Test]
    public async Task ThePayloadHoldsTheScriptsAndTheTerminfoSource()
    {
        // Act
        var (files, tag) = DevcontainerCommand.Payload();

        // Assert
        await Assert.That(files.Select(f => f.Path)).Contains("zsh/.zshenv");
        await Assert.That(files.Select(f => f.Path)).Contains(DevcontainerCommand.TerminfoFile);
        await Assert.That(tag.Length).IsEqualTo(16);
    }

    [Test]
    public async Task TheStagedCopyIsReadableByEveryone()
    {
        if (OperatingSystem.IsWindows()) return;
        // Arrange
        string dir = Path.Combine(TempDir(), "stage");
        var (files, _) = DevcontainerCommand.Payload();

        // Act
        bool ok = DevcontainerCommand.Stage(dir, files);

        // Assert
        await Assert.That(ok).IsTrue();
        await Assert.That(File.GetUnixFileMode(Path.Combine(dir, "zsh", ".zshenv")) & UnixFileMode.OtherRead).IsEqualTo(UnixFileMode.OtherRead);
        await Assert.That(File.GetUnixFileMode(Path.Combine(dir, "zsh")) & UnixFileMode.OtherExecute).IsEqualTo(UnixFileMode.OtherExecute);
    }

    // ---- The scripts, in a real sh -----------------------------------------------------------------------------

    private static (int Code, string Out) Sh(string script, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    [Test]
    public async Task TheSetupFindsTheShellAndTheScripts()
    {
        if (OperatingSystem.IsWindows()) return;
        // Arrange
        string root = TempDir(), home = TempDir();
        DevcontainerCommand.Stage(root, DevcontainerCommand.Payload().Files);

        // Act
        var (code, output) = Sh(DevcontainerCommand.SetupScript, ["erikterm-setup", root], new Dictionary<string, string> { ["HOME"] = home });
        var setup = DevcontainerCommand.ParseSetupOutput(output);

        // Assert
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(setup.Integration).IsTrue();
        await Assert.That(setup.Shell).IsNotNull();
        await Assert.That(setup.Home).IsEqualTo(home);
    }

    [Test]
    public async Task TheLaunchRunsTheIntegratedShellInItsDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        // Arrange
        string dir = TempDir(), marker = Path.Combine(TempDir(), "erikterm.zsh");
        File.WriteAllText(marker, "");

        // Act
        var (_, output) = Sh(DevcontainerCommand.LaunchScript, ["erikterm-shell", marker, dir, "/", "/bin/false", "/bin/sh", "-c", "echo integrated; pwd"]);

        // Assert
        await Assert.That(output).IsEqualTo($"integrated\n{dir}\n");
    }

    [Test]
    public async Task WithoutTheScriptsTheLaunchDropsTheInjectedVariables()
    {
        if (OperatingSystem.IsWindows()) return;
        // Arrange
        string env = Path.Combine(TempDir(), "show-env");
        File.WriteAllText(env, "#!/bin/sh\necho \"ZDOTDIR=${ZDOTDIR-unset}\"; pwd\n");
        File.SetUnixFileMode(env, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string fallback = TempDir();

        // Act
        var (_, output) = Sh(DevcontainerCommand.LaunchScript, ["erikterm-shell", "/nonexistent/erikterm.zsh", "/nonexistent/dir", fallback, env, "/bin/false"],
            new Dictionary<string, string> { ["ZDOTDIR"] = "/nonexistent/zsh" });

        // Assert
        await Assert.That(output).IsEqualTo($"ZDOTDIR=unset\n{fallback}\n");
    }

    // ---- Engines, folders, session, entry --------------------------------------------------------------------

    [Test]
    public async Task DockerIsPreferredThenPodman()
    {
        // Act
        var both = ContainerEngine.Find(null, null, n => "/usr/bin/" + n);
        var podmanOnly = ContainerEngine.Find(null, null, n => n == "podman" ? "/usr/bin/podman" : null);
        var unknown = ContainerEngine.Find("kubectl", null, n => "/usr/bin/" + n);

        // Assert
        await Assert.That(both!.Program).IsEqualTo("/usr/bin/docker");
        await Assert.That(podmanOnly!.Program).IsEqualTo("/usr/bin/podman");
        await Assert.That(unknown).IsNull();
    }

    [Test]
    public async Task TheWorkspaceIsTheNearestFolderWithADevContainerConfiguration()
    {
        // Arrange
        string root = TempDir();
        string deep = Path.Combine(root, "src", "lib");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(root, ".devcontainer"));
        File.WriteAllText(Path.Combine(root, ".devcontainer", "devcontainer.json"), "{}");

        // Act
        string folder = ContainerEngine.WorkspaceFolderFor(deep);

        // Assert
        await Assert.That(folder).IsEqualTo(root);
    }

    [Test]
    public async Task AConfigurationInASubfolderOfDevcontainerCounts()
    {
        // Arrange
        string root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, ".devcontainer", "python"));
        File.WriteAllText(Path.Combine(root, ".devcontainer", "python", "devcontainer.json"), "{}");

        // Act
        bool has = ContainerEngine.HasDevcontainerConfig(root);

        // Assert
        await Assert.That(has).IsTrue();
    }

    [Test]
    public async Task ASavedContainerShellOpensThereAgain()
    {
        // Arrange
        var saved = new SessionContainer { Engine = "docker", Id = "abc", WorkspaceFolder = "/home/me/src/app", Workdir = "/workspaces/app/src" };
        var file = new SessionFile { Windows = [new SessionWindow { Tabs = [new SessionTab { Layout = new SessionNode { Pane = new SessionPane { Container = saved } } }] }] };

        // Act
        var back = SessionJson.Deserialize(SessionJson.Serialize(file), out _)!.Windows[0].Tabs[0].Layout.Pane!.Container!;
        string[]? argv = back.Command("/usr/bin/erikterm", shellIntegration: true);

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[]
        {
            "/usr/bin/erikterm", "devcontainer", "shell", "--engine", "docker", "--container", "abc",
            "--workspace-folder", "/home/me/src/app", "--workdir", "/workspaces/app/src", "--fallback-shell",
        }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("rm", "abc")]
    [Arguments("docker", "")]
    [Arguments("docker", "--privileged")]
    [Arguments("docker", "abc\nrm -rf ~")]
    public async Task AnEditedSessionEntryThatIsntSafeIsDropped(string engine, string id)
    {
        // Arrange
        var saved = new SessionContainer { Engine = engine, Id = id };

        // Act
        string[]? argv = saved.Command("/usr/bin/erikterm", shellIntegration: true);

        // Assert
        await Assert.That(argv).IsNull();
    }

    [Test]
    public async Task DevcontainerIsACommandOfItsOwn()
    {
        // Act
        var kind = EntryCommand.Classify(["devcontainer", "up"], invokedAsTmux: false, "--spawn-helper");

        // Assert
        await Assert.That(kind).IsEqualTo(EntryKind.Devcontainer);
    }
}
