using System.Globalization;
using ErikTerm.Core;

namespace ErikTerm.Config;

public enum Osc52Policy { Allow, Ask, Deny }

/// <summary>shell_integration: whether erikterm injects its OSC 133 / OSC 7 scripts into the shells it starts itself.</summary>
public enum ShellIntegrationMode { Auto, Off }

/// <summary>How the tmux emulation shows Claude Code's agent-team teammates (tmux_agent_panes).</summary>
public enum TmuxAgentPanes
{
    /// <summary>Parked: the teammate keeps running hidden, as a pill in a strip below the lead pane; a click opens it as a split.</summary>
    Pills,
    /// <summary>As tmux shows them: a split per teammate.</summary>
    Split,
}

/// <summary>
/// wezterm's window_close_confirmation: AlwaysPrompt asks before closing a pane, tab or window in which something other
/// than a shell runs (a program, a coding agent even when idle); NeverPrompt closes at once.
/// </summary>
public enum WindowCloseConfirmation { AlwaysPrompt, NeverPrompt }

/// <summary>
/// Whether a borderless window draws its own minimize / maximize / close buttons. Auto turns them on where the
/// platform puts window buttons on the right and expects the app to draw them in a borderless window, and off on
/// macOS, whose buttons belong top-left and are the system's to draw.
/// </summary>
public enum WindowControlsMode { Auto, On, Off }

/// <summary>
/// How a borderless window makes room for the chrome that slides in at its top edge. Grow moves the window up and
/// makes it taller, so the text stays where it is on screen; Push keeps the window still and slides the text down
/// inside it. Either way the panes keep their rows and columns, so nothing reflows.
/// </summary>
public enum ChromeRevealMode { Grow, Push }

/// <summary>
/// What the compositor paints behind a translucent window. Only Windows acts on it: elsewhere the blur behind a
/// window is the compositor's own business and an application cannot ask for it.
/// </summary>
public enum WindowBlurMode
{
    /// <summary>Blur a see-through window, leave an opaque one alone.</summary>
    Auto,
    None,
    /// <summary>A live blur of whatever is behind the window (Windows Terminal's "acrylic").</summary>
    Acrylic,
    /// <summary>Tinted from the wallpaper instead of from what is behind the window.</summary>
    Mica,
}

public enum CursorStyle { SteadyBlock, BlinkingBlock, SteadyUnderline, BlinkingUnderline, SteadyBar, BlinkingBar }

/// <summary>
/// wezterm's exit_behavior: what a pane does when its program exits. Close (the default, as in wezterm) closes it;
/// Hold keeps it with its output and a bar offering restart / close; CloseOnCleanExit closes on exit status 0 and holds
/// otherwise (note that a shell exits with its last command's status).
/// </summary>
public enum ExitBehavior { Close, Hold, CloseOnCleanExit }

/// <summary>One [worktree_agents.NAME] table; null keeps the built-in value (or the default for a new agent).</summary>
public sealed record WorktreeAgentSettings(string Key, string? Name, string? Command, IReadOnlyList<string>? Args, IReadOnlyList<string>? PromptArgs);

/// <summary>How a blinking cursor blinks: on/off with eased edges, or a continuous breathing pulse (Sublime Text's "phase").</summary>
public enum CursorBlinkStyle { Fade, Phase }

