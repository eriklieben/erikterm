using System.Globalization;

namespace ErikTerm.Ui.AgentStatus;

/// <summary>
/// Session usage an agent reported through its status line (Claude Code's statusLine JSON, forwarded by
/// <c>erikterm cli statusline</c>): model, cost and context-window use. Ephemeral, never saved.
/// </summary>
public sealed record AgentUsage(string? Model, double? CostUsd, double? ContextPercent, long? ContextTokens, long? ContextSize, string? SessionId)
{
    /// <summary>"$0.42" (two decimals; "$12" from ten dollars up), or null.</summary>
    public static string? Cost(double? usd)
    {
        if (usd is not { } v || double.IsNaN(v) || v < 0) return null;
        return v >= 10 ? "$" + Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : "$" + v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>"38% ctx", or null.</summary>
    public static string? Context(double? percent) => percent is not { } p || double.IsNaN(p) ? null
        : Math.Clamp(Math.Round(p), 0, 100).ToString("0", CultureInfo.InvariantCulture) + "% ctx";

    /// <summary>Used share of the context window, 0–1, or null.</summary>
    public float? ContextFraction
    {
        get
        {
            if (ContextPercent is { } p) return (float)Math.Clamp(p / 100, 0, 1);
            return ContextTokens is { } t && ContextSize is { } s and > 0 ? (float)Math.Clamp((double)t / s, 0, 1) : null;
        }
    }

    /// <summary>The compact line: "Opus · $0.42 · 38% ctx" (parts that are unknown are left out).</summary>
    public string Line()
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(Model)) parts.Add(Model);
        if (Cost(CostUsd) is { } c) parts.Add(c);
        if (Context(ContextPercent ?? (ContextFraction is { } f ? f * 100 : null)) is { } x) parts.Add(x);
        return string.Join(" · ", parts);
    }
}
