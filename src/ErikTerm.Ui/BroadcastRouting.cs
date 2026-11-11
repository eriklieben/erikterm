namespace ErikTerm.Ui;

/// <summary>Whether a tab's input goes to one pane or several (Toggle Broadcast Input).</summary>
public enum BroadcastMode { Off, AllPanes, Selected }

/// <summary>Which panes of a tab receive a copy of what is typed in the focused one (broadcast input). Pure: panes are whatever the caller uses.</summary>
public static class BroadcastRouting
{
    /// <summary>
    /// The panes that get a copy, in layout order, into <paramref name="targets"/> (cleared first): the tab's panes other
    /// than <paramref name="focused"/> that are <paramref name="eligible"/>; in <see cref="BroadcastMode.Selected"/> only
    /// those in <paramref name="chosen"/>, and only when the focused pane is one of them. Never a pane of another tab:
    /// <paramref name="tabPanes"/> is all that is looked at.
    /// </summary>
    public static void Targets<T>(BroadcastMode mode, IReadOnlyList<T> tabPanes, T focused, Func<T, bool> eligible, IReadOnlySet<T>? chosen, List<T> targets)
        where T : class
    {
        targets.Clear();
        if (mode == BroadcastMode.Off) return;
        if (mode == BroadcastMode.Selected && (chosen is null || !chosen.Contains(focused))) return;
        for (int i = 0; i < tabPanes.Count; i++)
        {
            var p = tabPanes[i];
            if (ReferenceEquals(p, focused) || !eligible(p)) continue;
            if (mode == BroadcastMode.Selected && !chosen!.Contains(p)) continue;
            targets.Add(p);
        }
    }

    /// <summary>How many panes take part (the focused one counted): below two there is nothing to broadcast to.</summary>
    public static int GroupSize<T>(BroadcastMode mode, IReadOnlyList<T> tabPanes, Func<T, bool> eligible, IReadOnlySet<T>? chosen) where T : class
    {
        if (mode == BroadcastMode.Off) return 0;
        int n = 0;
        for (int i = 0; i < tabPanes.Count; i++)
            if (eligible(tabPanes[i]) && (mode == BroadcastMode.AllPanes || chosen?.Contains(tabPanes[i]) == true)) n++;
        return n;
    }
}
