using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ErikTerm.App.Integrations;

/// <summary>Combined state of a PR's checks or a workflow run.</summary>
internal enum CheckState : byte { None, Pending, Pass, Fail }

/// <summary><c>gh pr view --json number,title,state,url,statusCheckRollup,reviewDecision,isDraft</c>.</summary>
internal sealed class GhPullRequest
{
    public int Number { get; set; }
    public string? Title { get; set; }
    public string? State { get; set; }
    public string? Url { get; set; }
    public bool IsDraft { get; set; }
    public string? ReviewDecision { get; set; }
    public List<GhCheck>? StatusCheckRollup { get; set; }
}

/// <summary>A CheckRun (status/conclusion) or a StatusContext (state) in the rollup.</summary>
internal sealed class GhCheck
{
    [JsonPropertyName("__typename")] public string? TypeName { get; set; }
    public string? Name { get; set; }
    public string? Context { get; set; }
    public string? Status { get; set; }
    public string? Conclusion { get; set; }
    public string? State { get; set; }
    public string? DetailsUrl { get; set; }
    public string? TargetUrl { get; set; }
    public string? WorkflowName { get; set; }
}

/// <summary><c>gh run list --json status,conclusion,url,name,databaseId</c>.</summary>
internal sealed class GhRun
{
    public string? Status { get; set; }
    public string? Conclusion { get; set; }
    public string? Url { get; set; }
    public string? Name { get; set; }
    public long DatabaseId { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(GhPullRequest))]
[JsonSerializable(typeof(List<GhRun>))]
internal sealed partial class GhJsonContext : JsonSerializerContext;

/// <summary>What the badge shows for a branch: its PR (number, checks) or, without one, the latest workflow run.</summary>
internal sealed record GitHubStatus
{
    public int PrNumber { get; init; }
    public string? PrTitle { get; init; }
    public string? PrUrl { get; init; }
    public string? PrState { get; init; }   // OPEN, CLOSED, MERGED
    public bool Draft { get; init; }
    public string? ReviewDecision { get; init; } // APPROVED, CHANGES_REQUESTED, REVIEW_REQUIRED
    public CheckState Checks { get; init; }
    public int ChecksTotal { get; init; }
    public int ChecksFailed { get; init; }
    public int ChecksPending { get; init; }
    public string? RunName { get; init; }
    public string? RunUrl { get; init; }
    public long RunId { get; init; }

    public bool HasPr => PrNumber > 0;
}

internal static partial class GitHubParser
{
    public static GhPullRequest? ParsePr(string json)
    {
        try { return JsonSerializer.Deserialize(json, GhJsonContext.Default.GhPullRequest); }
        catch (JsonException) { return null; }
    }

    public static List<GhRun>? ParseRuns(string json)
    {
        try { return JsonSerializer.Deserialize(json, GhJsonContext.Default.ListGhRun); }
        catch (JsonException) { return null; }
    }

