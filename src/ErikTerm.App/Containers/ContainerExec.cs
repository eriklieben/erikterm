namespace ErikTerm.App.Containers;

/// <summary>
/// A shell in a container, as a pane's foreground program shows it: <c>docker exec -it ID zsh</c>, <c>podman exec …</c>,
/// the devcontainer CLI's <c>devcontainer exec --workspace-folder DIR …</c>, or <c>erikterm devcontainer</c> just
/// before it replaces itself with one of those. <see cref="Engine"/> is the program to ask about the container (with
/// <see cref="EngineOptions"/>, the global options that pick another daemon or connection, before every subcommand).
/// Exactly one of <see cref="Container"/> (an id or name) and <see cref="WorkspaceFolder"/> (a host folder whose dev
/// container has the <c>devcontainer.local_folder</c> label) is known, or neither for a devcontainer CLI exec that
/// didn't name a folder: that one means the process's own directory. A compose exec names a
/// <see cref="ComposeService"/> (and maybe its <see cref="ComposeProject"/>) instead: its container carries compose's labels.
/// </summary>
internal sealed record ContainerExecTarget(
    string Engine,
    IReadOnlyList<string> EngineOptions,
    string? Container,
    string? WorkspaceFolder,
    string? User,
    string? Workdir,
    bool ViaDevcontainerCli,
    string? ComposeService = null,
    string? ComposeProject = null);

/// <summary>Recognising <see cref="ContainerExecTarget"/>s in argv (pure: tested without any engine).</summary>
internal static class ContainerExec
{
    /// <summary>The engines whose <c>exec</c> is recognised, by argv[0]'s file name.</summary>
    public static bool IsEngine(string program) => EngineName(program) is not null;

    /// <summary>"docker" or "podman" for a path or name naming one (".exe" too), else null.</summary>
    public static string? EngineName(string program)
    {
        string name = FileName(program);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant() switch
        {
            "docker" => "docker",
            "podman" => "podman",
            _ => null,
        };
    }

    // Global options that take a value (docker 27, podman 5). Those that pick the daemon are kept for later calls.
    private static readonly HashSet<string> GlobalWithValue = new(StringComparer.Ordinal)
    {
        "--context", "-c", "-H", "--host", "--config", "--log-level", "-l", "--tlscacert", "--tlscert", "--tlskey",
        "--connection", "--url", "--identity", "--root", "--runroot", "--storage-driver", "--cgroup-manager",
        "--events-backend", "--runtime", "--tmpdir", "--module", "--network-cmd-path", "--conmon", "--imagestore",
    };

    private static readonly HashSet<string> GlobalKept = new(StringComparer.Ordinal)
    {
        "--context", "-c", "-H", "--host", "--config", "--tlscacert", "--tlscert", "--tlskey", "--tls", "--tlsverify",
        "--connection", "--url", "--identity", "--remote", "-r", "--root", "--runroot",
    };

    // exec's options that take a value. Single letters may also come clustered (-it, -itu root, -uroot).
    private static readonly HashSet<string> ExecWithValue = new(StringComparer.Ordinal)
    {
        "-e", "--env", "--env-file", "-u", "--user", "-w", "--workdir", "--detach-keys", "--preserve-fds", "--preserve-fd",
    };

    private const string ExecShortWithValue = "euw";

    // compose's options before its subcommand that take a value, and its exec's.
    private static readonly HashSet<string> ComposeGlobalWithValue = new(StringComparer.Ordinal)
    {
        "-f", "--file", "-p", "--project-name", "--project-directory", "--profile", "--env-file", "--ansi", "--progress", "--parallel",
    };

    private static readonly HashSet<string> ComposeExecWithValue = new(StringComparer.Ordinal)
    {
        "-e", "--env", "-u", "--user", "-w", "--workdir", "--index",
    };

    /// <summary>
    /// The container shell <paramref name="argv"/> runs, or null when it isn't one: an engine's <c>exec</c> (not
    /// detached, not podman's <c>--latest</c>, which names no container), or the devcontainer CLI's <c>exec</c>.
    /// </summary>
    public static ContainerExecTarget? Parse(IReadOnlyList<string>? argv)
    {
        if (argv is null || argv.Count < 2) return null;
        if (IsEngine(argv[0])) return ParseEngineExec(argv);
        if (StandaloneCompose(argv) is { } compose) return ParseComposeExec(argv, compose.Next, compose.Engine, []);
        return ParseDevcontainerExec(argv);
    }

