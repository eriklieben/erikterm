using ErikTerm.App.Integrations;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>
/// GitHub through the gh CLI (never its token: erikterm only runs gh): for a pane whose repository has a github.com
/// remote, the current branch's PR (<c>gh pr view --json …</c>) with its checks rollup, or without a PR the latest
/// workflow run (<c>gh run list</c>). Cached per repository + branch; refreshed on focus, finished commands and every
/// three minutes (every minute while checks run), backing off on errors and for 15 minutes on rate limits.
/// A pending → passed/failed transition you're not looking at gets a toast. Palette: open the PR, view checks, watch
/// the run in a split (with a toast when it ends).
/// </summary>
internal sealed class WindowGitHub(IFeatureWindow window, BackgroundJobs jobs, PaneTracks tracks, WindowLaunches launches, Lock gate)
{
    private sealed class GhJobResult
    {
        public GitHubStatus? Status;
        public RefreshOutcome Outcome;
    }

    private enum GhAuth : byte { Unknown, Checking, Yes, No }

    private readonly Dictionary<string, GhInfo> _ghInfos = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (string Slug, long RunId, string? Url)> _runWatches = [];
    private GhAuth _ghAuth;
    private const int GhTimeoutMs = 15_000;
    private const string RunExitVar = "ERIKTERM_RUN_EXIT";

    private static long Now => Environment.TickCount64;

    /// <summary>Bumped when gh turned out to be signed out: every badge drops its GitHub parts.</summary>
    public int AuthEpoch { get; private set; }

    public bool Enabled
    {
        get
        {
            var s = window.Settings;
            return s.GithubStatus && s.GitStatus && _ghAuth != GhAuth.No && ToolRunner.Has("gh");
        }
    }

    /// <summary>GitHub status is on and gh is signed in.</summary>
    public bool SignedIn => Enabled && _ghAuth == GhAuth.Yes;

    /// <summary>What is known about a worktree's branch (nothing is fetched for this).</summary>
    public GitHubStatus? StatusFor(string root, string branch) =>
        _ghInfos.TryGetValue(root + "\n" + branch, out var info) ? info.Status : null;

    /// <summary>The GitHub state for a repo's current branch (created on first use), or null when there is none to show.</summary>
    public GhInfo? InfoFor(RepoInfo? repo)
    {
        if (!Enabled || repo is not { Status: { Detached: false } status, Root: { } root, Slug: { } slug }) return null;
        string key = root + "\n" + status.Branch;
        if (!_ghInfos.TryGetValue(key, out var info))
        {
            _ghInfos[key] = info = new GhInfo { Root = root, Branch = status.Branch, Slug = slug };
            RefreshPolicy.Request(ref info.State);
        }
        return info;
    }

    /// <summary>Forgets branches no pane is on any more.</summary>
    public void Prune()
    {
        if (_ghInfos.Count == 0) return;
        var used = new HashSet<GhInfo>();
        foreach (var tr in tracks.All.Values)
            if (tr.Gh is { } g) used.Add(g);
        foreach (var key in _ghInfos.Where(kv => !used.Contains(kv.Value) && !kv.Value.State.InFlight).Select(kv => kv.Key).ToList()) _ghInfos.Remove(key);
    }

    /// <summary>Hook from the integrations' scan: starts the gh jobs that are due.</summary>
    public void Schedule(long now)
    {
        if (!Enabled || _ghInfos.Count == 0) return;
        if (_ghAuth == GhAuth.Unknown) { CheckGhAuth(); return; }
        if (_ghAuth != GhAuth.Yes) return;
        foreach (var info in _ghInfos.Values)
        {
            var timing = info.Status?.Checks == CheckState.Pending ? RefreshTiming.GitHub with { PeriodMs = 60_000 } : RefreshTiming.GitHub;
            if (RefreshPolicy.Due(info.State, now, timing, periodic: true)) StartGh(info, now);
        }
    }

    /// <summary>`gh auth status` once: its exit code says whether gh can talk to github.com (the output is ignored).</summary>
    private void CheckGhAuth()
    {
        _ghAuth = GhAuth.Checking;
        jobs.Run(() => ToolRunner.Run(["gh", "auth", "status", "--hostname", "github.com"], null, 8000) is { Ok: true } ? "yes" : "no",
            r =>
            {
                _ghAuth = r == "yes" ? GhAuth.Yes : GhAuth.No;
                if (_ghAuth == GhAuth.No) AuthEpoch++; // drop the GitHub parts
            });
    }

    private void StartGh(GhInfo info, long now)
    {
        RefreshPolicy.Started(ref info.State, now);
        string root = info.Root, branch = info.Branch;
        jobs.Run(() => RunGh(root, branch), r => GhDone(info, r));
    }

