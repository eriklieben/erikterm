# Compatibility

The escape sequences and protocols erikterm supports, for checking whether a program will work in it.

## Text and rendering

- UTF-8, wide characters and grapheme clusters (combining marks, VS16, skin tones, ZWJ sequences, flags, keycaps
  like 1️⃣), with Unicode 17 widths
- Ligatures (`harfbuzz_features`), color emoji, font fallback, and variable fonts
- Box drawing, block elements, braille and powerline separators drawn to fill the cell at any `line_height`
- 16, 256 and true color; styled and colored underlines (single, double, curly, dotted, dashed); overline;
  strikethrough
- Cursor shapes and blinking (DECSCUSR). xterm's `cnorm` can't turn off a blinking cursor you configured.
- Reflow on resize, for the screen and the scrollback
- Inline [images](images.md): kitty graphics, iTerm2 and sixel

## Terminal features

- Alternate screen, scroll regions, DEC line drawing, insert and delete lines and characters
- Bracketed paste, focus events, SGR mouse, synchronized output (2026)
- The kitty keyboard protocol, all flags
- Hyperlinks (OSC 8) and [shell integration](shell-integration.md) prompt marks (OSC 133)
- Clipboard (OSC 52), current directory (OSC 7), titles and the title stack
- Notifications (OSC 9, 777, 99) and progress (OSC 9;4)
- iTerm2 tab colors (OSC 6)
- Palette and default colors (OSC 4, 10, 11, 12, 104, 110–112)
- Light and dark mode reports (2031), in-band resize (2048)
- Agent status (OSC 7501, see [Coding agents](coding-agents.md))

## Queries answered

DA1, DA2, DA3, DSR, DECXCPR, DECRQM, DECRQSS, XTGETTCAP, XTVERSION, and the window and cell size in pixels. Reporting
the window title (`CSI 21 t`) is deliberately not answered, because echoing a program-controlled title back as input
can be abused.

See [Terminal type and terminfo](terminfo.md) for `TERM` and the terminfo entry.
