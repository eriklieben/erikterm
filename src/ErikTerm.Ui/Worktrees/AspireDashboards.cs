using System.Text.Json;

namespace ErikTerm.Ui;

/// <summary>A running .NET Aspire AppHost's dashboard. <see cref="Url"/> holds the login token, so it stays out of what is displayed.</summary>
public sealed record AspireDashboard(string AppHostPath, string Url, int Port)
{
    public string Label => $"aspire :{Port}";
}

/// <summary>Reading <c>aspire ps --format json</c> and matching AppHosts to worktrees. No process here.</summary>
public static class AspireDashboards
{
    /// <summary>The running AppHosts with a usable dashboard link. Output that isn't the expected JSON gives none.</summary>
    public static List<AspireDashboard> Parse(string json)
    {
        var found = new List<AspireDashboard>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return found;
            foreach (var host in doc.RootElement.EnumerateArray())
                if (Read(host) is { } dashboard) found.Add(dashboard);
        }
        catch (JsonException) { /* another CLI version's output, or an error text: nothing to show */ }
        return found;
    }

    private static AspireDashboard? Read(JsonElement host)
    {
        if (host.ValueKind != JsonValueKind.Object) return null;
        if (Text(host, "status") is { } status && !status.Equals("running", StringComparison.OrdinalIgnoreCase)) return null;
        if (Text(host, "appHostPath") is not { Length: > 0 } path || Text(host, "dashboardUrl") is not { } url) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || url.AsSpan().ContainsAny('\n', '\r', '\0')) return null;
        return new AspireDashboard(path, url, uri.Port);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The dashboards by the worktree (one of <paramref name="roots"/>) holding their AppHost, in port order so rows don't reshuffle.</summary>
    public static Dictionary<string, List<AspireDashboard>> ByWorktree(IEnumerable<AspireDashboard> dashboards, IReadOnlyCollection<string> roots, bool ignoreCase = false)
    {
        var byRoot = new Dictionary<string, List<AspireDashboard>>(StringComparer.Ordinal);
        foreach (var dashboard in dashboards)
        {
            if (WorktreeList.Owner(dashboard.AppHostPath, roots, ignoreCase) is not { } owner) continue;
            if (!byRoot.TryGetValue(owner, out var list)) byRoot[owner] = list = [];
            list.Add(dashboard);
        }
        foreach (var list in byRoot.Values) list.Sort((a, b) => a.Port.CompareTo(b.Port));
        return byRoot;
    }
}