    /// <summary>Background: the branch's PR, else its latest workflow run.</summary>
    private static GhJobResult RunGh(string root, string branch)
    {
        var pr = ToolRunner.Run(["gh", "pr", "view", "--json", "number,title,state,url,statusCheckRollup,reviewDecision,isDraft"], root, GhTimeoutMs);
        if (pr.TimedOut) return new GhJobResult { Outcome = RefreshOutcome.TimedOut };
        if (pr.Ok)
        {
            return GitHubParser.ParsePr(pr.Stdout) is { Number: > 0 } parsed
                ? new GhJobResult { Status = GitHubParser.FromPr(parsed) }
                : new GhJobResult { Outcome = RefreshOutcome.Failed };
        }
        if (GitHubParser.IsRateLimited(pr.Stderr)) return new GhJobResult { Outcome = RefreshOutcome.RateLimited };
        if (!GitHubParser.IsNoPr(pr.Stderr)) return new GhJobResult { Outcome = RefreshOutcome.Failed };

        var runs = ToolRunner.Run(["gh", "run", "list", "--branch=" + branch, "--limit", "1", "--json", "status,conclusion,url,name,databaseId"], root, GhTimeoutMs);
        if (runs.TimedOut) return new GhJobResult { Outcome = RefreshOutcome.TimedOut };
        if (!runs.Ok) return new GhJobResult { Outcome = GitHubParser.IsRateLimited(runs.Stderr) ? RefreshOutcome.RateLimited : RefreshOutcome.Failed };
        var list = GitHubParser.ParseRuns(runs.Stdout);
        if (list is null) return new GhJobResult { Outcome = RefreshOutcome.Failed };
        return new GhJobResult { Status = GitHubParser.FromRun(list.Count > 0 ? list[0] : null) };
    }

    private void GhDone(GhInfo info, GhJobResult? r)
    {
        long now = Now;
        RefreshPolicy.Completed(ref info.State, now, r?.Outcome ?? RefreshOutcome.Failed, RefreshTiming.GitHub);
        if (r is not { Status: { } status, Outcome: RefreshOutcome.Ok }) return; // keep the last known state
        var old = info.Status;
        if (Equals(old, status)) return;
        info.Status = status;
        info.Version++;
        if (old is { Checks: CheckState.Pending } && status.Checks is CheckState.Pass or CheckState.Fail && LookingElsewhere(info))
            ToastChecks(info, status);
    }

    /// <summary>The window is in the background, or the focused pane is on another repository/branch.</summary>
    private bool LookingElsewhere(GhInfo info) => !window.IsFocused || !ReferenceEquals(tracks.Of(window.Focused)?.Gh, info);

    private void ToastChecks(GhInfo info, GitHubStatus s)
    {
        bool pass = s.Checks == CheckState.Pass;
        string what = s.HasPr ? $"#{s.PrNumber} {s.PrTitle}" : $"{s.RunName ?? "Workflow"} on {info.Branch}";
        string? url = s.HasPr ? s.PrUrl + "/checks" : s.RunUrl;
        window.ToastWithAction(pass ? ToastKind.Success : ToastKind.Error, pass ? "CI passed" : "CI failed", $"{info.Slug} · {what}",
            "Open", url is null ? null : () => OpenUrl(url));
        window.Redraw();
    }

    private void OpenUrl(string url)
    {
        if (!GitHubParser.IsGitHubUrl(url) && LinkOpener.Normalize(url) is null) return;
        Clipboard.OpenLink(window, url);
    }

    /// <summary>
    /// argv that runs <paramref name="argv"/> and then waits for Enter, so a command that prints and exits (gh pr
    /// checks) stays readable. The command is passed as sh's positional parameters, never inside the script text.
    /// </summary>
    private static string[] Held(params string[] argv) =>
        ["sh", "-c", "\"$@\"; s=$?; printf '\\n\\033[2m[exit %s · press Enter to close]\\033[0m' \"$s\"; read -r _", "sh", .. argv];

    // ---- Watching a run ------------------------------------------------------------------------

    /// <summary>`gh run watch ID --exit-status` in a split; its exit status comes back as a user var (no polling gh).</summary>
    private void WatchRun(GhInfo info)
    {
        long id = info.Status?.RunId ?? 0;
        // The wrapper reports the exit status (0/1, base64) through OSC 1337 SetUserVar, then waits for Enter.
        const string script = "\"$@\"; s=$?; if [ \"$s\" -eq 0 ]; then v=MA==; else v=MQ==; fi; " +
            "printf '\\033]1337;SetUserVar=" + RunExitVar + "=%s\\007' \"$v\"; " +
            "printf '\\n\\033[2m[gh run watch exited with %s · press Enter to close]\\033[0m' \"$s\"; read -r _";
        string[] gh = id > 0 ? ["gh", "run", "watch", id.ToString(), "--exit-status"] : ["gh", "run", "watch", "--exit-status"];
        int before = Pane.PeekNextId();
        launches.SplitRun(info.Root, ["sh", "-c", script, "sh", .. gh]);
        string? url = id > 0 ? $"https://github.com/{info.Slug}/actions/runs/{id}" : info.Status?.RunUrl;
        if (window.Layout.LeafList.Any(p => p.Id == before)) _runWatches[before] = (info.Slug, id, url);
    }

