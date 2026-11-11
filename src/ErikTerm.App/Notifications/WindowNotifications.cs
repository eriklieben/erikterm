using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Notifications;

/// <summary>What <see cref="WindowNotifications"/> needs from the process: App implements it, tests use a fake.</summary>
internal interface INotificationProcess : IPaneTabs
{
    NotificationCenter Notifications { get; }
    Settings Settings { get; }
    Dictionary<KeyChord, ActionSpec> Keys { get; }
    string ConfigPath { get; }
    void DrainAgentFocusRequests();
    int NotificationPaneStamp();
    void PruneNotificationPanes(long now);
    string? NotificationTabLabel(int paneId, uint viewer);
    /// <summary>"Workspace 3" or "On DELL U2720Q" when the pane is in another window, away from where you are looking; else null.</summary>
    string? NotificationElsewhere(int paneId, uint viewer);
    /// <summary>Those labels are on show: keeps what they read reasonably fresh.</summary>
    void RefreshNotificationPlaces(long now);
    bool JumpToNotification(NotificationItem item);
}

/// <summary>What <see cref="WindowNotifications"/> needs from its window: TermWindow implements it, tests use a fake.</summary>
internal interface INotificationWindow : IWindowChrome
{
    uint Id { get; }
    bool IsFocused { get; }
    int FocusedPaneId { get; }
    int? ActiveTabId { get; }
    /// <summary>A bar came or went: the panes ease to their new room.</summary>
    void ApplyBarLayout();
    /// <summary>The notifications panel is opening: the pop-ups it replaces close.</summary>
    void CloseOverlaysForPanel(long now);
    /// <summary>The command palette, already filtered.</summary>
    void OpenPalette(string filter);
    string[] ShortcutFor(ActionSpec action);
    /// <summary>Adds the commands that finished since the last look and ran at least notify_command_duration, in every tab.</summary>
    void CollectFinishedCommands(List<FinishedCommandNotice> into, long now);
    void DeliverFinishedCommand(int tabIndex, int paneId, string title, string body, long now);
}

/// <summary>A finished command worth announcing, as the window found it.</summary>
internal readonly record struct FinishedCommandNotice(int TabIndex, int PaneId, FinishedCommand Command, string Text);

/// <summary>
/// One window's notification surfaces: two in-window bars and the Notifications panel. Posting and routing are the
/// process's (<see cref="NotificationCenter"/>, Host/ProcessNotifications.cs); agents post from Agents/WindowAgents.cs.
/// <list type="bullet">
/// <item>the info bar, under the tab bar: questions and warnings. The window owns it because other features post to it
///   too (OSC 52, close confirmation, git operations, themes); the desktop-notification question is posted here;</item>
/// <item>the notification bar, under the tab bar or at the bottom (notification_bar_position): the latest unread
///   notification of the open tab (notification_bar_scope; "all": of any window) with its place, age, [Jump],
///   "N unread" or "+N in other tabs" (opens the panel) and ✕;</item>
/// <item>the panel: the history overlay.</item>
/// </list>
/// The bars take layout room (<see cref="ReserveTop"/>, <see cref="ReserveBottom"/>) while shown, so they never cover
/// terminal text; panes ease to make room.
/// <para>Long-running commands (notify_command_duration, kitty's notify_on_cmd_finish / ghostty's
/// notify-on-command-finish): with shell integration (OSC 133;C … D;status), a command that ran at least that many
/// seconds and finishes in a pane you aren't looking at — another pane or tab, or the window doesn't have focus — is
/// announced like a program's own: the bar ("✓ make finished in 1m 12s", Jump focuses the pane), the history, and the
/// desktop when allowed. The command line comes from OSC 133;C's cmdline_url (erikterm's shell scripts and fish 4 send
/// it), else from the prompt line on screen.</para>
/// </summary>
internal sealed class WindowNotifications(INotificationWindow window, INotificationProcess process, InfoBar infoBar)
{
    private const string AskKey = "os-notifications-ask", NotificationKey = "notification";

    private readonly InfoBar _bar = new();
    private readonly NotificationsPanel _panel = new();
    private readonly List<FinishedCommandNotice> _finished = [];
    private int _version = -1;
    private long _hideAt = long.MaxValue, _ageAt = long.MaxValue;
    private long _elsewhereHiddenAt = long.MinValue; // ✕ on the other-tabs bar: nothing older shows there again
    private int _paneStamp;
    private (int Top, int Bottom) _reserve;
    private bool _overBar;
    private Settings? _settings; // the settings the bar was built with (a reload rebuilds it)

    private static long Now => Environment.TickCount64;

    private bool BarAtBottom => process.Settings.NotificationBarPosition == "bottom";

