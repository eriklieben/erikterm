namespace ErikTerm.Config;

/// <summary>How the settings page edits a setting.</summary>
public enum SettingKind
{
    /// <summary>true or false.</summary>
    Toggle,
    /// <summary>One of <see cref="SettingInfo.Choices"/> (for color_scheme: the schemes there are).</summary>
    Choice,
    /// <summary>A number from <see cref="SettingInfo.Min"/> to <see cref="SettingInfo.Max"/>.</summary>
    Number,
    /// <summary>A string.</summary>
    Text,
    /// <summary>A list of strings, typed separated by commas.</summary>
    List,
    /// <summary>A table or another shape the page doesn't edit: it opens the file.</summary>
    File,
}

/// <summary>
/// One setting as the settings page shows it. <see cref="Default"/> is the value when the file doesn't set it, as
/// shown (null when there's no single value: <see cref="DefaultLabel"/> says what happens instead).
/// <see cref="Platforms"/> lists where it has an effect (null: everywhere).
/// </summary>
public sealed record SettingInfo(string Key, string Group, string Label, string Help, SettingKind Kind, string? Default)
{
    public IReadOnlyList<string> Choices { get; init; } = [];
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; } = 1;
    public string Unit { get; init; } = "";
    public string? DefaultLabel { get; init; }
    public IReadOnlyList<string>? Platforms { get; init; }

    /// <summary>Whether the setting does something on this machine.</summary>
    public bool AppliesHere => Platforms is null || Platforms.Any(OnPlatform);

    /// <summary>"Windows only", "Windows and macOS only", or "" when it applies everywhere.</summary>
    public string PlatformNote => Platforms is null ? "" : string.Join(" and ", Platforms.Select(PlatformName)) + " only";

    private static bool OnPlatform(string platform) => platform switch
    {
        "windows" => OperatingSystem.IsWindows(),
        "macos" => OperatingSystem.IsMacOS(),
        "linux" => OperatingSystem.IsLinux(),
        _ => false,
    };

    private static string PlatformName(string platform) => platform switch
    {
        "windows" => "Windows",
        "macos" => "macOS",
        "linux" => "Linux",
        _ => platform,
    };
}

/// <summary>
/// Every top-level setting with a label and a plain explanation, in the order the settings page shows them. The keys
/// are the ones <see cref="SettingsLoader"/> reads (a test keeps the two lists the same).
/// </summary>
public static class SettingsCatalog
{
    private static readonly string[] WindowsOnly = ["windows"];
    private static readonly string[] Borderless = ["windows", "macos"];
    private static readonly string[] MacOnly = ["macos"];

    private static SettingInfo Toggle(string group, string key, string label, bool value, string help) =>
        new(key, group, label, help, SettingKind.Toggle, value ? "true" : "false");

    private static SettingInfo Choice(string group, string key, string label, string? value, string[] choices, string help) =>
        new(key, group, label, help, SettingKind.Choice, value) { Choices = choices };

    private static SettingInfo Number(string group, string key, string label, double value, double min, double max, double step, string unit, string help) =>
        new(key, group, label, help, SettingKind.Number, TomlEditor.Literal(value)) { Min = min, Max = max, Step = step, Unit = unit };

    private static SettingInfo Text(string group, string key, string label, string? value, string help) =>
        new(key, group, label, help, SettingKind.Text, value);

    private static SettingInfo List(string group, string key, string label, string? value, string help) =>
        new(key, group, label, help, SettingKind.List, value);

    private static SettingInfo InFile(string group, string key, string label, string help) =>
        new(key, group, label, help, SettingKind.File, null) { DefaultLabel = "edit in the file" };

    public const string Font = "Font", Colors = "Colours", Window = "Window", Cursor = "Cursor", Tabs = "Tabs & panes",
        Palette = "Command palette", Shell = "Shell & programs", Safety = "Clipboard & safety", Agents = "Coding agents",
        Notifications = "Notifications", Git = "Git & worktrees", Files = "Editor & files", Session = "Windows & session",
        Closing = "Closing", Keys = "Keys & snippets", Accessibility = "Accessibility";

    public static readonly IReadOnlyList<string> Groups =
        [Font, Colors, Window, Cursor, Accessibility, Tabs, Palette, Shell, Safety, Agents, Notifications, Git, Files, Session, Closing, Keys];

