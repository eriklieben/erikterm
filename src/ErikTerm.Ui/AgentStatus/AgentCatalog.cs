namespace ErikTerm.Ui.AgentStatus;

/// <summary>
/// A known coding agent: how to recognise its process and what its screen looks like in each state.
/// </summary>
/// <param name="Id">Stable id for the CLI/JSON ("claude-code", "codex", …).</param>
/// <param name="Name">Short display name for tabs and pills.</param>
/// <param name="Commands">Executable names (/proc comm or argv[0]'s file name; comm is cut at 15 bytes).</param>
/// <param name="ScriptHints">Substrings of a node/python/bun argv that reveal the agent ("@openai/codex").</param>
/// <param name="StatusApps">OSC 7501 <c>app=</c> values it reports as.</param>
/// <param name="Rules">Screen rules, most specific first (see <see cref="ScreenClassifier"/>).</param>
public sealed record AgentDefinition(string Id, string Name, string[] Commands, string[] ScriptHints, string[] StatusApps, ScreenRule[] Rules);

/// <summary>
/// The table of agents erikterm knows. Rules come from the agents' own UI strings (read out of their binaries);
/// they are deliberately conservative — a rule that doesn't match falls back to output activity.
/// </summary>
public static class AgentCatalog
{
    private const string Permission = "permission";
    private const string NeedsPermission = "needs permission";
    private const string EscToInterrupt = "esc to interrupt";
    private const string Approval = "approval";
    private const string Working = "working";
    private const string Allow = "Allow";
    private const string CursorAgent = "cursor-agent";

    private static ScreenClause C(int bottom, params string[] anyOf) => new(anyOf, bottom);

    // Claude Code's spinner frames ("·✢*✶✻✽", ✳ on macOS/ghostty), as in its binary.
    private const string ClaudeSpinner = "·✢*✶✻✽✳";

    public static readonly AgentDefinition Claude = new("claude-code", "Claude", ["claude"], ["@anthropic-ai/claude-code", "claude-code/cli", "/claude/versions/"], ["claude-code"],
    [
        // Permission dialogs: "Do you want to proceed?" / "Do you want to make this edit to x?" over numbered choices.
        new(Permission, AgentState.Waiting,
            [C(24, "Do you want to", "Would you like to", "needs your permission", "Do you trust"), C(18, "1. Yes", "1. Allow", "1. Approve")],
            NeedsPermission, Permission),
        // AskUserQuestion / plan approval: numbered options with the selector, and the dialog's footer.
        new("question", AgentState.Waiting,
            [new(["1. "], 18, LeadingGlyphs: "❯"), C(6, "Enter to select", "to navigate", "Esc to cancel")],
            "has a question", "question"),
        // "✻ Cogitating… (12s · ↓ 1.2k tokens · esc to interrupt)"
        new("spinner", AgentState.Working, [new(["… (", EscToInterrupt], 14, LeadingGlyphs: ClaudeSpinner)]),
        new("interrupt", AgentState.Working, [C(8, EscToInterrupt)]),
        // The input box: "❯ " (or "> ") between rules, and the footer hint.
        new("prompt", AgentState.Idle, [C(6, "? for shortcuts")]),
        new("input-box", AgentState.Idle, [new([], 8, LeadingGlyphs: "❯>"), new(["───"], 9)]),
    ]);

    public static readonly AgentDefinition Codex = new("codex", "Codex", ["codex", "codex-tui"], ["@openai/codex", "/codex/codex"], ["codex"],
    [
        new(Approval, AgentState.Waiting,
            [C(26, "Would you like to run the following command?", "Would you like to make the following edits?",
                "Would you like to send input", "Press a number to choose", "Allow command?")],
            NeedsPermission, Permission),
        new(Working, AgentState.Working, [C(8, "to interrupt")]),
        new("prompt", AgentState.Idle, [C(8, "context left", "Ask Codex to do anything", "Ask a follow-up", "? for shortcuts")]),
    ]);

    public static readonly AgentDefinition Gemini = new("gemini-cli", "Gemini", ["gemini"], ["@google/gemini-cli", "gemini-cli"], ["gemini-cli"],
    [
        new("confirm", AgentState.Waiting, [C(24, "Allow execution", "Apply this change?", "Waiting for user confirmation", "Do you want to proceed")],
            NeedsPermission, Permission),
        new(Working, AgentState.Working, [C(10, "esc to cancel")]),
        new("prompt", AgentState.Idle, [C(8, "Type your message")]),
    ]);

    public static readonly AgentDefinition OpenCode = new("opencode", "OpenCode", ["opencode"], ["opencode-ai", "/opencode"], ["opencode"],
    [
        new(Permission, AgentState.Waiting, [C(20, "Permission required", "Allow once", "allow always")], NeedsPermission, Permission),
        new(Working, AgentState.Working, [C(6, "esc interrupt", EscToInterrupt, Working)]),
    ]);

    public static readonly AgentDefinition Amp = new("amp", "Amp", ["amp"], ["@sourcegraph/amp"], ["amp"],
    [
        new(Approval, AgentState.Waiting, [C(20, "Approve", Allow, "Run this command?")], NeedsPermission, Permission),
        new(Working, AgentState.Working, [C(6, "Esc to cancel", EscToInterrupt)]),
    ]);

