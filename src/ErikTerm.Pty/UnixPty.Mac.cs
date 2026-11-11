using System.Runtime.InteropServices;
using static ErikTerm.Pty.Native;

namespace ErikTerm.Pty;

/// <summary>
/// macOS spawning. xnu's posix_spawn runs the file actions before POSIX_SPAWN_SETSID (bsd/kern/kern_exec.c), so a slave
/// opened in a file action is never the new session's controlling terminal — and a shell without one has no job
/// control and gets no Ctrl+C. Like node-pty's spawn-helper, the child therefore starts as a helper: the parent spawns
/// <see cref="SpawnHelper"/> (erikterm itself) in a new session with the slave as fds 0–2; the helper makes it its
/// controlling terminal (TIOCSCTTY) and replaces itself with the program via posix_spawn(POSIX_SPAWN_SETEXEC), which
/// also resets signal dispositions and the mask and closes every other descriptor. The pid stays the same throughout.
/// The parent opens the slave itself first: xnu refuses most ioctls on a ptmx master (TIOCSWINSZ included) until the
/// slave side has been opened once (tty_dev.c, fix_7070978).
/// </summary>
#pragma warning disable S6640 // ptsname_r and select take pointers to stackalloc buffers
internal sealed unsafe partial class UnixPty
#pragma warning restore S6640
{
    /// <summary>argv[1] that makes erikterm act as the spawn helper: <c>erikterm --erikterm-pty-exec CWD PROGRAM ARGV0 ARGS…</c>.</summary>
    public const string HelperArgument = Pty.SpawnHelperArgument;

    /// <summary>
    /// The command that runs the helper (normally [erikterm's own path]); the helper arguments follow it. Null: spawn
    /// the program directly, without a controlling terminal (tests that don't set one up).
    /// </summary>
    public static string[]? SpawnHelper { get; set; }

    private static UnixPty SpawnMac(PtyOptions options)
    {
        int master = posix_openpt(O_RDWR | O_NOCTTY | O_CLOEXEC | O_NONBLOCK);
        if (master < 0) throw Error("posix_openpt");
        int slave = -1;
        try
        {
            if (grantpt(master) != 0) throw Error("grantpt");
            if (unlockpt(master) != 0) throw Error("unlockpt");

            byte* nameBuf = stackalloc byte[256]; // ptsname_r needs at least 128 (TIOCPTYGNAME)
            int rc = ptsname_r(master, nameBuf, 256);
            if (rc != 0) throw new IOException($"ptsname_r failed: errno {rc}");
            string slavePath = Marshal.PtrToStringUTF8((nint)nameBuf)!;

            do slave = open(nameBuf, O_RDWR | O_NOCTTY | O_CLOEXEC);
            while (slave < 0 && Marshal.GetLastPInvokeError() == EINTR);
            if (slave < 0) throw Error("open(" + slavePath + ")");

            SetSize(master, options.Columns, options.Rows);

            var argv = options.Arguments ?? [options.Program];
            var env = options.Environment ?? Pty.DefaultEnvironment();
            int pid = SpawnHelper is { Length: > 0 } helper
                ? SpawnViaHelper(helper, options.Program, argv, env, slave, options.WorkingDirectory)
                : SpawnDetached(options.Program, argv, env, slave, options.WorkingDirectory);
            return new UnixPty(master, pid, slavePath);
        }
        catch
        {
            close(master);
            throw;
        }
        finally
        {
            // The child has its own copies (fds 0-2); ours only had to exist until the spawn returned.
            if (slave >= 0) close(slave);
        }
    }

    private static int SpawnViaHelper(string[] helper, string program, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string> env, int slave, string? cwd)
    {
        var helperArgv = Pty.SpawnHelperArgv(helper, program, argv, cwd);
        var envList = new List<string>(env.Count);
        foreach (var (key, value) in env) envList.Add($"{key}={value}");
        return SpawnWithStdio(helper[0], helperArgv, envList, slave, cwd: null, flags: POSIX_SPAWN_SETSID, searchPath: false);
    }

    /// <summary>No helper: the program in a session of its own with the slave as stdio, but no controlling terminal.</summary>
    private static int SpawnDetached(string program, IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> env, int slave, string? cwd)
    {
        var envList = new List<string>(env.Count);
        foreach (var (key, value) in env) envList.Add($"{key}={value}");
        return SpawnWithStdio(program, argv, envList, slave, cwd,
            (short)(POSIX_SPAWN_SETSID | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF), searchPath: true);
    }

    /// <summary>posix_spawn(p) with <paramref name="stdio"/> as fds 0–2.</summary>
    private static int SpawnWithStdio(string program, IReadOnlyList<string> argv, IReadOnlyList<string> env, int stdio, string? cwd,
        short flags, bool searchPath)
    {
        var strings = new NativeUtf8Strings();

        byte* actions = (byte*)NativeMemory.AllocZeroed(FileActionsSize);
        byte* attr = (byte*)NativeMemory.AllocZeroed(SpawnAttrSize);
        byte* sigs = (byte*)NativeMemory.AllocZeroed(SigSetSize);
        byte** argvPtr = (byte**)NativeMemory.AllocZeroed((nuint)(argv.Count + 1), (nuint)sizeof(nint));
        byte** envPtr = (byte**)NativeMemory.AllocZeroed((nuint)(env.Count + 1), (nuint)sizeof(nint));
        bool actionsReady = false, attrReady = false;
        try
        {
            for (int i = 0; i < argv.Count; i++) argvPtr[i] = strings.Add(argv[i]);
            for (int i = 0; i < env.Count; i++) envPtr[i] = strings.Add(env[i]);

            Check(posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
            actionsReady = true;
            for (int fd = 0; fd <= 2; fd++) Check(posix_spawn_file_actions_adddup2(actions, stdio, fd), "adddup2");
            if (cwd is not null) Check(posix_spawn_file_actions_addchdir_np(actions, strings.Add(cwd)), "addchdir");

            Check(posix_spawnattr_init(attr), "posix_spawnattr_init");
            attrReady = true;
            sigemptyset(sigs);
            Check(posix_spawnattr_setsigmask(attr, sigs), "setsigmask");
            sigfillset(sigs);
            Check(posix_spawnattr_setsigdefault(attr, sigs), "setsigdefault");
            Check(posix_spawnattr_setflags(attr, flags), "setflags");

            int pid;
            int rc = searchPath
                ? posix_spawnp(&pid, strings.Add(program), actions, attr, argvPtr, envPtr)
                : posix_spawn(&pid, strings.Add(program), actions, attr, argvPtr, envPtr);
            if (rc != 0) throw new IOException($"posix_spawn('{program}') failed: errno {rc}");
            return pid;
        }
        finally
        {
            if (actionsReady) posix_spawn_file_actions_destroy(actions);
            if (attrReady) posix_spawnattr_destroy(attr);
            NativeMemory.Free(actions);
            NativeMemory.Free(attr);
            NativeMemory.Free(sigs);
            NativeMemory.Free(argvPtr);
            NativeMemory.Free(envPtr);
            strings.Dispose();
        }
    }

    /// <summary>
    /// The helper, in the child (fds 0–2 are the slave, and it leads a new session): takes the tty as its controlling
    /// terminal and execs the program in place. Only returns on failure, after telling the terminal why (exit status 127).
    /// <paramref name="args"/> are the arguments after <see cref="HelperArgument"/>: CWD PROGRAM ARGV0 ARGS…
    /// </summary>
    public static int RunHelper(ReadOnlySpan<string> args)
    {
        if (!OperatingSystem.IsMacOS() || args.Length < 3)
        {
            Console.Error.WriteLine("erikterm: " + HelperArgument + " is internal (macOS pty spawning)");
            return 2;
        }
        string cwd = args[0], program = args[1];
        // Not fatal if it fails (e.g. run by hand from another terminal): the program still runs, without job control.
        ioctl(0, PtyConstants.Current.TIOCSCTTY, null);

        var strings = new NativeUtf8Strings();
        byte* actions = (byte*)NativeMemory.AllocZeroed(FileActionsSize);
        byte* attr = (byte*)NativeMemory.AllocZeroed(SpawnAttrSize);
        byte* sigs = (byte*)NativeMemory.AllocZeroed(SigSetSize);
        byte** argvPtr = (byte**)NativeMemory.AllocZeroed((nuint)(args.Length - 1), (nuint)sizeof(nint));
        int rc;
        try
        {
            for (int i = 2; i < args.Length; i++) argvPtr[i - 2] = strings.Add(args[i]);
            byte* file = strings.Add(program);

            posix_spawn_file_actions_init(actions);
            for (int fd = 0; fd <= 2; fd++) posix_spawn_file_actions_addinherit_np(actions, fd);
            if (cwd.Length > 0) posix_spawn_file_actions_addchdir_np(actions, strings.Add(cwd));
            posix_spawnattr_init(attr);
            sigemptyset(sigs);
            posix_spawnattr_setsigmask(attr, sigs);
            sigfillset(sigs);
            posix_spawnattr_setsigdefault(attr, sigs);
            var c = PtyConstants.Current;
            posix_spawnattr_setflags(attr, (short)(c.POSIX_SPAWN_SETEXEC | c.POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF));

            // The environment is ours as the parent built it; posix_spawnp searches its PATH.
            int pid;
            rc = posix_spawnp(&pid, file, actions, attr, argvPtr, *_NSGetEnviron());
        }
        finally
        {
            NativeMemory.Free(actions);
            NativeMemory.Free(attr);
            NativeMemory.Free(sigs);
            NativeMemory.Free(argvPtr);
            strings.Dispose();
        }
        // Still here: the exec failed.
        Console.Error.WriteLine($"erikterm: couldn't start {program}: {Marshal.GetPInvokeErrorMessage(rc)}" +
            (cwd.Length > 0 && !Directory.Exists(cwd) ? $" (in {cwd})" : ""));
        return 127;
    }

    private static int SelectWait(int fd, short events, int timeoutMs, out short revents)
    {
        uint* set = stackalloc uint[32]; // fd_set: FD_SETSIZE (1024) bits
        new Span<uint>(set, 32).Clear();
        set[fd >> 5] |= 1u << (fd & 31);
        MacTimeval tv = default;
        if (timeoutMs >= 0) tv = new MacTimeval { Sec = timeoutMs / 1000, Usec = timeoutMs % 1000 * 1000 };
        MacTimeval* timeout = timeoutMs >= 0 ? &tv : null;
        int ready = events == POLLOUT
            ? select(fd + 1, null, set, null, timeout)
            : select(fd + 1, set, null, null, timeout);
        revents = ready > 0 ? events : (short)0;
        return ready;
    }
}
