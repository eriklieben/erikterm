using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ErikTerm.App.Containers;
using ErikTerm.App.Integrations;
using ErikTerm.Core;

namespace ErikTerm.App.Cli;

/// <summary>
/// <c>erikterm devcontainer [shell|up] …</c>: a shell in a folder's dev container, set up the way erikterm's own
/// shells are (like <see cref="SshCommand"/> for a remote). The container is found by the <c>devcontainer.local_folder</c>
/// label the devcontainer CLI and VS Code put on it, started when it is stopped, and made with <c>devcontainer up</c>
/// when there is none yet. The first time a container is used, erikterm's shell integration scripts and terminfo
/// source are copied into <c>/usr/local/share/erikterm-HASH</c> (<c>docker cp</c> / <c>podman cp</c>, which need nothing in the
/// image) and the entry is compiled with <c>tic</c>; that is remembered per container (and per start of it). Then the
/// engine's <c>exec -it</c> replaces this process, so the pane's program is the exec itself, which is what the window
/// recognises as a container pane (WindowFeatures/WindowContainers.cs).
/// </summary>
internal static class DevcontainerCommand
{
    public const string Usage = """
        usage: erikterm devcontainer [shell] [options] [-- command [args]]
               erikterm devcontainer up [--rebuild] [options]

        A shell in a folder's dev container, with erikterm's terminfo and shell integration set up inside, so new
        splits and tabs open in the container too. The folder is the nearest one up with a .devcontainer/devcontainer.json
        (else the git repository you're in). A stopped container is started; when there is none, `devcontainer up`
        makes it (that needs the devcontainer CLI: npm install -g @devcontainers/cli). `up` always runs it first.

          --workspace-folder DIR   the folder the dev container is for
          --container ID|NAME      use this container instead of looking one up
          --engine docker|podman   default: docker, else podman (whichever is on PATH)
          --engine-option OPT      a global option for every engine call (repeat it: --engine-option --context ...)
          --user USER              default: the container's remoteUser
          --workdir DIR            where the shell starts inside (default: where you are, else the workspace folder)
          --rebuild                with up: remove the existing container and build it again
          --no-shell-integration   don't load erikterm's shell integration inside
        """;

    /// <summary>The shell every POSIX image has: runs the setup and launch scripts, and is the fallback shell.</summary>
    private const string PosixShell = "/bin/sh";

    public enum Mode { Shell, Up, Help }

    public sealed record Options(
        Mode Mode,
        bool Rebuild,
        string? WorkspaceFolder,
        string? Container,
        string? Engine,
        List<string> EngineOptions,
        string? User,
        string? Workdir,
        bool ShellIntegration,
        bool FallbackShell,
        List<string> Command,
        string? Error);

    public static Options Parse(IReadOnlyList<string> args)
    {
        var mode = Mode.Shell;
        int i = 0;
        if (args.Count > 0 && args[0] is "shell" or "up") { mode = args[0] == "up" ? Mode.Up : Mode.Shell; i = 1; }
        var o = new Options(mode, Rebuild: false, WorkspaceFolder: null, Container: null, Engine: null, EngineOptions: [], User: null,
            Workdir: null, ShellIntegration: true, FallbackShell: false, Command: [], Error: null);
        while (i < args.Count)
        {
            string a = args[i++];
            if (a == "--") { o.Command.AddRange(args.Skip(i)); break; }
            o = ParseOption(o, a, args, ref i);
        }
        if (o.Rebuild && o.Mode != Mode.Up) o = o with { Error = o.Error ?? "--rebuild goes with up" };
        if (o.Engine is not null && ContainerExec.EngineName(o.Engine) is null) o = o with { Error = o.Error ?? $"--engine {o.Engine}: docker or podman" };
        return o;
    }

