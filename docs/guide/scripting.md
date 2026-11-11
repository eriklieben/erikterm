# Scripting

`erikterm cli` controls a running erikterm from a shell or script, like `wezterm cli`. erikterm also emulates tmux,
so tools that drive tmux, such as Claude Code's agent teams, open erikterm panes instead.

## erikterm cli

Inside a pane, `erikterm cli` acts on that pane and window (through `ERIKTERM_SOCKET` and `ERIKTERM_PANE`). Elsewhere
it uses the most recently focused window. `--pane-id ID` and `--window-id ID` pick others (ids come from `list`);
`--socket PATH` picks another erikterm.

```sh
erikterm cli list [--format json]                 # panes of every tab and window
erikterm cli split-pane --right --percent 30 -- htop   # prints the new pane id; --left/--top/--bottom, --cwd DIR
erikterm cli spawn --cwd ~/src                    # new tab (--window-id ID: in that window; --new-window: a window)
erikterm cli activate-window --window-id 3
erikterm cli send-text --no-paste $'make\r'       # or pipe stdin; a bracketed paste without --no-paste
erikterm cli get-text --start-line -100 [--escapes]
erikterm cli activate-pane-direction Left         # also get-pane-direction (prints an id, or nothing at an edge)
erikterm cli adjust-pane-size --amount 5 Right
erikterm cli zoom-pane --toggle                   # also kill-pane, activate-pane --pane-id 3
erikterm cli swap-pane [--with 4 | --previous]    # also rotate-panes [--counter-clockwise]
erikterm cli select-layout tiled                  # even-horizontal, even-vertical, main-vertical, main-horizontal, tiled, even
erikterm cli move-pane-to-new-tab --pane-id 3
erikterm cli activate-tab --tab-index -1          # or --tab-relative 1, --tab-id ID
erikterm cli set-tab-title logs                   # also set-window-title TITLE
erikterm cli set-tab-color --tab-id 2 red         # red, yellow, green, cyan, blue, magenta, #rrggbb, none
erikterm cli reload-config
erikterm cli agents [--format json]               # coding agents: state, time in state, cost, context, last message
erikterm cli set-agent-status --state waiting --message "needs a decision"   # for hooks (see Coding agents)
erikterm cli statusline                           # Claude Code's status line (see Coding agents)
erikterm cli new-worktree --agent claude --prompt "fix the parser"   # prints the new pane id
erikterm cli wait-for-state --pane-id 4 --state done,waiting --timeout 10m   # prints the state it reached
```

`erikterm cli --help` lists every flag. The exit status is 0 on success and 1 on error, with a message on stderr.

Anything that can reach erikterm's control socket can type into, read and start programs in every pane. Only your
user can reach it, which is no more than any program running as you can already do; a program in one pane can
control the others. Don't run code you don't trust as your user.

### new-worktree

Does what [New Agent Worktree](coding-agents.md#new-agent-worktree) does. It finds the repository from `--cwd`, else
the pane given by `--pane-id`, else the current directory. `--agent KEY` (`claude`, `copilot`, `codex` or a
`[worktree_agents]` key; without it you get a shell), `--prompt`, `--agent-args`, `--branch`, `--base` and `--tab` /
`--split` stand in for the form. `--background` leaves the focus where it is.

### wait-for-state

Blocks until the pane reaches one of the given states (comma-separated):

| State | |
|---|---|
| `idle` | at its prompt, including a finished turn you haven't seen |
| `working` / `waiting` | running a turn / needs you |
| `done` | a turn ended, also during the wait. Use it right after a launch, since an agent can look `idle` while starting. |
| `none` | no agent runs there |
| `exit` | the pane's program is gone (in a worktree pane, the shell after the agent) |

`--timeout` takes `30`, `30s`, `500ms`, `5m` or `1h`; `0` checks once; without it there's no limit. It exits 2 on
timeout and 3 when the pane is unknown or closed.

## tmux emulation

Every pane has `$TMUX` set and a `tmux` command on its `PATH` that opens erikterm panes and tabs instead. It supports
the commands tools use, such as `split-window`, `new-window`, `send-keys`, `capture-pane`, `list-panes`,
`display-message`, `select-pane`, `resize-pane`, `select-layout` and `kill-pane`. Unknown commands fail with tmux's
message, and `ERIKTERM_TMUX_LOG=/path/log` logs every call.

**Real tmux still works.** `tmux` on its own, `new-session`, `attach`, `ls`, `-L` / `-S`, and commands aimed at a
session you made run the real tmux, if installed. A pane running real tmux is left alone and gets the
`Ctrl+H/J/K/L` pass-through.

Turn the emulation off with `tmux_emulation = false`, or for one window's new panes with *Toggle tmux Emulation*.
Startup files that check `$TMUX` (to start tmux automatically, say) see it set.

**On Windows** the emulation works too: the `tmux` command is a `tmux.exe` link to erikterm in
`%LOCALAPPDATA%\erikterm`. A command line it runs in a new pane goes through `$SHELL` if set, else Git for Windows'
`sh.exe`, else `cmd /c`; commands written for a POSIX shell (Claude Code's, or any with `-e VAR=value`) need one of
the first two. There is no real tmux to hand over to.

### Claude Code agent teams

Since Claude Code 2.1 teammates run in-process and never open panes. To give each teammate a pane, set:

```toml
set_environment_variables = { CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS = "1" }
```

and `"teammateMode": "tmux"` (or `"auto"`) in `~/.claude/settings.json`, or run `claude --teammate-mode tmux`.

By default a teammate doesn't take a split. It keeps running out of sight and shows as a pill along the bottom of
the lead's pane: `● researcher · working`, colored by its state (red: waiting, yellow: working, blue: done, green:
idle). Click the pill to open it as a split next to the lead; click its ✕ to park it again. Claude Code doesn't notice
the difference.

- The palette has *Open teammate: name* and *Park teammate: name*. `ToggleAgentPane(n)` opens or parks the n-th
  teammate; `ToggleAgentPane` alone parks the focused one or opens the first. They are unbound by default.
- *Agents…* and notifications name the teammate (*Claude (tester) needs permission*).
- If the lead's pane closes or moves to another tab, its teammates become ordinary splits. Teammates aren't saved
  with the session.

```toml
tmux_agent_panes = "pills"   # "split": show teammates as tmux would
```

## Neovim: smart-splits.nvim across Neovim splits and erikterm panes

With smart-splits.nvim, the same keys move and resize across Neovim splits and erikterm panes, and `Ctrl+click`ed
`path:line` links open in that Neovim. Copy or symlink `contrib/nvim/erikterm.lua` to
`~/.config/nvim/lua/erikterm.lua`, then:

```lua
local erikterm = require("erikterm")
erikterm.setup() -- does nothing outside erikterm

-- smart-splits 1.x/2.x: erikterm acts as a multiplexer, like wezterm or tmux
require("smart-splits").setup({ multiplexer_integration = erikterm.multiplexer(), at_edge = "stop" })
-- (or only hand moves past the last split to erikterm: require("smart-splits").setup({ at_edge = erikterm.at_edge }))

-- smart-splits 3.x: require("smart-splits").setup({ mux = { backend = { erikterm.backend } } })
```

Keep your `<C-h>` → `move_cursor_left` and `<A-h>` → `resize_left` keymaps, and bind the same keys in erikterm,
which passes them through to Neovim while it runs:

```toml
[keys]
"CTRL+h" = "ActivatePaneDirection(Left)"
"ALT+h"  = "AdjustPaneSize(Left, 3)"
```

If `erikterm` isn't on `$PATH`, set `require("erikterm").cli = "/path/to/erikterm"`.
