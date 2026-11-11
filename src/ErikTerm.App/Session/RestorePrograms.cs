using System.Text;

namespace ErikTerm.App.Session;

/// <summary>Which foreground programs come back on restore (restore_programs); how they start is RestoreLaunch.</summary>
internal static class RestorePrograms
{
    private static readonly HashSet<string> Shells =
        ["zsh", "bash", "fish", "sh", "dash", "ksh", "mksh", "tcsh", "csh", "nu", "xonsh", "elvish", "pwsh", "powershell", "cmd", "login"];

    /// <summary>"/usr/bin/nvim" → "nvim", "-zsh" (login shell) → "zsh", "pwsh.exe" → "pwsh".</summary>
    public static string NameOf(string program)
    {
        string name = program.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.StartsWith('-')) name = name[1..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    public static bool IsShell(string? name) => name is not null && Shells.Contains(NameOf(name).ToLowerInvariant());

    /// <summary>
    /// The program's name for matching: argv[0]'s file name when there is an argv, else the process name
    /// (Linux comm, at most 15 characters).
    /// </summary>
    public static string? ProgramName(string? processName, IReadOnlyList<string>? argv)
    {
        if (argv is { Count: > 0 } && argv[0].Length > 0) return NameOf(argv[0]);
        return processName is { Length: > 0 } ? NameOf(processName) : null;
    }

    /// <summary>
    /// What a pane's foreground program saves as (null: nothing, it comes back as a shell). argv only for programs that
    /// would be re-run: command lines can hold things best not written down. A command line with control characters is
    /// neither saved nor re-run. <paramref name="launched"/> is the agent a worktree tab started: its prompt and arguments
    /// were meant for that one launch, so it is never saved, even when restore_programs lists it.
    /// </summary>
    public static SessionProgram? Capture(string? foreground, string[]? argv, IReadOnlyList<string> allowed, IReadOnlyList<string>? launched)
    {
        if (foreground is null || IsShell(foreground)) return null;
        string? name = ProgramName(foreground, argv);
        if (name is null || IsShell(name)) return null;
        if (launched is not null && IsLaunchedProgram(name, argv, launched)) return null;
        bool rerun = Allowed(allowed, name);
        if (rerun && argv is not null && !RestoreLaunch.IsSafe(argv)) return null;
        return new SessionProgram { Name = name, Argv = argv is not null && rerun ? [.. argv] : null };
    }

    /// <summary>The agent itself by name, or under an interpreter (node …/copilot) by the arguments it was given.</summary>
    internal static bool IsLaunchedProgram(string name, IReadOnlyList<string>? argv, IReadOnlyList<string> launched)
    {
        if (launched.Count == 0) return false;
        if (string.Equals(NameOf(launched[0]), name, StringComparison.Ordinal)) return true;
        if (argv is null || launched.Count == 1 || argv.Count < launched.Count - 1) return false;
        return argv.Skip(argv.Count - (launched.Count - 1)).SequenceEqual(launched.Skip(1));
    }

    /// <summary>True when <paramref name="name"/> is listed (entries may be names or paths; matched on the name).</summary>
    public static bool Allowed(IReadOnlyList<string> allowed, string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = NameOf(name);
        foreach (var entry in allowed)
        {
            if (entry.Length == 0) continue;
            if (string.Equals(NameOf(entry), n, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

/// <summary>Saved scrollback: replayed into a new terminal before its shell starts.</summary>
internal static class SessionScrollback
{
    public const string Separator = "\e[0;2m── restored session ──\e[0m";

    /// <summary>
    /// Keeps printable text and SGR sequences (ESC [ digits/;/: m) only, so a saved line can't move the cursor,
    /// change modes or send replies when replayed.
    /// </summary>
    public static string Sanitize(string line)
    {
        var sb = new StringBuilder(line.Length);
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '\e')
            {
                i = AppendSgr(sb, line, i);
                continue;
            }
            i++;
            if (c < ' ' || c is >= '\x7f' and < '\xa0') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Keeps the SGR sequence at <paramref name="esc"/>; a lone/unsupported ESC is dropped and what follows prints as text. Returns where to go on.</summary>
    private static int AppendSgr(StringBuilder sb, string line, int esc)
    {
        int end = SgrEnd(line, esc);
        if (end < 0) return esc + 1;
        sb.Append(line, esc, end - esc + 1);
        return end + 1;
    }

    /// <summary>The index of the final <c>m</c> when an SGR sequence starts at <paramref name="i"/>, else -1.</summary>
    private static int SgrEnd(string line, int i)
    {
        int j = i + 1;
        if (j >= line.Length || line[j] != '[') return -1;
        j++;
        while (j < line.Length && (char.IsAsciiDigit(line[j]) || line[j] is ';' or ':')) j++;
        return j < line.Length && line[j] == 'm' ? j : -1;
    }

    /// <summary>The bytes to feed: each line, reset, CR LF, then the dim separator line.</summary>
    public static byte[] Replay(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines) sb.Append(Sanitize(line)).Append("\e[0m\r\n");
        sb.Append(Separator).Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
