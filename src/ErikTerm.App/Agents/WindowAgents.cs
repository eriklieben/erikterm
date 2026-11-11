using ErikTerm.App.Integrations;
using ErikTerm.App.Ipc;
using ErikTerm.App.Native;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Ui;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Agents;

/// <summary>
/// Coding-agent status for one window. Every <see cref="TickMs"/> each pane (all tabs) is polled: the foreground process
/// identifies the agent (re-read only when the process group changes), OSC 7501 records / notifications are
/// picked up when the terminal's signal version moved, and the agent's screen rules run only when the content
/// changed. <see cref="AgentStatusTracker"/> turns that into waiting / working / done / idle; transitions post
/// notifications (the notification bar, the desktop: NotificationCenter via Host/ProcessNotifications.cs) and flash the taskbar.
/// Drawing: tab dots (TabBar), the pane pill and the waiting rim (<see cref="AgentBadge"/>), the overview
/// (<see cref="AgentsOverlay{TPane}"/>, which lists every window's agents via Host/ProcessPanes.cs). The taskbar flash is per
/// window; notifications are the process's (Host/ProcessNotifications.cs). Nothing here is saved with the session.
/// <para>
/// Send to Agent…: the palette lists every pane running a coding agent, in every window, teammate pills included; the
/// selection (or, without one, a prompt typed into the palette) goes there as a bracketed paste through the pane's input
/// queue, like <c>erikterm cli send-text</c>. Enter leaves it in the agent's input and jumps there to finish it;
/// Ctrl+Enter also presses Enter there and stays put.
/// </para>
/// TermWindow forwards its hook points here: tick, drawing, keys and mouse of the overview, actions and the IPC switch.
/// </summary>
internal sealed class WindowAgents<TPane>(IAgentWindow<TPane> window, IAgentProcess<TPane> process, Lock gate)
    where TPane : class, IAgentPane
{
    private const int TickMs = 400;
    private const int PulseFrameMs = 83;          // ~12 fps, only while a working agent is on screen
    private const ushort AnyModifier = Sdl.KMOD_SHIFT | Sdl.KMOD_CTRL | Sdl.KMOD_LALT | Sdl.KMOD_GUI;

    private readonly TerminalScreenText _screenText = new();
    private readonly AgentsOverlay<TPane> _overlay = new();
    private readonly List<TerminalNotification> _notifyScratch = [];
    private long _tickAt, _pulseAt, _overlayRefreshAt;
    private bool _pulse, _anyAgents, _windowFocused = true, _wereEnabled = true;

    private static long Now => Environment.TickCount64;

    /// <summary>Some pane runs an agent: the corner may hold a pill.</summary>
    public bool AnyAgents => _anyAgents;

    public bool OverlayOpen => _overlay.IsOpen;

    public void CloseOverlay(long now) => _overlay.Close(now);
    public void OverlayMouseMove(float x, float y, long now) => _overlay.HandleMouseMove(x, y, now);
    public void OverlayMouseDown(float x, float y, long now) => _overlay.HandleMouseDown(x, y, now);
    public void DrawOverlay(DrawList dl, Theme overlayTheme, RectF area, long now) => _overlay.Draw(dl, overlayTheme, area, now);

    // ---- Hooks ------------------------------------------------------------------------------------

    /// <summary>Hook from Tick: polls agents a few times a second and keeps badges in step with focus.</summary>
    public void Tick(long now)
    {
        if (!process.Settings.AgentStatus)
        {
            if (_wereEnabled) Disable(now);
            return;
        }
        _wereEnabled = true;
        if (_overlay.Jump is null)
        {
            _overlay.Source = process.AgentRows; // every window's agents (Host/ProcessPanes.cs)
            _overlay.Jump = process.JumpToAgentPane;
            _overlay.Answer = Answer;
        }
        if (window.IsFocused != _windowFocused)
        {
            _windowFocused = window.IsFocused;
            window.Focused.Agent.LastInputAt = now; // focus reports (CSI I / O) make programs redraw
            if (_windowFocused) window.FlashTaskbar(untilFocused: false);
            _tickAt = 0; // seen / unseen right away
        }
        process.DrainAgentFocusRequests(); // clicked notifications (Host/ProcessPanes.cs)

        if (now >= _tickAt) PollAll(now);
        if (_anyAgents) UpdateBadgeTargets(now);
        if (_pulse && now >= _pulseAt)
        {
            _pulseAt = now + PulseFrameMs;
            window.Redraw();
        }
        if (_overlay.IsOpen && now >= _overlayRefreshAt)
        {
            _overlayRefreshAt = now + 500;
            _overlay.Refresh();
        }
    }

    private void PollAll(long now)
    {
        _tickAt = now + TickMs;
        window.SyncTabs();
        bool any = false, pulse = false;
        int activeTab = window.ActiveTabIndex;
        for (int t = 0; t < window.TabCount; t++)
        {
            if (window.TabLayout(t) is not { } layout) continue;
            bool active = t == activeTab;
            var tabState = AgentState.None;
            var leaves = layout.LeafList;
            for (int i = 0; i < leaves.Count; i++)
            {
                var pane = leaves[i];
                Poll(t, pane, active, now);
                var s = pane.AgentState;
                if (AgentStateText.Urgency(s) > AgentStateText.Urgency(tabState)) tabState = s;
                any |= pane.AgentActive;
                if (s == AgentState.Working && (active && (window.Zoomed is null || pane == window.Zoomed) || window.TabBarShown)) pulse = true;
            }
            window.ShowTabAgentState(t, tabState);
        }
        _anyAgents = any;
        _pulse = pulse;
    }

    /// <summary>Hook from the event loop: wake for the next poll while agents run, or the next pulse frame.</summary>
    public int WakeMs(long now)
    {
        if (!process.Settings.AgentStatus) return int.MaxValue;
        int ms = int.MaxValue;
        if (_pulse) ms = (int)Math.Max(1, _pulseAt - now);
        if (_anyAgents) ms = (int)Math.Min(ms, Math.Max(1, _tickAt - now));
        return ms;
    }

    /// <summary>Hook from IsAnimating: badge fades (the working pulse is a timed wake, not animation).</summary>
    public bool IsAnimating(long now)
    {
        if (_overlay.IsAnimating(now)) return true;
        if (!_anyAgents) return false;
#pragma warning disable S3267 // IsAnimating runs every frame: no LINQ iterator or closure here.
        foreach (var p in window.Layout.LeafList)
            if (p.Agent.Pill.IsAnimating(now) || p.Agent.Rim.IsAnimating(now)) return true;
#pragma warning restore S3267
        return false;
    }

    /// <summary>Hook from DrawOverlays, above the panes: pills and waiting rims of the visible panes.</summary>
    public void DrawBadges(DrawList dl, long now)
    {
        if (!_anyAgents || !process.Settings.AgentStatus) return;
        foreach (var pane in window.VisiblePanes())
        {
            var a = pane.Agent;
            if (!a.Active && a.Pill.Value(now) <= 0.002f) continue;
            var r = pane.AgentBounds(now);
            AgentBadge.Rim(dl, window.Theme, a, r, now);
            AgentBadge.Pill(dl, window.Theme, a, r, window.ZoomBadgeShown(now), now);
        }
    }

    /// <summary>Hook from RefreshTabTitles: an agent's name (and the task it put in the title) instead of "claude".</summary>
    public string TabLabel(TPane pane, string label)
    {
        if (!process.Settings.AgentStatus || pane.Agent.Definition is not { } def) return label;
        string title = pane.Term.Title;
        if (title is "" or "erikterm") return def.Name;
        // Claude Code: "✳ Fix the parser" / "◐ Fix the parser" → "Claude: Fix the parser".
        var t = title.AsSpan();
        if (AgentTitle.Parse(title) != AgentState.None) t = t[1..].TrimStart();
        if (t.IsEmpty || t.Equals(def.Name, StringComparison.OrdinalIgnoreCase) || t.Equals(def.Commands[0], StringComparison.Ordinal)) return def.Name;
        return $"{def.Name}: {t}";
    }

    // ---- Polling ----------------------------------------------------------------------------------

    private void Poll(int tabIndex, TPane pane, bool activeTab, long now)
    {
        var a = pane.Agent;
        Identify(pane, a, now);
        var def = a.Definition;
        bool signals;
        lock (gate) signals = ReadTerminalChanges(pane.Term, a, def, now);

        if (signals && _notifyScratch.Count > 0)
        {
            foreach (var n in _notifyScratch) OnTerminalNotification(tabIndex, pane, activeTab, n, now);
            _notifyScratch.Clear();
        }

        bool seen = activeTab && pane == window.Focused && window.IsFocused;
        var obs = new AgentObservation(def is not null, a.Heuristic, a.TitleState, a.LastOutputAt, a.LastInputAt, seen);
        if (a.Tracker.Update(obs, now) is { } transition) OnTransition(tabIndex, pane, transition, seen, now);
    }

    /// <summary>Picks up what changed on the terminal since the last poll (under the terminal lock). True when agent signals arrived.</summary>
    private bool ReadTerminalChanges(Terminal term, AgentPane a, AgentDefinition? def, long now)
    {
        bool signals = false;
        term.ProgramStatusEnabled = true; // answer OSC 7501 probes (off while agent_status = false)
        if (term.Columns != a.Cols || term.Rows != a.Rows)
        {
            (a.Cols, a.Rows) = (term.Columns, term.Rows);
            a.LastInputAt = now; // the redraw a resize asks for isn't work
        }
        if (term.ContentVersion != a.ContentVersion)
        {
            a.ContentVersion = term.ContentVersion;
            a.LastOutputAt = now;
            if (def is not null && def.Rules.Length > 0) ClassifyScreen(term, a, def, now);
        }
        if (!ReferenceEquals(term.Title, a.Title))
        {
            a.Title = term.Title;
            a.TitleState = def is null ? AgentState.None : AgentTitle.Parse(term.Title);
        }
        if (term.AgentSignalVersion != a.SignalVersion)
        {
            a.SignalVersion = term.AgentSignalVersion;
            signals = true;
            ReadProgramStatus(term, a, now);
            term.DrainNotifications(_notifyScratch);
        }
        if (term.BellCount != a.BellCount)
        {
            // Claude Code's "terminal_bell" channel: a bell from an agent means it wants you.
            if (def is not null && a.BellCount >= 0) a.Tracker.Report(new AgentReport(AgentState.Waiting, AgentSource.Notification, now, "rang the bell"));
            a.BellCount = term.BellCount;
        }
        return signals;
    }

    private void ClassifyScreen(Terminal term, AgentPane a, AgentDefinition def, long now)
    {
        _screenText.Attach(term);
        var rule = ScreenClassifier.Classify(def.Rules, _screenText);
        a.Heuristic = rule is null ? default : new AgentReport(rule.State, AgentSource.Heuristic, now, rule.Message, rule.Kind);
    }

    /// <summary>
    /// SendAgentNewline (Shift+Enter): ESC CR to a coding agent that reads it as a newline while the kitty keyboard
    /// protocol is off (KeyEncoder.ShiftEnterForAgent); otherwise the key goes to the program as if it weren't bound.
    /// </summary>
    public void SendNewline(uint key, ushort mod)
    {
        var pane = window.Focused;
        KittyKeyboardModes kitty;
        lock (gate) kitty = pane.Term.KeyboardFlags;
        // agent_status off: Definition stays null, so identify the agent here (one /proc read per key press).
        var agent = pane.Agent.Definition ?? pane.IdentifyForeground();
        if (KeyEncoder.ShiftEnterForAgent(agent?.Id, kitty) is { } seq)
        {
            window.SendInput(pane, seq);
            if (key != 0) window.BroadcastKey(key, mod); // the other panes get the plain key (Windowing/WindowBroadcast.cs)
        }
        else if (key != 0) window.SendKey(key, mod); // Windowing/KeyInputRouter.cs
    }

    /// <summary>Which agent runs in the pane; /proc is read again only when the foreground process group changes (and briefly after).</summary>
    private void Identify(TPane pane, AgentPane a, long now)
    {
        int pgid = pane.ForegroundProcessGroup();
        if (pgid != a.Pgid) { a.Pgid = pgid; a.IdentifiedAt = now; }
        else if (now - a.IdentifiedAt > 3000) return; // settled: an exec'd wrapper or process.title change shows up within 3 s
        var def = pane.IdentifyForeground();
        if (def is null)
            lock (gate)
                foreach (var r in pane.Term.ProgramStatus) { def = AgentCatalog.ByStatusApp(r.App); if (def is not null) break; }
        if (def == a.Definition) return;
        if (a.Definition is not null && def is null)
        {
            // The agent left the foreground: its in-flight records can't be true any more.
            lock (gate) pane.Term.ClearProgramStatus(transientOnly: true);
            a.Tracker.Reset(now);
            a.Heuristic = default;
            a.Usage = null;
        }
        a.Definition = def;
        a.ContentVersion = -1; // classify the screen with the new rules
        a.Title = null;
        a.PillText = "";
        window.Redraw();
    }

    /// <summary>OSC 7501 records → one report: a blocked record (any id) first, else the main record, else a working one.</summary>
    private static void ReadProgramStatus(Terminal term, AgentPane a, long now)
    {
        ProgramStatusRecord? pick = null;
        foreach (var r in term.ProgramStatus)
        {
            if (r.State == ProgramState.Blocked) { pick = r; break; }
            if (r.Id.Length == 0) pick = r;
            else if (pick is null || pick.Id.Length > 0 && r.State == ProgramState.Working) pick = r;
        }
        if (pick is null) { a.Tracker.ClearProgramStatus(); return; }
        a.Definition ??= AgentCatalog.ByStatusApp(pick.App);
        var state = pick.State switch
        {
            ProgramState.Working => AgentState.Working,
            ProgramState.Blocked => AgentState.Waiting,
            ProgramState.Done or ProgramState.Error => AgentState.Done,
            _ => AgentState.Idle,
        };
        string? msg = pick.Message;
        if (pick.State == ProgramState.Error) msg = "error" + (pick.Message is { } m ? ": " + m : "");
        a.Tracker.Report(new AgentReport(state, AgentSource.Osc7501, now, msg, pick.Kind, pick.Progress));
    }

    private void UpdateBadgeTargets(long now)
    {
        float ms = process.Settings.AnimationDurationMs * 1.6f;
        foreach (var pane in window.Layout.LeafList) UpdateBadgeTarget(pane, ms, now);
    }

    private void UpdateBadgeTarget(TPane pane, float ms, long now)
    {
        var a = pane.Agent;
        var s = a.State;
        bool focused = pane == window.Focused && window.IsFocused;
        float pill = s is AgentState.Working or AgentState.Waiting or AgentState.Done || s == AgentState.Idle && !focused ? 1 : 0;
#pragma warning disable S1244 // Target only ever holds the 0 or 1 set here, so exact comparison is the point.
        if (a.Pill.Target != pill) a.Pill.Set(pill, now, pill > 0 ? ms * 0.8f : ms * 2.5f);
        float rim = s == AgentState.Waiting ? 1 : 0;
        if (a.Rim.Target != rim) a.Rim.Set(rim, now, ms);
#pragma warning restore S1244
        if (s != AgentState.None) a.RefreshText();
    }

    private void Disable(long now)
    {
        _wereEnabled = false;
        _anyAgents = _pulse = false;
        for (int t = 0; t < window.TabCount; t++)
        {
            window.ClearTabAgentState(t);
            if (window.TabLayout(t) is not { } layout) continue;
            foreach (var p in layout.LeafList)
            {
                p.Agent.Tracker.Reset(now);
                p.Agent.Definition = null;
                lock (gate) { p.Term.ProgramStatusEnabled = false; p.Term.ClearProgramStatus(transientOnly: false); }
                p.Agent.Pgid = -2;
                p.Agent.Pill.Snap(0);
                p.Agent.Rim.Snap(0);
            }
        }
        _overlay.Close(now);
        window.Redraw();
    }

    // ---- Transitions & notifications --------------------------------------------------------------

    private void OnTransition(int tabIndex, TPane pane, AgentTransition t, bool seen, long now)
    {
        var a = pane.Agent;
        a.RefreshText();
        window.Redraw();
        if (t.From == AgentState.Waiting && t.To != AgentState.Waiting) process.Notifications.ResolveWaiting(pane.Id); // answered
        if (seen) return;
        if (t.To is not (AgentState.Waiting or AgentState.Done)) return;
        if (t.From == AgentState.Waiting && t.To == AgentState.Done) return; // answered elsewhere: nothing new to say

        string who = pane.Teammate is { } tm ? $"{a.Name} ({tm.Name})" : a.Name; // an agent-team teammate (Teammates/WindowTeammates.cs)
        string title = t.To == AgentState.Waiting
            ? $"{who} {AgentStateText.Describe(t.To, t.Kind)}"
            : $"{who} finished";
        var priority = t.To == AgentState.Waiting ? NotificationPriority.Waiting : NotificationPriority.Normal;
        Deliver(tabIndex, pane, NotificationSource.Agent, priority, a.Name, title, t.Message ?? "", t.To, seen, now);
    }

    /// <summary>A program asked for a desktop notification (OSC 9 / 777 / 99).</summary>
    private void OnTerminalNotification(int tabIndex, TPane pane, bool activeTab, TerminalNotification n, long now)
    {
        var a = pane.Agent;
        if (process.Settings.AgentStatus && a.Definition is not null)
        {
            // An agent's notification means it wants the user; the transition notifies (with this text).
            a.Tracker.Report(new AgentReport(AgentState.Waiting, AgentSource.Notification, now, n.Body));
            return;
        }
        bool seen = activeTab && pane == window.Focused && window.IsFocused;
        string app = pane.ForegroundName ?? "";
        Deliver(tabIndex, pane, NotificationSource.Program, NotificationPriority.Normal, app, n.Title ?? "", n.Body, // untitled: the body is the title
            AgentState.None, seen, now);
    }

    /// <summary>
    /// Everything goes through the process's NotificationCenter (Host/ProcessNotifications.cs), which records it for the bar and
    /// the panel and decides on the desktop; a toast only when the bar is off. Urgent + unfocused: the taskbar flashes.
    /// </summary>
    public void Deliver(int tabIndex, TPane pane, NotificationSource source, NotificationPriority priority, string app, string title, string body,
        AgentState state, bool seen, long now)
    {
        bool urgent = priority == NotificationPriority.Waiting;
        var request = new NotificationRequest(pane.Id, source, priority, app, title, body, window.Id, window.TabCount > 1 ? $"Tab {tabIndex + 1}" : "",
            PaneDirName(pane), state);
        var route = process.Notifications.Post(request, new NotificationContext(seen, process.AnyWindowFocused, now));
        if (route == NotificationRoute.None) return;
        if ((route & NotificationRoute.Toast) != 0) // in the window you're in
            window.ToastInFocusedWindow(urgent ? ToastKind.Error : ToastKind.Success, title.Length > 0 ? title : body, title.Length > 0 && body.Length > 0 ? body : request.Place);
        if (urgent && !window.IsFocused && process.Settings.AgentUrgency) window.FlashTaskbar(untilFocused: true);
        window.Redraw();
    }

    // ---- Navigation -------------------------------------------------------------------------------

    /// <summary>Shows <paramref name="pane"/> if it is in this window (its tab, unzoomed, focused); false when it isn't.</summary>
    public bool JumpToPane(TPane pane)
    {
        window.SyncTabs();
        for (int i = 0; i < window.TabCount; i++)
        {
            if (!window.TabLayout(i).Contains(pane)) continue;
            if (i != window.ActiveTabIndex) window.ActivateTab(i);
            if (window.Zoomed is not null && window.Zoomed != pane) window.SetZoom(null);
            window.Focus(pane);
            _tickAt = 0;
            window.Redraw();
            return true;
        }
        return false;
    }

    /// <summary>Hook from Execute: AgentsOverview.</summary>
    public void OpenOverview()
    {
        long now = Now;
        window.CloseOverlaysForAgents(now);
        _tickAt = 0;
        Tick(now);
        _overlay.Open(window.Theme, now);
        _overlayRefreshAt = now + 500;
    }

    /// <summary>Hook from OnKeyDown. True: the overview is open and took the key.</summary>
    public bool OverlayKey(uint key, ushort mod, long now)
    {
        if (!_overlay.IsOpen) return false;
        if (KeyNames.Chord(key, mod) is { } chord && process.Keys.TryGetValue(chord, out var action) && action.Name == "AgentsOverview")
            _overlay.Close(now);
        else
            _overlay.HandleKey(key, now, (mod & AnyModifier) == 0);
        window.SuppressText = true;
        return true;
    }

    /// <summary>
    /// A row's answer was picked. Under the terminal lock, so nothing the agent wrote can land between the check and the
    /// write: the pane must still be waiting, in the same state, with the same prompt on its live screen. If not, nothing
    /// is sent and the user is told. What can't be excluded is the agent moving on after its last output reached us.
    /// </summary>
    private void Answer(AgentRow<TPane> row, AgentAnswer answer)
    {
        var (pane, a) = (row.Pane, row.Pane.Agent);
        string? refused;
        lock (gate)
        {
            refused = WhyStale(pane, row);
            if (refused is null)
            {
                window.SendInput(pane, answer.Keys);
                (a.AnsweredPrompt, a.AnsweredSince) = (row.Prompt!.Fingerprint, a.Tracker.Since);
            }
        }
        _overlay.Refresh();
        if (refused is null) window.Toast(ToastKind.Success, $"Answered {a.Name}: {answer.Label}");
        else window.Toast(ToastKind.Info, $"Nothing sent to {a.Name}", refused);
        window.Redraw();
    }

    /// <summary>Why a row's prompt can no longer be answered, or null when it still can (under the terminal lock).</summary>
    private string? WhyStale(TPane pane, AgentRow<TPane> row)
    {
        var a = pane.Agent;
        if (pane.Exited) return "It has exited";
        if (pane.ForegroundProcessGroup() != a.Pgid) return "It is no longer in the foreground";
        if (a.State != AgentState.Waiting || a.Tracker.Since != row.Since) return "It is no longer waiting on that question";
        return row.Prompt is { } asked && OpenPrompt(pane)?.Fingerprint == asked.Fingerprint ? null : "The question has changed";
    }

    /// <summary>This window's agents for the overview / `cli agents` (App.AgentRows merges and sorts every window's).</summary>
    public void CollectRows(List<AgentRow<TPane>> rows, bool manyWindows, long now)
    {
        window.SyncTabs();
        int activeTab = window.ActiveTabIndex;
        for (int t = 0; t < window.TabCount; t++)
        {
            foreach (var p in window.TabLayout(t).LeafList)
            {
                var a = p.Agent;
                if (!a.Active || a.State == AgentState.None) continue;
                rows.Add(BuildRow(p, t, activeTab, manyWindows, now));
            }
        }
    }

    private AgentRow<TPane> BuildRow(TPane p, int t, int activeTab, bool manyWindows, long now)
    {
        var a = p.Agent;
        string where = manyWindows ? $"Window {window.Id} · Tab {t + 1} · {PaneDirName(p)}" : $"Tab {t + 1} · {PaneDirName(p)}";
        string age = Cli.AgentCli.Duration((int)((now - a.Tracker.Since) / 1000));
        bool focused = window.IsCurrentWindow && t == activeTab && p == window.Focused;
        return new AgentRow<TPane>(p, t, window.Id, window.TabId(t), a.State, RowTitle(p), RowDetail(a), where, age, a.Usage, focused, a.Tracker.Since, OpenPrompt(p));
    }

    private static string RowTitle(TPane p)
    {
        var a = p.Agent;
        string state = AgentStateText.Describe(a.State, a.Tracker.Kind);
        if (p.Teammate is { } tm) return $"{a.Name} · {tm.Name}{(tm.Parked ? " (pill)" : "")} · {state}";
        return $"{a.Name} · {state}";
    }

    private static string RowDetail(AgentPane a)
    {
        string usage = a.UsageText;
        string detail = a.Tracker.Message ?? "";
        if (usage.Length > 0) detail = detail.Length > 0 ? $"{usage} · {detail}" : usage;
        if (a.Usage?.Model is { } model) detail = $"{model} · {detail}".TrimEnd(' ', '·');
        return detail;
    }

    /// <summary>A directory for messages and the overview: the agent's reported one, or the pane's, with $HOME as ~.</summary>
    public static string PaneDirName(TPane p)
    {
        string? cwd = p.Agent.UsageCwd ?? p.KnownDirectory;
        if (cwd is null) return p.Label ?? "";
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (cwd == home) return "~";
        return cwd.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + cwd[home.Length..] : cwd;
    }

    /// <summary>Palette entries (hook from BuildCommands).</summary>
    public void AddCommands(Action<string, string, int, string, Func<bool>?> add) =>
        add("Agents", "Agents…", Icons.Sparkle, "AgentsOverview", () => process.Settings.AgentStatus);

    // ---- IPC ------------------------------------------------------------------------------------

    public IpcResponse IpcSetStatus(TPane pane, IpcRequest r)
    {
        var state = AgentStateText.Parse(r.State) ?? throw new IpcException($"invalid state '{r.State}' (expected waiting, working, done, idle or clear)");
        string? msg = r.Message is { } m ? AgentSignals.Clean(m, AgentSignals.MaxMessageChars) : null;
        string? kind = r.Kind is "permission" or "question" or "auth" ? r.Kind : null;
        pane.Agent.Tracker.Report(new AgentReport(state, AgentSource.Hook, Now, msg, kind));
        if (pane.Agent.Definition is null) { pane.Agent.Pgid = -2; } // look again: hooks prove an agent runs here
        _tickAt = 0;
        return IpcResponse.Success();
    }

    public IpcResponse IpcReportUsage(TPane pane, IpcRequest r)
    {
        if (r.Usage is { } u)
        {
            pane.Agent.Usage = new AgentUsage(u.Model is { } model ? AgentSignals.Clean(model, 64) : null, u.CostUsd, u.ContextPercent,
                u.ContextTokens, u.ContextSize, u.SessionId);
            pane.Agent.UsageCwd = u.Cwd is { } c && Directory.Exists(c) ? c : null;
            pane.Agent.RefreshText();
            window.Redraw();
        }
        return IpcResponse.Success();
    }

    /// <summary>
    /// A review's comments, into the agent's input like Send to Agent. Checked here, on the main thread and against the
    /// pane as it is now: the same pane may hold a shell by the time the request arrives, and Enter there would run the text.
    /// </summary>
    public IpcResponse IpcSendToAgent(TPane pane, IpcRequest r)
    {
        if (r.PaneId is null) throw new IpcException("send-to-agent needs a pane id");
        if (RunningAgent(pane) is not { } agent) throw new IpcException("That pane no longer runs a coding agent");
        var a = pane.Agent;
        if (AgentSend.AwaitsAnswer(a.Tracker.State, a.Tracker.Source, a.Heuristic.State))
            throw new IpcException($"{agent.Name} is waiting for an answer: answer it there first, then send again");
        bool bracketed;
        lock (gate) bracketed = pane.Term.BracketedPaste;
        var (payload, _) = AgentSend.Payload(r.Text ?? "", bracketed, process.Settings.PasteSanitize, r.Submit);
        if (payload.Length == 0) throw new IpcException("Nothing to send");
        window.SendInput(pane, payload);
        return IpcResponse.Success();
    }

    /// <summary>The agent running in the pane right now; polling knows it only a moment late, so the foreground process decides.</summary>
    private static AgentDefinition? RunningAgent(TPane pane)
    {
        if (pane.Exited) return null;
        if (pane.IdentifyForeground() is { } live) return live;
        return pane.Agent.Definition is { } known && pane.ForegroundProcessGroup() == pane.Agent.Pgid ? known : null; // recognized by its OSC 7501 status
    }

    /// <summary>One `cli agents` entry for a row (Host/ProcessPanes.cs lists every window).</summary>
    public static AgentInfo ToAgentInfo(AgentRow<TPane> row, long now)
    {
        var a = row.Pane.Agent;
        return new AgentInfo
        {
            WindowId = (int)row.WindowId, PaneId = row.Pane.Id, TabId = row.TabId, TabIndex = row.TabIndex,
            Agent = a.Definition?.Id ?? "unknown", Name = a.Name,
            State = AgentStateText.Word(a.State), Kind = a.Tracker.Kind, Message = a.Tracker.Message,
            Source = a.Tracker.Source.ToString().ToLowerInvariant(),
            SecondsInState = (int)((now - a.Tracker.Since) / 1000), Progress = a.Tracker.Progress,
            Cwd = PaneDirName(row.Pane), IsFocused = row.Focused,
            Usage = a.Usage is { } u ? new UsageInfo
            {
                Model = u.Model, CostUsd = u.CostUsd, ContextPercent = u.ContextPercent, ContextTokens = u.ContextTokens,
                ContextSize = u.ContextSize, SessionId = u.SessionId,
            } : null,
        };
    }

    /// <summary>Hook from ListPanes: <c>agent</c> / <c>agent_status</c> fields.</summary>
    public static (string? Agent, string? Status) PaneFields(TPane p)
    {
        if (p.Agent.Active && p.Agent.State != AgentState.None) return (p.Agent.Definition?.Id ?? "unknown", AgentStateText.Word(p.Agent.State));
        return (p.Agent.Definition?.Id, p.Agent.Definition is null ? null : "idle");
    }

    // ---- Send to Agent… -----------------------------------------------------------------------------

    /// <summary>This window's panes for the picker (App.CollectAgentPanes gathers every window's).</summary>
    public void CollectPanes(List<AgentPaneRef<TPane>> into)
    {
        window.SyncTabs();
        for (int t = 0; t < window.TabCount; t++)
        {
            foreach (var p in window.TabLayout(t).LeafList)
            {
                // agent_status off: Definition stays null, so identify the agent here.
                var agent = p.Agent.Definition ?? (process.Settings.AgentStatus ? null : p.IdentifyForeground());
                into.Add(new AgentPaneRef<TPane>(p, t, new AgentCandidate(p.Id, agent?.Name, p.Exited, window.Id, window.TabId(t))));
            }
        }
    }

    /// <summary>The panes on offer, nearest first (nothing to send to: an empty list).</summary>
    public List<AgentPaneRef<TPane>> Targets()
    {
        var all = process.CollectAgentPanes();
        var order = AgentSend.Targets(all.Select(a => a.Candidate), window.Focused.Id, window.Id, window.ActiveTabId ?? 0);
        var byId = all.ToDictionary(a => a.Candidate.PaneId);
        return order.ConvertAll(c => byId[c.PaneId]);
    }

    /// <summary>Hook from Execute: SendToAgent.</summary>
    public void OpenSend()
    {
        string? selection;
        var focused = window.Focused;
        lock (gate) selection = focused.Selection?.Text(focused.Term);
        var targets = Targets();
        if (targets.Count == 0)
        {
            window.Toast(ToastKind.Info, "No coding agent to send to", "Start Claude Code, Codex, … in a pane");
            return;
        }
        long now = Now;
        bool hasText = selection is { Length: > 0 };
        bool manyWindows = process.WindowCount > 1;
        var list = new List<Command>(targets.Count);
        foreach (var (pane, tabIndex, candidate) in targets)
        {
            var (p, name) = (pane, candidate.Agent!);
            string state = AgentStateText.Describe(pane.Agent.State, pane.Agent.Tracker.Kind);
            string title = pane.Teammate is { } tm ? $"{name} ({tm.Name}) · {state}" : $"{name} · {state}";
            string where = manyWindows ? $"Window {candidate.WindowId} · Tab {tabIndex + 1} · {PaneDirName(pane)}" : $"Tab {tabIndex + 1} · {PaneDirName(pane)}";
            list.Add(new Command
            {
                Id = "send-to-agent:" + pane.Id, Category = "Send to", Title = title, Detail = where, Icon = Icons.Sparkle,
                IconColor = AgentBadge.Color(window.Theme, pane.Agent.State),
                RunWith = hasText
                    ? mods => Send(p, name, selection!, submit: (mods & RunModifiers.Ctrl) != 0)
                    : _ => OpenPrompt(p, name),
            });
        }
        window.CloseMenu(now);
        window.OpenPalette(list, new CommandPalette.Options
        {
            Placeholder = hasText ? $"Send {selection!.EnumerateRunes().Count()} characters to…" : "Choose an agent…",
            Noun = targets.Count == 1 ? "agent" : "agents", Hints = hasText ? AgentPaletteHints.Picker : AgentPaletteHints.Choose,
        }, now);
    }

    /// <summary>The prompt on the pane's live screen that can still be answered from the overview, or null.</summary>
    private AgentPrompt? OpenPrompt(TPane pane)
    {
        var a = pane.Agent;
        if (a.State != AgentState.Waiting || a.Definition is not { } def) return null;
        AgentPrompt? prompt;
        lock (gate)
        {
            _screenText.Attach(pane.Term);
            prompt = AgentPrompts.Read(def, _screenText);
        }
        return prompt?.Fingerprint == a.AnsweredPrompt && a.AnsweredSince == a.Tracker.Since ? null : prompt;
    }

    /// <summary>No selection: the palette becomes a one-line prompt for that agent.</summary>
    private void OpenPrompt(TPane target, string name) => window.OpenPalette([], new CommandPalette.Options
    {
        Placeholder = $"Message for {name}…",
        Prompt = (text, mods) => Send(target, name, text, submit: (mods & RunModifiers.Ctrl) != 0),
        PromptHint = $"Enter leaves it in {name}'s input · Ctrl+Enter also sends it",
        Hints = AgentPaletteHints.Picker,
    }, Now);

    /// <summary>Into the agent's input queue as a paste, like <c>cli send-text</c>; <paramref name="submit"/> presses Enter after it.</summary>
    private void Send(TPane target, string name, string text, bool submit)
    {
        if (target.Exited) { window.Toast(ToastKind.Info, $"{name} has exited"); return; }
        bool bracketed;
        lock (gate) bracketed = target.Term.BracketedPaste;
        var (payload, removed) = AgentSend.Payload(text, bracketed, process.Settings.PasteSanitize, submit);
        if (removed > 0) window.Toast(ToastKind.Info, "Paste sanitized", $"removed {removed} control character{(removed == 1 ? "" : "s")}");
        if (payload.Length == 0) return;
        window.SendInput(target, payload);
        if (submit)
            window.ToastWithAction(ToastKind.Success, $"Sent to {name}", DescribeCount(text), "Jump", () => process.JumpToAgentPane(target));
        else
            process.JumpToAgentPane(target); // the text sits in its input box: finish it there
    }

    private static string DescribeCount(string text)
    {
        int chars = text.EnumerateRunes().Count();
        return $"{chars} character{(chars == 1 ? "" : "s")}";
    }
}

/// <summary>Key hints of the Send-to-Agent palette; outside the generic class so there is one instance of each.</summary>
internal static class AgentPaletteHints
{
    public static readonly (string[] Keys, string Label)[] Picker = [(["↵"], "paste, go there"), (["ctrl", "↵"], "paste + send")];
    public static readonly (string[] Keys, string Label)[] Choose = [(["↵"], "choose")];
}
