using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Integrations;

/// <summary>A pane as "Send to Agent…" sees it: which agent runs there (null: none) and where the pane is.</summary>
internal readonly record struct AgentCandidate(int PaneId, string? Agent, bool Exited, uint WindowId, int TabId);

/// <summary>Choosing the panes "Send to Agent…" offers, and what is written to the chosen one.</summary>
internal static class AgentSend
{
    /// <summary>
    /// The panes running a coding agent (teammates too, parked or not), except the one the text comes from and panes whose
    /// program has exited: the current tab's first, then the window's, then other windows', otherwise in the given order.
    /// </summary>
    public static List<AgentCandidate> Targets(IEnumerable<AgentCandidate> panes, int sourcePaneId, uint currentWindow, int currentTab) =>
        panes.Where(p => p.Agent is { Length: > 0 } && !p.Exited && p.PaneId != sourcePaneId)
            .OrderBy(p => Distance(p, currentWindow, currentTab))
            .ToList();

    private static int Distance(AgentCandidate p, uint window, int tab)
    {
        if (p.WindowId != window) return 2;
        return p.TabId == tab ? 0 : 1;
    }

    /// <summary>
    /// The agent has a question open, so Enter would answer it. A notification alone doesn't say that: agents send one
    /// while they sit idle at their prompt too, which is exactly when comments should go through.
    /// </summary>
    public static bool AwaitsAnswer(AgentState state, AgentSource source, AgentState onScreen) =>
        onScreen == AgentState.Waiting || (state == AgentState.Waiting && source != AgentSource.Notification);

    /// <summary>
    /// The bytes for the agent's terminal: the text as a paste (sanitised and bracketed like any paste), with Enter after
    /// the paste when <paramref name="submit"/>.
    /// </summary>
    public static (string Text, int Removed) Payload(string text, bool bracketed, bool sanitize, bool submit)
    {
        var paste = Security.PasteSanitizer.Prepare(text, bracketed, sanitize);
        return (paste.Text.Length > 0 && submit ? paste.Text + "\r" : paste.Text, paste.Removed);
    }
}