/// <summary>Bar/underline cursor thickness: in window points (scaled with the display), or a fraction of the cell; both 0 = automatic.</summary>
public readonly record struct CursorThickness(double Points, double Cells)
{
    public static CursorThickness Auto => default;
    public bool IsAuto => Points <= 0 && Cells <= 0;

    /// <summary>"1.5px" / 1.5 (points), "0.1cell", "10%" (of the cell); null when unreadable.</summary>
    public static CursorThickness? Parse(object value)
    {
        switch (value)
        {
            case long l when l > 0 && l <= 32: return new(l, 0);
            case double d when d > 0 && d <= 32: return new(d, 0);
            case string s:
                s = s.Trim().ToLowerInvariant();
                static double? Num(string t) =>
                    double.TryParse(t.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
                if (s.EndsWith("px") && Num(s[..^2]) is { } px && px > 0 && px <= 32) return new(px, 0);
                if (s.EndsWith("cell") && Num(s[..^4]) is { } c && c > 0 && c <= 1) return new(0, c);
                if (s.EndsWith('%') && Num(s[..^1]) is { } pc && pc > 0 && pc <= 100) return new(0, pc / 100);
                return null;
            default: return null;
        }
    }
}

/// <summary>wezterm's inactive_pane_hsb: multipliers applied to the colors of unfocused panes.</summary>
public readonly record struct Hsb(double Hue, double Saturation, double Brightness);

public readonly record struct Padding(int Left, int Top, int Right, int Bottom)
{
    public static Padding All(int v) => new(v, v, v, v);
}

/// <summary>
/// erikterm settings. Keys deliberately reuse wezterm's names so a wezterm.lua translates line by line.
/// Every property has a default; the config file only overrides.
/// </summary>
public sealed record Settings
{
    public string Font { get; init; } = "JetBrains Mono";
    public IReadOnlyList<string> FontFallback { get; init; } = [];
    public double FontSize { get; init; } = 12;
    public double LineHeight { get; init; } = 1.0;
    /// <summary>OpenType features for HarfBuzz shaping, e.g. "calt=0"; an empty list disables shaping (no ligatures).</summary>
    public IReadOnlyList<string> HarfbuzzFeatures { get; init; } = ["calt=1", "liga=1", "clig=1"];

    public ColorScheme Colors { get; init; } = ColorSchemes.Erik;
    public bool BoldBrightensAnsiColors { get; init; } = true;
    public double WindowBackgroundOpacity { get; init; } = 1.0;
    public double TextBackgroundOpacity { get; init; } = 1.0;

    public CursorStyle DefaultCursorStyle { get; init; } = CursorStyle.SteadyBlock;
    /// <summary>Time between the blinking cursor's visible and invisible states (wezterm's cursor_blink_rate); 0 = never blink.</summary>
    public int CursorBlinkRate { get; init; } = 800;
    /// <summary>Fade (on/off with eased edges) or Phase (a continuous pulse).</summary>
    public CursorBlinkStyle CursorBlinkStyle { get; init; } = CursorBlinkStyle.Fade;
    /// <summary>Width of the bar cursor / height of the underline cursor; Auto follows the font's line thickness.</summary>
    public CursorThickness CursorThickness { get; init; } = CursorThickness.Auto;
    /// <summary>cursor_height: a bar cursor's height in multiples of the font size; 0 fills the cell.</summary>
    public double CursorHeight { get; init; } = 1.19;

    /// <summary>"cell" (0) or a multiple of the font size; null when unreadable.</summary>
    public static double? ParseCursorHeight(object value) => value switch
    {
        string s when s.Trim().Equals("cell", StringComparison.OrdinalIgnoreCase) => 0,
        long l when l >= 1 && l <= 2 => l,
        double d when d >= 0.3 && d <= 2 => d,
        _ => null,
    };
    /// <summary>The cursor glides to its new position on short moves.</summary>
    public bool CursorAnimation { get; init; } = true;
    /// <summary>Scrolling eases between lines (and follows trackpads pixel by pixel) instead of jumping whole lines.</summary>
    public bool SmoothScrolling { get; init; } = true;
    public bool WindowDecorations { get; init; } = true;
    /// <summary>Only ever shown when window_decorations = "NONE": a decorated window already has its own.</summary>
    public WindowControlsMode WindowControls { get; init; } = WindowControlsMode.Auto;
    /// <summary>Only for a borderless window on Windows and macOS, where the chrome slides in at the top edge.</summary>
    public ChromeRevealMode ChromeReveal { get; init; } = ChromeRevealMode.Grow;
    /// <summary>Windows only; needs window_background_opacity below 1 to be visible at all.</summary>
    public WindowBlurMode WindowBlur { get; init; } = WindowBlurMode.Auto;
    public Padding WindowPadding { get; init; } = Padding.All(6);
    public int InitialCols { get; init; } = 120;
    public int InitialRows { get; init; } = 32;

    public int ScrollbackLines { get; init; } = 10_000;
    public IReadOnlyList<string>? DefaultProg { get; init; }
    public string? DefaultCwd { get; init; }
    public IReadOnlyDictionary<string, string> SetEnvironmentVariables { get; init; } = new Dictionary<string, string>();
    /// <summary>What a pane does when its program exits (wezterm's names): close, hold, or close only on exit status 0.</summary>
    public ExitBehavior ExitBehavior { get; init; } = ExitBehavior.Close;
    /// <summary>"auto": the zsh, bash or fish erikterm starts itself gets the integration without any dotfile edits; "off": never.</summary>
    public ShellIntegrationMode ShellIntegration { get; init; } = ShellIntegrationMode.Auto;

    /// <summary>
    /// TERM for programs: "auto" (default) is xterm-erikterm when that terminfo entry is installed
    /// (scripts/install-terminfo.sh), else xterm-256color; any other value is used as given. Not set on Windows.
    /// </summary>
    public string Term { get; init; } = "auto";

    // --- Panes & UI (wezterm names where wezterm has the setting) ---
    public Hsb InactivePaneHsb { get; init; } = new(1.0, 0.9, 0.8);
    public uint? CommandPaletteBgColor { get; init; }
    public uint? CommandPaletteFgColor { get; init; }
    public double CommandPaletteFontSize { get; init; } = 12;
    public int CommandPaletteRows { get; init; } = 12;
    public uint? PaneSelectBgColor { get; init; }
    public uint? PaneSelectFgColor { get; init; }
    public double PaneSelectFontSize { get; init; } = 36;
    public bool VisualBell { get; init; } = true;
    /// <summary>
    /// Text is drawn with at least this contrast ratio (WCAG: 1 = off, 4.5 = AA, 7 = AAA) against its cell's background,
    /// moving its color toward white or black as far as needed (kitty / ghostty's minimum_contrast). Null when the config
    /// doesn't set it: the color scheme's own default applies (<see cref="ColorScheme.MinimumContrast"/>, 4.5 for the
    /// built-in schemes, off for scheme files); a value the user sets always wins.
    /// </summary>
    public double? MinimumContrast { get; init; }

    /// <summary>The minimum_contrast panes in <paramref name="scheme"/> are drawn with: the config's, else the scheme's.</summary>
    public double MinimumContrastFor(ColorScheme scheme) => MinimumContrast ?? scheme.MinimumContrast;
    /// <summary>Length of UI animations (splits, menus, focus dimming). 0 disables them.</summary>
    public int AnimationDurationMs { get; init; } = 160;
    public bool EnableTabBar { get; init; } = true;
    public bool HideTabBarIfOnlyOneTab { get; init; } = true;
    /// <summary>ctrl_tab_order: what Ctrl+Tab cycles through ("recent" through the list, "position" by the bar's order with no list).</summary>
    public string CtrlTabOrder { get; init; } = "recent";
    /// <summary>tab_switcher_double_ctrl: tapping Ctrl twice on its own opens the tab list.</summary>
    public bool TabSwitcherDoubleCtrl { get; init; } = true;

    // --- Accessibility: follow the operating system unless set (AccessibilityPolicy applies them) ---
    /// <summary>reduce_motion: no UI animations, gliding cursor, smooth scrolling or blinking cursor.</summary>
    public AccessibilityMode ReduceMotion { get; init; } = AccessibilityMode.Auto;
    /// <summary>high_contrast: minimum_contrast of at least 7 and stronger UI chrome (and on Windows the theme's colours).</summary>
    public AccessibilityMode HighContrast { get; init; } = AccessibilityMode.Auto;
    /// <summary>text_scale: the font sizes follow the system's text size.</summary>
    public TextScaleMode TextScale { get; init; } = TextScaleMode.Auto;

    /// <summary>Whether programs may set the clipboard with OSC 52 (reading it is never allowed): allow, ask (a bar in the window), deny.</summary>
    public Osc52Policy Osc52 { get; init; } = Osc52Policy.Ask;
    /// <summary>The older on/off switch (enable_osc52; true = allow, false = deny). osc52 wins when both are set.</summary>
    public bool EnableOsc52 => Osc52 != Osc52Policy.Deny;
    /// <summary>Outside a bracketed paste, strip ESC sequences and control characters (but TAB and Enter) from pasted text. Inside one they always go.</summary>
    public bool PasteSanitize { get; init; } = true;
    /// <summary>
    /// Let programs send kitty graphics by naming a file, temporary file or shared memory object (t=f / t=t / t=s)
    /// for the terminal to read. Off: anything printed to the terminal could make it read your files. Inline image
    /// data (t=d, what Claude Code and most tools use) always works.
    /// </summary>
    public bool AllowKittyFileTransmission { get; init; }
    /// <summary>
    /// New panes get $TMUX / $TMUX_PANE and a `tmux` that drives erikterm (so Claude Code's agent teams open
    /// erikterm panes); real tmux still runs when started interactively.
    /// </summary>
    public bool TmuxEmulation { get; init; } = true;
    /// <summary>Claude Code agent-team teammates opened through the tmux emulation: pills below the lead pane, or splits.</summary>
    public TmuxAgentPanes TmuxAgentPanes { get; init; } = TmuxAgentPanes.Pills;

    /// <summary>Detect coding agents (Claude Code, Codex, …) and show their state on tabs and panes.</summary>
    public bool AgentStatus { get; init; } = true;
    /// <summary>Desktop notification (notify-send) / toast when an agent waits or finishes out of sight.</summary>
    public bool AgentNotifications { get; init; } = true;
    /// <summary>Flash the window in the taskbar when an agent waits while the window is unfocused.</summary>
    public bool AgentUrgency { get; init; } = true;

    // --- Notifications (agents, OSC 9 / 777 / 99) ---
    /// <summary>
    /// Desktop notifications (notify-send): "on", "off", or "ask" / unset — use the answer remembered in
    /// preferences.json, and ask with a bar in the window the first time. "on"/"off" here win over that answer;
    /// agent_notifications = false additionally keeps agents' notifications off the desktop.
    /// </summary>
    public string? OsNotifications { get; init; }
    /// <summary>The in-window notification bar (latest unread notification, unread count, Jump); off: toasts as before.</summary>
    public bool NotificationBar { get; init; } = true;
    /// <summary>"top" (under the tab bar) or "bottom".</summary>
    public string NotificationBarPosition { get; init; } = "top";
    /// <summary>Which notifications the bar shows: the open tab's ("tab", the default; others stay on their tabs and in the panel) or every window's ("all").</summary>
    public string NotificationBarScope { get; init; } = "tab";
    /// <summary>How long a notification that isn't waiting for you stays in the bar (it stays unread in the panel); 0 = until dismissed.</summary>
    public int NotificationBarTimeoutMs { get; init; } = 10_000;
    /// <summary>
    /// Seconds a shell command (OSC 133) must run before its end is announced — when it ends in a pane you aren't
    /// looking at — as "✓ make finished in 1m 12s" / "✗ make exited 2" through the notifications; 0 turns it off.
    /// </summary>
    public double NotifyCommandDuration { get; init; } = 10;

    // --- Developer-tool integrations (erikterm's own) ---
    /// <summary>Branch, ahead/behind and change counts per pane (git status in the background) on tabs and panes.</summary>
    public bool GitStatus { get; init; } = true;
    /// <summary>The branch's pull request and CI checks via the GitHub CLI (gh), when it is installed and logged in.</summary>
    public bool GithubStatus { get; init; } = true;
    /// <summary>New Agent Worktree: where worktrees go ({repo}, {name}; relative to the main worktree's root).</summary>
    public string GitWorktreeDir { get; init; } = "../{repo}.worktrees/{name}";
    /// <summary>New Agent Worktree: the command run in a new worktree before the agent, per repository name ("*": any other).</summary>
    public IReadOnlyDictionary<string, string> GitWorktreeSetup { get; init; } = new Dictionary<string, string>();
    /// <summary>"tab" or "split": where New Agent Worktree opens the agent (Alt+Enter the other one).</summary>
    public string WorktreeOpenIn { get; init; } = "tab";
    /// <summary>Warn on a tab when merge-tree predicts a conflict with the default branch.</summary>
    public bool GitConflictCheck { get; init; } = true;
    /// <summary>New Agent Worktree: [worktree_agents.NAME] tables, on top of the built-in agents.</summary>
    public IReadOnlyList<WorktreeAgentSettings> WorktreeAgents { get; init; } = [];
    /// <summary>How Ctrl+click opens file links: "nvim" (a running Neovim in the tab, else a new split), "split", "system" (xdg-open) or "none".</summary>
    public string EditorOpen { get; init; } = "nvim";
    /// <summary>Dropping files on a pane pastes their shell-quoted paths.</summary>
    public bool DropFiles { get; init; } = true;

    /// <summary>
    /// Keep the commands (OSC 133) and directories (OSC 7) of every pane for the palette's "Run again" / "Go to" entries,
    /// across restarts (history.json in the state directory). Commands the shell keeps out of its history (a leading
    /// space) are never kept. False: nothing is recorded or shown, and nothing is written.
    /// </summary>
    public bool RememberCommands { get; init; } = true;

    /// <summary>Named pieces of text for the palette ("Snippet: name") and SendSnippet(name).</summary>
    public IReadOnlyList<Snippet> Snippets { get; init; } = [];

    /// <summary>A second `erikterm` opens a window in the running instance (same config, --set and --class) instead of a process of its own.</summary>
    public bool SingleInstance { get; init; } = true;

    /// <summary>Rebuild the saved windows' tabs and splits on start (unless -e or --no-restore).</summary>
    public bool RestoreSession { get; init; } = true;
    /// <summary>Foreground programs re-run on restore, matched on the program name.</summary>
    public IReadOnlyList<string> RestorePrograms { get; init; } = ["nvim", "btop", "htop", "less", "man"];
    /// <summary>Lines of each pane's history saved with the session and replayed on restore (0 = none).</summary>
    public int RestoreScrollbackLines { get; init; }

    /// <summary>Ask before closing a pane, tab or window that runs something (wezterm's setting and names).</summary>
    public WindowCloseConfirmation WindowCloseConfirmation { get; init; } = WindowCloseConfirmation.AlwaysPrompt;
    /// <summary>Foreground programs that never make a close ask (shells and multiplexers; wezterm's list).</summary>
    public IReadOnlyList<string> SkipCloseConfirmationForProcessesNamed { get; init; } = DefaultSkipCloseConfirmation;
    public static readonly IReadOnlyList<string> DefaultSkipCloseConfirmation =
        ["bash", "sh", "zsh", "fish", "tmux", "nu", "cmd.exe", "pwsh.exe", "powershell.exe"];

    public bool DisableDefaultKeyBindings { get; init; }
    /// <summary>
    /// macOS: which Option key acts as Alt/Meta (Alt+key bindings, ESC-prefixed keys for the program) — "left" (the
    /// default), "right", "both" or "none"; the other one types the characters the layout puts on it (Option+e → é).
    /// </summary>
    public string MacosOptionAsAlt { get; init; } = "left";
    /// <summary>User [keys] bindings, applied over the defaults.</summary>
    public IReadOnlyList<KeyBinding> Keys { get; init; } = [];

    /// <summary>The effective key map: defaults (unless disabled) overridden by [keys].</summary>
    public List<KeyBinding> EffectiveKeys()
    {
        var map = new Dictionary<KeyChord, KeyBinding>();
        if (!DisableDefaultKeyBindings)
            foreach (var b in DefaultKeys.Build()) map[b.Chord] = b;
        foreach (var b in Keys) map[b.Chord] = b;
        return map.Values.Where(b => b.Action.Name != "None").ToList();
    }

    public CursorShape CursorShape => DefaultCursorStyle switch
    {
        CursorStyle.SteadyBar or CursorStyle.BlinkingBar => CursorShape.Bar,
        CursorStyle.SteadyUnderline or CursorStyle.BlinkingUnderline => CursorShape.Underline,
        _ => CursorShape.Block,
    };

    public bool CursorBlinks => DefaultCursorStyle is CursorStyle.BlinkingBlock or CursorStyle.BlinkingUnderline or CursorStyle.BlinkingBar;

    public void ApplyTo(Palette palette)
    {
        palette.Foreground = Colors.Foreground;
        palette.Background = Colors.Background;
        palette.Cursor = Colors.CursorBg;
        palette.CursorText = Colors.CursorFg;
        palette.Selection = Colors.SelectionBg;
        palette.BoldIsBright = BoldBrightensAnsiColors;
        for (int i = 0; i < 8; i++)
        {
            palette[i] = Colors.Ansi[i];
            palette[i + 8] = Colors.Brights[i];
        }
    }
}

/// <summary>Result of loading a config file: the settings plus anything worth telling the user.</summary>
public sealed record LoadResult(Settings Settings, string? Path, IReadOnlyList<string> Diagnostics)
{
    /// <summary>
    /// The file isn't valid TOML, so <see cref="Settings"/> is only the defaults (plus overrides). A live reload keeps the
    /// settings it already has instead: a half-saved file mustn't reset every window.
    /// </summary>
    public bool SyntaxError { get; init; }

    /// <summary>Schemes loaded from color scheme files (selectable by name, listed in the palette).</summary>
    public IReadOnlyList<ColorScheme> CustomSchemes { get; init; } = [];
}

/// <summary>Extras applied on top of the config file: command-line overrides and color scheme files.</summary>
public sealed record LoadOptions
{
    /// <summary><c>--set key=value</c> overrides, merged over the file on every load.</summary>
    public IReadOnlyList<ConfigOverride> Overrides { get; init; } = [];
    /// <summary>Directories with wezterm-format scheme files (see <see cref="ColorSchemeFiles"/>); read by <see cref="SettingsLoader.Load"/>.</summary>
    public IReadOnlyList<string> SchemeDirectories { get; init; } = [];
    /// <summary>Already-loaded custom schemes, for <see cref="SettingsLoader.Parse"/>.</summary>
    public IReadOnlyList<ColorScheme> Schemes { get; init; } = [];
}
