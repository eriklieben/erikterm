using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Panes;

/// <summary>
/// A pane Claude Code opened through the tmux emulation for an agent-team teammate (tmux_agent_panes = "pills").
/// It stays in its tab's layout tree where Claude Code put it, but while <see cref="Parked"/> it takes no room
/// (<see cref="ILayoutLeaf"/>): it keeps running, at the size it would have as the split, and shows as a pill in
/// the strip below its lead's pane (Teammates/WindowTeammates.cs). Main thread only; never saved with the session.
/// </summary>
internal sealed class Teammate
{
    /// <summary>The pane of the Claude Code session that made the team (its $TMUX_PANE).</summary>
    public int LeadId;
    /// <summary>--agent-name, else the select-pane -T title, else "teammate".</summary>
    public string Name = "teammate";
    public string? Team, AgentId;
    /// <summary>The color Claude Code gave the teammate: --agent-color or the pane-border-style fg (a tmux color name).</summary>
    public string? Color;
    public bool Parked = true;
    /// <summary>respawn-pane started Claude Code in it with --agent-name (until then: a Claude Code `cat` placeholder).</summary>
    public bool Confirmed;

    // Drawing (Teammates/WindowTeammates.cs): hover, where the pill was drawn last frame, cached label.
    public Tween Hover = new(0);
    public RectF PillRect;
    public string Label = "";
    public float LabelWidth, LabelPx;
    private AgentState _labelState;
    private bool _labelBuilt;
    private string? _labelKind, _labelName;

    /// <summary>"alpha · working" — rebuilt only when the name or the state changes (no allocation on a steady frame).</summary>
    public bool RefreshLabel(AgentState state, string? kind)
    {
        if (_labelBuilt && state == _labelState && kind == _labelKind && ReferenceEquals(Name, _labelName)) return false;
        _labelBuilt = true;
        (_labelState, _labelKind, _labelName) = (state, kind, Name);
        Label = state == AgentState.None ? Name : $"{Name} · {AgentStateText.Describe(state, kind)}";
        LabelPx = 0; // measure again
        return true;
    }
}

/// <summary>
/// Which teammates belong to which lead in one tab (generic so it is testable without real panes). A teammate whose
/// lead isn't in the tab is an orphan: it opens and stays an ordinary split. With pills off, every teammate shows.
/// </summary>
internal static class TeamRoster
{
    /// <summary>The first pane of the layout that takes room (a parked teammate doesn't), or null.</summary>
    public static T? FirstShown<T>(LayoutTree<T> layout) where T : class, ILayoutLeaf
    {
        foreach (var p in layout.LeafList) if (!p.LayoutHidden) return p;
        return null;
    }

    /// <param name="leaves">The tab's panes (layout order).</param>
    /// <param name="teammatesOf">A pane's list of teammates (cleared and refilled for leads).</param>
    public static void Sync<T>(IReadOnlyList<T> leaves, Func<T, int> id, Func<T, Teammate?> teammate, Func<T, List<T>> teammatesOf, bool pills)
        where T : class
    {
        foreach (var p in leaves) teammatesOf(p).Clear();
        foreach (var p in leaves)
        {
            if (teammate(p) is not { LeadId: > 0 } t) continue;
            T? lead = null;
            foreach (var q in leaves)
                if (id(q) == t.LeadId && teammate(q) is null) { lead = q; break; }
            if (lead is null) t.LeadId = 0; // orphaned for good: an ordinary split from now on
            if (lead is null || !pills)
            {
                t.Parked = false;
                continue;
            }
            teammatesOf(lead).Add(p);
        }
    }
}
