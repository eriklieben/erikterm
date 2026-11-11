namespace ErikTerm.App.Notifications;

/// <summary>Where panes live right now. Host/ProcessNotifications.cs answers from its windows; tests use a map.</summary>
internal interface IPaneTabs
{
    /// <summary>Id of the tab holding the pane; 0 when no window has it (closed).</summary>
    int TabOf(int paneId);
    /// <summary>The pane of the agent-team lead this pane works for; 0 for an ordinary pane.</summary>
    int LeadOf(int paneId);
}

/// <summary>
/// Which tab a notification belongs to, worked out from its pane each time it matters, so panes that moved to another
/// tab or window take their notifications with them. Teammate panes go with their lead's tab while the lead is alive.
/// Allocation-free (the tab markers poll it).
/// </summary>
internal readonly struct NotificationScope(IPaneTabs panes, int tabId)
{
    public int TabId { get; } = tabId;

    public static int HomeTab(IPaneTabs panes, int paneId)
    {
        int lead = panes.LeadOf(paneId);
        if (lead > 0)
        {
            int leadTab = panes.TabOf(lead);
            if (leadTab != 0) return leadTab;
        }
        return panes.TabOf(paneId);
    }

    /// <summary>The notification belongs to this tab.</summary>
    public bool IsHere(int paneId) => HomeTab(panes, paneId) == TabId;

    /// <summary>It belongs to another tab that still exists (a closed pane is in no tab: only the panel's history has it).</summary>
    public bool IsElsewhere(int paneId)
    {
        int tab = HomeTab(panes, paneId);
        return tab != 0 && tab != TabId;
    }
}
