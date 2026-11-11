using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using ErikTerm.App.Ipc;
using ErikTerm.App.Native;

namespace ErikTerm.App.Tmux;

/// <summary>
/// tmux emulation, process half: each pane gets <c>$TMUX</c> / <c>$TMUX_PANE</c> and a shim directory first on
/// <c>PATH</c> whose <c>tmux</c> is erikterm itself (TmuxShim.cs), so programs that drive tmux — Claude Code's agent
/// panes — open erikterm panes instead. One shim directory and control socket serve every window; the requests the
/// shim adds are handled by the window holding the target (WindowFeatures/WindowTmux.cs). Real tmux keeps working: the shim
/// hands interactive use to it.
/// </summary>
internal sealed class TmuxShimDirectory
{
    /// <summary>Prefix of the directories <see cref="ShimExecutableCopy"/> makes (not a shim directory: never on PATH).</summary>
    private const string ShimExePrefix = "tmux-exe-";

    private string _ipcDir = "";

    /// <summary>The shim directory; null when there is none (the emulation can't work in this process).</summary>
    public string? ShimDir { get; private set; }

    /// <summary>Makes &lt;IPC directory&gt;/shim-&lt;pid&gt;/tmux → this executable (IpcPaths.Directory).</summary>
    public void Start(string ipcDir)
    {
        _ipcDir = ipcDir;
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            RemoveStaleShims(ipcDir);
            string dir = TmuxEnvironment.ShimDirectory(ipcDir, Environment.ProcessId);
            IpcPaths.EnsureDirectory(dir);
            string link = Path.Combine(dir, TmuxEnvironment.ShimFileName);
            if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
            if (OperatingSystem.IsWindows()) LinkOrCopyShim(link, exe);
            else File.CreateSymbolicLink(link, exe);
            ShimDir = dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"erikterm: no tmux emulation (couldn't make the shim directory): {ex.Message}");
        }
    }

    /// <summary>
    /// The Windows shim: a hard link, since a symbolic link there needs Developer Mode or elevation. A hard link
    /// only works within one volume, so when erikterm lives on another drive than %LOCALAPPDATA% one copy of the
    /// executable is kept per build under the IPC directory and the shim is hard-linked from that (same volume);
    /// copying straight into the shim directory is the last resort.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void LinkOrCopyShim(string link, string exe)
    {
        if (WindowsFiles.CreateHardLink(link, exe)) return;
        if (ShimExecutableCopy(exe) is { } copy && WindowsFiles.CreateHardLink(link, copy)) return;
        File.Copy(exe, link, overwrite: true);
    }

    /// <summary>
    /// The copy of this executable in the IPC directory, made if it isn't there. Its directory name holds the
    /// executable's size and write time, so a rebuilt erikterm never runs a stale copy; the old ones go with the
    /// stale shims. Null when it can't be made (the caller copies into the shim directory instead).
    /// </summary>
    private string? ShimExecutableCopy(string exe)
    {
        try
        {
            string dir = Path.Combine(_ipcDir, ShimExecutableDirectory(new FileInfo(exe)));
            string copy = Path.Combine(dir, TmuxEnvironment.ShimFileName);
            if (File.Exists(copy)) return copy;
            Directory.CreateDirectory(dir);
            // Another instance may be copying the same build: write a private file and move it into place.
            string temp = Path.Combine(dir, $"{Environment.ProcessId}.tmp");
            File.Copy(exe, temp, overwrite: true);
            try { File.Move(temp, copy); }
            catch (IOException) { File.Delete(temp); } // the other instance got there first
            return File.Exists(copy) ? copy : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Name of the directory holding the copy of <paramref name="exe"/>: one per build.</summary>
    private static string ShimExecutableDirectory(FileInfo exe) =>
        string.Create(CultureInfo.InvariantCulture, $"{ShimExePrefix}{exe.Length:x}-{exe.LastWriteTimeUtc.Ticks:x}");

    /// <summary>Removes the shim directory (the control socket is going).</summary>
    public void Stop()
    {
        if (ShimDir is null) return;
        try { Directory.Delete(ShimDir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover directory is swept by the next instance */ }
        ShimDir = null;
    }

    /// <summary>Shim directories of instances that are gone (crashed, killed), and copies of executables that went with them.</summary>
    private static void RemoveStaleShims(string ipcDir)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(ipcDir, TmuxEnvironment.ShimPrefix + "*"))
            {
                if (!int.TryParse(Path.GetFileName(dir).AsSpan(TmuxEnvironment.ShimPrefix.Length), out int pid) || pid == Environment.ProcessId) continue;
                if (ProcessAlive(pid)) continue;
                TryDeleteDirectory(dir);
            }
            if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return;
            string current = ShimExecutableDirectory(new FileInfo(exe));
            var olderBuilds = Directory.GetDirectories(ipcDir, ShimExePrefix + "*")
                .Where(dir => !string.Equals(Path.GetFileName(dir), current, StringComparison.OrdinalIgnoreCase));
            foreach (string dir in olderBuilds)
                TryDeleteDirectory(dir); // fails while another instance still runs it, and goes next time
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort: runs again when the next instance starts */ }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still in use: the next instance's sweep removes it */ }
    }

    /// <summary>Whether a process is still running. Linux and macOS: <see cref="Pty.ProcessInfo.IsRunning"/> (/proc, kill 0);
    /// Windows: ask the OS.</summary>
    private static bool ProcessAlive(int pid)
    {
        if (!OperatingSystem.IsWindows()) return Pty.ProcessInfo.IsRunning(pid);
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

    /// <summary>TMUX, TMUX_PANE and the shim on PATH — or none of them (emulation off for the window, or no control socket).</summary>
    public void AddEnvironment(Dictionary<string, string> env, int paneId, bool emulation, string? socket)
    {
        if (emulation && ShimDir is not null && socket is not null)
            TmuxEnvironment.Apply(env, ShimDir, socket, Environment.ProcessId, paneId);
        else
            TmuxEnvironment.Remove(env); // e.g. a fake TMUX inherited from the erikterm we were started in
    }
}
