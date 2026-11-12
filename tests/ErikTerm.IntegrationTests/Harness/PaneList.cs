using System.Text.Json;

namespace ErikTerm.IntegrationTests.Harness;

/// <summary>Reading erikterm cli list --format json.</summary>
internal static class PaneList
{
    public static int Int(this JsonElement pane, string name) => pane.GetProperty(name).GetInt32();

    public static bool Bool(this JsonElement pane, string name) => pane.GetProperty(name).GetBoolean();

    public static string Str(this JsonElement pane, string name) => pane.GetProperty(name).GetString() ?? "";

    /// <summary>The tab's own title (Rename Tab, set-tab-title); empty when it has none.</summary>
    public static string TabTitle(this JsonElement pane) => pane.TryGetProperty("tab_title", out var t) ? t.GetString() ?? "" : "";

    public static int Cols(this JsonElement pane) => pane.GetProperty("size").GetProperty("cols").GetInt32();

    public static int Rows(this JsonElement pane) => pane.GetProperty("size").GetProperty("rows").GetInt32();

    /// <summary>The panes shown in a tab (parked teammates left out), by tab id.</summary>
    public static int[] PanesPerTab(this JsonElement[] panes) =>
        panes.Where(p => !p.Bool("parked")).GroupBy(p => (p.Int("window_id"), p.Int("tab_id"))).Select(g => g.Count()).ToArray();

    public static int Tabs(this JsonElement[] panes) => panes.Select(p => (p.Int("window_id"), p.Int("tab_id"))).Distinct().Count();

    public static int Windows(this JsonElement[] panes) => panes.Select(p => p.Int("window_id")).Distinct().Count();

    /// <summary>The panes of the active tab of the first window.</summary>
    public static JsonElement[] ActiveTab(this JsonElement[] panes) => panes.Where(p => p.Bool("tab_active")).ToArray();
}