    /// <summary>Scan hook: a watched run's wrapper reported its exit status.</summary>
    public void CheckRunWatches()
    {
        if (_runWatches.Count == 0) return;
        foreach (var (paneId, watch) in _runWatches.ToList())
        {
            var pane = window.FindPane(paneId);
            if (pane is null) { _runWatches.Remove(paneId); continue; }
            string? exit;
            lock (gate) exit = pane.Term.UserVars.TryGetValue(RunExitVar, out var v) ? v : null;
            if (exit is not ("0" or "1")) continue;
            _runWatches.Remove(paneId);
            bool pass = exit == "0";
            string? url = watch.Url;
            window.ToastWithAction(pass ? ToastKind.Success : ToastKind.Error, pass ? "CI passed" : "CI failed",
                $"{watch.Slug}{(watch.RunId > 0 ? $" · run {watch.RunId}" : "")}", "Open", url is null ? null : () => OpenUrl(url));
            foreach (var info in _ghInfos.Values)
                if (info.Slug == watch.Slug) { info.State.BlockedUntil = 0; RefreshPolicy.Request(ref info.State); }
            window.Redraw();
        }
    }

    // ---- Issues --------------------------------------------------------------------------------

    /// <summary>#123 → `gh browse 123 -n` (prints the URL; gh knows whether it's an issue or a PR) → the browser.</summary>
    public void OpenIssue(int number, string root)
    {
        if (number <= 0 || !ToolRunner.Has("gh")) return;
        jobs.Run(() =>
        {
            var r = ToolRunner.Run(["gh", "browse", number.ToString(), "--no-browser"], root, GhTimeoutMs);
            return r.Ok ? r.Stdout.Trim() : "";
        }, url =>
        {
            if (url is not null && GitHubParser.IsGitHubUrl(url)) Clipboard.OpenLink(window, url);
            else window.Toast(ToastKind.Error, $"Couldn't open #{number}", "gh browse didn't return a github.com URL");
        }, wakeLoop: true);
    }

    /// <summary>
    /// Debug script step <c>ghfake:pending|pass|fail</c>: feeds a made-up result for the first known repo/branch through
    /// the normal result path (badge, spinner, transition toast), for screenshots without waiting on real CI.
    /// </summary>
    public bool RunScriptStep(string kind, string arg)
    {
        if (kind != "ghfake") return false;
        if (_ghInfos.Values.FirstOrDefault() is not { } info) return true;
        var checks = arg switch { "pass" => CheckState.Pass, "fail" => CheckState.Fail, _ => CheckState.Pending };
        var baseline = info.Status ?? new GitHubStatus { PrNumber = 1, PrTitle = "Demo" };
        info.State.InFlight = true;
        GhDone(info, new GhJobResult
        {
            Status = baseline with { Checks = checks, ChecksPending = checks == CheckState.Pending ? 2 : 0, ChecksFailed = checks == CheckState.Fail ? 1 : 0 },
        });
        return true;
    }

    // ---- Palette -------------------------------------------------------------------------------

    /// <summary>Palette hook: GitHub entries for the focused pane's repository and branch.</summary>
    public void AddCommands(List<Command> list, PaneTrack? tr)
    {
        if (!Enabled || tr?.Gh is not { } info) return;
        var s = info.Status;
        void Gh(string id, string title, int icon, Action run) =>
            list.Add(new Command { Id = "github:" + id, Category = "GitHub", Title = title, Icon = icon, Run = run });
        if (s is { HasPr: true, PrUrl: { } prUrl })
        {
            Gh("open-pr", $"Open PR #{s.PrNumber} in Browser", RepoBadge.PullRequestIcon, () => OpenUrl(prUrl));
            Gh("checks", "View Checks (in a split)", ShellIcons.Pass, () => launches.SplitRun(info.Root, Held("gh", "pr", "checks")));
        }
        else if (s is { RunId: > 0 })
            Gh("checks", "View Latest Run (in a split)", ShellIcons.Pass, () => launches.SplitRun(info.Root, Held("gh", "run", "view", s.RunId.ToString())));
        string watchTitle = "Watch Run… (in a split)";
        if (s is { RunId: > 0 }) watchTitle = $"Watch Run{(s.RunName is { } n ? ": " + n : "")} (in a split)";
        Gh("watch", watchTitle,
            Icons.Eye, () => WatchRun(info));
        Gh("open-repo", $"Open {info.Slug} in Browser", RepoBadge.GitHubIcon, () => OpenUrl($"https://github.com/{info.Slug}"));
        if (s is { RunUrl: { } runUrl }) Gh("open-run", "Open Latest Run in Browser", RepoBadge.GitHubIcon, () => OpenUrl(runUrl));
        Gh("refresh", "Refresh PR & Checks", Icons.Refresh, () => { info.State.BlockedUntil = 0; RefreshPolicy.Request(ref info.State); tracks.ScanSoon(); });
    }
}
