using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ErikTerm.App.Ipc;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Cli;

/// <summary>
/// The agent side of <c>erikterm cli</c>: <c>statusline</c> (Claude Code's status line command: forwards model, cost
/// and context use to the pane and prints a line), the hook JSON behind <c>set-agent-status --hook-input</c>, and the
/// <c>agents</c> table.
/// </summary>
internal static class AgentCli
{
    /// <summary>How long statusline waits for erikterm: Claude Code runs it on every update, so it must be quick.</summary>
    public static readonly TimeSpan StatuslineTimeout = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan PassthroughTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <c>erikterm cli statusline</c>. Never fails loudly: whatever happens with erikterm, it prints a line and
    /// exits 0 (a broken status line would show up in Claude Code's UI).
    /// </summary>
    public static int Statusline(CliInvocation inv, TextReader stdin, TextWriter stdout, Func<string, string?> env, TimeSpan? timeout = null)
    {
        string json;
        try { json = stdin.ReadToEnd(); }
        catch (IOException) { json = ""; }
        var usage = ParseStatusline(json);

        string? socket = CliMain.ResolveSocket(inv, env, out bool own);
        if (socket is not null && usage is not null)
        {
            var request = new IpcRequest { Command = "report-usage", PaneId = inv.Request.PaneId, Usage = usage };
            if (own && request.PaneId is null && int.TryParse(env(IpcPaths.PaneVariable), out int pane)) request.PaneId = pane;
            if (request.PaneId is not null || own) // never attribute usage to some other window's focused pane
            {
                try { IpcClient.Send(socket, request, timeout ?? StatuslineTimeout); }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException) { /* usage is best effort: the status line still prints */ }
            }
        }

        string line = inv.Passthrough is { Length: > 0 } cmd ? RunPassthrough(cmd, json) ?? FormatLine(usage) : FormatLine(usage);
        stdout.Write(line.EndsWith('\n') ? line : line + "\n");
        stdout.Flush();
        return 0;
    }

    /// <summary>The compact default line: "Opus 5.5 · $0.42 · 38% ctx".</summary>
    public static string FormatLine(UsageInfo? u) =>
        u is null ? "" : new AgentUsage(u.Model, u.CostUsd, u.ContextPercent, u.ContextTokens, u.ContextSize, u.SessionId).Line();

    /// <summary>
    /// Claude Code's statusLine JSON → the fields erikterm shows: <c>model.display_name</c>, <c>cost.total_cost_usd</c>,
    /// <c>context_window.used_percentage</c> (else total_input_tokens / context_window_size), <c>session_id</c>,
    /// <c>workspace.current_dir</c>. Null when it isn't a JSON object.
    /// </summary>
    public static UsageInfo? ParseStatusline(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var u = new UsageInfo
            {
                Model = Str(Get(root, "model", "display_name")) ?? Str(Get(root, "model", "id")) ?? Str(Get(root, "model")),
                CostUsd = Num(Get(root, "cost", "total_cost_usd")),
                ContextPercent = Num(Get(root, "context_window", "used_percentage")),
                ContextTokens = (long?)Num(Get(root, "context_window", "total_input_tokens")),
                ContextSize = (long?)Num(Get(root, "context_window", "context_window_size")),
                SessionId = Str(Get(root, "session_id")),
                Cwd = Str(Get(root, "workspace", "current_dir")) ?? Str(Get(root, "cwd")),
            };
            if (u.ContextPercent is null && u.ContextTokens is { } t && u.ContextSize is > 0)
                u.ContextPercent = Math.Round(100.0 * t / u.ContextSize.Value, 1);
            if (u.Model is { Length: > 64 }) u.Model = u.Model[..64];
            return u;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A Claude Code hook's stdin JSON → a short message: Notification's <c>message</c>, PreToolUse's
    /// "Running Bash", UserPromptSubmit's prompt (first line, clipped). Null when there's nothing useful.
    /// </summary>
    public static string? HookMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? text = Str(Get(root, "message"))
                ?? (Str(Get(root, "tool_name")) is { } tool ? "Running " + tool : null)
                ?? Str(Get(root, "prompt"));
            if (text is null) return null;
            text = text.ReplaceLineEndings(" ").Trim();
            if (text.Length > 160) return text[..159] + "…";
            return text.Length == 0 ? null : text;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Kind of wait a hook's Notification describes (Claude Code's notification_type).</summary>
    public static string? HookKind(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Str(Get(doc.RootElement, "notification_type")) switch
            {
                "permission_prompt" or "worker_permission_prompt" => "permission",
                "elicitation_dialog" or "agent_needs_input" or "elicitation_url_dialog" => "question",
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Runs the user's own status line command (through /bin/sh, as Claude Code does) with the same stdin.</summary>
    private static string? RunPassthrough(string command, string stdin)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = false, UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
            if (!output.Wait(PassthroughTimeout)) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* it exited on its own */ } return null; }
            return output.Result.TrimEnd('\n');
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The <c>agents</c> table: PANE TAB AGENT STATE FOR COST CTX MESSAGE.</summary>
    public static string FormatAgents(IReadOnlyList<AgentInfo> agents)
    {
        if (agents.Count == 0) return "no coding agents running\n";
        string[] header = ["PANEID", "TAB", "AGENT", "STATE", "FOR", "COST", "CTX", "MESSAGE"];
        var rows = new List<string[]> { header };
        foreach (var a in agents)
            rows.Add([a.PaneId.ToString(CultureInfo.InvariantCulture), (a.TabIndex + 1).ToString(CultureInfo.InvariantCulture), a.Name,
                AgentStateText.Describe(AgentStateText.Parse(a.State) ?? AgentState.None, a.Kind), Duration(a.SecondsInState),
                AgentUsage.Cost(a.Usage?.CostUsd) ?? "", a.Usage?.ContextPercent is { } c ? $"{Math.Round(c):0}%" : "", a.Message ?? ""]);
        var sb = new StringBuilder();
        CliTable.Append(sb, rows, rightAligned: 2);
        double total = agents.Sum(a => a.Usage?.CostUsd ?? 0);
        if (total > 0) sb.Append("total ").Append(AgentUsage.Cost(total)).Append('\n');
        return sb.ToString();
    }

    /// <summary>"42s", "3m", "2h 5m".</summary>
    public static string Duration(int seconds) => seconds switch
    {
        < 60 => $"{Math.Max(0, seconds)}s",
        < 3600 => $"{seconds / 60}m",
        _ => $"{seconds / 3600}h {seconds / 60 % 60}m",
    };

    private static JsonElement? Get(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out var next)) return null;
            e = next;
        }
        return e;
    }

    private static string? Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s && s.GetString() is { Length: > 0 } v ? v : null;

    private static double? Num(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;
}
