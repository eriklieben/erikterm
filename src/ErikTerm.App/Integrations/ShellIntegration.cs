using System.Security.Cryptography;
using ErikTerm.App.Ipc;

namespace ErikTerm.App.Integrations;

/// <summary>
/// Automatic shell integration (shell_integration = "auto"): when erikterm starts a zsh, bash or fish itself, the scripts
/// in contrib/shell (embedded in the binary, written once to a private directory) are injected without touching the
/// user's dotfiles, the way kitty and ghostty do it. Each shell gets a way in that leaves its startup otherwise untouched:
/// a <c>ZDOTDIR</c> whose .zshenv puts it back (zsh), <c>--posix</c> and <c>ENV</c> (bash), an <c>XDG_DATA_DIRS</c>
/// entry with a vendor conf.d file (fish), <c>-NoExit -Command</c> dot-sourcing the script after the profile
/// (PowerShell, also on Windows, where it is the only one). See contrib/shell/inject. Anything unexpected leaves the
/// shell to start normally.
/// </summary>
internal static class ShellIntegration
{
    internal const string OrigZdotdirVariable = "ERIKTERM_ORIG_ZDOTDIR";
    internal const string OrigEnvVariable = "ERIKTERM_ORIG_ENV";
    internal const string OrigXdgDataDirsVariable = "ERIKTERM_ORIG_XDG_DATA_DIRS";
    internal const string UnexportHistfileVariable = "ERIKTERM_BASH_UNEXPORT_HISTFILE";

    internal enum ShellKind { None, Zsh, Bash, Fish, PowerShell }

    /// <summary>The embedded files: resource name and path under the integration directory.</summary>
    internal static readonly (string Resource, string Path)[] Files =
    [
        ("shell/erikterm.zsh", "erikterm.zsh"),
        ("shell/erikterm.bash", "erikterm.bash"),
        ("shell/erikterm.fish", "erikterm.fish"),
        ("shell/erikterm.ps1", "erikterm.ps1"),
        ("shell/inject/zsh/.zshenv", "zsh/.zshenv"),
        ("shell/inject/bash/erikterm-inject.bash", "bash/erikterm-inject.bash"),
        ("shell/inject/fish/vendor_conf.d/00-erikterm.fish", "fish-data/fish/vendor_conf.d/00-erikterm.fish"),
    ];

    // Paths relative to the integration directory, as named in the shells' startup (bash's and zsh's are fixed by the scripts' layout).
    private const string ZshDir = "zsh";
    private const string BashInjectFile = "bash/erikterm-inject.bash";
    private const string FishDataDir = "fish-data";
    private const string PowerShellScript = "erikterm.ps1";

    private const string BaseDirName = "shell-integration";

    /// <summary>
    /// Hook from the pane spawn paths, for the shell erikterm starts itself: the argv to run, after adding the injection
    /// to <paramref name="env"/>; <paramref name="argv"/> unchanged when this isn't a plain interactive recognised shell
    /// (on Windows only PowerShell is: no script for cmd, and a zsh or bash there is MSYS's), or when the scripts can't be
    /// written.
    /// </summary>
    public static string[] Apply(string[] argv, Dictionary<string, string> env)
    {
        var kind = Recognize(argv);
        if (kind == ShellKind.None || (!UnixFiles.Supported && kind != ShellKind.PowerShell)) return argv;
        if (!(kind == ShellKind.PowerShell ? IsPlainInteractivePowerShell(argv) : IsPlainInteractive(argv))) return argv;
        string? root = PrepareCached(env);
        return root is null ? argv : Inject(kind, argv, env, root);
    }

