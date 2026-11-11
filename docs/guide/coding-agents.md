# Coding agents

erikterm shows what each coding agent in your panes is doing, so you can see which one needs you. It can also start
agents in their own git worktrees and help you merge their work.

## Agent status

Claude Code, Codex, Gemini CLI, OpenCode, Amp, Aider, Cursor's agent, Copilot CLI and Droid are recognised in any
pane:

| | State | Meaning |
|---|---|---|
| red | **waiting** | needs you: a permission prompt, a question, a login |
| yellow, breathing | **working** | running a turn |
| blue | **done** | finished while you were elsewhere, until you focus the pane |
| empty outline | **idle** | at its prompt, seen |

The state shows as a dot on the tab and a pill in the pane's corner (*Claude · needs permission  $0.42 · 38% ctx*).
A waiting pane also gets a red rim. Tabs are named after the agent and its task (*Claude: Fix the parser*).
`Ctrl+Shift+A` (*Agents…*) lists every agent pane, waiting first, with its directory, last message and cost; `Enter`
jumps there.

When an agent starts waiting or finishes out of sight, it shows in the [notification bar](notifications.md) and, if
you allowed it and no erikterm window has focus, on the desktop.

```toml
agent_status = true          # false: no agent detection
agent_notifications = true   # false: agents never notify the desktop (they still show in the notification bar)
agent_urgency = true         # flash the taskbar entry while an agent waits
```

Claude Code 2.1+ and Codex need no setup. For others, and inside tmux, see [How erikterm knows](#how-erikterm-knows).

### Cost and context

To show the model, cost and context use, make `erikterm cli statusline` Claude Code's status line in
`~/.claude/settings.json`. It also prints a compact line (*Opus 5.5 · $0.42 · 38% ctx*) for your status bar:

```json
{ "statusLine": { "type": "command", "command": "erikterm cli statusline" } }
```

To keep your own script, use `erikterm cli statusline --passthrough 'bash ~/.claude/statusline-command.sh'`. Outside
erikterm the command just prints.

## Sending text to an agent

**Send to Agent…** (palette, `SendToAgent`, or *Send Selection to Agent…* in the context menu) pastes the selection,
or a prompt you type, into an agent in any window. `Enter` jumps there so you can finish it; `Ctrl+Enter` also
presses Enter and keeps you where you are.

## New Agent Worktree

`Ctrl+Shift+G` (*New Agent Worktree…*) starts an agent on a new branch in a new git worktree of the focused pane's
repository.

| Field | |
|---|---|
| **Prompt** | What to do. Empty just opens the agent. |
| **Agent** | Claude Code, GitHub Copilot, Codex, or a `[worktree_agents.NAME]`, if on `PATH`. `Tab` cycles. |
| **Arguments** | Extra arguments for this launch (`--model "x y"`). The label turns yellow with a warning when they bypass permission checks. |
| **Branch** | Made from the prompt, or a name like `floating-boat-lift-a23fd`. |
| **Base** | The default branch. Not fetched first. |

`Enter` creates it and switches to it; `Ctrl+Enter` creates it in the background and keeps the form open;
`Alt+Enter` opens it in a split instead of a tab (or the other way round); `Ctrl+Alt+Enter` does both; `↑` / `↓`
move between fields; `Esc` cancels. The form shows the exact command first, and git's message if it fails. An
existing directory or branch stops it.

The new tab gets a color of its own and runs `git_worktree_setup` (if set), then the agent, then your shell. If the
setup fails, the agent doesn't start. The prompt is for that one launch: *Restart Pane* and a restored session give
you a shell in the worktree. On Windows, agents installed as npm `.cmd` shims aren't offered.

```toml
git_worktree_dir = "../{repo}.worktrees/{name}"   # relative to the main worktree; ~/ works
git_worktree_setup = "pnpm install"               # or a table per repository name, "*" for the rest
worktree_open_in = "tab"                          # or "split"

[worktree_agents.claude]
args = ["--model", "sonnet"]                      # fills the Arguments field

[worktree_agents.aider]                           # your own agent: name, command, args, prompt_args
command = "aider"
prompt_args = ["--message", "{prompt}"]           # how it takes a first prompt; [] when it can't
```

Flags aren't translated between agents; a wrong one fails visibly in the pane.

## Worktrees

`Ctrl+Shift+H` (*Git: Worktrees…*) lists the repository's worktrees with their branch, changes, ahead/behind, PR and
CI state, and agent.

| Key | |
|---|---|
| `Enter` | go to a tab in that worktree, or open one |
| `n` / `f` / `c` | New Agent Worktree / Finish / Clean up |
| `d` | open the Aspire dashboard |
| `r` | refresh |

**Ports.** Programs started in a worktree get `ERIKTERM_PORT_BASE` and `ERIKTERM_PORT_COUNT`: 20 ports between 23000
and 31999 that belong to that worktree, across restarts. They are set when the program starts; `cd` doesn't change
them.

**Aspire.** A running AppHost shows as an `aspire :PORT` pill in its row. To run AppHosts from several worktrees at
once, use `aspire run --isolated` (which also separates user secrets). A port a host process binds itself should come
from `ERIKTERM_PORT_BASE`.

## Finishing and cleaning up

**Finish Worktree** (*Git: Finish Worktree…*) shows the commits and files that would land on the default branch, and
whether they would conflict (needs git 2.38). Pick **Squash merge** (default), **Merge**, **Rebase** or **Open PR**
with `←` / `→`. For a squash, `Tab` edits the message and `Ctrl+Enter` runs it. The message is filled from the branch's
commits, without co-author lines naming a coding agent. If the main worktree is dirty, on another branch or
mid-merge, it stops and says why. Afterwards it offers to remove the worktree and branch.

**Clean Up** (*Git: Clean Up Worktrees…*) lists worktrees whose work has landed, including squash merges and merged
PRs. Worktrees with uncommitted changes, a running agent or an open tab are kept.

**Nothing runs without a confirmation** showing the exact commands. They run in a visible split, so hooks and signing
work, and a failed step offers **Retry**. Nothing is forced, except deleting a squash-merged branch, which is checked
again first. An agent still running in the worktree stops it.

While a repository is mid-merge, rebase, cherry-pick or revert, the bar under the tab bar says so
(*Rebasing 3/7 · 2 conflicts*) with **Next conflict**, **Continue** and **Abort**. See also
[Compare and merge](compare-and-merge.md).

```toml
git_conflict_check = true   # warn on a tab when the branch would conflict with the default branch
```

## How erikterm knows

1. **Program status:** Claude Code 2.1+ and Codex report their state directly.
2. **Hooks**, where that can't arrive (for example inside tmux): merge
   [`contrib/claude/hooks.json`](../../contrib/claude/hooks.json) into `~/.claude/settings.json`. It runs
   `erikterm cli set-agent-status --state waiting|working|done|idle|clear [--message TEXT] [--kind permission|question|auth] [--hook-input]`,
   which sets the state of its own pane and does nothing outside erikterm.
3. **Notifications:** an agent's notification or bell means "waiting". For Claude Code, pick *kitty* or *iTerm2* as
   the notification channel in `/config`.
4. **The screen:** each agent's own prompts and spinners.

Agent states aren't saved with the session.
