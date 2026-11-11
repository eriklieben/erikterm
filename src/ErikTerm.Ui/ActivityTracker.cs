namespace ErikTerm.Ui;

/// <summary>What the activity rules need to know about a pane's terminal right now.</summary>
/// <param name="Version">Changes whenever output arrives (Terminal.ContentVersion).</param>
/// <param name="CommandsStarted">OSC 133;C count (Terminal.CommandsStarted).</param>
/// <param name="CommandRunning">Between OSC 133;C and D/A.</param>
/// <param name="HasPromptMarks">The shell emits OSC 133 marks, so prompt redraws can be told apart from output.</param>
/// <param name="SpawnedAt">When the pane's process started (ms, same clock as <c>now</c>).</param>
public readonly record struct PaneActivity(long Version, long CommandsStarted, bool CommandRunning, bool HasPromptMarks, long SpawnedAt);

/// <summary>
/// Decides whether a background tab's activity dot should light: output counts, except output within
/// <see cref="SpawnGraceMs"/> of a pane starting (the shell's first prompt), within <see cref="ResizeGraceMs"/> of
/// a resize (SIGWINCH redraws), and — when the shell has OSC 133 integration — output that is only the prompt
/// being redrawn (no command started and none was running). Panes are keyed by an id; pure bookkeeping.
/// </summary>
public sealed class ActivityTracker
{
    public const int SpawnGraceMs = 1500;
    public const int ResizeGraceMs = 1000;

    private sealed class State
    {
        public long Version, Commands;
        public bool WasRunning;
        public long QuietUntil;
    }

    private readonly Dictionary<int, State> _panes = [];

    /// <summary>The user has seen the pane as it is now (its tab is or was just active).</summary>
    public void Seen(int pane, in PaneActivity a)
    {
        var s = Get(pane, a);
        Baseline(s, a);
    }

    /// <summary>The pane was resized: whatever it prints for the next moment is a redraw.</summary>
    public void Resized(int pane, long now)
    {
        if (!_panes.TryGetValue(pane, out var s)) _panes[pane] = s = new State { Version = long.MinValue };
        s.QuietUntil = Math.Max(s.QuietUntil, now + ResizeGraceMs);
    }

    /// <summary>True when the pane did something worth the user's attention since it was last seen.</summary>
    public bool Check(int pane, in PaneActivity a, long now)
    {
        var s = Get(pane, a);
        if (a.Version == s.Version) return false;
        bool quiet = now < s.QuietUntil || now - a.SpawnedAt < SpawnGraceMs;
        bool promptOnly = a.HasPromptMarks && !a.CommandRunning && !s.WasRunning && a.CommandsStarted == s.Commands;
        if (quiet || promptOnly)
        {
            Baseline(s, a); // absorbed: later output is compared against this
            return false;
        }
        return true;
    }

    public void Forget(int pane) => _panes.Remove(pane);

    /// <summary>Drops panes that no longer exist.</summary>
    public void Retain(IReadOnlySet<int> live)
    {
        foreach (int id in _panes.Keys.Where(id => !live.Contains(id)).ToList()) _panes.Remove(id);
    }

    public int Count => _panes.Count;

    private State Get(int pane, in PaneActivity a)
    {
        if (!_panes.TryGetValue(pane, out var s))
        {
            // First sight (e.g. a pane that started in a background tab): what it has printed so far is its startup.
            s = new State();
            Baseline(s, a);
            _panes[pane] = s;
        }
        else if (s.Version == long.MinValue) Baseline(s, a); // created by Resized before first sight
        return s;
    }

    private static void Baseline(State s, in PaneActivity a)
    {
        s.Version = a.Version;
        s.Commands = a.CommandsStarted;
        s.WasRunning = a.CommandRunning;
    }
}