    /// <summary>One check's state. CheckRun: status QUEUED/IN_PROGRESS/… + conclusion; StatusContext: state.</summary>
    public static CheckState StateOf(GhCheck c)
    {
        if (c.State is { Length: > 0 } state && c.Status is null)
            return state.ToUpperInvariant() switch
            {
                "SUCCESS" => CheckState.Pass,
                "FAILURE" or "ERROR" => CheckState.Fail,
                _ => CheckState.Pending, // PENDING, EXPECTED
            };
        if (c.Status is { } status && !status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)) return CheckState.Pending;
        return ConclusionState(c.Conclusion);
    }

    private static CheckState ConclusionState(string? conclusion) => conclusion?.ToUpperInvariant() switch
    {
        "SUCCESS" or "NEUTRAL" or "SKIPPED" => CheckState.Pass,
        "FAILURE" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED" or "STARTUP_FAILURE" or "STALE" => CheckState.Fail,
        null or "" => CheckState.Pending,
        _ => CheckState.Pass,
    };

    /// <summary>Any failure fails the rollup; else anything still running makes it pending; else it passed.</summary>
    public static (CheckState State, int Total, int Failed, int Pending) Rollup(IReadOnlyList<GhCheck>? checks)
    {
        if (checks is null || checks.Count == 0) return (CheckState.None, 0, 0, 0);
        int failed = 0, pending = 0;
        foreach (var c in checks)
        {
            switch (StateOf(c))
            {
                case CheckState.Fail: failed++; break;
                case CheckState.Pending: pending++; break;
            }
        }
        CheckState state;
        if (failed > 0) state = CheckState.Fail;
        else state = pending > 0 ? CheckState.Pending : CheckState.Pass;
        return (state, checks.Count, failed, pending);
    }

    /// <summary>A workflow run's state (lowercase status/conclusion from <c>gh run list</c>).</summary>
    public static CheckState RunState(GhRun run) =>
        run.Status is { } s && !s.Equals("completed", StringComparison.OrdinalIgnoreCase) ? CheckState.Pending : ConclusionState(run.Conclusion);

    [GeneratedRegex(@"^https://github\.com/[\w.-]+/[\w.-]+/actions/runs/(\d{1,15})(?:[/?#]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex RunUrlRegex();

    /// <summary>The workflow run id in an Actions URL (…/actions/runs/123/job/456), or 0.</summary>
    public static long RunIdFromUrl(string? url) =>
        url is not null && RunUrlRegex().Match(url) is { Success: true } m && long.TryParse(m.Groups[1].ValueSpan, out long id) ? id : 0;

    public static GitHubStatus FromPr(GhPullRequest pr)
    {
        var (state, total, failed, pending) = Rollup(pr.StatusCheckRollup);
        var (runId, runName) = WatchedRun(pr.StatusCheckRollup);
        return new GitHubStatus
        {
            PrNumber = pr.Number, PrTitle = pr.Title, PrUrl = IsGitHubUrl(pr.Url) ? pr.Url : null, PrState = pr.State, Draft = pr.IsDraft,
            ReviewDecision = pr.ReviewDecision is { Length: > 0 } r ? r : null,
            Checks = state, ChecksTotal = total, ChecksFailed = failed, ChecksPending = pending,
            RunId = runId, RunName = runName,
        };
    }

    /// <summary>The run worth watching: a pending one, else a failed one, else any.</summary>
    private static (long RunId, string? RunName) WatchedRun(List<GhCheck>? checks)
    {
        if (checks is null) return (0, null);
        foreach (var want in (ReadOnlySpan<CheckState>)[CheckState.Pending, CheckState.Fail, CheckState.Pass])
        {
            foreach (var c in checks)
                if (StateOf(c) == want && RunIdFromUrl(c.DetailsUrl) is > 0 and var id) return (id, c.WorkflowName ?? c.Name);
        }
        return (0, null);
    }

    public static GitHubStatus FromRun(GhRun? run)
    {
        if (run is null) return new GitHubStatus();
        return new GitHubStatus { Checks = RunState(run), RunName = run.Name, RunUrl = IsGitHubUrl(run.Url) ? run.Url : null, RunId = Math.Max(0, run.DatabaseId) };
    }

    /// <summary>Only github.com https URLs are handed to the browser.</summary>
    public static bool IsGitHubUrl(string? url) =>
        url is { Length: < 2048 } && url.StartsWith("https://github.com/", StringComparison.Ordinal) && !url.AsSpan().ContainsAny(" \0\n\r\t\"'<>`");

    /// <summary>gh's "no pull requests found for branch" (exit 1): not an error, look at runs instead.</summary>
    public static bool IsNoPr(string stderr) => stderr.Contains("no pull requests found", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("no open pull requests", StringComparison.OrdinalIgnoreCase);

    public static bool IsRateLimited(string stderr) => stderr.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase) || stderr.Contains("secondary rate", StringComparison.OrdinalIgnoreCase);
}
