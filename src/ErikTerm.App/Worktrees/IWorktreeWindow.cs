using ErikTerm.App.Integrations;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Worktrees;

/// <summary>What the worktree features read and set on a pane: <see cref="Pane"/> implements it, tests use a fake.</summary>
internal interface IWorktreePane : IPaneView, IPaneAgentStatus
{
    /// <summary>The directory a new tab or a repository lookup starts in.</summary>
    string? StartDirectory(string? defaultCwd);
    /// <summary>The agent's name for the worktree list.</summary>
    string AgentLabel { get; }
    /// <summary>The exited program's status, or null when it isn't known.</summary>
    int? ExitCode { get; }
    /// <summary>Close when the program exits, whatever exit_behavior says.</summary>
    bool CloseOnExit { get; set; }
    /// <summary>What Restart Pane runs.</summary>
    string[] LaunchArgv { get; set; }
    string? LaunchCwd { get; set; }
    /// <summary>The agent a launch started: never saved with the session.</summary>
    IReadOnlyList<string>? LaunchedAgent { get; set; }
    /// <summary>A launch's programs still to come (setup, agent, shell); null otherwise.</summary>
    WorktreeLaunchPlan? WorktreeLaunch { get; set; }
}

/// <summary>One pane inside a worktree, as the window found it.</summary>
internal sealed record WorktreePaneRef<TPane>(TPane Pane, AgentState State, bool AgentActive, string AgentName);

/// <summary>A launch's new tab: its title (the branch), its first program and directory, and the setup echo.</summary>
internal readonly record struct WorktreeTabLaunch(string Title, IReadOnlyList<string> Argv, string Cwd, Action<Terminal>? Echo, bool Background);

/// <summary>Repository changes, one program at a time in a visible split (WindowFeatures/WindowRepoPlans.cs).</summary>
internal interface IWorktreeRepoPlans
{
    /// <summary>Runs the plan's first guard, then opens the split; <paramref name="failed"/> gets the guard's reason (nothing ran).</summary>
    void StartRepoPlan(RepoPlan plan, Action<string>? failed, Action? started);
}

/// <summary>Opening the panes of a New Agent Worktree launch.</summary>
internal interface IWorktreeLaunches<TPane> where TPane : class
{
    /// <summary>
    /// Opens a colored tab running <see cref="WorktreeTabLaunch.Argv"/>. <paramref name="prepared"/> runs after the pane
    /// started, before the tab is shown. On failure the tab is gone again and the reason is returned; null on success.
    /// </summary>
    string? OpenLaunchTab(WorktreeTabLaunch launch, Action<TPane> prepared);
    /// <summary>A split next to the focused pane, running <paramref name="argv"/>; null when it couldn't start.</summary>
    TPane? OpenLaunchSplit(IReadOnlyList<string> argv, string cwd, bool focus, Action<Terminal>? echo);
    /// <summary>A split to the right of <paramref name="target"/> (in any tab), running <paramref name="argv"/>; null when it couldn't start.</summary>
    TPane? OpenLaunchSplitNextTo(TPane target, IReadOnlyList<string> argv, string cwd, bool focus, Action<Terminal>? echo);
    /// <summary>Starts a program in an exited pane; false when it couldn't start.</summary>
    bool Respawn(TPane pane, string[] argv, string? cwd);
    /// <summary>A new tab with a shell in <paramref name="cwd"/>, named after the branch when there is one.</summary>
    void OpenShellTab(string cwd, string? branch);
}

/// <summary>What <see cref="WindowWorktrees{TPane}"/> needs from its window: TermWindow implements it, tests use a fake.</summary>
internal interface IWorktreeWindow<TPane> : IWindowPanes<TPane>, IWindowChrome, IWorktreeRepoPlans, IWorktreeLaunches<TPane>
    where TPane : class, IWorktreePane
{
    Theme OverlayTheme { get; }
    /// <summary>GitHub status is on and <c>gh</c> is available.</summary>
    bool GitHubEnabled { get; }
    /// <summary>GitHub status is on and <c>gh</c> is signed in.</summary>
    bool GitHubSignedIn { get; }
    void DefaultCursor();
    string[] ShortcutFor(ActionSpec action);
    /// <summary>The context menu and the command palette give way to a worktree overlay.</summary>
    void CloseMenus(long now);
    /// <summary>The directory the pane's git status follows.</summary>
    string? IntegrationCwd(TPane pane);
    /// <summary>Opens a dashboard in the browser without showing its link (it holds a login token); the reason when it couldn't.</summary>
    string? OpenDashboardLink(string url);
    /// <summary>Runs <paramref name="work"/> off the main thread, then <paramref name="then"/> on it (null when it threw).</summary>
    void RunInBackground<T>(Func<T?> work, Action<T?> then, bool wake = false) where T : class;
}

/// <summary>What <see cref="WindowWorktrees{TPane}"/> needs from the process: App implements it, tests use a fake.</summary>
internal interface IWorktreeProcess<TPane> where TPane : class
{
    Settings Settings { get; }
    /// <summary>Every window's panes, by the worktree (one of <paramref name="roots"/>) they're in.</summary>
    Dictionary<string, List<WorktreePaneRef<TPane>>> PanesByWorktree(List<string> roots);
    /// <summary>Shows a pane in that worktree, in any window; false when none is open there.</summary>
    bool JumpToWorktree(string path, List<string> roots);
    /// <summary>What a window's GitHub status knows about this worktree's branch (nothing is fetched for this).</summary>
    GitHubStatus? KnownGitHubStatus(string root, string branch);
}