    /// <summary>The bar's filter: the open tab, or null with notification_bar_scope = "all".</summary>
    private NotificationScope? BarScope => process.Settings.NotificationBarScope == "tab" ? TabScope : null;

    private NotificationScope? TabScope => window.ActiveTabId is { } tab ? new NotificationScope(process, tab) : null;

    /// <summary>The message on the notification bar.</summary>
    public InfoBarMessage? BarMessage => _bar.Current;

    public bool IsPanelOpen => _panel.IsOpen;

    // ---- Layout -----------------------------------------------------------------------------------

    /// <summary>Pixels the bars take below the tab bar.</summary>
    public int ReserveTop =>
        (infoBar.IsShown ? InfoBar.Reserve(window.Theme) : 0) + (_bar.IsShown && !BarAtBottom ? InfoBar.Reserve(window.Theme) : 0);

    /// <summary>Pixels the notification bar takes at the bottom (notification_bar_position = "bottom").</summary>
    public int ReserveBottom => _bar.IsShown && BarAtBottom ? InfoBar.Reserve(window.Theme) : 0;

    // ---- Tick -------------------------------------------------------------------------------------

    /// <summary>Hook from Tick: read on focus, the question bar, the notification bar, the panel, and room for them.</summary>
    public void Tick(long now)
    {
        var c = process.Notifications;
        process.DrainAgentFocusRequests(); // clicked desktop notifications (also without agent_status)
        if (window.IsFocused && c.UnreadCount > 0) c.MarkPaneSeen(window.FocusedPaneId); // looking at it = read

        SyncAskBar(c, now);

        if (!ReferenceEquals(_settings, process.Settings)) { _settings = process.Settings; _version = -1; }
        // Notifications are placed by where their panes are now: a tab switch or a moved pane changes what shows.
        if (_panel.IsOpen || (c.UnreadCount > 0 && BarScope is null)) process.RefreshNotificationPlaces(now); // these name other windows
        int stamp = c.UnreadCount > 0 || _panel.IsOpen ? unchecked(process.NotificationPaneStamp() * 31 + (window.ActiveTabId ?? 0)) : 0;
        if (stamp != _paneStamp)
        {
            _paneStamp = stamp;
            _version = -1;
            _panel.PlacesChanged(TabScope);
        }
        if (c.Version != _version || now >= _hideAt || now >= _ageAt) SyncBar(c, now);
        if (_panel.Tick(now)) window.Redraw();
        process.PruneNotificationPanes(now);

        var reserve = (ReserveTop, ReserveBottom);
        if (reserve != _reserve)
        {
            _reserve = reserve;
            window.ApplyBarLayout(); // panes ease out of the way (or back)
        }
    }

    private void SyncAskBar(NotificationCenter c, long now)
    {
        bool ask = c.AskVisible;
        if (ask == infoBar.Contains(AskKey)) return;
        if (ask) ShowAskBar(c, now);
        else infoBar.Remove(AskKey, now);
        window.Redraw();
    }

    /// <summary>Hook from Tick: commands that finished since the last look, announced in the order they were found.</summary>
    public void TickFinishedCommands(long now)
    {
        window.CollectFinishedCommands(_finished, now);
        if (_finished.Count == 0) return;
        foreach (var (tabIndex, paneId, command, text) in _finished)
        {
            var (title, body) = CommandNotice.Text(text, command.ExitCode, command.DurationMs);
            window.DeliverFinishedCommand(tabIndex, paneId, title, body, now);
        }
        _finished.Clear();
    }

    /// <summary>Hook from the event loop: the next auto-hide or "N min ago" change.</summary>
    public int WakeMs(long now)
    {
        long at = Math.Min(_hideAt, _ageAt);
        int ms = at == long.MaxValue ? int.MaxValue : (int)Math.Clamp(at - now, 1, int.MaxValue);
        return Math.Min(ms, _panel.WakeMs(now));
    }

    public bool IsAnimating(long now) => infoBar.IsAnimating(now) || _bar.IsAnimating(now) || _panel.IsAnimating(now);

