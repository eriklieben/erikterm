using System.Runtime.InteropServices;
using System.Text;

namespace ErikTerm.Pty;

/// <summary>
/// What runs in a terminal, beyond <see cref="Pty.ForegroundProcessName"/>: the foreground job's argv and a
/// process's working directory. Linux reads <c>/proc</c>; macOS asks libproc (<c>proc_pidpath</c>, <c>proc_pidinfo</c>
/// PROC_PIDVNODEPATHINFO) and <c>sysctl</c> KERN_PROCARGS2; Windows QueryFullProcessImageNameW and the process's
/// PEB (<see cref="WindowsPeb"/>), whose command line is split by CommandLineToArgvW's rules. Null when unknown.
/// </summary>
#pragma warning disable S6640 // sysctl, proc_pidinfo and readlink fill stackalloc buffers through pointers
public static unsafe class ProcessInfo
#pragma warning restore S6640
{
    /// <summary>
    /// The terminal's foreground process group (its leader's pid), or -1. Windows has no process groups: there it is
    /// the foreground process (<see cref="WindowsProcessTree"/>), and the pane's own pid while the shell is in front,
    /// as on Unix.
    /// </summary>
    public static int ForegroundProcessGroup(this Pty pty) => pty.ForegroundProcessId;

    /// <summary>argv of the foreground process group's leader (e.g. ["nvim", "notes.md"]), or null.</summary>
    public static string[]? ForegroundArguments(this Pty pty)
    {
        int pgid = pty.ForegroundProcessGroup();
        return pgid > 0 ? CommandLine(pgid) : null;
    }

    /// <summary>
    /// A process's argv (Linux: <c>/proc/&lt;pid&gt;/cmdline</c>; macOS: KERN_PROCARGS2; Windows: its command line, split
    /// like CommandLineToArgvW), or null (gone, a kernel thread, not ours — Windows: elevated when erikterm isn't).
    /// </summary>
    public static string[]? CommandLine(int pid)
    {
        if (pid <= 0) return null;
        if (OperatingSystem.IsMacOS()) return MacCommandLine(pid);
        if (OperatingSystem.IsWindows())
            return WindowsProcesses.CommandLine(pid) is { } line && WindowsCommandLine.Parse(line) is { Count: > 0 } argv && argv[0].Length > 0
                ? [.. argv] : null;
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            byte[] raw = File.ReadAllBytes($"/proc/{pid}/cmdline");
            return ParseCommandLine(raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Splits NUL-separated argv bytes (the trailing NUL is optional).</summary>
    internal static string[]? ParseCommandLine(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty) return null;
        if (raw[^1] == 0) raw = raw[..^1];
        var args = new List<string>();
        foreach (var range in raw.Split((byte)0)) args.Add(Encoding.UTF8.GetString(raw[range]));
        return args.Count > 0 && args[0].Length > 0 ? args.ToArray() : null;
    }

    /// <summary>A process's executable (Linux: <c>/proc/&lt;pid&gt;/exe</c>; macOS: proc_pidpath; Windows: QueryFullProcessImageNameW), or null (gone, not ours to read).</summary>
    public static string? ExecutablePath(int pid)
    {
        if (pid <= 0) return null;
        if (OperatingSystem.IsMacOS()) return MacExecutablePath(pid);
        if (OperatingSystem.IsWindows()) return WindowsProcesses.ImagePath(pid);
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            return new FileInfo($"/proc/{pid}/exe").LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// A process's current directory (Linux: <c>/proc/&lt;pid&gt;/cwd</c>; macOS: PROC_PIDVNODEPATHINFO; Windows: the
    /// PEB's CurrentDirectory), or null. Windows: PowerShell doesn't move its process directory on Set-Location (only
    /// the programs it starts get the new one), so for pwsh at its prompt this is where it started; OSC 7 says more.
    /// </summary>
    public static string? WorkingDirectory(int pid)
    {
        if (pid <= 0) return null;
        if (OperatingSystem.IsMacOS())
        {
            string? dir = MacWorkingDirectory(pid);
            return dir is not null && Directory.Exists(dir) ? dir : null;
        }
        if (OperatingSystem.IsWindows())
        {
            string? dir = WindowsProcesses.CurrentDirectory(pid);
            return dir is not null && Directory.Exists(dir) ? dir : null;
        }
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            string? target = new DirectoryInfo($"/proc/{pid}/cwd").LinkTarget;
            return target is not null && Directory.Exists(target) ? target : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Whether a process with this pid exists (Linux: /proc; macOS: kill(pid, 0), where EPERM means someone else's;
    /// Windows: OpenProcess, where access denied means someone else's).
    /// </summary>
    public static bool IsRunning(int pid)
    {
        if (pid <= 0) return false;
        if (OperatingSystem.IsWindows()) return WindowsProcesses.IsRunning(pid);
        if (OperatingSystem.IsMacOS())
            return Native.kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() == Native.EPERM;
        return Directory.Exists($"/proc/{pid}");
    }

    // ---- macOS -------------------------------------------------------------------------------------------

    /// <summary>
    /// The foreground process's name for <see cref="Pty.ForegroundProcessName"/> (proc_name: the executable's file name
    /// as exec'd, up to 32 bytes). A process exec'd through a symlink is named after the link, so the tmux shim
    /// (shim-PID/tmux → erikterm) reports "tmux"; there it becomes "erikterm-tmux", what the shim calls itself on Linux.
    /// </summary>
    internal static string? MacProcessName(int pid)
    {
        byte* buf = stackalloc byte[64];
        new Span<byte>(buf, 64).Clear();
        int n = Native.proc_name(pid, buf, 64);
        if (n <= 0) return null;
        string name = Encoding.UTF8.GetString(buf, Math.Min(n, 63));
        if (name == "tmux" && IsSelf(MacExecutablePath(pid))) return "erikterm-tmux";
        return name;
    }

    private static string? _self;

    private static bool IsSelf(string? exe)
    {
        if (exe is null) return false;
        _self ??= Environment.ProcessPath is { } p ? ResolveLinks(p) : "";
        return _self.Length > 0 && ResolveLinks(exe) == _self;
    }

    private static string ResolveLinks(string path)
    {
        try { return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path; }
    }

    private static string? MacExecutablePath(int pid)
    {
        byte* buf = stackalloc byte[PtyConstants.PROC_PIDPATHINFO_MAXSIZE];
        int n = Native.proc_pidpath(pid, buf, PtyConstants.PROC_PIDPATHINFO_MAXSIZE);
        return n > 0 ? Encoding.UTF8.GetString(buf, Math.Min(n, PtyConstants.PROC_PIDPATHINFO_MAXSIZE)) : null;
    }

    private static string? MacWorkingDirectory(int pid)
    {
        byte* info = stackalloc byte[PtyConstants.ProcVnodePathInfoSize];
        int n = Native.proc_pidinfo(pid, PtyConstants.PROC_PIDVNODEPATHINFO, 0, info, PtyConstants.ProcVnodePathInfoSize);
        if (n < PtyConstants.CwdPathOffset + 1) return null;
        return CString(new ReadOnlySpan<byte>(info + PtyConstants.CwdPathOffset, PtyConstants.MaxPathLen)) is { Length: > 0 } dir ? dir : null;
    }

    private static string[]? MacCommandLine(int pid)
    {
        int* mib = stackalloc int[3];
        mib[0] = PtyConstants.CTL_KERN;
        mib[1] = PtyConstants.KERN_ARGMAX;
        int argMax = 0;
        nuint size = sizeof(int);
        if (Native.sysctl(mib, 2, &argMax, &size, null, 0) != 0 || argMax <= 0) argMax = 1 << 20;
        mib[1] = PtyConstants.KERN_PROCARGS2;
        mib[2] = pid;
        byte[] buffer = new byte[argMax];
        fixed (byte* p = buffer)
        {
            size = (nuint)buffer.Length;
            if (Native.sysctl(mib, 3, p, &size, null, 0) != 0) return null;
        }
        return ParseProcArgs2(buffer.AsSpan(0, (int)Math.Min(size, (nuint)buffer.Length)));
    }

    /// <summary>
    /// Parses KERN_PROCARGS2: argc (a native-endian int), the executable path, NUL padding, then argc NUL-terminated
    /// arguments (the environment follows and is ignored). Null when it doesn't parse.
    /// </summary>
    internal static string[]? ParseProcArgs2(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < sizeof(int)) return null;
        int argc = MemoryMarshal.Read<int>(raw);
        if (argc <= 0 || argc > 1 << 16) return null;
        var rest = raw[sizeof(int)..];
        int end = rest.IndexOf((byte)0);
        if (end < 0) return null;
        rest = rest[end..];
        int start = rest.IndexOfAnyExcept((byte)0);
        if (start < 0) return null;
        rest = rest[start..];
        var args = new string[argc];
        for (int i = 0; i < argc; i++)
        {
            int nul = rest.IndexOf((byte)0);
            if (nul < 0)
            {
                if (i == argc - 1 && rest.Length > 0) { args[i] = Encoding.UTF8.GetString(rest); break; }
                return null;
            }
            args[i] = Encoding.UTF8.GetString(rest[..nul]);
            rest = rest[(nul + 1)..];
        }
        return args[0].Length > 0 ? args : null;
    }

    private static string CString(ReadOnlySpan<byte> raw)
    {
        int nul = raw.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul < 0 ? raw : raw[..nul]);
    }
}
