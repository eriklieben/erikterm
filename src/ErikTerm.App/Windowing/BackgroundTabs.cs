using ErikTerm.App.Panes;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// Keeping background tabs in step with the window: their panes are resized along with the active tab's (so
/// programs get SIGWINCH right away and switching doesn't re-layout), the activity dot ignores startup and
/// redraw noise, and a reloaded scrollback_lines applies to every pane.
/// </summary>
internal sealed class BackgroundTabs(IWindowHost window, Lock gate, TabList<Tab> tabs)
{
    private readonly List<(Pane Pane, RectI Rect)> _rects = [];
    private long _activityPruneAt;

    private static long Now => Environment.TickCount64;

    /// <summary>What each pane last showed that the user has seen (the activity dot).</summary>
    public ActivityTracker Activity { get; } = new();

    /// <summary>Hook at the end of ApplyLayout: lays out and resizes the panes of the other tabs in the same bounds.</summary>
    public void Resize(RectI bounds, Tab? outgoing)
    {
        if (tabs.Count < 2) return;
        long now = Now;
        var live = window.Layout;
        int gap = window.Gap;
        foreach (var tab in tabs.Items)
        {
            if (tab.Layout is null || ReferenceEquals(tab.Layout, live)) continue;
            window.SyncTeammates(tab.Layout); // Teammates/WindowTeammates.cs
            tab.Layout.Layout(bounds, gap, _rects);
            bool sliding = ReferenceEquals(tab, outgoing); // its rects are animating out; leave them be
            foreach (var (pane, rect) in _rects)
            {
                var r = tab.Zoomed is not null && pane == tab.Zoomed ? bounds : rect;
                if (!sliding)
                {
                    pane.Target = r;
                    pane.Rect.Snap(r);
                }
                if (window.SizeTerminal(pane, r, window.TeammateStripHeight(pane, r)))
                    Activity.Resized(pane.Id, now); // the redraw it answers with isn't news
            }
            window.SizeParkedTeammates(tab.Layout, bounds, background: true);
        }
        _rects.Clear();
    }

    // ---- Activity dot ----------------------------------------------------------------------------

    private static PaneActivity ActivityOf(Pane p) =>
        new(p.Term.ContentVersion, p.Term.CommandsStarted, p.Term.CommandRunning, p.Term.HasPromptMarks, p.SpawnedAt);

    /// <summary>Hook from the tab titles' refresh for a background tab: did any of its panes do something?</summary>
    public bool HasActivity(Tab tab, long now)
    {
        bool any = false;
        lock (gate)
            foreach (var p in tab.Layout.Leaves()) any |= Activity.Check(p.Id, ActivityOf(p), now);
        return any;
    }

    /// <summary>The user is looking at (or just left) this tab: its current output is seen.</summary>
    public void MarkSeen(Tab tab)
    {
        if (tab.Layout is null) return;
        lock (gate)
            foreach (var p in tab.Layout.Leaves()) Activity.Seen(p.Id, ActivityOf(p));
    }

    /// <summary>Hook from the session's tick: forgets panes that are gone.</summary>
    public void PruneActivity()
    {
        long now = Now;
        if (now < _activityPruneAt || Activity.Count < 16) return;
        _activityPruneAt = now + 10_000;
        var live = new HashSet<int>();
        foreach (var tab in tabs.Items.Where(t => t.Layout is not null))
            foreach (var p in tab.Layout.Leaves()) live.Add(p.Id);
        foreach (var p in window.Layout.Leaves()) live.Add(p.Id);
        Activity.Retain(live);
    }

    // ---- scrollback_lines ------------------------------------------------------------------------

    /// <summary>Hook from the window's settings reload: applies a changed scrollback_lines to every pane in every tab.</summary>
    public void ApplyScrollbackLimit()
    {
        window.SyncTabs();
        int lines = window.Settings.ScrollbackLines;
        lock (gate)
        {
            foreach (var layout in tabs.Items.Select(tab => tab.Layout))
            {
                if (layout is null) continue;
                foreach (var p in layout.Leaves())
                {
                    if (p.Term.ScrollbackCapacity == lines) continue;
                    p.Term.ScrollbackCapacity = lines;
                    if (p.Selection is { } sel && Math.Min(sel.AnchorLine, sel.HeadLine) < p.Term.FirstLine) p.Selection = null;
                }
            }
        }
        window.Redraw();
    }
}
