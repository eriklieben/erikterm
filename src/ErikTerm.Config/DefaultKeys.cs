namespace ErikTerm.Config;

/// <summary>The built-in key bindings: <see cref="Table"/> everywhere, and <see cref="MacTable"/> on top of it on macOS.</summary>
public static class DefaultKeys
{
    /// <summary>Built-in bindings (mostly wezterm's defaults). Config [keys] entries override by chord.</summary>
    public static readonly (string Chord, string Action)[] Table =
    [
        ("CTRL+SHIFT+p", "ActivateCommandPalette"),
        ("CTRL+SHIFT+c", "CopyTo(Clipboard)"),
        ("CTRL+SHIFT+v", "PasteFrom(Clipboard)"),
        ("SHIFT+insert", "PasteFrom(PrimarySelection)"),
        ("CTRL+SHIFT+r", "ReloadConfiguration"),
        ("CTRL+,", "OpenSettings"),
        ("CTRL+=", "IncreaseFontSize"),
        ("CTRL+SHIFT+=", "IncreaseFontSize"),
        ("CTRL+-", "DecreaseFontSize"),
        ("CTRL+0", "ResetFontSize"),
        ("SHIFT+pageup", "ScrollByPage(-1)"),
        ("SHIFT+pagedown", "ScrollByPage(1)"),
        ("SHIFT+home", "ScrollToTop"),
        ("SHIFT+end", "ScrollToBottom"),
        ("CTRL+SHIFT+ALT+%", "SplitHorizontal"),
        ("CTRL+SHIFT+ALT+\"", "SplitVertical"),
        ("CTRL+SHIFT+\\", "SplitHorizontal"),
        ("CTRL+SHIFT+-", "SplitVertical"),
        ("CTRL+SHIFT+w", "CloseCurrentPane"),
        ("CTRL+SHIFT+z", "TogglePaneZoomState"),
        ("CTRL+SHIFT+o", "PaneSelect"),
        ("CTRL+SHIFT+a", "AgentsOverview"),
        ("CTRL+SHIFT+g", "NewAgentWorktree"),
        ("CTRL+SHIFT+h", "ShowWorktrees"),
        ("CTRL+SHIFT+b", "NotificationsPanel"), // b for bell (CTRL+SHIFT+n is a new window)
        ("CTRL+SHIFT+left", "ActivatePaneDirection(Left)"),
        ("CTRL+SHIFT+right", "ActivatePaneDirection(Right)"),
        ("CTRL+SHIFT+up", "ActivatePaneDirection(Up)"),
        ("CTRL+SHIFT+down", "ActivatePaneDirection(Down)"),
        ("CTRL+SHIFT+ALT+left", "AdjustPaneSize(Left)"),
        ("CTRL+SHIFT+ALT+right", "AdjustPaneSize(Right)"),
        ("CTRL+SHIFT+ALT+up", "AdjustPaneSize(Up)"),
        ("CTRL+SHIFT+ALT+down", "AdjustPaneSize(Down)"),
        ("CTRL+SHIFT+n", "SpawnWindow"),
        ("ALT+enter", "ToggleFullScreen"),
        ("F11", "ToggleFullScreen"),
        ("CTRL+SHIFT+f", "Search"),
        ("CTRL+SHIFT+s", "SearchAllPanes"),
        ("CTRL+SHIFT+t", "SpawnTab"),
        ("CTRL+tab", "TabSwitcher(1)"),            // hold-to-switch, most recently used first (ctrl_tab_order)
        ("CTRL+SHIFT+tab", "TabSwitcher(-1)"),
        ("CTRL+pagedown", "ActivateTabRelative(1)"),
        ("CTRL+pageup", "ActivateTabRelative(-1)"),
        ("CTRL+SHIFT+pagedown", "MoveTabRelative(1)"),
        ("CTRL+SHIFT+pageup", "MoveTabRelative(-1)"),
        ("CTRL+SHIFT+1", "ActivateTab(0)"),
        ("CTRL+SHIFT+2", "ActivateTab(1)"),
        ("CTRL+SHIFT+3", "ActivateTab(2)"),
        ("CTRL+SHIFT+4", "ActivateTab(3)"),
        ("CTRL+SHIFT+5", "ActivateTab(4)"),
        ("CTRL+SHIFT+6", "ActivateTab(5)"),
        ("CTRL+SHIFT+7", "ActivateTab(6)"),
        ("CTRL+SHIFT+8", "ActivateTab(7)"),
        ("CTRL+SHIFT+9", "ActivateTab(-1)"),
        // wezterm binds QuickSelect to CTRL+SHIFT+Space. CTRL+SHIFT+Up/Down are taken by pane focus
        // (as in wezterm), so prompts use SHIFT+Up/Down like wezterm's docs example; the keys pass
        // through to the program when there are no prompt marks, in full-screen apps and while a
        // command is running (fzf, REPLs).
        ("CTRL+SHIFT+space", "QuickSelect"),
        ("SHIFT+up", "ScrollToPrompt(-1)"),
        ("SHIFT+down", "ScrollToPrompt(1)"),
        ("CTRL+SHIFT+x", "ActivateCopyMode"), // wezterm's copy-mode key: vim mode
        ("CTRL+SHIFT+ALT+i", "ToggleBroadcastInput"), // iTerm2's Cmd+Opt+I
        // A newline in Claude Code's input box even when the kitty keyboard protocol is off (ESC CR); everywhere else
        // Shift+Enter goes to the program as usual. "SHIFT+enter" = "None" turns it off.
        ("SHIFT+enter", "SendAgentNewline"),
    ];

