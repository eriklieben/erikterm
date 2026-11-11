# Frequently asked questions

Short answers, with links to the page that has the details.

## Programs on a remote host complain about an unknown terminal

erikterm sets `TERM=xterm-erikterm` when that terminfo entry is installed on your machine
(`scripts/install-terminfo.sh`). A host you ssh into doesn't have it yet. Use `erikterm ssh` instead of `ssh`: it
installs the entry on the host the first time, then runs ssh as usual. Or install it by hand:

```sh
erikterm --terminfo | ssh HOST 'mkdir -p ~/.terminfo && tic -x -o ~/.terminfo /dev/stdin'
```

Or skip the entry and use `term = "xterm-256color"` in `erikterm.toml`. Programs then fall back to plain underlines
and a few other basics. See [Terminal type and terminfo](guide/terminfo.md).

## Why C#, and why Native AOT?

C# is the language I work in. Native AOT compiles it ahead of time into one native binary of
about 12 MB: no .NET runtime to install, a fast start, and no JIT warming up while you work. The shipped binary uses
no NuGet packages. SDL3, FreeType and HarfBuzz come from your system, or with the Windows download.

## How does it compare to other terminals?

They are all good terminals, and erikterm borrows from them. One sentence each:

- **WezTerm** (Rust, Lua config, a built-in multiplexer): erikterm uses wezterm's setting names and reads its color
  scheme files, so a wezterm config translates line by line, but its config is a TOML file, not a script.
- **Ghostty** (Zig, the platform's own UI toolkit): Ghostty aims for a native look on each platform; erikterm draws
  its own tab bar, palette and menus the same way everywhere and puts its effort into developer tools.
- **Kitty** (C and Python): erikterm supports kitty's keyboard and graphics protocols, and `erikterm ssh` works like
  `kitten ssh`; unlike kitty, erikterm also runs on Windows.
- **Alacritty** (Rust): Alacritty stays small and leaves tabs and splits to tmux or your window manager; erikterm has
  tabs, splits and windows built in.

What erikterm adds is awareness of what runs in it: git and GitHub status, coding agents, a branch review and merge
tool, and Neovim links. It is fast in throughput benchmarks; [bench/README.md](../bench/README.md) has the benchmark
and how to run it yourself.

## Wayland or X11?

Both. SDL3 picks the video driver; set `SDL_VIDEO_DRIVER=wayland` or `SDL_VIDEO_DRIVER=x11` to choose yourself. Some
things differ: on Wayland the compositor decides where a new window goes, and the window icon comes from the desktop
entry unless the compositor supports xdg-toplevel-icon. See [Platform notes](guide/platforms.md) and
[Multiple windows](guide/windows.md).

## How are versions numbered?

With [Semantic Versioning](https://semver.org): a release with only fixes bumps the last number (1.2.0 → 1.2.1), one
with new features the middle one (1.2.1 → 1.3.0), and one where a setting, key binding, command-line option or file
format no longer works the way it did the first one (1.3.0 → 2.0.0); its release notes say what to do. Fixes,
security fixes included, go into the next release only; older ones aren't patched ([SECURITY.md](../SECURITY.md)).
[CHANGELOG.md](../CHANGELOG.md) lists every release.

## Where are my settings?

In `~/.config/erikterm/erikterm.toml` (or `$XDG_CONFIG_HOME/erikterm/erikterm.toml`; on Windows
`%APPDATA%\erikterm\erikterm.toml`). `--config-file PATH` or `$ERIKTERM_CONFIG_FILE` reads another file. Your own
color schemes go in `colors/` next to it. `Ctrl+,` opens the settings page. See [Configuration](guide/configuration.md).

What erikterm saves itself (the session, command history, preferences, crash logs) is in `~/.local/state/erikterm`
(`$XDG_STATE_HOME`; on Windows `%LOCALAPPDATA%\erikterm`).

## How do I reset erikterm?

- **One setting:** press `r` on it in the settings page.
- **All settings:** move `erikterm.toml` out of the way. Without a file, every setting has its default.
- **The saved session:** *Forget Saved Session* in the palette, or start once with `erikterm --no-restore`.
- **Everything else:** quit erikterm and delete the state folder above. That clears the session, history, preferences,
  crash logs and the list of hosts `erikterm ssh` has set up.

## Does erikterm collect telemetry?

No. erikterm has no telemetry, no analytics, no crash reporting and no update check, and it has no HTTP client.
Crash logs stay in the state folder on your machine. It only goes on the network through programs you have installed,
when a feature needs them:

- **gh**, for the pull request and CI status of the branch you're in (`github_status = true`, the default; only with
  `gh` installed and logged in). erikterm never reads your token.
- **git**, when you ask for it: *Download More Color Schemes…* in Browse Themes (a clone of
  [iTerm2-Color-Schemes](https://github.com/mbadolato/iTerm2-Color-Schemes) from GitHub), fetching the base branch in
  the review screen, or pushing a worktree to open a pull request. `git status` in the background stays local.
- **ssh**, for `erikterm ssh`.
- **docker or podman**, for `erikterm devcontainer`.

Links you click open in your browser. Turn the GitHub status off with `github_status = false`.

## Does erikterm work with a screen reader?

Not yet. [Accessibility](guide/accessibility.md) lists what there is today.
