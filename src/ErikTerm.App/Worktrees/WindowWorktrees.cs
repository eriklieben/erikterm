using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Notifications;
using ErikTerm.App.Native;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Worktrees;

/// <summary>
/// Git worktrees for one window: New Agent Worktree (a form, then <c>git worktree add</c> in the background and a new
/// colored tab or split that runs the agent there), the switcher, Finish Worktree and Clean Up. The rules live in
/// ErikTerm.Ui (WorktreeLauncher.cs and the Worktrees folder), git in Integrations/WorktreeGit.cs and
/// Integrations/WorktreeManagerGit.cs; repository changes go through <see cref="RepoPlan"/> in a visible split.
/// Row status is gathered off the main thread, time-limited and cached, so a repository with many worktrees never
/// costs a frame.
/// <para>
/// A prompt and its arguments belong to one launch. The pane's LaunchArgv is the shell, so Restart Pane never runs the
/// agent again, and Pane.LaunchedAgent keeps it out of the saved session even when restore_programs lists it. The
/// pane's programs follow <see cref="WorktreeLaunchPlan"/> (setup, agent, shell).
/// </para>
/// TermWindow forwards its hook points here: keys, text, mouse, tick, drawing, actions, the palette and a launch's
/// program exiting.
/// </summary>
internal sealed class WindowWorktrees<TPane>(IWorktreeWindow<TPane> window, IWorktreeProcess<TPane> process, Lock gate, Func<List<string>, Dictionary<string, List<AspireDashboard>>>? dashboards = null)
    where TPane : class, IWorktreePane
{
    private const int WorktreeStatusFreshMs = 4000, WorktreeRowsRefreshMs = 1000, DashboardsFreshMs = 5000;

    private readonly Func<List<string>, Dictionary<string, List<AspireDashboard>>> _readDashboards = dashboards ?? AspireCli.DashboardsByWorktree;

    private readonly AgentWorktreeOverlay _launchOverlay = new();
    private readonly WorktreesOverlay _listOverlay = new();
    private readonly FinishWorktreeOverlay _finishOverlay = new();
    private WorktreeSession? _session;
    private long _rowsAt;

    private sealed class WorktreeSession
    {
        public required WorktreeInventory Inventory;
        public required WorktreeOverlaps Overlaps;
        public bool Cleanup;
        public readonly Dictionary<string, (WorktreeRowStatus? Status, long At, bool InFlight)> Status = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (LandedVerdict Verdict, bool Done)> Landed = new(StringComparer.Ordinal);
        public Dictionary<string, List<AspireDashboard>> Dashboards = new(StringComparer.Ordinal);
        public long DashboardsAt;
        public bool DashboardsInFlight;
    }

    private sealed record WorktreeProbe(WorktreeRepo? Repo, string? Error, List<WorktreeAgent> Agents, PreferencesFile Preferences);
    private sealed record WorktreeResult(string? Error);

    private static long Now => Environment.TickCount64;

    /// <summary>A worktree overlay has the input: the window's other overlays and its panes don't see it.</summary>
    public bool AnyOpen => _launchOverlay.IsOpen || _listOverlay.IsOpen || _finishOverlay.IsOpen;

    public bool IsAnimating(long now) => _launchOverlay.IsAnimating(now) || _listOverlay.IsAnimating(now) || _finishOverlay.IsAnimating(now);

    /// <summary>Palette hook: New Agent Worktree… (outside a repository the action says so), then the Git entries.</summary>
    public void AddCommands(List<Command> list)
    {
        if (ActionSpec.TryParse("NewAgentWorktree", out var agent, out _))
            list.Add(new Command { Id = "NewAgentWorktree", Category = "Agents", Title = "New Agent Worktree…", Icon = Icons.Sparkle, Action = agent, Shortcut = window.ShortcutFor(agent) });
        if (!ToolRunner.Has("git")) return;
        foreach (var (id, title, icon) in new[]
                 {
                     ("ShowWorktrees", "Git: Worktrees…", RepoBadge.BranchIcon),
                     ("FinishWorktree", "Git: Finish Worktree…", RepoBadge.PullRequestIcon),
                     ("CleanUpWorktrees", "Git: Clean Up Worktrees…", Icons.Clear),
                 })
        {
            if (!ActionSpec.TryParse(id, out var spec, out _)) continue;
            list.Add(new Command { Id = id, Category = "Git", Title = title[5..], Icon = icon, Action = spec, Shortcut = window.ShortcutFor(spec) });
        }
    }

    /// <summary>The focused pane's directory, for the worktree commands.</summary>
    public string? FocusedCwd()
    {
        if (window.Focused.StartDirectory(process.Settings.DefaultCwd) is { Length: > 0 } cwd && Directory.Exists(cwd)) return cwd;
        window.Toast(ToastKind.Error, "No directory to look for a repository in", "The focused pane doesn't report one");
        return null;
    }

    /// <summary>Hook from Execute: ShowWorktrees / CleanUpWorktrees / FinishWorktree.</summary>
    public void Execute(string name)
    {
        switch (name)
        {
            case "ShowWorktrees": OpenWorktreeList(cleanup: false); break;
            case "CleanUpWorktrees": OpenWorktreeList(cleanup: true); break;
            case "FinishWorktree": OpenFinish(null); break;
        }
    }

    /// <summary>Hook from Tick (open only): statuses refresh in the background, rows follow panes and agents.</summary>
    public void Tick(long now)
    {
        if (!_listOverlay.IsOpen || now < _rowsAt) return;
        RefreshWorktreeRows();
    }

    /// <summary>Hook from App.PanesByWorktree, after the window synced its active tab: panes in this window, by the worktree they're in.</summary>
    public void CollectPanes(Dictionary<string, List<WorktreePaneRef<TPane>>> into, List<string> roots)
    {
        bool ignoreCase = OperatingSystem.IsWindows();
        for (int t = 0; t < window.TabCount; t++)
        {
            if (window.TabLayout(t) is not { } layout) continue;
            foreach (var pane in layout.LeafList)
            {
                if (window.IntegrationCwd(pane) is not { Length: > 0 } cwd || WorktreeList.Owner(cwd, roots, ignoreCase) is not { } owner) continue;
                if (!into.TryGetValue(owner, out var list)) into[owner] = list = [];
                list.Add(new WorktreePaneRef<TPane>(pane, pane.AgentState, pane.AgentActive, pane.AgentLabel));
            }
        }
    }

    // ---- Input and drawing ------------------------------------------------------------------------

    /// <summary>Hook from OnKeyDown. True: an overlay took the key (text is suppressed unless a message is being edited).</summary>
    public bool KeyDown(uint key, ushort mod, long now)
    {
        if (_launchOverlay.IsOpen) { _launchOverlay.HandleKey(key, mod, now); return true; }
        if (_finishOverlay.IsOpen)
        {
            _finishOverlay.HandleKey(key, mod, now);
            window.SuppressText = !_finishOverlay.EditingMessage;
            window.Redraw();
            return true;
        }
        if (_listOverlay.IsOpen)
        {
            _listOverlay.HandleKey(key, now);
            window.SuppressText = true;
            window.Redraw();
            return true;
        }
        return false;
    }

    /// <summary>Hook from OnTextInput.</summary>
    public bool TextInput(string text, long now)
    {
        if (_launchOverlay.IsOpen) { _launchOverlay.HandleText(text, now); return true; }
        if (_finishOverlay.IsOpen) { _finishOverlay.HandleText(text, now); return true; }
        return _listOverlay.IsOpen;
    }

    public bool MouseDown(float x, float y, long now)
    {
        if (_launchOverlay.IsOpen) { _launchOverlay.HandleMouseDown(x, y, now); window.Redraw(); return true; }
        if (_finishOverlay.IsOpen) { _finishOverlay.HandleMouseDown(x, y, now); window.Redraw(); return true; }
        if (_listOverlay.IsOpen) { _listOverlay.HandleMouseDown(x, y, now); window.Redraw(); return true; }
        return false;
    }

    public bool MouseMove(float x, float y, long now)
    {
        if (_launchOverlay.IsOpen || _finishOverlay.IsOpen) { window.DefaultCursor(); return true; }
        if (_listOverlay.IsOpen) { _listOverlay.HandleMouseMove(x, y, now); window.DefaultCursor(); window.Redraw(); return true; }
        return false;
    }

    public void CloseOverlays(long now)
    {
        _launchOverlay.Close(now);
        _listOverlay.Close(now);
        _finishOverlay.Close(now);
    }

    /// <summary>Render hook: the worktree overlays, in the order they stack.</summary>
    public void DrawOverlays(DrawList dl, RectF area, long now)
    {
        _launchOverlay.Draw(dl, window.OverlayTheme, area, now);
        _listOverlay.Draw(dl, window.OverlayTheme, area, now);
        _finishOverlay.Draw(dl, window.OverlayTheme, area, now);
    }

    // ---- List: switcher and cleanup ------------------------------------------------------------

    private void OpenWorktreeList(bool cleanup)
    {
        if (FocusedCwd() is not { } cwd) return;
        long now = Now;
        window.CloseMenus(now);
        _listOverlay.Open(cleanup ? "Clean Up Worktrees" : "Worktrees", "", cleanup, window.Theme, now);
        WireWorktreeOverlay();
        window.RunInBackground(() =>
        {
            var inv = WorktreeManagerGit.Inventory(cwd, out string? error);
            return new InventoryResult(inv, error);
        }, r => ShowWorktreeList(r, cwd, cleanup), wake: true);
    }

    private sealed record InventoryResult(WorktreeInventory? Inventory, string? Error);

    private void ShowWorktreeList(InventoryResult? r, string cwd, bool cleanup)
    {
        if (r?.Inventory is not { } inv)
        {
            _listOverlay.Close(Now);
            window.Toast(ToastKind.Error, "Can't list worktrees", r?.Error ?? $"Not in a git repository: {cwd}");
            return;
        }
        if (!_listOverlay.IsOpen) return; // closed while reading
        _session = new WorktreeSession { Inventory = inv, Cleanup = cleanup, Overlaps = new WorktreeOverlaps((work, then) => window.RunInBackground(work, then), () => { if (_listOverlay.IsOpen) RefreshWorktreeRows(); }) };
        RefreshWorktreeRows();
        window.Redraw();
    }

    private void WireWorktreeOverlay()
    {
        if (_listOverlay.Activated is not null) return;
        _listOverlay.Activated = OnWorktreeActivated;
        _listOverlay.FinishRequested = row => OpenFinish(row.Entry.Path);
        _listOverlay.DashboardRequested = OpenDashboard;
        _listOverlay.NewRequested = () => { _listOverlay.Close(Now); OpenAgentForm(); };
        _listOverlay.CleanUpRequested = () => OpenWorktreeList(cleanup: true);
        _listOverlay.RefreshRequested = () =>
        {
            if (_session is not { } s) return;
            s.Status.Clear();
            s.Landed.Clear();
            s.DashboardsAt = 0;
            s.Overlaps.Forget();
            RefreshWorktreeRows();
        };
        _listOverlay.RemoveRequested = ConfirmCleanup;
    }

    private void RefreshWorktreeRows()
    {
        _rowsAt = Now + WorktreeRowsRefreshMs;
        if (_session is not { } s) return;
        var inv = s.Inventory;
        var panes = process.PanesByWorktree(inv.Entries.Select(e => e.Path).ToList());
        if (!s.Cleanup) RequestDashboards(s);
        var rows = new List<WorktreeRow>(inv.Entries.Count);
        foreach (var entry in inv.Entries)
        {
            RequestWorktreeStatus(s, entry);
            if (s.Cleanup && !entry.IsMain) RequestLanded(s, entry);
            panes.TryGetValue(entry.Path, out var inside);
            rows.Add(s.Cleanup ? CleanupRow(s, entry, inside) : SwitcherRow(s, entry, inside));
        }
        if (s.Cleanup)
        {
            rows.RemoveAll(r => r.Entry.IsMain);
            foreach (var row in rows) row.Checked = row.Selectable;
        }
        else
        {
            s.Overlaps.Refresh(inv.Entries, inv.TargetOid ?? inv.Entries[0].Head, e => s.Status.GetValueOrDefault(e.Path).Status, Now);
            s.Overlaps.Annotate(rows);
        }
        _listOverlay.SetRows(rows);
        window.Redraw();
    }

    private void RequestWorktreeStatus(WorktreeSession s, WorktreeEntry entry)
    {
        long now = Now;
        if (s.Status.TryGetValue(entry.Path, out var known) && (known.InFlight || now - known.At < WorktreeStatusFreshMs)) return;
        s.Status[entry.Path] = (known.Status, now, true);
        string? targetOid = s.Inventory.TargetOid;
        window.RunInBackground(() => WorktreeManagerGit.Status(entry, targetOid), status =>
        {
            s.Status[entry.Path] = (status ?? known.Status, Now, false);
            if (_listOverlay.IsOpen && ReferenceEquals(_session, s)) { RefreshWorktreeRows(); }
        });
    }

    private void RequestDashboards(WorktreeSession s)
    {
        if (s.DashboardsInFlight || Now - s.DashboardsAt < DashboardsFreshMs) return;
        s.DashboardsInFlight = true;
        var roots = s.Inventory.Entries.Select(e => e.Path).ToList();
        window.RunInBackground(() => _readDashboards(roots), found =>
        {
            s.DashboardsInFlight = false;
            s.DashboardsAt = Now;
            if (found is null) return;
            s.Dashboards = found;
            if (_listOverlay.IsOpen && ReferenceEquals(_session, s)) RefreshWorktreeRows();
        });
    }

    private void OpenDashboard(AspireDashboard dashboard)
    {
        if (window.OpenDashboardLink(dashboard.Url) is { } error) window.Toast(ToastKind.Error, "Couldn't open the dashboard", error);
    }

    private void RequestLanded(WorktreeSession s, WorktreeEntry entry)
    {
        if (s.Landed.ContainsKey(entry.Path) || entry.Head is not { Length: 40 } head || s.Inventory.Target is not { } target) return;
        s.Landed[entry.Path] = (LandedVerdict.Unknown, false);
        bool useGh = window.GitHubEnabled;
        var inv = s.Inventory;
        window.RunInBackground<object>(() => WorktreeManagerGit.Landed(inv.MainRoot, head, entry.Branch, target, inv.HasWriteTree, useGh), result =>
        {
            s.Landed[entry.Path] = (result is LandedVerdict v ? v : new LandedVerdict(false, LandedReason.None, "Couldn't check", target), true);
            if (_listOverlay.IsOpen && ReferenceEquals(_session, s)) RefreshWorktreeRows();
        });
    }

    internal static string StatusText(WorktreeRowStatus? st, GitHubStatus? gh, out bool warn)
    {
        warn = false;
        if (st is null) return "…";
        if (st.TimedOut) return "slow";
        var sb = new System.Text.StringBuilder();
        if (st.Ahead > 0) sb.Append('↑').Append(st.Ahead).Append(' ');
        if (st.Behind > 0) sb.Append('↓').Append(st.Behind).Append(' ');
        if (st.Changes > 0) { sb.Append("● ").Append(st.Changes).Append(' '); warn = true; }
        if (gh is { HasPr: true })
        {
            string prState = (gh.PrState, gh.Draft) switch
            {
                ("MERGED", _) => " merged ",
                (_, true) => " draft ",
                _ => " ",
            };
            sb.Append('#').Append(gh.PrNumber).Append(prState);
            sb.Append(gh.Checks switch { CheckState.Pass => "✓", CheckState.Fail => "✗", CheckState.Pending => "…", _ => "" });
        }
        string text = sb.ToString().TrimEnd();
        return text.Length > 0 ? text : "clean";
    }

    private GitHubStatus? KnownGitHub(WorktreeEntry entry) => entry.Branch is { } b ? process.KnownGitHubStatus(entry.Path, b) : null;

    private WorktreeRow SwitcherRow(WorktreeSession s, WorktreeEntry entry, List<WorktreePaneRef<TPane>>? inside)
    {
        s.Status.TryGetValue(entry.Path, out var st);
        string status = StatusText(st.Status, KnownGitHub(entry), out bool warn);
        AgentState agent = AgentState.None;
        string? agentName = null;
        if (inside is not null)
            (agent, agentName) = inside.Where(p => p.AgentActive).Aggregate((agent, agentName), (best, p) =>
                AgentStateText.Urgency(p.State) >= AgentStateText.Urgency(best.agent) ? (p.State, p.AgentName) : best);
        string where;
        if (agent != AgentState.None) where = $"{agentName}: {AgentStateText.Word(agent)}";
        else where = inside is null ? "no tab" : Counted.Of(inside.Count, "pane") + " open";
        return new WorktreeRow
        {
            Entry = entry,
            Title = entry.IsMain ? entry.Name + " (main)" : entry.Name,
            Branch = entry.BranchLabel,
            Detail = $"{FileRefResolver.HomeRelative(entry.Path)} · {where}",
            Status = status,
            Warn = warn,
            Agent = agent,
            Dashboards = s.Dashboards.GetValueOrDefault(entry.Path) ?? [],
        };
    }

    private WorktreeRow CleanupRow(WorktreeSession s, WorktreeEntry entry, List<WorktreePaneRef<TPane>>? inside)
    {
        s.Status.TryGetValue(entry.Path, out var st);
        s.Landed.TryGetValue(entry.Path, out var landed);
        bool agent = inside?.Any(p => p.AgentActive) == true;
        int changes = st.Status?.Changes ?? 0;
        var verdict = landed.Done ? landed.Verdict : LandedVerdict.Unknown;
        var block = !landed.Done || st.Status is null ? RemovalBlock.NotLanded : Landing.Block(entry, verdict, changes, agent, inside is not null);
        string detail = !landed.Done ? "Checking…" : Landing.Explain(block, entry, verdict, changes);
        return new WorktreeRow
        {
            Entry = entry,
            Title = entry.Name,
            Branch = entry.BranchLabel,
            Detail = detail,
            Status = (block, landed.Done) switch
            {
                (RemovalBlock.None, _) => "landed",
                (_, true) => "kept",
                _ => "",
            },
            Warn = block is RemovalBlock.Dirty or RemovalBlock.AgentRunning,
            Selectable = block == RemovalBlock.None,
            Checked = block == RemovalBlock.None,
        };
    }

    private void OnWorktreeActivated(WorktreeRow row)
    {
        _listOverlay.Close(Now);
        if (process.JumpToWorktree(row.Entry.Path, _session?.Inventory.Entries.Select(e => e.Path).ToList() ?? [row.Entry.Path])) return;
        window.OpenShellTab(row.Entry.Path, row.Entry.Branch);
    }


    // ---- Removal ---------------------------------------------------------------------------------

    private void ConfirmCleanup()
    {
        if (_session is not { Cleanup: true } s) return;
        var picked = _listOverlay.CheckedRows.ToList();
        if (picked.Count == 0) return;
        var inv = s.Inventory;
        string? target = inv.Target;
        var lines = new List<string>();
        var commands = new List<string>();
        var steps = new List<RepoStep>();
        foreach (var e in picked.Select(row => row.Entry))
            AppendRemoval(inv, e, s.Landed[e.Path].Verdict, target!, lines, commands, steps);
        var plan = new RepoPlan("Clean up", steps);
        ShowRemovalConfirm(plan, lines, commands, $"Remove {Counted.Of(picked.Count, "worktree")}", picked.Select(r => r.Entry.Path).ToList());
        _listOverlay.Close(Now);
    }

    /// <summary>Adds what removing <paramref name="e"/> means: lines for the confirmation, commands to preview, the guarded steps.</summary>
    private void AppendRemoval(WorktreeInventory inv, WorktreeEntry e, LandedVerdict verdict, string target, List<string> lines, List<string> commands, List<RepoStep> steps)
    {
        bool useGh = window.GitHubEnabled;
        string head = e.Head ?? "";
        string shortHead = head.Length >= 7 ? head[..7] : head;
        string root = inv.MainRoot;
        bool writeTree = inv.HasWriteTree;
        lines.Add($"Remove worktree {e.Name} at {shortHead}  ({FileRefResolver.HomeRelative(e.Path)})");
        lines.Add($"  {verdict.Why}");
        var removeArgv = WorktreeCommands.RemoveWorktree(root, e.Path);
        steps.Add(new RepoStep(removeArgv, root, WorktreeCommands.Display(removeArgv), () => WorktreeManagerGit.VerifyRemoval(e, target, writeTree, useGh, root)));
        commands.Add(WorktreeCommands.Display(removeArgv));

        var deletion = Landing.Deletion(e, verdict, inv.Entries[0].Branch == target);
        if (deletion == BranchDeletion.None) return;
        string branch = e.Branch!;
        var argv = WorktreeCommands.DeleteBranch(root, branch, landedCheckPassed: deletion == BranchDeletion.AfterLandedCheck);
        Func<string?>? guard = deletion == BranchDeletion.AfterLandedCheck ? () => WorktreeManagerGit.VerifyBranchDelete(root, branch, head, target, writeTree, useGh) : null;
        steps.Add(new RepoStep(argv, root, WorktreeCommands.Display(argv), guard));
        commands.Add(WorktreeCommands.Display(argv));
        lines.Add(deletion == BranchDeletion.AfterLandedCheck ? $"Delete branch {branch} at {shortHead} with -D, only after a fresh landed check of that commit" : $"Delete branch {branch} at {shortHead}");
    }

    private void ShowRemovalConfirm(RepoPlan plan, List<string> lines, List<string> commands, string title, List<string> paths)
    {
        WorktreeConfirm? confirm = null;
        confirm = new WorktreeConfirm
        {
            Title = title,
            Detail = "Removes only what has landed",
            ConfirmLabel = "remove in a visible split",
            Lines = lines,
            Commands = commands,
            Danger = true,
            Confirmed = () =>
            {
                if (confirm is null) return;
                // An agent may have started since the list was read.
                if (process.PanesByWorktree(paths).Values.Any(panes => panes.Any(p => p.AgentActive)))
                {
                    confirm.Error = "An agent is running in that worktree now: stop it first";
                    window.Redraw();
                    return;
                }
                confirm.Busy = true;
                window.StartRepoPlan(plan, error => { confirm.Busy = false; confirm.Error = error; window.Redraw(); }, () => _finishOverlay.Close(Now));
            },
        };
        _finishOverlay.Open(confirm, window.Theme, Now);
        window.Redraw();
    }

    // ---- Finish ---------------------------------------------------------------------------------

    /// <param name="path">The worktree to finish; null: the focused pane's.</param>
    public void OpenFinish(string? path)
    {
        string? cwd = path is not null && Directory.Exists(path) ? path : FocusedCwd();
        if (cwd is null) return;
        long now = Now;
        window.CloseMenus(now);
        bool hasGh = window.GitHubSignedIn;
        window.RunInBackground(() =>
        {
            var inv = WorktreeManagerGit.Inventory(cwd, out string? error);
            if (inv is null) return new FinishProbe(null, null, null, error);
            string? owner = path ?? WorktreeManagerGit.OwnerOf(inv, cwd);
            var entry = owner is null ? null : inv.Entries.FirstOrDefault(e => e.Path == owner);
            if (entry is null) return new FinishProbe(inv, null, null, "This directory isn't in a worktree of the repository");
            var facts = WorktreeManagerGit.Facts(inv, entry, hasGh, ExistingPrFor(entry), out error);
            return new FinishProbe(inv, facts, entry, error, facts?.Subjects);
        }, ShowFinishWorktree, wake: true);
    }

    private sealed record FinishProbe(WorktreeInventory? Inventory, FinishFacts? Facts, WorktreeEntry? Entry, string? Error, IReadOnlyList<string>? Messages = null);

    private sealed record RemovalProbe(WorktreeInventory Inventory, WorktreeEntry Entry, LandedVerdict Verdict, int Changes);

    private int ExistingPrFor(WorktreeEntry entry)
    {
        var known = KnownGitHub(entry);
        return known is { HasPr: true, PrState: not "MERGED" and not "CLOSED" } ? known.PrNumber : 0;
    }

    private void ShowFinishWorktree(FinishProbe? p)
    {
        if (p is not { Facts: { } facts, Inventory: { } inv, Messages: { } messages })
        {
            window.Toast(ToastKind.Error, "Can't finish this worktree", p?.Error ?? "Reading the repository failed");
            return;
        }
        var panes = process.PanesByWorktree(inv.Entries.Select(e => e.Path).ToList());
        bool agentHere = panes.TryGetValue(facts.Worktree, out var here) && here.Any(r => r.AgentActive);
        bool agentMain = panes.TryGetValue(inv.Entries[0].Path, out var main) && main.Any(r => r.AgentActive);
        facts = facts with { AgentRunning = agentHere, AgentInMain = agentMain };
        var remembered = FinishRules.ParseOption(RememberedFinishOption(inv.MainRoot));
        var form = new FinishForm(facts, remembered, messages);
        long now = Now;
        _listOverlay.Close(now);
        _finishOverlay.Run = StartFinish;
        _finishOverlay.Open(form, window.Theme, now);
        window.Redraw();
    }

    private static string? RememberedFinishOption(string repoRoot)
    {
        var prefs = PreferencesStore.Load(PreferencesStore.DefaultPath());
        return prefs.WorktreeFinishOptions?.GetValueOrDefault(repoRoot);
    }

    private static void RememberFinishOption(string repoRoot, FinishOption option)
    {
        try
        {
            string path = PreferencesStore.DefaultPath();
            var prefs = PreferencesStore.Load(path);
            var map = prefs.WorktreeFinishOptions ?? [];
            map[repoRoot] = FinishRules.Key(option);
            prefs.WorktreeFinishOptions = map;
            PreferencesStore.Save(path, prefs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"erikterm: couldn't save preferences: {ex.Message}");
        }
    }

    private void StartFinish(FinishForm form)
    {
        var f = form.Facts;
        var option = form.Option;
        // Refused again at the last moment: an agent may have started since the dialog opened.
        var panes = process.PanesByWorktree([f.Worktree, f.MainRoot]);
        if (panes.TryGetValue(f.Worktree, out var inside) && inside.Any(r => r.AgentActive)) { form.Fail("An agent is still running in this worktree: stop it first"); return; }
        var steps = form.Steps.ToList();
        steps[0] = steps[0] with { Guard = () => WorktreeManagerGit.VerifyFinish(f, option, afterRebase: false) };
        if (option == FinishOption.Rebase)
            steps[1] = steps[1] with { Guard = () => WorktreeManagerGit.VerifyFinish(f, option, afterRebase: true) };
        Action? onSuccess = option == FinishOption.PullRequest
            ? () => { window.Toast(ToastKind.Success, "Pull request opened", f.Branch); }
            : () => OfferRemoval(f);
        var plan = new RepoPlan(FinishRules.Label(option) + " " + f.Branch, steps) { OnSuccess = onSuccess };
        form.Busy = true;
        RememberFinishOption(f.MainRoot, option);
        window.StartRepoPlan(plan, form.Fail, () => _finishOverlay.Close(Now));
    }

    /// <summary>After a successful finish: checks what's true now, then offers to remove the worktree and branch.</summary>
    private void OfferRemoval(FinishFacts f)
    {
        bool useGh = window.GitHubEnabled;
        window.RunInBackground(() =>
        {
            var inv = WorktreeManagerGit.Inventory(f.MainRoot, out _);
            if (inv?.Entries.FirstOrDefault(e => e.Path == f.Worktree) is not { Head: { Length: 40 } head } entry) return null;
            return new RemovalProbe(inv, entry, WorktreeManagerGit.Landed(inv.MainRoot, head, entry.Branch, f.Target, inv.HasWriteTree, useGh), WorktreeManagerGit.Changes(entry.Path) ?? 1);
        }, p =>
        {
            if (p is not { Inventory: var inv, Entry: var entry, Verdict: var verdict, Changes: var changes }) return;
            var panes = process.PanesByWorktree(inv.Entries.Select(e => e.Path).ToList());
            panes.TryGetValue(entry.Path, out var inside);
            bool agent = inside?.Any(r => r.AgentActive) == true;
            var block = Landing.Block(entry, verdict, changes, agent, inside is not null, ignoreOpenTab: true);
            if (block != RemovalBlock.None)
            {
                window.Toast(ToastKind.Info, "Keeping the worktree", Landing.Explain(block, entry, verdict, changes));
                return;
            }
            var lines = new List<string>();
            var commands = new List<string>();
            var steps = new List<RepoStep>();
            AppendRemoval(inv, entry, verdict, f.Target, lines, commands, steps);
            if (inside?.Count > 0) lines.Add($"{Counted.Of(inside.Count, "pane")} open there will sit in a removed directory");
            ShowRemovalConfirm(new RepoPlan("Remove " + entry.Name, steps), lines, commands, "Remove this worktree?", [entry.Path]);
        }, wake: true);
    }

    // ---- New Agent Worktree -----------------------------------------------------------------------

    /// <summary>NewAgentWorktree: looks at the focused pane's repository in the background, then opens the form.</summary>
    public void OpenAgentForm()
    {
        if (window.Focused.StartDirectory(process.Settings.DefaultCwd) is not { Length: > 0 } cwd || !Directory.Exists(cwd))
        {
            window.Toast(ToastKind.Error, "No directory to look for a repository in", "The focused pane doesn't report one");
            return;
        }
        var agents = ConfiguredAgents();
        window.RunInBackground(() =>
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            var repo = WorktreeGit.Probe(cwd, out string? error);
            var available = WorktreeLauncher.Available(agents, c => ToolRunner.Search(c, path), OperatingSystem.IsWindows());
            return new WorktreeProbe(repo, error, available, PreferencesStore.Load(PreferencesStore.DefaultPath()));
        }, probe => ShowAgentWorktree(probe, cwd), wake: true);
    }

    private void ShowAgentWorktree(WorktreeProbe? probe, string cwd)
    {
        if (probe?.Repo is not { } repo)
        {
            window.Toast(ToastKind.Error, "Can't open New Agent Worktree", probe?.Error ?? $"Couldn't read the repository at {cwd}");
            return;
        }
        long now = Now;
        window.CloseMenus(now);
        _launchOverlay.Launch = StartAgentWorktree;
        _launchOverlay.Open(new WorktreeForm(new WorktreeFormOptions
        {
            Cwd = cwd,
            RepoName = repo.Name,
            MainRoot = repo.MainRoot,
            Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DirTemplate = process.Settings.GitWorktreeDir,
            DefaultBase = repo.DefaultBase,
            Agents = probe.Agents,
            Branches = repo.Branches,
            PathExists = path => Directory.Exists(path) || File.Exists(path),
            Setup = WorktreeLauncher.SetupFor(process.Settings.GitWorktreeSetup, repo.Name),
            LastAgent = probe.Preferences.WorktreeAgent,
            RememberedArguments = probe.Preferences.WorktreeAgentArguments,
            NameSeed = Environment.TickCount,
            Placement = WorktreeLauncher.ParsePlacement(process.Settings.WorktreeOpenIn),
        }), window.Theme, now);
        window.Redraw();
    }

    /// <summary>The form was confirmed. Enter keeps it up until git is done, so a failure loses nothing; Ctrl+Enter returns to it at once.</summary>
    private void StartAgentWorktree(WorktreeForm form, WorktreeRequest request, bool background, WorktreePlacement placement)
    {
        if (background)
        {
            form.PrepareNext(request.Branch);
            window.Toast(ToastKind.Info, $"Creating worktree {request.Branch}…", "In the background: the form is ready for the next one");
        }
        else form.Busy = true;

        window.RunInBackground(() => new WorktreeResult(WorktreeGit.Create(request)),
            result => OnAgentWorktreeCreated(result, form, request, background, placement), wake: true);
    }

    private void OnAgentWorktreeCreated(WorktreeResult? result, WorktreeForm form, WorktreeRequest request, bool background, WorktreePlacement placement)
    {
        string? error = result is null ? "Creating the worktree failed unexpectedly" : result.Error;
        if (error is not null)
        {
            if (!background && _launchOverlay.IsShowing(form)) form.Fail(error);
            else window.Toast(ToastKind.Error, $"Couldn't create worktree {request.Branch}", error);
            window.Redraw();
            return;
        }
        if (!background && _launchOverlay.IsShowing(form)) _launchOverlay.Close(Now);
        if (placement == WorktreePlacement.Split) OpenWorktreeSplit(request, background);
        else OpenWorktreeTab(request, background);
    }

    private void OpenWorktreeTab(WorktreeRequest request, bool background)
    {
        var (plan, shell) = PlanWorktreeLaunch(request);
        var launch = new WorktreeTabLaunch(request.Branch, plan.First, request.Directory, SetupEcho(request), background);
        string? failure = window.OpenLaunchTab(launch, pane =>
        {
            AdoptWorktreeLaunch(pane, request, plan, shell);
            RememberWorktreeLaunch(request);
        });
        if (failure is not null)
            window.Toast(ToastKind.Error, $"The worktree is ready, but {request.Agent.Name} couldn't start", $"{request.Directory}: {failure}");
    }

    /// <summary>
    /// worktree_open_in = "split", or Alt+Enter: the same sequence as a tab, next to the focused pane, in the worktree's
    /// directory. The tab keeps its name and color. In the background the focus doesn't move.
    /// </summary>
    private void OpenWorktreeSplit(WorktreeRequest request, bool background)
    {
        var (plan, shell) = PlanWorktreeLaunch(request);
        var pane = window.OpenLaunchSplit(plan.First, request.Directory, focus: !background, SetupEcho(request));
        if (pane is null)
        {
            window.Toast(ToastKind.Error, $"The worktree is ready, but {request.Agent.Name} couldn't start", request.Directory);
            return;
        }
        pane.CloseOnExit = false; // a program of the user's, not an erikterm tool split
        AdoptWorktreeLaunch(pane, request, plan, shell);
        RememberWorktreeLaunch(request);
    }

    private List<WorktreeAgent> ConfiguredAgents() =>
        WorktreeLauncher.MergeAgents(process.Settings.WorktreeAgents.Select(a => new WorktreeAgentOverride(a.Key, a.Name, a.Command, a.Args, a.PromptArgs)));

    private string[] DefaultShell() => process.Settings.DefaultProg?.ToArray() ?? Pty.Pty.DefaultShellCommand();

    private (WorktreeLaunchPlan Plan, string[] Shell) PlanWorktreeLaunch(WorktreeRequest request)
    {
        string[] shell = DefaultShell();
        string[]? setup = request.Setup is { Length: > 0 } s ? WorktreeLauncher.SetupArgv(s, OperatingSystem.IsWindows()) : null;
        return (new WorktreeLaunchPlan(setup, request.AgentArgv.Length > 0 ? request.AgentArgv : null, shell), shell);
    }

    private static Action<Core.Terminal>? SetupEcho(WorktreeRequest request) =>
        request.Setup is { Length: > 0 } setup ? term => term.Feed(System.Text.Encoding.UTF8.GetBytes($"\e[2m$ {setup}\e[0m\r\n")) : null;

    /// <summary>What a pane of a launch is, in a tab or a split: Restart Pane gives a shell, never the prompt again, and the agent stays out of the saved session.</summary>
    private static void AdoptWorktreeLaunch(TPane pane, WorktreeRequest request, WorktreeLaunchPlan plan, string[] shell)
    {
        pane.LaunchArgv = shell;
        pane.LaunchCwd = request.Directory;
        pane.LaunchedAgent = request.AgentArgv.Length > 0 ? request.AgentArgv : null;
        pane.WorktreeLaunch = plan;
    }

    // ---- erikterm cli new-worktree -----------------------------------------------------------------

    private sealed record CliWorktree(WorktreeRequest? Request, string? Error);

    /// <summary>Git runs off the main thread; the reply, with the new pane's id, follows once the pane is open.</summary>
    public IpcResponse IpcNewWorktree(TPane from, IpcRequest r)
    {
        string cwd = r.Cwd ?? from.StartDirectory(process.Settings.DefaultCwd) ?? "";
        if (!Directory.Exists(cwd)) throw new IpcException(cwd.Length > 0 ? $"no such directory: {cwd}" : "no directory to look for a repository in: pass --cwd");
        var later = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agents = ConfiguredAgents();
        string[] shell = DefaultShell();
        var settings = process.Settings;
        var placement = WorktreeLauncher.ParsePlacement(r.Placement ?? settings.WorktreeOpenIn);
        window.RunInBackground(() => CreateForCli(r, cwd, agents, shell, settings), created => OpenCliWorktree(from, created, placement, r.NoFocus, later), wake: true);
        return new IpcResponse { Ok = true, Later = later };
    }

    private static CliWorktree CreateForCli(IpcRequest r, string cwd, List<WorktreeAgent> agents, string[] shell, Settings settings)
    {
        var repo = WorktreeGit.Probe(cwd, out string? error);
        if (repo is null) return new CliWorktree(null, error ?? $"Couldn't read the repository at {cwd}");
        string? path = Environment.GetEnvironmentVariable("PATH");
        var request = PlanCliWorktree(r, repo, cwd, agents, shell, command => ToolRunner.Search(command, path), settings.GitWorktreeDir,
            WorktreeLauncher.SetupFor(settings.GitWorktreeSetup, repo.Name), out error);
        if (request is null) return new CliWorktree(null, error);
        return new CliWorktree(request, WorktreeGit.Create(request));
    }

    /// <summary>
    /// What a <c>new-worktree</c> request launches, decided by the form itself so its names and checks are the same. Without an
    /// agent the form gets the shell as one, and <see cref="WorktreeRequest.AgentArgv"/> comes back empty.
    /// </summary>
    internal static WorktreeRequest? PlanCliWorktree(IpcRequest r, WorktreeRepo repo, string cwd, IReadOnlyList<WorktreeAgent> agents, string[] shell,
        Func<string, string?> findOnPath, string dirTemplate, string? setup, out string? error)
    {
        error = null;
        var agent = r.Agent is null ? new WorktreeAgent("", "A shell", shell[0], shell[1..], []) : ChooseCliAgent(r, agents, findOnPath, out error);
        if (agent is null) return null;
        var form = new WorktreeForm(new WorktreeFormOptions
        {
            Cwd = cwd, RepoName = repo.Name, MainRoot = repo.MainRoot, Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DirTemplate = dirTemplate, DefaultBase = repo.DefaultBase, Agents = [agent], Branches = repo.Branches,
            PathExists = path => Directory.Exists(path) || File.Exists(path), Setup = setup, NameSeed = Environment.TickCount,
        });
        form.Prompt.Set(r.Prompt ?? "");
        if (r.AgentArguments is { } arguments) form.Arguments.Set(arguments);
        if (r.Base is { } baseRef) form.Base.Set(baseRef);
        if (r.Branch is { } branch) form.Branch.Set(branch);
        form.Changed(r.Branch is null ? WorktreeField.Prompt : WorktreeField.Branch);
        if (form.TrySubmit() is not { } request) { error = form.Error; return null; }
        return r.Agent is null ? request with { AgentArgv = [] } : request;
    }

    private static WorktreeAgent? ChooseCliAgent(IpcRequest r, IReadOnlyList<WorktreeAgent> agents, Func<string, string?> findOnPath, out string? error)
    {
        error = null;
        var agent = agents.FirstOrDefault(a => a.Key == r.Agent);
        if (agent is null) error = $"no agent '{r.Agent}' (one of: {string.Join(", ", agents.Select(a => a.Key))})";
        else if (WorktreeLauncher.Available([agent], findOnPath, OperatingSystem.IsWindows()).Count == 0) error = $"{agent.Name} ({agent.Command}) isn't on PATH";
        else if (r.Prompt is { Length: > 0 } && !WorktreeLauncher.TakesPrompt(agent)) error = $"{agent.Name} takes no first prompt";
        return error is null ? agent : null;
    }

    private void OpenCliWorktree(TPane from, CliWorktree? created, WorktreePlacement placement, bool background, TaskCompletionSource<IpcResponse> later)
    {
        if (created?.Request is not { } request || created.Error is not null)
        {
            later.TrySetResult(IpcResponse.Failure(created?.Error ?? "Creating the worktree failed unexpectedly"));
            return;
        }
        var (plan, shell) = PlanWorktreeLaunch(request);
        string? failure = null;
        var pane = placement == WorktreePlacement.Split
            ? OpenCliSplit(from, request, plan, shell, background)
            : OpenCliTab(request, plan, shell, background, out failure);
        string why = failure is null ? "" : ": " + failure;
        later.TrySetResult(pane is null
            ? IpcResponse.Failure($"the worktree is ready at {request.Directory}, but {request.Agent.Name} couldn't start{why}")
            : new IpcResponse { Ok = true, PaneId = pane.Id });
        window.Redraw();
    }

    private TPane? OpenCliTab(WorktreeRequest request, WorktreeLaunchPlan plan, string[] shell, bool background, out string? failure)
    {
        TPane? opened = null;
        failure = window.OpenLaunchTab(new WorktreeTabLaunch(request.Branch, plan.First, request.Directory, SetupEcho(request), background), pane =>
        {
            AdoptWorktreeLaunch(pane, request, plan, shell);
            opened = pane;
        });
        return failure is null ? opened : null;
    }

    private TPane? OpenCliSplit(TPane from, WorktreeRequest request, WorktreeLaunchPlan plan, string[] shell, bool background)
    {
        var pane = window.OpenLaunchSplitNextTo(from, plan.First, request.Directory, focus: !background, SetupEcho(request));
        if (pane is null) return null;
        pane.CloseOnExit = false;
        AdoptWorktreeLaunch(pane, request, plan, shell);
        return pane;
    }

    /// <summary>Hook from HoldIfWanted: a worktree tab's program exited, so the next one starts in the same pane.</summary>
    public bool ContinueLaunch(TPane pane)
    {
        if (pane.WorktreeLaunch is not { } plan) return false;
        if (plan.Next(pane.ExitCode) is not { } step) { pane.WorktreeLaunch = null; return false; }
        if (step.IsLast) pane.WorktreeLaunch = null;
        if (step.Message is { } message) lock (gate) pane.Term.Feed(System.Text.Encoding.UTF8.GetBytes(message));
        return window.Respawn(pane, [.. step.Argv], pane.LaunchCwd);
    }

    /// <summary>Remembers the agent, and its arguments only where they differ from the config; they come back in the field, in plain sight.</summary>
    private static void RememberWorktreeLaunch(WorktreeRequest request)
    {
        try
        {
            string path = PreferencesStore.DefaultPath();
            var preferences = PreferencesStore.Load(path);
            preferences.WorktreeAgent = request.Agent.Key;
            var arguments = preferences.WorktreeAgentArguments ?? [];
            if (request.ArgumentsText == WorktreeLauncher.JoinArguments(request.Agent.Args)) arguments.Remove(request.Agent.Key);
            else arguments[request.Agent.Key] = request.ArgumentsText;
            preferences.WorktreeAgentArguments = arguments.Count > 0 ? arguments : null;
            PreferencesStore.Save(path, preferences);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"erikterm: couldn't save preferences: {ex.Message}");
        }
    }
}
