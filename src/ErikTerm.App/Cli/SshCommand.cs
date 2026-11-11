using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ErikTerm.Core;

namespace ErikTerm.App.Cli;

/// <summary>
/// <c>erikterm ssh [ssh options] destination [command]</c> — ssh with erikterm's terminfo (like kitty's <c>kitten ssh</c>):
/// the first time a host is used, the built-in <c>xterm-erikterm</c> source (<see cref="TermCapabilities.TerminfoSource"/>,
/// so the local entry needn't be installed) goes to the remote over ssh's stdin and is compiled there with
/// <c>tic -x -o ~/.terminfo</c>; the result is cached per host (and per version of the entry) in the state directory.
/// Then ssh runs as you asked — exec'd, same arguments — with <c>TERM=xterm-erikterm</c>, or <c>TERM=xterm-256color</c>
/// when the remote has no <c>tic</c>. Nothing else is installed on the remote (shell integration stays yours to
/// set up). Outside erikterm it is plain ssh.
/// </summary>
internal static class SshCommand
{
    public const string Usage = """
        usage: erikterm ssh [ssh options] destination [command]

        Runs ssh with TERM=xterm-erikterm, first installing that terminfo entry on the remote (once per host:
        tic -x -o ~/.terminfo, from erikterm's built-in source); without tic there, TERM=xterm-256color.
        Remembered in $XDG_STATE_HOME/erikterm/ssh-terminfo; delete a host's line (or the file) to check again.
        Outside erikterm it runs plain ssh.
        """;

    /// <summary>ssh's options that take an argument (OpenSSH 9: <c>ssh -h</c>).</summary>
    private const string OptionsWithArgument = "BbcDEeFIiJLlmOoPpQRSWw";

    /// <summary>Options that would make the install connection do something else (forward, background, no command, tty).</summary>
    private const string InstallDropsFlags = "fNnTtMGsV";
    private const string InstallDropsWithArgument = "DLRWwO";

    public const string MarkerInstalled = "ERIKTERM_TERMINFO_OK";
    public const string MarkerNoTic = "ERIKTERM_TERMINFO_NOTIC";

    /// <summary>
    /// Run by the remote login shell as <c>sh -c '…'</c> (one argument, single-quoted: the same in sh, bash, zsh, fish and
    /// csh); the entry's source arrives on stdin. Compiles it into ~/.terminfo, or for root into the system's terminfo
    /// directory (some ncurses builds, Arch's among them, ignore root's ~/.terminfo), and reports it installed only when
    /// <c>infocmp</c> then finds it. Prints one marker line.
    /// </summary>
    public const string RemoteScript =
        "if command -v tic >/dev/null 2>&1; then " +
        "t=$(mktemp 2>/dev/null || echo /tmp/erikterm-terminfo.$$); cat > \"$t\"; " +
        "if [ \"$(id -u 2>/dev/null)\" = 0 ]; then tic -x \"$t\" >/dev/null 2>&1; " +
        "else mkdir -p ~/.terminfo && tic -x -o ~/.terminfo \"$t\" >/dev/null 2>&1; fi; rm -f \"$t\"; " +
        "if infocmp -x xterm-erikterm >/dev/null 2>&1; then echo " + MarkerInstalled + "; else echo ERIKTERM_TERMINFO_FAILED; fi; " +
        "else cat >/dev/null; " +
        "if infocmp -x xterm-erikterm >/dev/null 2>&1; then echo " + MarkerInstalled + "; else echo " + MarkerNoTic + "; fi; fi";

    public enum InstallResult { Installed, NoTic, Failed }

    /// <summary>The command line split into ssh's options (with their arguments), the destination and the remote command.</summary>
    public sealed record Arguments(List<string> Options, string? Destination, List<string> Command);

    public static Arguments Split(IReadOnlyList<string> args)
    {
        var options = new List<string>();
        int i = 0;
        while (i < args.Count)
        {
            string a = args[i];
            if (a == "--") { i++; break; }
            if (a.Length < 2 || a[0] != '-') break;
            options.Add(a);
            // -vp 22, -p22, -oFoo=bar: an option letter that takes an argument ends the cluster; its argument is the
            // rest of the word or the next one.
            TakeClusterArgument(a, args, ref i, options);
            i++;
        }
        string? destination = i < args.Count ? args[i] : null;
        var command = i + 1 < args.Count ? args.Skip(i + 1).ToList() : [];
        return new Arguments(options, destination, command);
    }

    private static void TakeClusterArgument(string a, IReadOnlyList<string> args, ref int i, List<string> options)
    {
        for (int c = 1; c < a.Length; c++)
        {
            if (!OptionsWithArgument.Contains(a[c])) continue;
            if (c == a.Length - 1 && i + 1 < args.Count) options.Add(args[++i]);
            break;
        }
    }

