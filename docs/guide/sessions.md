# Sessions

erikterm saves your windows, tabs and panes and brings them back the next time it starts, so a restart doesn't cost
you your layout.

## What is restored

erikterm saves when it exits and shortly after the layout changes. The next start shows "Restored 3 tabs, 5 panes"
and brings back every window's tabs (order, active tab, custom titles), splits, zoomed and focused pane, size, full
screen and font zoom. Each pane gets a fresh shell in its directory, or the program it was running if that program is
in `restore_programs`.

```toml
restore_session = true                                     # --no-restore skips it once; -e never restores
restore_programs = ["nvim", "btop", "htop", "less", "man"]  # run again when they were in the foreground
restore_scrollback_lines = 0                               # > 0: also keep the last N lines of each pane
```

A restored program runs with its saved arguments, never typed into a shell, and your shell takes over when it exits.
Command lines are only recorded for programs in `restore_programs`. On Windows they are recorded but not run again.

## Where it is saved

The session is in `~/.local/state/erikterm/session.json` (on Windows `%LOCALAPPDATA%\erikterm`), readable only by
you. A window started with another `--class` keeps its own `session-<class>.json`.

- A window you close is dropped from the session; quitting saves what's left.
- Exiting the last shell of the last window, rather than closing it, forgets the session.
- New windows (`Ctrl+Shift+N`) start fresh, and windows started with `-e` aren't saved.
- If two separate processes run, the one that saves last wins.
- The palette has *Save Session Now* and *Forget Saved Session* (which also stops this process from saving).

## Resuming coding agents

A pane running a coding agent also records the agent's session, once its status line has reported one (see
`erikterm cli statusline` in [Coding agents](coding-agents.md)). After a restart, the window shows a *Resume N agents*
pill.

**Nothing resumes by itself.** Click the pill to start each agent with its resume command (`claude --resume ID`,
`codex resume ID`, …) in its pane; your shell comes back when it exits. ✕ dismisses the offer. Panes whose directory
is gone, or that run something else by then, are skipped. The agent must be on erikterm's `PATH` (on Windows, a real
`.exe`, not an npm `.cmd` shim). Aider has no session to resume.
