using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Notifications;

/// <summary>
/// Every notification erikterm raises goes through here (one per process, Host/ProcessNotifications.cs): agents waiting or
/// finishing out of sight, programs' OSC 9 / 777 / 99, anything later. It cleans the text, rate-limits per pane,
/// keeps the history (last <see cref="Capacity"/>, unread first in the panel) and decides the route:
/// <list type="bullet">
/// <item>the focused pane of the focused window: nothing at all;</item>
/// <item>always: the history, and the in-window bar (when <see cref="BarEnabled"/>; else a toast while the window is focused);</item>
/// <item>window not focused: the desktop when the mode is On (and, for agents, <see cref="AgentDesktop"/>), with its
///   own rate limit; when it is still Ask, nothing goes out — <see cref="AskPending"/> makes the windows show the
///   warning bar instead.</item>
/// </list>
/// The mode: <see cref="ConfigMode"/> (os_notifications = "on"/"off" in the config) wins over
/// <see cref="StoredMode"/> (the answer remembered in preferences.json), else Ask. Main thread only.
/// </summary>
internal sealed class NotificationCenter(IDesktopNotifier desktop)
{
    public const int Capacity = 100;
    /// <summary>The desktop-notification question's priority on a window's info bar (WindowNotifications.cs).</summary>
    public const int AskPriority = 10;
    public const int DesktopPaneIntervalMs = 8000;   // per pane, unless it waits for the user
    public const int DesktopGlobalIntervalMs = 1000; // any pane
    public const int FloodWindowMs = 2000, FloodMax = 4; // per pane, into the history

    private readonly List<NotificationItem> _items = []; // oldest first
    private readonly Dictionary<int, long> _desktopAt = [];
    private readonly Dictionary<int, (long Start, int Count)> _flood = [];
    private long _lastDesktopAt = long.MinValue / 2, _nextId;

    // ---- Settings (App sets them from the config and preferences.json) ----------------------------

    /// <summary>os_notifications from the config when it says "on" or "off"; null otherwise.</summary>
    public OsNotificationMode? ConfigMode;
    /// <summary>The remembered answer (preferences.json).</summary>
    public OsNotificationMode? StoredMode;
    /// <summary>agent_notifications: false keeps agents' notifications off the desktop (and out of toasts).</summary>
    public bool AgentDesktop = true;
    /// <summary>notification_bar: the in-window bar; off, focused windows get toasts as before.</summary>
    public bool BarEnabled = true;

    public OsNotificationMode Mode => ConfigMode ?? StoredMode ?? OsNotificationMode.Ask;

    /// <summary>Bumped on every change: windows re-read the bar and panel when it moved.</summary>
    public int Version { get; private set; }

    /// <summary>Something wanted the desktop while the mode is Ask: the windows show the warning bar.</summary>
    public bool AskPending { get; private set; }
    /// <summary>Who asked first ("Claude", "make").</summary>
    public string AskApp { get; private set; } = "";
    /// <summary>The warning was closed with ✕ ("decide later"): not shown again until erikterm restarts.</summary>
    public bool AskDeferred { get; private set; }
    public bool AskVisible => AskPending && !AskDeferred && Mode == OsNotificationMode.Ask;

    /// <summary>The user answered: App saves it to preferences.json.</summary>
    public Action<OsNotificationMode>? Decided;

    public IReadOnlyList<NotificationItem> Items => _items;
    public int UnreadCount { get; private set; }

    // ---- Posting ----------------------------------------------------------------------------------

    public NotificationRoute Post(in NotificationRequest r, in NotificationContext ctx)
    {
        if (ctx.PaneSeen) return NotificationRoute.None; // never for the pane you're looking at
        long now = ctx.Now;
        bool waiting = r.Priority == NotificationPriority.Waiting;

        var (app, title, body) = CleanedText(r);
        if (IsFlooding(r.PaneId, now, waiting)) return NotificationRoute.None;

        var item = Record(r, now, app, title, body);
        var route = NotificationRoute.History;
        bool desktopAllowed = r.Source != NotificationSource.Agent || AgentDesktop;
        if (BarEnabled) route |= NotificationRoute.Bar;
        else if (ctx.WindowFocused && desktopAllowed) route |= NotificationRoute.Toast;

        if (!ctx.WindowFocused && desktopAllowed) route |= OutOfWindowRoute(item, now, waiting);
        Changed();
        return route;
    }

    private static (string App, string Title, string Body) CleanedText(in NotificationRequest r)
    {
        string app = NotificationText.Clean(r.App, NotificationText.MaxApp);
        string title = NotificationText.Clean(r.Title, NotificationText.MaxTitle);
        string body = NotificationText.Clean(r.Body, NotificationText.MaxBody);
        if (title.Length == 0)
        {
            if (body.Length > 0) title = body;
            else title = app.Length > 0 ? app : "erikterm";
            if (title == body) body = "";
        }
        return (app, title, body);
    }

