namespace ErikTerm.App.Agents;

/// <summary>How each coding agent resumes one saved session (the rules; the offer is <see cref="WindowAgentResume{TPane}"/>).</summary>
internal static class AgentResume
{
    private const int MaxSessionIdChars = 128;

    /// <summary>
    /// A session id goes on the agent's command line and comes from a program or from a file that can be edited, so only
    /// the characters real ids use are accepted, and it can't start with '-' and turn into a flag.
    /// </summary>
    public static bool IsSessionId(string? id)
    {
        if (id is not { Length: > 0 and <= MaxSessionIdChars } || !char.IsAsciiLetterOrDigit(id[0])) return false;
        return id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    }

    /// <summary>The argv that resumes <paramref name="sessionId"/>; null for an agent without one (Aider) or an unusable id.</summary>
    public static string[]? Command(string? agentId, string? sessionId)
    {
        if (agentId is null || !IsSessionId(sessionId)) return null;
        string id = sessionId!;
        return agentId switch
        {
            "claude-code" => ["claude", "--resume", id],
            "codex" => ["codex", "resume", id],
            "gemini-cli" => ["gemini", "--resume", id],
            "opencode" => ["opencode", "--session", id],
            "amp" => ["amp", "threads", "continue", id],
            "cursor-agent" => ["cursor-agent", "--resume", id],
            "copilot-cli" => ["copilot", "--resume=" + id],
            "droid" => ["droid", "--resume", id],
            _ => null,
        };
    }
}
