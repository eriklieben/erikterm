namespace ErikTerm.Config;

/// <summary>
/// The top-level settings, one rule each: how to read the value and which property it sets. A bad value is reported
/// through <see cref="ValueReader"/> and leaves the setting as it was.
/// </summary>
internal static class SettingRules
{
    private delegate Settings Rule(ParseContext context, string key, Settings settings);

    private sealed class ParseContext(TomlTable root, ValueReader reader, IReadOnlyList<ColorScheme> customSchemes, string path, List<string> diagnostics)
    {
        public TomlTable Root => root;
        public ValueReader Reader => reader;
        public IReadOnlyList<ColorScheme> CustomSchemes => customSchemes;
        public string Path => path;
        public List<string> Diagnostics => diagnostics;
        public Osc52Policy? Osc52;
        public bool? LegacyOsc52;
    }

    /// <summary>The top-level settings in <paramref name="root"/>, over the defaults; unknown keys are reported.</summary>
    public static Settings Apply(TomlTable root, ValueReader reader, IReadOnlyList<ColorScheme> customSchemes, string path, List<string> diagnostics)
    {
        var context = new ParseContext(root, reader, customSchemes, path, diagnostics);
        var s = new Settings();
        foreach (string key in context.Root.Keys)
        {
            if (Rules.TryGetValue(key, out var rule)) s = rule(context, key, s);
            else context.Reader.Warn(key, "unknown setting (ignored)" + SettingsLoader.DidYouMean(key, KnownKeys));
        }
        return Osc52From(context) is { } policy ? s with { Osc52 = policy } : s;
    }

    /// <summary>osc52, else what the older enable_osc52 amounts to.</summary>
    private static Osc52Policy? Osc52From(ParseContext context)
    {
        if (context.Osc52 is { } policy) return policy;
        if (context.LegacyOsc52 is not { } enabled) return null;
        return enabled ? Osc52Policy.Allow : Osc52Policy.Deny;
    }

    // ---- Rules for the common shapes of value ----------------------------------------------------------------

    private static Rule Flag(Func<Settings, bool, Settings> set) =>
        (c, key, s) => c.Reader.Bool(key) is { } value ? set(s, value) : s;

    private static Rule Text(Func<Settings, string, Settings> set) =>
        (c, key, s) => c.Reader.String(key) is { } value ? set(s, value) : s;

    private static Rule Number(double min, double max, Func<Settings, double, Settings> set) =>
        (c, key, s) => c.Reader.ReadNumber(key, min, max) is { } value ? set(s, value) : s;

    private static Rule Whole(double min, double max, Func<Settings, int, Settings> set) =>
        Number(min, max, (s, value) => set(s, (int)value));

    private static Rule Words(Func<Settings, List<string>, Settings> set) =>
        (c, key, s) => c.Reader.StringList(key) is { } value ? set(s, value) : s;

    private static Rule Color(Func<Settings, uint, Settings> set) =>
        (c, key, s) => c.Reader.ReadColor(key) is { } value ? set(s, value) : s;

    /// <summary>A string that must pass <paramref name="valid"/>; otherwise <paramref name="problem"/> is reported.</summary>
    private static Rule Checked(Func<string, bool> valid, Func<string, string> problem, Func<Settings, string, Settings> set) =>
        (c, key, s) =>
        {
            if (c.Reader.String(key) is not { } value) return s;
            if (valid(value)) return set(s, value);
            c.Reader.Warn(key, problem(value));
            return s;
        };

    /// <summary>One of a few words, in any case, stored trimmed and in lower case.</summary>
    private static Rule Choice(string expected, Func<Settings, string, Settings> set, params string[] allowed) =>
        Checked(value => allowed.Contains(Canonical(value)), _ => expected, (s, value) => set(s, Canonical(value)));

    private static string Canonical(string value) => value.Trim().ToLowerInvariant();

