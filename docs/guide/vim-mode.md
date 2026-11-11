# Vim mode

Vim mode lets you move through the scrollback, select, search and copy with vim keys. It is also the keyboard route
to links and pane commands.

Press `Ctrl+Shift+X` (`ActivateCopyMode` or `VimMode`) in a pane. A cursor of its own appears, with a
`-- NORMAL --` / `-- VISUAL --` / `-- V-LINE --` / `-- V-BLOCK --` pill in the corner. Nothing you type reaches the
program until you leave with `i` or `a` (back to the prompt), `q` or `Esc` (in visual mode, `Esc` returns to normal
mode first). `g?` or `:help` shows a cheat sheet.

Vim mode is per pane, and output keeps arriving without moving your cursor. Clicks and drags move and select,
`Ctrl+click` opens links, and the palette and `Ctrl+Shift` shortcuts keep working. Your `[keys]` bindings on
`Ctrl+letter` keys vim doesn't use (such as `CTRL+h`) still win.

## Keys

| Keys | |
|---|---|
| `h j k l`, with counts (`5j`) | move |
| `w b e W B E ge` · `0 ^ $` | words / WORDS · line start, first character, end |
| `gg G` · `:N` · `NG` | top, bottom of the scrollback · line N |
| `H M L` · `zt zz zb` | top / middle / bottom of the screen · scroll the cursor line there |
| `Ctrl-u Ctrl-d` · `Ctrl-b Ctrl-f` · `Ctrl-y Ctrl-e` | half page · page · line |
| `f F t T` `; ,` · `%` | to a character on the line, repeat / reverse · matching bracket |
| `{ }` · `[[ ]]` | previous / next command output · previous / next prompt (with [shell integration](shell-integration.md); without it `{ }` are paragraphs) |
| `v V Ctrl-v` · `o` | visual: characters, lines, block · go to the other end |
| `iw aw iW aW ip ap` | text objects (`ip`: a command's output, `ap`: with its prompt) |
| `y` · `Y` | copy the selection / its lines to the clipboard and the primary selection |
| `yy` `y$` `yiw` `y3j` … | copy without selecting first |
| `yo` · `yc` | copy the current command's output · its command line |
| `/` `?` · `n N` · `*` `#` | search forward / backward as you type (vim regex, smartcase) · next / previous · the word under the cursor |
| `Ctrl+Shift+F` | the `/` search prompt |
| `gx` · `gf` | open the URL under the cursor · open `file:line` in Neovim |
| `go` · `gO` | open the command's output · the whole scrollback in Neovim, read-only |
| `;` (when not repeating `f`/`t`) · `gh` | hints on links, paths and hashes: a label moves there, an upper-case label copies, `Ctrl`+label opens |
| `m{a-z}` · `'{a-z}` `` `{a-z} `` · `''` | set a mark · jump to its line / exact spot · back from the last jump |
| `Ctrl-w h j k l` · `w W` | focus a pane · next / previous pane |
| `Ctrl-w s v` · `q` · `o` | split down / right · close · zoom |
| `Ctrl-w x` · `r R` · `=` | swap with the next pane · rotate · equalize |
| `Ctrl-w H J K L` · `T` | move the pane to the far left / bottom / top / right · to a new tab |
| `Ctrl-w > <` · `+ -` | wider / narrower · taller / shorter (a count multiplies: `10 Ctrl-w >`) |
| `gt gT` · `Ngt` | next / previous tab · tab N |

## Commands

`:` opens a prompt at the bottom of the pane with completion as you type: `Tab` / `Shift+Tab` cycle, `↑ ↓` go through
history. If a command fails, the prompt stays open so you can fix it.

| Command | |
|---|---|
| `:sp` `:vs` (`:vs htop`) · `:q` · `:only[!]` | split down / right, optionally running a command · close the pane · close the tab's other panes (`!` also those running a program) |
| `:tabnew` · `:tabn [N]` · `:tabp` · `:tabc` | new tab · next tab (or tab N) · previous · close the tab |
| `:theme NAME` (`:colo`) | switch color scheme, previewed as you type |
| `:set font_size=13` | change a setting for this session, like `--set` |
| `:noh` | clear search highlights |
| `:N` · `:$` | go to line N · the last line |
| `:w FILE` · `:w! FILE` | write the scrollback to a new file · overwrite one |
| `:send PANE` | paste the selection (or last yank) into another pane, by id or by part of its agent's name, program or title (`:send claude`) |
| `:layout NAME` | `even-horizontal`, `even-vertical`, `main-vertical`, `main-horizontal`, `tiled`, or `even` |
| `:help` | the cheat sheet |
| `:ActionName [arg]` | any erikterm action: `:SplitHorizontal`, `:ActivatePaneDirection Left` |

## Arranging panes without vim mode

The pane commands are also actions for `[keys]` and the palette (*Pane*, *Layout*): `SwapPane(Next|Previous)`,
`RotatePanes(Clockwise|CounterClockwise)`, `EqualizePanes`, `MovePaneToNewTab`, `MovePaneToEdge(Left|Right|Up|Down)`
and `PaneLayout(even-horizontal|even-vertical|main-vertical|main-horizontal|tiled)`. From a script, use
`erikterm cli swap-pane`, `rotate-panes`, `select-layout` and `move-pane-to-new-tab` (see [Scripting](scripting.md)).
