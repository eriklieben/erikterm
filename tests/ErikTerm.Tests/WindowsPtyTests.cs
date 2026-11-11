using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ErikTerm.Pty;

namespace ErikTerm.Tests;

/// <summary>ConPTY end to end. Skipped unless running on Windows.</summary>
[RunOn(OS.Windows)]
[NotInParallel(Isolation.Processes)]
public class WindowsPtyTests
{
    private const string NoEof = "Read never returned 0 (EOF) after the child exited";

    /// <summary>What the child wrote, and whether Read returned 0 (EOF) before the deadline.</summary>
    private readonly record struct PtyRun(string Output, bool ReachedEof);

    private static PtyRun RunToEnd(Pty.Pty pty)
    {
        // Read on a separate thread so a ConPTY that never reports EOF fails the test instead of hanging it.
        var output = new StringBuilder();
        var reader = new Thread(() =>
        {
            var buf = new byte[4096];
            int n;
            while ((n = pty.Read(buf)) > 0) lock (output) output.Append(Encoding.UTF8.GetString(buf, 0, n));
        }) { IsBackground = true };
        reader.Start();
        bool reachedEof = reader.Join(Eventually.DefaultTimeout);
        lock (output) return new PtyRun(output.ToString(), reachedEof);
    }

    /// <summary>True if <paramref name="work"/> finishes before the deadline (a generous one: this only catches hangs).</summary>
    private static async Task<bool> FinishesInTime(Task work) =>
        await Task.WhenAny(work, Task.Delay(Eventually.DefaultTimeout)) == work;

    private static Pty.Pty Cmd(string command, IReadOnlyDictionary<string, string>? env = null) =>
        Pty.Pty.Spawn(new PtyOptions { Program = "cmd.exe", Arguments = ["cmd.exe", "/d", "/c", command], Environment = env });

    [Test]
    public async Task EchoReachesOutputAndReadReturnsZeroAfterExit()
    {
        // Arrange
        using var pty = Cmd("echo hi");

        // Act
        var run = RunToEnd(pty);
        int exitCode = pty.WaitForExit();
        bool hasExitCode = pty.TryGetExitCode(out int code);

        // Assert
        await Assert.That(run.ReachedEof).IsTrue().Because(NoEof);
        await Assert.That(run.Output).Contains("hi");
        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(hasExitCode).IsTrue();
        await Assert.That(code).IsEqualTo(0);
    }

    [Test]
    public async Task ExitCodeIsPropagated()
    {
        // Arrange
        using var pty = Cmd("exit 7");

        // Act
        var run = RunToEnd(pty);
        int exitCode = pty.WaitForExit();

        // Assert
        await Assert.That(run.ReachedEof).IsTrue().Because(NoEof);
        await Assert.That(exitCode).IsEqualTo(7);
    }

    [Test]
    public async Task EnvironmentIsPassed()
    {
        // Arrange
        var env = Pty.Pty.DefaultEnvironment();
        env["ERIKTERM_TEST_VAR"] = "hello from env";
        using var pty = Cmd("echo %ERIKTERM_TEST_VAR%", env);

        // Act
        var run = RunToEnd(pty);

        // Assert
        await Assert.That(run.ReachedEof).IsTrue().Because(NoEof);
        await Assert.That(run.Output).Contains("hello from env");
    }

