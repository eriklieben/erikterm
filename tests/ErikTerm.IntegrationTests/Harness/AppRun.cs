using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace ErikTerm.IntegrationTests.Harness;

/// <summary>What a scenario starts: the debug script, extra configuration, the directory and the arguments.</summary>
internal sealed record Scenario
{
    /// <summary>
    /// ERIKTERM_DEBUG_SCRIPT_FILE steps, one per line (docs/development/README.md#debugging). <c>{dir}</c> becomes the
    /// run's directory; <c>shot:NAME</c> and <c>wait-file:NAME</c> without a slash are files in it.
    /// </summary>
    public string Script { get; init; } = "";

    /// <summary>TOML on top of <see cref="AppRun.BaseConfig"/>: a top-level key here replaces the base's line.</summary>
    public string Config { get; init; } = "";

    /// <summary>The working directory erikterm starts in (default: an empty directory of the run).</summary>
    public string? Cwd { get; init; }

    /// <summary>Extra command-line arguments (after --config-file, --no-restore unless <see cref="Restore"/>, --new-process).</summary>
    public string[] Args { get; init; } = [];

    /// <summary>Extra environment variables for erikterm and what runs in its panes.</summary>
    public Dictionary<string, string> Env { get; init; } = [];

    /// <summary>Leave out --no-restore: the run may restore the session saved in its state directory.</summary>
    public bool Restore { get; init; }

    /// <summary>
    /// The run's directory, made beforehand with <see cref="AppRun.NewDirectory"/> (to put a repository in it, or to
    /// reuse an earlier run's state: a saved session). Default: a new one.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>Stand-in programs of the scenario's own (name → sh script), in place of the recording ones.</summary>
    public Dictionary<string, string> Fakes { get; init; } = [];

    /// <summary>Leave the directory when the run ends (a later run reuses it); the last run deletes it.</summary>
    public bool KeepDirectory { get; init; }

    /// <summary>
    /// A real window: SDL's own video driver for the platform instead of the offscreen one (with the software renderer
    /// still). Only for tests marked <see cref="DesktopAttribute"/>, which run where a window may open.
    /// </summary>
    public bool Desktop { get; init; }

    /// <summary>
    /// No default_prog in the base settings: the panes run the platform's own shell (Pty.DefaultShell: PowerShell on
    /// Windows, $SHELL as a login shell on macOS), with erikterm's shell integration.
    /// </summary>
    public bool SystemShell { get; init; }
}

/// <summary>
/// One erikterm process, off-screen and private: SDL's offscreen video driver and software renderer, no
/// DISPLAY or WAYLAND_DISPLAY, and its own HOME, XDG runtime, state, config, data and cache directories, so it never
/// shows a window, talks to an erikterm you are using or reads your configuration. Coverage: run the whole test
/// assembly under dotnet-coverage (scripts/integration-tests.sh); the erikterm processes it starts are measured too.
/// <para>
/// Windows is less private: erikterm takes %LOCALAPPDATA% and %APPDATA% from Windows itself, so its control socket,
/// session file (one per run: --class), crash logs and remembered choices go to the real ones of the user running the
/// tests (<see cref="StateDirectory"/>). The configuration is still the run's own (--config-file).
/// </para>
/// </summary>
internal sealed class AppRun : IAsyncDisposable
{
    /// <summary>
    /// Settings every scenario starts from: fixed size, nothing animated, nothing from the machine. The panes run sh
    /// (<see cref="Platform.Shell"/>: Git for Windows' on Windows).
    /// </summary>
    public static string BaseConfig => $"default_prog = [{Platform.Toml(Platform.Shell)}]\n" + BaseSettings;

    private const string BaseSettings = """
        font_size = 12
        line_height = 1.2
        window_padding = 8
        initial_cols = 100
        initial_rows = 26
        default_cursor_style = "SteadyBlock"
        cursor_blink_rate = 0
        cursor_animation = false
        animation_duration_ms = 0
        smooth_scrolling = false
        enable_tab_bar = true
        hide_tab_bar_if_only_one_tab = false
        git_status = false
        github_status = false
        restore_session = false
        single_instance = false
        os_notifications = "off"
        window_close_confirmation = "NeverPrompt"
        """;

    private static readonly string AppDirectory = Metadata("AppDirectory");
    private static readonly string StandInDirectory = Metadata("StandInDirectory");

