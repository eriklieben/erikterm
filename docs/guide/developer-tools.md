# Developer tools

erikterm works with git, the GitHub CLI, Neovim and dev containers when they are installed: file and commit links
open where you want them, and tabs show the branch, changes and CI state.

## File links

Paths in output are links when the file exists: `path:line[:col]`, compiler errors like `File.cs(12,5): error …`,
.NET stack frames, Python tracebacks, plain paths, and the `a/` / `b/` paths of `git diff`. Relative paths are looked
up from the pane's directory, then the repository root.

`Ctrl+click` opens the file at that line in the Neovim running in the same tab, and focuses it. Without one, it opens
`nvim +line` (or `$VISUAL` / `$EDITOR`) in a split. Right-click for *Open in Neovim*, *Open in New Split* and *Copy
path:line*.

```toml
editor_open = "nvim"   # or "split", "system", "none"
```

For the running Neovim to be found, load `contrib/nvim/erikterm.lua` (see
[Scripting](scripting.md#neovim-smart-splitsnvim-across-neovim-splits-and-erikterm-panes)).

## Git status

Each pane's repository shows as a badge on its tab: branch, `↑ahead ↓behind` and `●changes`. For a few seconds after
it changes, a pill in the pane's corner shows the breakdown: `+staged ●modified ?untracked !conflicts`. There's no
badge outside a work tree, in a bare repository, or when `git status` is too slow (a huge repository).

The palette has *Git: Status*, *Git: Diff* and *Git: Log* (in a split, through `delta` when installed), *lazygit* and
*tig* when installed, *Refresh Status* and *Copy HEAD Commit Hash*.

```toml
git_status = true
```

## GitHub

With `gh` installed and logged in, and a github.com remote, the badge adds the branch's pull request and its checks
(a spinner while pending, ✓ or ✗), or the latest workflow run when there's no PR. erikterm never reads your token.
When checks finish while you're looking elsewhere, a toast says *CI passed* or *CI failed* with an *Open* button.

The palette has *GitHub: Open PR in Browser*, *View Checks*, *Watch Run* (in a split, with a toast when it ends) and
*Open Repository*.

```toml
github_status = true
```

## Commits and issues

In a git repository, commit hashes are links: `Ctrl+click` opens `git show` in a split (through `delta` when
installed), and right-click offers *Copy Full Hash*. In a GitHub repository, `#123` opens the issue or PR in your
browser. In quick select, `Ctrl`+label opens the match instead of copying it.

## Dev containers

`erikterm devcontainer` opens a shell in a folder's [dev container](https://containers.dev), with docker or podman:

```sh
erikterm devcontainer                      # the dev container of the folder you're in
erikterm devcontainer up --rebuild         # build it again first
erikterm devcontainer -- dotnet test       # run one command in it
```

The folder is the nearest one up with a `.devcontainer/devcontainer.json` (else the git repository). erikterm finds
the container by the label the devcontainer CLI and VS Code put on it, starts it when it's stopped, and creates it with
`devcontainer up` when there is none yet (that needs the [devcontainer CLI](https://github.com/devcontainers/cli):
`npm install -g @devcontainers/cli`). The shell runs as the container's `remoteUser`, in the directory you were in when
a bind mount holds it. Options: `--workspace-folder`, `--container`, `--engine docker|podman` (default: docker, else
podman), `--user`, `--workdir`; `erikterm devcontainer --help` lists them all.

The first time a container is used, erikterm copies its shell integration and terminfo entry into
`/tmp/erikterm-…` there (with `docker cp` / `podman cp`, so the image needs nothing extra) and compiles the entry with
`tic`. Without `tic` in the image, `TERM` is `xterm-256color`.

Once a pane's shell runs in a container, whether you started it this way or typed `docker exec -it …`,
`podman exec -it …`, `docker compose exec …` / `podman compose exec …` (or `docker-compose` / `podman-compose`) or
`devcontainer exec …`, erikterm treats the pane as being in the container:

- Its tab shows a cube before the title, and the title is the directory inside the container.
- Splits, new tabs and new windows open another shell in the same container and directory.
- Paths printed in the container (`/workspaces/app/src/x.cs:12`) are links to the file on your machine, through the
  container's bind mounts, and the git badge follows the repository on your machine.
- A restored session opens the shell in the container again. If the container is gone, you get a shell on your machine
  instead.

The palette offers *Dev Container: Open … in Its Container* when the focused pane is in a folder with a dev container
configuration, and *Rebuild …'s Container*.

## Drag and drop

Dropping files on a pane pastes their paths, shell-quoted and space-separated, without pressing Enter. Claude Code
turns dropped image paths into attachments.

```toml
drop_files = true
```

## Drop-down terminal (Hyprland)

`contrib/hyprland/` has a window rule, a `SUPER+F12` binding and a toggle script that run erikterm with
`--class erikterm-dropdown` on a special workspace that slides down from the top. It keeps its own
[session](sessions.md).