    public static readonly AgentDefinition Aider = new("aider", "Aider", ["aider"], ["/aider", "aider-chat", "-m aider"], ["aider"],
    [
        new("confirm", AgentState.Waiting, [C(4, "(Y)es/(N)o", "[Yes]:", "(D)on't ask again")], "has a question", "question"),
        new(Working, AgentState.Working, [C(4, "Waiting for", "Tokens:")]),
    ]);

    public static readonly AgentDefinition Cursor = new(CursorAgent, "Cursor", [CursorAgent], [CursorAgent], [CursorAgent],
    [
        new(Approval, AgentState.Waiting, [C(20, "Run this command?", Allow, "Accept")], NeedsPermission, Permission),
        new(Working, AgentState.Working, [C(6, "ctrl+c to stop", "Generating", EscToInterrupt)]),
    ]);

    public static readonly AgentDefinition Copilot = new("copilot-cli", "Copilot", ["copilot"], ["@github/copilot"], ["copilot"],
    [
        new(Approval, AgentState.Waiting, [C(20, "Do you want to run this command?", "Do you want to edit", Allow)], NeedsPermission, Permission),
        new(Working, AgentState.Working, [C(6, "Esc to cancel", EscToInterrupt)]),
    ]);

    public static readonly AgentDefinition Droid = new("droid", "Droid", ["droid"], ["factory-cli", "/droid"], ["droid"],
    [
        new(Approval, AgentState.Waiting, [C(20, Allow, "Approve", "Do you want to")], NeedsPermission, Permission),
        new(Working, AgentState.Working, [C(6, "esc to stop", EscToInterrupt)]),
    ]);

    public static readonly AgentDefinition[] All = [Claude, Codex, Gemini, OpenCode, Amp, Aider, Cursor, Copilot, Droid];

    private static readonly string[] Interpreters = ["node", "nodejs", "bun", "deno", "python", "python3", "uv", "uvx", "npx", "pnpm", "tsx"];

    /// <summary>
    /// The agent behind a foreground process: by its name (<paramref name="comm"/>, /proc comm), argv[0]'s file
    /// name, or — for interpreters (node, bun, python) — a script hint in the argv. Null when it isn't an agent.
    /// Hints are written with '/'; a Windows path (npm's claude.cmd runs <c>node …\@anthropic-ai\claude-code\cli.js</c>)
    /// is compared with its backslashes as slashes.
    /// </summary>
    public static AgentDefinition? Identify(string? comm, IReadOnlyList<string>? argv)
    {
        if (ByCommand(comm) is { } a) return a;
        if (argv is null || argv.Count == 0) return null;
        string exe = FileName(argv[0]);
        if (ByCommand(exe) is { } b) return b;
        if (ByPathHint(argv[0].Replace('\\', '/')) is { } byPath) return byPath;
        if (!IsInterpreter(exe) && !IsInterpreter(comm)) return null;
        for (int i = 1; i < argv.Count && i < 8; i++)
            if (ByScriptArgument(argv[i].Replace('\\', '/')) is { } script) return script;
        return null;
    }

    /// <summary>A native build run by path, e.g. ~/.local/share/claude/versions/2.1.319.</summary>
    private static AgentDefinition? ByPathHint(string program)
    {
        return Array.Find(All, def => Array.Exists(def.ScriptHints, hint => hint.StartsWith('/') && program.Contains(hint, StringComparison.Ordinal)));
    }

    private static AgentDefinition? ByScriptArgument(string arg)
    {
        if (ByCommand(FileName(arg)) is { } c && !arg.StartsWith('-')) return c;
        return Array.Find(All, def => Array.Exists(def.ScriptHints, hint => arg.Contains(hint, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The agent reporting as OSC 7501 <c>app=</c>, or null.</summary>
    public static AgentDefinition? ByStatusApp(string? app)
    {
        if (string.IsNullOrEmpty(app)) return null;
        return Array.Find(All, def => Array.Exists(def.StatusApps, s => string.Equals(s, app, StringComparison.OrdinalIgnoreCase)));
    }

    public static AgentDefinition? ById(string? id)
    {
        foreach (var def in All) if (def.Id == id) return def;
        return null;
    }

    private static AgentDefinition? ByCommand(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return Array.Find(All, def => Array.Exists(def.Commands, c => string.Equals(c, name, StringComparison.Ordinal)));
    }

    private static bool IsInterpreter(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var i in Interpreters)
            if (name == i || name.StartsWith(i, StringComparison.Ordinal) && name.Length > i.Length && (char.IsDigit(name[i.Length]) || name[i.Length] == '.'))
                return true;
        return false;
    }

    private static string FileName(string path)
    {
        int slash = path.LastIndexOfAny(['/', '\\']);
        string name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

/// <summary>Spinner glyphs agents put in the window title (OSC 0/2).</summary>
public static class AgentTitle
{
    /// <summary>
    /// Working when the title starts with a spinner frame (Claude Code's ◐/◑, braille spinners ⠋⠙…), Idle when it
    /// starts with Claude Code's resting ✳, None otherwise. Claude Code drops the glyph once OSC 7501 is answered.
    /// </summary>
    public static AgentState Parse(string? title)
    {
        if (string.IsNullOrEmpty(title)) return AgentState.None;
        char c = title[0];
        if (c is '◐' or '◑' or '◒' or '◓' || c is >= '⠁' and <= '⣿') return AgentState.Working;
        if (c == '✳') return AgentState.Idle;
        return AgentState.None;
    }
}
