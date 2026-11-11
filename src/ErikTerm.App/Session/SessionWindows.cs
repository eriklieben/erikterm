namespace ErikTerm.App.Session;

/// <summary>Which saved windows a start rebuilds (pure, so it is testable without SDL).</summary>
internal static class SessionWindows
{
    /// <summary>
    /// The windows of <paramref name="file"/> worth restoring — those with at least one tab — in saved order, at most
    /// <paramref name="max"/>. Positions are dropped when they're implausible (a hand-edited file must not put a window
    /// a million pixels away).
    /// </summary>
    public static List<SessionWindow> Restorable(SessionFile? file, int max)
    {
        var list = new List<SessionWindow>();
        if (file?.Windows is not { Count: > 0 } windows) return list;
        foreach (var w in windows)
        {
            if (w?.Tabs is not { Count: > 0 } tabs || !tabs.Exists(t => t is not null)) continue;
            if (w.X is not (>= -32768 and <= 32768) || w.Y is not (>= -32768 and <= 32768)) (w.X, w.Y) = (null, null);
            list.Add(w);
            if (list.Count >= max) break;
        }
        return list;
    }
}