    /// <summary>One option <paramref name="a"/>, and its value from <paramref name="args"/> when it takes one (moving past it).</summary>
    private static Options ParseOption(Options o, string a, IReadOnlyList<string> args, ref int i)
    {
        switch (a)
        {
            case "-h" or "--help": return o with { Mode = Mode.Help };
            case "--rebuild": return o with { Rebuild = true };
            case "--no-shell-integration": return o with { ShellIntegration = false };
            case "--fallback-shell": return o with { FallbackShell = true }; // erikterm's own panes: a host shell instead of a dead pane
            case "--workspace-folder" or "--container" or "--engine" or "--engine-option" or "--user" or "--workdir":
                if (i < args.Count) return WithValue(o, a, args[i++]);
                return WithValue(o, a, null) with { Error = $"{a} needs a value" };
            default: return o with { Error = o.Error ?? $"unknown option {a}" };
        }
    }

    private static Options WithValue(Options o, string option, string? value)
    {
        switch (option)
        {
            case "--workspace-folder": return o with { WorkspaceFolder = value };
            case "--container": return o with { Container = value };
            case "--engine": return o with { Engine = value };
            case "--user": return o with { User = value };
            case "--workdir": return o with { Workdir = value };
            default: // --engine-option, which may be given more than once
                if (value is not null) o.EngineOptions.Add(value);
                return o;
        }
    }

    // ---- Setting a container up --------------------------------------------------------------------

    public const string TerminfoFile = "xterm-erikterm.ti";
    public const string MarkerTerminfo = "ERIKTERM_TERMINFO_OK";
    public const string MarkerIntegration = "ERIKTERM_INTEGRATION_OK";

    /// <summary>
    /// Run in the container as <c>sh -c SCRIPT erikterm-setup DIR</c>: compiles the entry (into ~/.terminfo, or for root
    /// into the system's terminfo directory, since some ncurses builds ignore root's ~/.terminfo) and reports it only when
    /// <c>infocmp</c> then finds it; says whether the scripts are there, and finds the user's shell (their passwd entry,
    /// $SHELL, then zsh, bash, sh; never a nologin). One marker or KEY=value per line.
    /// </summary>
    public const string SetupScript =
        "d=$1; t=\"$d/" + TerminfoFile + "\"; " +
        "if ! infocmp -x xterm-erikterm >/dev/null 2>&1 && [ -r \"$t\" ] && command -v tic >/dev/null 2>&1; then " +
        "if [ \"$(id -u 2>/dev/null)\" = 0 ]; then tic -x \"$t\" >/dev/null 2>&1; " +
        "else mkdir -p \"$HOME/.terminfo\" 2>/dev/null && tic -x -o \"$HOME/.terminfo\" \"$t\" >/dev/null 2>&1; fi; fi; " +
        "infocmp -x xterm-erikterm >/dev/null 2>&1 && echo " + MarkerTerminfo + "; " +
        "[ -r \"$d/erikterm.zsh\" ] && echo " + MarkerIntegration + "; " +
        "u=$(id -un 2>/dev/null); s=; " +
        "if command -v getent >/dev/null 2>&1; then s=$(getent passwd \"$u\" 2>/dev/null | cut -d: -f7); fi; " +
        "[ -n \"$s\" ] || s=$(grep \"^$u:\" /etc/passwd 2>/dev/null | cut -d: -f7); " +
        "for c in \"$s\" \"${SHELL:-}\" /bin/zsh /bin/bash /bin/sh; do " +
        "case \"$c\" in ''|*nologin|*false) continue;; esac; " +
        "if [ -x \"$c\" ]; then echo \"ERIKTERM_SHELL=$c\"; break; fi; done; " +
        "echo \"ERIKTERM_HOME=$HOME\"";

    /// <summary>What <see cref="SetupScript"/> found.</summary>
    public sealed record Setup(bool Terminfo, bool Integration, string? Shell, string? Home);

