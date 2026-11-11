using ErikTerm.App.Compare;
using ErikTerm.App.Integrations;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;
using ErikTerm.Ui.Compare;

namespace ErikTerm.App.WindowFeatures;

/// <summary>What <see cref="WindowReview"/> needs beyond the feature window: the process's open reviews and the focused worktree.</summary>
internal interface IReviewWindow : IFeatureWindow
{
    /// <summary>Shows the tab already reviewing <paramref name="root"/>, in any window; false when there is none.</summary>
    bool JumpToReview(string root);
    /// <summary>The focused pane's directory for worktree actions, or null.</summary>
    string? FocusedWorktreeCwd();
}

/// <summary>
/// "Git: Compare with main": a tab running <c>erikterm review</c> for the focused pane's worktree. Running it again for
/// the same worktree jumps to the tab already showing it, found through the user variable the review sets on its pane.
/// </summary>
internal sealed class WindowReview(IReviewWindow window, BackgroundJobs jobs, PaneTracks tracks, WindowGitOperations gitOps, WindowLaunches launches, Lock gate)
{
    private const string TabTitlePrefix = "compare: ";

    private readonly Dictionary<string, string?> _reviewBaseNames = new(StringComparer.Ordinal);

    private sealed record ReviewBaseName(string? Name);

    private sealed record ReviewProbe(string? Root, string Branch);

    private static long Now => Environment.TickCount64;

    /// <summary>Palette hook: Compare with main, Compare with Branch….</summary>
    public void AddCommands(List<Command> list)
    {
        if (!ToolRunner.Has("git")) return;
        if (ActionSpec.TryParse("CompareWithMain", out var spec, out _))
        {
            string target = DefaultBranchName() ?? "main";
            list.Add(new Command { Id = "compare-with-main", Category = "Git", Title = $"Compare with {target}", Icon = RepoBadge.PullRequestIcon, Action = spec, Shortcut = window.ShortcutFor(spec) });
        }
        if (ActionSpec.TryParse("CompareWithMain(Pick)", out var pick, out _))
            list.Add(new Command { Id = "compare-with-branch", Category = "Git", Title = "Compare with Branch…", Icon = RepoBadge.BranchIcon, Action = pick, Shortcut = window.ShortcutFor(pick) });
    }

    /// <summary>The default branch's name for the focused pane's repository, read in the background the first time (the palette is built on the UI thread).</summary>
    private string? DefaultBranchName()
    {
        if (tracks.Of(window.Focused)?.Repo?.Root is not { } root) return null;
        if (gitOps.KnownTarget(root) is { } known) return known;
        if (_reviewBaseNames.TryGetValue(root, out var name)) return name;
        _reviewBaseNames[root] = null; // only once
        jobs.Run(() =>
        {
            string? @base = WorktreeGit.Probe(root, out _)?.DefaultBase;
            return @base is null ? null : new ReviewBaseName(WorktreeList.LocalBranch(@base));
        }, r => _reviewBaseNames[root] = r?.Name);
        return null;
    }

    /// <summary>The review of the focused pane's worktree: its tab if one is open, else a new one.</summary>
    public void Open(bool pickBase)
    {
        if (window.FocusedWorktreeCwd() is not { } cwd) return;
        window.CloseMenus(Now);
        jobs.Run(() =>
        {
            string? root = GitTool.TopLevel(cwd);
            if (root is null) return new ReviewProbe(null, "");
            var head = GitTool.Run(root, "symbolic-ref", "--quiet", "--short", "HEAD");
            string branch;
            if (head.Ok) branch = head.Text;
            else branch = GitTool.Run(root, "rev-parse", "--short", "HEAD") is { Ok: true } oid ? oid.Text : "HEAD";
            return new ReviewProbe(root, branch);
        }, r => ReviewProbed(r, cwd, pickBase), wakeLoop: true);
    }

    private void ReviewProbed(ReviewProbe? probe, string cwd, bool pickBase)
    {
        if (probe?.Root is not { } root)
        {
            window.Toast(ToastKind.Error, "Nothing to compare", $"Not in a git repository: {cwd}");
            return;
        }
        if (window.JumpToReview(root)) return;
        if (Environment.ProcessPath is not { } self) return;
        string[] argv = pickBase ? [self, "review", "--pick-base"] : [self, "review"];
        if (launches.SpawnToolTab(argv, root, "Couldn't open the compare") is not { } tab) return;
        tab.CustomTitle = TabTitlePrefix + probe.Branch;
        tab.TransientTitle = true; // names this tab's program, not a place to come back to
        window.Redraw();
    }

    /// <summary>Hook from App.ReviewPanes: panes of this window showing a review.</summary>
    public void CollectPanes(List<ReviewPaneRef> into)
    {
        lock (gate)
        {
            foreach (var layout in window.Tabs.Items.Select(tab => tab.Layout))
            {
                if (layout is null) continue;
                foreach (var pane in layout.LeafList)
                    if (pane.Term.UserVars.TryGetValue(ReviewTabs.UserVariable, out var root) && root.Length > 0)
                        into.Add(new ReviewPaneRef(pane.Id, root));
            }
        }
    }
}
