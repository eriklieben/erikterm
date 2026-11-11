namespace ErikTerm.Config;

[Flags]
public enum KeyMods { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Super = 8 }

/// <summary>A key plus modifiers. <see cref="Key"/> is a normalised name: "a", "5", "\\", "enter", "f1", "left", …</summary>
public readonly record struct KeyChord(KeyMods Mods, string Key)
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["return"] = "enter", ["esc"] = "escape", ["del"] = "delete", ["ins"] = "insert",
        ["pgup"] = "pageup", ["pgdn"] = "pagedown", ["uparrow"] = "up", ["downarrow"] = "down",
        ["leftarrow"] = "left", ["rightarrow"] = "right", ["plus"] = "=", ["minus"] = "-",
        ["equal"] = "=", ["backslash"] = "\\", ["quote"] = "'", ["comma"] = ",", ["period"] = ".",
        ["slash"] = "/", ["semicolon"] = ";", ["grave"] = "`", ["bracketleft"] = "[", ["bracketright"] = "]",
    };

    private static readonly HashSet<string> Named = new(StringComparer.Ordinal)
    {
        "enter", "tab", "escape", "space", "backspace", "delete", "insert", "home", "end",
        "pageup", "pagedown", "up", "down", "left", "right",
        "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12",
    };

    /// <summary>Shifted symbols map to the key that produces them on a US layout, so "CTRL+SHIFT+%" == "CTRL+SHIFT+5".</summary>
    private static readonly Dictionary<char, char> Unshift = new()
    {
        ['!'] = '1', ['@'] = '2', ['#'] = '3', ['$'] = '4', ['%'] = '5', ['^'] = '6', ['&'] = '7', ['*'] = '8',
        ['('] = '9', [')'] = '0', ['_'] = '-', ['+'] = '=', ['{'] = '[', ['}'] = ']', ['|'] = '\\', [':'] = ';',
        ['"'] = '\'', ['<'] = ',', ['>'] = '.', ['?'] = '/', ['~'] = '`',
    };

    public static bool TryParse(string text, out KeyChord chord, out string error)
    {
        chord = default;
        error = "";
        string s = text.Trim();
        if (s.Length == 0) { error = "empty key"; return false; }

        SplitChord(s, out string keyPart, out string modPart);
        if (!TryParseMods(modPart, out var mods, out error)) return false;
        if (!TryNormalizeKey(keyPart, ref mods, out string key, out error)) return false;

        chord = new KeyChord(mods, key);
        return true;
    }

    /// <summary>The key itself may be '+' or '|' ("CTRL++"), so the last character is taken when the text ends with a separator.</summary>
    private static void SplitChord(string s, out string keyPart, out string modPart)
    {
        if (s.Length >= 2 && s[^1] is '+' or '|' && s[^2] is '+' or '|')
        {
            keyPart = s[^1..];
            modPart = s[..^2];
            return;
        }
        int sep = s.LastIndexOfAny(['+', '|']);
        keyPart = sep < 0 ? s : s[(sep + 1)..];
        modPart = sep < 0 ? "" : s[..sep];
    }

    private static bool TryParseMods(string modPart, out KeyMods mods, out string error)
    {
        mods = KeyMods.None;
        error = "";
        foreach (var raw in modPart.Split(['+', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyMods.Ctrl; break;
                case "shift": mods |= KeyMods.Shift; break;
                case "alt" or "opt" or "option" or "meta": mods |= KeyMods.Alt; break;
                case "super" or "cmd" or "win" or "logo": mods |= KeyMods.Super; break;
                default: error = $"unknown modifier '{raw}' (use CTRL, SHIFT, ALT, SUPER)"; return false;
            }
        }
        return true;
    }

    /// <summary>The key's canonical name; a shifted symbol becomes its base key with Shift added.</summary>
    private static bool TryNormalizeKey(string keyPart, ref KeyMods mods, out string key, out string error)
    {
        error = "";
        key = keyPart.Trim();
        if (key.Length == 1)
        {
            char c = char.ToLowerInvariant(key[0]);
            if (Unshift.TryGetValue(c, out char baseKey))
            {
                c = baseKey;
                mods |= KeyMods.Shift;
            }
            key = c.ToString();
            return true;
        }
        key = Aliases.TryGetValue(key, out var alias) ? alias : key.ToLowerInvariant();
        if (key.Length == 1 || Named.Contains(key)) return true;
        error = $"unknown key '{keyPart}'";
        return false;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        string key = Key ?? ""; // default(KeyChord) has no key
        if ((Mods & KeyMods.Ctrl) != 0) parts.Add("Ctrl");
        if ((Mods & KeyMods.Alt) != 0) parts.Add("Alt");
        if ((Mods & KeyMods.Shift) != 0) parts.Add("Shift");
        if ((Mods & KeyMods.Super) != 0) parts.Add("Super");
        parts.Add(key switch
        {
            "enter" => "Enter", "tab" => "Tab", "escape" => "Esc", "space" => "Space", "backspace" => "Backspace",
            "delete" => "Del", "insert" => "Ins", "home" => "Home", "end" => "End", "pageup" => "PgUp", "pagedown" => "PgDn",
            "up" => "↑", "down" => "↓", "left" => "←", "right" => "→",
            _ => key.ToUpperInvariant(),
        });
        return string.Join("+", parts);
    }
}

