# Third-party notices

erikterm is MIT licensed (see `LICENSE`). It uses the libraries below. On Linux and macOS they come from your system.
The Windows download includes them as DLLs, and the full license text of each DLL is in its `licenses/` folder.

| Library | License | Project |
|---|---|---|
| SDL3 | zlib | https://libsdl.org |
| FreeType | FreeType License (FTL) | https://freetype.org |
| HarfBuzz | MIT ("Old MIT") | https://harfbuzz.github.io |
| Windows console host (`conpty.dll`, `OpenConsole.exe`; Windows download only) | MIT | https://github.com/microsoft/terminal |
| .NET runtime (compiled in) | MIT | https://dotnet.microsoft.com |

The Windows download also includes the libraries FreeType is built with in vcpkg. erikterm doesn't call them itself.

| Library (Windows download only) | DLLs | License | Project |
|---|---|---|---|
| libpng | `libpng16.dll` | PNG Reference Library License version 2 | http://www.libpng.org/pub/png/libpng.html |
| zlib | `z.dll` | zlib | https://zlib.net |
| bzip2 | `bz2.dll` | bzip2 license (BSD-style) | https://sourceware.org/bzip2/ |
| Brotli | `brotlicommon.dll`, `brotlidec.dll`, `brotlienc.dll` | MIT | https://github.com/google/brotli |

Compiled into the binary as data:

| What | License | Project |
|---|---|---|
| Unicode Character Database (character widths, emoji properties) | Unicode License v3 | https://www.unicode.org/license.txt |
| The colours of the built-in schemes *Catppuccin Macchiato* and *Catppuccin Mocha*, copyright © 2021 Catppuccin | MIT | https://github.com/catppuccin/catppuccin |
| The colours of the built-in scheme *Tokyo Night*, from tokyonight.nvim | Apache License 2.0 (https://www.apache.org/licenses/LICENSE-2.0) | https://github.com/folke/tokyonight.nvim |
| The colours of the built-in scheme *erik*, from Atom One Dark (one-dark-syntax), Copyright © 2016 GitHub Inc. | MIT | https://github.com/atom/one-dark-syntax |
| The colours of the built-in scheme *Ayu Dark*, from ayu for VS Code, Copyright © 2016 Ike Kurghinyan | MIT | https://github.com/ayu-theme/vscode-ayu |
| The colours of the built-in scheme *Ayu Light*, from ayu for VS Code, Copyright © 2016 Ike Kurghinyan | MIT | https://github.com/ayu-theme/vscode-ayu |
| The colours of the built-in scheme *Carbonfox*, from nightfox.nvim, Copyright © 2021 James Simpson | MIT | https://github.com/EdenEast/nightfox.nvim |
| The colours of the built-in scheme *Catppuccin Frappé*, from Catppuccin for wezterm, Copyright © 2021 Catppuccin | MIT | https://github.com/catppuccin/wezterm |
| The colours of the built-in scheme *Catppuccin Latte*, from Catppuccin for wezterm, Copyright © 2021 Catppuccin | MIT | https://github.com/catppuccin/wezterm |
| The colours of the built-in scheme *Cherry Blossom*, from Cherry Blossom, Copyright © 2025 daitsuku | MIT | https://github.com/daitsuku/cherry-blossom-vscode-theme |
| The colours of the built-in scheme *Cyberpunk*, from cyberpunk-iterm, Copyright © Merlijn Vos (Murderlon) | MIT | https://github.com/Murderlon/cyberpunk-iterm |
| The colours of the built-in scheme *Dracula*, from Dracula for wezterm, Copyright © 2022 Dracula Theme | MIT | https://github.com/dracula/wezterm |
| The colours of the built-in scheme *Everforest Dark*, from Everforest, Copyright © 2019 sainnhe | MIT | https://github.com/sainnhe/everforest |
| The colours of the built-in scheme *Everforest Light*, from Everforest, Copyright © 2019 sainnhe | MIT | https://github.com/sainnhe/everforest |
| The colours of the built-in scheme *Fairy Floss*, from fairyfloss, Copyright © Amy Wibowo (sailorhg) | MIT | https://github.com/sailorhg/fairyfloss |
| The colours of the built-in scheme *GitHub Dark*, from GitHub VS Code theme (Primer), Copyright © 2020 Primer | MIT | https://github.com/primer/github-vscode-theme |
| The colours of the built-in scheme *GitHub Light*, from GitHub VS Code theme (Primer), Copyright © 2020 Primer | MIT | https://github.com/primer/github-vscode-theme |
| The colours of the built-in scheme *Gruvbox Dark*, from gruvbox, By Pavel Pertsev (morhetz) | MIT | https://github.com/morhetz/gruvbox |
| The colours of the built-in scheme *Gruvbox Light*, from gruvbox, By Pavel Pertsev (morhetz) | MIT | https://github.com/morhetz/gruvbox |
| The colours of the built-in scheme *Kanagawa Lotus*, from kanagawa.nvim, Copyright © 2021 Tommaso Laurenzi | MIT | https://github.com/rebelot/kanagawa.nvim |
| The colours of the built-in scheme *Kanagawa Wave*, from kanagawa.nvim, Copyright © 2021 Tommaso Laurenzi | MIT | https://github.com/rebelot/kanagawa.nvim |
| The colours of the built-in scheme *LaserWave*, from LaserWave, Copyright © 2019 Jared Jones | MIT | https://github.com/Jaredk3nt/laserwave |
| The colours of the built-in scheme *Lumina Oxblood Light*, from Lumina themes, Copyright © 2026 Razik Ikhlef | MIT | https://github.com/RazikSF/lumina-themes |
| The colours of the built-in scheme *Night Owl*, from Night Owl, Copyright © 2018 Sarah Drasner | MIT | https://github.com/sdras/night-owl-vscode-theme |
| The colours of the built-in scheme *Nightfox*, from nightfox.nvim, Copyright © 2021 James Simpson | MIT | https://github.com/EdenEast/nightfox.nvim |
| The colours of the built-in scheme *Nord*, from Nord for Alacritty, Copyright © 2016-present Sven Greb | MIT | https://github.com/nordtheme/alacritty |

Portions of this software are copyright © The FreeType Project (https://freetype.org). All rights reserved.
