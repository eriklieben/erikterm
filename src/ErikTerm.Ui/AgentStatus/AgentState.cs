namespace ErikTerm.Ui.AgentStatus;

/// <summary>What a coding agent in a pane is doing, as the UI shows it.</summary>
public enum AgentState : byte
{
    /// <summary>No agent (or nothing known yet).</summary>
    None,
    /// <summary>At its prompt, and the user has seen it since it last finished (an empty outline).</summary>
    Idle,
    /// <summary>Running a turn (yellow, pulsing).</summary>
    Working,
    /// <summary>Needs the user: a permission prompt, a question, a login (red).</summary>
    Waiting,
    /// <summary>Finished a turn the user hasn't looked at yet (blue).</summary>
    Done,
}

/// <summary>Where a status report came from, strongest first.</summary>
public enum AgentSource : byte
{
    /// <summary>OSC 7501 program status (Claude Code, Codex).</summary>
    Osc7501,
    /// <summary><c>erikterm cli set-agent-status</c>, e.g. from Claude Code hooks.</summary>
    Hook,
    /// <summary>A future telemetry receiver (OpenTelemetry spans); nothing produces these yet.</summary>
    Telemetry,
    /// <summary>A desktop notification the program sent (OSC 9 / 777 / 99) or a bell.</summary>
    Notification,
    /// <summary>Screen-content rules (ScreenRules).</summary>
    Heuristic,
    /// <summary>A spinner glyph in the window title.</summary>
    Title,
    /// <summary>Output flowing (or not).</summary>
    Activity,
}

/// <summary>
/// One statement about an agent's state. Every signal source produces these; <see cref="AgentStatusTracker"/>
/// combines them. <paramref name="Kind"/> qualifies Waiting ("permission", "question", "auth").
/// </summary>
public readonly record struct AgentReport(AgentState State, AgentSource Source, long At, string? Message = null, string? Kind = null, int? Progress = null);

public static class AgentStateText
{
    /// <summary>Lower-case word for pills, the overview and the CLI.</summary>
    public static string Word(AgentState s) => s switch
    {
        AgentState.Idle => "idle",
        AgentState.Working => "working",
        AgentState.Waiting => "waiting",
        AgentState.Done => "done",
        _ => "none",
    };

    /// <summary>"needs permission" / "has a question" / "needs auth" for a Waiting kind, else the state word.</summary>
    public static string Describe(AgentState s, string? kind) => s == AgentState.Waiting
        ? kind switch
        {
            "permission" => "needs permission",
            "question" => "has a question",
            "auth" => "needs auth",
            _ => "waiting",
        }
        : Word(s);

    public static AgentState? Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "idle" => AgentState.Idle,
        "working" or "busy" or "running" => AgentState.Working,
        "waiting" or "blocked" or "input" => AgentState.Waiting,
        "done" or "finished" or "stop" => AgentState.Done,
        "none" or "clear" => AgentState.None,
        _ => null,
    };

    /// <summary>Sort key for "most urgent first": waiting, done, working, idle, none.</summary>
    public static int Urgency(AgentState s) => s switch
    {
        AgentState.Waiting => 4,
        AgentState.Done => 3,
        AgentState.Working => 2,
        AgentState.Idle => 1,
        _ => 0,
    };
}
