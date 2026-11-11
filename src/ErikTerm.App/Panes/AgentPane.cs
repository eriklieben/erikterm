using ErikTerm.App.Session;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Panes;

/// <summary>
/// A pane's coding-agent bookkeeping (main thread only): which agent runs there, the state tracker, what the
/// last poll saw, usage from the status line bridge, and the badge animations. Ephemeral, except <see cref="SavedAgent"/>.
/// </summary>
internal sealed class AgentPane
{
    public AgentDefinition? Definition;
    public readonly AgentStatusTracker Tracker = new();
    public AgentUsage? Usage;
    public string? UsageCwd;
    /// <summary>The agent the restored session had here, until it is resumed or the offer is dismissed (Agents/WindowAgentResume.cs).</summary>
    public SessionAgent? SavedAgent;

    // Process identification: re-read /proc only when the foreground process group changes.
    public int Pgid = -2;
    public long IdentifiedAt;

    // What the last poll consumed.
    public long ContentVersion = -1;
    public int SignalVersion = -1;
    public string? Title;
    public AgentState TitleState;
    public AgentReport Heuristic;
    public int Cols, Rows;

    public long LastOutputAt, LastInputAt;
    public int BellCount = -1;

    // The prompt last answered from the overview, and the state it was answered in: not offered twice.
    public string? AnsweredPrompt;
    public long AnsweredSince;

    // Badge (pill) and waiting rim, faded with the state.
    public Tween Pill = new(0);
    public Tween Rim = new(0);

    // Cached strings for drawing (rebuilt when the state or usage changes, never per frame).
    public string PillText = "", UsageText = "";
    public AgentState PillState;
    public string? PillKind;
    public AgentUsage? PillUsage;

    public AgentState State => Tracker.State;
    public bool Active => Definition is not null || Tracker.State != AgentState.None;
    public string Name => Definition?.Name ?? "Agent";

    /// <summary>Text for the pill: "Claude · waiting" (or "· needs permission"), rebuilt only on change.</summary>
    public void RefreshText()
    {
        var s = Tracker.State;
        if (s == PillState && Tracker.Kind == PillKind && ReferenceEquals(Usage, PillUsage) && PillText.Length > 0) return;
        PillState = s;
        PillKind = Tracker.Kind;
        PillUsage = Usage;
        PillText = $"{Name} · {AgentStateText.Describe(s, Tracker.Kind)}";
        UsageText = Usage is null ? "" : string.Join(" · ", new[] { AgentUsage.Cost(Usage.CostUsd), AgentUsage.Context(Usage.ContextPercent ?? Usage.ContextFraction * 100) }.Where(x => x is not null));
    }
}
