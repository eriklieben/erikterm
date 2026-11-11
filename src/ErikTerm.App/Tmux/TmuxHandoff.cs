using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ErikTerm.App.Tmux;

internal enum TmuxRoute
{
    /// <summary>erikterm answers it (panes, tabs, formats).</summary>
    Emulate,
    /// <summary>Hand the whole command line to the real tmux.</summary>
    Real,
}

internal readonly record struct TmuxDecision(TmuxRoute Route, string Reason);

/// <summary>
/// When <c>tmux</c> in an erikterm pane means real tmux. The emulation only covers the fake session that
/// <c>$TMUX</c> describes; anything that starts, attaches to or names another server goes to the real binary
/// (with the emulation's environment removed, so it runs as if erikterm weren't pretending).
/// </summary>
internal static class TmuxHandoff
{
    /// <summary>Commands that only make sense for a real server.</summary>
    private static readonly HashSet<string> RealOnly = ["new-session", "attach-session", "kill-server"];

    /// <param name="inv">The parsed command line.</param>
    /// <param name="fakeSocket">The socket in a fake <c>$TMUX</c> (this erikterm's control socket), or null when <c>$TMUX</c> is unset or real.</param>
    /// <param name="realAvailable">Whether a real tmux binary was found.</param>
    /// <param name="realServerRunning">Whether a real tmux server is listening on its default socket (commands the
    /// emulation doesn't know only make sense there; without one they get "unknown command").</param>
    public static TmuxDecision Decide(TmuxInvocation inv, string? fakeSocket, bool realAvailable, bool realServerRunning = true)
    {
        if (fakeSocket is null) return new(TmuxRoute.Real, "not inside erikterm's tmux emulation");
        if (inv.SocketName is not null) return new(TmuxRoute.Real, $"-L {inv.SocketName}: another server");
        if (inv.SocketPath is not null && !SamePath(inv.SocketPath, fakeSocket)) return new(TmuxRoute.Real, $"-S {inv.SocketPath}: another server");
        if (inv.NeedsRealServer) return new(TmuxRoute.Real, "server options only real tmux has");
        if (inv.Version) return new(TmuxRoute.Emulate, "-V");
        if (inv.UnknownCommand is { } unknown)
            return realAvailable && realServerRunning ? new(TmuxRoute.Real, $"'{unknown}' isn't emulated") : new(TmuxRoute.Emulate, $"unknown command {unknown}");
        if (inv.Commands.Count == 0) return new(TmuxRoute.Real, "interactive session");
        string first = inv.Commands[0].Name;
        if (RealOnly.Contains(first)) return new(TmuxRoute.Real, $"{first} starts or attaches a real session");
        if (first == "list-sessions" && realAvailable) return new(TmuxRoute.Real, "list the real sessions");
        return new(TmuxRoute.Emulate, "fake session");
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return a == b;
        }
    }

    /// <summary>
    /// The real tmux on <paramref name="path"/> (already scrubbed of shim directories): the first executable
    /// <c>tmux</c> that isn't erikterm itself.
    /// </summary>
    public static string? FindReal(string? path, string? selfExecutable)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string? self = Canonical(selfExecutable);
        foreach (string dir in path.Split(Path.PathSeparator))
        {
            if (dir.Length == 0 || TmuxEnvironment.IsShimDirectory(dir)) continue;
            string candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "tmux.exe" : "tmux");
            if (IsRealTmux(candidate, self, selfExecutable)) return candidate;
        }
        return null;
    }

    private static bool IsRealTmux(string candidate, string? self, string? selfExecutable)
    {
        if (!File.Exists(candidate)) return false;
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0) return false;
        if (self is not null && Canonical(candidate) == self) return false;
        return !IsThisExecutable(candidate, selfExecutable);
    }

    /// <summary>
    /// Whether a <c>tmux.exe</c> is this executable under another name. The Windows shim is a hard link, which has
    /// no target to resolve and looks like an ordinary file, so running it as "real tmux" would recurse forever;
    /// same size and timestamps is close enough to be sure, since both are copies or links of one build.
    /// </summary>
    private static bool IsThisExecutable(string candidate, string? selfExecutable)
    {
        if (!OperatingSystem.IsWindows() || selfExecutable is null) return false;
        try
        {
            var a = new FileInfo(candidate);
            var b = new FileInfo(selfExecutable);
            return a.Exists && b.Exists && a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string? Canonical(string? file)
    {
        if (string.IsNullOrEmpty(file)) return null;
        try
        {
            var info = new FileInfo(file);
            return (info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return file;
        }
    }
}

/// <summary>Runs the real tmux binary.</summary>
internal interface IRealTmux
{
    /// <summary>Path of the real binary, or null when tmux isn't installed.</summary>
    string? Path { get; }
    /// <summary>Replaces this process with real tmux (<c>execve</c>); returns an exit status only if that fails.</summary>
    int Exec(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, TextWriter stderr);
    /// <summary>Runs real tmux quietly and returns its exit status (e.g. to ask whether it knows a pane).</summary>
    int Run(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env);
    /// <summary>A real server listens on the default socket ($TMUX_TMPDIR or /tmp, then tmux-UID/default).</summary>
    bool ServerRunning { get; }
}

#pragma warning disable S6640 // execve takes native argv and envp arrays
internal sealed unsafe partial class RealTmux(string? path, string? tmuxTmpDir) : IRealTmux
#pragma warning restore S6640
{
    public string? Path { get; } = path;

    public bool ServerRunning
    {
        get
        {
            if (Path is null || OperatingSystem.IsWindows()) return false;
            #pragma warning disable S5443 // tmux's own default socket location, only probed to see whether a server listens there; nothing is created or trusted in it
            string dir = tmuxTmpDir is { Length: > 0 } t ? t : "/tmp";
            #pragma warning restore S5443
            string socket = System.IO.Path.Combine(dir, $"tmux-{(OperatingSystem.IsMacOS() ? mac_getuid() : getuid())}", "default");
            if (!File.Exists(socket)) return false;
            try
            {
                // A socket file outlives a crashed server: only a listening one counts.
                using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                s.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(socket));
                return true;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public int Exec(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, TextWriter stderr)
    {
        if (Path is null) return 127;
        stderr.Flush();
        Console.Out.Flush();
        if (!OperatingSystem.IsWindows())
        {
            var allocations = new List<nint>();
            byte** argv = (byte**)NativeMemory.AllocZeroed((nuint)(args.Count + 2), (nuint)sizeof(nint));
            byte** envp = (byte**)NativeMemory.AllocZeroed((nuint)(env.Count + 1), (nuint)sizeof(nint));
            try
            {
                byte* Utf8(string s)
                {
                    nint p = Marshal.StringToCoTaskMemUTF8(s);
                    allocations.Add(p);
                    return (byte*)p;
                }
                argv[0] = Utf8("tmux");
                for (int i = 0; i < args.Count; i++) argv[i + 1] = Utf8(args[i]);
                int k = 0;
                foreach (var (key, value) in env) envp[k++] = Utf8($"{key}={value}");
                if (OperatingSystem.IsMacOS()) mac_execve(Utf8(Path), argv, envp);
                else execve(Utf8(Path), argv, envp);
                // Only reached on failure: fall back to running it as a child.
            }
            finally
            {
                foreach (nint p in allocations) Marshal.FreeCoTaskMem(p);
                NativeMemory.Free(argv);
                NativeMemory.Free(envp);
            }
        }
        try
        {
            using var p = Process.Start(Start(args, env, quiet: false))!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            stderr.WriteLine($"tmux: {Path}: {ex.Message}");
            return 127;
        }
    }

    public int Run(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        if (Path is null) return 127;
        try
        {
            using var p = Process.Start(Start(args, env, quiet: true))!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(); } catch (InvalidOperationException) { /* it exited on its own */ }
                return 1;
            }
            return p.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 127;
        }
    }

    private ProcessStartInfo Start(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, bool quiet)
    {
        var psi = new ProcessStartInfo(Path!) { UseShellExecute = false, RedirectStandardOutput = quiet, RedirectStandardError = quiet };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }

    [LibraryImport("libc.so.6", SetLastError = true)]
    private static partial int execve(byte* path, byte** argv, byte** envp);

    [LibraryImport("libc.so.6")]
    private static partial uint getuid();

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "execve", SetLastError = true)]
    private static partial int mac_execve(byte* path, byte** argv, byte** envp);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getuid")]
    private static partial uint mac_getuid();
}
