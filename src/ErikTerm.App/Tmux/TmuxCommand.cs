namespace ErikTerm.App.Tmux;

public sealed class TmuxUsageException(string message) : Exception(message);

/// <summary>One tmux command after option parsing, e.g. <c>split-window -d -h -t %3 -- cat</c>.</summary>
internal sealed class TmuxCommand
{
    /// <summary>The canonical name (aliases resolved): "split-window", not "splitw".</summary>
    public required string Name { get; init; }
    /// <summary>Flags without a value ('d', 'h', …), in order; repeated flags appear repeatedly.</summary>
    public List<char> Flags { get; } = [];
    /// <summary>Flags with a value; the last one wins (<see cref="AllValues"/> keeps every -e).</summary>
    public Dictionary<char, string> Values { get; } = [];
    public List<(char Flag, string Value)> AllValues { get; } = [];
    public List<string> Args { get; } = [];

    public bool Has(char flag) => Flags.Contains(flag);
    public string? Value(char flag) => Values.GetValueOrDefault(flag);
    public string? Target => Value('t');
}

/// <summary>A whole <c>tmux</c> command line: server options, then commands separated by <c>;</c>.</summary>
internal sealed class TmuxInvocation
{
    /// <summary>-S socket-path.</summary>
    public string? SocketPath { get; init; }
    /// <summary>-L socket-name.</summary>
    public string? SocketName { get; init; }
    /// <summary>-V: print the version.</summary>
    public bool Version { get; init; }
    /// <summary>Server options that only real tmux understands: -C (control mode), -c (shell command), -f, -D, -T, -N, -l.</summary>
    public bool NeedsRealServer { get; init; }
    public List<TmuxCommand> Commands { get; } = [];
    /// <summary>The first command's name as typed, when it isn't one this parser knows (it is still passed on to real tmux).</summary>
    public string? UnknownCommand { get; init; }
}

/// <summary>
/// Parses tmux's command line the way tmux does: server flags, then a command (full name or alias, or an
/// unambiguous prefix), its getopt-style flags (clustered, values attached or separate, parsing stops at the
/// first non-flag or <c>--</c>), then its arguments. A lone <c>;</c> argument separates commands.
/// </summary>
internal static class TmuxParser
{
    private sealed record Spec(string Name, string? Alias, string Options, int MinArgs, int MaxArgs);

    // Option strings follow tmux's cmd-*.c tables ("x:" takes a value). MaxArgs -1 = any number.
    private static readonly Spec[] Specs =
    [
        new("attach-session", "attach", "c:dEf:rt:x", 0, 0),
        new("capture-pane", "capturep", "ab:CeE:JNpPqS:Tt:", 0, 0),
        new("display-message", "display", "aCc:d:lINpt:F:v", 0, 1),
        new("has-session", "has", "t:", 0, 0),
        new("kill-pane", "killp", "at:", 0, 0),
        new("kill-server", null, "", 0, 0),
        new("kill-session", null, "aCt:", 0, 0),
        new("kill-window", "killw", "at:", 0, 0),
        new("last-pane", "lastp", "det:Z", 0, 0),
        new("list-clients", "lsc", "F:f:O:rt:", 0, 0),
        new("list-panes", "lsp", "aF:f:O:rst:", 0, 0),
        new("list-sessions", "ls", "F:f:O:r", 0, 0),
        new("list-windows", "lsw", "aF:f:O:rt:", 0, 0),
        new("load-buffer", "loadb", "b:t:w", 1, 1),
        new("new-session", "new", "Ac:dDe:EF:f:n:Ps:t:x:Xy:", 0, -1),
        new("new-window", "neww", "abc:de:F:kn:PSt:", 0, -1),
        new("refresh-client", "refresh", "A:B:cC:Df:r:F:l:LRSt:U", 0, 1),
        new("rename-session", "rename", "t:", 1, 1),
        new("rename-window", "renamew", "t:", 1, 1),
        new("resize-pane", "resizep", "DLMRTt:Ux:y:Z", 0, 1),
        new("respawn-pane", "respawnp", "c:e:kt:", 0, -1),
        new("select-layout", "selectl", "Enopt:", 0, 1),
        new("select-pane", "selectp", "DdegLlMmP:RT:t:UZ", 0, 0),
        new("select-window", "selectw", "lnpTt:", 0, 0),
        new("send-keys", "send", "c:FHKlMN:Rt:X", 0, -1),
        new("set-environment", "setenv", "Fhgrt:u", 1, 2),
        new("set-option", "set", "aFgopqst:uUw", 1, 2),
        new("set-window-option", "setw", "aFgoqt:u", 1, 2),
        new("show-environment", "showenv", "hgst:", 0, 1),
        new("show-options", "show", "AgHpqst:vw", 0, 1),
        new("show-window-options", "showw", "gvt:", 0, 1),
        new("split-window", "splitw", "bc:de:fF:hIl:p:Pt:vZ", 0, -1),
        new("start-server", "start", "", 0, 0),
        new("switch-client", "switchc", "c:EFlnO:pt:rT:Z", 0, 0),
    ];

    public static IReadOnlyList<string> CommandNames { get; } = Specs.Select(s => s.Name).ToArray();

