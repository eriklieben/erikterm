using ErikTerm.App.Panes;
using ErikTerm.App.Tabs;
using ErikTerm.Ui;

namespace ErikTerm.App;

/// <summary>
/// A tab: its own split layout of panes. The active tab's state lives in its window's _layout/_focused/_zoomed.
/// A tab (and its panes) belongs to one window at a time; tabs move between windows (App.MoveTab).
/// </summary>
internal sealed class Tab : ITabView
{
    private static int _nextId;

    public int Id { get; } = Interlocked.Increment(ref _nextId);
    public LayoutTree<Pane> Layout = null!;
    public Pane Focused = null!;
    public Pane? Zoomed;
    public string? CustomTitle;
    /// <summary>The custom title names a tool in this tab: not saved with the session (a restored tab runs a shell).</summary>
    public bool TransientTitle;
    /// <summary>The tab's color as set (palette, menu, cli, SetTabColor, session): a scheme color name or #rrggbb (Config/TabColors.cs).</summary>
    public string? Color { get; set; }

    // Cached for the tab bar (refreshed a few times a second).
    public string AutoTitle = "";
    public long TitleAt;
    public bool Activity;
    public bool InContainer { get; set; } // a pane's shell runs in a container: the tab's container icon (WindowFeatures/WindowContainers.cs)
    public bool Unread;                   // a notification not looked at yet, from one of its panes (polled with the title)
    public ErikTerm.Ui.AgentStatus.AgentState AgentState; // most urgent agent in the tab (Agents/WindowAgents.cs)
    public uint? ProgramColor;            // OSC 6 from its focused pane's program (Tabs/WindowTabColors.cs)
    public ErikTerm.Ui.ProgressReport Progress; // OSC 9;4 of its panes, combined (Windowing/WindowProgress.cs)
    public ErikTerm.Ui.BroadcastMode Broadcast;   // input to several panes (Windowing/WindowBroadcast.cs); never saved
    public HashSet<Pane>? BroadcastPanes;         // the picked panes in BroadcastMode.Selected
    public TabColorCache ColorCache { get; } = new(); // what the color resolved to against a scheme

    public string Title => CustomTitle ?? AutoTitle;

    ErikTerm.Ui.AgentStatus.AgentState ITabView.AgentState => AgentState;
    bool ITabView.Activity => Activity;
    bool ITabView.Unread => Unread;
    ErikTerm.Ui.ProgressReport ITabView.Progress => Progress;
    uint? ITabView.ProgramColor => ProgramColor;
}
