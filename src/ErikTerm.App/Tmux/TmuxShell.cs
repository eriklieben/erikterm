namespace ErikTerm.App.Tmux;

/// <summary>
/// A shell the emulation can run a command line with: its path, the flag that takes the command, and whether that
/// command may be POSIX. <c>Posix</c> is false only for the Windows cmd.exe fallback — a <c>$SHELL</c> the user set
/// is taken at their word, as tmux takes <c>default-shell</c>.
/// </summary>
internal readonly record struct TmuxShellProgram(string Path, string Flag, bool Posix);

/// <summary>
/// What tmux's <c>default-shell</c> stands for when the shim has to run a command line as one string
/// (<c>split-window "cmd"</c>, <c>respawn-pane -- "cmd"</c>), and the flag that makes it do so.
/// <para>
/// Unix: <c>$SHELL</c>, else <c>/bin/sh</c>, with <c>-c</c> — what tmux itself uses.
/// </para>
/// <para>
/// Windows has no <c>$SHELL</c> and no <c>/bin/sh</c>, but the command lines programs hand to tmux are POSIX shell
/// either way: Claude Code's teammate command is <c>cd '&lt;dir&gt;' &amp;&amp; env VAR=1 '&lt;claude&gt;' --agent-id …</c>,
/// and the pane it parks first runs <c>cat</c>. Neither cmd.exe nor PowerShell can run that, so the shim looks for
/// a POSIX shell — Git for Windows', which every machine with git has.
/// </para>
/// <para>
/// It must be <c>&lt;Git&gt;\bin\sh.exe</c>, <b>not</b> <c>&lt;Git&gt;\usr\bin\sh.exe</c>. They are the same binary, but
/// only the one in <c>bin</c> is reached through the wrapper that sets up the MSYS environment, so only it has
/// <c>/usr/bin</c> on its PATH. Started as <c>usr\bin\sh.exe</c>, the very first thing Claude Code runs —
/// <c>cat</c> — fails with "command not found", and so do <c>env</c> and every other coreutil in the teammate
/// command line. Measured, not assumed: <c>usr\bin\sh.exe -c "cat --version"</c> says <c>cat: command not found</c>
/// where <c>bin\sh.exe</c> prints the version.
/// </para>
/// <para>
/// Without any POSIX shell the fallback is <c>%COMSPEC% /c</c> (cmd.exe), which runs a plain Windows command line
/// but not a POSIX one. That is a real loss, not a silent one: the shim refuses a command it knows to be POSIX —
/// anything from Claude Code, and anything with <c>-e VAR=value</c> — with a message naming Git for Windows
/// (see <see cref="TmuxShim.Program"/>). cmd.exe itself is only ever missing on a broken installation, so when
/// <c>%COMSPEC%</c> names nothing that exists the System32 copy is used, and failing that CreateProcess's own
/// search; a spawn that still fails shows its error in the pane.
/// </para>
/// <para>
/// The WSL launchers (<c>System32\bash.exe</c>, <c>WindowsApps\bash.exe</c>) are skipped on purpose: they run the
/// command inside a Linux distribution, with its own file system and its own Claude Code, not in this pane's world.
/// </para>
/// </summary>
internal static class TmuxShell
{
    private const string ShExe = "sh.exe";

    /// <summary>The shell and the flag that runs a command string with it.</summary>
    public static TmuxShellProgram ForCommand(Func<string, string?> env)
    {
        if (!OperatingSystem.IsWindows())
            return new(env("SHELL") is { Length: > 0 } s ? s : "/bin/sh", "-c", Posix: true);
        return WindowsShell(env);
    }

    private static TmuxShellProgram WindowsShell(Func<string, string?> env)
    {
        string path = env("PATH") ?? "";
        foreach (string candidate in WindowsCandidates(env, path))
        {
            if (candidate.Length == 0 || !File.Exists(candidate)) continue;
            try { return new(Path.GetFullPath(candidate), "-c", Posix: true); } // "<Git>\cmd\..\bin\sh.exe" → "<Git>\bin\sh.exe"
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new(candidate, "-c", Posix: true); }
        }
        return new(CommandProcessor(env), "/c", Posix: false);
    }

    /// <summary>cmd.exe: <c>%COMSPEC%</c>, the System32 copy, or the bare name for CreateProcess to find.</summary>
    private static string CommandProcessor(Func<string, string?> env)
    {
        if (env("COMSPEC") is { Length: > 0 } c && File.Exists(c)) return c;
        if (env("SystemRoot") is { Length: > 0 } root && Path.Combine(root, "System32", "cmd.exe") is var system && File.Exists(system)) return system;
        return "cmd.exe";
    }

    /// <summary>Where a POSIX shell may be on Windows, best first. Pure: <paramref name="path"/> is the PATH to search.</summary>
    internal static IEnumerable<string> WindowsCandidates(Func<string, string?> env, string path)
    {
        if (env("SHELL") is { Length: > 0 } shell) yield return shell;
        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Git for Windows: bin\sh.exe is the wrapper that puts /usr/bin on PATH (usr\bin\sh.exe does not).
        foreach (string dir in directories)
        {
            if (!File.Exists(Path.Combine(dir, "git.exe"))) continue;
            yield return Path.Combine(dir, ShExe);                       // git.exe in <Git>\bin
            yield return Path.Combine(dir, "..", "bin", ShExe);          // git.exe in <Git>\cmd
        }
        foreach (string root in new[] { env("ProgramFiles"), env("ProgramW6432"), env("ProgramFiles(x86)") }.OfType<string>().Where(r => r.Length > 0))
            yield return Path.Combine(root, "Git", "bin", ShExe);
        if (env("LOCALAPPDATA") is { Length: > 0 } local) yield return Path.Combine(local, "Programs", "Git", "bin", ShExe);
        // Anything else calling itself a shell, as long as it isn't a WSL launcher.
        foreach (string dir in directories)
        {
            if (IsWindowsSubsystemForLinux(dir, env)) continue;
            yield return Path.Combine(dir, ShExe);
            yield return Path.Combine(dir, "bash.exe");
        }
    }

    private static bool IsWindowsSubsystemForLinux(string dir, Func<string, string?> env)
    {
        string name = Path.GetFileName(dir.TrimEnd('/', '\\'));
        if (name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
        string system = env("SystemRoot") is { Length: > 0 } root ? Path.Combine(root, "System32") : "";
        return system.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(dir), system, StringComparison.OrdinalIgnoreCase);
    }
}
