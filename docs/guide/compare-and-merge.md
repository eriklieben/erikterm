# Compare and merge

erikterm has three full-screen tools that run in a pane: `compare` shows two files side by side, `review` reviews a
branch like a pull request, and `mergetool` resolves merge conflicts. They work in tabs, splits, over ssh and in other
terminals.

## Comparing two files

```sh
erikterm compare a.ts b.ts                      # two files side by side
erikterm compare main:src/a.ts HEAD:src/a.ts    # a side can be a git object (rev:path, :1:path)
```

Lines are aligned, with word-level highlights and an overview strip.

| Key | |
|---|---|
| `]` / `[` | next / previous difference |
| `d` | view: All, Differences, Context |
| `h` / `l` | pan |
| `i` | ignore rules: *whitespace*, *line endings*, *comments* |

Ignored differences stay on screen, dimmed, and are counted separately ("3 differences, 2 unimportant"); `]` skips
them. Whitespace and comment-like text inside strings still count. Comment syntax is chosen by file extension. Binary
files and files over 128 MB show a message instead.

## Reviewing a branch

**Git: Compare with main** in the palette reviews the current worktree like a GitHub pull request, without pushing.
It opens a tab named `compare: <branch>` that you can leave and come back to; running it again jumps to that tab.
**Git: Compare with Branch…** asks for the base first. In `[keys]`, use `CompareWithMain`, or `CompareWithMain(Pick)`
for the branch picker. From a shell:

```sh
erikterm review                                 # against the default branch
erikterm review --base release/2.0              # against another branch
```

Changed files are on the left and the compare on the right (the list moves to the top below 100 columns). Like a PR,
only this branch's changes count (`git diff <base>...HEAD`), and renames are detected. Files show A/M/D/R and line
counts; a dot marks uncommitted changes.

| Key | |
|---|---|
| `]` / `[` (or `n` / `p`) | next / previous change, continuing into the next file |
| `J` / `K` (or `}` / `{`) | next / previous file |
| `Tab` | switch between the file list and the compare |
| `u` | include uncommitted changes (staged, unstaged, untracked); on by default |
| `c` | the branch's commits: `Enter` reviews one, `Esc` goes back to all |
| `b` | compare with another branch |
| `F` | fetch `origin <base>` |
| `f` | open **Finish Worktree** (in a linked worktree; see [Coding agents](coding-agents.md)) |
| `a` (or `e`) | comment on the current line, or edit the comment there; `Enter` saves, `Ctrl+N` adds a line |
| `v` | start a range; move, then `a` comments on all of it |
| `x` `x` | delete the comment under the cursor |
| `s` | send the comments to the coding agent in this worktree, after confirming |
| `r` | refresh (also happens when you come back to the tab) |
| `d`, `i`, `h`/`l`, `q` | view, ignore rules, pan, close |

Comments follow their line as the file changes (`outdated` once it's gone). `s` sends them as one prompt with
`file:line` references to the agent working in this worktree, in any window, and presses Enter. If there's no agent,
or it's waiting for an answer, they stay. Unsent comments survive closing the review; nothing is written to the
repository.

The header shows whether the branch would merge cleanly, and notes when your local base is behind `origin/<base>`.
**erikterm never fetches on its own**, and the review changes nothing in the repository.

## Resolving conflicts

```sh
erikterm mergetool                              # every unmerged file of this repository
erikterm mergetool LOCAL BASE REMOTE MERGED     # one file, as git mergetool calls it
```

To make `git mergetool` use it, run this once (erikterm never changes your git config):

```sh
git config --global mergetool.erikterm.cmd 'erikterm mergetool "$LOCAL" "$BASE" "$REMOTE" "$MERGED"'
git config --global mergetool.erikterm.trustExitCode true
git config --global merge.tool erikterm
```

While a repository is mid-merge, rebase, cherry-pick or revert, the merge bar offers **Open Compare**, which opens the
resolver in a new tab (or a split with `worktree_open_in = "split"`).

Ours and theirs sit side by side above the result, with a file list when several files conflict (stacked below about
100 columns). Changes that don't overlap merge on their own; only real conflicts need a decision. Everything also
works with the mouse.

| Key | |
|---|---|
| `n` / `p` | next / previous conflict |
| `o` / `t` / `b` | take ours / theirs / both |
| `r` | reset the conflict |
| `u` | undo |
| `e` / `E` | edit the conflict / the whole result in `$VISUAL` / `$EDITOR` (Neovim by default) |
| `Ctrl+S` | save |
| `Tab` / `Shift+Tab` | next / previous file |
| `w` | switch between the wide and compact layouts |
| `q` / `Esc` | quit (asks if the result isn't saved) |
| arrows, `j`/`k`, `h`/`l`, `PgUp`/`PgDn`, `g`/`G` | scroll, pan |

**Saving** with conflicts left asks whether to keep the markers. In repository mode it then runs `git add` on the
file and moves on; it never commits, continues or aborts the merge. The exit code is 0 only when every file was
resolved and saved. Delete/modify and binary conflicts ask which version to keep. Line endings and encoding are kept
as they were.