    /// <summary>
    /// The options, one per word, without what would turn the install connection into something else: forwarding,
    /// background, no command, a tty, stdin from /dev/null, control commands, printing the version or the config.
    /// </summary>
    public static List<string> InstallOptions(IReadOnlyList<string> options)
    {
        var kept = new List<string>();
        for (int i = 0; i < options.Count; i++)
        {
            string a = options[i];
            if (a.Length < 2 || a[0] != '-') { kept.Add(a); continue; }
            KeepInstallFlags(a, options, ref i, kept);
        }
        return kept;
    }

    private static void KeepInstallFlags(string a, IReadOnlyList<string> options, ref int i, List<string> kept)
    {
        for (int c = 1; c < a.Length; c++)
        {
            char ch = a[c];
            if (OptionsWithArgument.Contains(ch))
            {
                KeepOptionWithArgument(ch, ClusterArgument(a, c, options, ref i), kept);
                return;
            }
            if (!InstallDropsFlags.Contains(ch)) kept.Add("-" + ch);
        }
    }

    private static string? ClusterArgument(string a, int c, IReadOnlyList<string> options, ref int i)
    {
        if (c < a.Length - 1) return a[(c + 1)..];
        return i + 1 < options.Count ? options[++i] : null;
    }

    private static void KeepOptionWithArgument(char ch, string? value, List<string> kept)
    {
        if (InstallDropsWithArgument.Contains(ch)) return;
        kept.Add("-" + ch);
        if (value is not null) kept.Add(value);
    }

    /// <summary>The install connection: no tty, no forwarding (also none from ssh_config), the script as the command.</summary>
    public static List<string> InstallArgv(string ssh, Arguments a) =>
        [ssh, .. InstallOptions(a.Options), "-T", "-o", "ClearAllForwardings=yes", "--", a.Destination!, "sh -c '" + RemoteScript + "'"];

    /// <summary><c>ssh -G</c> (prints the resolved configuration without connecting): host aliases and ~/.ssh/config count.</summary>
    public static List<string> ResolveArgv(string ssh, Arguments a) => [ssh, .. InstallOptions(a.Options), "-G", "--", a.Destination!];

    /// <summary>Which remote the cache is about: user@hostname:port from <c>ssh -G</c>'s output, else the destination as typed.</summary>
    public static string CacheKey(string destination, string? resolved)
    {
        string? user = null, host = null, port = null;
        foreach (var line in (resolved ?? "").Split('\n'))
        {
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            string k = line[..sp], v = line[(sp + 1)..].Trim();
            if (k == "user") user ??= v;
            else if (k == "hostname") host ??= v;
            else if (k == "port") port ??= v;
        }
        string key = host is null ? destination : $"{user}@{host}:{port ?? "22"}";
        return string.Concat(key.Where(c => !char.IsControl(c) && c != '\t'));
    }

