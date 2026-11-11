using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static ErikTerm.Pty.Native;

namespace ErikTerm.Pty;

/// <summary>
/// A Unix98 pseudo-terminal with a child process attached to its slave side.
/// .NET can't safely fork(), so the child is started with posix_spawn: POSIX_SPAWN_SETSID makes it a
/// session leader, then opening the slave (without O_NOCTTY) makes that tty its controlling terminal.
/// glibc runs setsid() before the file actions, which is what makes this work. macOS runs them the other way
/// round (xnu kern_exec.c: file actions, then setsid), so there the child starts as a small helper that takes the
/// tty as its controlling terminal and then becomes the program (UnixPty.Mac.cs).
/// </summary>
[UnsupportedOSPlatform("windows")]
#pragma warning disable S6640 // read, write and ptsname_r take pointers to pinned or stackalloc buffers
internal sealed unsafe partial class UnixPty : IPtyBackend
#pragma warning restore S6640
{
    private int _master;
    private int _exitCode = int.MinValue;

    public int MasterFd => _master;
    public int ProcessId { get; }
    public string? SlavePath { get; }

    private UnixPty(int master, int pid, string slavePath)
    {
        _master = master;
        ProcessId = pid;
        SlavePath = slavePath;
    }

    public static UnixPty Spawn(PtyOptions options)
    {
        if (OperatingSystem.IsMacOS()) return SpawnMac(options);
        // Non-blocking: writes go through PtyWriter's thread, which must be able to give up on a child that never reads
        // (a blocked write(2) on a pty master isn't even woken when the child dies). Read polls first.
        int master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC | O_NONBLOCK);
        if (master < 0) throw Error("posix_openpt");
        try
        {
            if (grantpt(master) != 0) throw Error("grantpt");
            if (unlockpt(master) != 0) throw Error("unlockpt");

            byte* nameBuf = stackalloc byte[256];
            int rc = ptsname_r(master, nameBuf, 256);
            if (rc != 0) throw new IOException($"ptsname_r failed: errno {rc}");
            string slavePath = Marshal.PtrToStringUTF8((nint)nameBuf)!;

            SetSize(master, options.Columns, options.Rows);

            var argv = options.Arguments ?? [options.Program];
            var env = options.Environment ?? Pty.DefaultEnvironment();
            int pid = SpawnChild(options.Program, argv, env, slavePath, options.WorkingDirectory);
            return new UnixPty(master, pid, slavePath);
        }
        catch
        {
            close(master);
            throw;
        }
    }

    private static int SpawnChild(string program, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> env,
        string slavePath, string? cwd)
    {
        var strings = new NativeUtf8Strings();

        byte* actions = (byte*)NativeMemory.AllocZeroed(FileActionsSize);
        byte* attr = (byte*)NativeMemory.AllocZeroed(SpawnAttrSize);
        byte* sigs = (byte*)NativeMemory.AllocZeroed(SigSetSize);
        byte** argvPtr = (byte**)NativeMemory.AllocZeroed((nuint)(argv.Count + 1), (nuint)sizeof(nint));
        byte** envPtr = (byte**)NativeMemory.AllocZeroed((nuint)(env.Count + 1), (nuint)sizeof(nint));
        try
        {
            for (int i = 0; i < argv.Count; i++) argvPtr[i] = strings.Add(argv[i]);
            int k = 0;
            foreach (var (key, value) in env) envPtr[k++] = strings.Add($"{key}={value}");

            Check(posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
            Check(posix_spawn_file_actions_addopen(actions, 0, strings.Add(slavePath), O_RDWR, 0), "addopen");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1), "adddup2");
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2), "adddup2");
            if (cwd is not null) Check(posix_spawn_file_actions_addchdir_np(actions, strings.Add(cwd)), "addchdir");

            Check(posix_spawnattr_init(attr), "posix_spawnattr_init");
            // The .NET runtime blocks/handles signals; give the shell a clean slate.
            sigemptyset(sigs);
            Check(posix_spawnattr_setsigmask(attr, sigs), "setsigmask");
            sigfillset(sigs);
            Check(posix_spawnattr_setsigdefault(attr, sigs), "setsigdefault");
            Check(posix_spawnattr_setflags(attr, (short)(POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF)), "setflags");

            int pid;
            int rc = posix_spawnp(&pid, strings.Add(program), actions, attr, argvPtr, envPtr);
            if (rc != 0) throw new IOException($"posix_spawnp('{program}') failed: errno {rc}");
            return pid;
        }
        finally
        {
            posix_spawn_file_actions_destroy(actions);
            posix_spawnattr_destroy(attr);
            NativeMemory.Free(actions);
            NativeMemory.Free(attr);
            NativeMemory.Free(sigs);
            NativeMemory.Free(argvPtr);
            NativeMemory.Free(envPtr);
            strings.Dispose();
        }
    }

    public int ForegroundProcessId
    {
        get
        {
            int fd = Volatile.Read(ref _master);
            if (fd < 0) return -1;
            // On Linux, TIOCGPGRP on the master reports the slave's foreground process group (macOS too: tty_dev.c).
            int pgid = tcgetpgrp(fd);
            return pgid > 0 ? pgid : -1;
        }
    }

    public string? ForegroundProcessName
    {
        get
        {
            int pgid = ForegroundProcessId;
            if (pgid <= 0) return null;
            if (OperatingSystem.IsMacOS()) return ProcessInfo.MacProcessName(pgid);
            try
            {
                string comm = File.ReadAllText($"/proc/{pgid}/comm").Trim();
                return comm.Length > 0 ? comm : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>How often a read with nothing to do checks whether the child is still there: often at first, then rarely.</summary>
    private const int ChildCheckMs = 1000, SlowChildCheckMs = 5000, ChecksBeforeSlowing = 5;

    internal static int ChildCheckDelay(int checks) => checks < ChecksBeforeSlowing ? ChildCheckMs : SlowChildCheckMs;

    /// <summary>Blocking read from the master. Returns 0 once the child side is gone (EOF / EIO) or the pty was disposed.</summary>
    public int Read(Span<byte> buffer, Func<bool>? idle)
    {
        bool quiet = false; // no output for Pty.IdleAfterMs: idle steps may run
        int checks = 0; // times this read has looked at the child
        fixed (byte* p = buffer)
        {
            while (true)
            {
                int fd = Volatile.Read(ref _master);
                if (fd < 0) return 0;
                nint n = read(fd, p, buffer.Length);
                if (n >= 0) return (int)n;
                if (AfterFailedRead(fd, idle, ref quiet, ref checks) is { } result) return result;
            }
        }
    }

    /// <summary>Handles a read that returned an error: the value to return from <see cref="Read"/>, or null to read again.</summary>
    private int? AfterFailedRead(int fd, Func<bool>? idle, ref bool quiet, ref int checks)
    {
        int errno = Marshal.GetLastPInvokeError();
        if (errno == EINTR) return null;
        if (errno == EIO) return 0;
        if (errno != EAGAIN) throw new IOException($"read failed: errno {errno}");
        return WaitForOutput(fd, idle, ref quiet, ref checks) ? null : 0;
    }

    /// <summary>Nothing to read: runs idle work or waits. False once the child is gone, true to look for output again.</summary>
    private bool WaitForOutput(int fd, Func<bool>? idle, ref bool quiet, ref int checks)
    {
        if (idle is not null)
        {
            if (!quiet)
            {
                int ready = Wait(fd, POLLIN, Pty.IdleAfterMs, out short r);
                if (WaitFailed(ready, r)) return false;
                if (ready != 0) return true; // output (or EINTR): read it
                quiet = true;
            }
            if (idle()) return true; // one step of idle work, then look for output again
        }
        int waited = Wait(fd, POLLIN, ChildCheckDelay(checks++), out short revents);
        if (WaitFailed(waited, revents)) return false;
        // Normally the child's exit shows up as EIO; not while something it left behind still holds the terminal open.
        return waited != 0 || !TryGetExitCode(out _);
    }

    private static bool WaitFailed(int ready, short revents) =>
        (ready < 0 && Marshal.GetLastPInvokeError() != EINTR) || (revents & POLLNVAL) != 0;

    /// <summary>
    /// Writes all of <paramref name="data"/>, waiting for the child to read as long as it takes. Only for callers that
    /// accept that (tests); <see cref="Pty.Write"/> queues instead (<see cref="PtyWriter"/>).
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int n = WriteSome(data, -1);
            if (n < 0) return; // child gone
            data = data[n..];
        }
    }

    /// <summary>One non-blocking write, after waiting up to <paramref name="timeoutMs"/> for room (IPtySink).</summary>
    public int WriteSome(ReadOnlySpan<byte> data, int timeoutMs)
    {
        if (data.IsEmpty) return 0;
        fixed (byte* p = data)
        {
            while (true)
            {
                int fd = Volatile.Read(ref _master);
                if (fd < 0) return -1;
                nint n = write(fd, p, data.Length);
                if (n >= 0) return (int)n;
                if (AfterFailedWrite(fd, timeoutMs) is { } result) return result;
            }
        }
    }

    /// <summary>Handles a write that returned an error: the value to return from <see cref="WriteSome"/>, or null to write again.</summary>
    private static int? AfterFailedWrite(int fd, int timeoutMs)
    {
        int errno = Marshal.GetLastPInvokeError();
        if (errno == EINTR) return null;
        if (errno != EAGAIN) return -1; // EIO: the child side is gone
        int ready = Wait(fd, POLLOUT, timeoutMs, out short revents);
        if (ready < 0 && Marshal.GetLastPInvokeError() == EINTR) return null;
        if (ready < 0 || (revents & POLLNVAL) != 0) return -1;
        if (ready == 0) return 0; // still no room: the caller checks whether to give up
        return null; // POLLOUT, or POLLHUP/POLLERR: the write says which (EIO once the child is gone)
    }

    /// <summary>
    /// Waits for <paramref name="fd"/> to be readable (POLLIN) or writable (POLLOUT): poll(2), except on macOS, where
    /// select(2) is used for the pty master (poll there goes through kqueue, which has long been unreliable for
    /// devices; select uses the tty driver's own d_select).
    /// </summary>
    private static int Wait(int fd, short events, int timeoutMs, out short revents)
    {
        if (OperatingSystem.IsMacOS() && fd is >= 0 and < 1024) return SelectWait(fd, events, timeoutMs, out revents);
        var pfd = new PollFd { Fd = fd, Events = events };
        int ready = poll(&pfd, 1, timeoutMs);
        revents = pfd.Revents;
        return ready;
    }

    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight) =>
        SetSize(_master, columns, rows, pixelWidth, pixelHeight);

    private static void SetSize(int fd, int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
    {
        var ws = new WinSize
        {
            Cols = (ushort)Math.Clamp(columns, 1, ushort.MaxValue),
            Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
            XPixel = (ushort)Math.Clamp(pixelWidth, 0, ushort.MaxValue),
            YPixel = (ushort)Math.Clamp(pixelHeight, 0, ushort.MaxValue),
        };
        if (ioctl(fd, TIOCSWINSZ, &ws) != 0) throw Error("ioctl(TIOCSWINSZ)");
    }

    /// <summary>Waits for the child and returns its exit status (128+signal if killed).</summary>
    public int WaitForExit()
    {
        if (_exitCode != int.MinValue) return _exitCode;
        int status;
        while (waitpid(ProcessId, &status, 0) < 0)
        {
            if (Marshal.GetLastPInvokeError() != EINTR) return _exitCode = -1;
        }
        return _exitCode = DecodeStatus(status);
    }

    public bool TryGetExitCode(out int exitCode)
    {
        if (_exitCode == int.MinValue)
        {
            int status;
            if (waitpid(ProcessId, &status, WNOHANG) == ProcessId) _exitCode = DecodeStatus(status);
        }
        exitCode = _exitCode;
        return _exitCode != int.MinValue;
    }

    private static int DecodeStatus(int status) =>
        (status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F);

    public void Hangup() => kill(ProcessId, SIGHUP);

    public void Dispose()
    {
        int fd = Interlocked.Exchange(ref _master, -1);
        if (fd >= 0) close(fd);
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new IOException($"{what} failed: errno {rc}");
    }

    private static IOException Error(string what) =>
        new($"{what} failed: errno {Marshal.GetLastPInvokeError()}");
}
