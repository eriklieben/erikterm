# Multiple windows

One erikterm process can have several windows, and you can move tabs between them with their programs still running.

## Opening a window

- `Ctrl+Shift+N` (*New Window*) opens a window in the same process.
- Running `erikterm` again opens a window in the running process, in the directory you ran it from. `-e PROGRAM`
  works too.

Only a process with the same config file, `--set` overrides and `--class` takes the new window. To start a separate
process instead, use `--new-process`, or set:

```toml
single_instance = false
```

## Moving tabs between windows

A moved tab keeps its programs, scrollback and state; nothing restarts.

- **Drag a tab out** of the tab bar. Drop it on another window's tab bar to put it there, or anywhere else to give it
  a window of its own.
- Use *Move Tab to New Window* or *Move Tab to Window N* in the palette or the tab's right-click menu
  (`MoveTabToNewWindow` and `MoveTabToWindow(N)` for `[keys]`).

On X11, Windows and macOS a marker shows where the tab will go, and a new window opens where you drop it. On Wayland
there is no marker over other windows, and the compositor decides where a new window goes.
