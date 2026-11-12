using System.Runtime.InteropServices;
using System.Text;

namespace ErikTerm.IntegrationTests.Harness;

/// <summary>
/// What differs between Linux, macOS and Windows for the scenarios: the shell the panes run, how a path is written for
/// it, the opener erikterm hands links to, and (Windows) the window handle of a run that has a real window.
/// <para>
/// Windows: the panes run Git for Windows' sh (usr\bin\sh.exe, the MSYS2 one: Git's bin\sh.exe is a launcher, and
/// erikterm would see the launcher's directory, not the shell's), so the scenarios type the same commands everywhere.
/// GitHub's Windows runners have Git; ERIKTERM_IT_SH names another sh.
/// </para>
/// </summary>
internal static class Platform
{
    private static readonly Lazy<string> ShellPath = new(FindShell);

    /// <summary>The sh the panes run: /bin/sh, or Git for Windows' on Windows.</summary>
    public static string Shell => ShellPath.Value;

    /// <summary>A TOML literal string (no escapes: Windows paths keep their backslashes).</summary>
    public static string Toml(string value) =>
        value.Contains('\'') ? throw new ArgumentException($"no single quote in a TOML literal string: {value}") : "'" + value + "'";

    /// <summary>A path as the pane's sh takes it in a command line: forward slashes on Windows (MSYS2 takes D:/a/b).</summary>
    public static string ShPath(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    /// <summary>
    /// The pane's current directory as sh expands it for an OSC 7 file:// URL: $PWD, or on Windows the Windows path
    /// (/D:/a/b; MSYS2's $PWD is /d/a/b, which erikterm doesn't know).
    /// </summary>
    public static string ShellPwd => OperatingSystem.IsWindows() ? "/$(pwd -W)" : "$PWD";

    /// <summary>The program erikterm opens links with (LinkOpener.SystemOpener), as the stand-in records it.</summary>
    public static string Opener => OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";

    /// <summary>Paths compare without case on Windows (and erikterm may report D: where the test wrote d:).</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string FindShell()
    {
        if (Environment.GetEnvironmentVariable("ERIKTERM_IT_SH") is { Length: > 0 } own) return own;
        if (!OperatingSystem.IsWindows()) return "/bin/sh";
        // git.exe is in Git\cmd, Git\bin or Git\mingw64\bin: the sh is Git\usr\bin\sh.exe.
        var roots = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(d => File.Exists(Path.Combine(d.Trim('"'), "git.exe")))
            .Select(d => Path.GetFullPath(d.Trim('"')))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd"));
        foreach (string dir in roots)
            for (var up = new DirectoryInfo(dir); up is not null; up = up.Parent)
                if (Path.Combine(up.FullName, "usr", "bin", "sh.exe") is var sh && File.Exists(sh)) return sh;
        throw new InvalidOperationException("the scenarios need Git for Windows' sh (usr\\bin\\sh.exe next to git); set ERIKTERM_IT_SH");
    }

    /// <summary>
    /// Where the runs' directories go: /tmp (Linux), /private/tmp (macOS: /tmp is a link to it, and erikterm reports the
    /// resolved path), and on Windows the runner's temp directory (RUNNER_TEMP) or the long form of %TEMP% (its 8.3
    /// form, C:\Users\RUNNER~1, is not what a shell or erikterm reports back).
    /// </summary>
    public static string TempRoot()
    {
        if (Environment.GetEnvironmentVariable("ERIKTERM_IT_ROOT") is { Length: > 0 } own) return own;
        if (OperatingSystem.IsMacOS()) return "/private/tmp";
        if (!OperatingSystem.IsWindows()) return "/tmp";
        if (Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } runner && Directory.Exists(runner)) return runner;
        return LongPath(Path.GetTempPath());
    }

    private static string LongPath(string path)
    {
        var buffer = new StringBuilder(1024);
        uint n = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        return n > 0 && n < buffer.Capacity ? buffer.ToString() : path;
    }

    /// <summary>The effective user id on macOS (the /tmp/erikterm-UID socket directory).</summary>
    public static uint MacEffectiveUid => OperatingSystem.IsMacOS() ? geteuid() : 0;

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "geteuid")]
    private static extern uint geteuid();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);

    // ---- Windows: a run's window --------------------------------------------------------------------------------

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    public static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    public static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>The visible top-level windows of a process (Windows), in z order.</summary>
    public static List<nint> WindowsOf(int pid)
    {
        var found = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out uint owner) != 0 && owner == pid && IsWindowVisible(hwnd)) found.Add(hwnd);
            return true;
        }, 0);
        return found;
    }

    /// <summary>WM_NCHITTEST at a point of the screen: what Windows would do with a press there (HT* codes).</summary>
    public static int HitTest(nint hwnd, int screenX, int screenY) =>
        (int)SendMessage(hwnd, 0x0084 /* WM_NCHITTEST */, 0, (nint)((screenY << 16) | (screenX & 0xFFFF)));
}

/// <summary>
/// A scenario that opens a real window (SDL's own video driver for the platform, not the offscreen one): what only
/// exists with a window, such as the Windows UI Automation provider and the Win32 hit test. Never on Linux, and only
/// where ERIKTERM_IT_DESKTOP=1 says a window may open (the integration jobs on GitHub's Windows and macOS runners,
/// which have a desktop session nobody uses): anywhere else the test is reported as skipped, with this reason.
/// </summary>
internal sealed class DesktopAttribute()
    : SkipAttribute("opens a real window: runs where ERIKTERM_IT_DESKTOP=1 (CI's Windows and macOS integration jobs)")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("ERIKTERM_IT_DESKTOP") != "1");
}
