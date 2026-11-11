using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.App.Worktrees;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// Opening erikterm's own tools and launches: a command in a split (<c>git show</c>, an editor, <c>gh run watch</c>), a
/// tool in a new tab (the merge tool, the review, the settings page), and the tabs and splits a New Agent Worktree
/// launch opens (TermWindow forwards <see cref="IWorktreeLaunches{TPane}"/> here).
/// </summary>
internal sealed class WindowLaunches(IFeatureWindow window, IPaneLauncher launcher, WindowIpc ipc)
{
    /// <summary>Opens <paramref name="argv"/> in a split to the right, started in <paramref name="cwd"/> (env -C: no shell).</summary>
    public void SplitRun(string? cwd, string[] argv)
    {
        string[] command = cwd is not null && Directory.Exists(cwd) && OperatingSystem.IsLinux() ? ["env", "-C", cwd, .. argv] : argv;
        launcher.Split(SplitAxis.Horizontal, command);
    }

    /// <summary>One of erikterm's own tools in a new tab; it closes when the tool quits, like the other tool panes. Null when it couldn't start.</summary>
    public Tab? SpawnToolTab(string[] argv, string cwd, string errorTitle)
    {
        window.SyncActiveTab();
        var tabs = window.Tabs;
        var from = tabs.Active!;
        var tab = new Tab();
        tabs.Add(tab); // before sizing the pane: the bar may appear and take room
        Pane pane;
        try
        {
            pane = launcher.SpawnPane(argv, cwd, window.ContentBounds());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            tabs.Remove(tab);
            tabs.Activate(from);
            window.Toast(ToastKind.Error, errorTitle, ex.Message);
            return null;
        }
        pane.CloseOnExit = true;
        tab.Layout = new LayoutTree<Pane>(pane);
        tab.Focused = pane;
        launcher.SwitchTo(from, tab, +1);
        return tab;
    }

    // ---- New Agent Worktree launches ----------------------------------------------------------------

    /// <summary>A colored tab running the launch's first program; the reason when it couldn't start, else null.</summary>
    public string? OpenLaunchTab(WorktreeTabLaunch launch, Action<Pane> prepared)
    {
        window.SyncActiveTab();
        var tabs = window.Tabs;
        var from = tabs.Active!;
        var tab = new Tab
        {
            CustomTitle = launch.Title,
            Color = WorktreeLauncher.ChooseTabColor(tabs.Items.Select(t => t.Color), TabColors.Offered, launch.Title),
        };
        tabs.Add(tab); // before sizing the pane: the bar may appear and take room
        Pane pane;
        try
        {
            pane = launcher.SpawnPane(LaunchCommand(launch.Argv), launch.Cwd, window.ContentBounds(), launch.Echo);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            tabs.Remove(tab);
            tabs.Activate(from);
            return ex.Message;
        }
        prepared(pane);
        tab.Layout = new LayoutTree<Pane>(pane);
        tab.Focused = pane;
        if (launch.Background)
        {
            tabs.Activate(from);
            tabs.Demote(tab); // Ctrl+Tab still flips to the tab you came from
            window.ApplyLayout(animate: false); // the tab bar may have appeared; also sizes the new tab
            window.Redraw();
        }
        else launcher.SwitchTo(from, tab, +1);
        return null;
    }

    public Pane? OpenLaunchSplit(IReadOnlyList<string> argv, string cwd, bool focus, Action<Terminal>? echo) =>
        launcher.Split(SplitAxis.Horizontal, [.. argv], cwd, focus, echo);

    /// <summary>A split to the right of <paramref name="target"/>, in whichever tab it is; null when it couldn't start.</summary>
    public Pane? OpenLaunchSplitNextTo(Pane target, IReadOnlyList<string> argv, string cwd, bool focus, Action<Terminal>? echo)
    {
        window.SyncActiveTab();
        // The pane may have closed while git was busy: then next to the focused one.
        var request = new IpcRequest { PaneId = window.FindPane(target.Id) is not null ? target.Id : null, Direction = "Right", Argv = LaunchCommand(argv), Cwd = cwd, NoFocus = !focus };
        try
        {
            return window.FindPane(ipc.SplitPane(request, echo).PaneId ?? 0);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or IpcException)
        {
            Console.Error.WriteLine($"erikterm: couldn't open the worktree's split: {ex.Message}");
            return null;
        }
    }

    /// <summary>A new tab with a shell in <paramref name="cwd"/>, named after the branch when there is one.</summary>
    public void OpenShellTab(string cwd, string? branch)
    {
        launcher.SpawnTabIn(cwd);
        if (window.Tabs.Active is { } tab && branch is not null) { tab.CustomTitle = branch; window.Redraw(); }
    }

    /// <summary>What to spawn: null for the default shell, which is how it gets its shell integration.</summary>
    private string[]? LaunchCommand(IReadOnlyList<string> argv) =>
        argv.SequenceEqual(launcher.DefaultShell()) ? null : [.. argv];
}