    /// <summary>The checkout the tests were built from.</summary>
    public static string RepositoryRoot { get; } = Path.GetFullPath(Metadata("RepositoryRoot"));

    private static string Metadata(string key) =>
        typeof(AppRun).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;

    /// <summary>
    /// A feed: step with a reference scene's content file (bench/testing/scenes/content: C# escapes, one line per
    /// row), its lines joined with \r\n like scripts/reference-shots.sh does.
    /// </summary>
    public static string FeedFile(string name) => "feed:" + string.Join("\\r\\n",
        File.ReadAllLines(Path.Combine(RepositoryRoot, "bench", "testing", "scenes", "content", name)).Select(l => l.TrimEnd('\r')));

    /// <summary>erikterm cli list --format json: one element per pane.</summary>
    public async Task<System.Text.Json.JsonElement[]> PanesAsync()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await CliAsync("list", "--format", "json"));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    /// <summary>Polls the pane list until <paramref name="ready"/> holds; returns it.</summary>
    public async Task<System.Text.Json.JsonElement[]> WaitForPanesAsync(Func<System.Text.Json.JsonElement[], bool> ready, int timeoutMs = 20_000)
    {
        var sw = Stopwatch.StartNew();
        System.Text.Json.JsonElement[] panes = [];
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            panes = await PanesAsync();
            if (ready(panes)) return panes;
            await Task.Delay(150);
        }
        throw new TimeoutException($"the panes never got there; last:\n{string.Join("\n", panes.Select(p => p.ToString()))}\nlog:\n{Output}");
    }

    /// <summary>The erikterm executable (the apphost, so Environment.ProcessPath is erikterm itself: the tmux shim,
    /// mergetool and the other subcommands run it).</summary>
    public static string Executable => Path.GetFullPath(Path.Combine(AppDirectory, OperatingSystem.IsWindows() ? "erikterm.exe" : "erikterm"));

    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly Dictionary<string, string> _env;
    private readonly bool _ownsDirectory;

    /// <summary>The run's directory: home/, rt/ (XDG_RUNTIME_DIR), state/, config/, cwd/ and the script's files.</summary>
    public string Directory { get; }

    /// <summary>
    /// Where erikterm keeps its state (session, crash logs): the run's own state/erikterm, except on Windows. There
    /// erikterm asks Windows for %LOCALAPPDATA% (Environment.GetFolderPath, which doesn't read the variable), so it is
    /// the real one of the user running the tests, shared by every run: each run has a window class of its own
    /// (<see cref="WindowClass"/>), so its session file is too, and crash logs are told apart by their pid.
    /// </summary>
    public string StateDirectory => StateDirectoryOf(Directory);

    private static string StateDirectoryOf(string dir) => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm")
        : Path.Combine(dir, "state", "erikterm");

    /// <summary>Windows: the --class every run of this directory passes (etit-…, the directory's name); null elsewhere.</summary>
    private static string? WindowClassOf(string dir) => OperatingSystem.IsWindows() ? Path.GetFileName(dir) : null;

    /// <summary>The session file this run saves and restores (session.json; Windows: session-&lt;class&gt;.json).</summary>
    public static string SessionFileOf(string dir) =>
        Path.Combine(StateDirectoryOf(dir), WindowClassOf(dir) is { } cls ? $"session-{cls}.json" : "session.json");

    private AppRun(string directory, bool owns, Process process, Dictionary<string, string> env)
    {
        Directory = directory;
        _ownsDirectory = owns;
        _process = process;
        _env = env;
    }

    /// <summary>
    /// A short private directory: Unix socket paths may be at most 107 bytes (103 on macOS), so not $TMPDIR
    /// (<see cref="Platform.TempRoot"/>).
    /// </summary>
    public static string NewDirectory()
    {
        string dir = Path.Combine(Platform.TempRoot(), "etit-" + Guid.NewGuid().ToString("N")[..10]);
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(dir);
        else System.IO.Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return dir;
    }

    public static AppRun Start(Scenario scenario)
    {
        bool owns = !scenario.KeepDirectory;
        string dir = scenario.Directory ?? NewDirectory();
        string home = Path.Combine(dir, "home"), runtime = Path.Combine(dir, "rt"), cwd = scenario.Cwd ?? Path.Combine(dir, "cwd");
        foreach (var sub in new[] { home, Path.Combine(dir, "state"), Path.Combine(dir, "config"), Path.Combine(dir, "data"), Path.Combine(dir, "cache"), cwd })
            System.IO.Directory.CreateDirectory(sub);
        if (!OperatingSystem.IsWindows())
            System.IO.Directory.CreateDirectory(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string config = Path.Combine(dir, "erikterm.toml");
        File.WriteAllText(config, MergeConfig(scenario.SystemShell ? BaseSettings : BaseConfig, scenario.Config));
        string script = Path.Combine(dir, "script.txt");
        File.WriteAllText(script, ExpandScript(scenario.Script, dir));

        var env = PrivateEnvironment(dir);
        if (scenario.Desktop) env.Remove("SDL_VIDEODRIVER"); // SDL picks the platform's own (windows, cocoa)
        env["ERIKTERM_DEBUG_SCRIPT_FILE"] = script;
        InstallFakes(dir, env, scenario.Fakes);
        foreach (var (k, v) in scenario.Env) env[k] = v.Replace("{dir}", dir);

        var start = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--config-file");
        start.ArgumentList.Add(config);
        if (!scenario.Restore) start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("--new-process");
        if (WindowClassOf(dir) is { } cls)
        {
            start.ArgumentList.Add("--class");
            start.ArgumentList.Add(cls);
        }
        foreach (var a in scenario.Args) start.ArgumentList.Add(a.Replace("{dir}", dir));
        Apply(start, env);

        var process = new Process { StartInfo = start };
        var run = new AppRun(dir, owns, process, env);
        process.OutputDataReceived += (_, e) => run.Append(e.Data);
        process.ErrorDataReceived += (_, e) => run.Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return run;
    }

    /// <summary>
    /// The test process's environment without anything that points at a display or a running erikterm or tmux, plus
    /// the private directories and SDL's offscreen drivers.
    /// </summary>
    internal static Dictionary<string, string> PrivateEnvironment(string dir)
    {
        // Windows: names have no case (the variable is "Path" there; PATH must find it).
        var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var name in env.Keys.ToList())
            if (name.StartsWith("ERIKTERM_", StringComparison.Ordinal) || name.StartsWith("SDL_", StringComparison.Ordinal)
                || name is "DISPLAY" or "WAYLAND_DISPLAY" or "WAYLAND_SOCKET" or "TMUX" or "TMUX_PANE" or "TERM_PROGRAM"
                    or "HYPRLAND_INSTANCE_SIGNATURE" or "SWAYSOCK" or "DBUS_SESSION_BUS_ADDRESS" or "ENV" or "BASH_ENV")
                env.Remove(name);
        env["SDL_VIDEODRIVER"] = "offscreen";
        env["SDL_RENDER_DRIVER"] = "software";
        env["HOME"] = Path.Combine(dir, "home");
        env["XDG_RUNTIME_DIR"] = Path.Combine(dir, "rt");
        env["XDG_STATE_HOME"] = Path.Combine(dir, "state");
        env["XDG_CONFIG_HOME"] = Path.Combine(dir, "config");
        env["XDG_DATA_HOME"] = Path.Combine(dir, "data");
        env["XDG_CACHE_HOME"] = Path.Combine(dir, "cache");
        env["PS1"] = "$ ";
        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["GIT_AUTHOR_NAME"] = env["GIT_COMMITTER_NAME"] = "integration";
        env["GIT_AUTHOR_EMAIL"] = env["GIT_COMMITTER_EMAIL"] = "integration@example.invalid";
        if (OperatingSystem.IsWindows())
        {
            env["LOCALAPPDATA"] = Path.Combine(dir, "state");
            env["APPDATA"] = Path.Combine(dir, "config");
            env["USERPROFILE"] = Path.Combine(dir, "home");
        }
        // The apphost finds the runtime through DOTNET_ROOT; the test runs on the same one.
        if (!env.ContainsKey("DOTNET_ROOT"))
        {
            string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            env["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        }
        return env;
    }

    /// <summary>
    /// The programs erikterm hands things to (the link opener, desktop notifications, the editor, coding agents) are
    /// stand-ins first on PATH that only write their arguments to calls.log: nothing reaches the desktop, and a test
    /// can check what would have been opened. They are sh scripts; on Windows, where CreateProcess only starts an .exe,
    /// each also gets NAME.exe, a copy of the stand-in program (tests/ErikTerm.IntegrationTests.StandIn) that runs the
    /// script NAME with Git for Windows' sh (FAKE_SH), and the link opener is explorer.exe.
    /// </summary>
    private static void InstallFakes(string dir, Dictionary<string, string> env, Dictionary<string, string> own)
    {
        string bin = Path.Combine(dir, "bin");
        System.IO.Directory.CreateDirectory(bin);
        // Forward slashes on Windows: the scripts use these with MSYS2's sh, and erikterm splits $VISUAL like a shell.
        string log = Platform.ShPath(Path.Combine(dir, "calls.log"));
        const string Recorder = "#!/bin/sh\nprintf '%s' \"$(basename \"$0\")\" >> \"$FAKE_CALLS\"\nfor a in \"$@\"; do printf ' [%s]' \"$a\" >> \"$FAKE_CALLS\"; done\nprintf '\\n' >> \"$FAKE_CALLS\"\n";
        foreach (var name in new[] { "xdg-open", "open", "explorer", "notify-send", "gh", "editor", "code" })
            WriteExecutable(Path.Combine(bin, name), Recorder);
        // Coding agents: report a state with OSC 7501 like Claude Code does, print their arguments, wait.
        const string Agent = "#!/bin/sh\nprintf '%s' \"$(basename \"$0\")\" >> \"$FAKE_CALLS\"\nfor a in \"$@\"; do printf ' [%s]' \"$a\" >> \"$FAKE_CALLS\"; done\nprintf '\\n' >> \"$FAKE_CALLS\"\n" +
            "printf '\\033]7501;state=working:app=claude-code\\033\\\\'\nprintf 'fake agent in %s\\r\\n' \"$PWD\"\nexec sleep 600\n";
        foreach (var name in new[] { "claude", "codex", "copilot" })
            WriteExecutable(Path.Combine(bin, name), Agent);
        foreach (var (name, script) in own)
            WriteExecutable(Path.Combine(bin, name), script);
        string path = env.GetValueOrDefault("PATH", "/usr/bin:/bin");
        if (OperatingSystem.IsWindows())
        {
            InstallStandIns(bin);
            env["FAKE_SH"] = Platform.Shell;
            // The scripts' tools (cat, sed, sleep, base64 …) are MSYS2's, next to its sh; after Windows' own directories.
            path += Path.PathSeparator + Path.GetDirectoryName(Platform.Shell);
        }
        env["PATH"] = bin + Path.PathSeparator + path;
        env["FAKE_CALLS"] = log;
        env["VISUAL"] = Platform.ShPath(Path.Combine(bin, "editor"));
        env["EDITOR"] = Platform.ShPath(Path.Combine(bin, "editor"));
        env["BROWSER"] = Platform.ShPath(Path.Combine(bin, Platform.Opener));
    }

    /// <summary>Windows: NAME.exe for every script NAME in <paramref name="bin"/>, with the stand-in's assembly beside them.</summary>
    private static void InstallStandIns(string bin)
    {
        foreach (string file in System.IO.Directory.GetFiles(StandInDirectory, "standin.*"))
            if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(bin, Path.GetFileName(file)), overwrite: true);
        string exe = Path.Combine(StandInDirectory, "standin.exe");
        foreach (string script in System.IO.Directory.GetFiles(bin).Where(f => Path.GetExtension(f).Length == 0))
            File.Copy(exe, script + ".exe", overwrite: true);
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>What the stand-in programs were asked to do, one line per call: "xdg-open [https://...]".</summary>
    public string[] Calls
    {
        get
        {
            string log = Path.Combine(Directory, "calls.log");
            return File.Exists(log) ? File.ReadAllLines(log) : [];
        }
    }

    /// <summary>
    /// Writes a reference scene content file (bench/testing/scenes/content, C# escapes) as the raw bytes a program
    /// would print, into the run directory; cat it in the pane to send it through the pty like real output.
    /// </summary>
    public string WriteContent(string name)
    {
        string text = string.Join("\r\n", File.ReadAllLines(Path.Combine(RepositoryRoot, "bench", "testing", "scenes", "content", name)));
        string path = Path.Combine(Directory, name);
        File.WriteAllText(path, System.Text.RegularExpressions.Regex.Unescape(text.Replace("\\e", "\\x1b")) + "\r\n");
        return path;
    }

    /// <summary>Types a command line into the shell (erikterm cli send-text, as keys: no bracketed paste).</summary>
    public Task<string> TypeAsync(string line) => CliAsync("send-text", "--no-paste", line + "\r");

    private static void Apply(ProcessStartInfo start, Dictionary<string, string> env)
    {
        start.Environment.Clear();
        foreach (var (k, v) in env) start.Environment[k] = v;
    }

    /// <summary>
    /// The base settings with the scenario's on top: a top-level key the scenario sets replaces the base line (TOML
    /// allows a key only once). The scenario's tables ([colors] …) go at the end.
    /// </summary>
    internal static string MergeConfig(string baseConfig, string overrides)
    {
        static string? Key(string line)
        {
            int eq = line.IndexOf('=');
            return eq > 0 && !line.TrimStart().StartsWith('#') && !line.TrimStart().StartsWith('[') ? line[..eq].Trim() : null;
        }
        var lines = overrides.Replace("\r\n", "\n").Split('\n');
        int firstTable = Array.FindIndex(lines, l => l.TrimStart().StartsWith('['));
        var topLevel = firstTable < 0 ? lines : lines[..firstTable];
        var keys = topLevel.Select(Key).Where(k => k is not null).ToHashSet();
        var kept = baseConfig.Replace("\r\n", "\n").Split('\n').Where(l => Key(l) is not { } k || !keys.Contains(k));
        return string.Join("\n", kept.Concat(lines)) + "\n";
    }

    /// <summary>{dir} → the run directory; shot:NAME / wait-file:NAME without a directory → files in it.</summary>
    internal static string ExpandScript(string script, string dir)
    {
        var sb = new StringBuilder();
        foreach (var raw in script.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            line = line.Replace("{dir}", dir);
            foreach (var kind in new[] { "shot:", "wait-file:" })
                if (line.StartsWith(kind, StringComparison.Ordinal) && !line.Contains('/') && !line.Contains('\\'))
                    line = kind + Path.Combine(dir, line[kind.Length..]);
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (_output) _output.AppendLine(line);
    }

    /// <summary>What erikterm wrote to stdout and stderr so far.</summary>
    public string Output { get { lock (_output) return _output.ToString(); } }

    public int ProcessId => _process.Id;

    public string PathOf(string name) => Path.Combine(Directory, name);

    /// <summary>
    /// The control socket of this instance (erikterm cli --socket): erikterm-PID.sock in the directory IpcPaths picks,
    /// $XDG_RUNTIME_DIR/erikterm on Linux (the run's own); the per-user temp or local app data directory elsewhere.
    /// </summary>
    public string Socket
    {
        get
        {
            string name = $"erikterm-{_process.Id}.sock";
            string[] dirs = OperatingSystem.IsLinux() ? [Path.Combine(Directory, "rt", "erikterm")]
                : OperatingSystem.IsWindows() ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "erikterm")]
                : [Path.Combine(Path.GetTempPath(), "erikterm"), "/tmp/erikterm-" + Platform.MacEffectiveUid];
            foreach (var d in dirs)
                if (File.Exists(Path.Combine(d, name))) return Path.Combine(d, name);
            return Path.Combine(dirs[0], name);
        }
    }

    /// <summary>Waits for a file the script writes (a shot:) to exist and stop growing.</summary>
    public async Task<string> WaitForFileAsync(string name, int timeoutMs = 60_000)
    {
        string path = Path.IsPathRooted(name) ? name : Path.Combine(Directory, name);
        var sw = Stopwatch.StartNew();
        long last = -1;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path))
            {
                long len = new FileInfo(path).Length;
                if (len > 0 && len == last) return path;
                last = len;
            }
            else if (_process.HasExited)
                throw new InvalidOperationException($"erikterm exited ({_process.ExitCode}) before writing {name}:\n{Output}");
            await Task.Delay(100);
        }
        throw new TimeoutException($"no {name} after {timeoutMs} ms:\n{Output}");
    }

    /// <summary>Lets a script waiting at wait-file:NAME go on.</summary>
    public void Release(string name) => File.WriteAllText(Path.Combine(Directory, name), "");

    /// <summary>Waits until the control socket is up (the first window has opened).</summary>
    public async Task WaitForSocketAsync(int timeoutMs = 60_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(Socket)) return;
            if (_process.HasExited) throw new InvalidOperationException($"erikterm exited ({_process.ExitCode}):\n{Output}");
            await Task.Delay(100);
        }
        throw new TimeoutException($"no control socket after {timeoutMs} ms:\n{Output}");
    }

    /// <summary>Runs erikterm cli against this instance: its exit code, stdout and stderr.</summary>
    public async Task<(int Code, string Stdout, string Stderr)> CliRawAsync(string[] args, string? stdin = null)
    {
        await WaitForSocketAsync();
        var start = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = Directory,
        };
        Apply(start, _env);
        start.Environment.Remove("ERIKTERM_DEBUG_SCRIPT_FILE");
        start.ArgumentList.Add("cli");
        start.ArgumentList.Add("--socket");
        start.ArgumentList.Add(Socket);
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        if (stdin is not null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(30_000);
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await stdout, await stderr);
    }

    public async Task<string> CliAsync(params string[] args)
    {
        var (code, stdout, stderr) = await CliRawAsync(args);
        if (code != 0) throw new InvalidOperationException($"erikterm cli {string.Join(' ', args)} exited {code}: {stderr}");
        return stdout;
    }

    /// <summary>Polls get-text until it contains <paramref name="expected"/>; returns the last text.</summary>
    public async Task<string> WaitForTextAsync(string expected, string[]? extra = null, int timeoutMs = 20_000)
    {
        var sw = Stopwatch.StartNew();
        string text = "";
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            text = await CliAsync(["get-text", .. extra ?? []]);
            if (text.Contains(expected, StringComparison.Ordinal)) return text;
            await Task.Delay(150);
        }
        throw new TimeoutException($"pane text never contained \"{expected}\"; last:\n{text}\nlog:\n{Output}");
    }

    /// <summary>Waits for erikterm to exit by itself; returns the exit code.</summary>
    public async Task<int> WaitForExitAsync(int timeoutMs = 60_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await _process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { throw new TimeoutException($"erikterm still running after {timeoutMs} ms:\n{Output}"); }
        _process.WaitForExit(); // the output readers finish
        return _process.ExitCode;
    }

    /// <summary>
    /// Crash logs (internal errors) this erikterm process wrote so far: there should be none. Told apart by the "pid:"
    /// line they carry, since on Windows every run writes them to the same directory (<see cref="StateDirectory"/>).
    /// </summary>
    public string[] CrashLogs => System.IO.Directory.Exists(StateDirectory)
        ? System.IO.Directory.GetFiles(StateDirectory, "crash-*.log").Select(File.ReadAllText)
            .Where(text => text.Contains($"\npid: {_process.Id}\n", StringComparison.Ordinal)).ToArray()
        : [];

    /// <summary>Writes a script (sh or other) for the pane to run, with \n line ends whatever the checkout has.</summary>
    public string WriteScript(string name, string content)
    {
        string path = Path.Combine(Directory, name);
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return path;
    }

    /// <summary>
    /// Lines of the output that report a problem: exceptions, internal errors, failed script steps, and diagnostics
    /// for the scenario's config file (a mistake there would quietly run the scenario on other settings).
    /// </summary>
    public string[] ProblemLines => Output.Split('\n')
        .Where(l => l.Contains("Exception", StringComparison.Ordinal) || l.Contains("Internal error", StringComparison.OrdinalIgnoreCase)
            || l.Contains("Unhandled", StringComparison.Ordinal) || l.Contains("script: ", StringComparison.Ordinal)
            || l.Contains("erikterm.toml:", StringComparison.Ordinal) || l.Contains("config not reloaded", StringComparison.Ordinal))
        .ToArray();

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) { /* never started */ }
        _process.Dispose();
        if (_ownsDirectory && Environment.GetEnvironmentVariable("ERIKTERM_IT_KEEP") != "1")
        {
            // Windows: the run's session file is in the shared state directory, not in the run's.
            if (OperatingSystem.IsWindows())
                try { File.Delete(SessionFileOf(Directory)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* left for the next cleanup */ }
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { /* a pane's program still holds a file: the OS temp cleanup gets it */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
