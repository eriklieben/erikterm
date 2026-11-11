using ErikTerm.App.Ipc;
using ErikTerm.App.Panes;

namespace ErikTerm.App.WindowFeatures;

/// <summary>The pane or tab an <c>erikterm cli</c> (or tmux shim) request names, in this window.</summary>
internal sealed class IpcTargets(IFeatureWindow window)
{
    /// <summary>The pane a request names (any tab of this window), or the focused pane.</summary>
    public (Tab Tab, Pane Pane) Target(IpcRequest r)
    {
        var tabs = window.Tabs;
        if (r.PaneId is not { } id) return (tabs.Active!, window.Focused);
        foreach (var tab in tabs.Items)
            foreach (var pane in tab.Layout.Leaves())
                if (pane.Id == id) return (tab, pane);
        // --window-id with the caller's own pane (ERIKTERM_PANE) in another window: that pane only says where we were asked from.
        if (r.WindowId is not null) return (tabs.Active!, window.Focused);
        throw new IpcException($"no pane with id {id}");
    }

    /// <summary>The tab a request names by id, else the named (or focused) pane's.</summary>
    public Tab Tab(IpcRequest r) =>
        r.TabId is { } id ? window.TabById(id) ?? throw new IpcException($"no tab with id {id}") : Target(r).Tab;

    public bool IsActive(Tab tab) => ReferenceEquals(tab, window.Tabs.Active);
}
