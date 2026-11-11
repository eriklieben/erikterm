# Accessibility

What erikterm does and doesn't do for accessibility today.

## Screen readers

erikterm draws all its text itself on the GPU, so a screen reader can only read what erikterm hands it through the
platform's accessibility interface.

**Windows (new, not yet tested with a screen reader).** erikterm supports UI Automation, the interface NVDA,
Narrator and JAWS use:

- The window is named `erikterm — <tab title>`. Each visible pane is an element of its own, named after its program
  or folder. The focused pane has the keyboard focus, and moving between panes or tabs moves the screen reader with
  it.
- The text of a pane, scrollback and screen, can be read by character, word, line and paragraph, with the screen
  reader's review commands. A line is one row of the terminal, as in Windows Terminal, so a command that wraps onto
  a second row is two lines. A paragraph is the whole line, wraps included. A wide character and a letter with a
  combining accent each count as one character.
- The caret follows the terminal's cursor, and the selection is erikterm's selection. A screen reader can select
  text and scroll a pane to a line.
- New output is spoken, about a tenth of a second after it arrives. The characters you type aren't spoken a second
  time when the shell echoes them: the screen reader already speaks your keys. A flood of output speaks only its last
  20 lines, and a progress line that keeps rewriting itself is spoken at most once a second.
- Full-screen programs (vim, less, htop: the alternate screen) aren't announced while they redraw. Read them with
  the screen reader's review cursor.

Known limits: this support is new and has not been tried with a screen reader yet. Text attributes (colors, bold)
aren't exposed, the tab bar, the command palette and other pop-ups aren't exposed, and a block selection reads as
the text from its first to its last cell. erikterm doesn't call itself Windows Terminal, so screen-reader features
made for Windows Terminal don't switch on. If something doesn't work, please report it (see
[Feedback](#feedback)).

**Linux and macOS.** Screen readers don't work yet: erikterm doesn't expose its text to AT-SPI or the macOS
accessibility API. If you rely on a screen reader there, a terminal with screen-reader support serves you better
for now.

## Following the system

erikterm follows your system's settings for motion, contrast and text size. It checks them at start, when one of its
windows gets focus again (after you change them in the system settings, say), and when the system's theme changes.
Each one has a setting, `"auto"` by default; `"on"` or `"off"` in the config wins over the system.

| Setting | On | Follows |
| --- | --- | --- |
| `reduce_motion` | No animations, no gliding cursor, no smooth scrolling, and a cursor that doesn't blink | Windows: *Animation effects* off. macOS: *Reduce motion*. GNOME: *Reduce Animation* (animations off), or the desktop portal's reduced-motion |
| `high_contrast` | `minimum_contrast` of at least 7 (WCAG AAA), and stronger text, outlines, dividers and focus marks in the tab bar, palette and menus | Windows: a contrast theme. macOS: *Increase contrast*. GNOME: *High Contrast*, or the desktop portal's contrast |
| `text_scale` | The font sizes (terminal, palette, pane-select letters) grow by the system's text size | Windows: *Make text bigger*. GNOME on Wayland: *Large Text* / text scaling |

They work on top of your own settings without changing the file. While reduce motion is on, erikterm acts as if
`animation_duration_ms = 0`, `cursor_animation = false`, `smooth_scrolling = false` and `cursor_blink_rate = 0`; when
it goes off, your values are back. A cursor that doesn't blink at all is always in view and nothing on the screen
changes while you read, so reduce motion stops the blink rather than turning the fade into a hard flash.

With a Windows contrast theme on, the panes take the theme's text, background, selection and accent colors, and the
16 colors programs choose from stay the scheme's, made readable on the new background by the raised
`minimum_contrast`. macOS and Linux have no such colors for applications to use: there, high contrast raises the
contrast of erikterm's own colors.

The text size comes on top of the display scaling, which erikterm already follows. macOS has no system-wide text size
for applications, so `text_scale` does nothing there; use `font_size`. On GNOME under X11 the system already passes the
text size on through the display scale, so erikterm doesn't apply it a second time.

**KDE, Hyprland, Sway and other desktops** often don't offer these settings in a way erikterm can read. Set them in the
config instead:

```toml
reduce_motion = "on"
high_contrast = "on"
font_size = 16
```

On Linux erikterm asks the [XDG desktop portal](https://flatpak.github.io/xdg-desktop-portal/) (`contrast`,
`reduced-motion`) and GNOME's own settings, through `gdbus` and `gsettings` when they're installed. You can check what
they say:

```sh
gsettings get org.gnome.desktop.interface enable-animations
gsettings get org.gnome.desktop.interface text-scaling-factor
gsettings get org.gnome.desktop.a11y.interface high-contrast
```

## What there is

**Contrast.** `minimum_contrast` moves text colors toward white or black until they reach a contrast ratio against
their background, keeping the hue. 4.5 is WCAG AA, 7 is AAA. It works with any color scheme. `high_contrast = "on"` sets it to at least 7 and strengthens the chrome as well
(above). erikterm has no built-in high-contrast scheme; you can write your own or look for one in the catalogue that Browse Themes downloads
([Color schemes](configuration.md#color-schemes)).

```toml
minimum_contrast = 7
```

**Text size.** `font_size` and `line_height` set the default. `Ctrl+=`, `Ctrl+-` and `Ctrl+0` (`Cmd` on macOS) zoom
the font of one window and reset it. The command palette and the pane-select letters have their own sizes. All of them grow with the system's text size
(`text_scale`, above).

```toml
font_size = 16
line_height = 1.2
command_palette_font_size = 16
pane_select_font_size = 48
```

**Motion and flashing.** `reduce_motion = "on"` stops all of the motion below at once. `animation_duration_ms = 0` turns
off the UI animations, and with them the gliding cursor and smooth scrolling. `cursor_blink_rate = 0` stops the cursor from blinking, also when a program asks for a blinking
cursor. `visual_bell = false` stops a bell from flashing the pane.

```toml
animation_duration_ms = 0
cursor_blink_rate = 0
visual_bell = false
```

**The cursor.** `default_cursor_style` picks a block, underline or bar. `cursor_thickness` and `cursor_height` size the
bar and underline, and `[colors]` sets `cursor_bg`, `cursor_fg` and `cursor_border` (the hollow cursor of unfocused
panes).

**Unfocused panes.** `inactive_pane_hsb` dims the panes you're not typing in. `brightness = 1.0` and
`saturation = 1.0` turn the dimming off.

**The keyboard.** Every action is in the command palette (`Ctrl+Shift+P`), and every key can be rebound in `[keys]`
([Panes, menus and keys](keys.md)). [Vim mode](vim-mode.md) moves through and copies from the scrollback without a
mouse.

All of these are on the settings page (`Ctrl+,`) as well; [`erikterm.example.toml`](../../erikterm.example.toml) lists
them with their defaults.

## Feedback

If something gets in your way, please [open an issue](https://github.com/eriklieben/erikterm/issues) and say what you
use and what you need. Reports from people who use assistive technology help most.
