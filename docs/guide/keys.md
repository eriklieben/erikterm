# Panes, menus and keys

Split a window into panes and tabs, and reach every action from the keyboard. If you forget a key, press
`Ctrl+Shift+P` for the command palette. On macOS the Cmd shortcuts work too: see [Platform notes](platforms.md#macos).

## Default keys

| Keys | Action |
|---|---|
| `Ctrl+Shift+P` | Command palette: search every action; themes preview live |
| right click | Context menu: copy, paste, split, zoom, close, … |
| `Ctrl+Shift+\` / `Ctrl+Shift+-` | Split right / down (wezterm's `Ctrl+Shift+Alt+%` / `"` also work) |
| `Ctrl+Shift+←↑→↓` | Focus the pane in that direction |
| `Ctrl+Shift+Alt+←↑→↓` | Move the divider |
| `Ctrl+Shift+Z` | Zoom / unzoom the pane |
| `Ctrl+Shift+O` | Pane picker: press the letter shown on a pane |
| `Ctrl+Shift+W` | Close the pane |
| `Ctrl+Shift+A` | Every pane running a coding agent, waiting first; `Enter` jumps there |
| `Ctrl+Shift+G` / `Ctrl+Shift+H` | New Agent Worktree / the repository's worktrees (see [Coding agents](coding-agents.md)) |
| `Ctrl+Shift+B` | Notification history of every tab; `T` narrows it to this tab |
| `Ctrl+Shift+Alt+I` | Broadcast input to all panes of the tab, and back |
| `Ctrl+Shift+C` / `Ctrl+Shift+V` | Copy / paste |
| `Shift+Insert`, middle click | Paste the primary selection |
| `Ctrl+Shift+Space` | Quick select: labels URLs, paths, hashes, IPs, UUIDs and numbers on screen. Type a label to copy, `Shift`+label to copy and paste, `Ctrl`+label to open the URL, file or commit. |
| `Shift+↑` / `Shift+↓` | Previous / next shell prompt (needs [shell integration](shell-integration.md); while a command runs, the key goes to the program) |
| `Shift+PgUp` / `PgDn` / `Home` / `End` | Scroll back |
| `Ctrl+Shift+F` | Search the scrollback: `Enter`/`↑` older, `Shift+Enter`/`↓` newer, `Alt+C` case, `Alt+R` regex, `Esc` closes |
| `Ctrl+Shift+S` | Search all panes of every tab and window; `Enter` goes to a match |
| `Ctrl+Shift+X` | [Vim mode](vim-mode.md) |
| `Ctrl+=` / `Ctrl+-` / `Ctrl+0` | Font size, per window |
| `Ctrl+Shift+T` | New tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | [Tab switcher](#tab-switcher), most recently used first |
| `Ctrl+PgDn` / `Ctrl+PgUp` | Next / previous tab in the bar |
| `Ctrl+Shift+1`…`8` / `Ctrl+Shift+9` | Tab 1–8 / the last tab |
| `Ctrl+Shift+PgUp` / `PgDn` | Move the tab left / right |
| `Ctrl+Shift+N` | New window |
| `Alt+Enter` / `F11` | Full screen |
| `Ctrl+,` | Settings |
| `Ctrl+Shift+R` | Reload the config |
| `Shift+Enter` | New line in Claude Code ([see below](#shiftenter-in-claude-code)) |

*Help › About erikterm* (palette or right-click menu) shows the version, renderer, fonts, session and third-party
licenses; `Ctrl+C` copies the diagnostics.

## Closing panes, tabs and windows

Closing a pane, tab or window where something other than a shell runs asks first, on the bar under the tab bar:
*Close this pane? nvim is still running*. That includes a coding agent, even an idle one. `Enter` or `y` closes, `Esc`
or `n` cancels. The window's close button asks once for all its tabs, and confirming still saves the session.

It doesn't ask for shells and tmux at their prompt, for `erikterm cli kill-pane` or the tmux emulation's `kill-pane`,
for a pane whose program already exited, for vim mode's `:q!` / `:tabclose!`, or on logout and SIGTERM.

```toml
window_close_confirmation = "AlwaysPrompt"   # "NeverPrompt": never ask
skip_close_confirmation_for_processes_named = ["bash", "sh", "zsh", "fish", "tmux", "nu", "cmd.exe", "pwsh.exe", "powershell.exe"]
```

Closing a tab's last pane closes the tab, a window's last tab closes the window, and the last window quits erikterm.

## When a program exits

| `exit_behavior` | |
|---|---|
| `"Close"` (default) | Close the pane. |
| `"Hold"` | Keep the pane and its output, with *make exited with code 2* `[Restart] [Close]` at the bottom. `Enter` restarts, `Esc` closes. |
| `"CloseOnCleanExit"` | Close on exit status 0, otherwise hold. A shell exits with its last command's status, so `exit` after a failed command holds. |

**Restart Pane** (palette, right-click menu, `RestartPane`) runs the pane's command again in the same pane, in the
directory its shell last reported (else where it started). If the program still runs, it asks first. Panes from the
tmux emulation, Claude Code's teammates, erikterm's own splits (*Git: Diff*, …) and `kill-pane` always close.

## Tabs

Click a tab to switch, middle-click to close, drag to reorder, double-click (or *Rename Tab…*) to rename. A dot on a
tab means something happened there; a new shell's first prompt and redraws after a resize don't count. When tabs
don't fit, the bar scrolls with the mouse wheel, and a *⌄ N more* button lists them all (with a dot when a hidden tab
has an agent waiting or something unread).

```toml
enable_tab_bar = true
hide_tab_bar_if_only_one_tab = true
```

### Tab switcher

Hold `Ctrl` and press `Tab` for a list of the window's tabs, most recently used first. Press `Tab` again to move down,
`Ctrl+Shift+Tab` to move up, and release `Ctrl` (or press `Enter`) to switch; `Esc` cancels. Typing filters the list.
A quick tap (under 150 ms) just flips to the previous tab.

Tapping `Ctrl` twice on its own opens the list and keeps it up until `Enter` or a click; tap twice again to close it.
A key in between cancels, so `Ctrl+C`, `Ctrl+C` never opens it. *Switch Tab…* (`ShowTabSwitcher`) does the same.

```toml
ctrl_tab_order = "recent"          # "position": Ctrl+Tab goes to the next tab in the bar, no list
tab_switcher_double_ctrl = true
```

### Tab colors and progress

*Set Tab Color…* (palette, or a tab's right-click menu) picks the theme's red, yellow, green, cyan, blue or magenta,
or *Reset*. The color follows the theme and is saved with the session. In `[keys]`: `SetTabColor(blue)`,
`SetTabColor(#ff8800)`, `SetTabColor(none)`; from a script: `erikterm cli set-tab-color [--tab-id ID] COLOR`. A tab
without a color shows the one its program asks for (iTerm2's `OSC 6`).

Programs that report progress (`OSC 9;4`) get a thin line along the bottom of their tab: red for an error, yellow when
paused, moving when the length is unknown. It goes away at the next shell prompt or when the program exits.

## Mouse and scrollbar

Drag to select; double-click selects a word, triple-click a line, and selecting copies to the primary selection.
Drag a divider to resize. The wheel scrolls the pane under the pointer (`smooth_scrolling = false` scrolls whole
lines). When a program uses the mouse (vim, htop, tmux), hold `Shift` to select anyway.

The scrollbar appears while you scroll or when the pointer is near a pane's right edge. Its ticks mark prompts,
failed commands (red), vim-mode marks and search matches; click one to go there.

## New panes start where you are

New splits, tabs and windows start in the focused pane's directory: the one the shell reported, else the running
program's, else `default_cwd`. On Windows, PowerShell's directory is only followed with
[shell integration](shell-integration.md).

## Broadcast input

*Broadcast Input to All Panes in Tab* (`Ctrl+Shift+Alt+I`, `ToggleBroadcastInput`) sends what you type or paste to
every pane of the tab. *Broadcast Input to Selected Panes…* (`ToggleBroadcastInput(Selected)`) lets you pick panes in
the pane picker first. Receiving panes get a rim and a *Broadcast · 3 panes ✕* pill: click it or press the key again
to stop.

Broadcast is per tab and not saved. Agent teammates, exited panes, mouse events and `erikterm cli send-text` are left
out.

## Rebinding keys

Bind keys in `[keys]` with wezterm's action names. *Show Key Bindings* in the palette shows the current map.

```toml
[keys]
"CTRL+h" = "ActivatePaneDirection(Left)"   # passed through to Neovim, tmux, or when there's no pane that way
"ALT+h"  = "AdjustPaneSize(Left, 3)"
"CTRL+SHIFT+x" = "None"                    # disable a default
"CTRL+ALT+1" = "ToggleAgentPane(1)"        # open / park Claude Code's first teammate
```

For the same keys across Neovim splits, see
[Scripting](scripting.md#neovim-smart-splitsnvim-across-neovim-splits-and-erikterm-panes).

### Shift+Enter in Claude Code

`Shift+Enter` (`SendAgentNewline`) always gives a new line in Claude Code, even when it hasn't turned on the kitty
keyboard protocol (an older version, a wrapper, a remote host). In other programs the key works as usual.

```toml
[keys]
"SHIFT+enter" = "None"                  # off
# "SHIFT+enter" = 'SendString(\x1b\r)'  # send ESC CR everywhere
```