    [Test]
    public async Task WorkingDirectoryIsUsed()
    {
        // Arrange
        string dir = Path.GetTempPath().TrimEnd('\\');
        using var pty = Pty.Pty.Spawn(new PtyOptions
        {
            Program = "cmd.exe", Arguments = ["cmd.exe", "/d", "/c", "cd"], WorkingDirectory = dir,
        });

        // Act
        var run = RunToEnd(pty);
        string unwrapped = run.Output.Replace("\r\n", "");

        // Assert
        await Assert.That(run.ReachedEof).IsTrue().Because(NoEof);
        await Assert.That(unwrapped).Contains(dir, StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task InputReachesChildAndResizeWorks()
    {
        // Arrange
        using var pty = Cmd("set /p x= & echo got %x%");

        // Act
        pty.Resize(100, 30);
        pty.Write("abc\r"u8);
        var run = RunToEnd(pty);

        // Assert
        await Assert.That(run.ReachedEof).IsTrue().Because(NoEof);
        await Assert.That(run.Output).Contains("got");
    }

    [Test]
    public async Task HangupEndsInteractiveShell()
    {
        // Arrange
        using var pty = Pty.Pty.Spawn(new PtyOptions { Program = "cmd.exe", Arguments = ["cmd.exe", "/d"] });

        // Act
        bool exitedBeforeHangup = pty.TryGetExitCode(out _);
        pty.Hangup();
        var run = RunToEnd(pty);
        bool exitedInTime = await FinishesInTime(Task.Run(pty.WaitForExit));
        bool hasExitCode = pty.TryGetExitCode(out _);

        // Assert
        await Assert.That(exitedBeforeHangup).IsFalse();
        await Assert.That(run.ReachedEof).IsTrue().Because(NoEof);
        await Assert.That(exitedInTime).IsTrue().Because("child survived Hangup");
        await Assert.That(hasExitCode).IsTrue();
    }

    /// <summary>True once no process has this id.</summary>
    private static bool IsGone(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    [Test]
    public async Task DisposeRightAfterSpawnEndsTheChild()
    {
        // Arrange: several, because the child outlives its console only when it was still attaching as the console closed.
        var pids = new List<int>();

        // Act
        for (int i = 0; i < 5; i++)
        {
            var pty = Pty.Pty.Spawn(new PtyOptions { Program = "cmd.exe", Arguments = ["cmd.exe", "/d"] });
            pids.Add(pty.ProcessId);
            pty.Dispose();
        }
        bool allGone = await Eventually.True(() => pids.All(IsGone));

        // Assert
        await Assert.That(allGone).IsTrue().Because("a child survived Dispose");
    }

    [Test]
    public async Task DisposeWithoutReadingDoesNotHang()
    {
        // Arrange
        var pty = Pty.Pty.Spawn(new PtyOptions { Program = "cmd.exe", Arguments = ["cmd.exe", "/d"] });

        // Act
        bool disposedInTime = await FinishesInTime(Task.Run(pty.Dispose));
        int read = pty.Read(new byte[16]);

        // Assert
        await Assert.That(disposedInTime).IsTrue().Because("Dispose blocked");
        await Assert.That(read).IsEqualTo(0);
    }

    [Test]
    public async Task PlatformSpecificMembers()
    {
        // Arrange
        using var pty = Cmd("exit 0");

        // Act
        int masterFd = pty.MasterFd;
        string? slavePath = pty.SlavePath;
        int processId = pty.ProcessId;
        var run = RunToEnd(pty);

        // Assert (the foreground process: WindowsForegroundProcessTests)
        await Assert.That(masterFd).IsEqualTo(-1);
        await Assert.That(slavePath).IsNull();
        await Assert.That(processId).IsGreaterThan(0);
        await Assert.That(run.ReachedEof).IsTrue().Because(NoEof);
    }

    [Test]
    public async Task DefaultShellExists()
    {
        // Act
        string shell = Pty.Pty.DefaultShell();
        bool exists = File.Exists(shell);

        // Assert
        await Assert.That(exists).IsTrue().Because(shell);
    }

    [Test]
    public async Task DefaultEnvironmentIsWindowsStyle()
    {
        // Act
        var env = Pty.Pty.DefaultEnvironment();
        bool hasTerm = env.ContainsKey("TERM");
        bool hasPath = env.ContainsKey("path"); // case-insensitive, like Windows
        string termProgram = env["TERM_PROGRAM"];

        // Assert
        await Assert.That(hasTerm).IsFalse();
        await Assert.That(hasPath).IsTrue();
        await Assert.That(termProgram).IsEqualTo("erikterm");
    }

    [Test]
    public async Task MissingProgramThrows()
    {
        // Act
        var act = () => Pty.Pty.Spawn(new PtyOptions { Program = "no-such-program-erikterm" });

        // Assert
        await Assert.That(act).ThrowsExactly<FileNotFoundException>();
    }
}

/// <summary>Pure Windows helpers: these run on every platform.</summary>
public class WindowsCommandLineTests
{
    /// <summary>
    /// Reference splitter with the CommandLineToArgvW / MSVC CRT rules, used to check that
    /// <see cref="WindowsCommandLine.Build"/> round-trips.
    /// </summary>
    private static List<string> Split(string cmd)
    {
        var args = new List<string>();
        int i = 0;
        // argv[0]: a leading quote runs to the next quote; otherwise up to whitespace. No escapes.
        var sb = new StringBuilder();
        if (cmd.Length > 0 && cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0) end = cmd.Length;
            sb.Append(cmd, 1, end - 1);
            i = Math.Min(end + 1, cmd.Length);
        }
        else
        {
            while (i < cmd.Length && cmd[i] is not (' ' or '\t')) sb.Append(cmd[i++]);
        }
        args.Add(sb.ToString());

        while (true)
        {
            while (i < cmd.Length && cmd[i] is ' ' or '\t') i++;
            if (i >= cmd.Length) break;
            sb.Clear();
            bool quoted = false;
            while (i < cmd.Length)
            {
                int backslashes = 0;
                while (i < cmd.Length && cmd[i] == '\\') { backslashes++; i++; }
                if (i < cmd.Length && cmd[i] == '"')
                {
                    sb.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1) sb.Append('"');
                    else quoted = !quoted;
                    i++;
                    continue;
                }
                sb.Append('\\', backslashes);
                if (i >= cmd.Length) break;
                if (!quoted && cmd[i] is ' ' or '\t') break;
                sb.Append(cmd[i++]);
            }
            args.Add(sb.ToString());
        }
        return args;
    }

    [Test]
    [Arguments(new[] { "cmd" }, "cmd")]
    [Arguments(new[] { "cmd", "/c", "echo hi" }, "cmd /c \"echo hi\"")]
    [Arguments(new[] { @"C:\Program Files\PowerShell\7\pwsh.exe", "-NoLogo" }, "\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo")]
    [Arguments(new[] { "x", "" }, "x \"\"")]
    [Arguments(new[] { "x", @"a\b" }, @"x a\b")]
    [Arguments(new[] { "x", @"a b\" }, "x \"a b\\\\\"")]
    [Arguments(new[] { "x", "say \"hi\"" }, "x \"say \\\"hi\\\"\"")]
    [Arguments(new[] { "x", @"\\server\share\" }, @"x \\server\share\")]
    [Arguments(new[] { "x", "a\\\"b" }, "x \"a\\\\\\\"b\"")]
    public async Task BuildsCommandLine(string[] argv, string expected)
    {
        // Act
        string commandLine = WindowsCommandLine.Build(argv);

        // Assert
        await Assert.That(commandLine).IsEqualTo(expected);
    }

    public static IEnumerable<string> TrickyArguments() =>
    [
        "", " ", "plain", "two words", "tab\there", "new\nline", "v\vtab", "\"", "\"\"", "a\"b", "\\", "\\\\",
        "trailing\\", "trailing\\\\", "with space\\", "\\\"", "a\\\\\"b", "C:\\Program Files\\", "%PATH%", "&|<>^",
        "unicode \u2713 \u00FC \u65E5\u672C", "\\\\?\\C:\\x y",
    ];

    [Test]
    [MethodDataSource(nameof(TrickyArguments))]
    public async Task CommandLineRoundTrips(string arg)
    {
        // Arrange
        string[] argv = ["C:\\Program Files\\app.exe", arg, "after"];

        // Act
        var split = Split(WindowsCommandLine.Build(argv));

        // Assert
        await Assert.That(split).IsEquivalentTo(argv, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EmptyProgramNameRoundTrips()
    {
        // Act
        var split = Split(WindowsCommandLine.Build(["", "x"]));

        // Assert
        await Assert.That(split).IsEquivalentTo(["", "x"], CollectionOrdering.Matching);
    }

    public static IEnumerable<Func<string[]>> UnrepresentableArgv() =>
    [
        () => [],
        () => ["a\"b"],
        () => ["a", "nul\0"],
    ];

    [Test]
    [MethodDataSource(nameof(UnrepresentableArgv))]
    public async Task RejectsUnrepresentableArgv(string[] argv)
    {
        // Act
        var act = () => WindowsCommandLine.Build(argv);

        // Assert
        await Assert.That(act).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task EnvironmentBlockIsSortedCaseInsensitivelyAndDoubleNulTerminated()
    {
        // Arrange
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Path"] = @"C:\Windows", ["windir"] = @"C:\Windows", ["ALLUSERSPROFILE"] = @"C:\ProgramData",
            ["_underscore"] = "u", ["=C:"] = @"C:\x", ["Empty"] = "",
        };

        // Act
        string block = WindowsCommandLine.BuildEnvironmentBlock(env);

        // Assert
        await Assert.That(block).IsEqualTo("=C:=C:\\x\0ALLUSERSPROFILE=C:\\ProgramData\0Empty=\0Path=C:\\Windows\0windir=C:\\Windows\0_underscore=u\0\0");
    }

    [Test]
    public async Task EmptyEnvironmentBlockIsTwoNuls()
    {
        // Act
        string block = WindowsCommandLine.BuildEnvironmentBlock(new Dictionary<string, string>());

        // Assert
        await Assert.That(block).IsEqualTo("\0\0");
    }

    public static IEnumerable<Func<Dictionary<string, string>>> BadEnvironments() =>
    [
        () => new(StringComparer.Ordinal) { ["PATH"] = "a", ["Path"] = "b" },
        () => new() { ["A=B"] = "x" },
        () => new() { [""] = "x" },
        () => new() { ["A"] = "x\0y" },
    ];

    [Test]
    [MethodDataSource(nameof(BadEnvironments))]
    public async Task EnvironmentBlockRejectsBadEntries(Dictionary<string, string> env)
    {
        // Act
        var act = () => WindowsCommandLine.BuildEnvironmentBlock(env);

        // Assert
        await Assert.That(act).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task SearchDirectoriesFollowCreateProcessOrder()
    {
        // Act
        var dirs = WindowsCommandLine.SearchDirectories(@"C:\Windows\system32", @"C:\Windows", @"C:\a; ""C:\b c"" ;;C:\d\");

        // Assert
        await Assert.That(dirs).IsEquivalentTo([@"C:\Windows\system32", @"C:\Windows", @"C:\a", @"C:\b c", @"C:\d\"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("cmd", @"C:\Windows\system32\cmd.exe")]
    [Arguments("cmd.exe", @"C:\Windows\system32\cmd.exe")]
    [Arguments("pwsh", @"C:\tools\pwsh.exe")]
    [Arguments("thing.com", @"C:\tools\thing.com")]
    [Arguments("thing", null)]                  // only .exe is appended
    [Arguments(@"D:\x\app", @"D:\x\app.exe")]
    [Arguments(@"D:\x\app.exe", @"D:\x\app.exe")]
    [Arguments(@"D:\y\app.exe", null)]          // a path is not searched for
    [Arguments("missing", null)]
    [Arguments("", null)]
    public async Task ResolvesProgramsLikeCreateProcess(string program, string? expected)
    {
        // Arrange
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Windows\system32\cmd.exe", @"C:\tools\pwsh.exe", @"C:\tools\thing.com", @"D:\x\app.exe",
        };
        string[] dirs = [@"C:\Windows\system32", @"C:\tools\"];

        // Act
        string? resolved = WindowsCommandLine.ResolveProgram(program, dirs, files.Contains);

        // Assert
        await Assert.That(resolved).IsEqualTo(expected);
    }

    [Test]
    public async Task Win32StructLayoutsMatchTheSdk()
    {
        // Arrange
        // Same natural-alignment rules on every OS, so this holds on Linux too.
        bool x64 = IntPtr.Size == 8;

        // Act
        int startupInfoSize = Unsafe.SizeOf<Win32.STARTUPINFOW>();
        int startupInfoExSize = Unsafe.SizeOf<Win32.STARTUPINFOEXW>();
        int processInformationSize = Unsafe.SizeOf<Win32.PROCESS_INFORMATION>();
        int coordSize = Unsafe.SizeOf<Win32.COORD>();
        int dwFlagsOffset = (int)Marshal.OffsetOf<Win32.STARTUPINFOW>(nameof(Win32.STARTUPINFOW.dwFlags));
        int hStdInputOffset = (int)Marshal.OffsetOf<Win32.STARTUPINFOW>(nameof(Win32.STARTUPINFOW.hStdInput));
        int attributeListOffset = (int)Marshal.OffsetOf<Win32.STARTUPINFOEXW>(nameof(Win32.STARTUPINFOEXW.lpAttributeList));

        // Assert
        await Assert.That(startupInfoSize).IsEqualTo(x64 ? 104 : 68);
        await Assert.That(startupInfoExSize).IsEqualTo(x64 ? 112 : 72);
        await Assert.That(processInformationSize).IsEqualTo(x64 ? 24 : 16);
        await Assert.That(coordSize).IsEqualTo(4);
        await Assert.That(dwFlagsOffset).IsEqualTo(x64 ? 60 : 44);
        await Assert.That(hStdInputOffset).IsEqualTo(x64 ? 80 : 56);
        await Assert.That(attributeListOffset).IsEqualTo(x64 ? 104 : 68);
    }
}
