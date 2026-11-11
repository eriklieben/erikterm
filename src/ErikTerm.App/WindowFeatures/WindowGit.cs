using ErikTerm.App.Integrations;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// Git status per directory: <c>git status --porcelain=v2 --branch</c> in a pane's directory, in the background, cached
/// per directory and refreshed on directory change, finished commands and focus (at most every 2 s; every 10 s for
/// the focused pane). Directories that aren't work trees (or bare repositories) show nothing; a status that takes
/// longer than 1.5 s (a huge repository) hides the badge and backs off for two minutes. The badge and pill that show
/// it are <see cref="WindowIntegrations"/>'s. Also commits (<c>git show</c> in a split) and the palette's Git entries.
/// </summary>
internal sealed class WindowGit(IFeatureWindow window, BackgroundJobs jobs, PaneTracks tracks, WindowLaunches launches)
{
    private sealed class GitJobResult
    {
        public GitStatus? Status;
        public bool TimedOut, Failed, RemotesFetched;
        public string? Root, Slug;
    }

    private readonly Dictionary<string, RepoInfo> _repos = new(StringComparer.Ordinal);
    private const int GitTimeoutMs = 1500;

    private static long Now => Environment.TickCount64;

    /// <summary>Every directory a pane is in, by path.</summary>
    public Dictionary<string, RepoInfo> Repos => _repos;

    public RepoInfo RepoFor(string cwd)
    {
        if (!_repos.TryGetValue(cwd, out var repo)) _repos[cwd] = repo = new RepoInfo { Cwd = cwd };
        return repo;
    }

    /// <summary>Forgets directories no pane is in any more.</summary>
    public void Prune()
    {
        if (_repos.Count == 0) return;
        var used = new HashSet<RepoInfo>();
        foreach (var tr in tracks.All.Values)
            if (tr.Repo is { } r) used.Add(r);
        foreach (var key in _repos.Where(kv => !used.Contains(kv.Value) && !kv.Value.State.InFlight).Select(kv => kv.Key).ToList()) _repos.Remove(key);
    }

    // ---- Refresh -------------------------------------------------------------------------------

    /// <summary>Hook from the integrations' scan: starts the status jobs that are due.</summary>
    public void Schedule(long now)
    {
        if (!window.Settings.GitStatus || _repos.Count == 0 || !ToolRunner.Has("git")) return;
        var focused = window.Focused;
        var focusedRepo = tracks.Of(focused)?.Repo;
        foreach (var repo in _repos.Values)
        {
            long content = focused?.Term.ContentVersion ?? 0;
            bool periodic = window.IsFocused && ReferenceEquals(repo, focusedRepo) && repo.Quiet.IsDue(content, now);
            if (!RefreshPolicy.Due(repo.State, now, RefreshTiming.Git, periodic)) continue;
            repo.Quiet.Checked(content, now);
            StartGit(repo, now);
        }
    }

    private void StartGit(RepoInfo repo, long now)
    {
        RefreshPolicy.Started(ref repo.State, now);
        string cwd = repo.Cwd;
        bool remotes = repo.Root is null || now - repo.RemotesAt > 300_000;
        jobs.Run(() => RunGit(cwd, remotes), r => GitDone(repo, r));
    }

    /// <summary>Background: status, then (now and then) the work tree root and GitHub remote.</summary>
    private static GitJobResult RunGit(string cwd, bool remotes)
    {
        var result = new GitJobResult();
        var st = ToolRunner.Run(["git", "-C", cwd, "-c", "core.fsmonitor=", "status", "--porcelain=v2", "--branch"], null, GitTimeoutMs);
        if (st.TimedOut) { result.TimedOut = true; return result; }
        if (st.StartError is not null) { result.Failed = true; return result; }
        if (st.ExitCode != 0) return result; // not a work tree (or a bare repository): nothing to show
        result.Status = GitStatusParser.Parse(st.Stdout);
        if (result.Status is null || !remotes) return result;

        var top = ToolRunner.Run(["git", "-C", cwd, "rev-parse", "--show-toplevel"], null, GitTimeoutMs);
        result.Root = top.Ok && top.Stdout.Trim() is { Length: > 0 } root && Path.IsPathRooted(root) ? root : null;
        var config = ToolRunner.Run(["git", "-C", cwd, "config", "--get-regexp", @"^remote\..*\.url$"], null, GitTimeoutMs);
        result.Slug = config.Ok ? GitStatusParser.GitHubSlugFromRemotes(config.Stdout) : null;
        result.RemotesFetched = !top.TimedOut && !config.TimedOut;
        return result;
    }

    private void GitDone(RepoInfo repo, GitJobResult? r)
    {
        long now = Now;
        RefreshPolicy.Completed(ref repo.State, now, GitOutcome(r), RefreshTiming.Git);
        if (r is null || r.Failed) return; // keep what we had; retried after a back-off
        var old = repo.Status;
        if (!ApplyGitResult(repo, r, now)) return;
        repo.Version++;
        // A new commit or a push (ahead → 0) usually means CI starts: look at GitHub again.
        if (old is not null && r.Status is not null && (old.Oid != r.Status.Oid || old.Ahead != r.Status.Ahead || old.Branch != r.Status.Branch))
            RequestGitHubRefresh(repo);
    }

