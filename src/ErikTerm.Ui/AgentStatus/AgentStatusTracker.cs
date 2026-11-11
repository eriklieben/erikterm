namespace ErikTerm.Ui.AgentStatus;

/// <summary>What the polling side saw in a pane on one tick (everything but the explicit reports).</summary>
/// <param name="HasAgent">A known agent runs in the foreground.</param>
/// <param name="Heuristic">The screen rule that matched (State None when none did).</param>
/// <param name="Title">Spinner state of the window title (<see cref="AgentTitle"/>).</param>
/// <param name="LastOutputAt">When output last arrived (ms; 0 = never).</param>
/// <param name="LastInputAt">When the user last typed into the pane (ms; 0 = never): echo isn't work.</param>
/// <param name="Seen">The user is looking at the pane right now (focused pane, active tab, focused window).</param>
public readonly record struct AgentObservation(bool HasAgent, AgentReport Heuristic, AgentState Title, long LastOutputAt, long LastInputAt, bool Seen);

/// <summary>The displayed state changed.</summary>
public readonly record struct AgentTransition(AgentState From, AgentState To, string? Message, string? Kind, AgentSource Source);

/// <summary>
/// Combines a pane's signals into one displayed <see cref="AgentState"/>, strongest source first:
/// explicit reports (OSC 7501, hooks, telemetry — the latest one wins), a notification (Waiting until the agent
/// moves on), screen heuristics, title spinners, then output activity. A turn that ends while the user isn't
/// looking shows as <see cref="AgentState.Done"/> until they look. Pure: the caller passes the clock.
/// </summary>
public sealed class AgentStatusTracker
{
    /// <summary>Output this recent counts as working (activity fallback).</summary>
    public const int ActiveMs = 1500;
    /// <summary>A turn shorter than this doesn't leave a "done" mark (a redraw, a typed command).</summary>
    public const int MinWorkMs = 1500;
    /// <summary>Inferred (non-explicit) Working → Idle must hold this long, so a flicker between tool calls isn't "done".</summary>
    public const int SettleMs = 800;
    /// <summary>Output within this long after typing is echo, not work.</summary>
    public const int EchoMs = 400;
    /// <summary>A hook's "working" yields to an idle screen after this much silence (an interrupted turn never runs Stop).</summary>
    public const int StaleHookMs = 5000;

    private AgentReport? _explicit;      // latest OSC 7501 / hook / telemetry report
    private AgentReport? _notification;  // latest notification-derived Waiting
    private AgentReport? _osc;           // current OSC 7501 summary (kept apart so a hook can't erase it)
    private AgentState _raw = AgentState.None;
    private AgentState _pending = AgentState.None;
    private long _pendingSince, _workSince;
    private bool _unseen;

    public AgentState State { get; private set; } = AgentState.None;
    /// <summary>The underlying state before seen/unseen ("finished" shows as Idle or Done).</summary>
    public AgentState Raw => _raw;
    public AgentSource Source { get; private set; } = AgentSource.Activity;
    public long Since { get; private set; }
    public string? Message { get; private set; }
    public string? Kind { get; private set; }
    public int? Progress { get; private set; }

    /// <summary>An explicit report (OSC 7501 summary, hook, telemetry) or a notification.</summary>
    public void Report(in AgentReport r)
    {
        switch (r.Source)
        {
            case AgentSource.Notification:
                // Already known to be waiting (OSC 7501 / a hook, with its kind): the notification adds nothing.
                if (_explicit is { State: AgentState.Waiting }) break;
                _notification = r with { State = AgentState.Waiting };
                break;
            case AgentSource.Osc7501: _osc = r; _explicit = r; break;
            default:
                if (r.State == AgentState.None) { _explicit = _osc; break; } // "clear" from a hook: back to whatever 7501 says
                _explicit = r;
                break;
        }
    }

    /// <summary>The OSC 7501 records are gone (cleared, or the program exited).</summary>
    public void ClearProgramStatus()
    {
        if (_osc is null) return;
        if (_explicit is { Source: AgentSource.Osc7501 }) _explicit = null;
        _osc = null;
    }

    /// <summary>Forget everything (the agent exited).</summary>
    public void Reset(long now)
    {
        _explicit = _notification = _osc = null;
        _raw = _pending = AgentState.None;
        _unseen = false;
        Message = Kind = null;
        Progress = null;
        if (State != AgentState.None) { State = AgentState.None; Since = now; }
    }

