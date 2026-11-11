using ErikTerm.App.Integrations;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Session;
using ErikTerm.App.Ui;
using ErikTerm.Pty;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>What <see cref="WindowRecent"/> needs beyond the feature window: the process's history (App.Recent.cs).</summary>
internal interface IRecentWindow : IFeatureWindow
{
    /// <summary>The history, or null with remember_commands off.</summary>
    RecentHistory? Recent { get; }
    /// <summary>Deletes the saved history; false when the file couldn't be deleted.</summary>
    bool ForgetRecent();
}

/// <summary>
/// Recent commands and directories in the palette (remember_commands): "Run again › cmd" types it into the focused pane
/// through the paste path (sanitized, bracketed) without pressing Enter — Shift+Enter runs it too; "Go to › dir" sends a
/// <c>cd</c> quoted for the pane's shell and presses Enter, or opens a new tab there with Alt+Enter (also when something
/// else than a shell is running in the pane). What is remembered and how: Session/RecentHistory.cs. The walk over every
/// pane that feeds it also finds the long commands that finished out of sight (<see cref="CollectFinishedCommands"/>).
/// </summary>
internal sealed class WindowRecent(IRecentWindow window, IPaneLauncher launcher, IPaneInput input, Lock gate)
{
    private const int InlineRecentCommands = 30, InlineRecentDirectories = 20;

    private readonly List<(string Text, bool Private, string? CwdUri)> _recentCommands = [];
    private readonly List<string> _recentDirectories = [];

    /// <summary>
    /// Hook from WindowNotifications: every pane's commands that finished since the last look, long ones into
    /// <paramref name="into"/>; on the way, the directories and commands for the history.
    /// </summary>
    public void CollectFinishedCommands(List<FinishedCommandNotice> into, long now)
    {
        double threshold = window.Settings.NotifyCommandDuration;
        window.SyncActiveTab();
        var tabs = window.Tabs;
        lock (gate)
        {
            for (int t = 0; t < tabs.Count; t++)
            {
                var tab = tabs.Items[t];
                if (tab.Layout is null) continue;
                foreach (var pane in tab.Layout.LeafList)
                {
                    CollectRecentDirectory(pane);
                    long finished = pane.Term.CommandsFinished;
                    if (finished == pane.SeenCommandsFinished) continue;
                    CollectRecentCommands(pane, pane.SeenCommandsFinished, finished);
                    pane.SeenCommandsFinished = finished;
                    var command = pane.Term.LastFinishedCommand;
                    if (!CommandNotice.ShouldNotify(command.DurationMs, threshold)) continue;
                    into.Add(new FinishedCommandNotice(t, pane.Id, command, pane.Term.CommandText(command)));
                }
            }
        }
        FlushRecent(now);
    }

    /// <summary>With the terminal locked: what this pane has reported since the last look.</summary>
    private void CollectRecentDirectory(Pane pane)
    {
        string? uri = pane.Term.WorkingDirectoryUri;
        if (ReferenceEquals(uri, pane.SeenCwdUri)) return;
        pane.SeenCwdUri = uri;
        if (uri is not null && window.Recent is not null) _recentDirectories.Add(uri);
    }

    /// <summary>With the terminal locked: the commands that finished since the last look, oldest first.</summary>
    private void CollectRecentCommands(Pane pane, long seen, long finished)
    {
        if (window.Recent is null) return;
        string? uri = pane.Term.WorkingDirectoryUri;
        for (long i = seen; i < finished; i++)
            if (pane.Term.TryGetFinishedCommand(i, out var command))
                _recentCommands.Add((pane.Term.CommandText(command), command.Private || command.MultiLine, uri));
    }

