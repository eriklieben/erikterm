using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ErikTerm.Pty;

namespace ErikTerm.Tests;

/// <summary>How long a process snapshot is reused (pure: runs on every platform).</summary>
public class WindowsSnapshotAgeTests
{
    private const long TakenAt = 1_000_000;

    [Test]
    [Arguments(249L, -500L, true)] // young enough, however busy the panes are
    [Arguments(250L, -500L, false)] // busy shortly before it was taken: a new program's name should show soon
    [Arguments(250L, 100L, false)] // activity since it was taken
    [Arguments(4_000L, 3_000L, false)]
    [Arguments(4_999L, -1_000L, true)] // quiet for a second before it was taken: good for a while
    [Arguments(5_000L, -1_000L, false)]
    public async Task ASnapshotLastsLongerWhenThePanesAreQuiet(long age, long activityRelativeToSnapshot, bool fresh)
    {
        // Act
#pragma warning disable CA1416 // the decision is plain arithmetic
        bool result = WindowsProcesses.IsFresh(TakenAt + age, TakenAt, TakenAt + activityRelativeToSnapshot);
#pragma warning restore CA1416

        // Assert
        await Assert.That(result).IsEqualTo(fresh);
    }
}

/// <summary>Which process of a ConPTY pane's tree is "in the foreground" (pure: runs on every platform).</summary>
public class WindowsProcessTreeTests
{
    private const long Start = 133_800_000_000_000_000; // a FILETIME in 2025
    private const int Root = 100;

    private static long At(double seconds) => Start + (long)(seconds * TimeSpan.TicksPerSecond);

    /// <summary>The snapshot's time: an hour after the root started, so every process below is settled.</summary>
    private static readonly long Now = At(3600);

    private static ProcessEntry P(int pid, int ppid, string name, double seconds, bool windowed = false) =>
        new(pid, ppid, name, At(seconds), windowed);

    private static ProcessEntry Shell => P(Root, 4, "pwsh.exe", 0);

    private static ProcessEntry? Pick(params ProcessEntry[] processes) =>
        WindowsProcessTree.Foreground(processes, Root, At(0), Now);

