using System.Diagnostics;
using ErikTerm.App.Compare;
using ErikTerm.App.Integrations;
using ErikTerm.Config;
using ErikTerm.Ui.Compare;

namespace ErikTerm.App.SettingsPage;

/// <summary>
/// <c>erikterm settings [--config-file PATH]</c>: the settings page in the terminal. erikterm opens it in a split
/// (Ctrl+, or the palette) in a tab of its own; the open windows apply each change as it is saved.
/// </summary>
internal static class SettingsTool
{
    public const string Usage = """
        usage: erikterm settings [--config-file PATH]
          every setting with what it does; changes are saved to the config file at once
        """;

    public static int Start(string[] args)
    {
        if (args is ["-h" or "--help"]) { Console.WriteLine(Usage); return 0; }
        string? explicitPath = null;
        if (args is ["--config-file", var given]) explicitPath = given;
        else if (args.Length > 0)
        {
            Console.Error.WriteLine($"erikterm settings: unexpected arguments\n{Usage}");
            return 2;
        }
        string path = SettingsLoader.DefaultPath(explicitPath);
        return TuiHost.Run(console => Run(console, path, console), "settings");
    }

    public static int Run(ITuiTerminal terminal, string path, TuiConsole? console = null)
    {
        var loop = new TuiLoop(terminal);
        console?.CatchSignals(loop.Stop);
        bool inErikterm = Environment.GetEnvironmentVariable("ERIKTERM_PANE") is { Length: > 0 };
        var screen = new SettingsScreen(new FileHost(path, terminal, loop))
        {
            CardColor = inErikterm ? rgb => terminal.Write(CardOn(rgb)) : null,
        };
        loop.Attach(screen);
        try { loop.Run(); }
        finally
        {
            terminal.LeaveFullScreen();
            if (inErikterm) terminal.Write(CardOff);
        }
        int signal = console?.StopSignal ?? 0;
        return signal != 0 ? signal : screen.ExitCode;
    }

    /// <summary>
    /// The pane's background becomes the page's outer colour (OSC 11) and erikterm draws it as a card with rounded
    /// corners (PaneRenderer.CardVariable), as see-through as the window.
    /// </summary>
    private static byte[] CardOn(uint rgb) => System.Text.Encoding.ASCII.GetBytes(
        $"\e]1337;SetUserVar=erikterm_card={Convert.ToBase64String("1"u8)}\a\e]11;#{rgb & 0xFFFFFF:x6}\e\\");

    /// <summary>Back to the pane's own background, for when the page ran in a pane that stays open.</summary>
    private static ReadOnlySpan<byte> CardOff => "\e]111\e\\\e]1337;SetUserVar=erikterm_card=\a"u8;

    /// <summary>The real file, checked with erikterm's own loader, and the user's editor in this terminal.</summary>
    private sealed class FileHost(string path, ITuiTerminal terminal, TuiLoop loop) : ISettingsHost
    {
        private readonly IReadOnlyList<string> _schemeDirectories = ColorSchemeFiles.DefaultDirectories(path);
        private IReadOnlyList<ColorScheme>? _schemes;

        public string DisplayPath => HomeRelative(path);

        public string Read()
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
        }

        /// <summary>
        /// Writes in place, so a symlinked config (dotfiles) stays a link and the windows' file watch sees one
        /// change. A new file is created without following a planted link.
        /// </summary>
        public string? Write(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var f = new FileStream(path, File.Exists(path) ? FileMode.Truncate : FileMode.CreateNew, FileAccess.Write);
                f.Write(System.Text.Encoding.UTF8.GetBytes(text));
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Couldn't save {Path.GetFileName(path)}: {ex.Message}";
            }
        }

        public IReadOnlyList<string> Problems(string text) =>
            SettingsLoader.Parse(text, path, new LoadOptions { Schemes = Schemes() }).Diagnostics;

        public IReadOnlyList<string> SchemeNames() => [.. ColorSchemes.WithCustom(Schemes()).Select(s => s.Name)];

        private IReadOnlyList<ColorScheme> Schemes() =>
            _schemes ??= ColorSchemeFiles.LoadDirectories(_schemeDirectories, []);

        /// <summary>The editor runs in this terminal: the page steps aside, then draws itself again.</summary>
        public void EditFile(int line)
        {
            if (!File.Exists(path) && Write("# erikterm configuration — changes apply when you save.\n") is not null) return;
            var argv = EditorCommands.EditorArgv(EditorCommands.PreferredEditor(Environment.GetEnvironmentVariable), path, Math.Max(0, line), 0);
            if (line <= 0) argv = EditorCommands.EditFileArgv(EditorCommands.PreferredEditor(Environment.GetEnvironmentVariable), path,
                OperatingSystem.IsWindows(), name => ToolRunner.Find(name) is not null || File.Exists(name)) ?? argv;
            terminal.LeaveFullScreen();
            try
            {
                var start = new ProcessStartInfo(ToolRunner.Find(argv[0]) ?? argv[0]) { UseShellExecute = false };
                foreach (string arg in argv.Skip(1)) start.ArgumentList.Add(arg);
                using var editor = Process.Start(start);
                editor?.WaitForExit();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine($"erikterm settings: couldn't start {argv[0]}: {ex.Message}");
            }
            finally
            {
                terminal.EnterFullScreen();
                terminal.Write("\e[?2004h"u8); // bracketed paste again: leaving full screen turned it off
                loop.Invalidate();
            }
        }

        private static string HomeRelative(string file)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string full = Path.GetFullPath(file);
            return home.Length > 0 && full.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + full[home.Length..] : full;
        }
    }
}
