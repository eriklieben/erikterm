using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static ErikTerm.Pty.Kernel32;
using static ErikTerm.Pty.Win32;

namespace ErikTerm.Pty;

/// <summary>
/// A ConPTY pseudoconsole (Windows 10 1809+) with a child process attached.
///
/// Two anonymous pipes connect us to conhost: we write keyboard input to <see cref="_input"/> and read
/// VT output from <see cref="_output"/>. Unlike a Unix pty, conhost keeps the output pipe open after the
/// child exits, until the pseudoconsole is closed. To give <see cref="Read"/> the same "0 at EOF"
/// meaning as on Linux, a watcher thread waits for the process and then closes the pseudoconsole, which
/// flushes the final frame and breaks the pipe.
///
/// Before Windows 11 24H2, ClosePseudoConsole blocks until conhost has exited, and conhost can't exit
/// while it is blocked writing to an undrained output pipe. Closing therefore happens either on a
/// background thread while the reader drains (exit watcher, <see cref="Hangup"/>), or after our end of
/// the output pipe has been closed so conhost's writes fail instead of blocking (<see cref="Dispose"/>).
///
/// Closing doesn't always end the child: on Windows 11 24H2 and Server 2025, a child still attaching when the
/// pseudoconsole closes (a pane closed right after it opened) stays alive, and so does conhost, so the output pipe never
/// breaks. Whoever closes the console therefore makes sure the child ends (<see cref="EnsureProcessEnds"/>).
///
/// Handles are SafeHandles so that Dispose racing a blocked Read/WaitForExit on another thread can't
/// close a handle out from under the call (the P/Invoke marshaller holds a reference for its duration).
/// </summary>
[SupportedOSPlatform("windows")]
#pragma warning disable S6640 // CreateProcessW, ReadFile and WriteFile take pointers to pinned buffers
internal sealed unsafe class WindowsPty : IPtyBackend
#pragma warning restore S6640
{
    private readonly SafeFileHandle _input;
    private readonly SafeFileHandle _output;
    private long _lastOutputAt; // Environment.TickCount64 of the last output read (idle work waits for quiet)
    private readonly SafeProcessHandle _process;
    private readonly Lock _consoleLock = new();
    private nint _console;                      // HPCON; 0 once closed. Guarded by _consoleLock.
    private long _exitCode = NoExitCode;
    private int _disposed;

    private const long NoExitCode = long.MinValue;

    /// <summary>How long a child may take to end after its console closed: longer than the 5 s Windows gives a CTRL_CLOSE_EVENT handler.</summary>
    private const uint CloseGraceMs = 6000;

    /// <summary>The child's creation time (FILETIME): which process the pid names, for <see cref="WindowsProcessTree"/>.</summary>
    private readonly long _created;
    private ForegroundPick? _pick;

    /// <summary>The pid of the pseudoconsole's host process, as the child sees it (0 until it has connected to it).</summary>
    private int _consoleHost;

    /// <summary>The foreground process found in one snapshot (by its version; asked again only for a newer one).</summary>
    private sealed record ForegroundPick(long Snapshot, ProcessEntry? Entry);

    public int ProcessId { get; }
    public int MasterFd => -1;
    public string? SlavePath => null;
    public int ForegroundProcessId => Foreground() is { } fg ? fg.Pid : -1;
    public string? ForegroundProcessName => Foreground() is { } fg ? WindowsProcessTree.DisplayName(fg.ExeName) : null;

    private WindowsPty(SafeFileHandle input, SafeFileHandle output, SafeProcessHandle process, int pid, nint console)
    {
        _input = input;
        _output = output;
        _process = process;
        _console = console;
        ProcessId = pid;
        // While we hold _process the pid can't be reused, so the root is always our child; its creation time tells its
        // real children from orphans that carry an older owner's pid as their parent.
        _created = WindowsProcesses.CreationTime(process);
        new Thread(WatchForExit, maxStackSize: 64 * 1024) { IsBackground = true, Name = "conpty-exit-watcher" }.Start();
    }

    /// <summary>The pane's foreground process in the current shared snapshot (see <see cref="WindowsProcessTree"/>), or null.</summary>
    private ProcessEntry? Foreground()
    {
        if (Volatile.Read(ref _disposed) != 0) return null;
        if (WindowsProcesses.Current() is not { } snapshot) return null;
        var pick = Volatile.Read(ref _pick);
        if (pick is not null && pick.Snapshot == snapshot.Version) return pick.Entry;
        // Every process on the pane's console reports this host, also one whose parent is gone (MSYS2's fork + exec).
        // It doesn't change once the child has connected; until then (right after the spawn) it is asked again.
        int host = Volatile.Read(ref _consoleHost);
        if (host == 0) Volatile.Write(ref _consoleHost, host = WindowsProcesses.ConsoleHostProcessId(_process));
        var entry = snapshot.Foreground(ProcessId, _created, host);
        Volatile.Write(ref _pick, new ForegroundPick(snapshot.Version, entry));
        return entry;
    }

    public static WindowsPty Spawn(PtyOptions options)
    {
        var argv = options.Arguments ?? [options.Program];
        var env = options.Environment ?? Pty.DefaultEnvironment();

        // Everything that can fail on bad input, before any OS object exists.
        string environmentBlock = WindowsCommandLine.BuildEnvironmentBlock(env);
        string? path = null;
        foreach (var (k, v) in env)
            if (string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) { path = v; break; }
        string application = ResolveProgram(options.Program, path)
            ?? throw new FileNotFoundException($"CreateProcess('{options.Program}') failed: program not found", options.Program);
        // A .bat / .cmd, and erikterm itself (a GUI-subsystem program: its terminal tools need cmd's console), start
        // through cmd.exe, which parses the command line by its own rules (WindowsCommandLine.BuildCmdLaunch).
        string commandLine;
        bool self = IsThisExecutable(application);
        if (self || WindowsCommandLine.IsBatchFile(application))
        {
            string cmd = Path.Combine(System.Environment.SystemDirectory, "cmd.exe");
            commandLine = WindowsCommandLine.BuildCmdLaunch(cmd, [application, .. argv.Skip(1)], waitForGuiProgram: self);
            application = cmd;
        }
        else commandLine = WindowsCommandLine.Build(argv);
        string? cwd = options.WorkingDirectory is { } d ? Path.GetFullPath(d) : null;

        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        nint console = 0;
        try
        {
            // Default (null) security attributes: not inheritable. conhost duplicates what it needs.
            if (CreatePipe(out inputRead, out inputWrite, null, 0) == 0) throw Win32Error("CreatePipe");
            if (CreatePipe(out outputRead, out outputWrite, null, 0) == 0) throw Win32Error("CreatePipe");

            int hr;
            try
            {
                hr = ConPty.Create(Size(options.Columns, options.Rows), inputRead, outputWrite, 0, out console);
            }
            catch (EntryPointNotFoundException e)
            {
                throw new PlatformNotSupportedException("ConPTY (CreatePseudoConsole) needs Windows 10 version 1809 or later.", e);
            }
            if (hr < 0)
            {
                console = 0;
                throw new IOException($"CreatePseudoConsole failed: HRESULT 0x{hr:X8}", hr);
            }

            // conhost holds its own duplicates now. Keeping our copy of the output pipe's write end
            // would mean ReadFile never sees ERROR_BROKEN_PIPE.
            // Null once closed, so the clean-up below leaves them alone.
            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;

            PROCESS_INFORMATION pi = StartProcess(application, commandLine, environmentBlock, cwd, console);
            CloseHandle(pi.hThread);
            var process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            return new WindowsPty(inputWrite, outputRead, process, (int)pi.dwProcessId, console);
        }
        catch
        {
            // Output read end first, so ClosePseudoConsole can't block on an undrained pipe.
            outputRead?.Dispose();
            if (console != 0) ConPty.Close(console);
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputWrite?.Dispose();
            throw;
        }
    }

    private static PROCESS_INFORMATION StartProcess(string application, string commandLine, string environmentBlock,
        string? cwd, nint console)
    {
        nuint size = 0;
        // Sizing call: fails with ERROR_INSUFFICIENT_BUFFER and sets size.
        if (InitializeProcThreadAttributeList(null, 1, 0, &size) == 0 && Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER)
            throw Win32Error("InitializeProcThreadAttributeList");

        void* attributes = NativeMemory.AllocZeroed(size);
        try
        {
            if (InitializeProcThreadAttributeList(attributes, 1, 0, &size) == 0)
                throw Win32Error("InitializeProcThreadAttributeList");
            try
            {
                // lpValue is the HPCON itself, not a pointer to it.
                if (UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        (void*)console, (nuint)sizeof(nint), null, null) == 0)
                    throw Win32Error("UpdateProcThreadAttribute");

                var si = new STARTUPINFOEXW();
                si.StartupInfo.cb = (uint)sizeof(STARTUPINFOEXW);
                // Null std handles + STARTF_USESTDHANDLES: without this a child can pick up our own
                // (possibly redirected) stdio instead of the pseudoconsole. Windows Terminal does the same.
                si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                si.lpAttributeList = (nint)attributes;

                // CreateProcessW may write to lpCommandLine, so it must be a mutable, NUL-terminated copy.
                char[] cmd = new char[commandLine.Length + 1];
                commandLine.CopyTo(cmd);

                PROCESS_INFORMATION pi;
                fixed (char* app = application)
                fixed (char* cmdPtr = cmd)
                fixed (char* envPtr = environmentBlock)
                fixed (char* cwdPtr = cwd) // null when cwd is null: inherit ours
                {
                    if (CreateProcessW(app, cmdPtr, null, null, bInheritHandles: 0,
                            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                            envPtr, cwdPtr, &si, &pi) == 0)
                        throw Win32Error($"CreateProcess('{application}')");
                }
                return pi;
            }
            finally
            {
                DeleteProcThreadAttributeList(attributes);
            }
        }
        finally
        {
            NativeMemory.Free(attributes);
        }
    }

    /// <summary>Whether <paramref name="application"/> is the executable of this process (erikterm starting one of its tools).</summary>
    private static bool IsThisExecutable(string application) =>
        System.Environment.ProcessPath is { } self
        && string.Equals(Path.GetFullPath(application), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase);

    internal static string? ResolveProgram(string program, string? path) =>
        WindowsCommandLine.ResolveProgram(program, WindowsCommandLine.SearchDirectories(
            System.Environment.SystemDirectory,
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows),
            path), File.Exists);

    public static string DefaultShell()
    {
        string? path = System.Environment.GetEnvironmentVariable("PATH");
        if (ResolveProgram("pwsh.exe", path) is { } pwsh) return pwsh;
        if (ResolveProgram("powershell.exe", path) is { } ps) return ps;
        string builtinPs = Path.Combine(System.Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(builtinPs)) return builtinPs;
        if (System.Environment.GetEnvironmentVariable("COMSPEC") is { Length: > 0 } comspec) return comspec;
        return Path.Combine(System.Environment.SystemDirectory, "cmd.exe");
    }

    private void WatchForExit()
    {
        try
        {
            if (WaitForSingleObject(_process, INFINITE) == WAIT_OBJECT_0) CacheExitCode();
        }
        catch (ObjectDisposedException) { /* disposed meanwhile: the console still gets closed below */ }
        // Flushes conhost's last frame, then breaks the output pipe so Read returns 0.
        CloseConsole();
    }

    private void CloseConsole()
    {
        nint console;
        lock (_consoleLock)
        {
            console = _console;
            _console = 0;
        }
        if (console != 0) ConPty.Close(console);
    }

    /// <summary>Waits for the child to end now that its console is closed, and terminates it if it doesn't. Blocks: not for the UI thread.</summary>
    private void EnsureProcessEnds()
    {
        try
        {
            if (WaitForSingleObject(_process, CloseGraceMs) != WAIT_OBJECT_0) TerminateProcess(_process, 1);
        }
        catch (ObjectDisposedException) { /* disposed meanwhile: the process handle is gone */ }
    }

    /// <summary>Blocking read of conhost's VT output. Returns 0 once the pipe is broken (console closed).</summary>
    public int Read(Span<byte> buffer, Func<bool>? idle)
    {
        if (buffer.IsEmpty) return 0;
        fixed (byte* p = buffer)
        {
            while (true)
            {
                // Quiet for a while and nothing waiting in the pipe: idle work first (ReadFile below blocks, and an
                // anonymous pipe can't be waited on with a timeout, so a read that starts sooner skips it).
                if (idle is not null && Environment.TickCount64 - _lastOutputAt >= Pty.IdleAfterMs)
                    while (Available() == 0 && idle()) { /* idle() does one step of work and returns true while more is left */ }
                if (ReadOnce(p, buffer.Length) is { } result) return result;
            }
        }
    }

    /// <summary>One ReadFile: the value to return from <see cref="Read"/>, or null to read again.</summary>
    private int? ReadOnce(byte* p, int length)
    {
        uint n;
        int ok;
        try
        {
            ok = ReadFile(_output, p, (uint)length, &n, null);
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        if (ok != 0)
        {
            if (n == 0) return null; // a zero-byte write on the other end; not EOF for a pipe
            _lastOutputAt = Environment.TickCount64;
            WindowsProcesses.NoteActivity();
            return (int)n;
        }
        int error = Marshal.GetLastPInvokeError();
        if (error is ERROR_BROKEN_PIPE or ERROR_OPERATION_ABORTED or ERROR_INVALID_HANDLE) return 0;
        throw Win32Error("ReadFile", error);
    }

    /// <summary>Bytes waiting in the output pipe, or -1 when that can't be told (the read then just blocks).</summary>
    private int Available()
    {
        uint avail = 0;
        try
        {
            return PeekNamedPipe(_output, null, 0, null, &avail, null) != 0 ? (int)Math.Min(avail, int.MaxValue) : -1;
        }
        catch (ObjectDisposedException)
        {
            return -1;
        }
    }

    /// <summary>
    /// One WriteFile (IPtySink). An anonymous pipe has no timeout or non-blocking mode, so this waits until conhost takes
    /// the bytes; it runs on PtyWriter's thread, never the UI thread. Closing the pseudoconsole breaks the pipe and ends it.
    /// </summary>
    public int WriteSome(ReadOnlySpan<byte> data, int timeoutMs)
    {
        WindowsProcesses.NoteActivity();
        if (data.IsEmpty) return 0;
        fixed (byte* p = data)
        {
            uint written;
            int ok;
            try
            {
                ok = WriteFile(_input, p, (uint)data.Length, &written, null);
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }
            if (ok != 0) return (int)written;
            int error = Marshal.GetLastPInvokeError();
            if (error is ERROR_BROKEN_PIPE or ERROR_NO_DATA) return -1; // console gone
            throw Win32Error("WriteFile", error);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        WindowsProcesses.NoteActivity();
        fixed (byte* p = data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                uint written;
                int ok;
                try
                {
                    ok = WriteFile(_input, p + offset, (uint)(data.Length - offset), &written, null);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                if (ok == 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error is ERROR_BROKEN_PIPE or ERROR_NO_DATA) return; // console gone
                    throw Win32Error("WriteFile", error);
                }
                offset += (int)written;
            }
        }
    }

    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        // Held across the call so CloseConsole can't free the HPCON while it's in use.
        lock (_consoleLock)
        {
            if (_console == 0) return;
            int hr = ConPty.Resize(_console, Size(columns, rows));
            if (hr < 0) throw new IOException($"ResizePseudoConsole failed: HRESULT 0x{hr:X8}", hr);
        }
    }

    private static COORD Size(int columns, int rows) => new()
    {
        X = (short)Math.Clamp(columns, 1, short.MaxValue),
        Y = (short)Math.Clamp(rows, 1, short.MaxValue),
    };

    public int WaitForExit()
    {
        long cached = Interlocked.Read(ref _exitCode);
        if (cached != NoExitCode) return (int)cached;
        try
        {
            if (WaitForSingleObject(_process, INFINITE) != WAIT_OBJECT_0) return -1;
            return CacheExitCode() ?? -1;
        }
        catch (ObjectDisposedException)
        {
            cached = Interlocked.Read(ref _exitCode);
            return cached != NoExitCode ? (int)cached : -1;
        }
    }

    public bool TryGetExitCode(out int exitCode)
    {
        long cached = Interlocked.Read(ref _exitCode);
        if (cached == NoExitCode)
        {
            try
            {
                if (WaitForSingleObject(_process, 0) == WAIT_OBJECT_0) CacheExitCode();
            }
            catch (ObjectDisposedException) { /* disposed meanwhile: no exit code to read */ }
            cached = Interlocked.Read(ref _exitCode);
        }
        exitCode = cached != NoExitCode ? (int)cached : int.MinValue; // as UnixPty
        return cached != NoExitCode;
    }

    /// <summary>Call only once the process has exited.</summary>
    private int? CacheExitCode()
    {
        uint code;
        if (GetExitCodeProcess(_process, &code) == 0) return null;
        Interlocked.CompareExchange(ref _exitCode, (int)code, NoExitCode);
        return (int)Interlocked.Read(ref _exitCode);
    }

    /// <summary>
    /// Closes the pseudoconsole, which terminates every process attached to it (the console analogue of
    /// SIGHUP). Done on a pool thread because ClosePseudoConsole can block until output is drained; a child that
    /// outlives it is terminated (<see cref="EnsureProcessEnds"/>). If the console is already closed but the process
    /// lingers (it detached from the console), it is terminated at once.
    /// </summary>
    public void Hangup()
    {
        bool hasConsole;
        lock (_consoleLock) hasConsole = _console != 0;
        if (hasConsole)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static self =>
            {
                self.CloseConsole();
                self.EnsureProcessEnds();
            }, this, preferLocal: false);
            return;
        }
        try
        {
            if (WaitForSingleObject(_process, 0) != WAIT_OBJECT_0) TerminateProcess(_process, 1);
        }
        catch (ObjectDisposedException) { /* disposed meanwhile: the process handle is gone */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Wake a blocked reader (harmless if there is none), then close our end of the output pipe so
        // that ClosePseudoConsole below can't block on output nobody reads. If a ReadFile is still in
        // flight, the SafeHandle defers the CloseHandle until it returns — and it is draining meanwhile.
        try { CancelIoEx(_output, null); } catch (ObjectDisposedException) { /* already disposed: there is no read to wake */ }
        _output.Dispose();
        _input.Dispose();
        CloseConsole();
        // The handle stays open until the child has ended, on a pool thread: Dispose doesn't wait for it.
        ThreadPool.UnsafeQueueUserWorkItem(static self =>
        {
            self.EnsureProcessEnds();
            self._process.Dispose();
        }, this, preferLocal: false);
    }

    private static IOException Win32Error(string what) => Win32Error(what, Marshal.GetLastPInvokeError());

    private static IOException Win32Error(string what, int error) =>
        new($"{what} failed: {new Win32Exception(error).Message} (error {error})", unchecked((int)0x80070000 | (error & 0xFFFF)));
}
