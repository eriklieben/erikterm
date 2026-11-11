using System.Runtime.InteropServices;
using System.Text;

namespace ErikTerm.Core.Images;

/// <summary>
/// Reads the files kitty <c>t=f</c> / <c>t=t</c> / <c>t=s</c> transmissions name. The file is opened first (Linux and
/// macOS: <c>O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_NOCTTY | O_CLOEXEC</c>, so a FIFO or a symlink never blocks or redirects
/// the open), then checked through the open descriptor — a non-empty regular file, not on procfs/sysfs and other
/// pseudo file systems, at most <see cref="ImageLimits.MaxEncodedBytes"/> to read — and read with a size cap.
/// A file is only deleted (<see cref="DeleteIfUnchanged"/>) after it was read — the caller waits until the data
/// also decoded — and only if the path still names the file that was read.
/// Failures carry no detail: the caller reports one generic error, so a program can't use the terminal to learn
/// whether a file exists, how big it is or whether it is readable.
/// </summary>
internal static partial class ImageFiles
{
    /// <summary>Longest POSIX shared memory name accepted (NAME_MAX).</summary>
    public const int MaxShmName = 255;

    /// <summary>"/dev/shm/&lt;name&gt;" for a valid POSIX shared memory name (an optional leading '/', then no '/'), else null.</summary>
    public static string? SharedMemoryPath(string name)
    {
        if (name.StartsWith('/')) name = name[1..];
        if (name.Length is 0 or > MaxShmName || name is "." or ".." || name.Contains('/') || name.Contains('\0')) return null;
        if (name.Any(c => c < ' ' || c == '\x7f')) return null;
#pragma warning disable S5443 // kitty t=s names a POSIX shared memory object, which lives here; the name is validated above and the read is O_NOFOLLOW
        return "/dev/shm/" + name;
#pragma warning restore S5443
    }

    /// <summary>Which file was read (device and inode on Linux and macOS), so only that one is ever deleted.</summary>
    public readonly record struct FileStamp(ulong Inode, uint DevMajor, uint DevMinor);

    /// <summary>
    /// Reads <paramref name="size"/> bytes (0 = to the end) at <paramref name="offset"/> from the regular file at
    /// <paramref name="path"/>; null on any failure. <paramref name="stamp"/> identifies the file read.
    /// </summary>
    public static byte[]? Read(string path, long offset, long size, out FileStamp stamp)
    {
        stamp = default;
        if (offset < 0 || size < 0 || IsVirtualPath(path)) return null;
        try
        {
            if (OperatingSystem.IsLinux()) return ReadLinux(path, offset, size, out stamp);
            if (OperatingSystem.IsMacOS()) return ReadMac(path, offset, size, out stamp);
            return ReadManaged(path, offset, size);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                      or DllNotFoundException or EntryPointNotFoundException or OutOfMemoryException)
        {
            return null;
        }
    }

    public static byte[]? Read(string path, long offset, long size) => Read(path, offset, size, out _);

    /// <summary>Unlinks <paramref name="path"/> if it still is the file <see cref="Read(string, long, long, out FileStamp)"/> read (no symlink is followed).</summary>
    public static void DeleteIfUnchanged(string path, FileStamp stamp)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (MacStat(path) is { } st && (st.Mode & S_IFMT) == S_IFREG && st.Inode == stamp.Inode && st.DevMajor == stamp.DevMajor)
                    MacUnlink(path);
                return;
            }
            if (!OperatingSystem.IsLinux()) { File.Delete(path); return; }
            if (Stat(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW) is { } now && (now.Mode & S_IFMT) == S_IFREG
                && now.Inode == stamp.Inode && now.DevMajor == stamp.DevMajor && now.DevMinor == stamp.DevMinor)
                Unlink(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { /* the file stays behind: that must not fail the image */ }
    }

    /// <summary>Paths that are never read, whatever is behind them (devices, process and kernel state).</summary>
    internal static bool IsVirtualPath(string path) =>
        path.StartsWith("/proc/", StringComparison.Ordinal) || path.StartsWith("/sys/", StringComparison.Ordinal)
#pragma warning disable S5443 // a string comparison that blocks device paths; no file is created or opened here
        || (path.StartsWith("/dev/", StringComparison.Ordinal) && !path.StartsWith("/dev/shm/", StringComparison.Ordinal))