    private void SyncBar(NotificationCenter c, long now)
    {
        _version = c.Version;
        window.Redraw();
        var settings = process.Settings;
        var scope = BarScope;
        var item = settings.NotificationBar ? c.BarItem(now, settings.NotificationBarTimeoutMs, out _hideAt, scope) : null;
        int elsewhere = scope is { } counted ? c.CountElsewhere(counted) : 0;
        if (item is null && settings.NotificationBar && scope is { } s &&
            c.NewestElsewhere(now, settings.NotificationBarTimeoutMs, _elsewhereHiddenAt, s, out _hideAt) is not null)
        {
            _ageAt = long.MaxValue;
            _bar.Show(new InfoBarMessage
            {
                Key = NotificationKey,
                Tone = InfoBarTone.Accent,
                Icon = Icons.Bell,
                Text = "New in other tabs",
                Chip = $"+{elsewhere} in other tabs",
                ChipClick = OpenPanel,
                Dismissed = HideElsewhereBar, // nothing is marked read: the tabs keep their markers
            }, window.Theme, now);
            return;
        }
        if (item is null)
        {
            _hideAt = _ageAt = long.MaxValue;
            _bar.Remove(NotificationKey, now);
            return;
        }
        long age = now - item.At;
        _ageAt = now + NotificationText.UntilAgeChanges(age);
        long id = item.Id;
        string chip;
        if (scope is not { } own) chip = $"{c.UnreadCount} unread";
        else chip = elsewhere > 0 ? $"+{elsewhere} in other tabs" : $"{c.CountHere(own)} unread";
        var m = new InfoBarMessage
        {
            Key = NotificationKey,
            Tone = item.Sticky ? InfoBarTone.Danger : InfoBarTone.Accent,
            Icon = Icons.Bell,
            Dot = item.AgentState,
            Label = scope is null ? PlaceAnywhere(item) : item.Place, // it's on this tab: where in the tab, not which
            Text = item.Count > 1 ? $"{item.Title}  ×{item.Count}" : item.Title,
            Detail = item.Body,
            Meta = NotificationText.Age(age),
            Chip = chip,
            ChipClick = OpenPanel,
            Buttons = [new InfoBarButton("Jump", () => Jump(id), primary: true)],
            Dismissed = () => process.Notifications.MarkRead(id),
        };
        _bar.Show(m, window.Theme, now);
    }

    private string PlaceAnywhere(NotificationItem item) =>
        NotificationText.Join(process.NotificationElsewhere(item.PaneId, window.Id) ?? "", item.Where(window.Id));

    private void HideElsewhereBar()
    {
        _elsewhereHiddenAt = Now;
        _version = -1;
    }

    private void ShowAskBar(NotificationCenter c, long now)
    {
        infoBar.Show(new InfoBarMessage
        {
            Key = AskKey,
            Priority = NotificationCenter.AskPriority,
            Tone = InfoBarTone.Warning,
            Icon = Icons.Bell,
            Text = $"{c.AskApp} wants to send desktop notifications",
            Buttons =
            [
                new InfoBarButton("Allow", () => DecideDesktop(OsNotificationMode.On), primary: true),
                new InfoBarButton("Only in erikterm", () => DecideDesktop(OsNotificationMode.Off)),
                new InfoBarButton("Settings…", () => window.OpenPalette("Notifications")),
            ],
            Dismissed = () => process.Notifications.DeferAsk(), // ✕ = decide later
        }, window.Theme, now);
    }

    // ---- Drawing (hooks from DrawOverlays) ----------------------------------------------------------

    public bool AnyBarVisible(long now) => infoBar.IsVisible(now) || _bar.IsVisible(now);

    /// <summary>The bars, in window pixels; <paramref name="tabBar"/> is the room the tab bar takes.</summary>
    public void DrawBars(DrawList dl, int windowWidth, int windowHeight, Padding pad, int tabBar, long now)
    {
        var theme = window.Theme;
        float x = pad.Left, width = Math.Max(1, windowWidth - pad.Left - pad.Right), height = InfoBar.Height(theme);
        float top = pad.Top + tabBar;
        infoBar.Draw(dl, theme, new RectF(x, top, width, height), fromTop: true, now);
        if (infoBar.IsShown) top += InfoBar.Reserve(theme);
        if (BarAtBottom)
            _bar.Draw(dl, theme, new RectF(x, windowHeight - pad.Bottom - height, width, height), fromTop: false, now);
        else
            _bar.Draw(dl, theme, new RectF(x, top, width, height), fromTop: true, now);
    }

    public void DrawPanel(DrawList dl, Theme theme, RectF area, long now) => _panel.Draw(dl, theme, area, now);

    // ---- Input ------------------------------------------------------------------------------------

    /// <summary>True when the pointer is over a bar; <paramref name="overControl"/>: over one of its buttons.</summary>
    public bool BarsMouseMove(float x, float y, out bool overControl)
    {
        bool overInfo = infoBar.HandleMouseMove(x, y, out bool c1);
        bool overBar = _bar.HandleMouseMove(x, y, out bool c2);
        bool over = overInfo || overBar;
        if (c1 || c2 || over != _overBar) window.Redraw();
        _overBar = over;
        overControl = infoBar.OverControl || _bar.OverControl;
        return over;
    }

    public bool BarsMouseDown(float x, float y) => infoBar.HandleMouseDown(x, y) || _bar.HandleMouseDown(x, y);

    public void PanelMouseMove(float x, float y, long now) => _panel.HandleMouseMove(x, y, now);

