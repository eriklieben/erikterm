using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErikTerm.App.Ipc;

/// <summary>
/// One request from <c>erikterm cli</c>. The wire format is one JSON object per line (UTF-8), with
/// snake_case names and absent fields omitted, e.g.
/// <c>{"command":"split-pane","pane_id":3,"direction":"Right","percent":30}</c>.
/// Which fields matter depends on <see cref="Command"/> (the subcommand name).
/// </summary>
internal sealed class IpcRequest
{
    public string Command { get; set; } = "";
    /// <summary>Target pane; null = the instance's focused pane.</summary>
    public int? PaneId { get; set; }
    public int? TabId { get; set; }
    /// <summary>Left / Right / Up / Down.</summary>
    public string? Direction { get; set; }
    public int? Percent { get; set; }
    public string? Cwd { get; set; }
    /// <summary>Program and arguments for split-pane / spawn; null = the default program.</summary>
    public string[]? Argv { get; set; }
    public string? Text { get; set; }
    public bool NoPaste { get; set; }
    /// <summary>send-to-agent: press Enter after the paste.</summary>
    public bool Submit { get; set; }
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
    public bool Escapes { get; set; }
    public int? Amount { get; set; }
    /// <summary>zoom-pane: "zoom", "unzoom" or "toggle".</summary>
    public string? Zoom { get; set; }
    public int? TabIndex { get; set; }
    public int? TabRelative { get; set; }
    public string? Title { get; set; }

    // ---- tmux emulation (Tmux/TmuxShim.cs) ----
    /// <summary>split-pane: the new pane's size in cells along the split (instead of <see cref="Percent"/>).</summary>
    public int? Cells { get; set; }
    /// <summary>split-pane / spawn: leave the focus where it is (tmux -d); a split in a background tab stays there.</summary>
    public bool NoFocus { get; set; }
    /// <summary>split-pane / spawn: the pane was made through the tmux shim (so respawn-pane / kill-pane may act on it).</summary>
    public bool Tmux { get; set; }
    /// <summary>split-pane / respawn-pane: Claude Code is opening an agent-team teammate's pane (Tmux/TmuxTeammates.cs).</summary>
    public bool Teammate { get; set; }
    /// <summary>split-pane / respawn-pane: the pane the tmux command came from ($TMUX_PANE): a teammate's lead.</summary>
    public int? CallerPaneId { get; set; }
    /// <summary>set-pane-color: a tmux color (blue, colour208, #rrggbb) Claude Code gave a teammate's pane.</summary>
    public string? Color { get; set; }
    /// <summary>respawn-pane: kill the running program first (tmux -k).</summary>
    public bool Kill { get; set; }
    /// <summary>select-layout: even-horizontal, even-vertical, main-horizontal, main-vertical, tiled or even (spread out evenly).</summary>
    public string? Layout { get; set; }
    /// <summary>resize-pane: absolute width / height in cells, or in percent of the tab.</summary>
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? WidthPercent { get; set; }
    public int? HeightPercent { get; set; }
    /// <summary>get-text: one line per screen row (soft-wrapped rows not joined), as tmux capture-pane without -J.</summary>
    public bool NoJoin { get; set; }
    /// <summary>The window a request is about (spawn: open the tab there; activate-window). Window ids are in `list`.</summary>
    public int? WindowId { get; set; }
    /// <summary>spawn --new-window: open a window instead of a tab.</summary>
    public bool NewWindow { get; set; }
    /// <summary>new-window (a second launch with single_instance): its config file, --set overrides and --class; the
    /// running instance only takes the launch when they match its own.</summary>
    public string? ConfigFile { get; set; }
    public string[]? Overrides { get; set; }
    public string? WindowClass { get; set; }
    /// <summary>set-agent-status: waiting / working / done / idle / clear.</summary>
    public string? State { get; set; }
    /// <summary>set-agent-status: what the agent is doing or asking.</summary>
    public string? Message { get; set; }
    /// <summary>set-agent-status: what a Waiting agent waits for (permission, question, auth).</summary>
    public string? Kind { get; set; }
    /// <summary>report-usage (from <c>erikterm cli statusline</c>).</summary>
    public UsageInfo? Usage { get; set; }
    /// <summary>swap-pane --with: the pane to trade places with (else the next one, or the previous with Direction "Previous").</summary>
    public int? OtherPaneId { get; set; }
    /// <summary>new-worktree: the new branch; null = named after the prompt, or a generated name.</summary>
    public string? Branch { get; set; }
    /// <summary>new-worktree: the branch or commit to start from; null = the repository's default branch.</summary>
    public string? Base { get; set; }
    /// <summary>new-worktree: the agent to start (built-in or [worktree_agents] key); null = a shell.</summary>
    public string? Agent { get; set; }
    /// <summary>new-worktree: the agent's first prompt.</summary>
    public string? Prompt { get; set; }
    /// <summary>new-worktree: the agent's arguments as one line, like the form's field; null = the configured ones.</summary>
    public string? AgentArguments { get; set; }
    /// <summary>new-worktree: "tab" or "split"; null = worktree_open_in.</summary>
    public string? Placement { get; set; }
    /// <summary>wait-for-state: how long to wait; null = until the state is reached or the pane closes.</summary>
    public long? TimeoutMs { get; set; }
}

