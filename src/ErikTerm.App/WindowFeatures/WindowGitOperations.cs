using ErikTerm.App.Integrations;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// The merge/rebase bar: while the focused pane's repository is mid-merge, rebase, cherry-pick or revert, the shared info
/// bar says so and offers Next conflict, Continue and Abort. State comes from git's own files, per worktree, in the
/// background; nothing runs from here without a click, and Abort asks first. Also the optional tab warning: a
/// merge-tree prediction against the default branch, re-run only when HEAD or the target moved (at most every 30 s).
/// </summary>
internal sealed class WindowGitOperations(
    IFeatureWindow window, IPaneLauncher launcher, BackgroundJobs jobs, PaneTracks tracks, WindowGit git, InfoBar infoBar,
    WindowRepoPlans repoPlans, WindowEditor editor, WindowLaunches launches)
{
    private const string GitOpBarKey = "git-operation", GitOpAbortKey = "git-operation-abort";
    private const int GitOpPollMs = 2000, ConflictCheckMinMs = 30_000, ConflictTargetPollMs = 60_000;

    private string? _gitOpRoot, _gitOpLastConflict, _gitOpShown;
    private long _gitOpAt;
    private QuietPoll _gitOpQuiet;
    private bool _gitOpInFlight;
    private readonly Dictionary<string, Dictionary<string, string>> _gitStatePaths = new(StringComparer.Ordinal);
    private GitOperationState _gitOpState;
    private IReadOnlyList<string> _gitOpConflicts = [];

    private sealed record GitOpResult(string Root, GitOperationState State, IReadOnlyList<string> Conflicts, Dictionary<string, string>? Paths);

    private static long Now => Environment.TickCount64;

    /// <summary>Hook from the scan when the focus moved: a merge or rebase may have started while it was elsewhere.</summary>
    public void FocusMoved() => _gitOpQuiet = default;

    /// <summary>Hook from Scan: polls the focused repository's state files.</summary>
    public void Tick(long now)
    {
        var focused = window.Focused;
        var repo = tracks.Of(focused)?.Repo;
        string? root = window.Settings.GitStatus && repo is { Status: not null } ? repo.Root : null;
        if (root != _gitOpRoot) { _gitOpRoot = root; _gitOpAt = 0; _gitOpQuiet = default; ClearGitOperation(now); } // the bar's buttons act on one repository
        if (root is null) { ClearGitOperation(now); return; }
        if (now < _gitOpAt || _gitOpInFlight) return;
        long content = focused.Term.ContentVersion;
        if (_gitOpShown is null && !_gitOpQuiet.IsDue(content, now)) return; // a bar on screen keeps polling: it may be resolved elsewhere
        _gitOpQuiet.Checked(content, now);
        _gitOpAt = now + GitOpPollMs;
        _gitOpInFlight = true;
        _gitStatePaths.TryGetValue(root, out var paths);
        jobs.Run(() =>
        {
            var p = paths ?? WorktreeManagerGit.StatePaths(root);
            if (p is null) return new GitOpResult(root, GitOperationState.None, [], null);
            var (state, conflicts) = WorktreeManagerGit.Probe(root, p);
            return new GitOpResult(root, state, conflicts, p);
        }, GitOperationProbed);
    }

    private void GitOperationProbed(GitOpResult? r)
    {
        _gitOpInFlight = false;
        if (r is null || r.Root != _gitOpRoot) return;
        if (r.Paths is { } paths) _gitStatePaths[r.Root] = paths;
        _gitOpState = r.State;
        _gitOpConflicts = r.Conflicts;
        ShowGitOperationBar(r.Root);
    }

    private void ClearGitOperation(long now)
    {
        if (_gitOpShown is null) return;
        _gitOpShown = null;
        _gitOpState = GitOperationState.None;
        _gitOpConflicts = [];
        infoBar.Remove(GitOpBarKey, now);
        infoBar.Remove(GitOpAbortKey, now);
    }

    private void ShowGitOperationBar(string root)
    {
        long now = Now;
        if (_gitOpState.Kind == GitOperationKind.None) { ClearGitOperation(now); return; }
        string text = GitOperationDetector.BarText(_gitOpState, _gitOpConflicts.Count);
        if (_gitOpShown == text) return;
        _gitOpShown = text;
        var kind = _gitOpState.Kind;
        var buttons = new List<InfoBarButton>();
        if (_gitOpConflicts.Count > 0)
        {
            buttons.Add(new InfoBarButton("Open Compare", () => OpenMergeTool(root), primary: true));
            buttons.Add(new InfoBarButton("Next conflict", () => NextConflict(root)));
        }
        if (GitOperationDetector.Subcommand(kind) is not null)
        {
            buttons.Add(new InfoBarButton("Continue", () => ContinueOperation(root, kind), primary: _gitOpConflicts.Count == 0));
            buttons.Add(new InfoBarButton("Abort", () => ConfirmAbort(root, kind)));
        }
        infoBar.Show(new InfoBarMessage
        {
            Key = GitOpBarKey, Tone = _gitOpConflicts.Count > 0 ? InfoBarTone.Warning : InfoBarTone.Info,
            Icon = RepoBadge.BranchIcon, Text = text, Detail = WindowGit.DisplayPath(root), Dismissible = false, Buttons = [.. buttons],
        }, window.Theme, now);
        window.Redraw();
    }

    private void NextConflict(string root)
    {
        if (GitOperationDetector.NextConflict(_gitOpConflicts, _gitOpLastConflict) is not { } next) return;
        _gitOpLastConflict = next;
        jobs.Run<object>(() => WorktreeManagerGit.OpenAtMarker(root, next), r =>
        {
            if (r is ValueTuple<string, int> at) editor.OpenFile(at.Item1, at.Item2, 1, forceSplit: editor.Mode == EditorOpenMode.None);
            else window.Toast(ToastKind.Error, "Couldn't open the conflicted file", next);
        }, wakeLoop: true);
    }

    /// <summary>The conflict resolver in a new tab (or next to the focused pane with worktree_open_in = "split"), in the repository.</summary>
    private void OpenMergeTool(string root)
    {
        if (Environment.ProcessPath is not { } self) return;
        string[] argv = [self, "mergetool"];
        if (WorktreeLauncher.ParsePlacement(window.Settings.WorktreeOpenIn) == WorktreePlacement.Split)
        {
            if (launcher.Split(SplitAxis.Horizontal, argv, root) is null) window.Toast(ToastKind.Error, "Couldn't open the merge tool", root);
            return;
        }
        launches.SpawnToolTab(argv, root, "Couldn't open the merge tool");
    }

    private void ContinueOperation(string root, GitOperationKind kind)
    {
        if (_gitOpConflicts.Count > 0)
        {
            window.Toast(ToastKind.Info, $"{Counted.Of(_gitOpConflicts.Count, "conflict")} left", "Resolve them first");
            return;
        }
        var argv = WorktreeCommands.OperationStep(root, kind, abort: false);
        repoPlans.Start(new RepoPlan(GitOperationDetector.BarText(_gitOpState, 0), [new RepoStep(argv, root, WorktreeCommands.Display(argv))]), null);
    }

    private void ConfirmAbort(string root, GitOperationKind kind)
    {
        string name = GitOperationDetector.Subcommand(kind) ?? "operation";
        infoBar.Show(new InfoBarMessage
        {
            Key = GitOpAbortKey, Priority = 3, Tone = InfoBarTone.Danger, Icon = Icons.Warning,
            Text = $"Abort the {name}?", Detail = "The work tree goes back to before it started; conflict resolutions so far are lost.", Dismissible = false,
            Buttons =
            [
                new InfoBarButton($"Abort {name}", () =>
                {
                    infoBar.Remove(GitOpAbortKey, Now);
                    var argv = WorktreeCommands.OperationStep(root, kind, abort: true);
                    repoPlans.Start(new RepoPlan($"Abort {name}", [new RepoStep(argv, root, WorktreeCommands.Display(argv))]), null);
                }, primary: true),
                new InfoBarButton("Keep going", () => infoBar.Remove(GitOpAbortKey, Now)),
            ],
        }, window.Theme, Now);
        window.Redraw();
    }

    // ---- Conflict warning on tabs -----------------------------------------------------------------

    private sealed class ConflictCheckState
    {
        public string? Head, TargetOid, Target;
        public long CheckedAt, PolledAt;
        public bool InFlight, Conflict;
    }

    private readonly Dictionary<string, ConflictCheckState> _conflictChecks = new(StringComparer.Ordinal);

    private sealed record ConflictJob(string Head, string? TargetOid, ConflictPrediction? Prediction);

    /// <summary>Hook from Scan: at most one merge-tree per repository per 30 s, and only when HEAD or the target moved.</summary>
    public void TickConflictChecks(long now)
    {
        var settings = window.Settings;
        if (!settings.GitConflictCheck || !settings.GitStatus) return;
        foreach (var repo in git.Repos.Values)
        {
            if (repo is not { Root: { } root, Status: { Detached: false, Oid: { Length: 40 } head } status }) continue;
            if (!_conflictChecks.TryGetValue(root, out var check)) _conflictChecks[root] = check = new ConflictCheckState();
            if (check.InFlight) continue;
            bool headMoved = check.Head != head;
            if (!headMoved && now - check.PolledAt < ConflictTargetPollMs) continue;
            if (now - check.CheckedAt < ConflictCheckMinMs && check.Target is not null) continue;
            StartConflictCheck(check, root, status.Branch, head, headMoved, now);
        }
    }

    private void StartConflictCheck(ConflictCheckState check, string repoRoot, string branch, string head, bool headMoved, long now)
    {
        check.InFlight = true;
        check.PolledAt = now;
        string? known = headMoved ? null : check.TargetOid;
        string? target = check.Target;
        jobs.Run(() =>
        {
            if (target is null && WorktreeManagerGit.Inventory(repoRoot, out _) is { Target: { } t } ) target = t;
            if (target is null || target == branch) return new ConflictJob(head, null, null);
            return WorktreeManagerGit.ConflictCheck(repoRoot, target, head, known) is { } result
                ? new ConflictJob(head, result.TargetOid, result.Prediction)
                : new ConflictJob(head, null, null);
        }, r => ConflictChecked(check, repoRoot, r, target));
    }

    private void ConflictChecked(ConflictCheckState check, string root, ConflictJob? job, string? target)
    {
        long now = Now;
        check.InFlight = false;
        check.CheckedAt = now;
        check.Target = target;
        if (job is null) return;
        check.Head = job.Head;
        if (job.TargetOid is not null) check.TargetOid = job.TargetOid;
        if (job.Prediction is not { Known: true } prediction) return; // unchanged, or unknown: keep what's shown
        bool conflict = !prediction.Clean;
        if (conflict == check.Conflict) return;
        check.Conflict = conflict;
        foreach (var repo in git.Repos.Values) if (repo.Root == root) repo.Version++;
        window.Redraw();
    }

    /// <summary>The tab warning: merging the branch into the default branch would conflict.</summary>
    public bool PredictsConflict(string? root) => root is not null && window.Settings.GitConflictCheck && _conflictChecks.TryGetValue(root, out var c) && c.Conflict;

    /// <summary>The default branch a conflict check found for the repository, or null.</summary>
    public string? KnownTarget(string root) => _conflictChecks.TryGetValue(root, out var check) ? check.Target : null;
}