    /// <summary>An enum by name, in any case; numbers aren't names.</summary>
    private static Rule Named<T>(string expected, Func<Settings, T, Settings> set) where T : struct, Enum =>
        (c, key, s) =>
        {
            if (c.Reader.String(key) is not { } value) return s;
            if (TryParseName(value, out T parsed)) return set(s, parsed);
            c.Reader.Warn(key, expected);
            return s;
        };

    /// <summary>An enum by name, or true / false for <paramref name="whenTrue"/> / <paramref name="whenFalse"/>.</summary>
    private static Rule Switch<T>(T whenTrue, T whenFalse, string expected, Func<Settings, T, Settings> set) where T : struct, Enum =>
        (c, key, s) =>
        {
            c.Root.TryGet(key, out var raw, out _);
            if (raw is bool b) return set(s, b ? whenTrue : whenFalse);
            return Named(expected, set)(c, key, s);
        };

    private static bool TryParseName<T>(string value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value.Trim(), ignoreCase: true, out parsed) && !int.TryParse(value, out _);

    /// <summary>An enum as <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> reads it.</summary>
    private static Rule Lenient<T>(string expected, Func<Settings, T, Settings> set) where T : struct, Enum =>
        (c, key, s) =>
        {
            if (c.Reader.String(key) is not { } value) return s;
            if (Enum.TryParse(value, ignoreCase: true, out T parsed)) return set(s, parsed);
            c.Reader.Warn(key, expected);
            return s;
        };

    /// <summary>Read after the other settings, or by a later step of <see cref="SettingsLoader.Parse"/>.</summary>
    private static Settings Elsewhere(ParseContext context, string key, Settings settings) => settings;

    // ---- Settings with rules of their own --------------------------------------------------------------------

    private static Settings ReadColorScheme(ParseContext c, string key, Settings s)
    {
        if (c.Reader.String(key) is not { } name) return s;
        if (ColorSchemes.Find(name, c.CustomSchemes) is { } scheme) return s with { Colors = scheme };
        string builtIn = string.Join(", ", ColorSchemes.All.Select(scheme => scheme.Name));
        string others = c.CustomSchemes.Count > 0
            ? $"; from files: {string.Join(", ", c.CustomSchemes.Select(scheme => scheme.Name))})"
            : "; more can go in ~/.config/erikterm/colors)";
        c.Reader.Warn(key, $"unknown color scheme \"{name}\" (built in: {builtIn}{others}");
        return s;
    }

    private static Settings ReadCursorThickness(ParseContext c, string key, Settings s)
    {
        if (c.Root.TryGet(key, out var value, out _) && CursorThickness.Parse(value) is { } thickness) return s with { CursorThickness = thickness };
        c.Reader.Warn(key, "expected points (e.g. 1.5 or \"1.5px\", up to 32) or a fraction of the cell (\"0.1cell\", \"10%\")");
        return s;
    }

    private static Settings ReadCursorHeight(ParseContext c, string key, Settings s)
    {
        if (c.Root.TryGet(key, out var value, out _) && Settings.ParseCursorHeight(value) is { } height) return s with { CursorHeight = height };
        c.Reader.Warn(key, "expected \"cell\" or a multiple of the font size from 0.3 to 2");
        return s;
    }

    private static Settings ReadDefaultProg(ParseContext c, string key, Settings s)
    {
        if (c.Reader.StringList(key) is { Count: > 0 } program) return s with { DefaultProg = program };
        if (c.Root.TryGet(key, out _, out _)) c.Reader.Warn(key, "expected a non-empty array of strings, e.g. [\"zsh\", \"-l\"]");
        return s;
    }