    /// <summary><c>erikterm devcontainer …</c> before it execs the engine: what the window waits for.</summary>
    public static bool IsDevcontainerCommand(IReadOnlyList<string>? argv, string? selfPath)
    {
        if (argv is not { Count: >= 2 } || argv[1] != "devcontainer") return false;
        string name = FileName(argv[0]);
        if (name.Equals("erikterm", StringComparison.OrdinalIgnoreCase) || name.Equals("erikterm.exe", StringComparison.OrdinalIgnoreCase)) return true;
        return selfPath is not null && string.Equals(argv[0], selfPath, StringComparison.Ordinal);
    }

    private static ContainerExecTarget? ParseEngineExec(IReadOnlyList<string> argv)
    {
        int i = 1;
        var kept = GlobalOptions(argv, ref i);
        if (i < argv.Count && argv[i] == "compose") return ParseComposeExec(argv, i + 1, argv[0], kept); // docker compose / podman compose
        if (i < argv.Count && argv[i] == "container") i++; // docker container exec / podman container exec
        if (i >= argv.Count || argv[i] != "exec") return null;
        i++;
        if (!ExecOptions(argv, ref i, out string? user, out string? workdir)) return null;
        if (i >= argv.Count || argv[i].Length == 0) return null;
        return new ContainerExecTarget(argv[0], kept, argv[i], null, user, workdir, false);
    }

    /// <summary>
    /// exec's options, up to the container they come before (moving past them): the user and workdir they name. False
    /// for a detached exec (nothing to show) and podman's <c>--latest</c> (no container named).
    /// </summary>
    private static bool ExecOptions(IReadOnlyList<string> argv, ref int i, out string? user, out string? workdir)
    {
        user = null;
        workdir = null;
        while (i < argv.Count)
        {
            string a = argv[i];
            if (a == "--") { i++; return true; }
            if (a.Length < 2 || a[0] != '-') return true;
            if (a is "-d" or "--detach" or "-l" or "--latest") return false;
            if (a.StartsWith("--", StringComparison.Ordinal)) ParseLongOption(argv, ref i, ref user, ref workdir);
            else if (!ParseShortCluster(argv, ref i, ref user, ref workdir)) return false;
        }
        return true;
    }

    /// <summary><c>--user root</c>, <c>--workdir=/src</c> and the like: the value after '=', else the next word when the option takes one.</summary>
    private static void ParseLongOption(IReadOnlyList<string> argv, ref int i, ref string? user, ref string? workdir)
    {
        string key = OptionName(argv[i]);
        string? value = OptionValue(argv, ref i, ExecWithValue.Contains(key));
        if (key == "--user") user = value;
        else if (key == "--workdir") workdir = value;
        i++;
    }

    /// <summary>
    /// <c>docker-compose …</c>, or <c>podman-compose …</c> (a Python script: <c>python3 /usr/bin/podman-compose …</c>):
    /// the engine its containers run on, and where its arguments start. Null for anything else.
    /// </summary>
    private static (string Engine, int Next)? StandaloneCompose(IReadOnlyList<string> argv)
    {
        for (int k = 0; k < Math.Min(2, argv.Count); k++)
        {
            string name = FileName(argv[k]);
            if (name is "docker-compose" or "docker-compose.exe") return ("docker", k + 1);
            if (name is "podman-compose") return ("podman", k + 1);
        }
        return null;
    }

    /// <summary>
    /// <c>compose [options] exec [options] SERVICE command</c>: the service (and the project when <c>-p</c> names it). Not
    /// a detached exec.
    /// </summary>
    private static ContainerExecTarget? ParseComposeExec(IReadOnlyList<string> argv, int i, string engine, List<string> engineOptions)
    {
        string? project = ComposeProject(argv, ref i);
        if (i >= argv.Count || argv[i] != "exec") return null;
        i++;
        if (!ComposeExecOptions(argv, ref i, out string? user, out string? workdir)) return null;
        if (i >= argv.Count || argv[i].Length == 0) return null;
        return new ContainerExecTarget(engine, engineOptions, null, null, user, workdir, false, argv[i], project);
    }

    /// <summary>compose's options before its subcommand (moving past them): the project <c>-p</c> names, if any.</summary>
    private static string? ComposeProject(IReadOnlyList<string> argv, ref int i)
    {
        string? project = null;
        while (i < argv.Count && argv[i].StartsWith('-'))
        {
            string key = OptionName(argv[i]);
            string? value = OptionValue(argv, ref i, ComposeGlobalWithValue.Contains(key));
            if (key is "-p" or "--project-name") project = value;
            i++;
        }
        return project;
    }