    /// <summary>A pane printing notifications in a loop: a few per pane every couple of seconds, then nothing.</summary>
    private bool IsFlooding(int paneId, long now, bool waiting)
    {
        if (!waiting)
        {
            if (_flood.TryGetValue(paneId, out var f) && now - f.Start < FloodWindowMs)
            {
                if (f.Count >= FloodMax) return true;
                _flood[paneId] = (f.Start, f.Count + 1);
            }
            else _flood[paneId] = (now, 1);
        }
        if (_flood.Count > 256) Prune(_flood, now, FloodWindowMs);
        return false;
    }

    private NotificationItem Record(in NotificationRequest r, long now, string app, string title, string body)
    {
        var item = Latest(r.PaneId);
        if (item is not null && !item.Read && item.Title == title && item.Body == body)
        {
            // The same thing again (a bell, a repeated "done"): bump it instead of stacking duplicates.
            _items.Remove(item);
            item.Count++;
            item.At = now;
            item.Resolved = false;
            if (r.Priority > item.Priority) item.Priority = r.Priority;
        }
        else
        {
            item = new NotificationItem { Id = ++_nextId, PaneId = r.PaneId, At = now };
            item.Priority = r.Priority;
        }
        item.WindowId = r.WindowId;
        item.Source = r.Source;
        item.AgentState = r.AgentState;
        item.App = app;
        item.Title = title;
        item.Body = body;
        item.TabLabel = NotificationText.Clean(r.TabLabel, 32);
        item.Place = NotificationText.Clean(r.Place, NotificationText.MaxPlace);
        _items.Add(item);
        while (_items.Count > Capacity) _items.RemoveAt(0);
        return item;
    }

    private NotificationRoute OutOfWindowRoute(NotificationItem item, long now, bool waiting)
    {
        switch (Mode)
        {
            case OsNotificationMode.On when desktop.Available:
                return SendToDesktop(item, now, waiting);
            case OsNotificationMode.Ask when desktop.Available:
                if (!AskPending) AskApp = item.App.Length > 0 ? item.App : "A program";
                AskPending = true;
                return NotificationRoute.Ask;
            default:
                return NotificationRoute.None;
        }
    }

    private NotificationRoute SendToDesktop(NotificationItem item, long now, bool waiting)
    {
        bool paneLimited = !waiting && _desktopAt.TryGetValue(item.PaneId, out long last) && now - last < DesktopPaneIntervalMs;
        if (paneLimited || now - _lastDesktopAt < DesktopGlobalIntervalMs) return NotificationRoute.None;
        _desktopAt[item.PaneId] = _lastDesktopAt = now;
        if (_desktopAt.Count > 256) Prune(_desktopAt, now, DesktopPaneIntervalMs);
        desktop.Send(item.PaneId, NotificationText.DesktopTitle(item.Title), NotificationText.EscapeMarkup(item.Body), waiting);
        item.SentToDesktop = true;
        return NotificationRoute.Os;
    }

    // ---- The user's answer ------------------------------------------------------------------------

    /// <summary>[Allow] (On), [Only in erikterm] (Off), the palette's toggles. Remembered via <see cref="Decided"/>.</summary>
    public void Decide(OsNotificationMode mode)
    {
        StoredMode = mode == OsNotificationMode.Ask ? null : mode;
        AskPending = false;
        AskDeferred = false;
        Decided?.Invoke(mode);
        Changed();
    }

    /// <summary>✕ on the warning: decide later (ask again after a restart).</summary>
    public void DeferAsk()
    {
        AskDeferred = true;
        Changed();
    }

    // ---- Unread / history -------------------------------------------------------------------------

    public void MarkRead(long id)
    {
        if (Find(id) is { Read: false } item) { item.Read = true; Changed(); }
    }

    /// <summary>The user looked at the pane (focused it in a focused window): its notifications are read.</summary>
    public void MarkPaneSeen(int paneId)
    {
        if (UnreadCount == 0) return;
        bool any = false;
        foreach (var it in _items)
            if (it.PaneId == paneId && !it.Read) { it.Read = true; any = true; }
        if (any) Changed();
    }