    /// <exception cref="TmuxUsageException">A known command with bad flags or the wrong number of arguments.</exception>
    public static TmuxInvocation Parse(IReadOnlyList<string> argv)
    {
        int i = 0;
        var options = ParseServerOptions(argv, ref i);
        var commands = ParseCommands(argv, i, out string? unknown);

        var inv = new TmuxInvocation
        {
            SocketPath = options.SocketPath, SocketName = options.SocketName, Version = options.Version, NeedsRealServer = options.Real, UnknownCommand = unknown,
        };
        inv.Commands.AddRange(commands);
        return inv;
    }

    private struct ServerOptions
    {
        public string? SocketPath, SocketName;
        public bool Version, Real;
    }

    private static ServerOptions ParseServerOptions(IReadOnlyList<string> argv, ref int i)
    {
        var options = new ServerOptions();
        while (i < argv.Count && argv[i].Length > 1 && argv[i][0] == '-')
        {
            string a = argv[i++];
            if (a == "--") break;
            ParseServerFlags(a, argv, ref i, ref options);
        }
        return options;
    }

    private static void ParseServerFlags(string a, IReadOnlyList<string> argv, ref int i, ref ServerOptions options)
    {
        for (int k = 1; k < a.Length; k++)
        {
            char f = a[k];
            if ("cfLST".Contains(f))
            {
                ApplyServerValue(f, OptionValue(a, k, argv, ref i, argv.Count, $"option requires an argument -- {f}"), ref options);
                return;
            }
            switch (f)
            {
                case 'V': options.Version = true; break;
                case 'C' or 'D' or 'N' or 'l': options.Real = true; break;
                case '2' or '8' or 'u' or 'U' or 'v' or 'q': break; // harmless client flags
                default: throw new TmuxUsageException($"unknown option -- {f}");
            }
        }
    }

    private static void ApplyServerValue(char f, string value, ref ServerOptions options)
    {
        if (f == 'S') options.SocketPath = value;
        else if (f == 'L') options.SocketName = value;
        else options.Real = true; // -c shell-command, -f file, -T features
    }

    /// <summary>The value of the flag at <paramref name="k"/> of <paramref name="a"/>: the rest of the word, else the next argument.</summary>
    private static string OptionValue(string a, int k, IReadOnlyList<string> argv, ref int i, int end, string missing)
    {
        if (k + 1 < a.Length) return a[(k + 1)..];
        if (i < end) return argv[i++];
        throw new TmuxUsageException(missing);
    }

    private static List<TmuxCommand> ParseCommands(IReadOnlyList<string> argv, int i, out string? unknown)
    {
        unknown = null;
        var commands = new List<TmuxCommand>();
        while (i < argv.Count)
        {
            int end = i;
            while (end < argv.Count && argv[end] != ";") end++;
            if (end > i)
            {
                var spec = Lookup(argv[i]);
                if (spec is null)
                {
                    if (commands.Count == 0) unknown = argv[i];
                    else throw new TmuxUsageException($"unknown command: {argv[i]}");
                    break;
                }
                commands.Add(ParseCommand(spec, argv, i + 1, end));
            }
            i = end + 1;
        }
        return commands;
    }

    private static Spec? Lookup(string name)
    {
        foreach (var s in Specs)
            if (s.Name == name || s.Alias == name) return s;
        // tmux accepts any unambiguous prefix of a command name.
        var matches = Specs.Where(s => s.Name.StartsWith(name, StringComparison.Ordinal)).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1) throw new TmuxUsageException($"ambiguous command: {name}, could be: {string.Join(", ", matches.Select(m => m.Name))}");
        return null;
    }

    private static TmuxCommand ParseCommand(Spec spec, IReadOnlyList<string> argv, int start, int end)
    {
        var cmd = new TmuxCommand { Name = spec.Name };
        int i = start;
        while (i < end)
        {
            string a = argv[i];
            if (a == "--") { i++; break; }
            if (a.Length < 2 || a[0] != '-') break; // getopt with "+": stop at the first argument
            i++;
            ParseCommandFlags(spec, cmd, a, argv, ref i, end);
        }
        for (; i < end; i++) cmd.Args.Add(argv[i]);
        if (cmd.Args.Count < spec.MinArgs || (spec.MaxArgs >= 0 && cmd.Args.Count > spec.MaxArgs))
            throw new TmuxUsageException($"{spec.Name}: {(cmd.Args.Count < spec.MinArgs ? "too few" : "too many")} arguments");
        return cmd;
    }

    private static void ParseCommandFlags(Spec spec, TmuxCommand cmd, string a, IReadOnlyList<string> argv, ref int i, int end)
    {
        for (int k = 1; k < a.Length; k++)
        {
            char f = a[k];
            int at = spec.Options.IndexOf(f);
            if (at < 0 || f == ':') throw new TmuxUsageException($"{spec.Name}: unknown option -- {f}");
            if (at + 1 < spec.Options.Length && spec.Options[at + 1] == ':')
            {
                string value = OptionValue(a, k, argv, ref i, end, $"{spec.Name}: option requires an argument -- {f}");
                cmd.Values[f] = value;
                cmd.AllValues.Add((f, value));
                return;
            }
            cmd.Flags.Add(f);
        }
    }
}
