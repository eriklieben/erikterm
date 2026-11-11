using ErikTerm.App.Integrations;
using ErikTerm.App.Notifications;
using ErikTerm.App.Panes;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Core;
using ErikTerm.Ui;
using ErikTerm.Ui.AgentStatus;

namespace ErikTerm.App.Tabs;

/// <summary>What the classes split out of TermWindow may read and set on a tab: <see cref="Tab"/> implements it, tests use a fake.</summary>
internal interface ITabView
{
    int Id { get; }
    string Title { get; }
    AgentState AgentState { get; }
    bool Activity { get; }
    bool Unread { get; }
    ProgressReport Progress { get; }
    /// <summary>A pane's shell runs in a container (WindowFeatures/WindowContainers.cs): the cube icon.</summary>
    bool InContainer { get; }
    /// <summary>The tab's color as set: a scheme color name or #rrggbb (Config/TabColors.cs).</summary>
    string? Color { get; set; }
    /// <summary>The color the tab's focused program set with OSC 6.</summary>
    uint? ProgramColor { get; }
    TabColorCache ColorCache { get; }
}

/// <summary>Writing the active tab's live layout, focus and zoom back to the tab, before reading them.</summary>
internal interface ITabSync
{
    void SyncTabs();
}

/// <summary>A window's tabs, for the classes split out of TermWindow: TermWindow implements it, tests use a fake over a real <see cref="TabList{T}"/>.</summary>
internal interface ITabWindow<TTab> : IWindowChrome, ITabSync where TTab : class, ITabView
{
    TabList<TTab> Tabs { get; }
    bool TabBarShown { get; }
    void ActivateTab(int index);
}

internal static class TabLookup
{
    /// <summary>The tab with this id, or null.</summary>
    public static TTab? ById<TTab>(this TabList<TTab> tabs, int id) where TTab : class, ITabView
    {
        var items = tabs.Items;
        for (int i = 0; i < items.Count; i++)
            if (items[i].Id == id) return items[i];
        return null;
    }
}

/// <summary>What <see cref="WindowTabColors{TTab}"/> needs from its window.</summary>
internal interface ITabColorWindow<TTab> : ITabWindow<TTab> where TTab : class, ITabView
{
    /// <summary>The scheme named colors resolve against.</summary>
    ColorScheme Scheme { get; }
    /// <summary>Opens the command palette with <paramref name="query"/> typed in.</summary>
    void OpenPalette(string query);
}

/// <summary>What <see cref="WindowTabSwitcher{TTab}"/> needs from its window.</summary>
internal interface ITabSwitcherWindow<TTab> : ITabWindow<TTab> where TTab : class, ITabView
{
    /// <summary>Some overlay already takes the keys (the double-tap of Ctrl doesn't open the list over it).</summary>
    bool OverlayOpen { get; }
    /// <summary>The list is opening: the pop-ups it replaces close.</summary>
    void CloseOverlaysForTabSwitcher(long now);
    bool AnimateProgress { get; }
    void ActivateTabRelative(int delta);
    RepoBadgeModel? TabRepoBadge(TTab tab);
    uint? TabColorOf(TTab tab);
}

/// <summary>What <see cref="WindowTabSwitcher{TTab}"/> needs from the process: App implements it, tests use a fake.</summary>
internal interface ITabSwitcherProcess : IPaneTabs
{
    Settings Settings { get; }
    Dictionary<KeyChord, ActionSpec> Keys { get; }
    NotificationCenter Notifications { get; }
}