    /// <summary>Whether the pane has anything unread (a tab's attention marker). Allocation-free.</summary>
    public bool HasUnread(int paneId)
    {
        if (UnreadCount == 0) return false;
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].PaneId == paneId && !_items[i].Read) return true;
        return false;
    }

    public void MarkAllRead()
    {
        if (UnreadCount == 0) return;
        foreach (var it in _items) it.Read = true;
        Changed();
    }

    /// <summary>The pane's agent stopped waiting (answered elsewhere, or the pane closed): its waiting items may auto-hide.</summary>
    public void ResolveWaiting(int paneId)
    {
        bool any = false;
        foreach (var it in _items)
            if (it.PaneId == paneId && it.Sticky) { it.Resolved = true; any = true; }
        if (any) Changed();
    }

    public void Remove(long id)
    {
        if (Find(id) is { } item) { _items.Remove(item); Changed(); }
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed();
    }

    public NotificationItem? Find(long id)
    {
        foreach (var it in _items) if (it.Id == id) return it;
        return null;
    }

    /// <summary>The history for the panel: unread first, newest first within each.</summary>
    public void Sorted(List<NotificationItem> into)
    {
        into.Clear();
        for (int i = _items.Count - 1; i >= 0; i--) if (!_items[i].Read) into.Add(_items[i]);
        for (int i = _items.Count - 1; i >= 0; i--) if (_items[i].Read) into.Add(_items[i]);
    }

    /// <summary>
    /// What the bar shows: the newest unread item that waits for the user, else the newest unread one younger than
    /// <paramref name="timeoutMs"/> (0 = no auto-hide). <paramref name="hideAt"/>: when that one times out (or MaxValue).
    /// </summary>
    /// <param name="scope">notification_bar_scope = "tab": only this tab's items count; null: every window's.</param>
    public NotificationItem? BarItem(long now, int timeoutMs, out long hideAt, NotificationScope? scope = null)
    {
        hideAt = long.MaxValue;
        for (int i = _items.Count - 1; i >= 0; i--)
            if (!_items[i].Read && _items[i].Sticky && InScope(_items[i], scope)) return _items[i];
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var it = _items[i];
            if (it.Read || !InScope(it, scope)) continue;
            if (timeoutMs <= 0) return it;
            if (now - it.At < timeoutMs) { hideAt = it.At + timeoutMs; return it; }
            break; // older ones are older still
        }
        return null;
    }

    private static bool InScope(NotificationItem it, NotificationScope? scope) => scope is not { } s || s.IsHere(it.PaneId);

    /// <summary>Unread notifications of the scope's tab.</summary>
    public int CountHere(NotificationScope scope)
    {
        int n = 0;
        for (int i = 0; i < _items.Count; i++) if (!_items[i].Read && scope.IsHere(_items[i].PaneId)) n++;
        return n;
    }

    /// <summary>Unread notifications of other tabs: the bar's "+N in other tabs".</summary>
    public int CountElsewhere(NotificationScope scope)
    {
        int n = 0;
        for (int i = 0; i < _items.Count; i++) if (!_items[i].Read && scope.IsElsewhere(_items[i].PaneId)) n++;
        return n;
    }

    /// <summary>
    /// With nothing of its own to show, the bar still announces news from other tabs for a moment: the newest unread
    /// item elsewhere posted after <paramref name="after"/> and younger than <paramref name="timeoutMs"/> (0 = no limit).
    /// Waiting items get no longer than the rest here: their tab keeps its dot, the bar doesn't camp on every tab.
    /// </summary>
    public NotificationItem? NewestElsewhere(long now, int timeoutMs, long after, NotificationScope scope, out long hideAt)
    {
        hideAt = long.MaxValue;
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var it = _items[i];
            if (it.Read || !scope.IsElsewhere(it.PaneId)) continue;
            if (it.At <= after) return null; // older ones are older still
            if (timeoutMs <= 0) return it;
            if (now - it.At >= timeoutMs) return null;
            hideAt = it.At + timeoutMs;
            return it;
        }
        return null;
    }

    private NotificationItem? Latest(int paneId)
    {
        for (int i = _items.Count - 1; i >= 0; i--) if (_items[i].PaneId == paneId) return _items[i];
        return null;
    }

    private void Changed()
    {
        int unread = 0;
        foreach (var it in _items) if (!it.Read) unread++;
        UnreadCount = unread;
        Version++;
    }

    private static void Prune(Dictionary<int, long> map, long now, int windowMs)
    {
        List<int>? old = null;
        foreach (var (k, at) in map) if (now - at > windowMs) (old ??= []).Add(k);
        if (old is not null) foreach (int k in old) map.Remove(k);
    }

    private static void Prune(Dictionary<int, (long Start, int Count)> map, long now, int windowMs)
    {
        List<int>? old = null;
        foreach (var (k, v) in map) if (now - v.Start > windowMs) (old ??= []).Add(k);
        if (old is not null) foreach (int k in old) map.Remove(k);
    }

    /// <summary>For the agents' transitions: which priority an agent state gets.</summary>
    public static NotificationPriority PriorityFor(AgentState s) =>
        s == AgentState.Waiting ? NotificationPriority.Waiting : NotificationPriority.Normal;
}
