namespace ErikTerm.Config;

/// <summary>
/// The settings that are tables of their own — [keys], [worktree_agents.*] and [colors] — read after the top-level
/// settings (<see cref="SettingRules"/>), [colors] on top of whichever color_scheme was chosen.
/// </summary>
internal static class SettingTables
{
    /// <summary>The settings that are tables of their own: read last, [colors] on top of whichever color_scheme was chosen.</summary>
    public static Settings Read(TomlTable root, Settings s, string path, List<string> diagnostics)
    {
        if (root.TryGet("keys", out var keysValue, out int keysLine))
        {
            if (keysValue is TomlTable keys) s = s with { Keys = ReadKeys(keys, path, diagnostics) };
            else diagnostics.Add($"{ConfigOverrides.Where(path, keysLine)}: keys: expected a table, e.g. [keys] \"CTRL+h\" = \"ActivatePaneDirection(Left)\"");
        }

        if (root.TryGet("worktree_agents", out var agentsValue, out int agentsLine))
        {
            if (agentsValue is TomlTable agents) s = s with { WorktreeAgents = ReadWorktreeAgents(agents, path, diagnostics) };
            else diagnostics.Add($"{ConfigOverrides.Where(path, agentsLine)}: worktree_agents: expected tables like [worktree_agents.claude]");
        }

        if (root.TryGet("colors", out var colorsValue, out int colorsLine))
        {
            if (colorsValue is TomlTable colors) s = s with { Colors = ReadColors(colors, s.Colors, path, diagnostics) };
            else diagnostics.Add($"{ConfigOverrides.Where(path, colorsLine)}: colors: expected a table ([colors])");
        }
        return s;
    }

    /// <summary>The keys of [colors].</summary>
    internal static readonly string[] KnownColorKeys =
    [
        "foreground", "background", "cursor_bg", "cursor_fg", "selection_bg", "selection_fg", "cursor_border", "split",
        "accent", "ansi", "brights",
    ];

    private static readonly string[] KnownWorktreeAgentKeys = ["name", "command", "args", "prompt_args"];

    private static List<WorktreeAgentSettings> ReadWorktreeAgents(TomlTable t, string path, List<string> diagnostics)
    {
        var agents = new List<WorktreeAgentSettings>();
        foreach (var name in t.Keys)
        {
            t.TryGet(name, out var value, out int line);
            if (value is not TomlTable table)
            {
                diagnostics.Add($"{ConfigOverrides.Where(path, line)}: worktree_agents.{name}: expected a table with command = \"...\"");
                continue;
            }
            var r = new ValueReader(table, path, diagnostics, $"worktree_agents.{name}.");
            string? displayName = null, command = null;
            List<string>? args = null, promptArgs = null;
            foreach (var key in table.Keys)
            {
                switch (key)
                {
                    case "name": displayName = r.String(key); break;
                    case "command": command = r.String(key); break;
                    case "args": args = r.StringList(key); break;
                    case "prompt_args":
                        promptArgs = r.StringList(key);
                        if (promptArgs is { Count: > 0 } && !promptArgs.Any(a => a.Contains("{prompt}", StringComparison.Ordinal)))
                        {
                            r.Warn(key, "must contain {prompt} (or be [] for an agent that takes no first prompt)");
                            promptArgs = null;
                        }
                        break;
                    default: r.Warn(key, "unknown setting (ignored)" + SettingsLoader.DidYouMean(key, KnownWorktreeAgentKeys)); break;
                }
            }
            agents.Add(new WorktreeAgentSettings(name, displayName, command, args, promptArgs));
        }
        return agents;
    }

    private static List<KeyBinding> ReadKeys(TomlTable t, string path, List<string> diagnostics)
    {
        var list = new List<KeyBinding>();
        foreach (string key in t.Keys)
        {
            t.TryGet(key, out var value, out int line);
            if (!KeyChord.TryParse(key, out var chord, out string chordError))
            {
                diagnostics.Add($"{ConfigOverrides.Where(path, line)}: keys.\"{key}\": {chordError}");
                continue;
            }
            if (value is not string actionText)
            {
                diagnostics.Add($"{ConfigOverrides.Where(path, line)}: keys.\"{key}\": expected an action string like \"SplitHorizontal\"");
                continue;
            }
            if (!ActionSpec.TryParse(actionText, out var action, out string actionError))
            {
                diagnostics.Add($"{ConfigOverrides.Where(path, line)}: keys.\"{key}\": {actionError}");
                continue;
            }
            list.Add(new KeyBinding(chord, action));
        }
        return list;
    }

    private static ColorScheme ReadColors(TomlTable t, ColorScheme baseScheme, string path, List<string> diagnostics)
    {
        var r = new ValueReader(t, path, diagnostics, "colors.");
        var c = baseScheme with { Ansi = (uint[])baseScheme.Ansi.Clone(), Brights = (uint[])baseScheme.Brights.Clone() };
        foreach (string key in t.Keys)
        {
            if (key is "ansi" or "brights")
            {
                if (r.ColorList(key, 8) is { } list) c = key == "ansi" ? c with { Ansi = list } : c with { Brights = list };
            }
            else if (!KnownColorKeys.Contains(key)) r.Warn(key, "unknown color (ignored)" + SettingsLoader.DidYouMean(key, KnownColorKeys));
            else if (r.ReadColor(key) is { } value) c = WithColor(c, key, value);
        }
        return c;
    }

    private static ColorScheme WithColor(ColorScheme c, string key, uint value) => key switch
    {
        "foreground" => c with { Foreground = value },
        "background" => c with { Background = value },
        "cursor_bg" => c with { CursorBg = value },
        "cursor_fg" => c with { CursorFg = value },
        "selection_bg" => c with { SelectionBg = value },
        "selection_fg" => c with { SelectionFg = value },
        "cursor_border" => c with { CursorBorder = value },
        "split" => c with { Split = value },
        "accent" => c with { Accent = value },
        _ => c,
    };
}