    /// <summary>A short hash of the entry's source: a changed entry (a newer erikterm) is installed again.</summary>
    public static string SourceHash(string source) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];

    // ---- The cache: one line per remote, "hash TAB result TAB key" ------------------------------------------

    public static string DefaultCachePath(Func<string, string?> env)
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm", "ssh-terminfo");
        string state = env("XDG_STATE_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(state, "erikterm", "ssh-terminfo");
    }

    public static InstallResult? Lookup(string cachePath, string key, string hash)
    {
        try
        {
            if (!File.Exists(cachePath) || new FileInfo(cachePath).Length > 1 << 20) return null;
            foreach (var line in File.ReadLines(cachePath))
            {
                var parts = line.Split('\t');
                if (parts.Length == 3 && parts[2] == key && parts[0] == hash && Enum.TryParse<InstallResult>(parts[1], out var r) && r != InstallResult.Failed)
                    return r;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable cache is a miss */ }
        return null;
    }

    public static void Store(string cachePath, string key, string hash, InstallResult result)
    {
        if (result == InstallResult.Failed) return; // try again next time
        var lines = CacheLinesExcept(cachePath, key);
        lines.Add($"{hash}\t{result}\t{key}");
        if (lines.Count > 500) lines.RemoveRange(0, lines.Count - 500);
        WriteCache(cachePath, lines);
    }

    private static List<string> CacheLinesExcept(string cachePath, string key)
    {
        var lines = new List<string>();
        try
        {
            if (File.Exists(cachePath))
                foreach (var line in File.ReadLines(cachePath))
                    if (line.Split('\t') is { Length: 3 } p && p[2] != key) lines.Add(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable cache starts over */ }
        return lines;
    }

    private static void WriteCache(string cachePath, List<string> lines)
    {
        string dir = Path.GetDirectoryName(cachePath)!;
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string tmp = $"{cachePath}.{Environment.ProcessId}.tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var w = new StreamWriter(new FileStream(tmp, options))) foreach (var l in lines) w.Write(l + "\n");
            File.Move(tmp, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not cached: the next connection installs again */ }
    }

    /// <summary>TERM for the session: the entry when it is there, else what every remote knows.</summary>
    public static string TermFor(InstallResult result) => result == InstallResult.Installed ? TermCapabilities.Name : TermCapabilities.BaseEntry;

    /// <summary>The marker line in the install connection's output.</summary>
    public static InstallResult ParseInstallOutput(string output)
    {
        if (output.Contains(MarkerInstalled, StringComparison.Ordinal)) return InstallResult.Installed;
        return output.Contains(MarkerNoTic, StringComparison.Ordinal) ? InstallResult.NoTic : InstallResult.Failed;
    }

    // ---- Running ------------------------------------------------------------------------------------------

    /// <summary>Runs an install connection: the source on its stdin, its stdout read for the marker; prompts (password, host key) go to the terminal.</summary>
    public static InstallResult Install(IReadOnlyList<string> argv, string source)
    {
        if (Integrations.ToolRunner.ProgramPath(argv[0]) is not { } program) return InstallResult.Failed;
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        for (int i = 1; i < argv.Count; i++) psi.ArgumentList.Add(argv[i]);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return InstallResult.Failed;
            var output = p.StandardOutput.ReadToEndAsync();
            try
            {
                p.StandardInput.Write(source);
                p.StandardInput.Close();
            }
            catch (IOException) { /* it exited early (no connection) */ }
            string text = output.Result;
            p.WaitForExit();
            return ParseInstallOutput(text);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return InstallResult.Failed;
        }
    }

    /// <summary>What <see cref="Run"/> decided: the argv to run and its TERM, and a note for stderr (or null).</summary>
    public sealed record Plan(List<string> Argv, string? Term, string? Note);

    /// <summary>
    /// Works out the ssh to run. <paramref name="install"/> runs an install connection (stubbed in tests),
    /// <paramref name="resolve"/> runs <c>ssh -G</c> (null output: unavailable).
    /// </summary>
    public static Plan Prepare(IReadOnlyList<string> args, string ssh, Func<string, string?> env, string cachePath,
        Func<IReadOnlyList<string>, string, InstallResult> install, Func<IReadOnlyList<string>, string?> resolve)
    {
        var a = Split(args);
        List<string> argv = [ssh, .. args];
        bool insideErikterm = env("TERM_PROGRAM") == "erikterm";
        if (a.Destination is null || !insideErikterm) return new Plan(argv, null, null); // ssh's usage / plain ssh

        string source = TermCapabilities.TerminfoSource();
        string hash = SourceHash(source);
        string key = CacheKey(a.Destination, resolve(ResolveArgv(ssh, a)));
        if (Lookup(cachePath, key, hash) is { } cached) return new Plan(argv, TermFor(cached), null);

        var result = install(InstallArgv(ssh, a), source);
        Store(cachePath, key, hash, result);
        string? note = result switch
        {
            InstallResult.Installed => $"erikterm ssh: xterm-erikterm is installed on {a.Destination} (~/.terminfo)",
            InstallResult.NoTic => $"erikterm ssh: {a.Destination} has no tic: using TERM=xterm-256color there",
            _ => $"erikterm ssh: couldn't install the terminfo entry on {a.Destination}: using TERM=xterm-256color this time",
        };
        return new Plan(argv, TermFor(result), note);
    }

    public static int Run(IReadOnlyList<string> args)
    {
        if (args is ["-h" or "--help"]) { Console.WriteLine(Usage); return 0; }
        string? ssh = Integrations.ToolRunner.Find(OperatingSystem.IsWindows() ? "ssh.exe" : "ssh");
        if (ssh is null) { Console.Error.WriteLine("erikterm ssh: ssh isn't on PATH"); return 127; }

        var plan = Prepare(args, ssh, Environment.GetEnvironmentVariable, DefaultCachePath(Environment.GetEnvironmentVariable),
            Install, argv => Integrations.ToolRunner.Run(argv, null, 5000) is { Ok: true } r ? r.Stdout : null);
        if (plan.Note is { } note) Console.Error.WriteLine(note);

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) env[k] = v;
        if (plan.Term is { } term) env["TERM"] = term;

        if (!OperatingSystem.IsWindows())
        {
            string error = Pty.Pty.ExecReplace(plan.Argv[0], plan.Argv, env); // returns only on failure
            Console.Error.WriteLine($"erikterm ssh: {plan.Argv[0]}: {error}");
            return 127;
        }
        return RunChild(plan);
    }

    /// <summary>Windows has no exec: runs ssh as a child and hands back its exit code.</summary>
    private static int RunChild(Plan plan)
    {
        if (Integrations.ToolRunner.ProgramPath(plan.Argv[0]) is not { } program)
        {
            Console.Error.WriteLine($"erikterm ssh: {plan.Argv[0]}: not found");
            return 127;
        }
        var psi = new ProcessStartInfo(program) { UseShellExecute = false };
        for (int i = 1; i < plan.Argv.Count; i++) psi.ArgumentList.Add(plan.Argv[i]);
        if (plan.Term is { } t) psi.Environment["TERM"] = t;
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
