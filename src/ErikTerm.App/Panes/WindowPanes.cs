using ErikTerm.App.Ui;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Panes;

/// <summary>
/// What every class split out of TermWindow may read on a pane. <see cref="Pane"/> implements it; tests use a fake over
/// a real <see cref="Terminal"/>, so they need neither a pty nor Pane.cs. Features add their own role next to it
/// (vim: <c>IVimPane</c>) and members only when a feature uses them: this doesn't wrap everything a pane can do.
/// </summary>
internal interface IPaneView : ILayoutLeaf
{
    int Id { get; }
    Terminal Term { get; }
    string Label { get; }
}

/// <summary>A pane's coding-agent status, for the classes that show it.</summary>
internal interface IPaneAgentStatus
{
    AgentState AgentState { get; }
    /// <summary>An agent runs here (identified, or its state is known).</summary>
    bool AgentActive { get; }
}

/// <summary>What the classes split out of TermWindow share of the window's look and input.</summary>
internal interface IWindowChrome
{
    Theme Theme { get; }
    /// <summary>Set after a key: whether the text event that follows it should be swallowed.</summary>
    bool SuppressText { get; set; }
    void Redraw();
    void Toast(ToastKind kind, string title, string? detail = null);
}

/// <summary>
/// A window's panes and tabs, for the classes split out of TermWindow. The window implements it; tests use a fake with
/// real <see cref="LayoutTree{T}"/> instances. <typeparamref name="TPane"/> is the pane type (Pane, or a test's fake),
/// so layout operations work on the real tree without the test project linking Pane.cs.
/// <para>
/// Tabs are named by index and panes are found through a layout. The active tab's layout, focus and zoom live on the
/// window until SyncActiveTab copies them back; <see cref="TabLayout"/> and <see cref="ClearTabZoom"/> take care of that
/// so callers don't need to ask which tab is active.
/// </para>
/// </summary>
internal interface IWindowPanes<TPane> where TPane : class, IPaneView
{
    TPane Focused { get; }
    TPane? Zoomed { get; }
    /// <summary>The active tab's layout (the live one, also while its tab hasn't been synced).</summary>
    LayoutTree<TPane> Layout { get; }
    /// <summary>The panes that take room in the layout (parked teammates don't), zoomed or not.</summary>
    int ShownPaneCount { get; }
    int TabCount { get; }
    int ActiveTabIndex { get; }
    /// <summary>A tab's layout, by index; the active tab's is the live one.</summary>
    LayoutTree<TPane> TabLayout(int tabIndex);
    /// <summary>The panes on screen (a reused list: don't hold on to it).</summary>
    List<TPane> VisiblePanes();
    /// <summary>A pane in this window, or null.</summary>
    TPane? FindPane(int id);
    void Focus(TPane pane);
    void SetZoom(TPane? pane);
    void ApplyLayout(bool animate);
    void ActivateTab(int index);
    /// <summary>Leaves zoom in a tab (any tab: the active one through <see cref="SetZoom"/>).</summary>
    void ClearTabZoom(int tabIndex);
}

/// <summary>The debug script's pointer (ERIKTERM_DEBUG_SCRIPT): moves and clicks through the window's own mouse handling.</summary>
internal interface IScriptPointer
{
    void ScriptMouseMove(float x, float y, long now);
    /// <summary>A left click: the pointer moves to (x, y), then the button goes down and up there.</summary>
    void ScriptClick(float x, float y, long now);
}