#pragma warning restore S5443
        || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal); // UNC, \\.\ devices

    private static bool SizeOk(long fileLength, long offset, ref long size)
    {
        if (fileLength <= 0) return false;
        if (size == 0) size = fileLength - offset;
        return size > 0 && size <= ImageLimits.MaxEncodedBytes && offset <= fileLength - size;
    }

    // ---- Linux -----------------------------------------------------------------------------------------

    private const string Libc = "libc.so.6";
    private const int O_RDONLY = 0, O_NOCTTY = 0x100, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
    private const int AT_EMPTY_PATH = 0x1000, AT_SYMLINK_NOFOLLOW = 0x100, AT_FDCWD = -100;
    private const uint STATX_TYPE = 0x1, STATX_MODE = 0x2, STATX_INO = 0x100, STATX_SIZE = 0x200;
    private const int S_IFMT = 0xF000, S_IFREG = 0x8000;
    private const int EINTR = 4;

    /// <summary>O_NOFOLLOW differs between architectures (asm-generic vs x86).</summary>
    private static int O_NOFOLLOW => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 or Architecture.Arm or Architecture.Ppc64le => 0x8000,
        Architecture.X64 or Architecture.X86 or Architecture.RiscV64 or Architecture.LoongArch64 or Architecture.S390x => 0x20000,
        _ => throw new NotSupportedException("unknown architecture"),
    };

    /// <summary>Pseudo file systems whose "files" are kernel state, not images (statfs f_type).</summary>
    private static ReadOnlySpan<long> PseudoFileSystems =>
    [
        0x9fa0,      // proc
        0x62656572,  // sysfs
        0x64626720,  // debugfs
        0x74726163,  // tracefs
        0x73636673,  // securityfs
        0x27e0eb,    // cgroup
        0x63677270,  // cgroup2
        0x62656570,  // configfs
        0xde5e81e4,  // efivarfs
        0x6165676C,  // pstorefs
        0xcafe4a11,  // bpf
        0x1cd1,      // devpts
    ];

    [LibraryImport(Libc, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport(Libc, EntryPoint = "close")]
    private static partial int Close(int fd);

    [LibraryImport(Libc, EntryPoint = "pread", SetLastError = true)]
#pragma warning disable S6640 // libc fills caller-owned byte buffers passed as pointers
    private static unsafe partial nint PRead(int fd, byte* buffer, nint count, long offset);

    [LibraryImport(Libc, EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatX(int dirfd, string path, int flags, uint mask, byte* statx);

    [LibraryImport(Libc, EntryPoint = "fstatfs", SetLastError = true)]
    private static unsafe partial int FStatFs(int fd, byte* statfs);
#pragma warning restore S6640

    [LibraryImport(Libc, EntryPoint = "unlink", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Unlink(string path);

    /// <summary>The fields of struct statx we use (its layout is the same on every architecture).</summary>
    private readonly record struct FileId(int Mode, long Size, ulong Inode, uint DevMajor, uint DevMinor);

#pragma warning disable S6640 // statx fills a stackalloc buffer whose fields are read by offset
    private static unsafe FileId? Stat(int fd, string path, int flags)
#pragma warning restore S6640
    {
        byte* buf = stackalloc byte[256];
        if (StatX(fd, path, flags, STATX_TYPE | STATX_MODE | STATX_INO | STATX_SIZE, buf) != 0) return null;
        return new FileId(*(ushort*)(buf + 28), *(long*)(buf + 40), *(ulong*)(buf + 32), *(uint*)(buf + 136), *(uint*)(buf + 140));
    }

    private static byte[]? ReadLinux(string path, long offset, long size, out FileStamp stamp)
    {
        stamp = default;
        int fd;
        do fd = Open(path, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_NOCTTY | O_CLOEXEC);
        while (fd < 0 && Marshal.GetLastPInvokeError() == EINTR);
        if (fd < 0) return null;
        try
        {
            if (Stat(fd, "", AT_EMPTY_PATH) is not { } st || (st.Mode & S_IFMT) != S_IFREG) return null;
            if (!IsOrdinaryFileSystem(fd)) return null;
            if (!SizeOk(st.Size, offset, ref size)) return null;

            var data = new byte[size];
            if (!PReadAll(fd, data, offset)) return null;
            stamp = new FileStamp(st.Inode, st.DevMajor, st.DevMinor);
            return data;
        }
        finally
        {
            Close(fd);
        }
    }

    /// <summary>False when the file system can't be identified or is one of <see cref="PseudoFileSystems"/>.</summary>
#pragma warning disable S6640 // fstatfs fills a stackalloc buffer and f_type is read through a pointer
    private static unsafe bool IsOrdinaryFileSystem(int fd)
#pragma warning restore S6640
    {
        byte* fs = stackalloc byte[512];
        if (FStatFs(fd, fs) != 0) return false;
        long fsType = *(long*)fs; // f_type is the first field (a long) on 64-bit Linux
        if (RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.Arm) fsType = *(int*)fs;
        foreach (long pseudo in PseudoFileSystems)
            if ((fsType & 0xFFFFFFFF) == pseudo) return false;
        return true;
    }

    /// <summary>Fills <paramref name="data"/> from <paramref name="offset"/>; false on an error, EAGAIN, or if the file shrank.</summary>
#pragma warning disable S6640 // pins the array to hand pread a pointer
    private static unsafe bool PReadAll(int fd, byte[] data, long offset)
#pragma warning restore S6640
    {
        long done = 0;
        fixed (byte* p = data)
        {
            while (done < data.Length)
            {
                nint n = PRead(fd, p + done, (nint)Math.Min(data.Length - done, 1 << 24), offset + done);
                if (n < 0 && Marshal.GetLastPInvokeError() == EINTR) continue;
                if (n <= 0) return false; // error, EAGAIN, or the file shrank
                done += n;
            }
        }
        return true;
    }

    // ---- macOS -------------------------------------------------------------------------------------------
    // bsd/sys/fcntl.h; struct stat with 64-bit inodes (bsd/sys/stat.h): st_dev (int32) at 0, st_mode (uint16) at 4,
    // st_ino at 8, st_size at 96, 144 bytes in all. x86_64 names the 64-bit-inode functions with a $INODE64 suffix.

    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int MacO_NONBLOCK = 0x4, MacO_NOFOLLOW = 0x100, MacO_NOCTTY = 0x20000, MacO_CLOEXEC = 0x1000000;
    internal const int MacStatDevOffset = 0, MacStatModeOffset = 4, MacStatInoOffset = 8, MacStatSizeOffset = 96;

    [LibraryImport(LibSystem, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacOpen(string path, int flags);

    [LibraryImport(LibSystem, EntryPoint = "close")]
    private static partial int MacClose(int fd);

    [LibraryImport(LibSystem, EntryPoint = "pread", SetLastError = true)]
#pragma warning disable S6640 // libc fills caller-owned byte buffers passed as pointers
    private static unsafe partial nint MacPRead(int fd, byte* buffer, nint count, long offset);

    [LibraryImport(LibSystem, EntryPoint = "fstat", SetLastError = true)]
    private static unsafe partial int MacFStat(int fd, byte* stat);

    [LibraryImport(LibSystem, EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static unsafe partial int MacFStatInode64(int fd, byte* stat);

    [LibraryImport(LibSystem, EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int MacLStat(string path, byte* stat);

    [LibraryImport(LibSystem, EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int MacLStatInode64(string path, byte* stat);
#pragma warning restore S6640

    [LibraryImport(LibSystem, EntryPoint = "unlink", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MacUnlink(string path);

    private static bool Inode64Suffix => RuntimeInformation.ProcessArchitecture == Architecture.X64;

#pragma warning disable S6640 // reads struct stat fields at fixed offsets from the raw buffer
    private static unsafe FileId MacFileId(byte* st) =>
#pragma warning restore S6640
        new(*(ushort*)(st + MacStatModeOffset), *(long*)(st + MacStatSizeOffset), *(ulong*)(st + MacStatInoOffset), *(uint*)(st + MacStatDevOffset), 0);

#pragma warning disable S6640 // lstat fills a stackalloc buffer
    private static unsafe FileId? MacStat(string path)
#pragma warning restore S6640
    {
        byte* st = stackalloc byte[256];
        if ((Inode64Suffix ? MacLStatInode64(path, st) : MacLStat(path, st)) != 0) return null;
        return MacFileId(st);
    }

#pragma warning disable S6640 // fstat fills a stackalloc buffer
    private static unsafe byte[]? ReadMac(string path, long offset, long size, out FileStamp stamp)
#pragma warning restore S6640
    {
        stamp = default;
        int fd;
        do fd = MacOpen(path, O_RDONLY | MacO_NONBLOCK | MacO_NOFOLLOW | MacO_NOCTTY | MacO_CLOEXEC);
        while (fd < 0 && Marshal.GetLastPInvokeError() == EINTR);
        if (fd < 0) return null;
        try
        {
            byte* buf = stackalloc byte[256];
            if ((Inode64Suffix ? MacFStatInode64(fd, buf) : MacFStat(fd, buf)) != 0) return null;
            var st = MacFileId(buf);
            if ((st.Mode & S_IFMT) != S_IFREG || !SizeOk(st.Size, offset, ref size)) return null;

            var data = new byte[size];
            long done = 0;
            fixed (byte* p = data)
            {
                while (done < size)
                {
                    nint n = MacPRead(fd, p + done, (nint)Math.Min(size - done, 1 << 24), offset + done);
                    if (n < 0 && Marshal.GetLastPInvokeError() == EINTR) continue;
                    if (n <= 0) return null;
                    done += n;
                }
            }
            stamp = new FileStamp(st.Inode, st.DevMajor, 0);
            return data;
        }
        finally
        {
            MacClose(fd);
        }
    }

    // ---- Elsewhere ---------------------------------------------------------------------------------------

    private static byte[]? ReadManaged(string path, long offset, long size)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            return null;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = RandomAccess.GetLength(handle);
        if (!SizeOk(length, offset, ref size)) return null;
        var data = new byte[size];
        long done = 0;
        while (done < size)
        {
            int n = RandomAccess.Read(handle, data.AsSpan((int)done), offset + done);
            if (n <= 0) return null;
            done += n;
        }
        return data;
    }

    /// <summary>Decodes a base64 file name: UTF-8, no NUL. Null when it isn't one.</summary>
    public static string? DecodeName(ReadOnlySpan<byte> base64)
    {
        if (ImageDecoder.DecodeBase64(base64) is not { Length: > 0 and <= 4096 } bytes) return null;
        string name;
        try { name = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (ArgumentException) { return null; }
        return name.Contains('\0') ? null : name;
    }
}
