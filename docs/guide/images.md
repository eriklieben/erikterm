# Images

Programs can show pictures in the terminal with any of the three common protocols: kitty graphics, iTerm2 inline
images and sixel. There's nothing to set up, except for Claude Code (see below).

## Supported protocols

| Protocol | Notes |
|---|---|
| **kitty graphics** | Full support, including placements under the text, Unicode placeholders, relative placements and animation. Sending images as a file path, temp file or shared memory is off by default. |
| **iTerm2 inline images** | PNG, GIF (first frame) and JPEG, sized in cells, pixels, percent or `auto`. Other formats show an "unsupported image" box. |
| **sixel** | Palettes (RGB and HLS). |

Kitty images sent by file path, temp file or shared memory are off unless you turn them on, because anything printed
in the terminal could otherwise make it read your local files:

```toml
allow_kitty_file_transmission = false
```

Even when on, only regular, non-empty files are read. Images over 50 megapixels are refused.

## How images behave

- **iTerm2 and sixel** images become part of the text: they scroll into the scrollback with it, and printing over
  them erases them. When the pane gets narrower, the picture is cut off at the right edge rather than broken up.
- **kitty** images float over (or under) the text. They scroll with it, but text doesn't erase them; the program
  deletes them. When the pane is resized they keep their place and size.
- Leaving the alternate screen deletes the images shown there, and a terminal reset deletes all of them.
- Animations only play while you can see them.

## Images in Claude Code

Claude Code doesn't recognise erikterm as an image-capable terminal on its own. Set this in `erikterm.toml`:

```toml
set_environment_variables = { CLAUDE_CODE_FORCE_TERMINAL_IMAGES = "1" }
```

Claude Code then uses the kitty protocol, which works in both its classic and fullscreen (`/tui fullscreen`)
renderers.
