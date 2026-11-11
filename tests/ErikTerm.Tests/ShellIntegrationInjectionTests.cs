using System.Text;
using System.Text.RegularExpressions;
using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.Config;
using static ErikTerm.App.Integrations.ShellIntegration;

namespace ErikTerm.Tests;

/// <summary>
/// shell_integration = "auto": which shells are recognised, the environment and argv given to each, extracting the
/// embedded scripts, and real zsh / bash runs in a temporary HOME (skipped when the shell isn't installed).
/// </summary>
[NotInParallel(nameof(ShellIntegrationInjectionTests))]
public class ShellIntegrationInjectionTests
{
    private const string Root = "/state/erikterm/shell-integration/v1";

    // ---- Recognising shells -------------------------------------------------------------------

    [Test]
    [Arguments("/usr/bin/zsh", "Zsh")]
    [Arguments("zsh", "Zsh")]
    [Arguments("/usr/local/bin/bash", "Bash")]
    [Arguments("fish", "Fish")]
    [Arguments("pwsh", "PowerShell")]
    [Arguments("/opt/microsoft/powershell/7/pwsh", "PowerShell")]
    [Arguments("pwsh.exe", "PowerShell")]
    [Arguments("PowerShell.EXE", "PowerShell")]
    [Arguments("/bin/sh", "None")]
    [Arguments("/usr/bin/nu", "None")]
    [Arguments("", "None")]
    public async Task Recognize_GoesByTheFileName(string program, string expected)
    {
        // Arrange
        string[] argv = [program];

        // Act
        var kind = Recognize(argv);

        // Assert
        await Assert.That(kind.ToString()).IsEqualTo(expected);
    }

    [Test]
    [Arguments("zsh")]
    [Arguments("zsh -l")]
    [Arguments("zsh -il")]
    [Arguments("bash --login")]
    [Arguments("fish -i --login")]
    public async Task PlainInteractive_LoginAndInteractiveFlagsOnly(string argv)
    {
        // Arrange
        var args = argv.Split(' ');

        // Act
        bool plain = IsPlainInteractive(args);

        // Assert
        await Assert.That(plain).IsTrue();
    }

    [Test]
    [Arguments("zsh -c ls")]
    [Arguments("bash script.sh")]
    [Arguments("bash --norc")]
    [Arguments("bash --rcfile x")]
    [Arguments("zsh -f")]
    [Arguments("fish -c ls")]
    public async Task PlainInteractive_AnythingElseIsLeftAlone(string argv)
    {
        // Arrange
        var args = argv.Split(' ');

        // Act
        bool plain = IsPlainInteractive(args);

        // Assert
        await Assert.That(plain).IsFalse();
    }

    // ---- Environment and argv per shell -------------------------------------------------------

    [Test]
    public async Task Zsh_PointsZdotdirAtTheIntegration()
    {
        // Arrange
        var env = new Dictionary<string, string>();

        // Act
        string[] argv = Inject(ShellKind.Zsh, ["zsh", "-l"], env, Root);

        // Assert
        await Assert.That(env["ZDOTDIR"]).IsEqualTo(Path.Combine(Root, "zsh"));
        await Assert.That(env.ContainsKey(OrigZdotdirVariable)).IsFalse();
        await Assert.That(argv).IsEquivalentTo(new[] { "zsh", "-l" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Zsh_RemembersTheUsersZdotdir()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["ZDOTDIR"] = "/home/me/.config/zsh" };

        // Act
        Inject(ShellKind.Zsh, ["zsh"], env, Root);

        // Assert
        await Assert.That(env[OrigZdotdirVariable]).IsEqualTo("/home/me/.config/zsh");
    }

    [Test]
    public async Task Bash_StartsInPosixModeWithEnv()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["HOME"] = "/home/me" };

        // Act
        string[] argv = Inject(ShellKind.Bash, ["bash", "-l"], env, Root);