    /// <summary>compose exec's options (moving past them): the user and workdir they name. False for a detached exec.</summary>
    private static bool ComposeExecOptions(IReadOnlyList<string> argv, ref int i, out string? user, out string? workdir)
    {
        user = null;
        workdir = null;
        while (i < argv.Count && argv[i].StartsWith('-'))
        {
            string key = OptionName(argv[i]);
            if (key is "-d" or "--detach") return false;
            string? value = OptionValue(argv, ref i, ComposeExecWithValue.Contains(key));
            if (key is "-u" or "--user") user = value;
            else if (key is "-w" or "--workdir") workdir = value;
            i++;
        }
        return true;
    }

    /// <summary>An option's value: after '=' in the same word, else the next word when it takes one (moving past it).</summary>
    private static string? OptionValue(IReadOnlyList<string> argv, ref int i, bool takesValue)
    {
        string a = argv[i];
        if (a.IndexOf('=') is > 0 and var eq) return a[(eq + 1)..];
        if (!takesValue || i + 1 >= argv.Count) return null;
        return argv[++i];
    }

    /// <summary>The engine's options before its subcommand; those that pick the daemon or connection are returned, to be used again.</summary>
    private static List<string> GlobalOptions(IReadOnlyList<string> argv, ref int i)
    {
        var kept = new List<string>();
        while (i < argv.Count && argv[i].StartsWith('-'))
        {
            string a = argv[i];
            string key = OptionName(a);
            bool takesValue = GlobalWithValue.Contains(key) && !a.Contains('=');
            if (GlobalKept.Contains(key))
            {
                kept.Add(a);
                if (takesValue && i + 1 < argv.Count) kept.Add(argv[i + 1]);
            }
            i += takesValue ? 2 : 1;
        }
        return kept;
    }

    /// <summary>
    /// <c>-it</c>, <c>-itu root</c>, <c>-uroot</c>, <c>-w /src</c>: letters until one that takes a value, which is the
    /// rest of the word or the next one. False for a detached exec (<c>-d</c> anywhere in the cluster).
    /// </summary>
    private static bool ParseShortCluster(IReadOnlyList<string> argv, ref int i, ref string? user, ref string? workdir)
    {
        string a = argv[i];
        for (int c = 1; c < a.Length; c++)
        {
            char ch = a[c];
            if (ch is 'd' or 'l') return false;
            if (!ExecShortWithValue.Contains(ch)) continue;
            string? value = c < a.Length - 1 ? a[(c + 1)..] : NextWord(argv, ref i);
            if (ch == 'u') user = value;
            else if (ch == 'w') workdir = value;
            break;
        }
        i++;
        return true;
    }

    /// <summary>The word after <paramref name="i"/> (moving to it), or null at the end.</summary>
    private static string? NextWord(IReadOnlyList<string> argv, ref int i) => i + 1 < argv.Count ? argv[++i] : null;

    /// <summary>
    /// The devcontainer CLI is a node script: <c>node /…/devcontainer.js exec …</c> (or <c>devcontainer exec …</c>
    /// through its shebang). Its options are scanned wherever they are; the command after them doesn't matter here.
    /// </summary>
    private static ContainerExecTarget? ParseDevcontainerExec(IReadOnlyList<string> argv)
    {
        int script = DevcontainerScript(argv);
        if (script < 0 || script + 1 >= argv.Count || argv[script + 1] != "exec") return null;

        string? folder = null, container = null, dockerPath = null;
        for (int i = script + 2; i < argv.Count; i++)
        {
            string a = argv[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
            string key = OptionName(a);
            string? value = a.IndexOf('=') is > 0 and var eq ? a[(eq + 1)..] : argv.ElementAtOrDefault(i + 1);
            switch (key)
            {
                case "--workspace-folder": folder = value; break;
                case "--container-id": container = value; break;
                case "--docker-path": dockerPath = value; break;
            }
        }
        return new ContainerExecTarget(dockerPath ?? "docker", [], container, container is null ? folder : null, null, null, true);
    }

    /// <summary>Where the devcontainer CLI's script is in the first three words, or -1.</summary>
    private static int DevcontainerScript(IReadOnlyList<string> argv)
    {
        for (int k = 0; k < Math.Min(3, argv.Count); k++)
            if (FileName(argv[k]) is "devcontainer" or "devcontainer.js" or "devcontainer.cmd") return k;
        return -1;
    }

    private static string OptionName(string a) => a.IndexOf('=') is > 0 and var eq ? a[..eq] : a;

    /// <summary>The last path segment, for '/' and '\' alike (a Windows argv seen on Linux, and the other way round).</summary>
    private static string FileName(string path)
    {
        int slash = path.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
