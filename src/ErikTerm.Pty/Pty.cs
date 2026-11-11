using System.Runtime.InteropServices;

namespace ErikTerm.Pty;

public sealed class PtyOptions
{
    /// <summary>
    /// The executable. Linux: looked up on the parent's PATH like execvp. Windows: a path, or a name
    /// searched in System32, the Windows directory and then the child environment's PATH, with ".exe"
    /// appended when it has no extension (as CreateProcess does).
    /// </summary>
    public required string Program { get; init; }
    /// <summary>argv including argv[0]. Defaults to [Program]. On Windows it is turned into a command line
    /// using the CommandLineToArgvW / MSVC CRT quoting rules.</summary>
    public IReadOnlyList<string>? Arguments { get; init; }
    /// <summary>Full environment for the child. Defaults to <see cref="Pty.DefaultEnvironment"/>.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public string? WorkingDirectory { get; init; }
    public int Columns { get; init; } = 80;
    public int Rows { get; init; } = 24;
}

/// <summary>What each platform's pseudo-terminal provides; <see cref="Pty"/> forwards to one of these.</summary>
internal interface IPtyBackend : IPtySink, IDisposable
{
    int ProcessId { get; }
    int MasterFd { get; }
    string? SlavePath { get; }
    /// <summary>Unix: the foreground process group (tcgetpgrp). Windows: the foreground process's pid. -1 if unknown.</summary>
    int ForegroundProcessId { get; }
    string? ForegroundProcessName { get; }
    /// <summary>Blocking read; <paramref name="idle"/> runs while no output is waiting (see <see cref="Pty.Read(Span{byte}, Func{bool})"/>).</summary>
    int Read(Span<byte> buffer, Func<bool>? idle);
    /// <summary>Writes everything, waiting as long as the child takes (tests only; <see cref="Pty.Write"/> queues).</summary>
    void Write(ReadOnlySpan<byte> data);
    void Resize(int columns, int rows, int pixelWidth, int pixelHeight);
    int WaitForExit();
    bool TryGetExitCode(out int exitCode);
    void Hangup();
}

/// <summary>
/// A pseudo-terminal with a child process attached to it. Linux and macOS: a Unix98 pty (<see cref="UnixPty"/>).
/// Windows: a ConPTY pseudoconsole (<see cref="WindowsPty"/>).
/// </summary>
public sealed class Pty : IDisposable
{
    private readonly IPtyBackend _backend;
    private readonly PtyWriter _writer;

    private Pty(IPtyBackend backend)
    {
        _backend = backend;
        _writer = new PtyWriter(backend, $"pty-writer-{backend.ProcessId}");
    }

    /// <summary>The input queue (<see cref="Write"/>, <see cref="WriteReply"/>): how much waits, what was dropped.</summary>
    public PtyWriter Input => _writer;

    /// <summary>Linux: the master file descriptor (-1 once disposed). Windows: always -1.</summary>
    public int MasterFd => _backend.MasterFd;
    public int ProcessId => _backend.ProcessId;
    /// <summary>Linux: the slave device path, e.g. /dev/pts/3. Windows: null (ConPTY has no device node).</summary>
    public string? SlavePath => _backend.SlavePath;

    /// <summary>
    /// Name of the terminal's foreground process (e.g. "tmux", "vim"), or null if it can't be determined.
    /// Linux: <c>tcgetpgrp</c> on the master, then <c>/proc/&lt;pgid&gt;/comm</c> (at most 15 characters).
    /// macOS: <c>tcgetpgrp</c>, then libproc <c>proc_name</c> (at most 32 characters).
    /// Windows: a console has no foreground process group, so it is inferred from the processes under the pane's own
    /// (<see cref="WindowsProcessTree"/>) and named by its executable without ".exe" ("nvim", "pwsh").
    /// </summary>
    public string? ForegroundProcessName => _backend.ForegroundProcessName;

    /// <summary>The foreground process group's leader (Linux, macOS) or the foreground process (Windows); -1 if unknown.</summary>
    internal int ForegroundProcessId => _backend.ForegroundProcessId;

    /// <summary>argv[1] that runs erikterm as the macOS spawn helper (<see cref="RunSpawnHelper"/>).</summary>
    public const string SpawnHelperArgument = "--erikterm-pty-exec";