        // Assert
        await Assert.That(argv).IsEquivalentTo(new[] { "bash", "--posix", "-l" }, CollectionOrdering.Matching);
        await Assert.That(env["ENV"]).IsEqualTo(Path.Combine(Root, "bash/erikterm-inject.bash"));
    }

    [Test]
    public async Task Bash_KeepsHistoryOutOfShHistory()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["HOME"] = "/home/me" };

        // Act
        Inject(ShellKind.Bash, ["bash"], env, Root);

        // Assert
        await Assert.That(env["HISTFILE"]).IsEqualTo(Path.Combine("/home/me", ".bash_history"));
        await Assert.That(env[UnexportHistfileVariable]).IsEqualTo("1");
    }

    [Test]
    public async Task Bash_LeavesTheUsersHistfileAlone()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["HOME"] = "/home/me", ["HISTFILE"] = "/somewhere/else" };

        // Act
        Inject(ShellKind.Bash, ["bash"], env, Root);

        // Assert
        await Assert.That(env["HISTFILE"]).IsEqualTo("/somewhere/else");
        await Assert.That(env.ContainsKey(UnexportHistfileVariable)).IsFalse();
    }

    [Test]
    public async Task Bash_RemembersTheUsersEnv()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["ENV"] = "/my/env" };

        // Act
        Inject(ShellKind.Bash, ["bash"], env, Root);

        // Assert
        await Assert.That(env[OrigEnvVariable]).IsEqualTo("/my/env");
    }

    [Test]
    public async Task Fish_AddsTheDirectoryAndKeepsTheDefaults()
    {
        // Arrange
        var env = new Dictionary<string, string>();

        // Act
        Inject(ShellKind.Fish, ["fish"], env, Root);

        // Assert
        await Assert.That(env["XDG_DATA_DIRS"]).IsEqualTo(Path.Combine(Root, "fish-data") + ":/usr/local/share:/usr/share");
        await Assert.That(env.ContainsKey(OrigXdgDataDirsVariable)).IsFalse();
    }

    [Test]
    public async Task Fish_RemembersExistingDataDirs()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["XDG_DATA_DIRS"] = "/custom/share" };

        // Act
        Inject(ShellKind.Fish, ["fish"], env, Root);

        // Assert
        await Assert.That(env[OrigXdgDataDirsVariable]).IsEqualTo("/custom/share");
        await Assert.That(env["XDG_DATA_DIRS"]).IsEqualTo(Path.Combine(Root, "fish-data") + ":/custom/share");
    }

    [Test]
    public async Task Apply_LeavesAnUnrecognisedProgramAlone()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["PATH"] = "/bin" };
        string[] argv = ["/usr/bin/nu"];

        // Act
        string[] result = Apply(argv, env);

        // Assert
        await Assert.That(result).IsSameReferenceAs(argv);
        await Assert.That(env.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Apply_LeavesAShellWithACommandAlone()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["PATH"] = "/bin" };
        string[] argv = ["zsh", "-c", "ls"];

        // Act
        string[] result = Apply(argv, env);

        // Assert
        await Assert.That(result).IsSameReferenceAs(argv);
        await Assert.That(env.ContainsKey("ZDOTDIR")).IsFalse();
    }

    // ---- Settings -----------------------------------------------------------------------------

    [Test]
    public async Task Setting_DefaultsToAuto()
    {
        // Arrange / Act
        var settings = SettingsLoader.Parse("").Settings;

        // Assert
        await Assert.That(settings.ShellIntegration).IsEqualTo(ShellIntegrationMode.Auto);
        await Assert.That(SettingsLoader.KnownKeys.Contains("shell_integration")).IsTrue();
    }

    [Test]
    public async Task Setting_OffTurnsItOff()
    {
        // Arrange / Act
        var settings = SettingsLoader.Parse("shell_integration = \"off\"").Settings;

        // Assert
        await Assert.That(settings.ShellIntegration).IsEqualTo(ShellIntegrationMode.Off);
    }

    [Test]
    public async Task Setting_InvalidValueWarns()
    {
        // Arrange / Act
        var result = SettingsLoader.Parse("shell_integration = \"sometimes\"");

        // Assert
        await Assert.That(result.Settings.ShellIntegration).IsEqualTo(ShellIntegrationMode.Auto);
        await Assert.That(result.Diagnostics).Contains(d => d.Contains("shell_integration", StringComparison.Ordinal));
    }

    // ---- Extraction ---------------------------------------------------------------------------

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "et-si-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task Prepare_WritesEveryFile()
    {
        // Arrange
        string state = NewTempDir();
        try
        {
            // Act
            string? root = Prepare(state);

            // Assert
            await Assert.That(root).IsNotNull();
            foreach (var (_, path) in Files)
                await Assert.That(File.Exists(Path.Combine(root!, path))).IsTrue();
        }
        finally { DeleteTempDir(state); }
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task Prepare_MakesTheDirectoryPrivate()
    {
        // Arrange
        string state = NewTempDir();
        try
        {
            // Act
            string root = Prepare(state)!;

            // Assert
            await Assert.That(UnixFiles.LStat(Path.GetDirectoryName(root)!)!.Value.Permissions).IsEqualTo(0x1C0); // 0700
            await Assert.That(UnixFiles.LStat(root)!.Value.Permissions).IsEqualTo(0x1C0);
            await Assert.That(UnixFiles.LStat(Path.Combine(root, "zsh"))!.Value.Permissions).IsEqualTo(0x1C0);
            await Assert.That(UnixFiles.LStat(Path.Combine(root, "zsh/.zshenv"))!.Value.Permissions).IsEqualTo(0x180); // 0600
        }
        finally { DeleteTempDir(state); }
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task Prepare_TightensADirectoryLeftOpen()
    {
        // Arrange
        string state = NewTempDir();
        try
        {
            string root = Prepare(state)!;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, (UnixFileMode)0x1FF);

            // Act
            string? again = Prepare(state);

            // Assert
            await Assert.That(again).IsEqualTo(root);
            await Assert.That(UnixFiles.LStat(root)!.Value.Permissions).IsEqualTo(0x1C0);
        }
        finally { DeleteTempDir(state); }
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task Prepare_ReplacesAChangedFile()
    {
        // Arrange
        string state = NewTempDir();
        try
        {
            string root = Prepare(state)!;
            string script = Path.Combine(root, "erikterm.zsh");
            string original = File.ReadAllText(script);
            File.WriteAllText(script, "# tampered with");

            // Act
            Prepare(state);

            // Assert
            await Assert.That(File.ReadAllText(script)).IsEqualTo(original);
        }
        finally { DeleteTempDir(state); }
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task Prepare_FailsSafelyWhenTheStateDirIsAFile()
    {
        // Arrange
        string state = Path.Combine(NewTempDir(), "not-a-dir");
        File.WriteAllText(state, "");
        try
        {
            // Act
            string? root = Prepare(state);

            // Assert
            await Assert.That(root).IsNull();
        }
        finally { DeleteTempDir(Path.GetDirectoryName(state)!); }
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    public async Task Prepare_RefusesASymlinkedDirectory()
    {
        // Arrange
        string state = NewTempDir();
        string elsewhere = NewTempDir();
        try
        {
            File.CreateSymbolicLink(Path.Combine(state, "shell-integration"), elsewhere);

            // Act
            string? root = Prepare(state);

            // Assert
            await Assert.That(root).IsNull();
        }
        finally { DeleteTempDir(state); DeleteTempDir(elsewhere); }
    }

    [Test]
    public async Task Resources_ExistForEveryFile()
    {
        // Arrange
        var assembly = typeof(ShellIntegration).Assembly;

        // Act / Assert
        foreach (var (resource, _) in Files)
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            await Assert.That(stream).IsNotNull();
        }
    }

    // ---- Real shells --------------------------------------------------------------------------
    //
    // Driven through a real pty with a clean environment and a temporary HOME: nothing here reads or writes the
    // user's files, and the injection comes from the same Inject/Prepare as the app.

    private static string? FindOnPath(string name)
    {
        foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/local/bin" })
            if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    private static string RequireShell(string name)
    {
        if (FindOnPath(name) is not { } path) { Skip.Test($"{name} is not installed"); return ""; }
        return path;
    }

    private sealed class Session : IDisposable
    {
        public readonly Pty.Pty Pty;
        private readonly StringBuilder _output = new();
        private readonly Thread _reader;

        public Session(string[] argv, Dictionary<string, string> env, string cwd)
        {
            Pty = global::ErikTerm.Pty.Pty.Spawn(new global::ErikTerm.Pty.PtyOptions
            {
                Program = argv[0], Arguments = argv, Environment = env, WorkingDirectory = cwd, Columns = 200, Rows = 40,
            });
            _reader = new Thread(ReadLoop) { IsBackground = true };
            _reader.Start();
        }

        private void ReadLoop()
        {
            var buf = new byte[8192];
            int n;
            try
            {
                while ((n = Pty.Read(buf)) > 0)
                {
                    string chunk = Encoding.UTF8.GetString(buf, 0, n);
                    lock (_output) _output.Append(chunk);
                    // PSReadLine asks where the cursor is (DSR 6) and waits for the answer a terminal would give.
                    for (int i = chunk.IndexOf("\e[6n", StringComparison.Ordinal); i >= 0; i = chunk.IndexOf("\e[6n", i + 1, StringComparison.Ordinal))
                        Pty.Write("\e[1;1R"u8);
                }
            }
            catch (IOException) { }
        }

        public string Output { get { lock (_output) return _output.ToString(); } }

        public async Task<bool> WaitFor(string text, int timeoutMs = 20_000)
        {
            for (int waited = 0; waited < timeoutMs; waited += 25)
            {
                if (Output.Contains(text, StringComparison.Ordinal)) return true;
                await Task.Delay(25);
            }
            return false;
        }

        public async Task<bool> WaitForCount(string text, int count, int timeoutMs = 20_000)
        {
            for (int waited = 0; waited < timeoutMs; waited += 25)
            {
                if (Count(Output, text) >= count) return true;
                await Task.Delay(25);
            }
            return false;
        }

        public async Task<bool> WaitForMatch(Regex regex, int timeoutMs = 20_000)
        {
            for (int waited = 0; waited < timeoutMs; waited += 25)
            {
                if (regex.IsMatch(Output)) return true;
                await Task.Delay(25);
            }
            return false;
        }

        /// <summary>Sends a line and waits until the output has more of <paramref name="marker"/> than before.</summary>
        public async Task<bool> Run(string line, string marker, int timeoutMs = 20_000)
        {
            int before = Regex.Matches(Output, Regex.Escape(marker)).Count;
            Pty.Write(Encoding.UTF8.GetBytes(line + "\r"));
            for (int waited = 0; waited < timeoutMs; waited += 25)
            {
                if (Regex.Matches(Output, Regex.Escape(marker)).Count > before) return true;
                await Task.Delay(25);
            }
            return false;
        }

        public void Dispose() => Pty.Dispose();
    }

    private const string PromptMark = "\e]133;A";
    private static int Count(string haystack, string needle) => Regex.Matches(haystack, Regex.Escape(needle)).Count;

    private static Dictionary<string, string> CleanEnv(string home) => new()
    {
        ["HOME"] = home,
        ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
        ["TERM"] = "xterm-256color",
        ["TERM_PROGRAM"] = "erikterm",
        ["USER"] = "test",
        ["LANG"] = "C.UTF-8",
    };

    private static Session StartInjected(ShellKind kind, string shellPath, string state, string home, Dictionary<string, string> env, params string[] extra)
    {
        string root = Prepare(state) ?? throw new InvalidOperationException("Prepare failed");
        string[] argv = Inject(kind, [shellPath, .. extra], env, root);
        return new Session(argv, env, home);
    }

    /// <summary>A prompt, optionally with a command run, and the shell's clean exit.</summary>
    private static async Task<int> ExitCleanly(Session s)
    {
        s.Pty.Write("exit 0\r"u8);
        return await Task.Run(() => s.Pty.WaitForExit());
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealZsh_EmptyRc_MarksAndDirectory_AndExitsCleanly()
    {
        // Arrange
        string zsh = RequireShell("zsh");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            using var s = StartInjected(ShellKind.Zsh, zsh, state, home, CleanEnv(home));

            // Act
            await Assert.That(await s.WaitFor(PromptMark)).IsTrue();
            await Assert.That(await s.Run("false", "\e]133;D;1")).IsTrue();
            await Assert.That(await s.WaitFor("\e]133;A", 5_000)).IsTrue();
            int code = await ExitCleanly(s);

            // Assert
            string output = s.Output;
            await Assert.That(output).Contains("\e]133;B");
            await Assert.That(output).Contains("\e]133;C");
            await Assert.That(output).Contains("\e]7;file://");
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealZsh_UserConfigLoadsAndZdotdirIsUnset()
    {
        // Arrange
        string zsh = RequireShell("zsh");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, ".zshrc"), """
                alias hello='echo hi-from-alias'
                print -r -- "RC-ZDOTDIR=[${ZDOTDIR-unset}]" > $HOME/rc-saw
                """);
            using var s = StartInjected(ShellKind.Zsh, zsh, state, home, CleanEnv(home));
            await Assert.That(await s.WaitFor(PromptMark)).IsTrue();

            // Act
            await Assert.That(await s.Run("hello", "hi-from-alias\r\n")).IsTrue();
            await Assert.That(await s.Run("print -r -- \"RES[${ZDOTDIR-unset}]$((6*7))\"", "RES[unset]42")).IsTrue();
            await Assert.That(await s.Run("print -r -- ORIG[${ERIKTERM_ORIG_ZDOTDIR-unset}]$((6*7))", "ORIG[unset]42")).IsTrue();
            int code = await ExitCleanly(s);

            // Assert
            await Assert.That(File.ReadAllText(Path.Combine(home, "rc-saw"))).Contains("RC-ZDOTDIR=[unset]");
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealZsh_HonoursTheUsersZdotdir()
    {
        // Arrange
        string zsh = RequireShell("zsh");
        string state = NewTempDir(), home = NewTempDir();
        string userDot = Path.Combine(home, "dot");
        try
        {
            Directory.CreateDirectory(userDot);
            File.WriteAllText(Path.Combine(userDot, ".zshrc"), "print -r -- rc-from-zdotdir > $HOME/rc-saw\n");
            var env = CleanEnv(home);
            env["ZDOTDIR"] = userDot;
            using var s = StartInjected(ShellKind.Zsh, zsh, state, home, env);
            await Assert.That(await s.WaitFor(PromptMark)).IsTrue();

            // Act
            await Assert.That(await s.Run("print -r -- \"RES[$ZDOTDIR]$((6*7))\"", $"RES[{userDot}]42")).IsTrue();
            await ExitCleanly(s);

            // Assert
            await Assert.That(File.Exists(Path.Combine(home, "rc-saw"))).IsTrue();
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealZsh_RunsTheUsersZshenvFirst()
    {
        // Arrange
        string zsh = RequireShell("zsh");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, ".zshenv"), "export FROM_ZSHENV=yes\ntypeset -p ZDOTDIR > \"$HOME/env-saw\" 2>&1\n");
            File.WriteAllText(Path.Combine(home, ".zshrc"), "print -r -- $FROM_ZSHENV > $HOME/rc-saw\n");
            using var s = StartInjected(ShellKind.Zsh, zsh, state, home, CleanEnv(home));

            // Act
            await Assert.That(await s.WaitFor(PromptMark)).IsTrue();
            await ExitCleanly(s);

            // Assert
            await Assert.That(File.ReadAllText(Path.Combine(home, "env-saw"))).DoesNotContain("erikterm");
            await Assert.That(File.ReadAllText(Path.Combine(home, "rc-saw"))).Contains("yes");
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealZsh_DoesNotLeakIntoChildren()
    {
        // Arrange
        string zsh = RequireShell("zsh");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            using var s = StartInjected(ShellKind.Zsh, zsh, state, home, CleanEnv(home));
            await Assert.That(await s.WaitFor(PromptMark)).IsTrue();

            // Act
            await s.Run("/usr/bin/env > $HOME/child-env; echo DONE$((6*7))", "DONE42");
            string childEnv = File.ReadAllText(Path.Combine(home, "child-env"));
            await ExitCleanly(s);

            // Assert
            await Assert.That(childEnv).DoesNotContain("ZDOTDIR");
            await Assert.That(childEnv).DoesNotContain("ERIKTERM_ORIG");
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealZsh_AlreadySourcedIsNotDoubled()
    {
        // Arrange
        string zsh = RequireShell("zsh");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            string script = Path.Combine(Prepare(state)!, "erikterm.zsh");
            File.WriteAllText(Path.Combine(home, ".zshrc"), $"source {script}\n");
            using var s = StartInjected(ShellKind.Zsh, zsh, state, home, CleanEnv(home));

            // Act
            await Assert.That(await s.WaitFor(PromptMark)).IsTrue();
            await Assert.That(await s.Run("echo DONE$((6*7))", "DONE42")).IsTrue();
            await Assert.That(await s.WaitForMatch(new Regex("\e\\]133;D;0"))).IsTrue();
            await s.WaitFor("\e]133;A", 5_000);
            int code = await ExitCleanly(s);

            // Assert
            string output = s.Output;
            await Assert.That(Count(output, PromptMark)).IsEqualTo(Count(output, "\e]133;B"));
            await Assert.That(Count(output, "\e]133;D;0")).IsEqualTo(1);
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealZsh_PromptWithItsOwnMarksIsNotDoubled()
    {
        // Arrange
        string zsh = RequireShell("zsh");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, ".zshrc"), "PROMPT=$'%{\\e]133;A\\a%}my> %{\\e]133;B\\a%}'\n");
            using var s = StartInjected(ShellKind.Zsh, zsh, state, home, CleanEnv(home));

            // Act
            await Assert.That(await s.WaitFor("my> ")).IsTrue();
            await s.Run("echo DONE$((6*7))", "DONE42");
            await s.WaitFor("my> ");
            int code = await ExitCleanly(s);

            // Assert
            string output = s.Output;
            await Assert.That(Count(output, PromptMark)).IsEqualTo(Count(output, "my> "));
            await Assert.That(Count(output, "\e]133;D;")).IsEqualTo(0); // the prompt owns the marks
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealZsh_FrameworkRebuildingPs1KeepsMarksOnEveryPrompt()
    {
        // Arrange
        string zsh = RequireShell("zsh");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, ".zshrc"), """
                rebuild() { PS1='rebuilt> ' }
                precmd_functions+=(rebuild)
                """);
            using var s = StartInjected(ShellKind.Zsh, zsh, state, home, CleanEnv(home));

            // Act
            await Assert.That(await s.WaitFor("rebuilt> ")).IsTrue();
            await s.Run("true", "\e]133;C");
            await Assert.That(await s.Run("true", "\e]133;D;0")).IsTrue();
            await s.WaitFor("rebuilt> ");
            int code = await ExitCleanly(s);

            // Assert
            string output = s.Output;
            await Assert.That(Count(output, PromptMark)).IsEqualTo(Count(output, "rebuilt> "));
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealZsh_OhMyPoshWithShellIntegrationLeavesTheMarksToIt()
    {
        // Arrange
        string zsh = RequireShell("zsh");
        string omp = RequireShell("oh-my-posh");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            string config = Path.Combine(home, "omp.toml");
            File.WriteAllText(config, """
                version = 3
                final_space = true
                shell_integration = true
                [[blocks]]
                type = 'prompt'
                alignment = 'left'
                [[blocks.segments]]
                type = 'text'
                style = 'plain'
                template = 'omp'
                """);
            File.WriteAllText(Path.Combine(home, ".zshrc"), $"eval \"$({omp} init zsh --config {config})\"\n");
            var env = CleanEnv(home);
            env["PATH"] = Path.GetDirectoryName(omp)! + ":" + env["PATH"];
            using var s = StartInjected(ShellKind.Zsh, zsh, state, home, env);

            // Act
            await Assert.That(await s.WaitFor(PromptMark, 30_000)).IsTrue();
            await s.Run("echo DONE$((6*7))", "DONE42");
            await Assert.That(await s.WaitFor("\e]133;D", 5_000)).IsTrue();
            await Assert.That(await s.WaitForCount(PromptMark, 2, 5_000)).IsTrue(); // the first prompt's mark is already there
            int before = s.Output.Length;
            int code = await ExitCleanly(s);

            // Assert: oh-my-posh's marks (BEL-terminated) alone, one A per prompt and one C per command; ours end in ST.
            string output = s.Output[..before];
            await Assert.That(Count(output, "\e]133;A")).IsEqualTo(2);
            await Assert.That(Count(output, "\e]133;C")).IsEqualTo(1);
            await Assert.That(Count(output, "\e]133;D")).IsLessThanOrEqualTo(2); // one per prompt: the first as well, with no command
            await Assert.That(new Regex("\e\\]133;D[^\a]*\e\\\\").IsMatch(output)).IsFalse();
            await Assert.That(Count(output, "\e]133;A\e\\")).IsEqualTo(0);
            await Assert.That(Count(output, "\e]133;C\e\\")).IsEqualTo(0);
            await Assert.That(output).Contains("\e]7;file://"); // OSC 7 still comes from the integration
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealBash_EmptyRc_MarksAndDirectory_AndExitsCleanly()
    {
        // Arrange
        string bash = RequireShell("bash");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            using var s = StartInjected(ShellKind.Bash, bash, state, home, CleanEnv(home));

            // Act
            await Assert.That(await s.WaitFor(PromptMark)).IsTrue();
            await Assert.That(await s.Run("false", "\e]133;D;1")).IsTrue();
            await s.WaitFor(PromptMark, 5_000);
            int code = await ExitCleanly(s);

            // Assert
            string output = s.Output;
            await Assert.That(output).Contains("\e]133;B");
            await Assert.That(output).Contains("\e]133;C");
            await Assert.That(output).Contains("\e]7;file://");
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealBash_UserConfigLoads_EnvRestored_PosixOff()
    {
        // Arrange
        string bash = RequireShell("bash");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, ".bashrc"), "alias hello='echo hi-from-alias'\necho \"RC-ENV=[${ENV-unset}]\" > \"$HOME/rc-saw\"\nshopt -o posix > \"$HOME/rc-posix\"\n");
            using var s = StartInjected(ShellKind.Bash, bash, state, home, CleanEnv(home));
            await Assert.That(await s.WaitFor(PromptMark)).IsTrue();

            // Act
            await Assert.That(await s.Run("hello", "hi-from-alias\r\n")).IsTrue();
            await Assert.That(await s.Run("echo \"HF[${HISTFILE##*/}]$((6*7))\"", "HF[.bash_history]42")).IsTrue();
            await s.Run("/usr/bin/env > $HOME/child-env; echo DONE$((6*7))", "DONE42");
            int code = await ExitCleanly(s);

            // Assert
            await Assert.That(File.ReadAllText(Path.Combine(home, "rc-saw"))).Contains("RC-ENV=[unset]");
            await Assert.That(File.ReadAllText(Path.Combine(home, "rc-posix"))).Contains("off");
            string childEnv = File.ReadAllText(Path.Combine(home, "child-env"));
            await Assert.That(childEnv).DoesNotContain("ERIKTERM_");
            await Assert.That(childEnv).DoesNotContain("HISTFILE=");
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealBash_LoginShellReadsTheProfile()
    {
        // Arrange
        string bash = RequireShell("bash");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, ".bash_profile"), "touch \"$HOME/profile-read\"\n");
            File.WriteAllText(Path.Combine(home, ".bashrc"), "touch \"$HOME/bashrc-read\"\n");
            using var s = StartInjected(ShellKind.Bash, bash, state, home, CleanEnv(home), "-l");

            // Act
            await Assert.That(await s.WaitFor(PromptMark)).IsTrue();
            int code = await ExitCleanly(s);

            // Assert
            await Assert.That(File.Exists(Path.Combine(home, "profile-read"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(home, "bashrc-read"))).IsFalse();
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealBash_PosixOptionsAreUndone()
    {
        // Arrange
        string bash = RequireShell("bash");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            using var s = StartInjected(ShellKind.Bash, bash, state, home, CleanEnv(home));
            await Assert.That(await s.WaitFor(PromptMark)).IsTrue();

            // Act
            await s.Run("shopt -p inherit_errexit > $HOME/opts; set +o | grep -c posix >> $HOME/opts; echo DONE$((6*7))", "DONE42");
            int code = await ExitCleanly(s);

            // Assert
            string opts = File.ReadAllText(Path.Combine(home, "opts"));
            await Assert.That(opts).Contains("shopt -u inherit_errexit");
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealFish_ConfigLoadsAndDataDirsAreRestored()
    {
        // Arrange
        string fish = RequireShell("fish");
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            string config = Path.Combine(home, ".config", "fish");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "config.fish"), "function greet; echo hi-from-config; end\n");
            using var s = StartInjected(ShellKind.Fish, fish, state, home, CleanEnv(home));

            // Act
            await Assert.That(await s.WaitFor("\e]7;file://")).IsTrue();
            await Assert.That(await s.Run("greet", "hi-from-config")).IsTrue();
            await Assert.That(await s.Run("echo DIRS[$XDG_DATA_DIRS](math 6*7)", "DIRS[]42")).IsTrue();
            await Assert.That(await s.Run("/usr/bin/env > $HOME/child-env; echo DONE(math 6*7)", "DONE42")).IsTrue();
            int code = await ExitCleanly(s);

            // Assert
            await Assert.That(File.ReadAllText(Path.Combine(home, "child-env"))).DoesNotContain("ERIKTERM_ORIG");
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    // ---- PowerShell ---------------------------------------------------------------------------

    [Test]
    [Arguments("pwsh")]
    [Arguments("pwsh -NoLogo")]
    [Arguments("pwsh -nol")]
    [Arguments("pwsh -l")]
    [Arguments("pwsh -Login -NoLogo")]
    [Arguments("powershell.exe -NOLOGO")]
    public async Task PowerShell_PlainFlags(string argv)
    {
        // Arrange
        var args = argv.Split(' ');

        // Act
        bool plain = IsPlainInteractivePowerShell(args);

        // Assert
        await Assert.That(plain).IsTrue();
    }

    [Test]
    [Arguments("pwsh -NoProfile")]
    [Arguments("pwsh -c ls")]
    [Arguments("pwsh -Command Get-Date")]
    [Arguments("pwsh -File x.ps1")]
    [Arguments("pwsh x.ps1")]
    [Arguments("pwsh -NoExit")]
    [Arguments("pwsh -n")]
    public async Task PowerShell_AnythingElseIsLeftAlone(string argv)
    {
        // Arrange
        var args = argv.Split(' ');

        // Act
        bool plain = IsPlainInteractivePowerShell(args);

        // Assert
        await Assert.That(plain).IsFalse();
    }

    [Test]
    public async Task PowerShell_DotSourcesTheScriptAfterTheProfile()
    {
        // Arrange
        var env = new Dictionary<string, string>();

        // Act
        string[] argv = Inject(ShellKind.PowerShell, ["pwsh", "-NoLogo"], env, Root);

        // Assert
        string script = Path.Combine(Root, "erikterm.ps1");
        await Assert.That(argv).IsEquivalentTo(new[] { "pwsh", "-NoLogo", "-NoExit", "-Command", $"try {{ . '{script}' }} catch {{}}" });
        await Assert.That(env.Count).IsEqualTo(0);
    }

    [Test]
    public async Task PowerShell_QuotesAPathWithAQuote()
    {
        // Arrange
        string script = @"C:\Users\O'Brien\AppData\Local\erikterm\erikterm.ps1";

        // Act
        string command = PowerShellLoadCommand(script);

        // Assert
        await Assert.That(command).IsEqualTo(@"try { . 'C:\Users\O''Brien\AppData\Local\erikterm\erikterm.ps1' } catch {}");
    }

    [Test]
    public async Task Apply_LeavesPowerShellWithACommandAlone()
    {
        // Arrange
        var env = new Dictionary<string, string> { ["PATH"] = "/bin" };
        string[] argv = ["pwsh", "-Command", "Get-Date"];

        // Act
        string[] result = Apply(argv, env);

        // Assert
        await Assert.That(result).IsSameReferenceAs(argv);
    }

    /// <summary>pwsh from PATH (it is often a dotnet tool or in /opt), with what it needs to run in a clean environment.</summary>
    private static (string Path, Dictionary<string, string> Env) RequirePwsh(string home)
    {
        string? found = null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, "pwsh"))) { found = Path.Combine(dir, "pwsh"); break; }
        if (found is null) { Skip.Test("pwsh is not installed"); return ("", []); }
        var env = CleanEnv(home);
        env["PATH"] = Path.GetDirectoryName(found) + ":" + env["PATH"];
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root) env["DOTNET_ROOT"] = root;
        env["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        env["POWERSHELL_UPDATECHECK"] = "Off";
        return (found, env);
    }

    private static void WritePwshProfile(string home, string text)
    {
        string dir = Path.Combine(home, ".config", "powershell");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Microsoft.PowerShell_profile.ps1"), text);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealPwsh_MarksCommandLineDirectoryAndStatus()
    {
        // Arrange
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            var (pwsh, env) = RequirePwsh(home);
            using var s = StartInjected(ShellKind.PowerShell, pwsh, state, home, env, "-NoLogo");
            await Assert.That(await s.WaitFor("\e]133;B", 30_000)).IsTrue();

            // Act
            bool failed = await s.Run("Get-Item /nonexistent -ErrorAction SilentlyContinue", "\e]133;D;1");
            bool native = await s.Run("/bin/sh -c 'exit 3'", "\e]133;D;3");
            bool succeeded = await s.Run("Write-Output ('DONE' + 6*7)", "\e]133;D;0");
            int code = await ExitCleanly(s);

            // Assert
            string output = s.Output;
            await Assert.That(failed).IsTrue();
            await Assert.That(native).IsTrue();
            await Assert.That(succeeded).IsTrue();
            await Assert.That(output).Contains("\e]133;C;cmdline_url=Write-Output%20%28%27DONE%27%20%2B%206%2A7%29");
            await Assert.That(output).Contains("\e]7;file://");
            await Assert.That(output).Contains(Uri.EscapeDataString(Path.GetFileName(home)));
            await Assert.That(Count(output, PromptMark)).IsEqualTo(Count(output, "\e]133;B"));
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealPwsh_TheProfilesPromptRunsAndSeesTheStatus()
    {
        // Arrange
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            var (pwsh, env) = RequirePwsh(home);
            WritePwshProfile(home, "function prompt { if ($?) { 'ok> ' } else { 'FAILED> ' } }\n");
            using var s = StartInjected(ShellKind.PowerShell, pwsh, state, home, env, "-NoLogo");
            await Assert.That(await s.WaitFor("ok> ", 30_000)).IsTrue();

            // Act
            bool sawFailure = await s.Run("Get-Item /nonexistent -ErrorAction SilentlyContinue", "FAILED> ");
            bool sawSuccess = await s.Run("$null = 1", "\e]133;D;0");
            await s.WaitForCount("ok> ", 2, 10_000);
            int code = await ExitCleanly(s);

            // Assert
            await Assert.That(sawFailure).IsTrue();
            await Assert.That(sawSuccess).IsTrue();
            await Assert.That(s.Output).Contains("\e]133;A\e\\ok> \e]133;B");
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealPwsh_ALineKeptOutOfHistoryIsNotNamed()
    {
        // Arrange
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            var (pwsh, env) = RequirePwsh(home);
            WritePwshProfile(home, "Set-PSReadLineOption -AddToHistoryHandler { param($line) -not $line.StartsWith('secret') }\n");
            using var s = StartInjected(ShellKind.PowerShell, pwsh, state, home, env, "-NoLogo");
            await Assert.That(await s.WaitFor("\e]133;B", 30_000)).IsTrue();

            // Act
            bool ran = await s.Run("secretcommand", "\e]133;D;1");
            int code = await ExitCleanly(s);

            // Assert
            string output = s.Output;
            await Assert.That(ran).IsTrue();
            await Assert.That(output).Contains("\e]133;C\e\\");
            await Assert.That(output).DoesNotContain("cmdline_url=secret");
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealPwsh_PromptWithItsOwnMarksIsNotDoubled()
    {
        // Arrange
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            var (pwsh, env) = RequirePwsh(home);
            WritePwshProfile(home, "function prompt { \"$([char]27)]133;A$([char]7)my> $([char]27)]133;B$([char]7)\" }\n");
            using var s = StartInjected(ShellKind.PowerShell, pwsh, state, home, env, "-NoLogo");
            await Assert.That(await s.WaitFor("my> ", 30_000)).IsTrue();

            // Act
            await s.Run("Write-Output ('DONE' + 6*7)", "DONE42");
            await s.WaitForCount("my> ", 2, 10_000);
            int code = await ExitCleanly(s);

            // Assert
            string output = s.Output;
            await Assert.That(Count(output, PromptMark)).IsEqualTo(Count(output, "my> "));
            await Assert.That(output).DoesNotContain("\e]133;D");
            await Assert.That(output).DoesNotContain("\e]133;C");
            await Assert.That(output).Contains("\e]7;file://");
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RealPwsh_AlreadyLoadedIsNotDoubled()
    {
        // Arrange
        string state = NewTempDir(), home = NewTempDir();
        try
        {
            var (pwsh, env) = RequirePwsh(home);
            string script = Path.Combine(Prepare(state)!, "erikterm.ps1");
            WritePwshProfile(home, $". '{script}'\n");
            using var s = StartInjected(ShellKind.PowerShell, pwsh, state, home, env, "-NoLogo");
            await Assert.That(await s.WaitFor("\e]133;B", 30_000)).IsTrue();

            // Act
            await s.Run("Write-Output ('DONE' + 6*7)", "\e]133;D;0");
            await s.WaitForCount("\e]133;B", 2, 10_000);
            int code = await ExitCleanly(s);

            // Assert
            string output = s.Output;
            await Assert.That(Count(output, PromptMark)).IsEqualTo(Count(output, "\e]133;B"));
            await Assert.That(Count(output, "\e]133;D;0")).IsEqualTo(1);
            await Assert.That(Count(output, "\e]133;C;cmdline_url=Write-Output")).IsEqualTo(1);
            await Assert.That(code).IsEqualTo(0);
        }
        finally { DeleteTempDir(state); DeleteTempDir(home); }
    }
}
