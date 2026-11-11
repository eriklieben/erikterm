# Platform notes

erikterm works the same on Linux, macOS and Windows, with a few differences listed here.

## Linux

Windows have the app id (class) `erikterm`, which docks and launchers use to find the desktop entry and its icon.
`--class` changes it. On Wayland compositors without the xdg-toplevel-icon protocol, such as Hyprland, the icon comes
from the desktop entry.

## macOS

Builds exist for Apple silicon (`osx-arm64`) and Intel (`osx-x64`, not built in CI). Install the libraries with:

```sh
brew install sdl3 freetype harfbuzz
```

### Keys

Cmd is the app's modifier; the `Ctrl+Shift` chords work as well. Ctrl and Option go to the program.

| Keys | Action |
|---|---|
| `Cmd+C` / `Cmd+V` | Copy / paste |
| `Cmd+Shift+P` | Command palette |
| `Cmd+T` / `Cmd+N` | New tab / new window |
| `Cmd+W` | Close the pane |
| `Cmd+F` / `Cmd+Shift+F` | Search the scrollback / all panes |
| `Cmd+K` | Clear the scrollback |
| `Cmd+D` / `Cmd+Shift+D` | Split right / down |
| `Cmd+1`…`8` / `Cmd+9` | Tab 1–8 / the last tab |
| `Cmd+Shift+[` / `Cmd+Shift+]` | Previous / next tab |
| `Cmd+=` / `Cmd+-` / `Cmd+0` | Font size |
| `Cmd+R` | Reload the config |
| `Cmd+Q`, `Cmd+H`, `Cmd+M`, `Ctrl+Cmd+F` | Menu bar: quit, hide, minimize, full screen |

The left Option key is Alt (Meta); the right one types accented characters.

```toml
macos_option_as_alt = "left"   # or "right", "both", "none"
```

### Shell, fonts and files

- **Shell:** erikterm starts a login shell (`$SHELL -l`, else `/bin/zsh -l`), as Terminal.app does, so
  `~/.zprofile` and Homebrew's `PATH` are set. `default_prog` overrides it. Shell integration works with macOS's zsh
  and bash 3.2.
- **Fonts:** the default is SF Mono if installed, else Menlo; emoji come from Apple Color Emoji. Fonts in
  `~/Library/Fonts`, `/Library/Fonts` and `/System/Library/Fonts` are found. A face inside a `.ttc` collection is named
  `file.ttc#N`.
- **Files:** settings in `~/.config/erikterm/erikterm.toml`, as on Linux (`$XDG_CONFIG_HOME` is honoured); session,
  preferences and crash logs in `~/.local/state/erikterm` (`$XDG_STATE_HOME`).

## Windows

- **Console host:** the Windows download includes `conpty.dll` and `OpenConsole.exe`, the console host that Windows
  Terminal ships, which is much faster than the one built into Windows. Without those files next to `erikterm.exe`,
  or with `ERIKTERM_CONPTY=system` set, erikterm uses the built-in one.
- **What runs in a pane:** erikterm shows the program you started, such as `nvim`, `cargo` or Claude Code, rather than
  the helpers it runs. Programs are named without `.exe`. It can't see the arguments or directory of an elevated
  program unless erikterm is elevated too.
- **Directories:** PowerShell's `cd` is only followed through [shell integration](shell-integration.md); cmd's `cd`
  is followed as is.
- **Sessions:** restored sessions record programs but don't run them again.