    public static readonly IReadOnlyList<SettingInfo> All =
    [
        Text(Font, "font", "Font family", "JetBrains Mono",
            "The font, by the name your system lists it under (fc-list on Linux). Characters it lacks come from the fallback fonts, then from the system."),
        List(Font, "font_fallback", "Fallback fonts", "",
            "Fonts tried in this order for characters the main font lacks, before the automatic fallbacks. Separate the names with commas.")
            with { DefaultLabel = "none" },
        Number(Font, "font_size", "Font size", 12, 4, 200, 0.5, "pt",
            "Text size in points. Ctrl+= and Ctrl+- zoom a single window without changing this."),
        Number(Font, "line_height", "Line height", 1.0, 0.5, 4, 0.05, "×",
            "A multiple of the font's own line height. Above 1 adds room between lines; box drawing still joins up."),
        List(Font, "harfbuzz_features", "Font features", "calt=1, liga=1, clig=1",
            "OpenType features. The defaults turn on ligatures, which join -> != === into single glyphs; calt=0, liga=0 turns them off. ss01, zero and the like switch on the font's stylistic sets."),

        Choice(Colors, "color_scheme", "Colour scheme", "erik", [],
            "The 16 terminal colours plus background, text, cursor and selection. Schemes from your colors folder are listed too, and ←/→ shows each one in your panes as you go. The palette's Browse Themes… has the whole catalogue."),
        InFile(Colors, "colors", "Colour overrides",
            "Single colours on top of the scheme: foreground, background, cursor, selection, the 16 ANSI colours. They go in the file under [colors]."),
        Toggle(Colors, "bold_brightens_ansi_colors", "Bold uses bright colours", true,
            "Bold text in one of the 8 basic colours is drawn in its bright variant, as most terminals do."),
        Number(Colors, "minimum_contrast", "Minimum contrast", 1, 1, 21, 0.5, "",
            "Moves text colours just far enough from their background to stay readable: 1 is off, 4.5 is WCAG AA, 7 is AAA. Box drawing and powerline glyphs are left alone."),

        Number(Window, "window_background_opacity", "Background opacity", 1, 0, 1, 0.05, "",
            "0 is fully clear, 1 is solid. The blur behind it comes from your compositor; on Windows, Backdrop asks for it."),
        Number(Window, "text_background_opacity", "Text background opacity", 1, 0, 1, 0.05, "",
            "For cells a program paints with its own background, such as a CLI's input box. At 1 they stay solid on a see-through window; lower it to let the transparency through."),
        Choice(Window, "window_blur", "Backdrop", "auto", ["auto", "none", "acrylic", "mica"],
            "What Windows paints behind see-through pixels. auto: acrylic while the window is see-through, nothing when it's opaque.")
            with { Platforms = WindowsOnly },
        Choice(Window, "window_decorations", "Window frame", "RESIZE", ["RESIZE", "NONE"],
            "NONE for a borderless window. On Windows and macOS erikterm then draws its own: the tab bar slides in at the top edge. On Linux NONE only drops the compositor's frame."),
        Choice(Window, "window_controls", "Window buttons", "auto", ["auto", "on", "off"],
            "Minimize, maximize and close in a borderless window, shown with the bar at the top edge. auto: on, except on macOS.")
            with { Platforms = Borderless },
        Choice(Window, "chrome_reveal", "Making room for the bar", "grow", ["grow", "push"],
            "How a borderless window shows its bar. grow moves the window up and makes it taller, so the text stays put; push keeps the window still and slides the text down.")
            with { Platforms = Borderless },
        InFile(Window, "window_padding", "Padding",
            "Space between the window's edge and the text, in pixels: one number, or { left, right, top, bottom }."),
        Number(Window, "initial_cols", "Width of a new window", 120, 10, 1000, 1, "columns",
            "Used when there's no saved session to bring back."),
        Number(Window, "initial_rows", "Height of a new window", 32, 3, 500, 1, "rows",
            "Used when there's no saved session to bring back."),

        Choice(Cursor, "default_cursor_style", "Cursor shape", "SteadyBlock",
            ["SteadyBlock", "BlinkingBlock", "SteadyUnderline", "BlinkingUnderline", "SteadyBar", "BlinkingBar"],
            "Programs can still change it, as vim does with guicursor."),
        Number(Cursor, "cursor_blink_rate", "Blink speed", 800, 0, 10_000, 50, "ms",
            "Time between visible and invisible, for the blinking shapes. 0 never blinks."),
        Choice(Cursor, "cursor_blink_style", "Blink style", "fade", ["fade", "phase"],
            "fade: on and off with eased edges. phase: a continuous, breathing pulse."),
        Text(Cursor, "cursor_thickness", "Bar and underline thickness", null,
            "Points (1.5 or \"1.5px\", scaled with the display) or a part of the cell (\"0.1cell\", \"10%\").")
            with { DefaultLabel = "follows the font" },
        Number(Cursor, "cursor_height", "Bar height", 1.19, 0.3, 2, 0.05, "× font size",
            "The bar's height as a multiple of the font size, whatever the line height. \"cell\" in the file fills the whole line."),
        Toggle(Cursor, "cursor_animation", "Gliding cursor", true,
            "The cursor glides on short moves. Typing still moves it at once."),
        Toggle(Cursor, "smooth_scrolling", "Smooth scrolling", true,
            "Scrolling eases between lines, and trackpads scroll by pixels."),

        Choice(Accessibility, "reduce_motion", "Reduce motion", "auto", ["auto", "on", "off"],
            "on: no animations, no gliding cursor, no smooth scrolling, and the cursor doesn't blink. Your other settings stay as they are in the file. auto: on when your system is set to reduce motion or show no animations."),
        Choice(Accessibility, "high_contrast", "High contrast", "auto", ["auto", "on", "off"],
            "on: text gets a contrast of at least 7 (WCAG AAA) and the tab bar, palette and dividers get stronger outlines and text. With a contrast theme on Windows the panes take its colours. auto: on when your system is set to high or increased contrast."),
        Choice(Accessibility, "text_scale", "Follow the system text size", "auto", ["auto", "off"],
            "auto: the font sizes grow with the text size in your system settings (Windows: Make text bigger; GNOME: Large Text or text scaling). off: they stay as set here. Display scaling is applied either way."),

        Toggle(Tabs, "enable_tab_bar", "Tab bar", true, "Shows the tab bar."),
        Toggle(Tabs, "hide_tab_bar_if_only_one_tab", "Hide the tab bar with one tab", true,
            "While a window has a single tab, the tab bar stays out of the way."),
        Choice(Tabs, "ctrl_tab_order", "Ctrl+Tab order", "recent", ["recent", "position"],
            "recent: a list of the tabs, most recently used first; hold Ctrl and let go to switch. position: the next tab in the bar."),
        Toggle(Tabs, "tab_switcher_double_ctrl", "Double-tap Ctrl for the tab list", true,
            "Tap Ctrl twice on its own to open the tab list, which stays up until you pick a tab."),
        InFile(Tabs, "inactive_pane_hsb", "Dimming of unfocused panes",
            "Saturation and brightness of the panes you're not typing in, as { saturation = 0.9, brightness = 0.8 }."),
        Toggle(Tabs, "visual_bell", "Visual bell", true, "A bell flashes the pane."),
        Number(Tabs, "animation_duration_ms", "Animation length", 160, 0, 2000, 20, "ms",
            "Length of splits, tab and menu animations. 0 turns them off."),
        Number(Tabs, "pane_select_font_size", "Pane select letters", 36, 8, 200, 2, "pt",
            "Size of the letters Pane Select (Ctrl+Shift+O) puts on each pane."),
        Text(Tabs, "pane_select_bg_color", "Pane select background", null, "A colour such as \"#b7bdf8\".")
            with { DefaultLabel = "the scheme's accent" },
        Text(Tabs, "pane_select_fg_color", "Pane select text", null, "A colour such as \"#1e2030\".")
            with { DefaultLabel = "from the scheme" },

        Number(Palette, "command_palette_font_size", "Text size", 12, 6, 72, 1, "pt", "Text size in the command palette."),
        Number(Palette, "command_palette_rows", "Rows", 12, 3, 50, 1, "rows", "How many results the palette shows at once."),
        Text(Palette, "command_palette_bg_color", "Background", null, "A colour such as \"#1e2030\".")
            with { DefaultLabel = "a shade darker than the background" },
        Text(Palette, "command_palette_fg_color", "Text colour", null, "A colour such as \"#cad3f5\".")
            with { DefaultLabel = "the scheme's text" },

        List(Shell, "default_prog", "Program", null,
            "What a new pane runs, with its arguments, separated by commas: zsh, -l.")
            with { DefaultLabel = "your login shell" },
        Text(Shell, "default_cwd", "Start folder", null,
            "The folder new panes start in; ~ is your home folder.")
            with { DefaultLabel = "where erikterm was started" },
        InFile(Shell, "set_environment_variables", "Environment variables",
            "Extra variables for every pane, as a table: { EDITOR = \"nvim\" }."),
        Text(Shell, "term", "TERM", "auto",
            "What programs are told this terminal is. auto: xterm-erikterm when its terminfo is installed (undercurl, coloured underlines, true colour in Neovim and tmux), else xterm-256color."),
        Choice(Shell, "shell_integration", "Shell integration", "auto", ["auto", "off"],
            "auto: the zsh, bash, fish or PowerShell erikterm starts reports its prompts, commands and folder (OSC 133 and OSC 7), with no dotfile edits. Jumping between prompts and copying a command's output need it."),
        Choice(Shell, "exit_behavior", "When a program exits", "Close", ["Close", "Hold", "CloseOnCleanExit"],
            "Close: the pane closes. Hold: it stays, with Restart and Close. CloseOnCleanExit: it closes only when the program succeeded."),
        Number(Shell, "scrollback_lines", "Scrollback", 10_000, 0, 1_000_000, 1000, "lines",
            "Lines kept above the screen in each pane. Applies at once; making it smaller keeps the newest lines."),

        Choice(Safety, "osc52", "Programs setting the clipboard", "ask", ["ask", "allow", "deny"],
            "For programs such as tmux, or Neovim over ssh. ask: a bar in the pane asks you first."),
        Toggle(Safety, "paste_sanitize", "Clean up pastes", true,
            "Strips escape sequences and control characters from pastes into programs that don't use bracketed paste. Inside bracketed paste they're always stripped."),
        Toggle(Safety, "allow_kitty_file_transmission", "Images from files", false,
            "Lets programs send kitty images as a file path, temp file or shared memory, which makes erikterm read a local file. Images sent inline always work."),

        Toggle(Agents, "agent_status", "Agent status", true,
            "Notices Claude Code, Codex and other coding agents in a pane, and marks its tab and pane as waiting, working or done."),
        Toggle(Agents, "agent_notifications", "Desktop notifications from agents", true,
            "Off: agents never notify the desktop. They still show up in the notification bar."),
        Toggle(Agents, "agent_urgency", "Flash the taskbar", true,
            "When an agent waits for you and the window isn't focused."),
        Toggle(Agents, "tmux_emulation", "tmux emulation", true,
            "Every pane gets $TMUX and a small tmux of erikterm's own: a program that asks tmux for a split or a window gets a real erikterm pane. Claude Code's agent teams use it. Applies to new panes."),
        Choice(Agents, "tmux_agent_panes", "Teammate panes", "pills", ["pills", "split"],
            "pills: Claude Code teammates keep running out of sight, as pills under the lead pane; click one to open it. split: each gets its own pane."),
        InFile(Agents, "worktree_agents", "Agents in New Agent Worktree",
            "Claude Code, Copilot and Codex are built in. Add others or change their arguments under [worktree_agents.NAME]."),

        Choice(Notifications, "os_notifications", "Desktop notifications", null, ["ask", "on", "off"],
            "Unset, erikterm uses the answer you gave its warning bar. on or off here wins over that answer.")
            with { DefaultLabel = "your earlier answer" },
        Toggle(Notifications, "notification_bar", "Notification bar", true,
            "A bar in the window with the latest unread notification, how many are unread, and Jump. Off: short pop-ups instead."),
        Choice(Notifications, "notification_bar_position", "Bar position", "top", ["top", "bottom"],
            "top: under the tab bar."),
        Choice(Notifications, "notification_bar_scope", "Bar shows", "tab", ["tab", "all"],
            "tab: the open tab's notifications, with a count for the other tabs. all: every window's."),
        Number(Notifications, "notification_bar_timeout_ms", "Hide from the bar after", 10_000, 0, 3_600_000, 1000, "ms",
            "Only items that don't need you: an agent that waits stays. 0 keeps everything."),
        Number(Notifications, "notify_command_duration", "Announce long commands", 10, 0, 86_400, 5, "s",
            "A shell command that ran at least this long and finishes while you look elsewhere is announced: ✓ make finished in 1m 12s. 0 turns it off."),

        Toggle(Git, "git_status", "Git status on tabs", true,
            "Branch, commits ahead and behind, and changed files on tabs and panes, from git status in the background."),
        Toggle(Git, "github_status", "Pull request and checks", true,
            "The branch's pull request and CI checks, through gh when it's installed and logged in."),
        Toggle(Git, "git_conflict_check", "Conflict warning", true,
            "A warning on a tab when a merge with the default branch would conflict. Checked only when the branch or its target moved."),
        Text(Git, "git_worktree_dir", "Where worktrees go", "../{repo}.worktrees/{name}",
            "For New Agent Worktree (Ctrl+Shift+G), relative to the repository's root. It must contain {name}."),
        Choice(Git, "worktree_open_in", "Open a new worktree in", "tab", ["tab", "split"],
            "A new tab, or a split next to the focused pane. Alt+Enter in the form picks the other."),
        InFile(Git, "git_worktree_setup", "Worktree setup command",
            "A command run in a new worktree before the agent starts, such as pnpm install. A table sets one per repository."),

        Choice(Files, "editor_open", "Ctrl+click on path:line", "nvim", ["nvim", "split", "system", "none"],
            "nvim: a Neovim already running in the tab, else a split. split: your editor in a split. system: the app your system opens the file with."),
        Toggle(Files, "drop_files", "Drop files on a pane", true, "Pastes their paths, quoted for the shell."),
        Toggle(Files, "remember_commands", "Remember commands and folders", true,
            "Recent commands and folders show up in the palette. A command typed with a leading space never is. Off: nothing is recorded."),

        Toggle(Session, "single_instance", "One erikterm", true,
            "Starting erikterm again opens a window in the one already running, when it uses the same config file. --new-process opts out once."),
        Toggle(Session, "restore_session", "Bring back the last session", true,
            "Tabs, splits, folders, window sizes and zoom come back at the next start."),
        List(Session, "restore_programs", "Programs to start again", "nvim, btop, htop, less, man",
            "When one of these was running in a pane, it's started again on restore."),
        Number(Session, "restore_scrollback_lines", "Scrollback to bring back", 0, 0, 100_000, 100, "lines",
            "The last lines of each pane are saved and shown again on restore. 0 saves none."),

        Choice(Closing, "window_close_confirmation", "Ask before closing", "AlwaysPrompt", ["AlwaysPrompt", "NeverPrompt"],
            "AlwaysPrompt: closing a pane, tab or window that runs a program or a coding agent asks first."),
        List(Closing, "skip_close_confirmation_for_processes_named", "Close without asking for",
            string.Join(", ", Settings.DefaultSkipCloseConfirmation),
            "Programs that never make closing ask, such as your shell."),

        InFile(Keys, "keys", "Key bindings",
            "Your own keys, under [keys]: \"CTRL+h\" = \"ActivatePaneDirection(Left)\". \"None\" turns a default key off."),
        Toggle(Keys, "disable_default_key_bindings", "Start without the default keys", false,
            "Only your own keys stay."),
        Choice(Keys, "macos_option_as_alt", "Option as Alt", "left", ["left", "right", "both", "none"],
            "Which Option key acts as Alt. The other one keeps typing é, ø and friends.")
            with { Platforms = MacOnly },
        InFile(Keys, "snippets", "Snippets",
            "Text you insert from the palette, under [snippets]: gs = \"git status\". {{cwd}} is the pane's folder."),
    ];

    /// <summary>Settings the page leaves out: old names kept working for existing files.</summary>
    public static readonly IReadOnlyList<string> Hidden = ["enable_osc52"];
}