    /// <summary>Which shell <paramref name="argv"/> starts, going by argv[0]'s file name.</summary>
    internal static ShellKind Recognize(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0 || string.IsNullOrEmpty(argv[0])) return ShellKind.None;
        string name = Path.GetFileName(argv[0]);
        return name switch
        {
            "zsh" => ShellKind.Zsh,
            "bash" => ShellKind.Bash,
            "fish" => ShellKind.Fish,
            _ when name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("powershell", StringComparison.OrdinalIgnoreCase) || name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
                => ShellKind.PowerShell,
            _ => ShellKind.None,
        };
    }

    /// <summary>
    /// PowerShell's flags are words, any case, and may be abbreviated: only <c>-Login</c>/<c>-l</c>, <c>-NoLogo</c> and
    /// <c>-Interactive</c> count as plain. -Command, -File, -NoProfile, -NoExit and the rest ask for something specific.
    /// </summary>
    internal static bool IsPlainInteractivePowerShell(IReadOnlyList<string> argv)
    {
        for (int i = 1; i < argv.Count; i++)
        {
            string arg = argv[i];
            if (arg.Length < 2 || arg[0] != '-') return false;
            string word = arg[1..];
            if (word.Equals("l", StringComparison.OrdinalIgnoreCase)) continue;
            if (!(IsPrefixOf(word, "login", 2) || IsPrefixOf(word, "nologo", 3) || IsPrefixOf(word, "interactive", 1))) return false;
        }
        return true;
    }

    /// <summary><paramref name="word"/> is <paramref name="full"/> or an abbreviation of it at least <paramref name="min"/> long.</summary>
    private static bool IsPrefixOf(string word, string full, int min) =>
        word.Length >= min && word.Length <= full.Length && full.StartsWith(word, StringComparison.OrdinalIgnoreCase);

    /// <summary>The PowerShell code that loads the script: a single-quoted path, and an error in it never stops the shell.</summary>
    internal static string PowerShellLoadCommand(string script) =>
        "try { . '" + script.Replace("'", "''") + "' } catch {}";

    /// <summary>
    /// Only login and interactive flags: anything else (-c, a script, --norc, --rcfile, -f …) asks for something specific
    /// and is left alone. (argv[0] is the program.)
    /// </summary>
    internal static bool IsPlainInteractive(IReadOnlyList<string> argv)
    {
        for (int i = 1; i < argv.Count; i++)
        {
            string arg = argv[i];
            if (arg is "--login" or "--interactive") continue;
            if (arg.Length >= 2 && arg[0] == '-' && arg[1] != '-' && arg.AsSpan(1).IndexOfAnyExcept('l', 'i') < 0) continue;
            return false;
        }
        return true;
    }

    /// <summary>Adds the injection for <paramref name="kind"/> to <paramref name="env"/>; returns the argv to run.</summary>
    internal static string[] Inject(ShellKind kind, string[] argv, IDictionary<string, string> env, string root)
    {
        switch (kind)
        {
            case ShellKind.Zsh:
                if (env.TryGetValue("ZDOTDIR", out var zdotdir)) env[OrigZdotdirVariable] = zdotdir;
                env["ZDOTDIR"] = Path.Combine(root, ZshDir);
                return argv;
            case ShellKind.Bash:
                if (env.TryGetValue("ENV", out var envFile)) env[OrigEnvVariable] = envFile;
                env["ENV"] = Path.Combine(root, BashInjectFile);
                // In posix mode bash would default to ~/.sh_history.
                if (!env.ContainsKey("HISTFILE") && env.TryGetValue("HOME", out var home) && home.Length > 0)
                {
                    env["HISTFILE"] = Path.Combine(home, ".bash_history");
                    env[UnexportHistfileVariable] = "1";
                }
                return [argv[0], "--posix", .. argv[1..]];
            case ShellKind.Fish:
                bool had = env.TryGetValue("XDG_DATA_DIRS", out var dataDirs) && dataDirs.Length > 0;
                if (had) env[OrigXdgDataDirsVariable] = dataDirs!;
                // Without the variable fish looks in the defaults; our entry mustn't hide those.
                env["XDG_DATA_DIRS"] = Path.Combine(root, FishDataDir) + ":" + (had ? dataDirs : "/usr/local/share:/usr/share");
                return argv;
            case ShellKind.PowerShell:
                // After the profile, so the prompt it sets up is the one wrapped; -Command must come last.
                return [.. argv, "-NoExit", "-Command", PowerShellLoadCommand(Path.Combine(root, PowerShellScript))];
            default:
                return argv;
        }
    }

    private static string? _prepared;
    private static readonly Lock PrepareLock = new();

    /// <summary><see cref="Prepare"/> for this process' state directory, written once and then only checked for still being there.</summary>
    internal static string? PrepareCached(IReadOnlyDictionary<string, string>? env = null)
    {
        lock (PrepareLock)
        {
            if (_prepared is { } root && File.Exists(Path.Combine(root, Files[0].Path))) return root;
            return _prepared = Prepare(StateDirectory(env));
        }
    }

    /// <summary>The per-version integration directory under <paramref name="stateDir"/>; null when it can't be made private or written.</summary>
    internal static string? Prepare(string stateDir)
    {
        try
        {
            var contents = new byte[Files.Length][];
            for (int i = 0; i < Files.Length; i++)
                contents[i] = ReadResource(Files[i].Resource) ?? throw new FileNotFoundException(Files[i].Resource);

            string baseDir = Path.Combine(stateDir, BaseDirName);
            string root = Path.Combine(baseDir, VersionTag(contents));
            EnsurePrivateDirectory(baseDir);
            EnsurePrivateDirectory(root);
            for (int i = 0; i < Files.Length; i++)
            {
                string target = Path.Combine(root, Files[i].Path);
                for (string? d = Path.GetDirectoryName(target); d is not null && d.Length > root.Length; d = Path.GetDirectoryName(d))
                    EnsurePrivateDirectory(d);
                if (!IsCurrent(target, contents[i])) WriteAtomically(target, contents[i]);
            }
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null; // never in the way of starting a shell
        }
    }

    private static string StateDirectory(IReadOnlyDictionary<string, string>? env)
    {
        if (OperatingSystem.IsWindows()) // as the session file: %LOCALAPPDATA%\erikterm
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm");
        string? fromEnv = env is not null && env.TryGetValue("XDG_STATE_HOME", out var v) ? v : Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        string state = fromEnv is { Length: > 0 } x && Path.IsPathRooted(x)
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(state, "erikterm");
    }

    /// <summary>
    /// The scripts with their paths under the integration directory, for a copy somewhere else (inside a container:
    /// Cli/DevcontainerCommand.cs). Null when one of them isn't embedded.
    /// </summary>
    internal static List<(string Path, byte[] Content)>? Payload()
    {
        var list = new List<(string Path, byte[] Content)>(Files.Length);
        foreach (var (resource, path) in Files)
        {
            if (ReadResource(resource) is not { } content) return null;
            list.Add((path, content));
        }
        return list;
    }

    /// <summary>A hash of the scripts: another version of erikterm uses another directory, and old ones stay valid for running shells.</summary>
    private static string VersionTag(byte[][] contents)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var c in contents) hash.AppendData(c);
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }

    /// <summary>Creates <paramref name="dir"/> with mode 0700 and insists that it is ours: scripts in it are run by every new shell.</summary>
    private static void EnsurePrivateDirectory(string dir)
    {
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows()) return; // %LOCALAPPDATA% is already private to the user
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (UnixFiles.LStat(dir) is not { } info || info.Type != UnixFiles.S_IFDIR || info.Uid != UnixFiles.EffectiveUid || (info.Permissions & 0x3F) != 0)
            throw new InvalidOperationException("not a private directory: " + dir);
    }

    private static bool IsCurrent(string path, byte[] content)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == content.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(content);
    }

    private static void WriteAtomically(string target, byte[] content)
    {
        string tmp = target + "." + Environment.ProcessId + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var f = new FileStream(tmp, options)) f.Write(content);
        File.Move(tmp, target, overwrite: true);
    }

    private static byte[]? ReadResource(string name)
    {
        using var stream = typeof(ShellIntegration).Assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
