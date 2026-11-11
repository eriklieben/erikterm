using ErikTerm.App.Native;
using ErikTerm.Config;

namespace ErikTerm.App;

internal static class Program
{
    private const string Usage = """
        usage: erikterm [--config-file PATH] [--set KEY=VALUE]... [--class NAME] [--no-restore] [--new-process] [-e PROGRAM [ARGS...]]
               erikterm cli <subcommand> [options]     (erikterm cli --help)
               erikterm ssh [ssh options] destination  (ssh with TERM=xterm-erikterm; installs the entry there first)
               erikterm tmux-shim <tmux arguments>     (what `tmux` runs in a pane with tmux_emulation)
               erikterm mergetool [LOCAL BASE REMOTE MERGED]  (resolve merge conflicts in the terminal)
               erikterm compare <a> <b>                (compare two files in the terminal; a side may be main:src/a.ts)
               erikterm review [--base BRANCH]         (review this branch like a pull request, in the terminal)
               erikterm settings                       (every setting explained; changes save at once)

          --config-file PATH   settings file (default: ~/.config/erikterm/erikterm.toml)
          --set KEY=VALUE      override a setting, as a TOML value (repeatable; also --config KEY=VALUE).
                               Words that aren't TOML values are strings: --set color_scheme=Tokyo Night
                               Dotted keys reach into tables: --set colors.split='"#b7bdf8"'
          --class NAME         window class / Wayland app id (e.g. for a Hyprland dropdown rule)
          --no-restore         start fresh instead of restoring the last session
          --new-process        don't open the window in a running erikterm (single_instance)
          -e PROGRAM ...       run PROGRAM instead of default_prog / $SHELL (no session restore)
          -V, --version        print the version and commit
          --terminfo           print the xterm-erikterm terminfo source (for tic -x; see scripts/install-terminfo.sh)
        """;

    private static int Main(string[] args)
    {
        // What this process is (EntryCommand.cs keeps the order testable: the tmux shim before --version, so that
        // `tmux -V` answers with a tmux version).
        // The spawn helper is checked by argument alone: nothing else may run before it (see below).
        bool spawnHelper = args is [var first, ..] && first == Pty.Pty.SpawnHelperArgument;
        bool invokedAsTmux = !spawnHelper && Tmux.TmuxShim.InvokedAsTmux();
        var entry = EntryCommand.Classify(args, invokedAsTmux, Pty.Pty.SpawnHelperArgument);
        // macOS: a pane's child starts as erikterm itself, which takes the pty as its controlling tty and becomes the
        // program (ErikTerm.Pty/UnixPty.Mac.cs). Nothing else may run first: this process is about to be replaced.
        if (entry == EntryKind.SpawnHelper) return Pty.Pty.RunSpawnHelper(args.AsSpan(1));
        if (OperatingSystem.IsMacOS() && Environment.ProcessPath is { } self) Pty.Pty.UseSpawnHelper(self);
        // tmux emulation: run as `tmux` (the pane's shim symlink, a hard-linked tmux.exe on Windows) or
        // `erikterm tmux-shim ...` (Tmux/TmuxShim.cs). Decided before the console is touched, since the shim is a
        // short-lived command-line program like `cli`, not the window.
        WindowsConsole.AttachToParent(args, entry is EntryKind.TmuxShim or EntryKind.Ssh or EntryKind.Devcontainer or EntryKind.MergeTool or EntryKind.Compare or EntryKind.Review or EntryKind.Settings); // Windows: GUI-subsystem exe; reuse the launching console
        if (RunWithoutWindow(entry, args) is { } exitCode) return exitCode;

        NativeLibraries.Register();
        // The Wayland app id / X11 class: matches contrib/linux/erikterm.desktop (dock icon, StartupWMClass), whatever
        // the binary is called. --class replaces it; App.WindowClass stays null so sessions and IPC are unaffected.
        Sdl.SetHint("SDL_APP_ID", "erikterm");
        var overrides = new List<ConfigOverride>();
        if (ParseWindowArguments(args, overrides, out string? configFile, out string[]? command) is { } argumentExit) return argumentExit;

        // Any thread: keep the stack trace (CrashLog.cs). App.Run saves the session on the way out of a fatal error.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("unhandled exception", e.ExceptionObject as Exception);
        try
        {
            return new App(Path.GetFullPath(SettingsLoader.DefaultPath(configFile)), command, overrides).Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"erikterm: {ex.Message}");
            if (CrashLog.Write("fatal error", ex) is { } log) Console.Error.WriteLine($"erikterm: details in {log}");
            return 1;
        }
    }

    /// <summary>The commands that never open a window. Null: this process is the window.</summary>
    private static int? RunWithoutWindow(EntryKind entry, string[] args)
    {
        switch (entry)
        {
            case EntryKind.TmuxShim: return Tmux.TmuxShim.RunMain(args is ["tmux-shim", .. var rest] ? rest : args);
            case EntryKind.Version: Console.WriteLine(Ui.AboutProbe.VersionText()); return 0;
            case EntryKind.Terminfo: Console.Out.Write(Core.TermCapabilities.TerminfoSource()); return 0;
            case EntryKind.Cli: return Cli.CliMain.Run(args[1..]); // the control client: no SDL, no window
            case EntryKind.Ssh: return Cli.SshCommand.Run(args[1..]); // ssh with the terminfo entry on the remote
            case EntryKind.Devcontainer: return Cli.DevcontainerCommand.Run(args[1..]); // a shell in a dev container, set up the same way
            case EntryKind.MergeTool: return Compare.MergeTool.Start(args[1..]); // the conflict resolver: no SDL, no window
            case EntryKind.Compare: return Compare.CompareTool.Start(args[1..]); // side-by-side compare, the same way
            case EntryKind.Review: return Compare.ReviewTool.Start(args[1..]); // a local pull request of this branch, the same way
            case EntryKind.Settings: return SettingsPage.SettingsTool.Start(args[1..]); // the settings page, the same way
            default: return null;
        }
    }

    /// <summary>The window's command line. Null when the window may open; otherwise the exit code to stop with.</summary>
    private static int? ParseWindowArguments(string[] args, List<ConfigOverride> overrides, out string? configFile, out string[]? command)
    {
        configFile = null;
        command = null;
        int i = 0;
        while (i < args.Length)
        {
            string arg = args[i++];
            switch (arg)
            {
                case "--config-file" when i < args.Length: configFile = args[i++]; break;
                case "--set" or "--config" when i < args.Length:
                    if (!ConfigOverrides.TryParse(args[i++], out var o, out string error))
                    {
                        Console.Error.WriteLine($"erikterm: {arg}: {error}");
                        return 2;
                    }
                    overrides.Add(o!);
                    break;
                case "--class" when i < args.Length: App.WindowClass = args[i++]; Sdl.SetHint("SDL_APP_ID", App.WindowClass); break;
                case "--no-restore": App.NoRestore = true; break;
                case "--new-process": App.NewProcess = true; break;
                case "-e" when i < args.Length: command = args[i..]; i = args.Length; break;
                case "-h" or "--help": Console.WriteLine(Usage); return 0;
                default: Console.Error.WriteLine($"erikterm: unexpected argument '{arg}'\n{Usage}"); return 2;
            }
        }
        return null;
    }
}
