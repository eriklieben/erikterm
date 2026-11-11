using System.Globalization;

namespace ErikTerm.App.Tmux;

/// <summary>
/// The environment that makes a pane look like a tmux pane, and how to undo it for real tmux.
/// <c>TMUX</c> is "socket,server-pid,session" like tmux's; erikterm's control socket stands in for the tmux
/// socket, so the shim can tell its own fake session (<c>-S</c> that socket) from a real tmux server.
/// <c>TMUX_PANE</c> is <c>%</c> + the erikterm pane id. The shim directory (holding a <c>tmux</c> symlink to
/// erikterm — a hard link named <c>tmux.exe</c> on Windows) goes first on <c>PATH</c>.
/// </summary>
internal static class TmuxEnvironment
{
    public const string TmuxVariable = "TMUX";
    public const string PaneVariable = "TMUX_PANE";
    /// <summary>
    /// Claude Code caps itself at 256 colors whenever $TMUX is set (it assumes a tmux that may not pass 24-bit
    /// color through) unless this is set. The emulation does pass truecolor through, so it sets it; it goes again
    /// with the rest of the emulation's variables when real tmux takes over.
    /// </summary>
    public const string ClaudeTruecolorVariable = "CLAUDE_CODE_TMUX_TRUECOLOR";
    /// <summary>Path of a log of every shim invocation (argv, how it was handled, exit status).</summary>
    public const string LogVariable = "ERIKTERM_TMUX_LOG";
    /// <summary>Name of the shim directory under the IPC directory: <c>shim-&lt;pid&gt;</c>.</summary>
    public const string ShimPrefix = "shim-";

    /// <summary>
    /// What the shim is called in that directory. Windows needs the extension: <c>CreateProcess</c> and every
    /// PATH search built on it only run executables, and a <c>tmux.cmd</c> is no substitute — Node refuses to
    /// spawn a batch file without a shell, and Claude Code spawns without one.
    /// </summary>
    public static string ShimFileName => OperatingSystem.IsWindows() ? "tmux.exe" : "tmux";

    public static string ShimDirectory(string ipcDir, int pid) => Path.Combine(ipcDir, ShimPrefix + pid.ToString(CultureInfo.InvariantCulture));

    public static string FakeTmuxValue(string socket, int serverPid) => $"{socket},{serverPid},0";

    /// <summary>The socket path in a <c>$TMUX</c> value (everything before the first comma), or null.</summary>
    public static string? SocketOf(string? tmux)
    {
        if (string.IsNullOrEmpty(tmux)) return null;
        int comma = tmux.IndexOf(',');
        string s = comma < 0 ? tmux : tmux[..comma];
        return s.Length > 0 ? s : null;
    }

    /// <summary>True for a <c>$TMUX</c> that names an erikterm control socket (…/erikterm-&lt;pid&gt;.sock), i.e. the emulation.</summary>
    public static bool IsFake(string? tmux) =>
        SocketOf(tmux) is { } s && Path.GetFileName(s).StartsWith("erikterm-", StringComparison.Ordinal) && s.EndsWith(".sock", StringComparison.Ordinal);

    /// <summary>A PATH entry that is some erikterm instance's shim directory.</summary>
    public static bool IsShimDirectory(string entry)
    {
        string trimmed = entry.TrimEnd('/', '\\');
        string name = Path.GetFileName(trimmed);
        if (!name.StartsWith(ShimPrefix, StringComparison.Ordinal) || !int.TryParse(name.AsSpan(ShimPrefix.Length), out _)) return false;
        string parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "");
        return parent.StartsWith("erikterm", StringComparison.Ordinal);
    }

    /// <summary>PATH without any erikterm shim directories.</summary>
    public static string ScrubPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var parts = path.Split(Path.PathSeparator).Where(p => !IsShimDirectory(p));
        return string.Join(Path.PathSeparator, parts);
    }

    /// <summary>Turns the emulation on in a pane's environment.</summary>
    public static void Apply(IDictionary<string, string> env, string shimDir, string socket, int serverPid, int paneId)
    {
        string rest = ScrubPath(env.TryGetValue("PATH", out var p) ? p : null);
        env["PATH"] = rest.Length > 0 ? shimDir + Path.PathSeparator + rest : shimDir;
        env[TmuxVariable] = FakeTmuxValue(socket, serverPid);
        env[PaneVariable] = "%" + paneId.ToString(CultureInfo.InvariantCulture);
        env[ClaudeTruecolorVariable] = "1";
    }

    /// <summary>
    /// Removes the emulation: shim directories leave PATH, and a fake TMUX / TMUX_PANE goes. A real tmux's
    /// TMUX (erikterm started inside tmux) is left alone — that's what the pane would see without erikterm's help.
    /// </summary>
    public static void Remove(IDictionary<string, string> env)
    {
        if (env.TryGetValue("PATH", out var p)) env["PATH"] = ScrubPath(p);
        if (env.TryGetValue(TmuxVariable, out var t) && IsFake(t))
        {
            env.Remove(TmuxVariable);
            env.Remove(PaneVariable);
            env.Remove(ClaudeTruecolorVariable);
        }
    }

    /// <summary>The environment real tmux runs with: the emulation's variables removed, PATH restored.</summary>
    public static Dictionary<string, string> ForRealTmux(IEnumerable<KeyValuePair<string, string>> current)
    {
        var env = new Dictionary<string, string>(current, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (env.TryGetValue(TmuxVariable, out var t) && IsFake(t))
        {
            env.Remove(TmuxVariable);
            env.Remove(PaneVariable);
            env.Remove(ClaudeTruecolorVariable); // real tmux may not pass 24-bit color through
        }
        if (env.TryGetValue("PATH", out var p)) env["PATH"] = ScrubPath(p);
        return env;
    }
}
