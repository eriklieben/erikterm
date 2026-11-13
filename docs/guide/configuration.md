# Configuration

erikterm reads its settings from `~/.config/erikterm/erikterm.toml`. Setting names match wezterm's, and saving the
file applies changes right away. [`erikterm.example.toml`](../../erikterm.example.toml) lists every option with its
default.

## The settings page

`Ctrl+,` (or **Settings** in the palette) opens every setting in a tab of its own, grouped, each with what it does
and its default. Arrow keys change a value and save it right away, so the open windows show it at once; `r` puts a
setting back to its default, `u` undoes the last change and `/` searches. Tables such as `[colors]` and `[keys]` open
in your editor. The page changes only the line of the setting, so your comments and the order of the file stay as
they were. It also runs on its own: `erikterm settings`.

## The config file

Use `--config-file PATH` or `$ERIKTERM_CONFIG_FILE` to read another file. A small example:

```toml
color_scheme = "Catppuccin Macchiato"
window_background_opacity = 0.75   # transparent window; blur is up to the compositor
window_decorations = "NONE"
default_cursor_style = "BlinkingBar"
cursor_blink_style = "phase"       # a smooth, breathing blink instead of on/off
cursor_thickness = "1.5px"         # a thinner bar (points, or "0.1cell")
font = "JetBrainsMono Nerd Font"
font_size = 11
line_height = 1.25
inactive_pane_hsb = { saturation = 0.9, brightness = 0.35 }
command_palette_bg_color = "#1e2030"

[colors]
split = "#b7bdf8"
```

If a setting is unknown or has a bad value, erikterm reports it with its line number (and a "did you mean" for near
misses) and skips it; the rest of the file still applies. A font that isn't installed is reported too. The first
problem shows as a toast, and *Show Config Problems* in the palette lists them all. If you save a file with a syntax
error, every window keeps its current settings.

## Overriding settings for one run

`--set KEY=VALUE` applies a setting on top of the file, for this window and the windows it opens, and survives config
reloads. You can repeat it; `--config KEY=VALUE` (wezterm's spelling) works too. The value is TOML, and anything that
isn't valid TOML is taken as a string, so quotes are optional:

```sh
erikterm --set font_size=13 --set color_scheme=Tokyo Night
erikterm --set 'color_scheme="Tokyo Night"' --set colors.split='#b7bdf8' --set 'window_padding={ left = 2 }'
```

A dotted key (`colors.split`) changes one entry of a table; an inline table replaces the whole value. In
[vim mode](vim-mode.md), `:set` does the same for the running session.

## Readable text

`minimum_contrast` makes text at least that readable against its background, by moving its color toward white or
black only as far as needed, so it keeps its hue.

```toml
minimum_contrast = 4.5   # 1 = off; 4.5 is WCAG AA, 7 is AAA
```

When you don't set it, the color scheme decides: the built-in schemes bring 4.5, so text in a prompt or statusline
block stays readable in every one of them, and keep text at 3:1 in an inactive pane after `inactive_pane_hsb` dims it;
a scheme file brings none (1). Whatever you set wins, so `minimum_contrast = 1` turns it off for the built-in schemes
too, and a value of your own leaves inactive panes dimmed as they are.

Hidden text stays hidden, and images, color emoji, box drawing and powerline glyphs keep their colors.

## Following the system's accessibility settings

erikterm reads three settings from your system and acts on them. You don't need to change the config for this:

```toml
reduce_motion = "auto"   # "on" / "off" override the system
high_contrast = "auto"   # "on" / "off" override the system
text_scale = "auto"      # "off" keeps the font sizes as set
```

They work on top of your other settings. Reduce motion makes erikterm behave as if `animation_duration_ms = 0`, but
your `animation_duration_ms` stays in the file, and it's used again when reduce motion goes off.
[Accessibility](accessibility.md#following-the-system) says which system setting each one follows.

## Color schemes

**Browse Themes…** in the palette (`BrowseThemes`) lists every scheme with live preview: move through the list to
try one, `Enter` keeps it for this session, `Esc` goes back. It includes the built-in schemes, your own files and wezterm's.

[Color schemes](themes/README.md) shows each built-in scheme, where it comes from, its accent and its contrast.

### Your own scheme files

erikterm loads wezterm-format scheme files from `~/.config/erikterm/colors/*.toml` (Windows:
`%APPDATA%\erikterm\colors`; also `colors/` next to `--config-file`) and from `~/.config/wezterm/colors/*.toml`, so
your wezterm schemes just work.

```toml
[colors]
foreground = "#ebdbb2"
background = "#282828"
cursor_bg = "#fe8019"            # also cursor_fg, cursor_border, selection_bg, selection_fg, split
ansi    = ["#282828", "#cc241d", "#98971a", "#d79921", "#458588", "#b16286", "#689d6a", "#a89984"]
brights = ["#928374", "#fb4934", "#b8bb26", "#fabd2f", "#83a598", "#d3869b", "#8ec07c", "#ebdbb2"]

[metadata]
name = "Gruvbox Dark"            # else the file name
```

- `color_scheme = "Gruvbox Dark"` picks it. A file scheme replaces a built-in one with the same name.
- Saving a scheme file reloads it, like the config. Errors are reported like config errors.
- `selection_fg` colors selected text (without it, text keeps its colors). `cursor_border` is the cursor's outline in
  unfocused panes.
- Your config's own `[colors]` wins over the scheme.
- Other wezterm keys (`tab_bar`, `indexed`, …) are ignored.

### Downloading more schemes

*Download More Color Schemes…* at the top of Browse Themes fetches the
[iTerm2-Color-Schemes](https://github.com/mbadolato/iTerm2-Color-Schemes) catalogue (MIT licensed; its `LICENSE` is
saved with the files). It needs `git`, and nothing touches the network until you press *Download*.

The files go to `~/.config/erikterm/colors/iTerm2-Color-Schemes` (the `colors/` directory next to your config file).
Downloading again updates them, and deleting the directory removes them. Your own scheme files win over a catalogue
scheme with the same name. A catalogue scheme named like a built-in one (Dracula, Nord, …) is left out: the built-in
keeps its accent and contrast. To use the catalogue's version anyway, copy its file into `colors/` yourself.