/// <summary>Exit statuses of <c>erikterm cli</c>.</summary>
internal static class CliExit
{
    public const int Ok = 0, Failed = 1, TimedOut = 2, PaneGone = 3;
}

/// <summary>An agent's session usage, from its status line (see AgentUsage).</summary>
internal sealed class UsageInfo
{
    public string? Model { get; set; }
    public double? CostUsd { get; set; }
    public double? ContextPercent { get; set; }
    public long? ContextTokens { get; set; }
    public long? ContextSize { get; set; }
    public string? SessionId { get; set; }
    /// <summary>The agent's working directory (statusLine workspace.current_dir).</summary>
    public string? Cwd { get; set; }
}

/// <summary>A pane running a coding agent, as <c>agents</c> shows it.</summary>
internal sealed class AgentInfo
{
    public int WindowId { get; set; }
    public int PaneId { get; set; }
    public int TabId { get; set; }
    public int TabIndex { get; set; }
    /// <summary>Agent id ("claude-code", "codex", …).</summary>
    public string Agent { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>waiting / working / done / idle.</summary>
    public string State { get; set; } = "";
    public string? Kind { get; set; }
    public string? Message { get; set; }
    /// <summary>Where the state came from: osc7501, hook, notification, heuristic, title, activity.</summary>
    public string Source { get; set; } = "";
    public int SecondsInState { get; set; }
    public int? Progress { get; set; }
    public string Cwd { get; set; } = "";
    public bool IsFocused { get; set; }
    public UsageInfo? Usage { get; set; }
}

/// <summary>The reply to one request, on one line.</summary>
internal sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>Text the CLI prints as-is (get-text).</summary>
    public string? Output { get; set; }
    /// <summary>The new pane (split-pane, spawn) or the neighbour (get-pane-direction).</summary>
    public int? PaneId { get; set; }
    /// <summary>The window a new tab / window went into (spawn, new-window).</summary>
    public int? WindowId { get; set; }
    public List<PaneInfo>? Panes { get; set; }
    public List<AgentInfo>? Agents { get; set; }
    /// <summary>The CLI's exit status for this failure; null = <see cref="CliExit.Failed"/>.</summary>
    public int? ExitCode { get; set; }
    /// <summary>Not sent: the real reply comes later, so the main thread isn't held up. The server cancels it when the client hangs up.</summary>
    [JsonIgnore]
    public TaskCompletionSource<IpcResponse>? Later { get; set; }

