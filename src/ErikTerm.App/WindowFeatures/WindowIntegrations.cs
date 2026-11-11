using ErikTerm.App.Integrations;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.WindowFeatures;

/// <summary>What the repo pills draw with, from the window's frame (no calls back into the window while drawing).</summary>
internal readonly record struct RepoPillFrame(DrawList Dl, Theme Theme, bool GitStatus, bool AgentPills, bool ZoomBadge, bool WindowFocused);

/// <summary>
/// Developer-tool integrations, the shared part: per-pane tracking (<see cref="PaneTracks"/>: directory, finished
/// commands, focus), the scan that drives git status (<see cref="WindowGit"/>), GitHub (<see cref="WindowGitHub"/>), the
/// merge/rebase bar (<see cref="WindowGitOperations"/>) and dev containers (<see cref="WindowContainers"/>), and the
/// badge each pane gets from them: a compact one in its tab and a pill in its top-right corner.
///
/// Nothing here runs on the render path: a scan every 250 ms compares a few fields per pane (no allocation when
/// nothing changed), jobs run on the thread pool (<see cref="BackgroundJobs"/>), and a result only asks for a frame
/// when what's shown changed. The per-frame hooks take what they need as arguments.
/// </summary>
internal sealed class WindowIntegrations(
    IFeatureWindow window, BackgroundJobs jobs, PaneTracks tracks, WindowGit git, WindowGitHub github, WindowGitOperations gitOps,
    WindowContainers containers, WindowEditor editor)
{
    private readonly Dictionary<int, RectF> _pillRects = [];
    private long _pruneAt;
    private Pane? _trackedFocus;
    private bool _trackedWindowFocus = true;
    private int _badgeEpoch;           // bumped when settings change every badge (WindowGitHub.AuthEpoch: gh availability)
    private long _spinnerFrameAt;
    private object? _integrationSettings; // the Settings instance the badges were built with

    private const int ScanMs = 250, PillMs = 4000;

    private int BadgeEpoch => _badgeEpoch + github.AuthEpoch;

    // ==== Hooks ================================================================================

    /// <summary>Tick hook (main thread, once per loop iteration).</summary>
    public void Tick(long now, Settings settings, Pane focused, bool windowFocused, bool tabBarShown, float mouseX, float mouseY)
    {
        jobs.Drain();
        if (!ReferenceEquals(settings, _integrationSettings)) { _integrationSettings = settings; _badgeEpoch++; editor.SettingsChanged(); } // config reloaded
        bool focusMoved = !ReferenceEquals(_trackedFocus, focused) || _trackedWindowFocus != windowFocused;
        if (now >= tracks.ScanAt || focusMoved)
        {
            tracks.ScanAt = now + ScanMs;
            Scan(now);
        }
        TickPills(now, settings.GitStatus, mouseX, mouseY, settings.AnimationDurationMs);
        // Pending CI: the spinner steps ten times a second while it is on screen and the window has focus.
        if (windowFocused && now >= _spinnerFrameAt && SpinnerVisible(tabBarShown, settings.GitStatus, focused))
        {
            _spinnerFrameAt = now + 100;
            window.Redraw();
        }
    }

    /// <summary>Event-loop hook: how long it may sleep (results to collect, a pill to fade, a spinner step).</summary>
    public int WakeMs(long now, bool windowFocused, bool tabBarShown, bool gitStatus, Pane focused)
    {
        int ms = int.MaxValue;
        if (jobs.Busy) ms = 60;
        foreach (var tr in tracks.All.Values)
            if (tr.Pill.Target > 0 && tr.PillUntil > now) ms = (int)Math.Min(ms, tr.PillUntil - now + 1);
        if (windowFocused && SpinnerVisible(tabBarShown, gitStatus, focused)) ms = (int)Math.Min(ms, Math.Max(1, _spinnerFrameAt - now));
        return ms;
    }

    /// <summary>IsAnimating hook.</summary>
    public bool IsAnimating(long now)
    {
#pragma warning disable S3267 // IsAnimating runs every frame: no LINQ iterator or closure here.
        foreach (var tr in tracks.All.Values) if (tr.Pill.IsAnimating(now)) return true;
#pragma warning restore S3267
        return false;
    }

    // ==== Scan =================================================================================

    private void Scan(long now)
    {
        var focused = window.Focused;
        bool windowFocused = window.IsFocused;
        bool focusMoved = !ReferenceEquals(_trackedFocus, focused);
        var previousRepo = tracks.Of(_trackedFocus)?.Repo;
        bool regained = windowFocused && !_trackedWindowFocus;
        _trackedFocus = focused;
        _trackedWindowFocus = windowFocused;

        containers.Track(now); // before the directories, which follow a pane into its container
        TrackAllPanes(now);
        if ((focusMoved || regained) && focused is not null) NoteFocusedPane(focused, focusMoved, previousRepo, now);
        tracks.DropDead();
        if (now >= _pruneAt)
        {
            _pruneAt = now + 10_000;
            git.Prune();
            github.Prune();
        }
        gitOps.Tick(now);               // merge/rebase bar
        gitOps.TickConflictChecks(now); // tab conflict warning
        git.Schedule(now);
        github.Schedule(now);
        github.CheckRunWatches();
    }

    private void TrackAllPanes(long now)
    {
        foreach (var tr in tracks.All.Values) tr.Live = false;
        var tabs = window.Tabs;
        var items = tabs.Items;
        for (int i = 0; i < items.Count; i++)
        {
            bool active = ReferenceEquals(items[i], tabs.Active);
            var leaves = (active ? window.Layout : items[i].Layout).LeafList;
            for (int j = 0; j < leaves.Count; j++) TrackPane(leaves[j], now, active);
        }
        if (tabs.Count == 0) { var leaves = window.Layout.LeafList; for (int j = 0; j < leaves.Count; j++) TrackPane(leaves[j], now, true); }
    }

    private void NoteFocusedPane(Pane focused, bool focusMoved, RepoInfo? previousRepo, long now)
    {
        var tr = tracks.Track(focused);
        tr.FocusedAt = now;
        gitOps.FocusMoved(); // a merge or rebase may have started while the focus was elsewhere
        RequestRefresh(tr);
        // The pill says something new only when focus moved to another repository.
        if (focusMoved && !ReferenceEquals(previousRepo, tr.Repo)) FlashPill(tr, now);
    }

    private void TrackPane(Pane pane, long now, bool visible)
    {
        var tr = tracks.Track(pane);
        tr.Live = true;
        if (!window.Settings.GitStatus)
        {
            if (tr.Badge is not null) { tr.Badge = null; tr.Repo = null; tr.RepoVersion = -1; if (visible) window.Redraw(); }
            tr.Cwd = null;
            tr.OscSeen = false;
            return;
        }

        UpdateTrackedDirectory(pane, tr, now);

        // A command finished (OSC 133 D, or a new prompt): it may have changed the tree, pushed, checked out…
        long started = pane.Term.CommandsStarted;
        if (started != tr.CommandsSeen && !pane.Term.CommandRunning)
        {
            if (tr.CommandsSeen >= 0) RequestRefresh(tr);
            tr.CommandsSeen = started;
        }

        UpdateBadge(pane, tr, visible, now);
    }

    private void UpdateTrackedDirectory(Pane pane, PaneTrack tr, long now)
    {
        // Directory: OSC 7 (a new string instance each prompt), else /proc every few seconds.
        string? uri = pane.Term.WorkingDirectoryUri;
        string? cwd = tr.Cwd;
        if (!tr.OscSeen || !ReferenceEquals(uri, tr.OscUri))
        {
            tr.OscUri = uri;
            tr.OscSeen = true;
            tr.CwdPolledAt = now;
            cwd = PaneTracks.PaneDirectory(pane);
        }
        else if (uri is null && now - tr.CwdPolledAt >= 3000)
        {
            tr.CwdPolledAt = now;
            cwd = PaneTracks.PaneDirectory(pane); // no OSC 7: /proc
        }
        if (cwd != tr.Cwd)
        {
            tr.Cwd = cwd;
            tr.Repo = cwd is null ? null : git.RepoFor(cwd);
            if (tr.Repo is { } repo) RefreshPolicy.Request(ref repo.State);
            if (pane == window.Focused) FlashPill(tr, now);
        }
    }

    /// <summary>Asks for fresh git (and GitHub) status for the pane's repository.</summary>
    private static void RequestRefresh(PaneTrack tr)
    {
        if (tr.Repo is { } repo) RefreshPolicy.Request(ref repo.State);
        if (tr.Gh is { } gh) RefreshPolicy.Request(ref gh.State);
    }

    // ==== Badge ================================================================================

    /// <summary>Recomputes the pane's badge when its repo or GitHub state changed (cheap version checks otherwise).</summary>
    private void UpdateBadge(Pane pane, PaneTrack tr, bool visible, long now)
    {
        var repo = tr.Repo;
        bool changed = false;
        int version = repo?.Version ?? -1;
        int epoch = BadgeEpoch;
        if (version != tr.RepoVersion || tr.Epoch != epoch)
        {
            tr.RepoVersion = version;
            tr.Epoch = epoch;
            tr.Gh = github.InfoFor(repo);
            changed = true;
        }
        int ghVersion = tr.Gh?.Version ?? -1;
        if (ghVersion != tr.GhVersion) { tr.GhVersion = ghVersion; changed = true; }
        if (!changed) return;

        var model = RepoBadgeModel.From(repo?.Status, tr.Gh?.Status);
        if (model is not null && repo is not null) model = model with { RepoName = RepoNameOf(repo) };
        if (model is not null && gitOps.PredictsConflict(repo?.Root)) model = model with { WouldConflict = true };
        if (Equals(model, tr.Badge)) return;
        bool appeared = tr.Badge is null;
        tr.Badge = model;
        if (visible) window.Redraw();
        if (model is not null && pane == window.Focused && (appeared || tr.Pill.Target > 0)) FlashPill(tr, now);
    }

    /// <summary>
    /// The name to show for a repository: the GitHub repo's, which a worktree shares with its main checkout, else
    /// the work tree root's directory.
    /// </summary>
    private static string RepoNameOf(RepoInfo repo)
    {
        if (repo.Slug is { } slug && slug.LastIndexOf('/') is var i and >= 0 && i < slug.Length - 1) return slug[(i + 1)..];
        return repo.Root is { } root ? WorktreeLauncher.RepoName(root) : "";
    }

    /// <summary>Palette hook: Git and GitHub entries for the focused pane's repository.</summary>
    public void AddCommands(List<Command> list)
    {
        var tr = tracks.Of(window.Focused);
        git.AddCommands(list, tr);
        github.AddCommands(list, tr);
    }

    /// <summary>The badge of a tab: its focused pane's (<paramref name="focused"/>; no allocation, called while drawing the tab bar).</summary>
    public RepoBadgeModel? TabBadge(Pane? focused, bool gitStatus) => gitStatus ? tracks.Of(focused)?.Badge : null;

    // ==== Pane pill ============================================================================

    /// <summary>Shows the pane's repo pill for a few seconds (focus, directory or status changed).</summary>
    private static void FlashPill(PaneTrack tr, long now)
    {
        if (tr.Badge is null && tr.Repo?.Status is null) return;
        tr.PillUntil = now + PillMs;
    }

    private void TickPills(long now, bool gitStatus, float mouseX, float mouseY, float anim)
    {
        foreach (var (id, tr) in tracks.All)
        {
            bool show = tr.Badge is not null && now < tr.PillUntil && gitStatus;
            // Keep it while the pointer rests on it, so it can be read.
            if (!show && tr.Pill.Target > 0 && tr.Badge is not null && _pillRects.TryGetValue(id, out var r) && r.Contains(mouseX, mouseY))
            {
                show = true;
                tr.PillUntil = now + 600;
            }
            float want = show ? 1 : 0;
#pragma warning disable S1244 // Target only ever holds a value set here, so exact comparison is the point.
            if (tr.Pill.Target != want) tr.Pill.Set(want, now, anim * (show ? 1.2f : 2.5f));
#pragma warning restore S1244
        }
    }

    private bool SpinnerVisible(bool tabBarShown, bool gitStatus, Pane focused)
    {
        if (tabBarShown)
        {
            var tabs = window.Tabs;
            var items = tabs.Items;
            for (int i = 0; i < items.Count; i++)
                if (RepoBadge.Spins(TabBadge(ReferenceEquals(items[i], tabs.Active) ? focused : items[i].Focused, gitStatus))) return true;
        }
        foreach (var tr in tracks.All.Values)
            if (tr.Pill.Target > 0 && RepoBadge.Spins(tr.Badge)) return true;
        return false;
    }

    /// <summary>The repo pill in each visible pane's top-right corner (while it is flashed or hovered).</summary>
    public void DrawPills(in RepoPillFrame f, List<Pane> visible, long now)
    {
        _pillRects.Clear();
        if (!f.GitStatus) return;
        var th = f.Theme;
        foreach (var pane in visible)
        {
            if (tracks.Of(pane) is not { Badge: { } badge } tr) continue;
            float a = tr.Pill.Value(now) * pane.Appear.Value(now);
            if (a <= 0.001f) continue;
            RectF pr = pane.Rect.Value(now);
            float margin = th.S(8);
            float slide = (1 - Ease.OutCubic(a)) * th.S(-6);
            float right = RepoPillRight(f, pane, pr) - margin;
            float w = RepoBadge.DrawPill(f.Dl, th, badge, right, pr.Y + margin + slide, pr.W - margin * 2, a, now, f.WindowFocused);
            if (w > 0) _pillRects[pane.Id] = new RectF(right - w, pr.Y + margin, w, RepoBadge.PillHeight(th));
        }
    }

    /// <summary>
    /// Right edge for the repo pill (the caller keeps its margin from it): the pane's, or left of the agent-status pill
    /// when that is shown in the same top-right corner (Agents/WindowAgents.cs / AgentBadge).
    /// </summary>
    private static float RepoPillRight(in RepoPillFrame f, Pane pane, RectF paneRect)
    {
        if (!f.AgentPills) return paneRect.Right;
        long now = Environment.TickCount64;
        var th = f.Theme;
        if (AgentBadge.PillBounds(f.Dl, th, pane.Agent, paneRect, f.ZoomBadge, now) is not { } agent) return paneRect.Right;
        float margin = th.S(8), top = paneRect.Y + margin;
        if (agent.Y >= top + RepoBadge.PillHeight(th)) return paneRect.Right; // pushed below the zoom badge: no overlap
        return agent.X - th.S(6) + margin;
    }
}
