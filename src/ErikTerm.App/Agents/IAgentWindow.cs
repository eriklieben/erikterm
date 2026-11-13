using ErikTerm.App.Integrations;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Agents;

/// <summary>What coding-agent status reads and sets on a pane: <see cref="Pane"/> implements it, tests use a fake.</summary>
internal interface IAgentPane : IPaneView, IPaneAgentStatus
{
    AgentPane Agent { get; }
    bool Exited { get; }
    Selection? Selection { get; }
    /// <summary>The agent-team teammate this pane is, or null.</summary>
    Teammate? Teammate { get; }
    /// <summary>The pane's rectangle right now (animating with the layout): where the pill and the rim draw.</summary>
    RectF AgentBounds(long now);
    /// <summary>The foreground program's name, or null when it isn't known.</summary>
    string? ForegroundName { get; }
    int ForegroundProcessGroup();
    /// <summary>The coding agent the foreground process is, or null (reads /proc).</summary>
    AgentDefinition? IdentifyForeground();
    /// <summary>The directory the pane reports, else its shell's; null when neither is known.</summary>
    string? KnownDirectory { get; }
}

/// <summary>One pane "Send to Agent…" can offer, as a window found it.</summary>
internal readonly record struct AgentPaneRef<TPane>(TPane Pane, int TabIndex, AgentCandidate Candidate);

/// <summary>Keys and text for an agent's pane, through the window's input path.</summary>
internal interface IAgentInput<in TPane> where TPane : class
{
    void SendInput(TPane pane, string text);
    void SendKey(uint key, ushort mod);
    void BroadcastKey(uint key, ushort mod);
}

/// <summary>What <see cref="WindowAgents{TPane}"/> needs from its window: TermWindow implements it, tests use a fake.</summary>
internal interface IAgentWindow<TPane> : IWindowPanes<TPane>, IWindowChrome, ITabSync, IAgentInput<TPane>
    where TPane : class, IAgentPane
{
    uint Id { get; }
    /// <summary>The window has keyboard focus.</summary>
    bool IsFocused { get; }
    /// <summary>The window is the app's focused one (the last focused one while another application has focus).</summary>
    bool IsCurrentWindow { get; }
    bool TabBarShown { get; }
    /// <summary>The window's size in pixels: the waiting rim stays inside it.</summary>
    (int Width, int Height) PixelSize { get; }
    /// <summary>The zoom badge is showing in the top-right corner, where the pill would go.</summary>
    bool ZoomBadgeShown(long now);
    int? ActiveTabId { get; }
    int TabId(int tabIndex);
    /// <summary>Stores a tab's combined agent state; a change refreshes its title and redraws.</summary>
    void ShowTabAgentState(int tabIndex, AgentState state);
    /// <summary>Forgets a tab's agent state (agent_status turned off).</summary>
    void ClearTabAgentState(int tabIndex);
    /// <summary>Flashes the taskbar entry until the window gets focus, or stops the flash.</summary>
    void FlashTaskbar(bool untilFocused);
    /// <summary>A toast in the window you're in: another of the app's windows, or this one.</summary>
    void ToastInFocusedWindow(ToastKind kind, string title, string? detail);
    /// <summary>A toast with a button.</summary>
    void ToastWithAction(ToastKind kind, string title, string? detail, string actionLabel, Action action);
    void CloseMenu(long now);
    /// <summary>The overview is opening: the pop-ups it replaces close.</summary>
    void CloseOverlaysForAgents(long now);
    void OpenPalette(List<Command> commands, CommandPalette.Options options, long now);
}

/// <summary>What <see cref="WindowAgents{TPane}"/> needs from the process: App implements it, tests use a fake.</summary>
internal interface IAgentProcess<TPane> where TPane : class
{
    Settings Settings { get; }
    Dictionary<KeyChord, ActionSpec> Keys { get; }
    NotificationCenter Notifications { get; }
    bool AnyWindowFocused { get; }
    int WindowCount { get; }
    /// <summary>Clicked notifications: focuses their panes, wherever they are now.</summary>
    void DrainAgentFocusRequests();
    /// <summary>Every window's agents, most urgent first.</summary>
    List<AgentRow<TPane>> AgentRows();
    /// <summary>Shows the pane in its window and raises that window when it's another one.</summary>
    void JumpToAgentPane(TPane pane);
    /// <summary>Every window's panes, for the send picker.</summary>
    List<AgentPaneRef<TPane>> CollectAgentPanes();
}