/// <summary>An action name with an optional argument, e.g. <c>ActivatePaneDirection(Left)</c>.</summary>
public readonly record struct ActionSpec(string Name, string? Arg)
{
    /// <summary>Actions understood by the app, with the arguments they accept (null = none, empty = free-form).</summary>
    public static readonly IReadOnlyDictionary<string, string[]?> Known = new Dictionary<string, string[]?>(StringComparer.Ordinal)
    {
        ["ActivateCommandPalette"] = null,
        ["ShowContextMenu"] = null,
        ["SplitHorizontal"] = null,
        ["SplitVertical"] = null,
        ["CloseCurrentPane"] = null,
        ["TogglePaneZoomState"] = null,
        ["PaneSelect"] = null,
        ["ActivatePaneDirection"] = ["Left", "Right", "Up", "Down"],
        ["AdjustPaneSize"] = ["Left", "Right", "Up", "Down"],
        ["CopyTo"] = ["Clipboard", "PrimarySelection", "ClipboardAndPrimarySelection"],
        ["PasteFrom"] = ["Clipboard", "PrimarySelection"],
        ["ReloadConfiguration"] = null,
        ["OpenSettings"] = null, // the settings page in a split (SettingsPage/)
        ["IncreaseFontSize"] = null,
        ["DecreaseFontSize"] = null,
        ["ResetFontSize"] = null,
        ["ClearScrollback"] = null,
        ["ScrollByPage"] = ["-1", "1"],
        ["ScrollToTop"] = null,
        ["ScrollToBottom"] = null,
        ["SpawnWindow"] = null,
        ["MoveTabToNewWindow"] = null,
        ["MoveTabToWindow"] = [],
        ["ToggleFullScreen"] = null,
        ["ToggleTransparency"] = null,
        ["SendString"] = [],
        ["SendAgentNewline"] = null,
        ["Search"] = [],
        ["SearchAllPanes"] = null,
        ["SpawnTab"] = ["", "CurrentPaneDomain", "DefaultDomain"],
        ["CloseCurrentTab"] = [],
        ["ActivateTab"] = [],
        ["ActivateTabRelative"] = [],
        ["TabSwitcher"] = ["", "1", "-1"],   // Ctrl+Tab's hold-to-switch list: the direction of the first step (Tabs/WindowTabSwitcher.cs)
        ["ShowTabSwitcher"] = null,          // the same list, staying open until Enter, a click or Esc
        ["MoveTab"] = [],
        ["MoveTabRelative"] = [],
        ["SetTabColor"] = [], // a scheme color name, #rrggbb, or none (TabColors.cs)
        ["RestartPane"] = null,
        ["ToggleBroadcastInput"] = ["", "All", "Selected"], // all the tab's panes (default), or the ones picked
        ["SendSnippet"] = [],  // a [snippets] name
        ["SendToAgent"] = null,
        ["BrowseThemes"] = null,
        // Links & shell integration (OSC 133 prompts).
        ["QuickSelect"] = null,
        ["ScrollToPrompt"] = ["-1", "1"],
        ["SelectCommandOutput"] = null,
        ["CopyCommandOutput"] = null,
        ["ShowAbout"] = null,
        ["AgentsOverview"] = null,
        ["NewAgentWorktree"] = null,
        ["ShowWorktrees"] = null,
        ["FinishWorktree"] = null,
        ["CompareWithMain"] = ["", "Pick"], // a tab reviewing this branch against the default branch; Pick asks for the base first
        ["CleanUpWorktrees"] = null,
        ["NotificationsPanel"] = null,
        ["ClearNotifications"] = null,
        ["ToggleDesktopNotifications"] = null,
        // Vim mode (copy mode) and pane arrangement.
        ["ActivateCopyMode"] = null,
        ["VimMode"] = null,
        ["SwapPane"] = ["", "Next", "Previous"],
        ["RotatePanes"] = ["Clockwise", "CounterClockwise"],
        ["EqualizePanes"] = null,
        ["MovePaneToNewTab"] = null,
        ["MovePaneToEdge"] = ["Left", "Right", "Up", "Down"],
        ["PaneLayout"] = ["even-horizontal", "even-vertical", "main-vertical", "main-horizontal", "tiled"],
        // Claude Code agent-team teammates (tmux_agent_panes = "pills"): open / park the n-th.
        ["ToggleAgentPane"] = [],
        ["None"] = null,
    };

    public static bool TryParse(string text, out ActionSpec action, out string error)
    {
        action = default;
        error = "";
        string s = text.Trim();
        string name = s, arg = "";
        bool hasArg = false;
        int open = s.IndexOf('(');
        if (open >= 0)
        {
            if (!s.EndsWith(')')) { error = $"missing ')' in '{text}'"; return false; }
            name = s[..open].Trim();
            arg = s[(open + 1)..^1].Trim();
            hasArg = true;
        }
        if (name == "DisableDefaultAssignment") name = "None";
        if (!Known.TryGetValue(name, out var allowed))
        {
            error = $"unknown action '{name}'";
            return false;
        }
        // Accept wezterm's `CopyTo 'Clipboard'` style quoting and an optional amount: AdjustPaneSize(Left, 5).
        arg = arg.Trim('\'', '"');
        string firstArg = arg.Split(',')[0].Trim().Trim('\'', '"');
        if (allowed is null && hasArg && arg.Length > 0) { error = $"{name} takes no argument"; return false; }
        if (allowed is { Length: > 0 } && !allowed.Contains(firstArg, StringComparer.OrdinalIgnoreCase))
        {
            error = $"{name} expects one of {string.Join(", ", allowed)}";
            return false;
        }
        if (name == "SetTabColor" && !TabColors.TryNormalize(firstArg, out _))
        {
            error = $"SetTabColor expects a color name ({string.Join(", ", TabColors.Offered)}, …), \"#rrggbb\" or \"none\"";
            return false;
        }
        action = new ActionSpec(name, hasArg ? arg : null);
        return true;
    }

    public override string ToString() => Arg is null ? Name : $"{Name}({Arg})";
}

public readonly record struct KeyBinding(KeyChord Chord, ActionSpec Action);