    /// <summary>
    /// macOS: the command that starts the spawn helper — normally erikterm's own path; its Main hands
    /// <see cref="SpawnHelperArgument"/> to <see cref="RunSpawnHelper"/>. Without one, children get the pty as stdio but
    /// not as their controlling terminal (no job control). Ignored elsewhere.
    /// </summary>
    public static void UseSpawnHelper(params string[] command)
    {
        if (OperatingSystem.IsWindows()) return;
        UnixPty.SpawnHelper = command.Length > 0 && command[0].Length > 0 ? command : null;
    }

    /// <summary>The helper's argv: the helper command, <see cref="SpawnHelperArgument"/>, the directory ("" = keep), the program, argv.</summary>
    internal static List<string> SpawnHelperArgv(IReadOnlyList<string> helper, string program, IReadOnlyList<string> argv, string? cwd)
    {
        var list = new List<string>(helper.Count + 3 + argv.Count);
        list.AddRange(helper);
        list.Add(SpawnHelperArgument);
        list.Add(cwd ?? "");
        list.Add(program);
        list.AddRange(argv);
        return list;
    }

    /// <summary>The spawn helper's body (macOS, in the child): <paramref name="args"/> follow <see cref="SpawnHelperArgument"/>. Returns only on failure.</summary>
    public static int RunSpawnHelper(ReadOnlySpan<string> args) =>
        OperatingSystem.IsWindows() ? 2 : UnixPty.RunHelper(args);

