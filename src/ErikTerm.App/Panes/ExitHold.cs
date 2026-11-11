using ErikTerm.Config;

namespace ErikTerm.App.Panes;

/// <summary>
/// exit_behavior, decided: whether a pane whose program exited closes or stays, and what its exit bar says. Pure, so it
/// can be tested without a window (Windowing/ExitHoldFlow.cs acts on it).
/// </summary>
internal static class ExitHold
{
    /// <summary>
    /// Hold or close. <paramref name="mustClose"/>: panes that always close — tmux emulation panes and teammates (tmux
    /// closes them; Claude Code counts on it), erikterm's own tool splits, and panes kill-pane hung up.
    /// CloseOnCleanExit holds unless the status is known to be 0.
    /// </summary>
    public static bool ShouldHold(ExitBehavior behavior, bool mustClose, int? exitCode) =>
        !mustClose && behavior switch
        {
            ExitBehavior.Hold => true,
            ExitBehavior.CloseOnCleanExit => exitCode != 0,
            _ => false,
        };

    /// <summary>"make exited with code 3" ("Process …" without a name; without a code when it couldn't be read).</summary>
    public static string Message(string program, int? exitCode)
    {
        string who = program.Length > 0 ? program : "Process";
        return exitCode is { } code ? $"{who} exited with code {code}" : $"{who} exited";
    }

    /// <summary>The program's name for the exit bar: argv[0]'s file name ("make", "zsh"), or "".</summary>
    public static string ProgramName(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || string.IsNullOrEmpty(argv[0])) return "";
        string name = argv[0].Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        name = slash >= 0 ? name[(slash + 1)..] : name;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        // A restored program runs through a small sh wrapper (Session/RestoreLaunch.cs) that execs the shell after it:
        // when the pane's process ends, that shell is what exited.
        if (argv.Count > 5 && argv[1] == "-c" && argv[2] == Session.RestoreLaunch.Script && int.TryParse(argv[4], out int n) && n >= 0 && 5 + n < argv.Count)
            return ProgramName(argv.Skip(5 + n).ToArray());
        return name;
    }

    /// <summary>Where Restart Pane runs the program: the directory the shell last reported (OSC 7), else where it started.</summary>
    public static string? RestartDirectory(string? reported, string? launched, string? defaultCwd)
    {
        if (reported is { Length: > 0 } && Directory.Exists(reported)) return reported;
        if (launched is { Length: > 0 } && Directory.Exists(launched)) return launched;
        return defaultCwd is { Length: > 0 } && Directory.Exists(defaultCwd) ? defaultCwd : null;
    }
}
