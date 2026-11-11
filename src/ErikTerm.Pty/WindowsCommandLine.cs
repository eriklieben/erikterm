using System.Text;

namespace ErikTerm.Pty;

/// <summary>
/// Pure helpers for starting a Windows process: argv → command line, environment → UTF-16 block,
/// program name → executable path. No OS calls, so they are unit-tested on every platform.
/// </summary>
internal static class WindowsCommandLine
{
    /// <summary>
    /// Joins argv into a command line that CommandLineToArgvW (and the MSVC CRT) split back into the
    /// same argv. argv[0] is parsed by different rules (no backslash escapes: a leading quote runs to
    /// the next quote, otherwise it ends at a space or tab), so it is only quoted, never escaped.
    /// </summary>
    public static string Build(IReadOnlyList<string> argv) => Build(argv, batchFile: false);

    /// <summary>
    /// Like <see cref="Build(IReadOnlyList{string})"/>; with <paramref name="batchFile"/> (the program is a .bat / .cmd,
    /// which CreateProcess hands to <c>cmd.exe /c</c>) the result must also survive cmd's parsing, which ignores the
    /// CRT rules: an argument like <c>"&amp;calc</c> would otherwise run a command ("BatBadBut"). Arguments are quoted
    /// whenever they hold a cmd metacharacter, and those that can't be passed safely (<c>"</c>, <c>%</c>, CR, LF)
    /// are refused with an ArgumentException.
    /// </summary>
    public static string Build(IReadOnlyList<string> argv, bool batchFile)
    {
        if (argv.Count == 0) throw new ArgumentException("argv must contain at least argv[0].", nameof(argv));
        if (batchFile) return BuildForCmd(argv);
        var sb = new StringBuilder();
        AppendProgramName(sb, argv[0]);
        for (int i = 1; i < argv.Count; i++)
        {
            sb.Append(' ');
            AppendArgument(sb, argv[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits a command line into argv by CommandLineToArgvW's rules (shell32; Wine's implementation and tests are the
    /// reference for what the documentation leaves out). argv[0], the program, has rules of its own: a leading quote
    /// runs to the next quote, else it ends at the first space or tab; backslashes are literal. Then, argument by
    /// argument (separated by spaces and tabs outside quotes):
    /// <list type="bullet">
    /// <item>2n backslashes and a quote: n backslashes, and the quote opens or closes a quoted part;</item>
    /// <item>2n+1 backslashes and a quote: n backslashes and a literal quote;</item>
    /// <item>backslashes not followed by a quote are literal;</item>
    /// <item>two quotes inside a quoted part: a literal quote, and the quoted part ends. (The MSVC CRT since 2008 stays
    /// in the quoted part instead. <see cref="Build(IReadOnlyList{string})"/> never writes this, so both rules give
    /// the same argv for what erikterm starts.)</item>
    /// </list>
    /// An unclosed quote runs to the end. Empty or blank: no arguments (CommandLineToArgvW would return the calling
    /// program's own path).
    /// </summary>
    public static List<string> Parse(ReadOnlySpan<char> commandLine)
    {
        var args = new List<string>();
        if (commandLine.IsWhiteSpace()) return args;
        var sb = new StringBuilder();
        int i = ReadProgramName(commandLine, sb);
        args.Add(sb.ToString());

        while (true)
        {
            while (i < commandLine.Length && commandLine[i] is ' ' or '\t') i++;
            if (i >= commandLine.Length) return args;
            sb.Clear();
            ReadArgument(commandLine, ref i, sb);
            args.Add(sb.ToString());
        }
    }

    /// <summary>Reads argv[0] into <paramref name="sb"/>; returns the index after it.</summary>
    private static int ReadProgramName(ReadOnlySpan<char> commandLine, StringBuilder sb)
    {
        if (commandLine[0] == '"')
        {
            int end = commandLine[1..].IndexOf('"');
            if (end < 0) { sb.Append(commandLine[1..]); return commandLine.Length; }
            sb.Append(commandLine.Slice(1, end));
            return end + 2;
        }
        int i = 0;
        while (i < commandLine.Length && commandLine[i] is not (' ' or '\t')) sb.Append(commandLine[i++]);
        return i;
    }

    /// <summary>Reads one argument into <paramref name="sb"/>, leaving <paramref name="i"/> on the separator after it.</summary>
    private static void ReadArgument(ReadOnlySpan<char> commandLine, ref int i, StringBuilder sb)
    {
        bool quoted = false;
        while (i < commandLine.Length)
        {
            char c = commandLine[i];
            if (c == '\\') ReadBackslashes(commandLine, ref i, sb);
            else if (c == '"') ReadQuote(commandLine, ref i, sb, ref quoted);
            else if (!quoted && c is ' ' or '\t') return;
            else
            {
                sb.Append(c);
                i++;
            }
        }
    }

    /// <summary>A run of backslashes: literal, unless a quote follows.</summary>
    private static void ReadBackslashes(ReadOnlySpan<char> commandLine, ref int i, StringBuilder sb)
    {
        int backslashes = 0;
        while (i < commandLine.Length && commandLine[i] == '\\') { backslashes++; i++; }
        if (i < commandLine.Length && commandLine[i] == '"')
        {
            sb.Append('\\', backslashes / 2);
            if (backslashes % 2 == 1) { sb.Append('"'); i++; } // escaped: the quote is text
            // even: the quote is handled by the caller as a delimiter
        }
        else sb.Append('\\', backslashes);
    }

    /// <summary>A quote delimiter: opens or closes a quoted part, or is a literal quote when doubled inside one.</summary>
    private static void ReadQuote(ReadOnlySpan<char> commandLine, ref int i, StringBuilder sb, ref bool quoted)
    {
        if (quoted && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
        {
            sb.Append('"');
            quoted = false;
            i += 2;
        }
        else
        {
            quoted = !quoted;
            i++;
        }
    }

    /// <summary>True for a program CreateProcess runs through cmd.exe (.bat / .cmd, ignoring trailing dots and spaces).</summary>
    public static bool IsBatchFile(string program)
    {
        string p = program.TrimEnd(' ', '.');
        return p.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildForCmd(IReadOnlyList<string> argv)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < argv.Count; i++)
        {
            string arg = argv[i];
            if (arg.AsSpan().IndexOfAny("\0\r\n\"%") >= 0)
                throw new ArgumentException($"Argument {i} can't be passed to a batch file safely (it contains a quote, %, a line break or NUL).");
            if (i > 0) sb.Append(' ');
            // Inside quotes cmd takes & | < > ^ ( ) , ; = and spaces literally; argv[0] is always quoted.
            if (i == 0 || arg.Length == 0 || arg.AsSpan().IndexOfAny(" \t&|<>^(),;=!") >= 0)
            {
                // A trailing backslash would escape the closing quote for a CRT-parsed program the batch file calls.
                int trailing = arg.Length - arg.TrimEnd('\\').Length;
                sb.Append('"').Append(arg).Append('\\', trailing).Append('"');
            }
            else sb.Append(arg);
        }
        return sb.ToString();
    }

    private static void AppendProgramName(StringBuilder sb, string arg)
    {
        if (arg.Contains('\0')) throw new ArgumentException("argv[0] contains a NUL character.");
        if (arg.Contains('"')) throw new ArgumentException($"argv[0] can't contain a double quote on Windows: {arg}");
        if (arg.Length == 0 || arg.AsSpan().IndexOfAny(' ', '\t') >= 0) sb.Append('"').Append(arg).Append('"');
        else sb.Append(arg);
    }

    private static void AppendArgument(StringBuilder sb, string arg)
    {
        if (arg.Contains('\0')) throw new ArgumentException("Argument contains a NUL character.");
        if (arg.Length > 0 && arg.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            sb.Append(arg);
            return;
        }

        // Backslashes are literal unless they precede a quote, where 2n backslashes mean n literal ones.
        sb.Append('"');
        int i = 0;
        while (true)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { i++; backslashes++; }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2); // they precede the closing quote
                break;
            }
            if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(arg[i]);
            i++;
        }
        sb.Append('"');
    }

    /// <summary>
    /// Builds a CREATE_UNICODE_ENVIRONMENT block: "k=v\0" entries sorted case-insensitively by name
    /// (as CreateProcess documents), terminated by an extra "\0". Names that differ only in case are
    /// rejected, since Windows treats them as the same variable.
    /// </summary>
    public static string BuildEnvironmentBlock(IEnumerable<KeyValuePair<string, string>> environment)
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (var kv in environment)
        {
            string key = kv.Key, value = kv.Value ?? "";
            // A leading '=' is legal: cmd.exe keeps per-drive directories as "=C:=C:\dir".
            if (key.Length == 0 || key.IndexOf('=', 1) >= 0 || key.Contains('\0'))
                throw new ArgumentException($"Invalid environment variable name: '{key}'");
            if (value.Contains('\0'))
                throw new ArgumentException($"Environment variable '{key}' contains a NUL character.");
            entries.Add(new(key, value));
        }
        entries.Sort(static (a, b) =>
        {
            int c = string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });

        var sb = new StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0 && string.Equals(entries[i - 1].Key, entries[i].Key, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Environment variables '{entries[i - 1].Key}' and '{entries[i].Key}' differ only in case.");
            sb.Append(entries[i].Key).Append('=').Append(entries[i].Value).Append('\0');
        }
        if (entries.Count == 0) sb.Append('\0'); // an empty block is still two NULs
        sb.Append('\0');
        return sb.ToString();
    }

