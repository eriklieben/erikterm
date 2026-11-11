using System.Diagnostics;
using System.Net.Sockets;

namespace ErikTerm.App.Ipc;

/// <summary>
/// Where instances put their control sockets, and how <c>erikterm cli</c> finds one.
/// Linux: <c>$XDG_RUNTIME_DIR/erikterm/</c> (else <c>$TMPDIR/erikterm-$USER/</c>), mode 0700. macOS has no
/// XDG_RUNTIME_DIR: <c>&lt;per-user temp dir&gt;/erikterm/</c> (confstr _CS_DARWIN_USER_TEMP_DIR, the /var/folders/…/T/
/// that $TMPDIR names, private to the user), or <c>/tmp/erikterm-&lt;uid&gt;/</c> when a socket path there would be longer
/// than sun_path allows (104 bytes on macOS). Since the fallback
/// lives in a shared directory where anyone could have made that name first, the directory is only used when it is
/// a real directory owned by us with mode 0700, and a socket is only trusted inside it when it is a socket we own.
/// Windows: <c>%LOCALAPPDATA%\erikterm\</c>. Each instance listens on <c>erikterm-&lt;pid&gt;.sock</c>;
/// <c>last-focused</c> holds the socket path of the instance whose window was focused last.
/// </summary>
internal static partial class IpcPaths
{
    public const string SocketVariable = "ERIKTERM_SOCKET";
    public const string PaneVariable = "ERIKTERM_PANE";
    public const string MarkerName = "last-focused";

    public static string Directory(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm");
        if (OperatingSystem.IsMacOS())
            return MacDirectory(DarwinUserTempDirectory() ?? env("TMPDIR") ?? Path.GetTempPath(), UnixFiles.EffectiveUid);
        if (env("XDG_RUNTIME_DIR") is { Length: > 0 } runtime) return Path.Combine(runtime, "erikterm");
        return Path.Combine(Path.GetTempPath(), "erikterm-" + Environment.UserName);
    }

    /// <summary>
    /// macOS: <paramref name="userTemp"/>/erikterm, unless the longest socket path in it wouldn't fit in sun_path (a
    /// very long temp directory); then /tmp/erikterm-&lt;uid&gt; (checked like any shared directory: ours, 0700).
    /// </summary>
    internal static string MacDirectory(string userTemp, uint uid)
    {
        string dir = userTemp.Length > 0 && userTemp[0] == '/' ? Path.Combine(userTemp, "erikterm") : "";
        if (dir.Length > 0 && FitsSocketPaths(dir, MacMaxSocketPathBytes)) return dir;
        #pragma warning disable S5443 // macOS fallback when the per-user temp path is too long for sun_path; EnsureDirectory creates it 0700 and refuses a symlink, another owner or wider access
        return "/tmp/erikterm-" + uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        #pragma warning restore S5443
    }

    /// <summary>Whether the longest instance socket erikterm makes in <paramref name="dir"/> (a 7-digit pid) fits.</summary>
    internal static bool FitsSocketPaths(string dir, int maxBytes) =>
        System.Text.Encoding.UTF8.GetByteCount(SocketFor(dir, 9_999_999)) <= maxBytes;

