# Color schemes

erikterm ships 34 built-in color schemes, dark and light. `erik` is the default.

## Picking one

- Set `color_scheme` in `~/.config/erikterm/erikterm.toml`. The name is case-insensitive:

  ```toml
  color_scheme = "Rosé Pine"
  ```

- Or open **Browse Themes…** in the palette. Moving through the list previews each scheme live; `Enter` keeps it for
  this session and `Esc` goes back.
- Saving the config applies the new scheme to every open window right away.

Each built-in also sets an accent color for the active tab, focus marks and palette highlights. `[colors] accent`
overrides it, and the rest of `[colors]` overrides the scheme. Your own scheme files and downloads are described in
[Configuration](../configuration.md#color-schemes).

## Contrast

Every built-in scheme meets a minimum contrast floor out of the box, so its text is readable without changing
`minimum_contrast`.

Every built-in scheme brings a default `minimum_contrast` of 4.5 (WCAG AA). erikterm moves a text color toward white
or black, keeping its hue, just far enough to reach 4.5:1 against the background of its own cell, colored blocks
included. That keeps the text of 16-color statuslines and prompts readable in every scheme: black text on a green or
blue block, white text on bright black, bright black text, and any other pair of the 16 colors. In an inactive pane,
which `inactive_pane_hsb` dims, text keeps at least 3:1 against what it is drawn on there (dimming darkens the text
but not the window's background, so erikterm checks again after dimming). Box drawing and powerline glyphs keep their
colors.

The default only applies when your config doesn't set `minimum_contrast`. Your own value always wins, in either
direction: `minimum_contrast = 1` shows a scheme's colors exactly as published, and `high_contrast` still raises it to
7. With a value of your own, inactive panes are dimmed as they are, without the second check. Scheme files from your
colors folder or the catalogue bring no default; set `minimum_contrast` for them yourself.

The tab bar follows the same rule in every scheme: the titles, numbers and badges on the tabs read at 4.5:1 against
their tab, and its icons and dots at 3:1. erikterm moves those colors toward white or black where a scheme's own
colors don't reach that on the tab pills.

No scheme's palette is changed for contrast. erikterm's own schemes (Blossom, Night Blossom and the others marked
"erikterm original") are designed so that their color blocks reach 4.5:1 without the default. Each page lists the
measured contrast of the statusline in erikterm's standard scene, focused and dimmed.

## Dark

| Scheme | Family / origin | License |
|---|---|---|
| [erik (default)](erik.md) | erikterm | MIT (erikterm) |
| [Catppuccin Macchiato](catppuccin-macchiato.md) | Catppuccin | MIT |
| [Catppuccin Mocha](catppuccin-mocha.md) | Catppuccin | MIT |
| [Tokyo Night](tokyo-night.md) | Tokyo Night | Apache-2.0 |
| [Dracula](dracula.md) | Dracula | MIT |
| [Nord](nord.md) | Nord | MIT |
| [Gruvbox Dark](gruvbox-dark.md) | Gruvbox | MIT |
| [Kanagawa Wave](kanagawa-wave.md) | Kanagawa | MIT |
| [GitHub Dark](github-dark.md) | GitHub | MIT |
| [Catppuccin Frappé](catppuccin-frappe.md) | Catppuccin | MIT |
| [Everforest Dark](everforest-dark.md) | Everforest | MIT |
| [Night Owl](night-owl.md) | Night Owl | MIT |
| [Ayu Dark](ayu-dark.md) | Ayu | MIT |
| [One Half Dark](one-half-dark.md) | One Half | MIT |
| [Nightfox](nightfox.md) | Nightfox | MIT |
| [Carbonfox](carbonfox.md) | Nightfox | MIT |
| [Cyberpunk](cyberpunk.md) | Cyberpunk | MIT |
| [Fairy Floss](fairy-floss.md) | Fairy Floss | MIT |
| [LaserWave](laserwave.md) | LaserWave | MIT |
| [Cherry Blossom](cherry-blossom.md) | Cherry Blossom | MIT |
| [Night Blossom](night-blossom.md) | erikterm original | MIT (erikterm) |
| [Graphite & Blush](graphite-and-blush.md) | erikterm original | MIT (erikterm) |
| [Neon Grid](neon-grid.md) | erikterm original | MIT (erikterm) |
| [Abyss & Brass](abyss-and-brass.md) | erikterm original | MIT (erikterm) |
| [Coral Navy](coral-navy.md) | erikterm original | MIT (erikterm) |

## Light

| Scheme | Family / origin | License |
|---|---|---|
| [Gruvbox Light](gruvbox-light.md) | Gruvbox | MIT |
| [Kanagawa Lotus](kanagawa-lotus.md) | Kanagawa | MIT |
| [GitHub Light](github-light.md) | GitHub | MIT |
| [Catppuccin Latte](catppuccin-latte.md) | Catppuccin | MIT |
| [Everforest Light](everforest-light.md) | Everforest | MIT |
| [Ayu Light](ayu-light.md) | Ayu | MIT |
| [Lumina Oxblood Light](lumina-oxblood-light.md) | Lumina | MIT |
| [Blossom](blossom.md) | erikterm original | MIT (erikterm) |
| [Cream & Rose](cream-and-rose.md) | erikterm original | MIT (erikterm) |
