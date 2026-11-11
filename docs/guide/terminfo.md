# Terminal type and terminfo

Programs learn what a terminal can do from its terminfo entry, named by `$TERM`. erikterm ships its own,
`xterm-erikterm`. With it, programs such as Neovim draw curly and dotted underlines in the theme's colors (for LSP
diagnostics and spelling), and know about true color, cursor shapes, strikethrough, overline and more. Without it
they fall back to `xterm-256color`, where Neovim's undercurl is a plain underline.

## Installing it locally

```sh
scripts/install-terminfo.sh   # installs into ~/.terminfo; panes opened afterwards get TERM=xterm-erikterm
```

```toml
term = "auto"   # xterm-erikterm when it's installed, else xterm-256color; or name one, e.g. "xterm-256color"
```

## Using it over ssh

Use `erikterm ssh` instead of `ssh` (like `kitten ssh`):

```sh
erikterm ssh [ssh options] destination [command]
```

The first time you connect to a host, it installs erikterm's entry there in `~/.terminfo` (as root, in the system's
terminfo directory, since some ncurses builds ignore root's `~/.terminfo`), then runs ssh with your arguments and
`TERM=xterm-erikterm`. This works even if the entry isn't installed locally.

- Each host is set up once. Aliases for the same host share it, and it's redone when a newer erikterm changes the
  entry.
- A host without `tic` gets `TERM=xterm-256color`, and that is remembered too. A failed connection is tried again next
  time.
- The first time costs one extra connection, without a terminal or port forwarding.
- Nothing else is installed. Shell integration on the remote host is up to you (`contrib/shell/`).
- Outside erikterm it is plain ssh.

In a dev container, `erikterm devcontainer` installs the entry the same way (see
[Developer tools](developer-tools.md#dev-containers)).

To install the entry on a host by hand:

```sh
erikterm --terminfo | ssh HOST 'mkdir -p ~/.terminfo && tic -x -o ~/.terminfo /dev/stdin'
```

## Program colors and light or dark mode

Programs can change a pane's palette, foreground, background and cursor colors. These sit on top of your color
scheme: switching scheme keeps them until the program resets them or the terminal is reset, as in kitty and wezterm.
Programs that ask whether the background is light or dark get an answer, and are told when a scheme change flips it.

For the full list of supported sequences, see [Compatibility](compatibility.md).