    /// <summary>
    /// Linux / macOS: replaces this process with <paramref name="path"/> (execve) — the same pid, terminal and signals,
    /// as a shell's <c>exec</c> does (<c>erikterm ssh</c>). Returns only on failure, with the error's message.
    /// </summary>
#pragma warning disable S6640 // builds the native argv and envp arrays for execve
    public static unsafe string ExecReplace(string path, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> environment)
#pragma warning restore S6640
    {
        if (OperatingSystem.IsWindows()) return "not supported on Windows";
        var strings = new NativeUtf8Strings();
        byte** args = (byte**)NativeMemory.AllocZeroed((nuint)(argv.Count + 1), (nuint)sizeof(nint));
        byte** envp = (byte**)NativeMemory.AllocZeroed((nuint)(environment.Count + 1), (nuint)sizeof(nint));
        try
        {
            for (int i = 0; i < argv.Count; i++) args[i] = strings.Add(argv[i]);
            int n = 0;
            foreach (var (k, v) in environment) envp[n++] = strings.Add(k + "=" + v);
            Native.execve(strings.Add(path), args, envp);
            return Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());
        }
        finally
        {
            NativeMemory.Free(args);
            NativeMemory.Free(envp);
            strings.Dispose();
        }
    }

    public static Pty Spawn(PtyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OperatingSystem.IsWindows()) return new Pty(WindowsPty.Spawn(options));
        return new Pty(UnixPty.Spawn(options));
    }

    /// <summary>
    /// The parent's environment minus other terminals' identity variables, plus COLORTERM=truecolor and
    /// TERM_PROGRAM=erikterm. Linux: TERM=xterm-256color. Windows: TERM is removed and not set — see
    /// the comment in the body. On Windows the dictionary compares keys case-insensitively, like the OS.
    /// </summary>
    public static Dictionary<string, string> DefaultEnvironment()
    {
        bool windows = OperatingSystem.IsWindows();
        var env = new Dictionary<string, string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var cmp = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
        {
            string key = (string)e.Key;
            // Don't leak the parent terminal's identity into ours.
            if (key.StartsWith("WEZTERM_", cmp) || key is "TERM_PROGRAM_VERSION" or "WINDOWID" or "VTE_VERSION")
                continue;
            if (windows && (key.Equals("WT_SESSION", cmp) || key.Equals("WT_PROFILE_ID", cmp)))
                continue;
            env[key] = (string?)e.Value ?? "";
        }
        if (windows)
        {
            // Windows programs talk to conhost (inside ConPTY), not to us, and native tools don't use
            // terminfo — so TERM describes nothing useful, and ports that do read it (vim, less, curses)
            // misbehave when it names an entry their bundled terminfo lacks. Windows Terminal doesn't set
            // it either. An inherited TERM would describe the terminal that launched us (e.g. mintty), so
            // drop it. Users can still set one via set_environment_variables (wezterm sets xterm-256color).
            env.Remove("TERM");
        }
        else
        {
            env["TERM"] = "xterm-256color";
        }
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "erikterm";
        return env;
    }

    /// <summary>
    /// The user's preferred shell. Linux: $SHELL, else /bin/sh. macOS: $SHELL, else /bin/zsh (the default shell since
    /// 10.15). Windows: pwsh.exe if it is on PATH, else Windows PowerShell (powershell.exe), else %COMSPEC%, else
    /// cmd.exe. Returns a full path on Windows.
    /// </summary>
    public static string DefaultShell()
    {
        if (OperatingSystem.IsWindows()) return WindowsPty.DefaultShell();
        if (System.Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s) return s;
        return OperatingSystem.IsMacOS() && File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh";
    }

    /// <summary>
    /// argv for the default shell: <see cref="DefaultShell"/> alone, except on macOS, where terminals start a login
    /// shell (Terminal.app, iTerm2 and wezterm do): /etc/zprofile runs path_helper and ~/.zprofile is where Homebrew's
    /// PATH goes, and an app started from the Dock or Finder has neither in its environment.
    /// </summary>
    public static string[] DefaultShellCommand() => DefaultShellCommand(DefaultShell(), OperatingSystem.IsMacOS());

    internal static string[] DefaultShellCommand(string shell, bool macOS) => macOS ? [shell, "-l"] : [shell];

    /// <summary>Blocking read of the child's output. Returns 0 once the child side is gone (EOF).</summary>
    public int Read(Span<byte> buffer) => _backend.Read(buffer, null);

    /// <summary>
    /// <see cref="Read(Span{byte})"/> that does background work while the child is quiet: once no output has come for
    /// <see cref="IdleAfterMs"/>, it calls <paramref name="idle"/> (a short step of work, returning true while there is
    /// more) before blocking, and checks for output again after each step, so output never waits behind more than one
    /// step. Waiting for quiet keeps the work out of a flood of output, even one that comes slower than it is read.
    /// On Windows (no waitable pipe) the work runs only when a read starts after such a quiet spell.
    /// </summary>
    public int Read(Span<byte> buffer, Func<bool>? idle) => _backend.Read(buffer, idle);

    /// <summary>How long the child must have been quiet before <see cref="Read(Span{byte}, Func{bool})"/> does idle work.</summary>
    public const int IdleAfterMs = 25;

    /// <summary>
    /// Queues <paramref name="data"/> as keyboard input; a writer thread hands it to the child, so this never blocks, even
    /// when the child doesn't read. False if it was dropped: <see cref="PtyWriter.Capacity"/> is already waiting, or the
    /// child is gone.
    /// </summary>
    public bool Write(ReadOnlySpan<byte> data) => _writer.Write(data);

    /// <summary>
    /// Queues a terminal reply (DA, DSR, OSC query answers). Dropped (false) once <see cref="PtyWriter.ReplyLimit"/> is
    /// waiting: a program that floods queries without reading the answers can't grow the queue.
    /// </summary>
    public bool WriteReply(ReadOnlySpan<byte> data) => _writer.WriteReply(data);

    /// <summary>Sets the terminal size. Pixel sizes are reported to the child on Linux and ignored on Windows.</summary>
    public void Resize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0) =>
        _backend.Resize(columns, rows, pixelWidth, pixelHeight);

    /// <summary>
    /// Waits for the child and returns its exit status. Linux: 0–255, or 128+signal if killed.
    /// Windows: the process exit code (may be negative, e.g. NTSTATUS codes). -1 if it can't be obtained.
    /// </summary>
    public int WaitForExit() => _backend.WaitForExit();

    public bool TryGetExitCode(out int exitCode) => _backend.TryGetExitCode(out exitCode);

    /// <summary>Asks the child to go away: SIGHUP on Linux; closing the pseudoconsole on Windows. Doesn't block.</summary>
    public void Hangup() => _backend.Hangup();

    /// <summary>Stops the input queue (what the child takes within 50 ms still goes out), then closes the pty.</summary>
    public void Dispose()
    {
        _writer.Close(TimeSpan.FromMilliseconds(50));
        _backend.Dispose();
    }
}
