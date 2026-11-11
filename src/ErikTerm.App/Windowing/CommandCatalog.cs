using ErikTerm.App.Integrations;
using ErikTerm.App.Panes;
using ErikTerm.App.Tabs;
using ErikTerm.App.Ui;
using ErikTerm.App.Vim;
using ErikTerm.Config;
using ErikTerm.Ui;

namespace ErikTerm.App.Windowing;

/// <summary>
/// What the command palette and the pane's context menu offer, the shortcut shown next to an entry, and the few
/// entries that open something of their own (settings, the config file, the key bindings).
/// </summary>
#pragma warning disable S107 // TermWindow's wiring: each part takes the peers it calls, so its dependencies stay visible; a bundle of them would only hide that
internal sealed class CommandCatalog(
    IWindowHost window, TabBarController tabBar, TabTearOff tearOff, WindowThemes themes, WindowBroadcast broadcast, ExitHoldFlow exitHold,
    CloseConfirmFlow closeConfirm, PaneActions paneActions, AboutPanel about, WindowVim<Pane> vim, WindowTabColors<Tab> tabColors, ContextMenu menu)
#pragma warning restore S107
{
    private const string WindowCategory = "Window";

    /// <summary>The palette's Execute: an entry's own run, else its action.</summary>
    public void RunCommand(Command cmd, RunModifiers mods = RunModifiers.None)
    {
        if (cmd.RunWith is { } runWith) runWith(mods);
        else if (cmd.Action is { } action) window.Execute(action);
        else cmd.Run?.Invoke();
    }

    // ==== Command catalog ======================================================================

    public List<Command> BuildCommands()
    {
        var list = new List<Command>();
        void Add(string category, string title, int icon, string action, Func<bool>? enabled = null)
        {
            if (!ActionSpec.TryParse(action, out var spec, out _)) return;
            list.Add(new Command
            {
                Id = action, Category = category, Title = title, Icon = icon, Action = spec,
                IsEnabled = enabled, Shortcut = ShortcutFor(spec),
            });
        }
        bool multi() => window.ShownPaneCount > 1;
        bool hasSelection() => window.Focused.Selection is not null;
        bool zoomed = window.Zoomed is not null;

        Add("Pane", "Split Right", Icons.SplitRight, "SplitHorizontal");
        Add("Pane", "Split Down", Icons.SplitDown, "SplitVertical");
        Add("Pane", !zoomed ? "Zoom Pane" : "Unzoom Pane", !zoomed ? Icons.ZoomIn : Icons.ZoomOut, "TogglePaneZoomState", multi);
        Add("Pane", "Select Pane…", Icons.Layout, "PaneSelect", multi);
        Add("Pane", "Close Pane", Icons.Close, "CloseCurrentPane");
        Add("Pane", "Focus Left", Icons.ArrowLeft, "ActivatePaneDirection(Left)", multi);
        Add("Pane", "Focus Right", Icons.ArrowRight, "ActivatePaneDirection(Right)", multi);
        Add("Pane", "Focus Up", Icons.ArrowUp, "ActivatePaneDirection(Up)", multi);
        Add("Pane", "Focus Down", Icons.ArrowDown, "ActivatePaneDirection(Down)", multi);
        Add("Pane", "Move Divider Left", Icons.ResizeH, "AdjustPaneSize(Left)", multi);
        Add("Pane", "Move Divider Right", Icons.ResizeH, "AdjustPaneSize(Right)", multi);
        Add("Pane", "Move Divider Up", Icons.ResizeV, "AdjustPaneSize(Up)", multi);
        Add("Pane", "Move Divider Down", Icons.ResizeV, "AdjustPaneSize(Down)", multi);

        Add("Edit", "Copy", Icons.Copy, "CopyTo(Clipboard)", hasSelection);
        Add("Edit", "Paste", Icons.Paste, "PasteFrom(Clipboard)");
        Add("Edit", "Paste Primary Selection", Icons.Paste, "PasteFrom(PrimarySelection)");
        list.Add(new Command { Id = "select-all", Category = "Edit", Title = "Select All", Icon = Icons.SelectAll, Run = paneActions.SelectAll });

        Add("View", "Increase Font Size", Icons.FontBigger, "IncreaseFontSize");
        Add("View", "Decrease Font Size", Icons.FontSmaller, "DecreaseFontSize");
        Add("View", "Reset Font Size", Icons.FontSmaller, "ResetFontSize");
        Add("View", "Toggle Full Screen", Icons.Window, "ToggleFullScreen");
        Add("View", "Toggle Transparency", Icons.Eye, "ToggleTransparency");
        Add("View", "Scroll to Top", Icons.Top, "ScrollToTop");
        Add("View", "Scroll to Bottom", Icons.Bottom, "ScrollToBottom");
        Add("View", "Clear Scrollback", Icons.Clear, "ClearScrollback");

        Add("Theme", "Browse Themes…", Icons.Color, "BrowseThemes"); // Windowing/WindowThemes.cs
        foreach (var scheme in window.AllSchemes()) list.Add(themes.ThemeCommand(scheme, "Theme"));

        Add(WindowCategory, "New Window", Icons.Add, "SpawnWindow");
        Add(WindowCategory, "Reload Configuration", Icons.Refresh, "ReloadConfiguration");
        Add(WindowCategory, "Settings", Icons.Gear, "OpenSettings");
        list.Add(new Command { Id = "open-config", Category = WindowCategory, Title = "Open Configuration File", Icon = Icons.Gear, Run = OpenConfigFile });
        list.Add(new Command { Id = "reset-terminal", Category = "Terminal", Title = "Reset Terminal", Icon = Icons.Terminal, Run = paneActions.ResetTerminal });
        window.AddShellCommands(Add); // quick select, prompts (WindowFeatures/WindowLinks.cs)
        vim.AddCommands(Add); // vim mode, pane arrangement (Vim/WindowVim.cs)
        Add("Search", "Search All Panes…", Icons.Search, "SearchAllPanes"); // Search/WindowPaneSearch.cs
        window.AddAgentCommands(Add); // Agents… (Agents/WindowAgents.cs)
        Add("Agents", "Send to Agent…", Icons.Send, "SendToAgent"); // Agents/WindowAgents.cs
        broadcast.AddCommands(list); // Broadcast Input (Windowing/WindowBroadcast.cs)
        window.AddIntegrationCommandGroup(list); // snippets, recent, teammates, notifications, git, worktrees, review, containers
        list.Add(new Command { Id = "keys", Category = "Help", Title = "Show Key Bindings (in a new pane)", Icon = Icons.Keyboard, Run = ShowKeyBindings });
        Add("Help", "About erikterm", Icons.Info, "ShowAbout");
        tabBar.AddCommands(list);
        exitHold.AddCommands(list); // Restart Pane (Windowing/ExitHoldFlow.cs)
        tabColors.AddCommands(list); // Set Tab Color… (Tabs/WindowTabColors.cs)
        window.AddTmuxAndProcessCommands(list); // WindowFeatures/WindowTmux.cs, App.Session.cs, App.Process.cs
        tearOff.AddCommands(list); // move tab to another window (Windowing/TabTearOff.cs)
        return list;
    }

    /// <summary>The simplest binding for an action (fewest modifiers), as keycap labels.</summary>
    public string[] ShortcutFor(ActionSpec action)
    {
        KeyChord? best = null;
        foreach (var (chord, a) in window.Keys)
        {
            if (a != action) continue;
            if (best is null || ModCount(chord.Mods) < ModCount(best.Value.Mods)) best = chord;
        }
        return best is { } b ? Widgets.KeycapLabels(b) : [];
    }

    private static int ModCount(KeyMods m) => System.Numerics.BitOperations.PopCount((uint)m);

    public string? ShortcutText(string action) =>
        ActionSpec.TryParse(action, out var spec, out _) && ShortcutFor(spec) is { Length: > 0 } caps ? string.Join("+", caps) : null;

    /// <summary>The settings page (erikterm settings) in a tab of its own; it closes with q like the other tool tabs.</summary>
    public void OpenSettings()
    {
        if (Environment.ProcessPath is not { } self) return;
        string cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (window.SpawnToolTab([self, "settings", "--config-file", window.ConfigPath], cwd, "Couldn't open the settings") is not { } tab) return;
        tab.CustomTitle = "Settings";
        tab.TransientTitle = true; // names this tab's program, not a place to come back to
        window.Redraw();
    }

    public void OpenConfigFile()
    {
        string configPath = window.ConfigPath;
        if (!File.Exists(configPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            try
            {
                using var f = new FileStream(configPath, FileMode.CreateNew, FileAccess.Write); // never through a planted symlink
                f.Write("# erikterm configuration — changes apply when you save.\n"u8);
            }
            catch (IOException) when (File.Exists(configPath)) { /* created meanwhile */ }
        }
        // As a file link opens one (WindowFeatures/WindowEditor.cs), and never through sh, which Windows doesn't have.
        var argv = EditorCommands.EditFileArgv(window.PreferredEditor(), configPath, OperatingSystem.IsWindows(),
            name => ToolRunner.Find(name) is not null || File.Exists(name));
        if (argv is not null) window.Split(SplitAxis.Horizontal, argv);
        else if (ToolRunner.Launch([LinkOpener.SystemOpener(), configPath]) is { } error) // whatever opens .toml files
            window.Toast(ToastKind.Error, "Couldn't open the configuration file", error);
    }

    private void ShowKeyBindings()
    {
        var lines = window.Keys.OrderBy(k => k.Value.ToString()).Select(k => $"{k.Key,-26} {k.Value}");
        string text = "erikterm key bindings\n\n" + string.Join("\n", lines) + "\n";
        window.Split(SplitAxis.Horizontal, Security.PagerCommand.Less(text)); // piped, no temp file
    }

    // ==== Context menu =========================================================================

    public void OpenContextMenu(float x, float y, long now)
    {
        var focused = window.Focused;
        bool hasSelection = focused.Selection is not null;
        bool multi = window.ShownPaneCount > 1;
        bool zoomed = window.Zoomed is not null;
        var items = new List<ContextMenu.Item>
        {
            new("Copy", Icons.Copy, ShortcutText("CopyTo(Clipboard)"), () => window.Execute(new ActionSpec("CopyTo", "Clipboard")), hasSelection),
            new("Paste", Icons.Paste, ShortcutText("PasteFrom(Clipboard)"), () => window.Execute(new ActionSpec("PasteFrom", "Clipboard"))),
            new("Select All", Icons.SelectAll, null, paneActions.SelectAll),
            new("Find…", Icons.Search, ShortcutText("Search"), () => window.Execute(new ActionSpec("Search", null))),
            ContextMenu.Item.Separator,
            new("New Tab", Icons.Add, ShortcutText("SpawnTab"), tabBar.SpawnTab),
            new("Split Right", Icons.SplitRight, ShortcutText("SplitHorizontal"), () => window.Split(SplitAxis.Horizontal)),
            new("Split Down", Icons.SplitDown, ShortcutText("SplitVertical"), () => window.Split(SplitAxis.Vertical)),
            new(!zoomed ? "Zoom Pane" : "Unzoom Pane", !zoomed ? Icons.ZoomIn : Icons.ZoomOut,
                ShortcutText("TogglePaneZoomState"), () => window.Execute(new ActionSpec("TogglePaneZoomState", null)), multi),
            new("Close Pane", Icons.Close, ShortcutText("CloseCurrentPane"), () => closeConfirm.RequestClosePane(window.Focused)),
            new("Restart Pane", Icons.Refresh, ShortcutText("RestartPane"), () => exitHold.RequestRestartPane(window.Focused), focused.Teammate is null),
            ContextMenu.Item.Separator,
            new("Clear Scrollback", Icons.Clear, null, () => window.Execute(new ActionSpec("ClearScrollback", null))),
            new("Command Palette…", Icons.Search, ShortcutText("ActivateCommandPalette"), () => window.Execute(new ActionSpec("ActivateCommandPalette", null))),
        };
        window.AddShellMenuItems(items, x, y); // Open Link, Copy Link Address, Copy Command Output (WindowFeatures/WindowLinks.cs)
        if (hasSelection && window.HasAgentTargets)
            items.Insert(1, new("Send Selection to Agent…", Icons.Send, ShortcutText("SendToAgent"), window.OpenAgentSend)); // Agents/WindowAgents.cs
        items.Add(ContextMenu.Item.Separator);
        items.Add(new("About erikterm", Icons.Info, null, about.Show));
        menu.Open(items, x, y, window.Theme, now);
    }
}