    private static bool IsTerminalName(string term) =>
        term.Length > 0 && term.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '+');

    private static Settings ReadLegacyOsc52(ParseContext c, string key, Settings s)
    {
        if (c.Reader.Bool(key) is { } enabled) c.LegacyOsc52 = enabled;
        return s;
    }

    private static Settings ReadOsc52(ParseContext c, string key, Settings s)
    {
        if (c.Reader.String(key) is not { } value) return s;
        if (TryParseName(value, out Osc52Policy policy)) c.Osc52 = policy;
        else c.Reader.Warn(key, "expected \"allow\", \"ask\" or \"deny\"");
        return s;
    }

    private static Settings ReadSnippets(ParseContext c, string key, Settings s)
    {
        c.Root.TryGet(key, out var value, out int line);
        return s with { Snippets = Config.Snippets.Read(value, message => c.Diagnostics.Add($"{ConfigOverrides.Where(c.Path, line)}: {message}")) };
    }

    private static Settings ApplyWorktreeSetup(ParseContext c, string key, Settings s) =>
        ReadWorktreeSetup(c.Root, c.Reader) is { } setup ? s with { GitWorktreeSetup = setup } : s;

    /// <summary>git_worktree_setup: one command for every repository, or a table of repository name to command ("*": any other).</summary>
    private static Dictionary<string, string>? ReadWorktreeSetup(TomlTable root, ValueReader r)
    {
        const string key = "git_worktree_setup";
        root.TryGet(key, out var value, out _);
        var setup = value is string one ? new Dictionary<string, string> { ["*"] = one } : r.StringTable(key);
        if (setup is null) return null;
        foreach (var bad in setup.Where(kv => kv.Value.Any(char.IsControl)).Select(kv => kv.Key).ToList())
        {
            r.Warn(key, $"'{bad}' can't hold control characters (ignored)");
            setup.Remove(bad);
        }
        return setup;
    }

    // ---- The table ---------------------------------------------------------------------------------------------

    private static readonly (string Key, Rule Apply)[] RuleList =
    [
        ("font", Text((s, v) => s with { Font = v })),
        ("font_fallback", Words((s, v) => s with { FontFallback = v })),
        ("font_size", Number(4, 200, (s, v) => s with { FontSize = v })),
        ("line_height", Number(0.5, 4, (s, v) => s with { LineHeight = v })),
        ("harfbuzz_features", Words((s, v) => s with { HarfbuzzFeatures = v })),
        ("color_scheme", ReadColorScheme),
        ("colors", Elsewhere),
        ("bold_brightens_ansi_colors", Flag((s, v) => s with { BoldBrightensAnsiColors = v })),
        ("window_background_opacity", Number(0, 1, (s, v) => s with { WindowBackgroundOpacity = v })),
        ("text_background_opacity", Number(0, 1, (s, v) => s with { TextBackgroundOpacity = v })),
        ("default_cursor_style", Lenient<CursorStyle>($"expected one of {string.Join(", ", Enum.GetNames<CursorStyle>())}", (s, v) => s with { DefaultCursorStyle = v })),
        ("cursor_blink_rate", Whole(0, 10_000, (s, v) => s with { CursorBlinkRate = v })),
        ("cursor_blink_style", Lenient<CursorBlinkStyle>("expected \"fade\" or \"phase\"", (s, v) => s with { CursorBlinkStyle = v })),
        ("cursor_thickness", ReadCursorThickness),
        ("cursor_height", ReadCursorHeight),
        ("cursor_animation", Flag((s, v) => s with { CursorAnimation = v })),
        ("smooth_scrolling", Flag((s, v) => s with { SmoothScrolling = v })),
        ("window_decorations", Text((s, v) => s with { WindowDecorations = !v.Trim().Equals("NONE", StringComparison.OrdinalIgnoreCase) })),
        ("window_controls", Named<WindowControlsMode>("expected \"auto\", \"on\" or \"off\"", (s, v) => s with { WindowControls = v })),
        ("chrome_reveal", Named<ChromeRevealMode>("expected \"grow\" or \"push\"", (s, v) => s with { ChromeReveal = v })),
        ("window_blur", Named<WindowBlurMode>("expected \"auto\", \"none\", \"acrylic\" or \"mica\"", (s, v) => s with { WindowBlur = v })),
        ("window_padding", (c, key, s) => c.Reader.Padding(key) is { } padding ? s with { WindowPadding = padding } : s),
        ("initial_cols", Whole(10, 1000, (s, v) => s with { InitialCols = v })),
        ("initial_rows", Whole(3, 500, (s, v) => s with { InitialRows = v })),
        ("scrollback_lines", Whole(0, 1_000_000, (s, v) => s with { ScrollbackLines = v })),
        ("default_prog", ReadDefaultProg),
        ("default_cwd", Text((s, v) => s with { DefaultCwd = SettingsLoader.ExpandHome(v) })),
        ("set_environment_variables", (c, key, s) => c.Reader.StringTable(key) is { } env ? s with { SetEnvironmentVariables = env } : s),
        ("term", Checked(IsTerminalName, term => $"\"{term}\" isn't a terminal name (use \"auto\", \"xterm-256color\" or \"xterm-erikterm\")", (s, v) => s with { Term = v })),
        ("exit_behavior", Named<ExitBehavior>("expected \"Close\", \"Hold\" or \"CloseOnCleanExit\"", (s, v) => s with { ExitBehavior = v })),
        ("shell_integration", Named<ShellIntegrationMode>("expected \"auto\" or \"off\"", (s, v) => s with { ShellIntegration = v })),
        ("inactive_pane_hsb", (c, key, s) => c.Reader.Hsb(key) is { } hsb ? s with { InactivePaneHsb = hsb } : s),
        ("command_palette_bg_color", Color((s, v) => s with { CommandPaletteBgColor = v })),
        ("command_palette_fg_color", Color((s, v) => s with { CommandPaletteFgColor = v })),
        ("command_palette_font_size", Number(6, 72, (s, v) => s with { CommandPaletteFontSize = v })),
        ("command_palette_rows", Whole(3, 50, (s, v) => s with { CommandPaletteRows = v })),
        ("pane_select_bg_color", Color((s, v) => s with { PaneSelectBgColor = v })),
        ("pane_select_fg_color", Color((s, v) => s with { PaneSelectFgColor = v })),
        ("pane_select_font_size", Number(8, 200, (s, v) => s with { PaneSelectFontSize = v })),
        ("visual_bell", Flag((s, v) => s with { VisualBell = v })),
        ("minimum_contrast", Number(1, 21, (s, v) => s with { MinimumContrast = v })),
        ("animation_duration_ms", Whole(0, 2000, (s, v) => s with { AnimationDurationMs = v })),
        ("reduce_motion", Switch(AccessibilityMode.On, AccessibilityMode.Off, "expected \"auto\", \"on\" or \"off\"", (s, v) => s with { ReduceMotion = v })),
        ("high_contrast", Switch(AccessibilityMode.On, AccessibilityMode.Off, "expected \"auto\", \"on\" or \"off\"", (s, v) => s with { HighContrast = v })),
        ("text_scale", Switch(TextScaleMode.Auto, TextScaleMode.Off, "expected \"auto\" or \"off\"", (s, v) => s with { TextScale = v })),
        ("enable_tab_bar", Flag((s, v) => s with { EnableTabBar = v })),
        ("hide_tab_bar_if_only_one_tab", Flag((s, v) => s with { HideTabBarIfOnlyOneTab = v })),
        ("ctrl_tab_order", Choice("expected \"recent\" or \"position\"", (s, v) => s with { CtrlTabOrder = v }, "recent", "position")),
        ("tab_switcher_double_ctrl", Flag((s, v) => s with { TabSwitcherDoubleCtrl = v })),
        ("enable_osc52", ReadLegacyOsc52),
        ("osc52", ReadOsc52),
        ("paste_sanitize", Flag((s, v) => s with { PasteSanitize = v })),
        ("allow_kitty_file_transmission", Flag((s, v) => s with { AllowKittyFileTransmission = v })),
        ("tmux_emulation", Flag((s, v) => s with { TmuxEmulation = v })),
        ("tmux_agent_panes", Named<TmuxAgentPanes>("expected \"pills\" or \"split\"", (s, v) => s with { TmuxAgentPanes = v })),
        ("agent_status", Flag((s, v) => s with { AgentStatus = v })),
        ("agent_notifications", Flag((s, v) => s with { AgentNotifications = v })),
        ("agent_urgency", Flag((s, v) => s with { AgentUrgency = v })),
        ("os_notifications", Choice("expected \"ask\", \"on\" or \"off\"", (s, v) => s with { OsNotifications = v }, "ask", "on", "off")),
        ("notification_bar", Flag((s, v) => s with { NotificationBar = v })),
        ("notification_bar_position", Choice("expected \"top\" or \"bottom\"", (s, v) => s with { NotificationBarPosition = v }, "top", "bottom")),
        ("notification_bar_scope", Choice("expected \"tab\" or \"all\"", (s, v) => s with { NotificationBarScope = v }, "tab", "all")),
        ("notification_bar_timeout_ms", Whole(0, 3_600_000, (s, v) => s with { NotificationBarTimeoutMs = v })),
        ("notify_command_duration", Number(0, 86_400, (s, v) => s with { NotifyCommandDuration = v })),
        ("git_status", Flag((s, v) => s with { GitStatus = v })),
        ("github_status", Flag((s, v) => s with { GithubStatus = v })),
        ("git_worktree_dir", Checked(
            dir => dir.Contains("{name}", StringComparison.Ordinal) && !dir.Any(char.IsControl),
            _ => "must contain {name}, so every worktree gets its own directory",
            (s, v) => s with { GitWorktreeDir = v })),
        ("git_worktree_setup", ApplyWorktreeSetup),
        ("worktree_agents", Elsewhere),
        ("worktree_open_in", Choice("expected \"tab\" or \"split\"", (s, v) => s with { WorktreeOpenIn = v }, "tab", "split")),
        ("git_conflict_check", Flag((s, v) => s with { GitConflictCheck = v })),
        ("editor_open", Choice("expected \"nvim\", \"split\", \"system\" or \"none\"", (s, v) => s with { EditorOpen = v }, "nvim", "split", "system", "none")),
        ("drop_files", Flag((s, v) => s with { DropFiles = v })),
        ("remember_commands", Flag((s, v) => s with { RememberCommands = v })),
        ("snippets", ReadSnippets),
        ("restore_session", Flag((s, v) => s with { RestoreSession = v })),
        ("single_instance", Flag((s, v) => s with { SingleInstance = v })),
        ("restore_programs", Words((s, v) => s with { RestorePrograms = v })),
        ("restore_scrollback_lines", Whole(0, 100_000, (s, v) => s with { RestoreScrollbackLines = v })),
        ("window_close_confirmation", Named<WindowCloseConfirmation>("expected \"AlwaysPrompt\" or \"NeverPrompt\"", (s, v) => s with { WindowCloseConfirmation = v })),
        ("skip_close_confirmation_for_processes_named", Words((s, v) => s with { SkipCloseConfirmationForProcessesNamed = v })),
        ("disable_default_key_bindings", Flag((s, v) => s with { DisableDefaultKeyBindings = v })),
        ("macos_option_as_alt", Choice("expected \"left\", \"right\", \"both\" or \"none\"", (s, v) => s with { MacosOptionAsAlt = v }, "left", "right", "both", "none")),
        ("keys", Elsewhere),
    ];

    private static readonly Dictionary<string, Rule> Rules = RuleList.ToDictionary(rule => rule.Key, rule => rule.Apply);

    /// <summary>Every top-level setting <see cref="SettingsLoader.Parse"/> understands.</summary>
    public static readonly string[] KnownKeys = [.. RuleList.Select(rule => rule.Key)];
}
