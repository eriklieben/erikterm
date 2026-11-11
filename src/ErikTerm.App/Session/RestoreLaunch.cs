namespace ErikTerm.App.Session;

/// <summary>
/// How a restored program starts: as the pane's own process with the saved argv, never typed into a shell (a file
/// name holding ^U or a newline would otherwise run as a command). A small /bin/sh wrapper runs the program and then
/// execs the pane's usual shell, so quitting the program leaves you at a prompt. The argv only ever reaches the
/// wrapper as separate arguments — the script itself is constant.
/// </summary>
internal static class RestoreLaunch
{
    public const int MaxArgs = 256;
    public const int MaxArgChars = 4096;
    public const int MaxTotalChars = 32 * 1024;
    public const string WrapperName = "erikterm-restore";

    /// <summary>
    /// $1 = how many of the following arguments are the program's argv; the rest is the shell to exec afterwards.
    /// The eval'd string is made of "${N}" references only (digits), never of argument text. set -m gives the program
    /// its own foreground process group, so the next session save sees it (not sh) in the foreground.
    /// </summary>
    public const string Script =
        "n=$1; shift; c=; i=1; " +
        "while [ \"$i\" -le \"$n\" ]; do c=\"$c \\\"\\${$i}\\\"\"; i=$((i+1)); done; " +
        "set -m 2>/dev/null; eval \"$c\"; shift \"$n\"; exec \"$@\"";

    /// <summary>
    /// True when an argv may be saved and started again: 1–<see cref="MaxArgs"/> arguments, a non-empty argv[0],
    /// and every argument <see cref="IsSafeArgument"/>, <see cref="MaxTotalChars"/> in all.
    /// </summary>
    public static bool IsSafe(IReadOnlyList<string>? argv)
    {
        if (argv is null || argv.Count is 0 or > MaxArgs || string.IsNullOrEmpty(argv[0])) return false;
        int total = 0;
        foreach (var a in argv)
        {
            if (!IsSafeArgument(a)) return false;
            total += a.Length;
            if (total > MaxTotalChars) return false;
        }
        return true;
    }

    /// <summary>
    /// True when one argument can be restored: at most <see cref="MaxArgChars"/> long, with no control characters
    /// (C0 including TAB and newlines, DEL, C1) and no U+2028 / U+2029 (line / paragraph separators). A saved argv
    /// comes from /proc, and a file name can hold anything — "\x15touch x\r" reaching a line editor would clear
    /// the line and run a command — so an argv with any of them is neither saved nor started.
    /// </summary>
    public static bool IsSafeArgument(string? arg)
    {
        if (arg is null || arg.Length > MaxArgChars) return false;
        return arg.All(c => !(c < ' ' || c is >= '\x7f' and <= '\x9f' or '\u2028' or '\u2029'));
    }

    /// <summary>The argv to re-run for a saved program, or null (not in restore_programs, unsafe, nothing to run).</summary>
    public static IReadOnlyList<string>? ProgramArgv(SessionProgram? program, IReadOnlyList<string> allowed)
    {
        if (program is null) return null;
        IReadOnlyList<string>? argv = null;
        if (program.Argv is { Count: > 0 } a) argv = a;
        else if (program.Name is { Length: > 0 } n) argv = [n];
        if (argv is null || !IsSafe(argv)) return null;
        return RestorePrograms.Allowed(allowed, RestorePrograms.NameOf(argv[0])) ? argv : null;
    }

    /// <summary>
    /// The pane's command line: /bin/sh running <paramref name="program"/>, then exec'ing <paramref name="shell"/>.
    /// Null on Windows (no POSIX shell to wrap with; programs aren't re-run there) or when either is unusable.
    /// </summary>
    public static string[]? Wrap(IReadOnlyList<string> program, IReadOnlyList<string> shell)
    {
        if (OperatingSystem.IsWindows() || !IsSafe(program) || shell.Count == 0 || string.IsNullOrEmpty(shell[0])) return null;
        var argv = new List<string>(program.Count + shell.Count + 5) { "/bin/sh", "-c", Script, WrapperName, program.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        argv.AddRange(program);
        argv.AddRange(shell);
        return [.. argv];
    }

    public static string[]? CommandFor(SessionProgram? program, IReadOnlyList<string> allowed, IReadOnlyList<string> shell) =>
        ProgramArgv(program, allowed) is { } argv ? Wrap(argv, shell) : null;
}
