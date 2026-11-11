# erikterm documentation

New to erikterm? Start with the [README](../README.md) to install it, then [Panes, menus and keys](guide/keys.md).

## Using erikterm

- [Platform notes](guide/platforms.md): Linux, macOS and Windows specifics.
- [Panes, menus and keys](guide/keys.md): every default key, panes, tabs and menus.
- [Vim mode](guide/vim-mode.md): move, select, search and copy through the scrollback with vim keys.
- [Multiple windows](guide/windows.md): several windows in one process, and moving tabs between them.
- [Sessions](guide/sessions.md): restoring windows, tabs, panes and agents after a restart.
- [Links and shell integration](guide/shell-integration.md): prompt marks, hyperlinks, quick select, file links and snippets.
- [Developer tools](guide/developer-tools.md): git and GitHub status, Neovim, dev containers, drag and drop.
- [Compare and merge](guide/compare-and-merge.md): side-by-side review and conflict resolution.
- [Coding agents](guide/coding-agents.md): agent status, permission prompts, worktrees and resuming.
- [Notifications](guide/notifications.md): info bars, the notification panel and desktop notifications.
- [Images](guide/images.md): kitty graphics, iTerm2 and sixel images.
- [Terminal type and terminfo](guide/terminfo.md): TERM and the terminfo entry.
- [Configuration](guide/configuration.md): the config file and its options.
- [Scripting](guide/scripting.md): `erikterm cli`, the tmux emulation and Neovim splits.
- [Compatibility](guide/compatibility.md): the escape sequences and protocols erikterm supports.
- [Accessibility](guide/accessibility.md): contrast, text size, motion and the cursor; no screen-reader support yet.
- [FAQ](faq.md): ssh and TERM, how erikterm compares, where settings live, resetting, telemetry (none).

## Working on erikterm

- [Developing erikterm](development/README.md): architecture, building and debugging.
- [Testing and benchmarking](../bench/README.md): the test suite, benchmarks and the Windows and macOS test plan.
- [Code analysis](development/code-analysis.md): CodeQL, OpenSSF Scorecard, SonarQube Cloud and dependency updates.
- [The Windows console host](development/windows-console-host.md): why erikterm ships its own ConPTY, and what it taught us about performance.
