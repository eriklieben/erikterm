namespace ErikTerm.App.Ipc;

/// <summary>A window as request routing sees it: its id and which panes / tabs it holds.</summary>
internal readonly record struct IpcWindowEntry(int Id, Func<int, bool> HasPane, Func<int, bool> HasTab);

/// <summary>Which window an <c>erikterm cli</c> request is about. Pure, so it is testable without windows.</summary>
internal static class IpcRouting
{
    /// <summary>
    /// <c>--window-id</c> first; else the window holding <c>--pane-id</c> (panes have process-wide ids), else the one
    /// holding <c>--tab-id</c>; else the most recently focused window (<paramref name="focused"/>, an index, or -1 for
    /// the first). Returns an index into <paramref name="windows"/>, or -1 with <paramref name="error"/> set.
    /// </summary>
    public static int Resolve(IpcRequest r, IReadOnlyList<IpcWindowEntry> windows, int focused, out string? error)
    {
        error = null;
        if (windows.Count == 0) { error = "no windows are open"; return -1; }
        if (r.WindowId is { } wid) return Find(windows, w => w.Id == wid, $"no window with id {wid}", out error);
        if (r.PaneId is { } pid) return Find(windows, w => w.HasPane(pid), $"no pane with id {pid}", out error);
        if (r.TabId is { } tid) return Find(windows, w => w.HasTab(tid), $"no tab with id {tid}", out error);
        return focused >= 0 && focused < windows.Count ? focused : 0;
    }

    /// <summary>The first window that matches, or -1 with <paramref name="error"/> set.</summary>
    private static int Find(IReadOnlyList<IpcWindowEntry> windows, Func<IpcWindowEntry, bool> matches, string missing, out string? error)
    {
        error = null;
        for (int i = 0; i < windows.Count; i++) if (matches(windows[i])) return i;
        error = missing;
        return -1;
    }
}
