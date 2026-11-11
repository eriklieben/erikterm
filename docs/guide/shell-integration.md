# Links and shell integration

Shell integration tells erikterm where each prompt, command and its output are, so you can jump between prompts, copy
a command's output, and get notified when a long command ends. This page also covers links, the clipboard, pasting,
recent commands and snippets.

## Shell integration

It works out of the box for zsh, bash, fish and PowerShell. When erikterm starts your default shell, or a
`default_prog` naming one of them, it loads the integration itself. Nothing is installed and your dotfiles aren't
changed.

```toml
shell_integration = "auto"   # "off": leave the shell alone (you can still source the scripts by hand)
```

With it you can:

- jump between prompts with `Shift+↑` / `Shift+↓` (`ScrollToPrompt(-1|1)`)
- see failed commands as a red bar in the left margin
- hover the margin to outline a command's output, and click to select it
- use *Select Command Output* / *Copy Command Output* (palette, context menu) on the command under the cursor or the
  last one that printed something
- be [notified](notifications.md) when a command that ran longer than `notify_command_duration` finishes out of sight
- run recent commands again and go to recent directories from the palette (see below)

It only applies to an interactive shell erikterm starts itself, not to `-e`, agent panes, or a shell given `-c`, a
script, `--norc`, `--rcfile`, or PowerShell's `-NoProfile`, `-Command` or `-File`. On Windows only PowerShell is
supported, not cmd. Your prompt (oh-my-posh, starship or your own) keeps working, and if it already sends the marks
itself (oh-my-posh with `shell_integration = true`, fish 4), nothing is marked twice.

### Sourcing the scripts by hand

For a shell erikterm doesn't start, source the script **after** your prompt setup (for example after
`eval "$(starship init zsh)"`). In `~/.zshrc`:

```sh
[[ $TERM_PROGRAM == erikterm ]] && source ~/path/to/erikterm/contrib/shell/erikterm.zsh
```

At the end of PowerShell's `$PROFILE`:

```powershell
if ($env:TERM_PROGRAM -eq 'erikterm') { . /path/to/contrib/shell/erikterm.ps1 }
```

`contrib/shell/erikterm.bash` (in `~/.bashrc`) and `erikterm.fish` work the same way. The scripts do nothing inside
tmux, and bash-preexec keeps working.

To use other keys for prompts, rebind them, for example `"CTRL+SHIFT+k" = "ScrollToPrompt(-1)"`.

## Recent commands and directories

With shell integration, the commands you ran and the directories you visited, in every pane and window, show in the
palette, most recent first. More are under *Recent Commands…* and *Recent Directories…*.

| Palette entry | `Enter` | With a modifier |
|---|---|---|
| *Run again › cmd* | types the command into the focused pane without running it | `Shift+Enter`: runs it too |
| *Go to › dir* | runs `cd` to it in the pane's shell | `Alt+Enter`: opens a new tab there |

*Go to* opens a new tab when something other than a shell runs in the pane. What your shell keeps out of its
history (such as a command typed with a leading space) stays out of this list too, and multi-line commands aren't
kept. *Clear Recent Commands and Directories* deletes the list.

```toml
remember_commands = true   # false: record, show and save nothing
```

## Links

Hovering a URL underlines it. Hold `Ctrl` and click to open it with `xdg-open` (`open` on macOS, `explorer` on
Windows). Only http(s), ftp, file, mailto and ssh links open. Right-click a link for *Open Link* and *Copy Link
Address*. In programs that use the mouse (vim, tmux), use `Ctrl+Shift+click`. Links a program prints with OSC 8 show
their target at the bottom of the pane.

For file paths, commit hashes and issue numbers, see [Developer tools](developer-tools.md).

## Clipboard from programs

Programs can set the clipboard or primary selection (OSC 52), which is handy for Neovim or tmux over SSH. Anything
printed in a pane can try this, so by default erikterm asks first on the bar under the tab bar:
*nvim wants to set the clipboard (42 characters)* with **Allow**, **Deny** and **Always for this pane**. An
unanswered request goes away after 10 seconds. Programs can never read the clipboard.

```toml
osc52 = "ask"   # "allow": always, "deny": never
```

The older `enable_osc52 = true` / `false` still works and means `"allow"` / `"deny"`.

## Pasting

Pasted text can't press keys for you: escape sequences and control characters other than Tab and Enter are removed
from every paste (including drag and drop, quick select and `erikterm cli send-text`), and a toast says so.

```toml
paste_sanitize = true   # false: don't clean pastes into programs that didn't ask for bracketed paste
```

## Snippets

Snippets are named pieces of text from your config. They show in the palette as *Snippet › name*, and you can bind
them with `SendSnippet(name)`.

```toml
[snippets]
gs = "git status"
deploy = { text = "make deploy DIR={{cwd}}", description = "Deploy from here" }

# or as an array of tables:
# [[snippets]]
# name = "gs"
# text = "git status"
# description = "optional"
```

`{{cwd}}` is the pane's directory and `{{selection}}` the selected text, inserted as they are, without quoting.
Write `{{{{` for a literal `{{`. A snippet is pasted into the focused pane; `Shift+Enter` in the palette also presses
Enter.