    [Test]
    public async Task IdleShellIsTheForeground()
    {
        // Act
        var fg = Pick(Shell, P(200, 4, "explorer.exe", 5));

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task ProgramStartedFromTheShellIsTheForeground()
    {
        // Act
        var fg = Pick(Shell, P(200, Root, "nvim.exe", 10));

        // Assert
        await Assert.That(fg?.ExeName).IsEqualTo("nvim.exe");
    }

    [Test]
    public async Task YoungestProcessWinsByCreationTimeNotByPid()
    {
        // Arrange: the younger one has the lower pid (pids are reused, not handed out in order)
        var older = P(900, Root, "ssh-agent.exe", 10);
        var younger = P(300, Root, "nvim.exe", 20);

        // Act
        var fg = Pick(Shell, older, younger);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(300);
    }

    [Test]
    [Arguments("conhost.exe")]
    [Arguments("OpenConsole.exe")]
    [Arguments("CONHOST.EXE")]
    public async Task ConsoleHostsAreLeftOut(string host)
    {
        // Arrange: a program started with a console of its own gets a conhost child, always the youngest process
        var node = P(200, Root, "node.exe", 10);

        // Act
        var fg = Pick(Shell, node, P(300, 200, host, 11));

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(200);
    }

    [Test]
    public async Task GuiProgramsStartedFromTheShellAreLeftOut()
    {
        // Act
        var fg = Pick(Shell, P(200, Root, "notepad.exe", 20, windowed: true));

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task WhatAGuiProgramStartedIsLeftOutToo()
    {
        // Arrange: Neovide's window, and the nvim it runs in a console of its own
        var neovide = P(200, Root, "neovide.exe", 20, windowed: true);

        // Act
        var fg = Pick(Shell, neovide, P(300, 200, "nvim.exe", 21));

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task OrphanOfAnEarlierOwnerOfTheShellsPidIsNotItsChild()
    {
        // Arrange: started before the shell existed, so its parent was another process that had pid 100 then
        var orphan = new ProcessEntry(200, Root, "daemon.exe", Start - TimeSpan.TicksPerMinute);

        // Act
        var fg = Pick(Shell, orphan);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task OrphanBelowADescendantIsNotItsChild()
    {
        // Arrange
        var cmd = P(200, Root, "cmd.exe", 10);
        var orphan = P(300, 200, "daemon.exe", 5); // older than the cmd whose pid it names as its parent

        // Act
        var fg = Pick(Shell, cmd, orphan);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(200);
    }

    [Test]
    public async Task ProcessWithoutACreationTimeIsLeftOut()
    {
        // Arrange: it couldn't be opened (exited since the snapshot, or protected)
        var unknown = new ProcessEntry(200, Root, "nvim.exe", 0);

        // Act
        var fg = Pick(Shell, unknown);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task RootWithAnotherCreationTimeIsAnotherProcess()
    {
        // Arrange: the pane's process exited and its pid was reused
        var reused = new ProcessEntry(Root, 4, "svchost.exe", At(50));

        // Act
        var fg = Pick(reused);

        // Assert
        await Assert.That(fg).IsNull();
    }

    [Test]
    public async Task MissingRootIsNull()
    {
        // Act
        var fg = Pick(P(200, 4, "explorer.exe", 5));

        // Assert
        await Assert.That(fg).IsNull();
    }

    [Test]
    public async Task ProgramIsReportedRatherThanWhatItStarted()
    {
        // Arrange: nvim's language server is the youngest process, but nvim is what the user ran
        var nvim = P(200, Root, "nvim.exe", 10);
        var server = P(300, 200, "rust-analyzer.exe", 20);

        // Act
        var fg = Pick(Shell, nvim, server);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(200);
    }

    [Test]
    public async Task ShellsOnTheWayAreSteppedThrough()
    {
        // Arrange: pwsh → cmd running npm's claude.cmd → node (Claude Code) → node (an MCP server)
        var cmd = P(200, Root, "cmd.exe", 10);
        var claude = P(300, 200, "node.exe", 11);
        var mcp = P(400, 300, "node.exe", 15);

        // Act
        var fg = Pick(Shell, cmd, claude, mcp);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(300);
    }

    [Test]
    public async Task InnermostShellWhenThePathIsAllShells()
    {
        // Act
        var fg = Pick(Shell, P(200, Root, "bash.exe", 10), P(300, 200, "pwsh.exe", 20));

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(300);
    }

    [Test]
    public async Task ProgramRunAsThePanesOwnProcessIsTheForeground()
    {
        // Arrange: the pane runs nvim directly (no shell); it started a language server
        var nvim = P(Root, 4, "nvim.exe", 0);

        // Act
        var fg = Pick(nvim, P(200, Root, "lua-language-server.exe", 30));

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task ProcessYoungerThanTheSettleTimeDoesNotCountYet()
    {
        // Arrange: a prompt helper that lives for a moment at every prompt
        var helper = new ProcessEntry(200, Root, "oh-my-posh.exe", Now - WindowsProcessTree.SettleTicks / 2);

        // Act
        var fg = Pick(Shell, helper);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task ChildCreatedInTheSameClockTickAsItsParentCounts()
    {
        // Arrange: creation times are coarse, so a child can carry its parent's
        var cmd = P(200, Root, "cmd.exe", 10);
        var node = P(300, 200, "node.exe", 10);

        // Act
        var fg = Pick(Shell, cmd, node);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(300);
    }

    // ---- Processes on the pane's console whose parent is gone (MSYS2's fork + exec) ----------------------

    private const int Host = 50; // the pane's conhost / OpenConsole

    private static ProcessEntry Sh => P(Root, 4, "sh.exe", 0) with { ConsoleHost = Host };

    private static ProcessEntry OnConsole(int pid, int ppid, string name, double seconds, int host = Host) =>
        P(pid, ppid, name, seconds) with { ConsoleHost = host };

    private static ProcessEntry? PickOnConsole(params ProcessEntry[] processes) =>
        WindowsProcessTree.Foreground(processes, Root, At(0), Now, Host);

    [Test]
    public async Task ProgramWhoseForkedParentExitedIsAdoptedByTheConsole()
    {
        // Arrange: sh forked (pid 150), the fork exec'd sleep and exited, so sleep's parent pid names nothing
        var sleep = OnConsole(300, 150, "sleep.exe", 10);

        // Act
        var fg = PickOnConsole(Sh, sleep);

        // Assert
        await Assert.That(fg?.ExeName).IsEqualTo("sleep.exe");
    }

    [Test]
    public async Task ProgramWhoseParentsPidWasReusedIsAdoptedToo()
    {
        // Arrange: the forked sh's pid went to a process created after sleep, which can't be sleep's parent
        var reuser = P(150, 4, "svchost.exe", 20);
        var sleep = OnConsole(300, 150, "sleep.exe", 10);

        // Act
        var fg = PickOnConsole(Sh, reuser, sleep);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(300);
    }

    [Test]
    [Arguments(0)]  // not on a console (a GUI or detached process), or it couldn't be asked
    [Arguments(77)] // another console: another pane's, or a window of its own
    public async Task OrphanOnAnotherConsoleIsNotAdopted(int host)
    {
        // Act
        var fg = PickOnConsole(Sh, OnConsole(300, 150, "sleep.exe", 10, host));

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task OrphanOlderThanThePaneIsNotAdopted()
    {
        // Arrange: on the same host pid but created before the pane's own process
        var older = OnConsole(300, 150, "sleep.exe", -10);

        // Act
        var fg = PickOnConsole(Sh, older);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task WithoutTheRootsConsoleHostNothingIsAdopted()
    {
        // Act: the root hasn't connected to its console yet (host 0), as on Linux and macOS nothing changes
        var fg = WindowsProcessTree.Foreground([Sh, OnConsole(300, 150, "sleep.exe", 10)], Root, At(0), Now, consoleHost: 0);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task AdoptedProgramIsReportedRatherThanWhatItStarted()
    {
        // Arrange: vim (adopted) started a language server, a child of a live parent with the same console
        var vim = OnConsole(300, 150, "vim.exe", 10);
        var server = OnConsole(400, 300, "clangd.exe", 20);

        // Act
        var fg = PickOnConsole(Sh, vim, server);

        // Assert
        await Assert.That(fg?.ExeName).IsEqualTo("vim.exe");
    }

    [Test]
    public async Task ChildOfALiveParentOnTheConsoleIsNotAdopted()
    {
        // Arrange: nvim is the root's own child, its server nvim's; both on the console
        var nvim = OnConsole(200, Root, "nvim.exe", 10);
        var server = OnConsole(300, 200, "lua-language-server.exe", 20);

        // Act
        var fg = PickOnConsole(Sh, nvim, server);

        // Assert: through nvim, not the server lifted up to the root
        await Assert.That(fg?.ExeName).IsEqualTo("nvim.exe");
    }

    [Test]
    public async Task AdoptedProgramYoungerThanTheSettleTimeDoesNotCountYet()
    {
        // Arrange: Git Bash's prompt runs git through forked subshells, for a moment
        var git = new ProcessEntry(300, 150, "git.exe", Now - WindowsProcessTree.SettleTicks / 2, ConsoleHost: Host);

        // Act
        var fg = PickOnConsole(Sh, git);

        // Assert
        await Assert.That(fg?.Pid).IsEqualTo(Root);
    }

    [Test]
    public async Task YoungestOfTreeAndAdoptedWins()
    {
        // Arrange: a child of the shell from earlier, and the program started since
        var agent = P(200, Root, "ssh-agent.exe", 5);
        var npm = OnConsole(300, 150, "node.exe", 30);

        // Act
        var fg = PickOnConsole(Sh, agent, npm);

        // Assert
        await Assert.That(fg?.ExeName).IsEqualTo("node.exe");
    }

    [Test]
    [Arguments("nvim.exe", "nvim")]
    [Arguments("PING.EXE", "PING")]
    [Arguments("tool", "tool")]
    [Arguments(".exe", ".exe")]
    public async Task DisplayNameDropsTheExeExtension(string exe, string expected)
    {
        // Act
        string name = WindowsProcessTree.DisplayName(exe);

        // Assert
        await Assert.That(name).IsEqualTo(expected);
    }

    [Test]
    [Arguments("pwsh.exe", true)]
    [Arguments("CMD.EXE", true)]
    [Arguments("bash.exe", true)]
    [Arguments("powershell.exe", true)]
    [Arguments("node.exe", false)]
    [Arguments("nvim.exe", false)]
    public async Task RecognisesShells(string exe, bool expected)
    {
        // Act
        bool shell = WindowsProcessTree.IsShell(exe);

        // Assert
        await Assert.That(shell).IsEqualTo(expected);
    }
}

/// <summary><see cref="WindowsCommandLine.Parse"/>: CommandLineToArgvW's rules (pure: runs on every platform).</summary>
public class WindowsCommandLineParseTests
{
    [Test]
    [Arguments("nvim notes.md", new[] { "nvim", "notes.md" })]
    [Arguments("x  a\t b", new[] { "x", "a", "b" })]                                  // runs of spaces and tabs separate
    [Arguments("\"C:\\Program Files\\Neovim\\bin\\nvim.exe\" a", new[] { @"C:\Program Files\Neovim\bin\nvim.exe", "a" })]
    [Arguments(@"C:\tools\x.exe a", new[] { @"C:\tools\x.exe", "a" })]               // argv[0]: backslashes are literal
    [Arguments("\"C:\\dir\\\"x y", new[] { @"C:\dir\", "x", "y" })]                   // argv[0] ends at its closing quote
    [Arguments("x \"a b\"", new[] { "x", "a b" })]
    [Arguments(@"x a\\b", new[] { "x", @"a\\b" })]                                    // backslashes not before a quote
    [Arguments("x a\\\"b", new[] { "x", "a\"b" })]                                    // 1 backslash + quote: a literal quote
    [Arguments("x a\\\\\\\"b", new[] { "x", "a\\\"b" })]                              // 3: one backslash and a literal quote
    [Arguments("x \"a\\\\\" b", new[] { "x", "a\\", "b" })]                           // 2 + quote: one backslash, the quote closes
    [Arguments("x \"\"", new[] { "x", "" })]                                          // an empty argument
    [Arguments("x \"a\"\"b\"", new[] { "x", "a\"b" })]                                // "" in quotes: a literal quote
    [Arguments("x \"a\"\"b c\"", new[] { "x", "a\"b", "c" })]                         // ... which ends the quoted part (the CRT would stay in it)
    [Arguments("x \"\"\"", new[] { "x", "\"" })]                                      // three quotes outside: one literal quote
    [Arguments("x \"unclosed arg", new[] { "x", "unclosed arg" })]                    // an unclosed quote runs to the end
    [Arguments(" x", new[] { "", "x" })]                                              // a leading space: an empty argv[0]
    public async Task SplitsLikeCommandLineToArgvW(string commandLine, string[] expected)
    {
        // Act
        var argv = WindowsCommandLine.Parse(commandLine);

        // Assert
        await Assert.That(argv).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("")]
    [Arguments("  \t ")]
    public async Task BlankCommandLineHasNoArguments(string commandLine)
    {
        // Act
        var argv = WindowsCommandLine.Parse(commandLine);

        // Assert
        await Assert.That(argv).IsEmpty();
    }

    [Test]
    [MethodDataSource(typeof(WindowsCommandLineTests), nameof(WindowsCommandLineTests.TrickyArguments))]
    public async Task ParsesWhatBuildWrites(string arg)
    {
        // Arrange
        string[] argv = [@"C:\Program Files\app.exe", arg, "after"];

        // Act
        var parsed = WindowsCommandLine.Parse(WindowsCommandLine.Build(argv));

        // Assert
        await Assert.That(parsed).IsEquivalentTo(argv, CollectionOrdering.Matching);
    }
}

/// <summary>The 64-bit PEB / RTL_USER_PROCESS_PARAMETERS offsets and the parsing of what is read (pure).</summary>
public class WindowsPebTests
{
    [Test]
    public async Task OffsetsMatchTheDocumentedLayout()
    {
        // Act
        var offsets = (WindowsPeb.PebBaseAddressOffset, WindowsPeb.ProcessBasicInformationSize, WindowsPeb.ProcessParametersOffset,
            WindowsPeb.FlagsOffset, WindowsPeb.CurrentDirectoryOffset, WindowsPeb.ImagePathNameOffset, WindowsPeb.CommandLineOffset,
            WindowsPeb.UnicodeStringSize, WindowsPeb.UnicodeStringBufferOffset, WindowsPeb.ParametersReadSize);

        // Assert: winternl.h (PROCESS_BASIC_INFORMATION, PEB, ImagePathName / CommandLine); ReactOS / phnt (the rest)
        await Assert.That(offsets).IsEqualTo((0x08, 0x30, 0x20, 0x08, 0x38, 0x60, 0x70, 0x10, 0x08, 0x80));
    }

    [Test]
    public async Task CurrentDirectoryAndDllPathLeadToWhereWinternlPutsImagePathName()
    {
        // Arrange: CURDIR = UNICODE_STRING DosPath + HANDLE Handle; then UNICODE_STRING DllPath
        const int CurDirSize = 0x10 + 0x08;

        // Act
        int imagePathName = WindowsPeb.CurrentDirectoryOffset + CurDirSize + WindowsPeb.UnicodeStringSize;

        // Assert
        await Assert.That(imagePathName).IsEqualTo(WindowsPeb.ImagePathNameOffset);
    }

    [Test]
    public async Task Win32ProcessStructLayoutsMatchTheSdk()
    {
        // Arrange
        bool x64 = IntPtr.Size == 8;

        // Act
        int basicSize = Unsafe.SizeOf<Win32.PROCESS_BASIC_INFORMATION>();
        int pebOffset = (int)Marshal.OffsetOf<Win32.PROCESS_BASIC_INFORMATION>(nameof(Win32.PROCESS_BASIC_INFORMATION.PebBaseAddress));
        int entrySize = Unsafe.SizeOf<Win32.PROCESSENTRY32W>();
        var (parentOffset, exeFileOffset) = EntryOffsets();

        // Assert
        await Assert.That(basicSize).IsEqualTo(x64 ? WindowsPeb.ProcessBasicInformationSize : 24);
        await Assert.That(pebOffset).IsEqualTo(x64 ? WindowsPeb.PebBaseAddressOffset : 4);
        await Assert.That(entrySize).IsEqualTo(x64 ? 568 : 556);
        await Assert.That(exeFileOffset).IsEqualTo(x64 ? 44 : 36);
        await Assert.That(parentOffset).IsEqualTo(x64 ? 32 : 24);
    }

    /// <summary>Managed offsets (what the pointer passed to Process32FirstW/NextW sees) of th32ParentProcessID and szExeFile.</summary>
    private static unsafe (int Parent, int ExeFile) EntryOffsets()
    {
        Win32.PROCESSENTRY32W e = default;
        return ((int)((byte*)&e.th32ParentProcessID - (byte*)&e), (int)((byte*)e.szExeFile - (byte*)&e));
    }

    /// <summary>An RTL_USER_PROCESS_PARAMETERS copy with one UNICODE_STRING filled in.</summary>
    private static byte[] Parameters(uint flags, int offset, ushort length, ulong buffer)
    {
        var block = new byte[WindowsPeb.ParametersReadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(WindowsPeb.FlagsOffset), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(offset), length);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(offset + 2), (ushort)(length + 2));
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(offset + WindowsPeb.UnicodeStringBufferOffset), buffer);
        return block;
    }

    [Test]
    public async Task ReadsTheCommandLineStringOfNormalizedParameters()
    {
        // Arrange
        var block = Parameters(WindowsPeb.ParamsNormalized, WindowsPeb.CommandLineOffset, 24, 0x7FF6_1234_0000);

        // Act
        var text = WindowsPeb.UnicodeString(block, WindowsPeb.CommandLineOffset, 0x1_0000);

        // Assert
        await Assert.That(text).IsEqualTo(((ulong)0x7FF6_1234_0000, 24));
    }

    [Test]
    public async Task RebasesTheBufferOfParametersNotYetNormalized()
    {
        // Arrange: before RtlNormalizeProcessParams the buffer is an offset from the structure
        var block = Parameters(0, WindowsPeb.CurrentDirectoryOffset, 8, 0x2F0);

        // Act
        var text = WindowsPeb.UnicodeString(block, WindowsPeb.CurrentDirectoryOffset, 0x5000_0000);

        // Assert
        await Assert.That(text).IsEqualTo(((ulong)0x5000_02F0, 8));
    }

    [Test]
    [Arguments((ushort)0, 0x1000UL)]  // empty
    [Arguments((ushort)7, 0x1000UL)]  // an odd byte count isn't UTF-16
    [Arguments((ushort)8, 0UL)]       // no buffer
    public async Task RejectsUnusableStrings(ushort length, ulong buffer)
    {
        // Arrange
        var block = Parameters(WindowsPeb.ParamsNormalized, WindowsPeb.CommandLineOffset, length, buffer);

        // Act
        var text = WindowsPeb.UnicodeString(block, WindowsPeb.CommandLineOffset, 0x1_0000);

        // Assert
        await Assert.That(text).IsNull();
    }

    [Test]
    public async Task RejectsATruncatedBlock()
    {
        // Act
        var text = WindowsPeb.UnicodeString(new byte[WindowsPeb.CommandLineOffset], WindowsPeb.CommandLineOffset, 0x1_0000);

        // Assert
        await Assert.That(text).IsNull();
    }

    [Test]
    [Arguments(@"C:\src\", @"C:\src")]
    [Arguments(@"C:\", @"C:\")]
    [Arguments(@"\\server\share\", @"\\server\share")]
    [Arguments(@"C:\src", @"C:\src")]
    public async Task CurrentDirectoryLosesItsTrailingBackslash(string dosPath, string expected)
    {
        // Act
        string dir = WindowsPeb.TrimDirectory(dosPath);

        // Assert
        await Assert.That(dir).IsEqualTo(expected);
    }
}

/// <summary>The subsystem of a PE image, which tells a GUI program from a console one (pure).</summary>
public class PeImageTests
{
    /// <summary>A DOS header pointing at a PE signature, a COFF header and the start of an optional header.</summary>
    private static byte[] Image(ushort magic, ushort subsystem, int peOffset = 0x80)
    {
        var image = new byte[1024];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), peOffset);
        "PE\0\0"u8.CopyTo(image.AsSpan(peOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 24), magic);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(peOffset + 24 + 68), subsystem);
        return image;
    }

    [Test]
    [Arguments((ushort)0x20B, PeImage.SubsystemWindowsGui)]
    [Arguments((ushort)0x10B, PeImage.SubsystemWindowsCui)]
    public async Task ReadsTheSubsystemOfPe32AndPe32Plus(ushort magic, ushort subsystem)
    {
        // Act
        var read = PeImage.Subsystem(Image(magic, subsystem));

        // Assert
        await Assert.That(read).IsEqualTo(subsystem);
    }

    [Test]
    public async Task ReadsAConsoleSubsystemFromARealImage()
    {
        // Arrange: this test assembly is a PE file on every platform
        byte[] file = File.ReadAllBytes(typeof(PeImageTests).Assembly.Location);

        // Act
        var read = PeImage.Subsystem(file.AsSpan(0, Math.Min(file.Length, PeImage.HeaderBytes)));

        // Assert
        await Assert.That(read).IsEqualTo(PeImage.SubsystemWindowsCui);
    }

    [Test]
    public async Task NotAnImageIsNull()
    {
        // Arrange
        var image = Image(0x20B, PeImage.SubsystemWindowsGui);
        image[0] = (byte)'#';

        // Act
        var read = PeImage.Subsystem(image);

        // Assert
        await Assert.That(read).IsNull();
    }

    [Test]
    public async Task HeaderPastTheBytesReadIsNull()
    {
        // Act
        var read = PeImage.Subsystem(Image(0x20B, PeImage.SubsystemWindowsGui, peOffset: 0x1C0).AsSpan(0, 0x1C0 + 40));

        // Assert
        await Assert.That(read).IsNull();
    }

    [Test]
    public async Task UnknownOptionalHeaderMagicIsNull()
    {
        // Act
        var read = PeImage.Subsystem(Image(0x107, PeImage.SubsystemWindowsGui));

        // Assert
        await Assert.That(read).IsNull();
    }
}

/// <summary>Foreground process, argv, image and directory from a real ConPTY pane. Skipped unless running on Windows.</summary>
[RunOn(OS.Windows)]
[NotInParallel(Isolation.Processes)]
public class WindowsForegroundProcessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    /// <summary>cmd running ping for a minute: the pane's tree is cmd → PING.EXE.</summary>
    private static Pty.Pty Ping(string? cwd = null) => Pty.Pty.Spawn(new PtyOptions
    {
        Program = "cmd.exe", Arguments = ["cmd.exe", "/d", "/c", "ping -n 60 127.0.0.1"], WorkingDirectory = cwd,
    });

    private static bool IsPing(string? name) => string.Equals(name, "ping", StringComparison.OrdinalIgnoreCase);

    [Test]
    public async Task IdleShellIsItsOwnForeground()
    {
        // Arrange
        using var pty = Pty.Pty.Spawn(new PtyOptions { Program = "cmd.exe", Arguments = ["cmd.exe", "/d", "/k"] });

        // Act
        string? name = await Eventually.Value(() => pty.ForegroundProcessName, n => n is not null, Wait);
        int pid = pty.ForegroundProcessGroup();

        // Assert
        await Assert.That(name).IsEqualTo("cmd");
        await Assert.That(pid).IsEqualTo(pty.ProcessId);
    }

    [Test]
    public async Task ProgramTheShellRunsIsTheForeground()
    {
        // Arrange
        using var pty = Ping();

        // Act
        string? name = await Eventually.Value(() => pty.ForegroundProcessName, IsPing, Wait);
        int pid = pty.ForegroundProcessGroup();

        // Assert
        await Assert.That(IsPing(name)).IsTrue().Because($"foreground: {name}");
        await Assert.That(pid).IsNotEqualTo(pty.ProcessId);
    }

    [Test]
    public async Task ForegroundArgumentsAreTheProgramsCommandLine()
    {
        // Arrange
        using var pty = Ping();
        await Eventually.True(() => IsPing(pty.ForegroundProcessName), Wait);

        // Act
        var argv = pty.ForegroundArguments();

        // Assert
        await Assert.That(argv).IsNotNull();
        await Assert.That(argv![1..]).IsEquivalentTo(["-n", "60", "127.0.0.1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ForegroundExecutableIsItsFullPath()
    {
        // Arrange
        using var pty = Ping();
        await Eventually.True(() => IsPing(pty.ForegroundProcessName), Wait);

        // Act
        string? exe = ProcessInfo.ExecutablePath(pty.ForegroundProcessGroup());

        // Assert
        await Assert.That(exe).IsNotNull();
        await Assert.That(string.Equals(exe, Path.Combine(Environment.SystemDirectory, "PING.EXE"), StringComparison.OrdinalIgnoreCase)).IsTrue().Because(exe ?? "");
    }

    [Test]
    public async Task ForegroundWorkingDirectoryIsWhereItRuns()
    {
        // Arrange
        string dir = Directory.CreateTempSubdirectory("erikterm-fg-").FullName;
        try
        {
            using var pty = Ping(dir);
            await Eventually.True(() => IsPing(pty.ForegroundProcessName), Wait);

            // Act
            string? cwd = ProcessInfo.WorkingDirectory(pty.ForegroundProcessGroup());

            // Assert
            await Assert.That(string.Equals(cwd, WindowsPeb.TrimDirectory(dir), StringComparison.OrdinalIgnoreCase)).IsTrue().Because(cwd ?? "");
        }
        finally
        {
            try { Directory.Delete(dir); } catch (IOException) { }
        }
    }

    [Test]
    public async Task ReadsOwnCommandLine()
    {
        // Act
        var argv = ProcessInfo.CommandLine(Environment.ProcessId);

        // Assert
        await Assert.That(argv).IsNotNull();
    }

    [Test]
    public async Task ReadsOwnExecutablePath()
    {
        // Act
        string? exe = ProcessInfo.ExecutablePath(Environment.ProcessId);

        // Assert
        await Assert.That(string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)).IsTrue().Because(exe ?? "");
    }

    [Test]
    public async Task ReadsOwnWorkingDirectory()
    {
        // Arrange
        string expected = WindowsPeb.TrimDirectory(Path.GetFullPath(Environment.CurrentDirectory));

        // Act
        string? cwd = ProcessInfo.WorkingDirectory(Environment.ProcessId);

        // Assert
        await Assert.That(string.Equals(cwd, expected, StringComparison.OrdinalIgnoreCase)).IsTrue().Because(cwd ?? "");
    }

    [Test]
    public async Task SystemProcessFailsSoft()
    {
        // Act: pid 4 is the System process: no PEB, and not ours to read
        var argv = ProcessInfo.CommandLine(4);
        string? cwd = ProcessInfo.WorkingDirectory(4);

        // Assert
        await Assert.That(argv).IsNull();
        await Assert.That(cwd).IsNull();
    }

    [Test]
    public async Task OwnProcessIsRunning()
    {
        // Act
        bool running = ProcessInfo.IsRunning(Environment.ProcessId);

        // Assert
        await Assert.That(running).IsTrue();
    }

    [Test]
    public async Task UnusedPidIsNotRunning()
    {
        // Act: Windows pids are multiples of 4 and nowhere near this
        bool running = ProcessInfo.IsRunning(0x7FFF_FFF0);

        // Assert
        await Assert.That(running).IsFalse();
    }
}
