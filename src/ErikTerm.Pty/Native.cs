using System.Reflection;
using System.Runtime.InteropServices;

namespace ErikTerm.Pty;

/// <summary>
/// libc bindings for Linux (glibc, x86_64/arm64) and macOS (libSystem, arm64/x86_64). Struct sizes were taken from the
/// glibc and macOS SDK headers; opaque types are over-allocated so layout drift can't corrupt memory (on macOS
/// posix_spawnattr_t and posix_spawn_file_actions_t are a pointer the init call fills in, sigset_t is 4 bytes).
/// Constants that differ between the two come from <see cref="PtyConstants"/>, picked once at startup.
/// </summary>
#pragma warning disable S6640 // libc signatures take raw pointers
internal static unsafe partial class Native
#pragma warning restore S6640
{
    /// <summary>Logical name; <see cref="Resolve"/> maps it to libc.so.6 or libSystem.B.dylib.</summary>
    private const string Libc = "libc";

    static Native() => NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Libc) return 0;
        string file = OperatingSystem.IsMacOS() ? PtyConstants.MacLibc : PtyConstants.LinuxLibc;
        return NativeLibrary.TryLoad(file, assembly, searchPath, out nint handle) ? handle : 0;
    }

    private static readonly PtyConstants C = PtyConstants.Current;

    // The same on Linux and macOS (checked against both sets of headers; see PtyConstants for the sources).
    public const int O_RDWR = 0x2;

    public const short POLLIN = 0x1;
    public const short POLLOUT = 0x4;
    public const short POLLERR = 0x8;
    public const short POLLHUP = 0x10;
    public const short POLLNVAL = 0x20;

    public const int EINTR = 4;
    public const int EIO = 5;
    public const int EPERM = 1;

    public const short POSIX_SPAWN_SETSIGDEF = 0x04;
    public const short POSIX_SPAWN_SETSIGMASK = 0x08;

    public const int SIGHUP = 1;
    public const int SIGKILL = 9;
    public const int WNOHANG = 1;

    // Different per OS.
    public static readonly int O_NOCTTY = C.O_NOCTTY;
    public static readonly int O_CLOEXEC = C.O_CLOEXEC;
    public static readonly int O_NONBLOCK = C.O_NONBLOCK;
    public static readonly int EAGAIN = C.EAGAIN;
    public static readonly ulong TIOCSWINSZ = C.TIOCSWINSZ;
    public static readonly short POSIX_SPAWN_SETSID = C.POSIX_SPAWN_SETSID;

    /// <summary>
    /// glibc: sizeof(posix_spawnattr_t)=336, sizeof(posix_spawn_file_actions_t)=80, sizeof(sigset_t)=128.
    /// macOS: 8, 8 and 4.
    /// </summary>
    public const int SpawnAttrSize = 512;
    public const int FileActionsSize = 256;
    public const int SigSetSize = 256;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events, Revents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort Rows, Cols, XPixel, YPixel;
    }

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int posix_openpt(int flags);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int grantpt(int fd);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int unlockpt(int fd);

    [LibraryImport(Libc)]
    public static partial int ptsname_r(int fd, byte* buf, nuint len);

    /// <summary>open(2) without O_CREAT: the variadic mode argument is never read, so two fixed arguments are right everywhere.</summary>
    [LibraryImport(Libc, EntryPoint = "open", SetLastError = true)]
    public static partial int open(byte* path, int flags);

    /// <summary>
    /// ioctl(2) with a pointer argument. ioctl is variadic: on Apple arm64 variadic arguments go on the stack, not in
    /// registers, so there the call passes six dummy register arguments and the pointer as the ninth (first stack) slot.
    /// </summary>
    public static int ioctl(int fd, ulong request, void* arg) =>
        PtyConstants.VariadicOnStack ? ioctl_stack(fd, request, 0, 0, 0, 0, 0, 0, arg) : ioctl_regs(fd, request, arg);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl_regs(int fd, ulong request, void* arg);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl_stack(int fd, ulong request, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, void* arg);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial nint read(int fd, byte* buf, nint count);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial nint write(int fd, byte* buf, nint count);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int tcgetpgrp(int fd);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int kill(int pid, int sig);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int waitpid(int pid, int* status, int options);

    [LibraryImport(Libc)]
    public static partial int sigemptyset(void* set);

    [LibraryImport(Libc)]
    public static partial int sigfillset(void* set);

    [LibraryImport(Libc)]
    public static partial int posix_spawn_file_actions_init(void* actions);

    [LibraryImport(Libc)]
    public static partial int posix_spawn_file_actions_destroy(void* actions);

    [LibraryImport(Libc)]
    public static partial int posix_spawn_file_actions_addopen(void* actions, int fd, byte* path, int oflag, uint mode);

    [LibraryImport(Libc)]
    public static partial int posix_spawn_file_actions_adddup2(void* actions, int fd, int newfd);

    /// <summary>glibc 2.29+, macOS 10.15+.</summary>
    [LibraryImport(Libc)]
    public static partial int posix_spawn_file_actions_addchdir_np(void* actions, byte* path);

    /// <summary>macOS 10.7+ only: keep <paramref name="fd"/> open across a POSIX_SPAWN_CLOEXEC_DEFAULT spawn.</summary>
    [LibraryImport(Libc)]
    public static partial int posix_spawn_file_actions_addinherit_np(void* actions, int fd);

    [LibraryImport(Libc)]
    public static partial int posix_spawnattr_init(void* attr);

    [LibraryImport(Libc)]
    public static partial int posix_spawnattr_destroy(void* attr);

    [LibraryImport(Libc)]
    public static partial int posix_spawnattr_setflags(void* attr, short flags);

    [LibraryImport(Libc)]
    public static partial int posix_spawnattr_setsigmask(void* attr, void* sigmask);

    [LibraryImport(Libc)]
    public static partial int posix_spawnattr_setsigdefault(void* attr, void* sigdefault);

    [LibraryImport(Libc)]
    public static partial int posix_spawnp(int* pid, byte* file, void* fileActions, void* attr, byte** argv, byte** envp);

    [LibraryImport(Libc)]
    public static partial int posix_spawn(int* pid, byte* path, void* fileActions, void* attr, byte** argv, byte** envp);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int execve(byte* path, byte** argv, byte** envp);

    // ---- macOS only ----------------------------------------------------------------------------------

    /// <summary>struct timeval on macOS: a long and an int (suseconds_t), padded to 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MacTimeval
    {
        public long Sec;
        public int Usec, Pad;
    }

    /// <summary>select(2); the fd_sets are FD_SETSIZE (1024) bits.</summary>
    [LibraryImport(Libc, SetLastError = true)]
    public static partial int select(int nfds, uint* readfds, uint* writefds, uint* errorfds, MacTimeval* timeout);

    /// <summary>The process's environ (macOS has no exported <c>environ</c> symbol for dynamic code).</summary>
    [LibraryImport(Libc)]
    public static partial byte*** _NSGetEnviron();

    /// <summary>libproc: the process's name (up to 32 bytes; the executable's file name, as exec'd).</summary>
    [LibraryImport(Libc)]
    public static partial int proc_name(int pid, byte* buffer, uint size);

    /// <summary>libproc: the executable's path (buffer of PROC_PIDPATHINFO_MAXSIZE).</summary>
    [LibraryImport(Libc)]
    public static partial int proc_pidpath(int pid, byte* buffer, uint size);

    [LibraryImport(Libc)]
    public static partial int proc_pidinfo(int pid, int flavor, ulong arg, void* buffer, int size);

    [LibraryImport(Libc, SetLastError = true)]
    public static partial int sysctl(int* name, uint namelen, void* oldp, nuint* oldlenp, void* newp, nuint newlen);
}
