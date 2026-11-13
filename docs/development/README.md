# Developing erikterm

How erikterm is put together, and how to build and debug it. To use erikterm, see the [guide](../README.md).

## Architecture

A GPU-rendered terminal emulator in C#, compiled with Native AOT to a single ~12 MB binary.

```
keyboard ─► KeyEncoder ─► PTY master ─► shell
                              │
window ◄─ TerminalView ◄─ Terminal ◄─ VtParser
 (SDL3)    (glyph atlas)   (grid/modes)  (DEC state machine)
```

| Project | What it is | Dependencies |
|---|---|---|
| `src/ErikTerm.Core` | VT parser, screen model, scrollback, palette | none |
| `src/ErikTerm.Config` | TOML reader, settings, color schemes | none |
| `src/ErikTerm.Pty` | PTY: Linux via `posix_openpt` + `posix_spawn`, Windows via ConPTY; what runs in a pane (`/proc`, libproc, Toolhelp + PEB) | libc / kernel32, ntdll |
| `src/ErikTerm.Ui` | Split layout tree, fuzzy matcher, animation easing (no rendering deps) | none |
| `src/ErikTerm.App` | Window, input, rendering → `erikterm` | SDL3, FreeType, HarfBuzz (system libs via P/Invoke) |
| `bench/ErikTerm.Bench` | Throughput benchmark → `erikterm-bench` | none |
| `tests/ErikTerm.Tests` | Tests (parser, terminal, PTY, app logic; see [Tests](../../bench/README.md#tests)) | TUnit (test-only) |

Shipped code uses **no NuGet packages**. Native libraries are loaded from the system:
SDL3 (zlib license), FreeType (FTL/GPLv2) and HarfBuzz (MIT; optional — without it there are no
ligatures), plus the `fc-match` / `fc-list` CLIs for font discovery (fallback fonts for code points the chain doesn't cover
are looked up off the UI thread; such a cell shows a .notdef box until its font is found).

## Build & run

```sh
scripts/run.sh                 # AOT-publish and launch $SHELL
scripts/run.sh -e btop         # run a specific program
dotnet test --project tests/ErikTerm.Tests   # see Tests below
scripts/check.sh               # what CI's test, code quality, release scripts and integration jobs run (Windows: scripts\check.ps1)
scripts/integration-tests.sh   # the off-screen integration scenarios alone (see Testing)
```

Install a local build for your user (Linux): `scripts/install-desktop.sh` links `~/.local/bin/erikterm` to `artifacts/app/erikterm` (publishing it first if needed) and installs the icons and desktop entry; `--prefix DIR` installs elsewhere and `--uninstall` removes it. `scripts/render-icons.sh` regenerates the PNGs and the `.ico` from `assets/icon/`.

## Debugging

Debug: `ERIKTERM_SCREENSHOT=/tmp/f.bmp ERIKTERM_SCREENSHOT_DELAY=1500` saves a rendered frame (`/tmp/f-{window}.bmp`:
one per window, each that long after it opened; without `{window}` only the first window);
`ERIKTERM_DEBUG_SCRIPT="action:QuickSelect;wait;key:a"` (or `ERIKTERM_DEBUG_SCRIPT_FILE`, one step per line) runs steps
every 350 ms (also `mouse:COL,ROW`, `ctrl:1`, `rclick:COL,ROW`, `gutter:ROW`, `scroll:N`, `feed:TEXT` with C# escapes
and `\x3b` for `;`, `down:X,Y` / `drag:X,Y` / `up:X,Y` with the button held between them, `presstab:N`, `menutab:N`;
`dragover:COL,ROW`, `drop:COL,ROW:/a.png|/b c.txt`, `dragleave`, `cellclick:COL,ROW` and `ghfake:pending|pass|fail` for
the developer-tool integrations; `ckey:x` / `cskey:x` for Ctrl+x / Ctrl+Shift+x; `vim:KEYS` feeds vim mode keys in vim
notation, entering it first, e.g. `vim:5k<C-v>jjll` or `vim::theme gruv`; `throw:N` makes the next N frames fail, to
exercise the internal-error handling; `exit:N` hangs up the focused pane's program as if it had exited with status N,
for `exit_behavior`; `infobar:N` presses the N-th button of the bar under the tab bar; `ckey:tab` is Ctrl+Tab with Ctrl
staying held, `ctrl-up` releases it and `ctrl-tap` is both at once (a quick tap), for the tab switcher; `clickmore` clicks the tab bar's "more" button, `wheelbar:N`
scrolls an overflowing bar).
Steps go to the newest window; window steps: `window:ID`, `new-window`, `close-window` (like the compositor's close:
it may ask first, answer with `key:enter` / `key:escape`), `move-tab:ID` (0 = a new window),
`drop-on:ID,X,Y` (the next tab dropped outside its window lands there), `shot:PATH` (`{window}`: every window), `sleep:MS`,
`wait-file:PATH` (the steps pause until PATH exists, at most 2 minutes: the integration scenarios check the window
there, then create the file; see [Integration scenarios](../../bench/README.md#integration-scenarios)).
Point `XDG_STATE_HOME` at a temporary directory when testing session restore.
Internal errors are written with their stack trace to `$XDG_STATE_HOME/erikterm/crash-<time>.log` (newest 20 kept;
About shows the latest). An error in one window's event, tick or frame drops that work and shows *Internal error
(logged)* instead of ending the process. Input to a program (keys, pastes, `cli send-text`, terminal replies) is
queued per pane and written by a thread of its own, so a program that stops reading can't freeze the windows; at most
16 MB of input and 64 KB of unanswered replies wait, beyond that they are dropped.
`ERIKTERM_LOG_UNHANDLED=1` (stderr) or `=/path/log` (appended) logs every escape sequence erikterm ignores — CSI
(incl. unknown DEC/ANSI modes), OSC, DCS, APC, ESC — like `ERIKTERM_TMUX_LOG`:
`2026-11-07T22:14:20.123 [4242] unhandled CSI ?1015h`. Each distinct sequence once, at most 20 lines a second; off, it
costs nothing (only the ignore paths look at it). `erikterm --version` (also `erikterm cli --version`) prints the
version, commit and build date.
`ERIKTERM_FRAME_STATS=1` prints frame build time (avg/p95/max), quads, rows rebuilt vs. replayed from the row
cache and allocations per frame to stderr every 3 s (`0 frames (idle)` when nothing was drawn).

## How things work inside

Details the user guide leaves out, for when you change these parts.

**Window features.** A window's features are classes it holds, not parts of `TermWindow`: git status, GitHub, the
merge/rebase bar, links and quick select, the editor, drag and drop, OSC 52, recent commands, snippets, dev containers,
IPC and the tmux requests live in `src/ErikTerm.App/WindowFeatures/`, next to `Agents/`, `Teammates/`, `Worktrees/` and
`Notifications/`. Each takes small interfaces for what it needs from the window (`IFeatureWindow`, `IPaneLauncher`,
`IPaneInput`, `IPointerWindow`), which `TermWindow` implements at the end of `TermWindow.cs`; per-frame hooks get their
inputs as arguments instead of calling back. Background work goes through `BackgroundJobs`, and git status, GitHub and
the links share the per-pane `PaneTracks`.

**Rendering.** Rows are rebuilt only when they change, each visible row's quads are cached, and an idle window draws
nothing. Progress animations redraw only while they are on screen in a focused window. `minimum_contrast` is worked
out once per foreground and background pair while a row is built. The Unicode width tables are generated by
`scripts/gen-unicode-tables.py`.

**Screen readers (Windows).** Two layers. A platform-neutral model in `src/ErikTerm.Core/Accessibility/`:
`TerminalRows` decodes rows into characters (wide characters and grapheme clusters count as one) under the terminal
lock, one row per lock, cached by row version. `TextNavigator` does character, word, line (a row) and paragraph (a
logical line) navigation over absolute line numbers, so ranges stay on their text while output scrolls.
`OutputTracker` diffs a terminal against what it saw last. `OutputAnnouncer` in `src/ErikTerm.Ui/Accessibility/`
decides what gets spoken: 100 ms batches, typed echo dropped, floods cut to 20 lines, nothing on the alternate screen.
All of it is unit-tested on Linux. On top of that sits a thin UI Automation layer in `src/ErikTerm.App/Accessibility/`.
COM interfaces are declared for the COM source generator (`[GeneratedComInterface]` / `[GeneratedComClass]`, no
reflection) and interface pointers cross as `nint`. The window is subclassed to answer `WM_GETOBJECT`, because SDL's
message hook can't return a value. `WindowAccessibility` publishes an immutable view of the panes for UI
Automation's threads, raises focus, text and caret events and notifications from the main thread, and runs Select and
ScrollIntoView on the main thread. Nothing is built before the first `WM_GETOBJECT`, and nothing runs per frame
while `UiaClientsAreListening` is false. The manual test plan is `bench/testing/screen-readers.md`.

**The VT core.** `Terminal` (ErikTerm.Core) keeps the grid, the cursor and the pen, and everything the parser calls
per byte or per cell: printing, controls, ESC/CSI dispatch, cursor movement, scrolling, editing and SGR stay direct
calls on its own fields (`VtParser<Sink>` is generic over a struct, so AOT emits direct calls too). The colder parts
are classes it holds: `SynchronizedOutputGate` (DEC 2026), `ScreenResizer` (resize and reflow), `ScrollbackCompactor`,
`InputModes`, `TerminalReplies`, `ColorReports`, `Hyperlinks`, `SemanticPrompts` (OSC 133), `ProgramSignals` (OSC
7501 / 9 / 777 / 99), `UserVariables`, `ProgramTabColor`, and for images `KittyGraphics`, `CellImages` (sixel, iTerm2)
and `PlacementTracker`; they reach back through small interfaces (`IImageGrid`, `ITerminalLines`, …). `Row` keeps
its cells and marks, holds its clusters by value (`RowClusters`) and keeps its underline colors in two fields that
`RowUnderlineColors` works on, so a row is still one object of the same size; `PackedCells` packs a scrollback row at
rest. `Row.Clear` runs once per scrolled line: keep it small (the rare cluster clearing is out of line on purpose).

**What runs in a pane.** On Linux this comes from `/proc`. On macOS it comes from libproc and `sysctl`, and pane
programs start through the internal `erikterm --erikterm-pty-exec` step, because `posix_spawn` can't make the new
terminal the controlling terminal; Homebrew libraries are looked up in `/opt/homebrew/lib` and `/usr/local/lib`. On
Windows a console has no foreground process group, so one Toolhelp snapshot, shared by every pane and taken at most
four times a second, is walked down from the shell to the first program that isn't a shell. It leaves out
conhost/OpenConsole, GUI programs and processes younger than 250 ms, and reads arguments and the directory from the
process's PEB.

**Shell integration.** Each shell is set up without editing its dotfiles: `ZDOTDIR` for zsh, `bash --posix` with
`ENV`, a `vendor_conf.d` directory on `XDG_DATA_DIRS` for fish, and `-NoExit -Command` for PowerShell. The scripts are
written to `$XDG_STATE_HOME/erikterm/shell-integration/<hash>` (mode 0700). Bash reads the command line from history
in `PS0` and uses no DEBUG trap; PowerShell uses PSReadLine's `AddToHistoryHandler`. pwsh's `-Login`, `-NoLogo` and
`-Interactive` count as a plain start.

**`erikterm ssh`.** Hosts that already have the terminfo entry are recorded in `$XDG_STATE_HOME/erikterm/ssh-terminfo`,
keyed by the output of `ssh -G`. The install runs over a separate connection without a tty or forwarding.

**Terminfo.** `share/terminfo/erikterm.terminfo` is generated from `src/ErikTerm.Core/TermCapabilities.cs`, the same
table that answers XTGETTCAP, and a test keeps the two in step.

**Images.** Each terminal keeps at most 320 MB of decoded images, dropping the least recently used first. Animation
frames are timed redraws. Claude Code detects image support through XTVERSION.

**Notifications.** Desktop notification bodies are markup-escaped. A pane sends at most one desktop notification every
8 seconds and erikterm at most one a second, and text that comes from programs is escaped and length-capped.

**History and session files.** `history.json` keeps 200 commands and 100 directories, merged between processes, and
drops lines over 1 KiB. `session.json` is written atomically, holds a `windows` array, and records at most 256
arguments or 32 KiB per program line. A file from an incompatible version is ignored.

**Control socket.** `$XDG_RUNTIME_DIR/erikterm/erikterm-<pid>.sock`, mode 0600 in a 0700 directory that must be owned
by you and must not be a symlink; on macOS `$TMPDIR/erikterm`, or `/tmp/erikterm-<uid>` when that path would be too
long for a socket. A `last-focused` marker picks the instance a new launch goes to. The protocol is one JSON object per
line, for example `{"command":"split-pane",...}` answered by `{"ok":true,"pane_id":4}`.

**tmux emulation.** Panes get `TMUX=<socket>,<pid>,0` and `TMUX_PANE=%<id>`, and a `shim-<pid>` directory with a `tmux`
link to `erikterm tmux-shim` goes first on their `PATH`. Claude Code's teammates are recognised by `split-window … --
cat` together with `AI_AGENT` or `CLAUDE_CODE_ENTRYPOINT`, followed by `respawn-pane -k` with `--agent-id`,
`--agent-name`, `--team-name` and `--agent-color`. `src/ErikTerm.App/Tmux/` lists the emulated commands, flags and
`#{…}` formats.

**Agent detection.** Signals are checked in order: an agent's own OSC 7501 status (probed with `OSC 7501 ; ?`, with
base64 payloads filtered out of messages), then the screen rules in `AgentCatalog.cs`, which run every 400 ms on panes
whose content changed. Built-in prompt arguments: `-- PROMPT` for claude and codex, `--interactive=PROMPT` for copilot.

**Git and GitHub.** `git status --porcelain=v2` runs at most every 2 seconds (10 seconds for the focused pane), with
`GIT_OPTIONAL_LOCKS=0` and a 1.5-second cutoff. `gh` refreshes every 3 minutes, every minute while checks run, and
backs off on errors and rate limits. Review uses `git diff <base>...HEAD -M` and `git merge-tree`. Finish and Clean Up
use `merge --squash`, `git cherry` and `merge-tree --write-tree`, and run `branch -D` only after a fresh check that the
branch has landed. The theme catalogue is a shallow HTTPS clone with hooks, redirects and symlinks off, size caps, and
validation of every file.

**Window icon.** X11 gets it through `_NET_WM_ICON`, Wayland only with xdg-toplevel-icon, and Windows from the exe's
icon resource.

## Tests and benchmarks

How to run the test suite, the benchmarks and the cross-platform test plan is in [bench/README.md](../../bench/README.md).
CodeQL, OpenSSF Scorecard, SonarQube Cloud and Dependabot are in [Code analysis](code-analysis.md); the fuzz targets,
the weekly fuzzing and how to run it locally in [Fuzzing](fuzzing.md).

## Commit messages and releases

Releases are made by [semantic-release](https://github.com/semantic-release/semantic-release) from the commit messages
on `main`: it works out the next version, writes the release notes and publishes the downloads and packages
([docs/releasing.md](../releasing.md)). For that, commit messages follow the
[Angular commit format](https://github.com/angular/angular/blob/main/contributing-docs/commit-message-guidelines.md):

```text
<type>(<scope>): <subject>          ← the header: max. 100 characters, imperative, lower case, no period

<body: what and why, optional>

<footer: BREAKING CHANGE: ..., Fixes #123, optional>
```

The header is the line in the release notes, so write it for users: `fix(pty): keep the exit code of a killed shell`,
not `fix: PtyWriter null check`. Commits not in this format never release anything.

| Type | Use for | Release |
| --- | --- | --- |
| `feat` | A new feature or setting users can see. | minor |
| `fix` | A bug fix users can notice. | patch |
| `perf` | Faster or lighter. | patch |
| `revert` | Reverting an earlier commit (`revert: feat(tabs): ...`). | patch |
| `docs` | Documentation only. | none |
| `refactor` | A code change that neither fixes a bug nor adds a feature. | none |
| `test` | Adding or fixing tests. | none |
| `build` | Build system, packaging, tooling dependencies. | none |
| `ci` | GitHub Actions workflows. | none |
| `style` | Formatting, no code change. | none |
| `chore` | Anything else users don't see. | none |

Scopes are optional; when you use one, pick from (another one is a warning, not an error; the list is in
[`commitlint.config.mjs`](../../commitlint.config.mjs)):

| Area | Scopes |
| --- | --- |
| The terminal | `vt` (parser, modes, sequences), `render` (drawing, glyph atlas), `font`, `input` (keys, mouse, IME), `pty`, `shell` (shell integration), `images` |
| The window | `tabs`, `panes`, `search`, `vim` (vim mode), `settings` (the settings page), `config` (`erikterm.toml`, key bindings), `themes`, `session` (restore, history), `notifications` |
| Developer tools | `agents`, `git`, `github`, `review`, `worktrees`, `tmux` (the tmux emulation), `ssh`, `cli` (command line, `erikterm cli`), `ipc` (control socket) |
| Platforms | `linux`, `macos`, `windows` |
| The repository | `docs`, `ci`, `deps`, `deps-dev`, `release`, `packaging`, `bench`, `tests` |

A **breaking change** (a setting, key binding, command-line option or file format that no longer works the way it
did) adds a `!` after the type or scope and/or a `BREAKING CHANGE:` footer that says what to do:

```text
feat(config)!: rename tab_bar to tabs

BREAKING CHANGE: the tab_bar setting is now tabs; rename it in erikterm.toml.
```

It releases the next major version (1.3.0 → 2.0.0), whatever its type.

**Pull requests** are squash merged with their title as the commit message, so the title is what has to follow the
format (the *Commit messages* workflow checks it; the commits inside are only checked as a warning). Commits pushed
straight to `main` are checked by the local hook only.

**Local check.** `pnpm install` in the repository root installs the release tooling and a `commit-msg` git hook that
runs commitlint on every commit (skip it once with `git commit --no-verify`; don't install it:
`SKIP_INSTALL_SIMPLE_GIT_HOOKS=1 pnpm install`). The hook goes into the repository's shared hooks folder, so it also
checks commits made in its worktrees. By hand: `echo "feat(tabs): reopen a closed tab" | pnpm exec commitlint`.

**Cutting a release.** Nothing is released by a push alone. After pushing straight to `main`: **Actions → Release →
Run workflow** with *Dry run* unticked (ticked, it only shows the next version and its notes). For a pull request: the
label `cut-release` (or `security`) on it releases once CI on `main` passed. Details and versions:
[docs/releasing.md](../releasing.md).
