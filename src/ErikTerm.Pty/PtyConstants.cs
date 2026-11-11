using System.Runtime.InteropServices;

namespace ErikTerm.Pty;

/// <summary>
/// The libc values the pty code needs that differ between Linux and macOS, one table per OS. <see cref="Current"/> is
/// picked once; <see cref="Native"/> copies it into static readonly fields, so the read/write loops pay nothing.
/// Linux: glibc / asm-generic headers (x86_64 and arm64 agree on these). macOS: the xnu and Libc sources
/// (apple-oss-distributions): bsd/sys/fcntl.h, bsd/sys/errno.h, bsd/sys/ttycom.h + bsd/sys/ioccom.h,
/// bsd/sys/spawn.h, bsd/sys/sysctl.h, bsd/sys/proc_info.h.
/// </summary>
internal sealed record PtyConstants(
    string Os,
    int O_NOCTTY,
    int O_CLOEXEC,
    int O_NONBLOCK,
    int EAGAIN,
    ulong TIOCSWINSZ,
    ulong TIOCSCTTY,
    short POSIX_SPAWN_SETSID,
    short POSIX_SPAWN_CLOEXEC_DEFAULT,
    short POSIX_SPAWN_SETEXEC)
{
    public const string LinuxLibc = "libc.so.6";
    public const string MacLibc = "/usr/lib/libSystem.B.dylib";

    public static readonly PtyConstants Linux = new(
        Os: "linux",
        O_NOCTTY: 0x100,          // asm-generic/fcntl.h 00000400
        O_CLOEXEC: 0x80000,       // 02000000
        O_NONBLOCK: 0x800,        // 00004000
        EAGAIN: 11,               // asm-generic/errno-base.h
        TIOCSWINSZ: 0x5414,       // asm-generic/ioctls.h
        TIOCSCTTY: 0x540E,
        POSIX_SPAWN_SETSID: 0x80, // glibc spawn.h (2.26+)
        POSIX_SPAWN_CLOEXEC_DEFAULT: 0, // none on Linux
        POSIX_SPAWN_SETEXEC: 0);        // none on Linux

    public static readonly PtyConstants MacOS = new(
        Os: "macos",
        O_NOCTTY: 0x20000,        // bsd/sys/fcntl.h 0x00020000
        O_CLOEXEC: 0x1000000,     // 0x01000000
        O_NONBLOCK: 0x4,          // 0x00000004
        EAGAIN: 35,               // bsd/sys/errno.h (11 is EDEADLK there)
        TIOCSWINSZ: 0x80087467,   // _IOW('t', 103, struct winsize): IOC_IN | 8 << 16 | 't' << 8 | 103
        TIOCSCTTY: 0x20007461,    // _IO('t', 97): IOC_VOID | 't' << 8 | 97
        POSIX_SPAWN_SETSID: 0x400,          // bsd/sys/spawn.h (macOS 10.15+)
        POSIX_SPAWN_CLOEXEC_DEFAULT: 0x4000,
        POSIX_SPAWN_SETEXEC: 0x40);

    public static PtyConstants For(bool macOS) => macOS ? MacOS : Linux;

    public static readonly PtyConstants Current = For(OperatingSystem.IsMacOS());

    /// <summary>
    /// Apple arm64 passes the variadic arguments of a C function on the stack (everyone else uses the same registers as
    /// for fixed arguments), so variadic calls (ioctl) need a different P/Invoke signature there.
    /// </summary>
    public static readonly bool VariadicOnStack = IsVariadicOnStack(OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture);

    public static bool IsVariadicOnStack(bool macOS, Architecture architecture) => macOS && architecture == Architecture.Arm64;

    // Shared values the code relies on being equal on both (spot-checked by the tests).
    public const int O_RDWR = 0x2, EINTR = 4, EIO = 5, POLLIN = 0x1, POLLOUT = 0x4, POLLNVAL = 0x20;
    public const short POSIX_SPAWN_SETSIGDEF = 0x04, POSIX_SPAWN_SETSIGMASK = 0x08;

    // macOS sysctl / libproc (bsd/sys/sysctl.h, bsd/sys/proc_info.h).
    public const int CTL_KERN = 1, KERN_ARGMAX = 8, KERN_PROCARGS2 = 49;
    public const int PROC_PIDVNODEPATHINFO = 9;
    /// <summary>4 * MAXPATHLEN.</summary>
    public const int PROC_PIDPATHINFO_MAXSIZE = 4096;
    /// <summary>
    /// sizeof(struct proc_vnodepathinfo): two vnode_info_path, each a vnode_info (vinfo_stat 136 + vi_type 4 + vi_pad 4
    /// + fsid_t 8 = 152 bytes) and a MAXPATHLEN (1024) path; pvi_cdir's path is at offset 152.
    /// </summary>
    public const int ProcVnodePathInfoSize = 2 * (152 + 1024), CwdPathOffset = 152, MaxPathLen = 1024;
}
