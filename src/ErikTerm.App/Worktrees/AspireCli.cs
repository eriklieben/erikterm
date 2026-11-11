using ErikTerm.App.Integrations;
using ErikTerm.Ui;

namespace ErikTerm.App.Worktrees;

/// <summary>The AppHosts <c>aspire ps</c> reports. Without the CLI there is simply nothing to show.</summary>
internal static class AspireCli
{
    private const int TimeoutMs = 5000;

    /// <summary>On PATH, else where its installer puts it (a session started from the desktop often lacks that PATH entry).</summary>
    private static string? Find() =>
        ToolRunner.Find("aspire") ?? ToolRunner.Search("aspire", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "bin"));

    public static Dictionary<string, List<AspireDashboard>> DashboardsByWorktree(List<string> roots)
    {
        if (Find() is not { } aspire) return [];
        var result = ToolRunner.Run([aspire, "ps", "--format", "json", "--nologo", "--non-interactive"], null, TimeoutMs);
        return result.Ok ? AspireDashboards.ByWorktree(AspireDashboards.Parse(result.Stdout), roots, OperatingSystem.IsWindows()) : [];
    }
}
