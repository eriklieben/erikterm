using ErikTerm.App.Ipc;
using ErikTerm.App.Ui;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.Tabs;

/// <summary>
/// Tab colors: palette › Set Tab Color… (the scheme's colors + Reset), the tab's right-click menu, <c>erikterm cli
/// set-tab-color</c>, the SetTabColor(name|#rrggbb|none) action, and saved with the session. A named color follows the
/// theme. The tab bar draws it in the accent's place for that tab (TabBar.cs). Without one, a color the tab's focused
/// program set with iTerm2's OSC 6 shows instead.
/// </summary>
internal sealed class WindowTabColors<TTab>(ITabColorWindow<TTab> window) where TTab : class, ITabView
{
    /// <summary>The color the tab shows. Cached per scheme (the tab bar asks every frame).</summary>
    public uint? ColorOf(TTab tab) => tab.ColorCache.Shown(tab.Color, tab.ProgramColor, window.Scheme);

    /// <summary>SetTabColor and the palette: a color name, #rrggbb, or none/"" (reset).</summary>
    public void Set(TTab tab, string? text)
    {
        if (!TabColors.TryNormalize(text, out var color))
        {
            window.Toast(ToastKind.Error, $"\"{text}\" isn't a tab color", $"Use {string.Join(", ", TabColors.Offered)}, a #rrggbb color or none");
            return;
        }
        tab.Color = color;
        window.Redraw();
        if (!window.TabBarShown) window.Toast(ToastKind.Info, "The tab bar is hidden", "Set enable_tab_bar = true (and hide_tab_bar_if_only_one_tab = false) to see tab colors");
    }

    /// <summary>Set Tab Color… (palette, tab menu): the palette, showing the color entries.</summary>
    public void OpenPicker() => window.OpenPalette("Tab Color ");

    /// <summary>Palette entries (hook from BuildCommands): Set Tab Color…, then one per color with a swatch, and Reset.</summary>
    public void AddCommands(List<Command> list)
    {
        if (window.Tabs.Active is not { } tab) return;
        list.Add(new Command { Id = "tab-color", Category = "Tab", Title = "Set Tab Color…", Icon = Icons.Color, Run = OpenPicker });
        foreach (string name in TabColors.Offered)
        {
            string color = name;
            bool current = tab.Color == color;
            list.Add(new Command
            {
                Id = "tab-color:" + color, Category = "Tab Color", Title = TabColors.DisplayName(color) + (current ? "  (current)" : ""),
                Icon = Icons.Swatch, IconColor = TabColors.Resolve(color, window.Scheme), Run = () => Set(tab, color),
            });
        }
        list.Add(new Command
        {
            Id = "tab-color:none", Category = "Tab Color", Title = "Reset", Icon = Icons.Clear, Run = () => Set(tab, null),
            IsEnabled = () => tab.Color is not null,
        });
    }

    /// <summary>set-tab-color for the tab IPC named.</summary>
    public IpcResponse IpcSet(TTab tab, IpcRequest r)
    {
        if (!TabColors.TryNormalize(r.Color, out var color))
            throw new IpcException($"'{r.Color}' isn't a tab color (use {string.Join(", ", TabColors.Offered)}, #rrggbb or none)");
        tab.Color = color;
        window.Redraw();
        return IpcResponse.Success();
    }
}
