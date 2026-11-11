using System.Runtime.InteropServices;

namespace ErikTerm.App;

/// <summary>
/// Internal errors with their stack traces, for bug reports: <c>$XDG_STATE_HOME/erikterm/crash-&lt;time&gt;.log</c> (default
/// ~/.local/state; Windows %LOCALAPPDATA%\erikterm), 0600. Fatal errors (Program.Main) and errors the main loop survived
/// (one window's frame or event, Host/WindowErrors.cs) both go here; About names the newest file. Never throws.
/// </summary>
internal static class CrashLog
{
    private const int KeepFiles = 20;

    /// <summary>The directory, from XDG_STATE_HOME (or <paramref name="stateHome"/>, for tests).</summary>
    public static string Directory(string? stateHome = null)
    {
        if (stateHome is null && OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm");
        string state = stateHome
            ?? (Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)
                ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"));
        return Path.Combine(state, "erikterm");
    }

    /// <summary>Writes a log for <paramref name="context"/> and <paramref name="ex"/> (with its stack). Returns its path, or null.</summary>
    public static string? Write(string context, Exception? ex, string? stateHome = null, DateTime? now = null)
    {
        try
        {
            string dir = Directory(stateHome);
            System.IO.Directory.CreateDirectory(dir);
            var time = now ?? DateTime.Now;
            string path = Path.Combine(dir, $"crash-{time:yyyyMMdd-HHmmss-fff}.log");
            string text =
                $"erikterm {typeof(CrashLog).Assembly.GetName().Version} ({RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription})\n" +
                $"time: {time:yyyy-MM-dd HH:mm:ss.fff zzz}\n" +
                $"pid: {Environment.ProcessId}\n" +
                $"what: {context}\n\n" +
                (ex?.ToString() ?? "(no exception)") + "\n";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(path, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(text);
            Prune(dir);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The newest crash log, or null.</summary>
    public static (string Path, DateTime Time)? Latest(string? stateHome = null)
    {
        try
        {
            var dir = new DirectoryInfo(Directory(stateHome));
            if (!dir.Exists) return null;
            FileInfo? newest = null;
            foreach (var f in dir.EnumerateFiles("crash-*.log"))
                if (newest is null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
            return newest is null ? null : (newest.FullName, newest.LastWriteTime);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Keeps the newest <see cref="KeepFiles"/> logs.</summary>
    private static void Prune(string dir)
    {
        var files = new DirectoryInfo(dir).GetFiles("crash-*.log");
        if (files.Length <= KeepFiles) return;
        Array.Sort(files, (a, b) => string.CompareOrdinal(b.Name, a.Name)); // names sort by time
        for (int i = KeepFiles; i < files.Length; i++)
        {
            try { files[i].Delete(); }
            catch (IOException) { /* in use or already gone: the next prune tries again */ }
        }
    }
}
