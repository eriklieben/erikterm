using ErikTerm.App.Panes;
using ErikTerm.App.Rendering;
using ErikTerm.App.Session;
using ErikTerm.App.Ui;
using ErikTerm.Ui;

namespace ErikTerm.App.Agents;

/// <summary>What <see cref="WindowAgentResume{TPane}"/> needs from its window: TermWindow implements it, tests use a fake.</summary>
internal interface IAgentResumeWindow<TPane> : IWindowPanes<TPane>, IWindowChrome
    where TPane : class, IAgentPane
{
    /// <summary>The agent's executable can be found the way the pane would start it.</summary>
    bool AgentProgramFound(string program);
    /// <summary>Starts <paramref name="argv"/> as the pane's program, then the pane's shell when it exits; false (and a toast) when it can't start.</summary>
    bool StartAgentInPane(TPane pane, string[] argv, string directory);
}

/// <summary>
/// "Resume N agents" after a restart: one pill in a window whose restored panes had a coding agent running with a known
/// session id. Nothing starts until the pill is clicked; the click starts each agent with its own resume command in its
/// restored pane, in every tab of the window. A pane that runs something else by then is left alone. Used or dismissed,
/// the offer is gone, also from the next save; left alone, it survives the next restart with the panes it was about.
/// <para>
/// The agent is started as the pane's own process and the shell comes back when it exits (the same sequence as a
/// worktree launch, which needs no POSIX shell), never typed into the pane. TermWindow forwards its hook points here.
/// </para>
/// </summary>
internal sealed class WindowAgentResume<TPane>(IAgentResumeWindow<TPane> window)
    where TPane : class, IAgentPane
{
    private const int CheckMs = 1000;
    private const int StartGraceMs = 10_000; // long enough for a resumed agent to show up in the foreground

    private enum Part { None, Pill, Close }

    private readonly record struct Offer(TPane Pane, SessionAgent Agent, string Directory);

    private readonly List<Offer> _offers = [];
    private readonly List<(TPane Pane, long StartedAt)> _started = [];
    private long _checkAt;
    private int _count;
    private string _label = "";
    private Tween _shown = new(0);
    private RectF _pill, _close;
    private Part _hover;

    private static long Now => Environment.TickCount64;

    /// <summary>The panes the pill offers right now.</summary>
    public int Count => _count;

    /// <summary>The pill's text, e.g. "Resume 3 agents".</summary>
    public string Label => _label;

    // ---- Saving and restoring ---------------------------------------------------------------------

    /// <summary>Hook from CapturePane: what session.json records for the pane. An offer nobody answered yet stays saved.</summary>
    public SessionAgent? Capture(TPane pane)
    {
        var a = pane.Agent;
        if (a.Usage?.SessionId is { } id && (a.Definition ?? pane.IdentifyForeground()) is { } agent && AgentResume.Command(agent.Id, id) is not null)
            return new SessionAgent { Id = agent.Id, SessionId = id };
        return a.SavedAgent;
    }

    /// <summary>Hook from SpawnRestoredPane, for a pane that came back as a plain shell.</summary>
    public void Consider(TPane pane, SessionPane saved)
    {
        if (saved.Agent is not { } agent || AgentResume.Command(agent.Id, agent.SessionId) is null) return;
        if (saved.Cwd is not { Length: > 0 } directory || !Directory.Exists(directory)) return;
        pane.Agent.SavedAgent = agent;
        _offers.Add(new Offer(pane, agent, directory));
    }

    // ---- Running ----------------------------------------------------------------------------------

    /// <summary>Hook from Tick, about once a second while there is an offer or a resumed agent that hasn't reported yet.</summary>
    public void Tick(long now)
    {
        if (_offers.Count == 0 && _started.Count == 0 || now < _checkAt) return;
        _checkAt = now + CheckMs;
        _started.RemoveAll(s => Settled(s.Pane, s.StartedAt, now));
        _offers.RemoveAll(o => Withdrawn(o.Pane));
        Update(now);
    }

    /// <summary>Hook from the event loop.</summary>
    public int WakeMs(long now) =>
        _offers.Count == 0 && _started.Count == 0 ? int.MaxValue : (int)Math.Max(1, _checkAt - now);

    public bool IsAnimating(long now) => _shown.IsAnimating(now);

    /// <summary>The agent reports its own session now, or it never came up: the saved record has done its work.</summary>
    private bool Settled(TPane pane, long startedAt, long now)
    {
        var a = pane.Agent;
        bool settled = window.FindPane(pane.Id) is null || a.Usage?.SessionId is not null || (a.Definition is null && now - startedAt > StartGraceMs);
        if (settled) a.SavedAgent = null;
        return settled;
    }

    /// <summary>The pane closed, or an agent was started there by hand: nothing left to offer.</summary>
    private bool Withdrawn(TPane pane)
    {
        bool gone = window.FindPane(pane.Id) is null || pane.Agent.Active;
        if (gone) pane.Agent.SavedAgent = null;
        return gone;
    }

    private static bool AtShell(TPane pane) => !pane.Exited && RestorePrograms.IsShell(pane.ForegroundName);

    private void Update(long now)
    {
        int count = _offers.Count(o => AtShell(o.Pane));
        if (count != _count)
        {
            _count = count;
            _label = count == 1 ? "Resume 1 agent" : $"Resume {count} agents";
            window.Redraw();
        }
        float target = count > 0 ? 1 : 0;
#pragma warning disable S1244 // Target only ever holds a value set here (0 or 1), so exact comparison is the point.
        if (_shown.Target != target)
#pragma warning restore S1244
        {
            _shown.Set(target, now, window.Theme.AnimationMs);
            window.Redraw();
        }
    }

    // ---- The two answers --------------------------------------------------------------------------

    /// <summary>The click on the pill: starts each agent. Nothing else calls this.</summary>
    public void Resume()
    {
        var offers = _offers.ToArray();
        _offers.Clear();
        var problems = new List<string>();
        int started = 0;
        foreach (var offer in offers)
        {
            if (Start(offer) is { } problem)
            {
                offer.Pane.Agent.SavedAgent = null;
                if (!problems.Contains(problem)) problems.Add(problem);
            }
            else started++;
        }
        Update(Now);
        if (problems.Count > 0)
            window.Toast(ToastKind.Info, $"Resumed {started} of {offers.Length} agent{(offers.Length == 1 ? "" : "s")}", string.Join(" · ", problems));
    }

    /// <summary>Null when the agent started, else why it didn't.</summary>
    private string? Start(Offer offer)
    {
        if (window.FindPane(offer.Pane.Id) is null) return "pane closed";
        if (!AtShell(offer.Pane)) return "pane is busy";
        if (!Directory.Exists(offer.Directory)) return "directory is gone";
        string[] argv = AgentResume.Command(offer.Agent.Id, offer.Agent.SessionId)!;
        if (!window.AgentProgramFound(argv[0])) return $"{argv[0]} isn't on PATH";
        if (!window.StartAgentInPane(offer.Pane, argv, offer.Directory)) return $"{argv[0]} couldn't start";
        _started.Add((offer.Pane, Now));
        return null;
    }

    /// <summary>The ✕ on the pill: nothing is resumed, and the next start won't ask again.</summary>
    public void Dismiss()
    {
        foreach (var offer in _offers) offer.Pane.Agent.SavedAgent = null;
        _offers.Clear();
        Update(Now);
    }

    // ---- The pill ---------------------------------------------------------------------------------

    /// <summary>Hook from DrawOverlays: the pill, centered, its bottom at <paramref name="bottom"/> (a fresh shell's prompt is at the top).</summary>
    public void Draw(DrawList dl, RectF area, float bottom, long now)
    {
        _pill = _close = default;
        float v = _shown.Value(now);
        if (v <= 0.002f || _label.Length == 0) return;
        var th = window.Theme;
        float px = th.UiPx * 0.8f;
        var m = dl.MetricsFor(px);
        float h = MathF.Round(m.Height + th.S(10));
        float icon = px * 1.5f, closeW = icon + th.S(10);
        float w = MathF.Round(th.S(10) + icon + dl.Measure(_label, px, FontStyle.Bold) + th.S(10) + closeW);
        if (w > area.W - th.S(32)) return;
        var r = new RectF(MathF.Round(area.X + (area.W - w) / 2), MathF.Round(bottom - h + (1 - v) * th.S(6)), w, h);
        _pill = r;
        _close = new RectF(r.Right - closeW, r.Y, closeW, h);

        uint accent = th.Accent;
        dl.Panel(r, h / 2, Colors.Mix(th.PanelBg, accent, _hover == Part.Pill ? 0.26f : 0.12f), 0.94f * v, Colors.Mix(th.PanelBg, accent, 0.5f), v);
        float b = MathF.Round(r.Y + (h - m.Height) / 2 + m.Ascender), x = r.X + th.S(10);
        dl.Glyph(Icons.Refresh, x, b, px, accent, v);
        x += icon;
        dl.Text(_label, x, b, px, th.PanelFg, v, FontStyle.Bold);
        dl.Rect(new RectF(_close.X, r.Y + th.S(5), MathF.Max(1, MathF.Round(th.S(1))), h - th.S(10)), th.Muted, 0.4f * v);
        dl.Glyph(Icons.Close, _close.X + (closeW - icon) / 2, b, px, _hover == Part.Close ? th.PanelFg : th.Muted, v);
    }

    private Part PartAt(float x, float y)
    {
        if (_close.Contains(x, y)) return Part.Close;
        return _pill.Contains(x, y) ? Part.Pill : Part.None;
    }

    /// <summary>Hook from OnMouseMove: true while the pointer is over the pill.</summary>
    public bool MouseMove(float x, float y)
    {
        var part = _count > 0 ? PartAt(x, y) : Part.None;
        if (part != _hover)
        {
            _hover = part;
            window.Redraw();
        }
        return part != Part.None;
    }

    /// <summary>Hook from OnMouseDown (left button): true when the click was the pill's.</summary>
    public bool MouseDown(float x, float y)
    {
        var part = _count > 0 ? PartAt(x, y) : Part.None;
        if (part == Part.None) return false;
        _hover = Part.None;
        if (part == Part.Close) Dismiss();
        else Resume();
        window.Redraw();
        return true;
    }
}