    public AgentTransition? Update(in AgentObservation o, long now)
    {
        var (raw, source, report) = Resolve(o, now);

        // Inferred endings settle first; explicit ones apply at once.
        bool inferred = source is AgentSource.Heuristic or AgentSource.Title or AgentSource.Activity;
        if (StillSettling(raw, inferred, now)) { raw = AgentState.Working; source = Source; report = null; }

        if (raw != _raw) EnterRawState(raw, inferred, o.Seen, now);
        if (o.Seen) _unseen = false;
        ApplyDetails(report, raw);
        Source = source;

        var shown = raw switch
        {
            AgentState.Idle or AgentState.Done => _unseen ? AgentState.Done : AgentState.Idle,
            _ => raw,
        };
        if (shown == State) return null;
        var t = new AgentTransition(State, shown, Message, Kind, source);
        State = shown;
        Since = now;
        return t;
    }

    /// <summary>Tracks an inferred Working to Idle or Done; true while it hasn't held for <see cref="SettleMs"/> yet.</summary>
    private bool StillSettling(AgentState raw, bool inferred, long now)
    {
        if (!(inferred && _raw == AgentState.Working && raw is AgentState.Idle or AgentState.Done))
        {
            _pending = AgentState.None;
            return false;
        }
        if (_pending != raw) { _pending = raw; _pendingSince = now; }
        return now - _pendingSince < SettleMs;
    }

    private void EnterRawState(AgentState raw, bool inferred, bool seen, long now)
    {
        bool finished = raw is AgentState.Idle or AgentState.Done
            && (_raw == AgentState.Working && now - _workSince >= MinWorkMs || raw == AgentState.Done && _raw != AgentState.Done && !inferred);
        if (finished && !seen) _unseen = true;
        if (raw is AgentState.Working or AgentState.Waiting) _unseen = false;
        if (raw == AgentState.Working && _raw != AgentState.Working) _workSince = now;
        _raw = raw;
    }

    private void ApplyDetails(AgentReport? report, AgentState raw)
    {
        if (report is { } r)
        {
            if (r.Message is not null) Message = r.Message;
            Kind = r.Kind;
            Progress = r.Progress;
        }
        else if (raw is AgentState.Idle or AgentState.None) { Kind = null; Progress = null; }
    }

    private (AgentState, AgentSource, AgentReport?) Resolve(in AgentObservation o, long now)
    {
        if (!o.HasAgent && _explicit is null) return (AgentState.None, AgentSource.Activity, null);

        bool outputSinceInput = o.LastOutputAt - o.LastInputAt > EchoMs;
        bool active = o.LastOutputAt > 0 && now - o.LastOutputAt < ActiveMs && outputSinceInput;
        var inferred = o.Heuristic.State != AgentState.None ? o.Heuristic.State : o.Title;

        if (LiveNotification(o, inferred) is { } n) return (AgentState.Waiting, AgentSource.Notification, n);
        if (LiveExplicit(o, inferred, now) is { } x) return (x.State == AgentState.None ? AgentState.Idle : x.State, x.Source, x);

        if (o.Heuristic.State != AgentState.None) return (o.Heuristic.State, AgentSource.Heuristic, o.Heuristic);
        if (o.Title != AgentState.None)
            return (o.Title == AgentState.Idle && active ? AgentState.Working : o.Title, AgentSource.Title, null);
        return (active ? AgentState.Working : AgentState.Idle, AgentSource.Activity, null);
    }

    /// <summary>A notification means "come here" until the agent visibly moves on or the user answers.</summary>
    private AgentReport? LiveNotification(in AgentObservation o, AgentState inferred)
    {
        if (_notification is not { } n) return null;
        bool newer = _explicit is not { } e || n.At >= e.At;
        bool movedOn = o.LastInputAt > n.At || inferred == AgentState.Working && o.LastOutputAt > n.At + 500;
        if (newer && !movedOn) return n;
        _notification = null;
        return null;
    }

    /// <summary>The explicit report still in force; a hook's "working" that went stale falls back to OSC 7501.</summary>
    private AgentReport? LiveExplicit(in AgentObservation o, AgentState inferred, long now)
    {
        if (_explicit is not { } x) return null;
        bool staleHook = x.Source == AgentSource.Hook && x.State == AgentState.Working && inferred == AgentState.Idle
            && now - Math.Max(x.At, o.LastOutputAt) > StaleHookMs;
        if (!staleHook) return x;
        _explicit = _osc;
        return null;
    }
}
