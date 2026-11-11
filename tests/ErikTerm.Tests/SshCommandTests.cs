using ErikTerm.App;
using ErikTerm.App.Cli;
using ErikTerm.Core;

namespace ErikTerm.Tests;

/// <summary><c>erikterm ssh</c>: command construction, the per-host cache and the fallback. Nothing here connects anywhere: installs are stubs or a fake ssh script.</summary>
public class SshCommandTests
{
    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "et-ssh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static Func<string, string?> Env(string? termProgram = "erikterm") =>
        name => name == "TERM_PROGRAM" ? termProgram : null;

    // ---- Splitting the command line ------------------------------------------------------------------

    [Test]
    public async Task TheFirstWordThatIsntAnOptionIsTheDestination()
    {
        // Act
        var a = SshCommand.Split(["-p", "2222", "-i", "key", "user@host", "ls", "-la"]);

        // Assert
        await Assert.That(a.Options).IsEquivalentTo(new[] { "-p", "2222", "-i", "key" }, CollectionOrdering.Matching);
        await Assert.That(a.Destination).IsEqualTo("user@host");
        await Assert.That(a.Command).IsEquivalentTo(new[] { "ls", "-la" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("-p2222")]
    [Arguments("-oStrictHostKeyChecking=no")]
    [Arguments("-vvv")]
    public async Task OptionsThatCarryTheirValueDontTakeTheNextWord(string option)
    {
        // Act
        var a = SshCommand.Split([option, "host"]);

        // Assert
        await Assert.That(a.Destination).IsEqualTo("host");
    }

    [Test]
    public async Task AClusterEndingInAnArgumentOptionTakesTheNextWord()
    {
        // Act
        var a = SshCommand.Split(["-vp", "22", "host"]);

        // Assert
        await Assert.That(a.Destination).IsEqualTo("host");
    }

    [Test]
    public async Task DoubleDashEndsTheOptions()
    {
        // Act
        var a = SshCommand.Split(["--", "host", "-x"]);

        // Assert
        await Assert.That(a.Destination).IsEqualTo("host");
        await Assert.That(a.Command).IsEquivalentTo(new[] { "-x" });
    }

    [Test]
    public async Task NoDestinationIsNull()
    {
        // Act
        var a = SshCommand.Split(["-V"]);

        // Assert
        await Assert.That(a.Destination).IsNull();
    }

    // ---- The install connection ----------------------------------------------------------------------

    [Test]
    public async Task ConnectionOptionsAreKeptForTheInstall()
    {
        // Act
        var kept = SshCommand.InstallOptions(["-p", "2222", "-i", "key", "-J", "jump"]);

        // Assert
        await Assert.That(kept).IsEquivalentTo(new[] { "-p", "2222", "-i", "key", "-J", "jump" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("-L", "8080:localhost:80")]
    [Arguments("-R", "9090:localhost:90")]
    [Arguments("-D", "1080")]
    [Arguments("-W", "host:22")]
    [Arguments("-O", "exit")]
    public async Task ForwardsAndControlCommandsAreDroppedWithTheirValue(string option, string value)
    {
        // Act
        var kept = SshCommand.InstallOptions([option, value, "-p", "22"]);

        // Assert
        await Assert.That(kept).IsEquivalentTo(new[] { "-p", "22" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("-t")]
    [Arguments("-N")]
    [Arguments("-f")]
    [Arguments("-n")]
    [Arguments("-M")]
    public async Task FlagsThatWouldBreakTheInstallAreDropped(string flag)
    {
        // Act
        var kept = SshCommand.InstallOptions([flag]);

        // Assert
        await Assert.That(kept.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AClusterKeepsWhatItCan()
    {
        // Act
        var kept = SshCommand.InstallOptions(["-tv"]);

        // Assert
        await Assert.That(kept).IsEquivalentTo(new[] { "-v" });
    }

    [Test]
    public async Task TheInstallRunsTheScriptWithoutATtyOrForwarding()
    {
        // Arrange
        var a = SshCommand.Split(["-p", "22", "host"]);

        // Act
        var argv = SshCommand.InstallArgv("/usr/bin/ssh", a);

        // Assert
        await Assert.That(argv[0]).IsEqualTo("/usr/bin/ssh");
        await Assert.That(argv).Contains("-T");
        await Assert.That(argv).Contains("ClearAllForwardings=yes");
        await Assert.That(argv[^2]).IsEqualTo("host");
        await Assert.That(argv[^1]).StartsWith("sh -c '");
    }

    [Test]
    public async Task TheRemoteScriptCompilesIntoTheUsersTerminfo()
    {
        // Assert
        await Assert.That(SshCommand.RemoteScript).Contains("tic -x -o ~/.terminfo");
    }

    [Test]
    public async Task ACompiledEntryThatIsntFoundCountsAsFailed()
    {
        if (OperatingSystem.IsWindows()) return;
        // Arrange: tic "succeeds" but writes nothing ncurses reads (root's ~/.terminfo on Arch), and infocmp finds nothing.
        string dir = TempDir(), bin = Path.Combine(dir, "bin"), home = Path.Combine(dir, "home");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(home);
        foreach (var (name, body) in new[] { ("tic", "exit 0"), ("infocmp", "exit 1") })
        {
            File.WriteAllText(Path.Combine(bin, name), "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(Path.Combine(bin, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(SshCommand.RemoteScript);
        psi.Environment["PATH"] = bin + ":/usr/bin:/bin";
        psi.Environment["HOME"] = home;

        // Act
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardInput.Write(TermCapabilities.TerminfoSource());
        p.StandardInput.Close();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        // Assert
        await Assert.That(SshCommand.ParseInstallOutput(output)).IsEqualTo(SshCommand.InstallResult.Failed);
    }

    [Test]
    public async Task TheRemoteScriptSurvivesSingleQuoting()
    {
        // Assert
        await Assert.That(SshCommand.RemoteScript).DoesNotContain("'");
    }

    // ---- The cache key ----------------------------------------------------------------------------

    [Test]
    public async Task TheCacheKeyIsTheResolvedRemote()
    {
        // Act
        string key = SshCommand.CacheKey("alias", "user erik\nhostname example.org\nport 2222\nidentityfile ~/.ssh/x\n");

        // Assert
        await Assert.That(key).IsEqualTo("erik@example.org:2222");
    }

    [Test]
    public async Task WithoutSshGTheCacheKeyIsTheDestination()
    {
        // Act
        string key = SshCommand.CacheKey("user@host", null);

        // Assert
        await Assert.That(key).IsEqualTo("user@host");
    }

    [Test]
    public async Task ATabCantForgeACacheField()
    {
        // Act
        string key = SshCommand.CacheKey("evil\thost", null);

        // Assert
        await Assert.That(key).DoesNotContain("\t");
    }

    // ---- The cache --------------------------------------------------------------------------------

    [Test]
    public async Task AStoredResultIsFound()
    {
        // Arrange
        string dir = TempDir(), file = Path.Combine(dir, "state", "ssh-terminfo");
        SshCommand.Store(file, "host", "hash1", SshCommand.InstallResult.Installed);

        // Act
        var result = SshCommand.Lookup(file, "host", "hash1");

        // Assert
        await Assert.That(result).IsEqualTo(SshCommand.InstallResult.Installed);
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task ANewerEntryInstallsAgain()
    {
        // Arrange
        string dir = TempDir(), file = Path.Combine(dir, "ssh-terminfo");
        SshCommand.Store(file, "host", "old-hash", SshCommand.InstallResult.Installed);

        // Act
        var result = SshCommand.Lookup(file, "host", "new-hash");

        // Assert
        await Assert.That(result).IsNull();
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task AFailureIsNotRemembered()
    {
        // Arrange
        string dir = TempDir(), file = Path.Combine(dir, "ssh-terminfo");

        // Act
        SshCommand.Store(file, "host", "h", SshCommand.InstallResult.Failed);

        // Assert
        await Assert.That(SshCommand.Lookup(file, "host", "h")).IsNull();
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task AnotherResultForTheHostReplacesTheOld()
    {
        // Arrange
        string dir = TempDir(), file = Path.Combine(dir, "ssh-terminfo");
        SshCommand.Store(file, "host", "h", SshCommand.InstallResult.NoTic);

        // Act
        SshCommand.Store(file, "host", "h", SshCommand.InstallResult.Installed);

        // Assert
        await Assert.That(SshCommand.Lookup(file, "host", "h")).IsEqualTo(SshCommand.InstallResult.Installed);
        await Assert.That(File.ReadAllLines(file).Length).IsEqualTo(1);
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    [Arguments("ERIKTERM_TERMINFO_OK\n", "Installed")]
    [Arguments("motd\nERIKTERM_TERMINFO_NOTIC\n", "NoTic")]
    [Arguments("ERIKTERM_TERMINFO_FAILED\n", "Failed")]
    [Arguments("", "Failed")]
    public async Task TheInstallsOutputTellsWhatHappened(string output, string expected)
    {
        // Act
        var result = SshCommand.ParseInstallOutput(output);

        // Assert
        await Assert.That(result.ToString()).IsEqualTo(expected);
    }

    [Test]
    [Arguments("Installed", "xterm-erikterm")]
    [Arguments("NoTic", "xterm-256color")]
    [Arguments("Failed", "xterm-256color")]
    public async Task TermFollowsTheResult(string result, string expected)
    {
        // Act
        string term = SshCommand.TermFor(Enum.Parse<SshCommand.InstallResult>(result));

        // Assert
        await Assert.That(term).IsEqualTo(expected);
    }

    // ---- Putting it together --------------------------------------------------------------------

    [Test]
    public async Task OutsideEriktermItIsPlainSsh()
    {
        // Arrange
        string dir = TempDir();
        bool installed = false;

        // Act
        var plan = SshCommand.Prepare(["host"], "/bin/ssh", Env(termProgram: null), Path.Combine(dir, "c"),
            (_, _) => { installed = true; return SshCommand.InstallResult.Installed; }, _ => null);

        // Assert
        await Assert.That(plan.Term).IsNull();
        await Assert.That(installed).IsFalse();
        await Assert.That(plan.Argv).IsEquivalentTo(new[] { "/bin/ssh", "host" }, CollectionOrdering.Matching);
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task TheFirstConnectionInstallsTheEntrySource()
    {
        // Arrange
        string dir = TempDir();
        string? sent = null;

        // Act
        var plan = SshCommand.Prepare(["host"], "/bin/ssh", Env(), Path.Combine(dir, "c"),
            (_, source) => { sent = source; return SshCommand.InstallResult.Installed; }, _ => null);

        // Assert
        await Assert.That(plan.Term).IsEqualTo("xterm-erikterm");
        await Assert.That(sent).IsEqualTo(TermCapabilities.TerminfoSource());
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task TheSshItselfRunsWithYourArgumentsUntouched()
    {
        // Arrange
        string dir = TempDir();

        // Act
        var plan = SshCommand.Prepare(["-L", "80:a:80", "host", "ls"], "/bin/ssh", Env(), Path.Combine(dir, "c"),
            (_, _) => SshCommand.InstallResult.Installed, _ => null);

        // Assert
        await Assert.That(plan.Argv).IsEquivalentTo(new[] { "/bin/ssh", "-L", "80:a:80", "host", "ls" }, CollectionOrdering.Matching);
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task ASecondConnectionUsesTheCache()
    {
        // Arrange
        string dir = TempDir(), cache = Path.Combine(dir, "c");
        int installs = 0;
        SshCommand.InstallResult Install(IReadOnlyList<string> _, string __) { installs++; return SshCommand.InstallResult.Installed; }
        SshCommand.Prepare(["host"], "/bin/ssh", Env(), cache, Install, _ => null);

        // Act
        var plan = SshCommand.Prepare(["host"], "/bin/ssh", Env(), cache, Install, _ => null);

        // Assert
        await Assert.That(installs).IsEqualTo(1);
        await Assert.That(plan.Term).IsEqualTo("xterm-erikterm");
        await Assert.That(plan.Note).IsNull();
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task ARemoteWithoutTicFallsBackAndIsRemembered()
    {
        // Arrange
        string dir = TempDir(), cache = Path.Combine(dir, "c");
        int installs = 0;
        SshCommand.InstallResult Install(IReadOnlyList<string> _, string __) { installs++; return SshCommand.InstallResult.NoTic; }

        // Act
        var first = SshCommand.Prepare(["host"], "/bin/ssh", Env(), cache, Install, _ => null);
        var second = SshCommand.Prepare(["host"], "/bin/ssh", Env(), cache, Install, _ => null);

        // Assert
        await Assert.That(first.Term).IsEqualTo("xterm-256color");
        await Assert.That(second.Term).IsEqualTo("xterm-256color");
        await Assert.That(installs).IsEqualTo(1);
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task AFailedInstallFallsBackAndTriesAgainNextTime()
    {
        // Arrange
        string dir = TempDir(), cache = Path.Combine(dir, "c");
        int installs = 0;
        SshCommand.InstallResult Install(IReadOnlyList<string> _, string __) { installs++; return SshCommand.InstallResult.Failed; }

        // Act
        var first = SshCommand.Prepare(["host"], "/bin/ssh", Env(), cache, Install, _ => null);
        SshCommand.Prepare(["host"], "/bin/ssh", Env(), cache, Install, _ => null);

        // Assert
        await Assert.That(first.Term).IsEqualTo("xterm-256color");
        await Assert.That(installs).IsEqualTo(2);
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task AliasesOfOneHostShareTheCacheEntry()
    {
        // Arrange
        string dir = TempDir(), cache = Path.Combine(dir, "c");
        int installs = 0;
        SshCommand.InstallResult Install(IReadOnlyList<string> _, string __) { installs++; return SshCommand.InstallResult.Installed; }
        const string resolved = "user erik\nhostname example.org\nport 22\n";

        // Act
        SshCommand.Prepare(["alias1"], "/bin/ssh", Env(), cache, Install, _ => resolved);
        SshCommand.Prepare(["alias2"], "/bin/ssh", Env(), cache, Install, _ => resolved);

        // Assert
        await Assert.That(installs).IsEqualTo(1);
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task WithoutADestinationSshPrintsItsOwnUsage()
    {
        // Arrange
        string dir = TempDir();

        // Act
        var plan = SshCommand.Prepare(["-V"], "/bin/ssh", Env(), Path.Combine(dir, "c"), (_, _) => SshCommand.InstallResult.Installed, _ => null);

        // Assert
        await Assert.That(plan.Term).IsNull();
        await Assert.That(plan.Argv).IsEquivalentTo(new[] { "/bin/ssh", "-V" }, CollectionOrdering.Matching);
        Directory.Delete(dir, recursive: true);
    }

    // ---- A fake ssh on disk --------------------------------------------------------------------------

    private static string FakeSsh(string dir, string body)
    {
        string path = Path.Combine(dir, "ssh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Test]
    [RunOn(OS.Linux)]
    [NotInParallel(Isolation.Processes)]
    public async Task TheSourceReachesSshsStdin()
    {
        // Arrange
        string dir = TempDir(), got = Path.Combine(dir, "stdin");
        string ssh = FakeSsh(dir, $"cat > '{got}'\necho {SshCommand.MarkerInstalled}");

        // Act
        var result = SshCommand.Install([ssh, "host", "script"], "the terminfo source\n");

        // Assert
        await Assert.That(result).IsEqualTo(SshCommand.InstallResult.Installed);
        await Assert.That(File.ReadAllText(got)).IsEqualTo("the terminfo source\n");
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    [RunOn(OS.Linux)]
    [NotInParallel(Isolation.Processes)]
    public async Task ARemoteWithoutTicIsReported()
    {
        // Arrange
        string dir = TempDir();
        string ssh = FakeSsh(dir, $"cat >/dev/null\necho {SshCommand.MarkerNoTic}");

        // Act
        var result = SshCommand.Install([ssh, "host"], "source");

        // Assert
        await Assert.That(result).IsEqualTo(SshCommand.InstallResult.NoTic);
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    [RunOn(OS.Linux)]
    [NotInParallel(Isolation.Processes)]
    public async Task AConnectionThatFailsIsAFailure()
    {
        // Arrange
        string dir = TempDir();
        string ssh = FakeSsh(dir, "exit 255");

        // Act
        var result = SshCommand.Install([ssh, "host"], new string('x', 1 << 20)); // more than a pipe holds: it must not hang

        // Assert
        await Assert.That(result).IsEqualTo(SshCommand.InstallResult.Failed);
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    [RunOn(OS.Linux)]
    [NotInParallel(Isolation.Processes)]
    public async Task TheRealScriptWorksAgainstTicInASandbox()
    {
        // Arrange: the script exactly as ssh would run it remotely, under a throwaway HOME, with a fake tic
        string dir = TempDir(), home = Path.Combine(dir, "home"), bin = Path.Combine(dir, "bin"), got = Path.Combine(dir, "compiled");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(bin);
        string tic = Path.Combine(bin, "tic");
        File.WriteAllText(tic, $"#!/bin/sh\necho \"$@\" > '{got}.args'\nfor last; do :; done\ncp \"$last\" '{got}'\n");
        // and a fake infocmp that finds the entry once tic has run: the real one would look at this machine's terminfo
        string infocmp = Path.Combine(bin, "infocmp");
        File.WriteAllText(infocmp, $"#!/bin/sh\n[ -f '{got}' ]\n");
        if (!OperatingSystem.IsWindows())
            foreach (string tool in new[] { tic, infocmp }) File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string ssh = FakeSsh(dir, $"export HOME='{home}' PATH='{bin}:/usr/bin:/bin'\nexec /bin/sh -c \"$1\"");

        // Act
        var result = SshCommand.Install([ssh, SshCommand.RemoteScript], "source text\n");

        // Assert
        await Assert.That(result).IsEqualTo(SshCommand.InstallResult.Installed);
        await Assert.That(File.ReadAllText(got)).IsEqualTo("source text\n");
        await Assert.That(File.ReadAllText(got + ".args")).Contains("-x -o " + home + "/.terminfo");
        Directory.Delete(dir, recursive: true);
    }

    // ---- Dispatch -------------------------------------------------------------------------------

    [Test]
    public async Task SshIsAnEntryCommand()
    {
        // Act
        var entry = EntryCommand.Classify(["ssh", "host"], invokedAsTmux: false, "--helper");

        // Assert
        await Assert.That(entry).IsEqualTo(EntryKind.Ssh);
    }

    [Test]
    public async Task AsTmuxSshIsATmuxCommand()
    {
        // Act
        var entry = EntryCommand.Classify(["ssh"], invokedAsTmux: true, "--helper");

        // Assert
        await Assert.That(entry).IsEqualTo(EntryKind.TmuxShim);
    }
}
