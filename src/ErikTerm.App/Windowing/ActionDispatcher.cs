using ErikTerm.App.Panes;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.App.Vim;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// Runs an action (a key binding, the palette, a menu, vim mode): a thin switch per group that hands each action to
/// the class that does it. <c>key</c>/<c>mod</c> are the triggering key, for the actions that pass it through.
/// </summary>
internal sealed class ActionDispatcher(
    IWindowHost window, PaneActions panes, TabBarController tabs, TabList<Tab> tabList, WindowSearch search, CloseConfirmFlow closeConfirm,
    ExitHoldFlow exitHold, WindowBroadcast broadcast, WindowThemes themes, AboutPanel about, CommandCatalog commands,
    WindowTabSwitcher<Tab> tabSwitcher, WindowTabColors<Tab> tabColors, WindowVim<Pane> vim, ContextMenu menu, CommandPalette palette)
{
    private static long Now => Environment.TickCount64;

    public static int ParseInt(string s, int fallback) => int.TryParse(s.Trim(), out int v) ? v : fallback;

    /// <summary>Runs an action. <paramref name="key"/>/<paramref name="mod"/> are the triggering key, for pass-through.</summary>
    public void Execute(ActionSpec action, uint key = 0, ushort mod = 0)
    {
        long now = Now;
        string arg = action.Arg ?? "";
        string firstArg = arg.Split(',')[0].Trim().Trim('\'', '"');
        if (!RunPaneAction(action.Name, arg, firstArg, key, mod, now) && !RunViewAction(action, arg, firstArg, key, mod)
            && !RunTabAction(action.Name, firstArg, mod, now))
            RunFeatureAction(action.Name, firstArg);
        window.Redraw();
    }

    /// <summary>The palette, menus, splits and the focused pane.</summary>
    private bool RunPaneAction(string name, string arg, string firstArg, uint key, ushort mod, long now)
    {
        switch (name)
        {
            case "ActivateCommandPalette":
                menu.Close(now);
                palette.Open(commands.BuildCommands(), window.Theme, now);
                break;
            case "ShowContextMenu":
                var t = window.Focused.Target;
                commands.OpenContextMenu(t.X + t.W / 2f, t.Y + t.H / 3f, now);
                break;
            case "OpenSettings": commands.OpenSettings(); break;
            case "SplitHorizontal": window.Split(SplitAxis.Horizontal); break;
            case "SplitVertical": window.Split(SplitAxis.Vertical); break;
            case "CloseCurrentPane": closeConfirm.RequestClosePane(window.Focused); break; // Windowing/CloseConfirmFlow.cs
            case "TogglePaneZoomState": panes.ToggleZoom(); break;
            case "PaneSelect": panes.OpenPaneSelect(now); break;
            case "ActivatePaneDirection": panes.ActivateDirection(PaneActions.ParseDirection(firstArg), key, mod); break;
            case "AdjustPaneSize": panes.AdjustSize(PaneActions.ParseDirection(firstArg), PaneActions.AdjustAmount(arg), key, mod); break;
            case "CopyTo": window.Copy(toClipboard: firstArg != "PrimarySelection", toPrimary: firstArg != "Clipboard"); break;
            case "PasteFrom": panes.PasteFrom(firstArg); break;
            case "ClearScrollback": panes.ClearScrollback(); break;
            case "ScrollByPage": panes.ScrollByPage(firstArg, now); break;
            case "ScrollToTop": panes.ScrollToTop(now); break;
            case "ScrollToBottom": panes.ScrollToBottom(now); break;
            case "RestartPane": exitHold.RequestRestartPane(window.Focused); break; // Windowing/ExitHoldFlow.cs
            case "ToggleBroadcastInput": broadcast.Toggle(firstArg); break; // Windowing/WindowBroadcast.cs
            default: return false;
        }
        return true;
    }

    /// <summary>The window, fonts, input and the modes that take over a pane.</summary>
    private bool RunViewAction(ActionSpec action, string arg, string firstArg, uint key, ushort mod)
    {
        switch (action.Name)
        {
            case "ReloadConfiguration": window.ReloadConfiguration(); break;
            case "IncreaseFontSize" or "DecreaseFontSize" or "ResetFontSize": panes.ZoomFont(action.Name); break;
            case "SpawnWindow": window.SpawnWindow(); break;
            case "ShowAbout": about.Show(); break; // Windowing/AboutPanel.cs
            case "ToggleFullScreen": panes.ToggleFullScreen(); break;
            case "ToggleTransparency": panes.ToggleTransparency(); break;
            case "SendAgentNewline": window.SendAgentNewline(key, mod); break; // Agents/WindowAgents.cs
            case "SendString":
                string sendString = System.Text.RegularExpressions.Regex.Unescape(arg);
                window.SendInput(window.Focused, sendString);
                broadcast.Text(sendString); // Windowing/WindowBroadcast.cs
                break;
            case "QuickSelect" or "ScrollToPrompt" or "SelectCommandOutput" or "CopyCommandOutput":
                window.ExecuteShellAction(action, key, mod); // WindowFeatures/WindowLinks.cs
                break;
            case "ActivateCopyMode" or "VimMode" or "SwapPane" or "RotatePanes" or "EqualizePanes" or "MovePaneToNewTab" or "MovePaneToEdge" or "PaneLayout":
                vim.ExecuteAction(action.Name, firstArg, PaneActions.ParseDirection(firstArg)); // Vim/WindowVim.cs
                break;
            case "Search" or "SearchAllPanes": search.Execute(action.Name, firstArg); break;
            case "BrowseThemes": themes.OpenBrowser(); break; // Windowing/WindowThemes.cs
            default: return false;
        }
        return true;
    }

    /// <summary>Tabs and moving them between windows.</summary>
    private bool RunTabAction(string name, string firstArg, ushort mod, long now)
    {
        switch (name)
        {
            case "TabSwitcher": tabSwitcher.RunAction(firstArg, mod, now); break; // Tabs/WindowTabSwitcher.cs
            case "ShowTabSwitcher": tabSwitcher.OpenSticky(now); break;
            case "SpawnTab": tabs.SpawnTab(); break;
            case "CloseCurrentTab":
                if (tabList.Active is { } tab) closeConfirm.RequestCloseTab(tab);
                break;
            case "SetTabColor": // Tabs/WindowTabColors.cs
                if (tabList.Active is { } colored) tabColors.Set(colored, firstArg);
                break;
            case "ActivateTab": tabs.ActivateTabIndex(tabList.Resolve(ParseInt(firstArg, 0))); break;
            case "ActivateTabRelative": tabs.ActivateTabRelative(ParseInt(firstArg, 1)); break;
            case "MoveTabRelative": tabList.MoveRelative(ParseInt(firstArg, 1)); break;
            case "MoveTab":
                int to = ParseInt(firstArg, 0);
                tabList.Move(tabList.ActiveIndex, to < 0 ? tabList.Count + to : to);
                break;
            case "MoveTabToNewWindow": // App.TearOff.cs
                if (tabList.Active is { } lone) window.MoveTabToNewWindow(lone);
                break;
            case "MoveTabToWindow":
                if (tabList.Active is { } moving) window.MoveTabToWindow(moving, ParseInt(firstArg, 0));
                break;
            default: return false;
        }
        return true;
    }

    /// <summary>Agents, worktrees, review, teammates, notifications and snippets (the git and integrations side of the window).</summary>
    private void RunFeatureAction(string name, string firstArg)
    {
        switch (name)
        {
            case "AgentsOverview" or "SendToAgent": window.RunAgentAction(name); break; // Agents/WindowAgents.cs
            case "NewAgentWorktree" or "ShowWorktrees" or "FinishWorktree" or "CleanUpWorktrees": window.RunWorktreeAction(name); break; // Worktrees/WindowWorktrees.cs
            case "CompareWithMain": window.OpenReview(firstArg == "Pick"); break; // WindowFeatures/WindowReview.cs
            case "ToggleAgentPane": window.RunTeammateToggle(firstArg); break; // Teammates/WindowTeammates.cs
            case "NotificationsPanel" or "ClearNotifications" or "ToggleDesktopNotifications": window.RunNotificationsAction(name); break;
            case "SendSnippet": window.SendSnippetNamed(firstArg); break; // WindowFeatures/WindowSnippets.cs
        }
    }
}