    private static List<KeyBinding> Parse((string Chord, string Action)[] table)
    {
        var list = new List<KeyBinding>();
        foreach (var (chord, action) in table)
        {
            if (!KeyChord.TryParse(chord, out var c, out var e1)) throw new InvalidOperationException(e1);
            if (!ActionSpec.TryParse(action, out var a, out var e2)) throw new InvalidOperationException(e2);
            list.Add(new KeyBinding(c, a));
        }
        return list;
    }

    /// <summary>
    /// macOS: Cmd (SUPER) is the app's modifier, as in wezterm, kitty, iTerm2 and Terminal.app, so Ctrl and Option keep
    /// going to the program. These come on top of <see cref="Table"/> (the Ctrl+Shift chords keep working). Not bound
    /// here because the menu bar SDL creates already handles them: Cmd+Q (quit), Cmd+H (hide), Cmd+M (minimize) and
    /// Ctrl+Cmd+F (full screen). Cmd+W closes the pane, like Ctrl+Shift+W (App.cs takes it off the Window menu).
    /// </summary>
    public static readonly (string Chord, string Action)[] MacTable =
    [
        ("SUPER+c", "CopyTo(Clipboard)"),
        ("SUPER+v", "PasteFrom(Clipboard)"),
        ("SUPER+SHIFT+p", "ActivateCommandPalette"),
        ("SUPER+t", "SpawnTab"),
        ("SUPER+n", "SpawnWindow"),
        ("SUPER+w", "CloseCurrentPane"),
        ("SUPER+f", "Search"),
        ("SUPER+SHIFT+f", "SearchAllPanes"),
        ("SUPER+r", "ReloadConfiguration"),
        ("SUPER+k", "ClearScrollback"),
        ("SUPER+=", "IncreaseFontSize"),
        ("SUPER+SHIFT+=", "IncreaseFontSize"),
        ("SUPER+-", "DecreaseFontSize"),
        ("SUPER+0", "ResetFontSize"),
        ("SUPER+d", "SplitHorizontal"),
        ("SUPER+SHIFT+d", "SplitVertical"),
        ("SUPER+SHIFT+[", "ActivateTabRelative(-1)"),
        ("SUPER+SHIFT+]", "ActivateTabRelative(1)"),
        ("SUPER+1", "ActivateTab(0)"),
        ("SUPER+2", "ActivateTab(1)"),
        ("SUPER+3", "ActivateTab(2)"),
        ("SUPER+4", "ActivateTab(3)"),
        ("SUPER+5", "ActivateTab(4)"),
        ("SUPER+6", "ActivateTab(5)"),
        ("SUPER+7", "ActivateTab(6)"),
        ("SUPER+8", "ActivateTab(7)"),
        ("SUPER+9", "ActivateTab(-1)"),
    ];

    /// <summary>The built-in bindings for this OS (<see cref="Build(bool)"/>).</summary>
    public static List<KeyBinding> Build() => Build(OperatingSystem.IsMacOS());

    /// <summary>Built-in bindings: <see cref="Table"/>, plus <see cref="MacTable"/> first on macOS.</summary>
    public static List<KeyBinding> Build(bool macOS) => Parse(macOS ? [.. MacTable, .. Table] : Table);
}
