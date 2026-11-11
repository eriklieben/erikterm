using System.Runtime.InteropServices;

namespace ErikTerm.App.Ipc;

/// <summary>
/// lstat-style checks of who owns a file and what it is. Linux: statx, whose layout is the same on every architecture.
/// macOS: lstat with the 64-bit-inode struct stat (bsd/sys/stat.h: st_mode at 4, st_uid at 16; the symbol is
/// lstat$INODE64 on x86_64, plain lstat on arm64).
/// </summary>
#pragma warning disable S6640 // statx and lstat fill C structs through pointers
internal static unsafe partial class UnixFiles
#pragma warning restore S6640
{
    private const string Libc = "libc.so.6";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int AT_FDCWD = -100, AT_SYMLINK_NOFOLLOW = 0x100;
    private const uint STATX_TYPE = 0x1, STATX_MODE = 0x2, STATX_UID = 0x8;

    public const int S_IFMT = 0xF000, S_IFDIR = 0x4000, S_IFREG = 0x8000, S_IFSOCK = 0xC000, S_IFLNK = 0xA000;

    /// <summary>File type and permission bits, and the owner, of the path itself (symlinks are not followed).</summary>
    public readonly record struct Info(int Mode, uint Uid)
    {
        public int Type => Mode & S_IFMT;
        public int Permissions => Mode & 0xFFF;
    }

    [LibraryImport(Libc, EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX(int dirfd, string path, int flags, uint mask, byte* statx);

    [LibraryImport(Libc, EntryPoint = "geteuid")]
    private static partial uint GetEuid();

    [LibraryImport(LibSystem, EntryPoint = "geteuid")]
    private static partial uint MacGetEuid();

    [LibraryImport(LibSystem, EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacLStat(string path, byte* stat);

    [LibraryImport(LibSystem, EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacLStatInode64(string path, byte* stat);

    public static uint EffectiveUid => OperatingSystem.IsMacOS() ? MacGetEuid() : GetEuid();

    /// <summary>sizeof(struct stat) on macOS (64-bit inodes), and where st_mode / st_uid are.</summary>
    internal const int MacStatSize = 144, MacStatModeOffset = 4, MacStatUidOffset = 16;

    /// <summary>Whether <see cref="LStat"/> can examine files here (Linux and macOS).</summary>
    public static bool Supported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>lstat of <paramref name="path"/>, or null if it doesn't exist or can't be examined.</summary>
    public static Info? LStat(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            byte* st = stackalloc byte[256];
            int rc = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? MacLStatInode64(path, st) : MacLStat(path, st);
            if (rc != 0) return null;
            return new Info(*(ushort*)(st + MacStatModeOffset), *(uint*)(st + MacStatUidOffset));
        }
        if (!OperatingSystem.IsLinux()) return null;
        byte* buf = stackalloc byte[256];
        if (StatX(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_TYPE | STATX_MODE | STATX_UID, buf) != 0) return null;
        return new Info(*(ushort*)(buf + 28), *(uint*)(buf + 20));
    }

    /// <summary>
    /// Null when <paramref name="info"/> describes a private directory of ours: a real directory (not a symlink)
    /// owned by <paramref name="uid"/> with no permissions for group or others; else why not.
    /// </summary>
    public static string? WhyNotPrivateDirectory(Info? info, uint uid) => info switch
    {
        null => "it can't be examined",
        { Type: S_IFLNK } => "it is a symbolic link",
        { Type: not S_IFDIR } => "it is not a directory",
        { } i when i.Uid != uid => $"it is owned by uid {i.Uid}, not {uid}",
        { } i when (i.Permissions & 0x3F) != 0 => $"its mode is {Convert.ToString(i.Permissions, 8)}, not 700",
        _ => null,
    };
}