    private static RefreshOutcome GitOutcome(GitJobResult? r)
    {
        if (r is null || r.Failed) return RefreshOutcome.Failed;
        return r.TimedOut ? RefreshOutcome.TimedOut : RefreshOutcome.Ok;
    }

    /// <summary>Stores the job's result in the repo; true when something the pills show changed.</summary>
    private static bool ApplyGitResult(RepoInfo repo, GitJobResult r, long now)
    {
        bool changed = !Equals(repo.Status, r.Status); // a timeout clears it: hide rather than show stale counts
        repo.Status = r.Status;
        if (r.RemotesFetched)
        {
            changed |= repo.Root != r.Root || repo.Slug != r.Slug;
            (repo.Root, repo.Slug, repo.RemotesAt) = (r.Root, r.Slug, now);
        }
        else if (r.Status is null) (repo.Root, repo.Slug) = (null, null);
        return changed;
    }

    private void RequestGitHubRefresh(RepoInfo repo)
    {
        foreach (var tr in tracks.All.Values)
            if (ReferenceEquals(tr.Repo, repo) && tr.Gh is { } gh) RefreshPolicy.Request(ref gh.State);
    }

    // ---- Commits -------------------------------------------------------------------------------

    /// <summary>Validates a hash (7–40 hex), resolves it to a commit in the pane's repository, then runs <paramref name="then"/>.</summary>
    public void ResolveCommit(string hash, string cwd, Action<string> then)
    {
        if (!TextPatterns.IsCommitHash(hash)) { window.Toast(ToastKind.Error, "Not a commit hash", hash); return; }
        jobs.Run(() =>
        {
            var r = ToolRunner.Run(["git", "-C", cwd, "rev-parse", "--verify", "--quiet", hash + "^{commit}"], null, 3000);
            string full = r.Ok ? r.Stdout.Trim() : "";
            return TextPatterns.IsCommitHash(full) && full.Length == 40 ? full : "";
        }, full =>
        {
            if (string.IsNullOrEmpty(full)) window.Toast(ToastKind.Error, "Unknown commit", $"{hash} isn't a commit in {DisplayPath(cwd)}");
            else then(full);
        }, wakeLoop: true);
    }

    /// <summary>git show in a split, through delta when it is installed (pager kept open: LESS without F).</summary>
    public void ShowCommit(string fullHash, string cwd) =>
        launches.SplitRun(cwd, [.. GitPaged(cwd, "show"), "--stat", "--patch", "--end-of-options", fullHash]);

    /// <summary>
    /// argv prefix for a paged git command: <c>env LESS=R git -C cwd -c pager.CMD=… CMD</c>. Uses delta when
    /// installed, else less -R with --color.
    /// </summary>
    private static string[] GitPaged(string cwd, string command)
    {
        bool delta = ToolRunner.Has("delta");
        string pager = delta ? "delta --paging=always" : "less -R";
        var argv = new List<string> { "env", "LESS=R", "DELTA_PAGER=less -R", "git", "-C", cwd, "-c", "core.pager=" + pager, "-c", $"pager.{command}={pager}", command };
        if (!delta) argv.Add("--color=always");
        return [.. argv];
    }

    public static string DisplayPath(string path) => FileRefResolver.HomeRelative(path);

    // ---- Palette -------------------------------------------------------------------------------

    /// <summary>Palette hook: Git entries for the focused pane's repository.</summary>
    public void AddCommands(List<Command> list, PaneTrack? tr)
    {
        if (!window.Settings.GitStatus || tr?.Repo is not { Status: { } status } repo) return;
        string cwd = repo.Cwd;
        void Git(string id, string title, int icon, Action run) =>
            list.Add(new Command { Id = "git:" + id, Category = "Git", Title = title, Icon = icon, Run = run });
        Git("status", $"Status of {status.Branch} (in a split)", RepoBadge.BranchIcon, () => launches.SplitRun(cwd, [.. GitPaged(cwd, "status")]));
        Git("diff", "Diff (in a split)", Icons.SplitRight, () => launches.SplitRun(cwd, [.. GitPaged(cwd, "diff")]));
        Git("diff-staged", "Diff Staged (in a split)", Icons.SplitRight, () => launches.SplitRun(cwd, [.. GitPaged(cwd, "diff"), "--cached"]));
        Git("log", "Log (in a split)", RepoBadge.CommitIcon, () => launches.SplitRun(cwd, [.. GitPaged(cwd, "log"), "--graph", "--decorate", "--oneline", "-n", "500"]));
        if (ToolRunner.Has("lazygit")) Git("lazygit", "Open lazygit (in a split)", Icons.Terminal, () => launches.SplitRun(cwd, ["lazygit"]));
        if (ToolRunner.Has("tig")) Git("tig", "Open tig (in a split)", Icons.Terminal, () => launches.SplitRun(cwd, ["tig"]));
        Git("refresh", "Refresh Status", Icons.Refresh, () =>
        {
            repo.State.BlockedUntil = 0;
            RefreshPolicy.Request(ref repo.State);
            if (tr.Gh is { } gh) { gh.State.BlockedUntil = 0; RefreshPolicy.Request(ref gh.State); }
            tracks.ScanSoon();
        });
        if (status.Oid is { Length: 40 } head)
            Git("copy-head", "Copy HEAD Commit Hash", Icons.Copy, () => Clipboard.Copy(window, head, "Copied commit hash"));
    }
}