    /// <summary>confstr(_CS_DARWIN_USER_TEMP_DIR): the per-user temp directory whatever $TMPDIR says, or null.</summary>
    #pragma warning disable S6640 // confstr writes into a caller-supplied C buffer
    private static unsafe string? DarwinUserTempDirectory()
    #pragma warning restore S6640
    {
        try
        {
            byte* buf = stackalloc byte[1024];
            nuint n = ConfStr(65537 /* _CS_DARWIN_USER_TEMP_DIR, Libc include/unistd.h */, buf, 1024);
            return n is > 1 and <= 1024 ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)buf) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [System.Runtime.InteropServices.LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "confstr")]
    #pragma warning disable S6640 // native confstr signature
    private static unsafe partial nuint ConfStr(int name, byte* buf, nuint len);
    #pragma warning restore S6640

    public static string SocketFor(string dir, int pid) => Path.Combine(dir, $"erikterm-{pid}.sock");

    /// <summary>Longest Unix socket path: sun_path is 108 bytes with the terminating NUL on Linux, 104 on macOS (sys/un.h).</summary>
    public static int MaxSocketPathBytes => OperatingSystem.IsMacOS() ? MacMaxSocketPathBytes : LinuxMaxSocketPathBytes;

    public const int LinuxMaxSocketPathBytes = 107, MacMaxSocketPathBytes = 103;

    /// <summary>
    /// Creates the directory, private to the user (0700) on Unix, and checks that it is: an IOException when it is a
    /// symlink, not a directory, someone else's, or open to others (see <see cref="DirectoryProblem"/>).
    /// </summary>
    public static void EnsureDirectory(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(dir);
            return;
        }
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (UnixFiles.LStat(dir) is null) System.IO.Directory.CreateDirectory(dir, Private); // mkdir(0700): never wider
        if (!UnixFiles.Supported) { File.SetUnixFileMode(dir, Private); return; }
        // A directory of ours that is merely readable by others (an older umask) is tightened; anything else is refused.
        if (UnixFiles.LStat(dir) is { Type: UnixFiles.S_IFDIR } info && info.Uid == UnixFiles.EffectiveUid
            && (info.Permissions & 0x3F) != 0 && (info.Permissions & 0x12) == 0)
            File.SetUnixFileMode(dir, Private);
        if (DirectoryProblem(dir) is { } problem) throw new IOException($"refusing to use {dir}: {problem}");
    }

    /// <summary>Null when <paramref name="dir"/> is safe to keep sockets in (always on Windows and other Unix), else why not.</summary>
    public static string? DirectoryProblem(string dir) =>
        UnixFiles.Supported ? UnixFiles.WhyNotPrivateDirectory(UnixFiles.LStat(dir), UnixFiles.EffectiveUid) : null;

    /// <summary>An IOException when a Unix socket can't have this path (too long for sun_path, or a NUL in it).</summary>
    public static void CheckSocketPath(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        int bytes = System.Text.Encoding.UTF8.GetByteCount(path);
        if (bytes > MaxSocketPathBytes)
            throw new IOException($"socket path {path} is {bytes} bytes long; Unix sockets allow at most {MaxSocketPathBytes}");
        if (path.Contains('\0')) throw new IOException("socket path contains a NUL character");
    }

    /// <summary>
    /// True when <paramref name="socket"/> is one of our instance sockets in <paramref name="dir"/>: directly inside it,
    /// named erikterm-&lt;pid&gt;.sock, and (Linux, macOS) a socket owned by us — not something another user planted.
    /// </summary>
    public static bool IsOwnSocket(string dir, string socket)
    {
        string full, parent;
        try
        {
            full = Path.GetFullPath(socket);
            parent = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (!string.Equals(Path.GetDirectoryName(full), parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return false;
        string name = Path.GetFileName(full);
        if (!name.StartsWith("erikterm-", StringComparison.Ordinal) || !name.EndsWith(".sock", StringComparison.Ordinal)
            || !int.TryParse(name.AsSpan(9, name.Length - 9 - 5), System.Globalization.NumberStyles.None, null, out _))
            return false;
        if (!UnixFiles.Supported) return true;
        return UnixFiles.LStat(full) is { Type: UnixFiles.S_IFSOCK } info && info.Uid == UnixFiles.EffectiveUid;
    }

    /// <summary>Records <paramref name="socket"/> as the most recently focused instance (atomic replace).</summary>
    public static void WriteMarker(string dir, string socket)
    {
        string marker = Path.Combine(dir, MarkerName);
        string temp = marker + "." + Environment.ProcessId;
        File.WriteAllText(temp, socket);
        File.Move(temp, marker, overwrite: true);
    }

    public static string? ReadMarker(string dir)
    {
        try
        {
            return File.ReadAllText(Path.Combine(dir, MarkerName)).Trim() is { Length: > 0 } s ? s : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes the marker if it still names <paramref name="socket"/> (on exit).</summary>
    public static void ClearMarker(string dir, string socket)
    {
        try
        {
            if (ReadMarker(dir) == socket) File.Delete(Path.Combine(dir, MarkerName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a stale marker is checked for liveness when read */ }
    }

    /// <summary>
    /// The socket <c>erikterm cli</c> talks to when none is given: the last-focused instance if it is
    /// still listening, else the only — or newest — live one. Sockets left behind by instances that
    /// died are deleted along the way. Null when nothing is running.
    /// </summary>
    public static string? Discover(string dir)
    {
        if (System.IO.Directory.Exists(dir) && DirectoryProblem(dir) is not null) return null; // not ours: trust nothing in it
        if (ReadMarker(dir) is { } marked && IsOwnSocket(dir, marked) && IsAlive(marked)) return marked;
        string[] sockets;
        try
        {
            if (!System.IO.Directory.Exists(dir)) return null;
            sockets = System.IO.Directory.GetFiles(dir, "erikterm-*.sock");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return NewestLiveSocket(dir, sockets);
    }

    /// <summary>The most recently started live socket of <paramref name="sockets"/>; dead ones are deleted when their process is gone.</summary>
    private static string? NewestLiveSocket(string dir, string[] sockets)
    {
        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string s in sockets)
        {
            if (!IsOwnSocket(dir, s)) continue;
            if (!IsAlive(s))
            {
                if (!ProcessRunning(s)) TryDelete(s);
                continue;
            }
            var t = File.GetLastWriteTimeUtc(s);
            if (best is null || t > bestTime) (best, bestTime) = (s, t);
        }
        return best;
    }

    /// <summary>
    /// Every instance that is listening: the last-focused one first, then the others newest first (a second launch with
    /// single_instance tries them in this order). Dead sockets are skipped, and deleted when their process is gone.
    /// </summary>
    public static List<string> LiveSockets(string dir)
    {
        var list = new List<string>();
        string? marked = ReadMarker(dir) is { } m && IsAlive(m) ? m : null;
        if (marked is not null) list.Add(marked);
        string[] sockets;
        try
        {
            if (!System.IO.Directory.Exists(dir)) return list;
            sockets = System.IO.Directory.GetFiles(dir, "erikterm-*.sock");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return list;
        }
        var others = new List<(string Socket, DateTime Time)>();
        foreach (string s in sockets)
        {
            if (s == marked) continue;
            if (!IsAlive(s))
            {
                if (!ProcessRunning(s)) TryDelete(s);
                continue;
            }
            others.Add((s, File.GetLastWriteTimeUtc(s)));
        }
        others.Sort((a, b) => b.Time.CompareTo(a.Time));
        foreach (var (s, _) in others) list.Add(s);
        return list;
    }

    /// <summary>True when something accepts connections on the socket.</summary>
    public static bool IsAlive(string socket)
    {
        if (!File.Exists(socket)) return false;
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(socket));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Whether the pid in "erikterm-&lt;pid&gt;.sock" is a running process (unknown names count as running).</summary>
    private static bool ProcessRunning(string socket)
    {
        string name = Path.GetFileNameWithoutExtension(socket);
        if (!int.TryParse(name.AsSpan("erikterm-".Length), out int pid)) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: the file stays until a later sweep */ }
    }
}