    public static IpcResponse Success() => new() { Ok = true };
    public static IpcResponse Failure(string error, int? exitCode = null) => new() { Ok = false, Error = error, ExitCode = exitCode };
}

/// <summary>A pane as <c>list --format json</c> shows it; field names follow <c>wezterm cli list</c>.</summary>
internal sealed class PaneInfo
{
    public int WindowId { get; set; }
    public int TabId { get; set; }
    public int TabIndex { get; set; }
    public string? TabTitle { get; set; }
    public int PaneId { get; set; }
    public string Workspace { get; set; } = "default";
    public PaneSize Size { get; set; } = new();
    public string Title { get; set; } = "";
    /// <summary>file:// URI from OSC 7, or "".</summary>
    public string Cwd { get; set; } = "";
    public int CursorX { get; set; }
    public int CursorY { get; set; }
    /// <summary>The active pane of its tab.</summary>
    public bool IsActive { get; set; }
    public bool IsZoomed { get; set; }
    /// <summary>The active pane of the active tab of the focused window: where typing goes.</summary>
    public bool IsFocused { get; set; }
    public string? TtyName { get; set; }
    public string? ForegroundProcessName { get; set; }
    /// <summary>OSC 1337 SetUserVar values, e.g. IS_NVIM.</summary>
    public Dictionary<string, string>? UserVars { get; set; }

    // ---- for the tmux emulation ----
    /// <summary>The pane's first process (its shell).</summary>
    public int Pid { get; set; }
    /// <summary>Position in cells from the tab's top-left corner.</summary>
    public int Left { get; set; }
    public int Top { get; set; }
    /// <summary>The foreground program is real tmux: the emulation leaves this pane alone.</summary>
    public bool RealTmux { get; set; }
    /// <summary>A title set with tmux select-pane -T.</summary>
    public string? PaneTitle { get; set; }
    /// <summary>The foreground program's directory (else the shell's / OSC 7), as a path.</summary>
    public string? CurrentPath { get; set; }
    /// <summary>Made through the tmux shim (split-window / new-window).</summary>
    public bool CreatedByTmux { get; set; }
    /// <summary>DECCKM: arrow keys are sent as SS3.</summary>
    public bool CursorKeysApplication { get; set; }
    public int HistorySize { get; set; }
    public int HistoryLimit { get; set; }
    /// <summary>The pane's tab is its window's active tab (tmux window_active, whether or not the window has focus).</summary>
    public bool TabActive { get; set; }
    /// <summary>The coding agent running there ("claude-code", "codex", …), or null.</summary>
    public string? Agent { get; set; }
    /// <summary>Its state: waiting / working / done / idle (null without an agent).</summary>
    public string? AgentStatus { get; set; }
    /// <summary>An agent-team teammate's pane kept running hidden as a pill (tmux_agent_panes = "pills").</summary>
    public bool Parked { get; set; }
}

internal sealed class PaneSize
{
    public int Rows { get; set; }
    public int Cols { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
}

/// <summary>Source-generated serializers: no reflection, so it works under Native AOT and trimming.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSerializable(typeof(List<PaneInfo>))]
[JsonSerializable(typeof(List<AgentInfo>))]
internal sealed partial class IpcJson : JsonSerializerContext
{
    /// <summary>Indented, for <c>list --format json</c>.</summary>
    public static readonly IpcJson Pretty = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    });

    public static string Encode(IpcRequest r) => JsonSerializer.Serialize(r, Default.IpcRequest);
    public static string Encode(IpcResponse r) => JsonSerializer.Serialize(r, Default.IpcResponse);

    /// <summary>Null on malformed input.</summary>
    public static IpcRequest? DecodeRequest(string line)
    {
        try { return JsonSerializer.Deserialize(line, Default.IpcRequest); }
        catch (JsonException) { return null; }
    }

    public static IpcResponse? DecodeResponse(string line)
    {
        try { return JsonSerializer.Deserialize(line, Default.IpcResponse); }
        catch (JsonException) { return null; }
    }
}