    /// <summary>
    /// The directories CreateProcess would search for a bare program name, minus the application's
    /// own and the current directory (searching those is a well-known DLL/EXE planting hazard):
    /// System32, the Windows directory, then each PATH entry.
    /// </summary>
    public static List<string> SearchDirectories(string? systemDirectory, string? windowsDirectory, string? path)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrEmpty(systemDirectory)) dirs.Add(systemDirectory);
        if (!string.IsNullOrEmpty(windowsDirectory)) dirs.Add(windowsDirectory);
        if (path is not null)
        {
            foreach (string entry in path.Split(';'))
            {
                string dir = entry.Trim();
                if (dir.Length >= 2 && dir[0] == '"' && dir[^1] == '"') dir = dir[1..^1];
                if (dir.Length > 0) dirs.Add(dir);
            }
        }
        return dirs;
    }

    /// <summary>
    /// Finds the executable for <paramref name="program"/>. A name containing a directory (or drive) is
    /// used as is; a bare name is looked up in <paramref name="searchDirectories"/>. Like CreateProcess,
    /// ".exe" is appended when the file name has no extension. Returns null if nothing exists.
    /// </summary>
    public static string? ResolveProgram(string program, IEnumerable<string> searchDirectories, Func<string, bool> fileExists)
    {
        if (program.Length == 0) return null;
        int lastSep = program.AsSpan().LastIndexOfAny('\\', '/');
        bool hasDirectory = lastSep >= 0 || (program.Length >= 2 && program[1] == ':');
        // "If the file name ends in a period (.) with no extension, .exe is not appended."
        bool hasExtension = program.AsSpan(lastSep + 1).Contains('.');
        string name = hasExtension ? program : program + ".exe";

        if (hasDirectory) return fileExists(name) ? name : null;
        foreach (string dir in searchDirectories)
        {
            string candidate = dir.EndsWith('\\') || dir.EndsWith('/') ? dir + name : dir + "\\" + name;
            if (fileExists(candidate)) return candidate;
        }
        return null;
    }
}