    public static Setup ParseSetupOutput(string output)
    {
        bool terminfo = false, integration = false;
        string? shell = null, home = null;
        foreach (var raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line == MarkerTerminfo) terminfo = true;
            else if (line == MarkerIntegration) integration = true;
            else if (line.StartsWith("ERIKTERM_SHELL=", StringComparison.Ordinal) && line.Length > 15) shell = line[15..];
            else if (line.StartsWith("ERIKTERM_HOME=", StringComparison.Ordinal) && line.Length > 14) home = line[14..];
        }
        return new Setup(terminfo, integration, shell, home);
    }

    /// <summary>The files copied into the container, and the hash that names their directory there.</summary>
    public static (List<(string Path, byte[] Content)> Files, string Tag) Payload()
    {
        var files = ShellIntegration.Payload() ?? [];
        files.Add((TerminfoFile, Encoding.UTF8.GetBytes(TermCapabilities.TerminfoSource())));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, content) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\0"));
            hash.AppendData(content);
        }
        return (files, Convert.ToHexStringLower(hash.GetHashAndReset())[..16]);
    }

    /// <summary>
    /// Where the payload goes in the container: a directory only root can write to (<c>docker cp</c> copies as root), so
    /// no other user in the container can put their own scripts there first for the shell to load, as one could in
    /// /tmp, whose names are open to everyone and this one predictable. /usr/local/share is in the FHS, so the common base
    /// images (Debian, Ubuntu, Alpine, Fedora) have it; where it is missing the copy fails and the shell starts without
    /// the integration, TERM falling back, as when a copy fails anywhere.
    /// </summary>
    public static string ContainerDirectory(string tag) => "/usr/local/share/erikterm-" + tag;

    /// <summary>
    /// Writes the payload to <paramref name="dir"/> readable by everyone (0755 / 0644): the copy in the container belongs
    /// to its root, and the dev container's user has to read it. Nothing in it is secret.
    /// </summary>
    public static bool Stage(string dir, List<(string Path, byte[] Content)> files)
    {
        try
        {
            const UnixFileMode dirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            foreach (var (path, content) in files)
            {
                string target = Path.Combine(dir, path);
                string parent = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(parent);
                // The mode explicitly: creating applies the umask, which may leave the directories closed to others.
                if (!OperatingSystem.IsWindows())
                    for (string? d = parent; d is not null && d.Length >= dir.Length; d = Path.GetDirectoryName(d)) File.SetUnixFileMode(d, dirMode);
                if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(content)) continue;
                File.WriteAllBytes(target, content);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, fileMode);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---- The cache: one line per container start, "root TAB id TAB started TAB user TAB terminfo TAB integration TAB shell TAB home" --

    /// <summary>The cache's key for a setup of the payload in <paramref name="root"/> (its directory in the container, named by its tag).</summary>
    public static string CacheKey(string root, ContainerInfo c, string? user) => string.Join('\t', root, c.Id, c.StartedAt, user ?? "");

    public static string DefaultCachePath(Func<string, string?> env) =>
        Path.Combine(Path.GetDirectoryName(SshCommand.DefaultCachePath(env))!, "container-setup");

    public static Setup? Lookup(string cachePath, string key)
    {
        try
        {
            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length > 1 << 20) return null;
            foreach (var line in File.ReadLines(cachePath))
            {
                var p = line.Split('\t');
                if (p.Length == 8 && string.Join('\t', p[..4]) == key)
                    return new Setup(p[4] == "1", p[5] == "1", p[6].Length > 0 ? p[6] : null, p[7].Length > 0 ? p[7] : null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable cache is a miss */ }
        return null;
    }

    public static void Store(string cachePath, string key, Setup s)
    {
        if (s.Shell is null) return; // the setup didn't run: try again next time
        var lines = OtherContainers(cachePath, key.Split('\t')[1]);
        lines.Add(string.Join('\t', key, s.Terminfo ? "1" : "0", s.Integration ? "1" : "0", Clean(s.Shell), Clean(s.Home)));
        if (lines.Count > 200) lines.RemoveRange(0, lines.Count - 200);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string tmp = $"{cachePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, string.Concat(lines.Select(l => l + "\n")));
            File.Move(tmp, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not cached: the next shell sets up again */ }
    }

    /// <summary>The cache's lines for containers other than <paramref name="id"/>: older starts of that one go.</summary>
    private static List<string> OtherContainers(string cachePath, string id)
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(cachePath)) return lines;
            foreach (var line in File.ReadLines(cachePath))
                if (line.Split('\t') is { Length: 8 } p && p[1] != id) lines.Add(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable cache starts over */ }
        return lines;
    }

    private static string Clean(string? v) => string.Concat((v ?? "").Where(ch => ch is not ('\t' or '\n' or '\r')));

    // ---- The shell ---------------------------------------------------------------------------------------

    /// <summary>
    /// Run in the container as <c>/bin/sh -c LAUNCH erikterm-shell MARKER WORKDIR FALLBACK SHELL ARGV…</c>: goes to the
    /// directory (else the workspace, else home: a directory that is gone mustn't stop the shell), then runs the shell
    /// with the integration (ARGV) while its files are there, else the plain shell without the injected variables
    /// (a ZDOTDIR naming a missing directory would skip the user's own .zshrc).
    /// </summary>
    public const string LaunchScript =
        "m=$1; w=$2; f=$3; s=$4; shift 4; " +
        "cd \"$w\" 2>/dev/null || cd \"$f\" 2>/dev/null || cd; " +
        "if [ -n \"$m\" ] && [ -r \"$m\" ]; then exec \"$@\"; fi; " +
        "unset ZDOTDIR ENV XDG_DATA_DIRS HISTFILE " + ShellIntegration.OrigZdotdirVariable + " " + ShellIntegration.OrigEnvVariable + " " +
        ShellIntegration.OrigXdgDataDirsVariable + " " + ShellIntegration.UnexportHistfileVariable + "; " +
        "exec \"$s\"";

    /// <summary>Where an exec runs: the container, as whom (null: the engine's default) and in which directory.</summary>
    public readonly record struct ExecTarget(ContainerInfo Container, string? User, string Workdir);

    /// <summary>The shell an exec starts: what <see cref="SetUp"/> found, and the integration staged at <c>Root</c> when <c>Integration</c> is on.</summary>
    public readonly record struct ExecShell(Setup Setup, string? Root, bool Integration);

    /// <summary>
    /// The engine's exec: environment through <c>-e</c> (TERM, the terminal's variables, the integration's), then a
    /// <paramref name="command"/> as given, or the shell through <see cref="LaunchScript"/>.
    /// </summary>
    public static List<string> ExecArgv(ContainerEngine engine, ExecTarget target, ExecShell shellSetup,
        IReadOnlyDictionary<string, string> terminalEnv, IReadOnlyList<string> command, bool tty)
    {
        var (c, user, workdir) = target;
        var (setup, root, integration) = shellSetup;
        var env = new Dictionary<string, string>(terminalEnv, StringComparer.Ordinal);
        var argv = engine.Argv("exec", tty ? "-it" : "-i");
        if (user is { Length: > 0 }) argv.AddRange(["-u", user]);
        if (command.Count > 0)
        {
            argv.AddRange(["-w", workdir]);
            AddEnvironment(argv, env);
            argv.Add(c.Id);
            argv.AddRange(command);
            return argv;
        }

        string shell = setup.Shell ?? PosixShell;
        string[] shellArgv = [shell];
        string marker = "";
        if (integration && root is not null && setup.Integration)
        {
            var injected = new Dictionary<string, string>(StringComparer.Ordinal);
            if (setup.Home is { Length: > 0 } home) injected["HOME"] = home;
            var kind = ShellIntegration.Recognize(shellArgv);
            if (kind is not (ShellIntegration.ShellKind.None or ShellIntegration.ShellKind.PowerShell))
            {
                shellArgv = ShellIntegration.Inject(kind, shellArgv, injected, root);
                injected.Remove("HOME");
                foreach (var (k, v) in injected) env[k] = v.Replace('\\', '/'); // Path.Combine on a Windows host
                marker = root + "/erikterm.zsh";
            }
        }
        AddEnvironment(argv, env);
        argv.Add(c.Id);
        argv.AddRange([PosixShell, "-c", LaunchScript, "erikterm-shell", marker, workdir, c.WorkspaceFolder, shell]);
        argv.AddRange(shellArgv);
        return argv;
    }

    private static void AddEnvironment(List<string> argv, Dictionary<string, string> env)
    {
        foreach (var (k, v) in env.OrderBy(e => e.Key, StringComparer.Ordinal)) argv.AddRange(["-e", k + "=" + v]);
    }

    /// <summary>
    /// The variables a shell in the container gets from the terminal: TERM (erikterm's entry when it is installed there,
    /// else xterm-256color; a TERM the user chose with the term setting stays), COLORTERM, TERM_PROGRAM(_VERSION), and a
    /// worktree's port block.
    /// </summary>
    public static Dictionary<string, string> TerminalEnvironment(Func<string, string?> env, bool terminfoInstalled)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        result["TERM"] = TermInside(env("TERM"), terminfoInstalled);
        result["TERM_PROGRAM"] = "erikterm";
        foreach (var name in (ReadOnlySpan<string>)["COLORTERM", "TERM_PROGRAM_VERSION", Worktrees.WorktreePorts.BaseVariable, Worktrees.WorktreePorts.CountVariable])
            if (env(name) is { Length: > 0 } v) result[name] = v;
        return result;
    }

    private static string TermInside(string? term, bool terminfoInstalled)
    {
        if (term == TermCapabilities.Name) return terminfoInstalled ? TermCapabilities.Name : TermCapabilities.BaseEntry;
        return term is { Length: > 0 } ? term : TermCapabilities.BaseEntry;
    }

    /// <summary>Where the shell starts inside: --workdir, else this directory when a bind mount holds it, else the workspace folder.</summary>
    public static string ShellWorkdir(Options o, ContainerInfo c, string? cwd)
    {
        if (o.Workdir is { Length: > 0 } w) return w;
        string? mounted = cwd is null ? null : ContainerPaths.ToContainer(c.Mounts, cwd);
        return mounted ?? c.WorkspaceFolder;
    }

    // ---- Running ------------------------------------------------------------------------------------------

    public static int Run(IReadOnlyList<string> args)
    {
        var o = Parse(args);
        if (o.Mode == Mode.Help) { Console.WriteLine(Usage); return 0; }
        if (o.Error is { } error) { Console.Error.WriteLine($"erikterm devcontainer: {error}\n\n{Usage}"); return 2; }
        int code = Launch(o, out string? problem);
        if (problem is null) return code;
        Console.Error.WriteLine($"erikterm devcontainer: {problem}");
        return o.FallbackShell ? HostShell() : code;
    }

    private static int Launch(Options o, out string? problem)
    {
        problem = null;
        if (ContainerEngine.Find(o.Engine, o.EngineOptions) is not { } engine)
        {
            problem = o.Engine is null ? "neither docker nor podman is on PATH" : $"{o.Engine} isn't on PATH";
            return 127;
        }
        string cwd = Directory.GetCurrentDirectory();
        var c = FindContainer(o, engine, cwd, out problem);
        if (c is null) return 1;
        if (!c.Running)
        {
            Console.Error.WriteLine($"erikterm devcontainer: starting {c.DisplayName} ({c.ShortId})");
            if (!engine.Start(c.Id) || engine.Inspect(c.Id) is not { Running: true } started) { problem = $"couldn't start {c.ShortId}"; return 1; }
            c = started;
        }

        string? user = o.User ?? c.RemoteUser;
        var (files, tag) = Payload();
        string cachePath = DefaultCachePath(Environment.GetEnvironmentVariable);
        string root = ContainerDirectory(tag);
        string key = CacheKey(root, c, user); // by the directory: a setup of the payload elsewhere (an older erikterm's) is a miss
        var setup = Lookup(cachePath, key);
        if (setup is null)
        {
            setup = SetUp(engine, c, user, files, tag, Path.GetDirectoryName(cachePath)!);
            Store(cachePath, key, setup);
        }

        var terminalEnv = TerminalEnvironment(Environment.GetEnvironmentVariable, setup.Terminfo);
        bool tty = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var argv = ExecArgv(engine, new ExecTarget(c, user, ShellWorkdir(o, c, cwd)), new ExecShell(setup, root, o.ShellIntegration), terminalEnv, o.Command, tty);
        return Exec(argv, out problem);
    }

    /// <summary>--container, else the folder's dev container; <c>devcontainer up</c> first for up, or when the folder has none yet.</summary>
    private static ContainerInfo? FindContainer(Options o, ContainerEngine engine, string cwd, out string? problem)
    {
        problem = null;
        ContainerInfo? c = o.Container is { } id ? engine.Inspect(id) : null;
        string? folder = FolderFor(o, cwd);
        if (c is null && folder is not null) c = engine.FindDevContainer(folder);
        bool create = o.Mode == Mode.Up || (c is null && o.Container is null); // a restored pane (--container) never builds one
        if (create && folder is not null)
        {
            problem = DevcontainerUp(o, engine, folder, exists: c is not null);
            if (problem is not null) return null;
            c = engine.FindDevContainer(folder);
        }
        if (c is null) problem = o.Container is { } wanted ? $"no container {wanted}" : $"no dev container for {folder}";
        return c;
    }

    /// <summary>--workspace-folder, else (without --container) the folder holding this directory's dev container config.</summary>
    private static string? FolderFor(Options o, string cwd)
    {
        if (o.WorkspaceFolder is { } f) return Path.GetFullPath(f);
        return o.Container is null ? ContainerEngine.WorkspaceFolderFor(cwd) : null;
    }

    /// <summary>Runs <c>devcontainer up</c> for <paramref name="folder"/>: null when it ran, else why it couldn't.</summary>
    private static string? DevcontainerUp(Options o, ContainerEngine engine, string folder, bool exists)
    {
        if (!exists && !ContainerEngine.HasDevcontainerConfig(folder)) return $"no dev container for {folder}, and no .devcontainer/devcontainer.json there";
        if (ContainerEngine.FindDevcontainerCli() is not { } cli)
            return $"no dev container for {folder} yet; making one needs the devcontainer CLI (npm install -g @devcontainers/cli)";
        List<string> up = [cli, "up", "--workspace-folder", folder, "--docker-path", engine.Program];
        if (o.Rebuild) up.Add("--remove-existing-container");
        return RunVisible(up) is int code and not 0 ? $"devcontainer up failed (exit {code})" : null;
    }

    /// <summary>Copies the payload in and runs <see cref="SetupScript"/>; without the copy the shell still starts (TERM falls back).</summary>
    private static Setup SetUp(ContainerEngine engine, ContainerInfo c, string? user, List<(string Path, byte[] Content)> files, string tag, string stateDir)
    {
        string stage = Path.Combine(stateDir, "container-payload", tag);
        string root = ContainerDirectory(tag);
        if (Stage(stage, files))
            ToolRunner.Run(engine.Argv("cp", Path.Combine(stage, "."), c.Id + ":" + root), null, ContainerEngine.TimeoutMs);
        var argv = engine.Argv("exec", "-i");
        if (user is { Length: > 0 }) argv.AddRange(["-u", user]);
        argv.AddRange([c.Id, PosixShell, "-c", SetupScript, "erikterm-setup", root]);
        var r = ToolRunner.Run(argv, null, ContainerEngine.TimeoutMs);
        return r.StartError is null && !r.TimedOut ? ParseSetupOutput(r.Stdout) : new Setup(false, false, null, null);
    }

    /// <summary>Runs a program with this terminal as its own (devcontainer up's build output is worth seeing); its exit code, or null when it couldn't start.</summary>
    private static int? RunVisible(IReadOnlyList<string> argv)
    {
        try
        {
            var psi = new ProcessStartInfo(argv[0]) { UseShellExecute = false };
            for (int i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>Linux / macOS: the exec replaces this process (the pane's program becomes it). Windows: a child, waited for.</summary>
    private static int Exec(List<string> argv, out string? problem)
    {
        problem = null;
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        if (!OperatingSystem.IsWindows())
        {
            string error = Pty.Pty.ExecReplace(argv[0], argv, env); // returns only on failure
            problem = $"{argv[0]}: {error}";
            return 127;
        }
        int? code = RunVisible(argv);
        if (code is null) problem = $"{argv[0]}: couldn't start";
        return code ?? 127;
    }

    /// <summary>--fallback-shell: the user's shell on the host, where the pane is, so a container that is gone leaves a working pane.</summary>
    private static int HostShell()
    {
        if (OperatingSystem.IsWindows()) return 1;
        string shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } s && File.Exists(s) ? s : PosixShell;
        Console.Error.WriteLine($"erikterm devcontainer: opening {Path.GetFileName(shell)} on this machine instead");
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        Pty.Pty.ExecReplace(shell, [shell], env);
        return 127;
    }
}
