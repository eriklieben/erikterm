using System.Collections.Concurrent;
using ErikTerm.App.Agents;
using ErikTerm.App.Integrations;
using ErikTerm.App.Panes;
using ErikTerm.App.Search;
using ErikTerm.App.Ui;
using ErikTerm.App.Worktrees;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Host;

/// <summary>
/// Every window's panes as one set: listing them (the agents overview, "Send to Agent…", search across all panes,
/// worktrees), finding the one with an id, and showing a pane in whichever window has it now — raising that window
/// when it's another one. Main thread, except <see cref="RequestFocus"/>.
/// </summary>
internal sealed class ProcessPanes(WindowSet<TermWindow, Tab> windows)
{
    private readonly ConcurrentQueue<int> _focusRequests = new(); // pane ids from clicked notifications

    private static long Now => Environment.TickCount64;

    private int LiveWindows()
    {
        int live = 0;
        for (int i = 0; i < windows.Count; i++) if (!windows[i].CloseRequested) live++;
        return live;
    }

    /// <summary>The pane with that id, with its tab and window; null when no open window has it.</summary>
    public Pane? Find(int paneId, out Tab? tab, out TermWindow? window)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            if (w.CloseRequested) continue;
            var tabs = w.Tabs.Items;
            for (int t = 0; t < tabs.Count; t++)
            {
                var leaves = tabs[t].Layout.LeafList;
                for (int p = 0; p < leaves.Count; p++)
                    if (leaves[p].Id == paneId) { tab = tabs[t]; window = w; return leaves[p]; }
            }
        }
        tab = null;
        window = null;
        return null;
    }

    // ---- Agents ------------------------------------------------------------------------------------------------

    /// <summary>Every window's agents, most urgent first, then longest in that state.</summary>
    public List<AgentRow<Pane>> AgentRows()
    {
        long now = Now;
        var rows = new List<AgentRow<Pane>>();
        int live = LiveWindows();
        for (int i = 0; i < windows.Count; i++)
            if (!windows[i].CloseRequested) windows[i].CollectAgentRows(rows, live > 1, now);
        rows.Sort((x, y) =>
        {
            int c = AgentStateText.Urgency(y.State).CompareTo(AgentStateText.Urgency(x.State));
            return c != 0 ? c : x.Since.CompareTo(y.Since); // longest waiting first
        });
        return rows;
    }

    /// <summary>The overview picked a pane: show it in its window and raise that window when it's another one.</summary>
    public void JumpToAgentPane(Pane pane)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            if (w.CloseRequested || !w.JumpToPane(pane)) continue;
            if (!ReferenceEquals(windows.Focused, w)) w.RequestAttention();
            return;
        }
    }

    /// <summary>A desktop notification was clicked (a pool thread): its pane gets the focus on the next drain.</summary>
    public void RequestFocus(int paneId) => _focusRequests.Enqueue(paneId);

    /// <summary>Hook from WindowAgents.Tick: focus the panes of clicked notifications (wherever they are now).</summary>
    public void DrainFocusRequests()
    {
        while (_focusRequests.TryDequeue(out int id))
            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                if (w.CloseRequested || w.PaneById(id) is not { } pane) continue;
                w.JumpToPane(pane);
                w.RequestAttention();
                break;
            }
    }

    /// <summary>Every window's panes, for "Send to Agent…".</summary>
    public List<AgentPaneRef<Pane>> CollectAgentPanes()
    {
        var all = new List<AgentPaneRef<Pane>>();
        for (int i = 0; i < windows.Count; i++)
            if (!windows[i].CloseRequested) windows[i].CollectAgentPanes(all);
        return all;
    }

    // ---- Search across all panes -------------------------------------------------------------------------------

    /// <summary>Every pane to search, those of <paramref name="origin"/> first. Runs on the UI thread, once per query.</summary>
    public List<PaneSearchTarget<Pane>> SearchTargets(TermWindow origin)
    {
        var targets = new List<PaneSearchTarget<Pane>>();
        int number = 1, live = LiveWindows();
        origin.CollectPaneSearchTargets(targets, live > 1 ? "This window · " : "");
        for (int i = 0; i < windows.Count; i++)
            if (!windows[i].CloseRequested && !ReferenceEquals(windows[i], origin))
                windows[i].CollectPaneSearchTargets(targets, $"Window {++number} · ");
        return targets;
    }

    /// <summary>A match was picked: show it in the window that has the pane and raise that window when it's another one.</summary>
    public bool RevealSearchHit(Pane pane, PaneSearchQuery query, SearchMatch match)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            if (w.CloseRequested || !w.RevealSearchHit(pane, query, match)) continue;
            if (!ReferenceEquals(windows.Focused, w)) w.RequestAttention();
            return true;
        }
        return false;
    }

    // ---- Worktrees ---------------------------------------------------------------------------------------------

    /// <summary>Every window's panes, by the worktree (one of <paramref name="roots"/>) they're in.</summary>
    public Dictionary<string, List<WorktreePaneRef<Pane>>> ByWorktree(List<string> roots)
    {
        var map = new Dictionary<string, List<WorktreePaneRef<Pane>>>(StringComparer.Ordinal);
        for (int i = 0; i < windows.Count; i++)
            if (!windows[i].CloseRequested) windows[i].CollectWorktreePanes(map, roots);
        return map;
    }

    /// <summary>Shows a pane in that worktree, in any window; false when none is open there.</summary>
    public bool JumpToWorktree(string path, List<string> roots)
    {
        if (!ByWorktree(roots).TryGetValue(path, out var panes) || panes.Count == 0) return false;
        var pane = panes.Find(p => p.AgentActive) ?? panes[0];
        JumpToAgentPane(pane.Pane);
        return true;
    }

    /// <summary>Shows the pane already reviewing the worktree at <paramref name="root"/>, in any window; false when there is none.</summary>
    public bool JumpToReview(string root)
    {
        var panes = new List<ErikTerm.Ui.Compare.ReviewPaneRef>();
        for (int i = 0; i < windows.Count; i++)
            if (!windows[i].CloseRequested) windows[i].CollectReviewPanes(panes);
        if (ErikTerm.Ui.Compare.ReviewTabs.Find(panes, root, OperatingSystem.IsWindows()) is not { } id) return false;
        for (int i = 0; i < windows.Count; i++)
            if (!windows[i].CloseRequested && windows[i].PaneById(id) is { } pane) { JumpToAgentPane(pane); return true; }
        return false;
    }

    /// <summary>What a window's GitHub status knows about this worktree's branch (nothing is fetched for this).</summary>
    public GitHubStatus? KnownGitHubStatus(string root, string branch)
    {
        for (int i = 0; i < windows.Count; i++)
            if (!windows[i].CloseRequested && windows[i].GitHubStatusFor(root, branch) is { } status) return status;
        return null;
    }
}