    public void PanelMouseDown(float x, float y, long now) => _panel.HandleMouseDown(x, y, now);

    /// <summary>Hook from OnKeyDown while the panel is open.</summary>
    public void PanelKey(uint key, ushort mod, long now)
    {
        if (KeyNames.Chord(key, mod) is { } chord && process.Keys.TryGetValue(chord, out var action) && action.Name == "NotificationsPanel")
        {
            _panel.Close(now);
            return;
        }
        _panel.HandleKey(key, mod, now);
    }

    public void ClosePanel(long now) => _panel.Close(now);

    // ---- Actions ----------------------------------------------------------------------------------

    /// <summary>Hook from ExecuteMore: NotificationsPanel, ClearNotifications, ToggleDesktopNotifications.</summary>
    public void Execute(string name)
    {
        switch (name)
        {
            case "NotificationsPanel":
                if (_panel.IsOpen) _panel.Close(Now);
                else OpenPanel();
                break;
            case "ClearNotifications": process.Notifications.Clear(); window.Toast(ToastKind.Success, "Notifications cleared"); break;
            case "ToggleDesktopNotifications":
                DecideDesktop(process.Notifications.Mode == OsNotificationMode.On ? OsNotificationMode.Off : OsNotificationMode.On);
                break;
        }
    }

    private void OpenPanel()
    {
        long now = Now;
        window.CloseOverlaysForPanel(now);
        _panel.Center = process.Notifications;
        _panel.Viewer = window.Id;
        _panel.Jump = item => Jump(item.Id);
        _panel.TabLabel = paneId => process.NotificationTabLabel(paneId, window.Id);
        _panel.PlacesChanged(TabScope);
        _panel.Open(window.Theme, now);
    }

    private void Jump(long id)
    {
        if (process.Notifications.Find(id) is not { } item) return;
        if (!process.JumpToNotification(item)) window.Toast(ToastKind.Info, "That pane has closed", item.Title);
    }

    /// <summary>[Allow] / [Only in erikterm] / the palette: remembered in preferences.json unless the config decides.</summary>
    private void DecideDesktop(OsNotificationMode mode)
    {
        var c = process.Notifications;
        if (c.ConfigMode is { } fixedMode && fixedMode != mode)
        {
            window.Toast(ToastKind.Info, $"os_notifications = \"{PreferencesStore.ModeWord(fixedMode)}\" is set in {Path.GetFileName(process.ConfigPath)}",
                "Remove it (or change it) to choose here");
            return;
        }
        c.Decide(mode);
        switch (mode)
        {
            case OsNotificationMode.On: window.Toast(ToastKind.Success, "Desktop notifications on", "Change it in the palette: Notifications"); break;
            case OsNotificationMode.Off: window.Toast(ToastKind.Success, "Notifications stay in erikterm", "The notification bar shows them; change it in the palette"); break;
            default: window.Toast(ToastKind.Info, "erikterm will ask again", "The next time something wants to notify the desktop"); break;
        }
    }

    /// <summary>Palette entries (hook from BuildCommands).</summary>
    public void AddCommands(List<Command> list)
    {
        var c = process.Notifications;
        void Add(string title, int icon, string action, Func<bool>? enabled = null)
        {
            if (!ActionSpec.TryParse(action, out var spec, out _)) return;
            list.Add(new Command { Id = action, Category = "Notifications", Title = title, Icon = icon, Action = spec, IsEnabled = enabled, Shortcut = window.ShortcutFor(spec) });
        }
        Add(c.UnreadCount > 0 ? $"Notifications…  ({c.UnreadCount} unread)" : "Notifications…", Icons.Bell, "NotificationsPanel");
        Add("Clear Notifications", Icons.Clear, "ClearNotifications", () => process.Notifications.Items.Count > 0);
        string now = c.Mode switch { OsNotificationMode.On => "on", OsNotificationMode.Off => "off", _ => "not decided" };
        Add($"Toggle Desktop Notifications  (now {now})", c.Mode == OsNotificationMode.On ? Icons.Bell : Icons.BellOff, "ToggleDesktopNotifications");
        void Choice(string id, string title, OsNotificationMode mode, int icon)
        {
            bool current = c.Mode == mode;
            list.Add(new Command
            {
                Id = id, Category = "Notifications", Title = current ? title + "  (current)" : title, Icon = icon,
                Run = () => DecideDesktop(mode), IsEnabled = () => !current,
            });
        }
        Choice("notifications:on", "Desktop Notifications: Allow", OsNotificationMode.On, Icons.Bell);
        Choice("notifications:off", "Desktop Notifications: Only in erikterm", OsNotificationMode.Off, Icons.BellOff);
        Choice("notifications:ask", "Desktop Notifications: Ask Me Next Time", OsNotificationMode.Ask, Icons.Info);
    }
}