    /// <summary>Outside the lock: into the history.</summary>
    private void FlushRecent(long now)
    {
        if (_recentCommands.Count == 0 && _recentDirectories.Count == 0) return;
        if (window.Recent is { } recent)
        {
            long unix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var (text, isPrivate, uri) in _recentCommands)
                recent.AddCommand(text, PaneCwd.FromOsc7(uri), unix, isPrivate, now);
            foreach (string uri in _recentDirectories)
                if (PaneCwd.FromOsc7(uri) is { } path && Directory.Exists(path)) recent.AddDirectory(path, unix, now);
        }
        _recentCommands.Clear();
        _recentDirectories.Clear();
    }

    /// <summary>A path for display: ~ for the home directory, shortened in the middle when long.</summary>
    public static string ShortPath(string path) =>
        FileRefResolver.ShortDisplay(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static readonly (string[] Keys, string Label)[] RecentCommandHints = [(["↵"], "type it"), (["⇧", "↵"], "run it")];
    private static readonly (string[] Keys, string Label)[] RecentDirectoryHints = [(["↵"], "cd"), (["alt", "↵"], "new tab")];

    private Command RecentCommandEntry(RecentEntry e) => new()
    {
        Id = "recent-command:" + e.Text, Category = "Run again", Title = e.Text, Icon = Icons.Rerun,
        Detail = e.Cwd is { } cwd ? ShortPath(cwd) : null,
        RunWith = mods => RunAgain(e.Text, run: (mods & RunModifiers.Shift) != 0),
    };

    private Command RecentDirectoryEntry(RecentEntry e) => new()
    {
        Id = "recent-directory:" + e.Text, Category = "Go to", Title = ShortPath(e.Text), Icon = Icons.Folder,
        RunWith = mods => GoToDirectory(e.Text, newTab: (mods & RunModifiers.Alt) != 0),
    };

    /// <summary>Palette entries (hook from BuildCommands): the latest few inline, and a picker for each whole list.</summary>
    public void AddCommands(List<Command> list)
    {
        if (window.Recent is not { } recent) return;
        if (recent.Commands.Count > 0)
            list.Add(new Command { Id = "recent-commands", Category = "Terminal", Title = "Recent Commands…", Icon = Icons.History, Run = OpenRecentCommands });
        if (recent.Directories.Count > 0)
            list.Add(new Command { Id = "recent-directories", Category = "Terminal", Title = "Recent Directories…", Icon = Icons.History, Run = OpenRecentDirectories });
        for (int i = 0; i < recent.Commands.Count && i < InlineRecentCommands; i++) list.Add(RecentCommandEntry(recent.Commands[i]));
        for (int i = 0; i < recent.Directories.Count && i < InlineRecentDirectories; i++) list.Add(RecentDirectoryEntry(recent.Directories[i]));
        if (recent.Commands.Count + recent.Directories.Count > 0)
            list.Add(new Command
            {
                Id = "recent-clear", Category = "Terminal", Title = "Clear Recent Commands and Directories", Icon = Icons.Clear,
                Run = () =>
                {
                    if (window.ForgetRecent()) window.Toast(ToastKind.Success, "Recent commands and directories cleared");
                    else window.Toast(ToastKind.Error, "Couldn't delete the saved history", "It is still on disk and will come back next time");
                },
            });
    }

    private void OpenRecentCommands()
    {
        if (window.Recent is not { } recent) return;
        var list = new List<Command>(recent.Commands.Count);
        for (int i = 0; i < recent.Commands.Count; i++) list.Add(RecentCommandEntry(recent.Commands[i]));
        window.OpenPalette(list, new CommandPalette.Options { Placeholder = "Search recent commands…", Noun = "commands", Hints = RecentCommandHints });
    }

    private void OpenRecentDirectories()
    {
        if (window.Recent is not { } recent) return;
        var list = new List<Command>(recent.Directories.Count);
        for (int i = 0; i < recent.Directories.Count; i++) list.Add(RecentDirectoryEntry(recent.Directories[i]));
        window.OpenPalette(list, new CommandPalette.Options { Placeholder = "Search recent directories…", Noun = "directories", Hints = RecentDirectoryHints });
    }

    /// <summary>Types a command line into the focused pane as a paste; <paramref name="run"/> presses Enter after it.</summary>
    private void RunAgain(string text, bool run)
    {
        input.PasteText(window.Focused, text, broadcast: true);
        if (run) PressEnter();
    }

    /// <summary>Enter in the focused pane (and, with broadcast input, in the others).</summary>
    public void PressEnter()
    {
        input.SendInput(window.Focused, "\r");
        input.BroadcastText("\r");
    }

    private void GoToDirectory(string directory, bool newTab)
    {
        if (!Directory.Exists(directory)) { window.Toast(ToastKind.Error, "That directory is gone", ShortPath(directory)); return; }
        var pane = window.Focused;
        if (newTab || !AtShellPrompt(pane)) { launcher.SpawnTabIn(directory); return; }
        string? shell = pane.Pty.ForegroundProcessName ?? (pane.LaunchArgv.Length > 0 ? pane.LaunchArgv[0] : null);
        if (ShellCd.Command(directory, ShellCd.Detect(shell)) is not { } line)
        {
            window.Toast(ToastKind.Error, "Can't type that path into a shell", ShortPath(directory));
            return;
        }
        input.PasteText(pane, line, broadcast: true);
        PressEnter();
    }

    /// <summary>Whether what's in the foreground is a shell waiting at its prompt, as far as can be told.</summary>
    private bool AtShellPrompt(Pane pane)
    {
        bool running, alt;
        lock (gate) (running, alt) = (pane.Term.CommandRunning, pane.Term.IsAlternateScreen);
        if (running || alt) return false;
        if (pane.Pty.ForegroundProcessName is { } name) return ShellCd.IsShell(name);
        int group = pane.Pty.ForegroundProcessGroup();
        return group <= 0 || group == pane.Pty.ProcessId; // not known here (Windows): assume the pane's own program
    }
}
