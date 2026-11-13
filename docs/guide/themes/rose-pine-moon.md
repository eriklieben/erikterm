# Rosé Pine Moon

Dark. Upstream: Rosé Pine.

![Rosé Pine Moon in erikterm](../../media/themes/rose-pine-moon.webp)

## What it looks like

Lavender-white text on a lighter, bluer purple (#232136). The same muted accents as Rosé Pine: rose red, pine, gold,
foam, iris and a soft coral. A little brighter and cooler than the main variant.

## Where it comes from

From [Rosé Pine](https://github.com/rose-pine/ghostty) by the Rosé Pine team. MIT license.

## Use it

```toml
color_scheme = "Rosé Pine Moon"
```

Its accent, which colors the active tab, focus marks and palette highlights, is `#c4a7e7` (iris). `[colors] accent` in
your config overrides it.

## Accessibility

Measured from the pixels of the screenshot above (focused editor) and of the same scene with the editor in the
inactive pane (dimmed by the default `inactive_pane_hsb`): the statusline's text against its own block.

| Statusline block | Focused | Dimmed | Without the contrast default |
|---|---|---|---|
| NORMAL (black on green) | 4.56:1 | 3.43:1 | 3.24:1 / 2.64:1 |
| main (black on white) | 8.96:1 | 6.55:1 | 8.96:1 / 6.55:1 |
| Moon.cs [+] (white on bright black) | 4.5:1 | 3.84:1 | 3.91:1 / 3.39:1 |
| C# utf-8 (bright black text) | 4.51:1 | 3.15:1 | 3.03:1 / 2.24:1 |
| 14:23 (black on blue) | 6.83:1 | 5.07:1 | 6.83:1 / 5.07:1 |

Lowest: 4.5:1 focused (target 4.5:1, WCAG AA) and 3.15:1 dimmed (target 3:1). Body text: 12:1.

The built-in contrast default (`minimum_contrast = 4.5`) does the work here. As published, these are below 4.5:1, and
erikterm draws them lighter or darker, keeping their hue, until they reach it: text in black (1.3:1), green (4.3:1),
bright black (3:1), bright green (4.3:1); black text on green (3.2:1); white text on bright black (3.9:1); bright
white text on bright black (3.9:1). The palette is as published, not adapted. With `minimum_contrast = 1` in your
config you get the published colors as they are; the notes below describe those.

## See also

- [Rosé Pine](rose-pine.md)
- [Rosé Pine Dawn](rose-pine-dawn.md)
- [Catppuccin Macchiato](catppuccin-macchiato.md)
- [All color schemes](README.md)
