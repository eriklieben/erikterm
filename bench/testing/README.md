# Testing erikterm on Windows and macOS

erikterm is developed and used daily on Linux (Arch, Hyprland/Wayland, 1.5× display scale). This guide is for
checking it on **Windows** and **macOS** against that baseline: a person can follow it, and so can a Claude Code
session on the Windows or Mac machine ("follow bench/testing/README.md and report what differs").

It has two parts:

1. **Reference screenshots** (scripted, reproducible): the same scenes rendered on every platform with the same
   config, saved as PNGs next to the Linux ones in [`reference/`](reference). Compare them side by side.
2. **A manual checklist** per feature area: steps, the expected result, the Linux reference shot where there is one,
   and what is *expected* to differ on Windows/macOS (read from the code, not guessed). Anything that differs
   *and isn't listed as expected* is a finding.

Screen readers (UI Automation, NVDA, Narrator) have a plan of their own: [screen-readers.md](screen-readers.md).

Windows has not been run end to end before, so first-run problems —
DLL loading, font discovery, ConPTY — are likely and worth reporting in detail.

**Contents:** [Setup](#1-setup) · [Reference screenshots](#2-reference-screenshots) ·
[Checklist](#3-checklist) · [Platform differences](#4-expected-platform-differences-summary) ·
[Reporting](#5-reporting) · [How the scenes work](#6-how-the-scenes-work)

---

## 1. Setup

### Get a binary

**CI artifact (easiest).** Every push to `main` builds all three platforms (`.github/workflows/build.yml`).
In GitHub: *Actions → build → the latest run → Artifacts*, or with the GitHub CLI:

```sh
gh run download --repo eriklieben/erikterm -n erikterm-win-x64     # Windows: a zip with erikterm.exe + SDL3/FreeType/HarfBuzz DLLs
gh run download --repo eriklieben/erikterm -n erikterm-osx-arm64   # macOS (Apple silicon): a tar.gz with the binary
```

- Windows: unzip anywhere; the DLLs must stay next to `erikterm.exe` (erikterm loads them only from its own
  folder). Unsigned: SmartScreen may ask once.
- macOS: `tar xzf erikterm-osx-arm64.tar.gz`, then `xattr -dr com.apple.quarantine erikterm` in that folder
  (unsigned build), and install the native libraries below.

**Build from source — Windows.** Native AOT can't cross-compile, so build on the Windows machine:

1. .NET SDK **10.0.403 or newer** (`global.json`; `dotnet --version`), plus the MSVC linker: Visual Studio 2022+
   or *Build Tools for Visual Studio* with the *Desktop development with C++* workload.
2. Native DLLs in `deps\win-x64\`: `SDL3.dll`, `freetype.dll` (or MSYS2's `libfreetype-6.dll`), optionally
   `harfbuzz.dll` (ligatures), plus whatever those depend on. CI gets them from vcpkg:
   ```powershell
   & "$env:VCPKG_INSTALLATION_ROOT\vcpkg.exe" install sdl3 freetype harfbuzz --triplet x64-windows
   New-Item -ItemType Directory -Force deps\win-x64
   Copy-Item "$env:VCPKG_INSTALLATION_ROOT\installed\x64-windows\bin\*.dll" deps\win-x64\
   ```
   (With a standalone vcpkg clone, use its folder instead of `$env:VCPKG_INSTALLATION_ROOT`.)
3. `scripts\publish-windows.ps1` → `artifacts\app-win\erikterm.exe` with every DLL from `deps\win-x64` copied next to it.

**Build from source — macOS.** Xcode command line tools, the .NET 10 SDK, then:

```sh
brew install sdl3 freetype harfbuzz fontconfig
dotnet publish src/ErikTerm.App -c Release -r osx-arm64 -o artifacts/app    # osx-x64 on Intel
```

`fontconfig` matters on macOS: font discovery runs `fc-match` / `fc-list` (only Windows has its own font
locator). Without them erikterm finds no font at all.

### Font

The reference scenes and the user's config use **JetBrainsMono Nerd Font**. Install it before comparing:

- Windows: download *JetBrainsMono* from nerdfonts.com, select the `.ttf` files → *Install* (per user is fine:
  erikterm reads both `HKCU` and `HKLM` font registrations and the per-user fonts folder).
- macOS: `brew install --cask font-jetbrains-mono-nerd-font`.

Without it, erikterm falls back (Windows: Cascadia Mono → Cascadia Code → Consolas → Courier New; macOS: whatever
`fc-match monospace` returns), shows a *Config problem* toast "font: no installed font matches …", and the
screenshots won't line up with Linux. That fallback is itself worth a check once.

### Config

| | Config file | State (session, preferences, crash logs) |
|---|---|---|
| Linux | `~/.config/erikterm/erikterm.toml` | `~/.local/state/erikterm/` (`$XDG_STATE_HOME`) |
| macOS | `~/.config/erikterm/erikterm.toml` (same rule as Linux, not `~/Library`) | `~/.local/state/erikterm/` |
| Windows | `%APPDATA%\erikterm\erikterm.toml` | `%LOCALAPPDATA%\erikterm\` |

`--config-file PATH` or `ERIKTERM_CONFIG_FILE` override the location. To test with a full config, copy
[`erikterm.example.toml`](../../erikterm.example.toml) to the location above and change what you want to try:

```powershell
New-Item -ItemType Directory -Force "$env:APPDATA\erikterm"; Copy-Item erikterm.example.toml "$env:APPDATA\erikterm\erikterm.toml"
```
```sh
mkdir -p ~/.config/erikterm && cp erikterm.example.toml ~/.config/erikterm/erikterm.toml
```

Every option is in [`erikterm.example.toml`](../../erikterm.example.toml). Start erikterm with `--no-restore`
while testing, so a session from an earlier run doesn't change what you see.

---

## 2. Reference screenshots

### Run the scenes

```sh
scripts/reference-shots.sh path/to/erikterm                  # Linux / macOS → bench/testing/reference/<linux|macos>/
```
```powershell
scripts\reference-shots.ps1 path\to\erikterm.exe             # Windows → bench\testing\reference\windows\
scripts\reference-shots.ps1 path\to\erikterm.exe -Scene 03-rendering,05-content   # just some scenes
```

Each scene opens one erikterm window for a few seconds (twelve windows in all, about three minutes), takes its
screenshots and closes it. **Don't type or click while it runs**: keys go to the focused scene window and change
the shots. The scene windows run with `--new-process --no-restore`, their own window class
(`erikterm-refshots`, so they never save over your `session.json`), and on Linux/macOS a private
`XDG_RUNTIME_DIR`/`XDG_STATE_HOME`. On a tiling window manager give the class a floating rule, so the window gets
the size `scene.toml` asks for (`ERIKTERM_SHOT_CLASS=…`, PowerShell `-Class …`, picks another class).

PNG conversion: ImageMagick (`magick`) if installed — recommended, it keeps the alpha channel of
[`transparency.png`](reference/linux/transparency.png) (Windows: `winget install ImageMagick.ImageMagick`); otherwise `sips` on macOS or
System.Drawing on Windows (opaque). `pngquant`, if installed, shrinks the PNGs (Linux/macOS; `REFSHOTS_QUANTIZE=0`
turns that off).

### Compare

Open `reference/linux/X.png` and `reference/<os>/X.png` side by side (or stitch them):

```sh
magick reference/linux/text.png reference/windows/text.png +append /tmp/text-side-by-side.png
```

For a pixel diff, both images must be the same size. Screenshots are in physical pixels: the Linux references
are at **1.5×** (cell 14×40 px at 12 pt). Windows at 150 % should match the size exactly; a Mac at 2× gives
bigger images — resize first:

```sh
magick reference/macos/text.png -resize 1424x1064! /tmp/mac-text.png
magick compare -metric AE -fuzz 8% reference/linux/text.png /tmp/mac-text.png /tmp/text-diff.png   # red = differs
```

Expect differences in glyph rasterization (FreeType settings and hinting are the same, but fonts for emoji and
fallback differ per OS), so read the diff, don't count it: look for missing glyphs (`.notdef` boxes), shifted
baselines, wrong colors, missing overlays.

A Claude Code session can do the comparison itself: run the script, then `Read` each pair of PNGs and report
per scene what differs — see [Reporting](#5-reporting).

### The scenes

| Scene | Screenshots | What it shows |
|---|---|---|
| `01-window` | [`window`](reference/linux/window.png), [`about`](reference/linux/about.png) | Plain window: size from `initial_cols`/`initial_rows`, padding, background, block cursor. Then the About overlay (version, renderer, display, fonts — expected to differ). |
| `02-transparency` | [`transparency`](reference/linux/transparency.png) | `window_background_opacity = 0.8`: the PNG's alpha is 0.8 where the background is. |
| `03-rendering` | [`text`](reference/linux/text.png), [`colors`](reference/linux/colors.png), [`underlines`](reference/linux/underlines.png) | Ligatures, bold/dim/italic/underline/strike/inverse, emoji (incl. skin tone, ZWJ family, flag, keycaps), CJK wide text, combining marks, Nerd Font icons, powerline, box drawing/blocks/braille, wrapping. Then 16 ANSI colors (normal, bright, bold, dim), 256-color cube, grayscale ramp, truecolor gradients. Then the underline styles (single, double, curly, dotted, dashed) in the text color and in SGR 58 colors, overline, and styles on wide text and emoji. |
| `04-overlays` | [`palette`](reference/linux/palette.png), [`context-menu`](reference/linux/context-menu.png), [`search`](reference/linux/search.png), [`quick-select`](reference/linux/quick-select.png) | Command palette filtered to "split", the right-click menu, scrollback search for "error" (4 matches), quick select labels on hashes/URLs/paths/IPs/UUIDs/numbers. |
| `05-content` | [`images`](reference/linux/images.png), [`hyperlink`](reference/linux/hyperlink.png), [`url-hover`](reference/linux/url-hover.png) | The same PNG via kitty graphics and iTerm2 (24×6 cells each) and a sixel; an OSC 8 hyperlink hovered with Ctrl held (underline + target pill); a plain URL hovered. |
| `06-vim` | [`vim-visual`](reference/linux/vim-visual.png), [`vim-help`](reference/linux/vim-help.png) | Vim mode: a visual selection with the `-- VISUAL --` pill; the `g?` cheat sheet. |
| `07-layout` | [`splits`](reference/linux/splits.png), [`zoom`](reference/linux/zoom.png), [`pane-select`](reference/linux/pane-select.png), [`tabs`](reference/linux/tabs.png) | Split right then down (inactive panes dimmed, hollow cursors), zoom, the pane picker (A/S/D), three renamed tabs. |
| `08-agent` | [`agent-status`](reference/linux/agent-status.png), [`agents-overview`](reference/linux/agents-overview.png), [`close-confirm`](reference/linux/close-confirm.png) | Simulated Claude Code (OSC 7501): "needs permission" pill + red rim, "working" pill with progress hairline; the Agents overview; the close confirmation bar for an agent pane. |
| `09-notify` | [`notification-bar`](reference/linux/notification-bar.png), [`osc52-prompt`](reference/linux/osc52-prompt.png) | The notification bar (OSC 777 while unfocused), then the OSC 52 "wants to set the clipboard" bar stacked above it. |
| `10-config` | [`config-toast`](reference/linux/config-toast.png), [`config-problems`](reference/linux/config-problems.png) | Two bad `--set` overrides: the startup toast, then palette › *Show Config Problems*. |
| `12-worktree` | [`agent-worktree`](reference/linux/agent-worktree.png), [`agent-worktree-tab`](reference/linux/agent-worktree-tab.png), [`agent-worktree-exited`](reference/linux/agent-worktree-exited.png), [`agent-worktree-background`](reference/linux/agent-worktree-background.png) | The New Agent Worktree form in a throwaway repository, with fake agents on PATH; the tab it opens (color, name, agent status dot); the same tab after the agent exited (debug step `exit:0`), now a shell; two more launched with Ctrl+Enter from inside that worktree (debug step `ckey:enter`). `scripts/reference-shots.sh` only: the Windows script skips it. |
| `11-status` | [`progress`](reference/linux/progress.png), [`tab-colors`](reference/linux/tab-colors.png), [`held-pane`](reference/linux/held-pane.png), [`command-finished`](reference/linux/command-finished.png) | Four tabs with colors (blue, green, magenta, none) and OSC 9;4 progress — the first tab split in two panes (normal 65 %, error 80 %: the tab's bar is red, each pane has its own along its top edge), indeterminate (still: animations are off), paused; the palette's *Tab Color* entries with swatches; a pane whose program exited with `exit_behavior = "Hold"` (*sleep exited with code 3* `[Restart] [Close]` — Windows names powershell — via the debug step `exit:3`); a command (OSC 133) that ran over `notify_command_duration` finishing while the window is unfocused (*✓ make test finished in 1s* on the notification bar). |
| `18-notify-tabs` | [`notification-other-tabs`](reference/linux/notification-other-tabs.png), [`notification-panel`](reference/linux/notification-panel.png) | The bar scoped to the open tab: its own notification with *+1 in other tabs*; the panel listing every tab with its All / This tab control. |
| `13-tabswitcher` | [`tab-switcher`](reference/linux/tab-switcher.png), [`tab-switcher-filter`](reference/linux/tab-switcher-filter.png), [`tab-quick-tap`](reference/linux/tab-quick-tap.png), [`tab-overflow`](reference/linux/tab-overflow.png), [`tab-overflow-list`](reference/linux/tab-overflow-list.png) | The Ctrl+Tab list while Ctrl is held (most recently used first, the second row selected: colors, agent status, a progress bar, a long title in full), filtered by typing; a quick tap (debug step `ctrl-tap`) that just flips to the previous tab; the tab bar with too many tabs (*N more*, with its dot: a tab out of view has an agent waiting); the list from that button. |
| `15-merge-tool` | [`merge-tool`](reference/linux/merge-tool.png), [`merge-tool-decided`](reference/linux/merge-tool-decided.png) | The conflict resolver in a repository stopped on a merge: two columns over the result, file list, overview strip; the second shot after taking a side. `font_size = 8` (scene) so a tiled window still has the width for the wide layout. |
| `16-merge-compact` | [`merge-compact`](reference/linux/merge-compact.png) | The resolver in a narrow pane: stacked conflict cards. |
| `17-compare` | [`compare`](reference/linux/compare.png), [`compare-rules`](reference/linux/compare-rules.png) | `erikterm compare`: aligned columns, a real change highlighted, comment and whitespace changes dimmed and counted as unimportant; the second shot with the ignore rules open. |
| `18-review` | [`review-palette`](reference/linux/review-palette.png), [`review`](reference/linux/review.png), [`review-commits`](reference/linux/review-commits.png), [`review-tab-reused`](reference/linux/review-tab-reused.png), [`review-finish`](reference/linux/review-finish.png) | **Git: Compare with main** on a worktree with commits, uncommitted work and a base that moved: palette entry; the `compare: <branch>` tab with the file list and compare; the commits view; running it again from tab 1 jumps back (two tabs, not three); `f` opens Finish Worktree over the review. |
| `19-review-narrow` | [`review-narrow`](reference/linux/review-narrow.png) | The review at 80 columns: the file list stacked above the compare. |
| `12-tools` | [`scrollbar-marks`](reference/linux/scrollbar-marks.png), [`scrollbar-search`](reference/linux/scrollbar-search.png), [`broadcast`](reference/linux/broadcast.png), [`theme-browser`](reference/linux/theme-browser.png) | Twelve commands (OSC 133), three failing, scrolled back: prompt ticks and red ticks beside the scrollbar; then a search for "error" (accent ticks, the current match larger); three panes with broadcast input on (a rim and a *Broadcast · 3 panes* pill on each); *Browse Themes…* filtered to "catppuccin". |

The scenes feed their text straight into the terminal parser (the debug script's `feed:` step), so they test
rendering and UI, **not** the PTY. On Windows, run the PTY check from the checklist too (§3.2): printing the
same content through ConPTY can lose sequences that the scenes don't.

---

## 3. Checklist

Start erikterm normally (with your config, or with `--config-file bench/testing/scenes/scene.toml` to match the
references), run each step, and compare with *Expected*. **Platform notes** say what differs by design; everything
else should behave like Linux. Shortcuts are the defaults; on macOS too they use **Ctrl**, not Cmd (there are no
Cmd bindings by default; `SUPER+…` chords can be bound in `[keys]`).

### 3.1 Startup and window

| Step | Expected | Platform notes |
|---|---|---|
| `erikterm --version` (also `erikterm cli --version`) | `erikterm 0.1.0 (<commit>), built <date> UTC` | Windows: `erikterm.exe` is a GUI app; from cmd the output can land after the next prompt — pipe it (`erikterm --version \| more`) or use PowerShell. |
| Start `erikterm --no-restore` | A window with your shell: Linux/macOS `$SHELL`; Windows **pwsh**, else Windows PowerShell, else `%COMSPEC%`. stderr prints `erikterm: config <path>` and the font/cell size. Ref: [`window.png`](reference/linux/window.png) | Windows: the first start is where a missing DLL or a font problem shows. A crash leaves a log (§3.20). |
| Resize the window, drag it to a monitor with another scale | Text reflows; the cell grid stays sharp at the new scale (`EVENT_WINDOW_DISPLAY_SCALE_CHANGED`). | Windows: per-monitor DPI; macOS: Retina 2×. Report the scale in your notes. |
| `window_background_opacity = 0.75` (user config) | The background is see-through, text stays opaque. Palette › *Toggle Transparency* makes it opaque and back. Ref: [`transparency.png`](reference/linux/transparency.png) (alpha 0.8). | Blur is the compositor's: none on Windows/macOS unless the OS adds it. Check that Windows really shows the desktop through (DWM) rather than black. |
| `window_decorations = "NONE"` vs `"RESIZE"` | NONE: no title bar; RESIZE: the OS title bar and borders. | Windows/macOS: check that a NONE window can still be moved/resized somehow (OS-dependent) and report how. |
| `Alt+Enter` / `F11` | Full screen and back. | macOS: Option+Enter may be taken by text input; note what happens. |
| Window icon | Linux: from the desktop entry / X11 icon. | Windows: the exe's icon resource (taskbar, Alt+Tab). macOS: no app bundle, the Dock icon may be generic — expected. |

### 3.2 Text rendering and the PTY

| Step | Expected | Platform notes |
|---|---|---|
| Compare [`text.png`](reference/linux/text.png) | Same layout as Linux: ligatures (`->` `=>` `!=` …), styles, emoji in color and two cells wide, CJK two cells wide, combining marks on their base letter, Nerd Font icons, powerline shapes touching their neighbors, box drawing lines meeting across cells. | Emoji art differs by design: Linux Noto Color Emoji (bitmap CBDT), Windows Segoe UI Emoji (COLR layers), macOS Apple Color Emoji (sbix) — all through FreeType. A `.notdef` box that stays is a bug (fallback is looked up in the background, so a box may show for a moment). |
| Compare [`underlines.png`](reference/linux/underlines.png) (and `undercurl` in [`text.png`](reference/linux/text.png)) | Single, double, curly (an anti-aliased wave, one period per cell, joining across cells), dotted, dashed; colored ones in their SGR 58 color, the rest in the text color; overline at the top of the cell. On HiDPI the wave is as smooth as at 1× (it's drawn at the device cell size). | Same everywhere; a jagged or broken wave is a bug. |
| Run `scripts/install-terminfo.sh` (Linux/macOS), open a new pane: `echo $TERM`, `infocmp -x xterm-erikterm \| grep Smulx` | `xterm-erikterm`, and `Smulx=\E[4:%p1%dm`. Without the entry (or with `term = "xterm-256color"`): `xterm-256color`. | macOS: `tic` from the system ncurses works; the entry lands in `~/.terminfo/78/` (hashed layout) and erikterm finds it there. Windows: TERM isn't set at all (ConPTY); skip. |
| In Neovim (TERM=xterm-erikterm): `:lua vim.diagnostic.set(vim.api.nvim_create_namespace('t'), 0, {{lnum=0,col=0,end_col=3,message='x',severity=1}})` | The first word gets a **curly underline in the diagnostic color** (not a plain underline in the text color). `:set spell` gives curly spelling underlines. | Windows: Neovim talks to ConPTY, not to erikterm; expect plain underlines there. |
| PTY check: in a pane run `bench/testing/scenes/print-vt.sh bench/testing/scenes/content/text.vt` (Windows, in pwsh: `bench\testing\scenes\print-vt.ps1 bench\testing\scenes\content\text.vt`), same for `colors.vt`, `images.vt`, `links.vt` | Looks like the scripted shot (the scripted one has the text at the top; here it follows your prompt). | **Windows: this goes through ConPTY**, which parses and re-renders program output. Write down every difference — dropped images (kitty/iTerm2/sixel), lost OSC 8 links, changed colors or styles. `ERIKTERM_LOG_UNHANDLED=%TEMP%\et-unhandled.log` logs sequences erikterm ignored (ConPTY sends its own, e.g. `?9001h`). |
| `Ctrl+=` / `Ctrl+-` / `Ctrl+0` | Font zooms and resets; the grid stays sharp. | — |
| Fonts: set `font = "Cascadia Code"` (Windows) / `"Menlo"` (macOS) | Applies on save; bold/italic from the family. | Windows: found through the registry + Fonts folders; macOS: through `fc-match`. |

### 3.3 Cursor

| Step | Expected | Platform notes |
|---|---|---|
| User config: `default_cursor_style = "BlinkingBar"`, `cursor_blink_style = "phase"`, `cursor_thickness = "1px"` | A thin bar that pulses smoothly (no hard on/off), 1 px × scale wide. | — |
| Same config, type a few letters in a shell | The bar is the height of the text, not of the whole line. It moves with each letter on the same frame, thickens for an instant and settles: it is never seen on top of a letter. | — |
| `printf '\e[2 q'` / `'\e[4 q'` / `'\e[6 q'` (pwsh: ``Write-Host -NoNewline "`e[2 q"``) | Steady block / underline / bar. | Windows: ConPTY may re-emit its own cursor shape sequences; note if the shape doesn't stick. |
| Type quickly, jump words | The cursor glides on short moves (`cursor_animation`). Unfocused panes: hollow cursor (see [`splits.png`](reference/linux/splits.png)). | — |

### 3.4 Colors and schemes

| Step | Expected | Platform notes |
|---|---|---|
| Compare [`colors.png`](reference/linux/colors.png) | Identical colors (they come from the scheme, not the OS). Bold → bright colors (`bold_brightens_ansi_colors`). | Same everywhere; a difference here is a renderer bug (gamma/sRGB, blending). |
| Palette › type `theme` → arrow through the themes | Live preview while moving; Enter keeps it, Esc restores. | — |
| In a pane: `printf '\e]11;#fdf6e3\a'`, then `printf '\e]111\a'` | The pane's background turns light (the whole pane, not just written cells), then back. Switching theme in between keeps the program's color until the reset. | — |
| In Neovim: `:echo &background`, then switch erikterm between a dark and a light theme (palette › Theme) | Neovim's `background` follows (DECSET 2031: erikterm tells it when the background flips). | — |
| Put a wezterm scheme file in `colors/` (Windows `%APPDATA%\erikterm\colors\`, others `~/.config/erikterm/colors/`), set `color_scheme` to its `[metadata] name` | Applies; editing the scheme file reloads it live. | — |
| Palette › *Browse Themes…*, type a name, arrow through the list | Live preview; `Enter` keeps it, `Esc` restores. Ref: [`theme-browser.png`](reference/linux/theme-browser.png) | — |
| *Browse Themes…* › *Download More Color Schemes…* | A bar asks first; *Download* shows "N of M", then a toast. The themes are in the list, and in `iTerm2-Color-Schemes/` under `colors/` next to the config. | Needs a TLS library at run time (Linux: OpenSSL). |
| `minimum_contrast = 4.5`, then `printf '\e[30mblack \e[2mfaint\e[0m \e[38;2;60;60;80mdark\e[0m ─│█▀\n'` | The three words become readable (lighter, same hue); the box drawing / block characters keep their colors. `minimum_contrast = 1` puts them back. | Same everywhere (it's in the renderer). |

### 3.5 Splits, panes, zoom

| Step | Expected | Platform notes |
|---|---|---|
| `Ctrl+Shift+\` then `Ctrl+Shift+-` | Split right, then down; the new pane is focused; inactive panes dimmed (`inactive_pane_hsb`). Ref: [`splits.png`](reference/linux/splits.png) | New panes start in the focused pane's directory: OSC 7, else the foreground program's directory, else the shell's (Linux `/proc`, macOS libproc, Windows the process's PEB), else `default_cwd`. **Windows/pwsh:** PowerShell doesn't move its process directory on `cd`, so at a pwsh prompt without OSC 7 the split opens where that pwsh started; while a program runs (`nvim` in `C:\src`), it opens in the program's directory. cmd tracks `cd`. |
| `Ctrl+Shift+←↑→↓`, `Ctrl+Shift+Alt+←↑→↓`, drag a divider | Focus moves / divider moves. | — |
| `Ctrl+Shift+Z` | Zoom with a "Zoomed" badge, again to restore. Ref: [`zoom.png`](reference/linux/zoom.png) | — |
| `Ctrl+Shift+O` | Pane picker: a letter per pane. Ref: [`pane-select.png`](reference/linux/pane-select.png) | The label under each letter is the foreground program: Linux `sleep` in the reference; macOS the same; Windows: the foreground program is the newest process under the pane's shell (conhost, GUI programs and processes younger than 250 ms left out), named after its `.exe` (`timeout`, `nvim`). Run `timeout /t 60` in a pwsh pane and check the label. |
| `Ctrl+Shift+W` in a pane at a shell prompt | Closes at once. | — |
| `exit_behavior = "Hold"`, then in a pane `erikterm cli split-pane -- sh -c 'echo bye; exit 3'` | The pane stays: *sh exited with code 3* `[Restart] [Close]` along its bottom, `bye` still visible above it, no cursor. `Enter` restarts it (the output stays, the program runs again below it); `Esc` closes it. Ref: [`held-pane.png`](reference/linux/held-pane.png) | Windows: the status comes from `GetExitCodeProcess` (ConPTY); check the code shown. |
| `exit_behavior = "CloseOnCleanExit"`: `exit 0` in one pane's shell, `exit 1` in another's | The first closes, the second holds. | — |
| Palette › *Restart Pane* on a pane running `top` | Asks *Restart this pane? top is still running*; `Enter` restarts it. | macOS/Windows: like closing, it only asks when the foreground program is known (Linux) or an agent runs. |
| Split twice, `Ctrl+Shift+Alt+I`, type `echo hi`, paste something | Every pane has a rim and a *Broadcast · 3 panes ✕* pill; the text and the paste arrive in all three. Click the pill: back to one pane. Ref: [`broadcast.png`](reference/linux/broadcast.png) | Windows: keys go through ConPTY; check that each pane gets them. |
| Palette › *Broadcast Input to Selected Panes…*, press two letters, `Enter` | Only those two panes get the rim, and input typed in the third stays there. | — |
| Broadcast on in a tab with agent-team teammates (§3.14), type something | Teammates, parked or open, never get the input. | Linux/macOS only (needs the tmux emulation). |

### 3.6 Tabs

| Step | Expected | Platform notes |
|---|---|---|
| `Ctrl+Shift+T` twice, `Ctrl+Tab`, `Ctrl+Shift+1..9`, `Ctrl+Shift+PgUp/PgDn`, middle-click a tab, drag to reorder, double-click to rename | Tab bar appears with the second tab; everything as described. Ref: [`tabs.png`](reference/linux/tabs.png) (renamed tabs) | Default tab titles come from the foreground program; for a shell at its prompt, its OSC 7 directory or the title it set (pwsh: its path). Windows: start `nvim` in one tab — the title should become `nvim` within a second and go back when it exits; with oh-my-posh/starship, the title must not flash the prompt program's name after each command. |
| Output in a background tab | A dot on that tab. | — |
| Three tabs; go 1 → 2 → 3 (Ctrl+Shift+1…3). Hold `Ctrl`, tap `Tab`, keep holding; `Tab` again; `Ctrl+Shift+Tab`; release `Ctrl` | After about 150 ms a list appears: 3, 2, 1 with 2 selected, moving down and up with wrap; releasing switches. Ref: [`tab-switcher.png`](reference/linux/tab-switcher.png) | macOS: Ctrl, not Cmd. |
| Tap `Ctrl+Tab` quickly, twice | No list flashes; the tab flips to the previous one and back. | — |
| Hold `Ctrl+Tab`, type a few letters of a title, `Backspace` one, `Enter`; hold `Ctrl+Tab`, `Esc`; hold `Ctrl+Tab` and switch to another window with the mouse | Filters as you type (the program sees nothing); Enter switches; Esc cancels; leaving the window cancels. | With kitty keyboard protocol programs (Ctrl+Tab stays erikterm's). |
| Hold `Ctrl+Tab`, then close a tab from another shell (`erikterm cli` / middle-click after releasing the hold in a second window); drag a tab out of the window while the list is open | The list loses that entry and never switches to the wrong tab or crashes. | — |
| Two or more tabs: tap `Ctrl` twice quickly on its own; then again; then `Ctrl+C` twice in a shell | The tab list opens and stays up; the second double tap closes it; `Ctrl+C` twice does not open it. | — |
| `ctrl_tab_order = "position"`, reload | `Ctrl+Tab` steps to the next tab in the bar with no list. Palette › *Switch Tab…* still opens the list in bar order. | — |
| 25 tabs in a narrow window | The bar shows those that fit, the active one stays in view when switching (with `Ctrl+Shift+PgUp`/`PgDn` and with `Ctrl+Tab`), the wheel over the bar scrolls; *N more* lists every tab, with a dot when a tab out of view waits. Reordering by drag scrolls at the bar's edges; tear-off and the Windows self-drawn caption buttons still work. Ref: [`tab-overflow.png`](reference/linux/tab-overflow.png), [`tab-overflow-list.png`](reference/linux/tab-overflow-list.png) | — |
| Palette › *Set Tab Color…* (or right-click a tab) › *Blue*; then *Tab Color › Reset*; `erikterm cli set-tab-color red` | The tab takes the color (a tint, its number; the outline when active), the palette shows swatches; Reset removes it. Restart erikterm: the color is restored with the session. Ref: [`tab-colors.png`](reference/linux/tab-colors.png) | — |
| `printf '\e]6;1;bg;red;brightness;255\a\e]6;1;bg;green;brightness;128\a\e]6;1;bg;blue;brightness;0\a'` in a tab without a color; then `printf '\e]6;1;bg;*;default\a'` | The tab turns orange (iTerm2's tab color), then back. | Windows: ConPTY may not pass OSC 6 through — report it. |
| Two tabs; in a split of the first: `printf '\e]9;4;1;40\a'`, in the other pane `printf '\e]9;4;2;90\a'`; in the second tab `printf '\e]9;4;3\a'` | The first tab's bottom hairline is red (error wins), each of its panes has a bar along its top (40 % accent, 90 % red); the second tab's segment glides while the window has focus. `printf '\e]9;4;0\a'` (or the next shell prompt) removes a bar. Ref: [`progress.png`](reference/linux/progress.png) | Windows: ConPTY may handle OSC 9;4 itself (taskbar) rather than pass it on — report what arrives. |

### 3.7 Command palette and context menu

| Step | Expected | Platform notes |
|---|---|---|
| `Ctrl+Shift+P`, type `split` | Fuzzy matches with shortcuts. Ref: [`palette.png`](reference/linux/palette.png) | — |
| Right click in a pane | Context menu (Copy disabled without a selection). Ref: [`context-menu.png`](reference/linux/context-menu.png) | — |
| Palette › *Show Key Bindings (in a new pane)* | A pane listing the effective bindings. | — |
| With shell integration, run a few commands, `cd` around, then the palette: type `run again` / `go to` | *Run again › cmd*: `Enter` types it without running it, `Shift+Enter` runs it. *Go to › dir*: `Enter` does `cd`, `Alt+Enter` opens a new tab there. A command typed with a leading space isn't listed. Restart erikterm: the list is still there. | **Windows: needs OSC 133 / OSC 7** (oh-my-posh); PowerShell gets `Set-Location -LiteralPath`. |
| `[snippets] x = "echo {{cwd}}"` in the config, palette › *Snippet › x* | Inserted as a paste, with the directory filled in. `Shift+Enter` also runs it. | — |

### 3.8 Search and quick select

| Step | Expected | Platform notes |
|---|---|---|
| `Ctrl+Shift+F`, type a word | Matches highlighted, "N of M", Enter/↑ older, Shift+Enter/↓ newer, `Alt+C` case, `Alt+R` regex, Esc closes. Ref: [`search.png`](reference/linux/search.png) | — |
| With shell integration: run commands, one failing, scroll back | Ticks beside the scrollbar: prompts, failures in red; with the search bar open, its matches too. A click on a tick scrolls there; dragging the thumb scrolls; the pointer near the right edge reveals it. Ref: [`scrollbar-marks.png`](reference/linux/scrollbar-marks.png), [`scrollbar-search.png`](reference/linux/scrollbar-search.png) | **Windows: needs OSC 133** (search ticks don't). |
| `Ctrl+Shift+Space` | Labels on URLs, paths, hashes, IPs, UUIDs, numbers; a label copies, Shift+label pastes, Ctrl+label opens. Ref: [`quick-select.png`](reference/linux/quick-select.png) | Opening uses `xdg-open` / `open` (macOS) / `explorer.exe` (Windows). |

### 3.9 Copy, paste, OSC 52

| Step | Expected | Platform notes |
|---|---|---|
| Select with the mouse, `Ctrl+Shift+C`, paste elsewhere; `Ctrl+Shift+V` | Round trip through the system clipboard; double/triple click selects word/line. | — |
| Middle click / `Shift+Insert` | Pastes the primary selection. | Primary selection is an X11/Wayland thing: on Windows/macOS SDL keeps it inside the app — expect it to work only within erikterm; report what happens. |
| Paste text with an escape sequence into a shell | Control characters removed (toast says so). | — |
| `printf '\e]52;c;SGVsbG8=\a'` (pwsh: ``Write-Host -NoNewline "`e]52;c;SGVsbG8=`a"``) | A bar: "… wants to set the clipboard (5 characters)" with Allow / Deny / Always for this pane. Allow → clipboard holds `Hello`. Ref: [`osc52-prompt.png`](reference/linux/osc52-prompt.png) | Windows: if no bar appears, ConPTY swallowed OSC 52 — a finding. The program name in the bar is the foreground process (Windows: the `.exe` name without the extension). |

### 3.10 Vim mode

| Step | Expected | Platform notes |
|---|---|---|
| `Ctrl+Shift+X`, then `gg`, `5j`, `w`, `v`, `e`, `y` | Own cursor, `-- NORMAL --` / `-- VISUAL --` pill, "Copied N characters". Ref: [`vim-visual.png`](reference/linux/vim-visual.png) | — |
| `g?` | The cheat sheet. Ref: [`vim-help.png`](reference/linux/vim-help.png) | — |
| `:theme tok` | Live theme preview while typing. | — |
| `gO` | The scrollback opens read-only in Neovim (if installed). | The export file goes to `$XDG_RUNTIME_DIR/erikterm/exports` or the temp directory (Windows: `%TEMP%\erikterm\exports`). |

### 3.11 Images

| Step | Expected | Platform notes |
|---|---|---|
| Compare [`images.png`](reference/linux/images.png) | kitty and iTerm2 images identical in size (24×6 cells); the sixel is 120×60 **pixels**, so smaller next to the text on a HiDPI screen. | — |
| PTY check with `content/images.vt` (§3.2) | Same as the scripted shot. | **Windows: the key ConPTY test.** If ConPTY drops APC/DCS/OSC 1337, images won't arrive from real programs (Claude Code's images, `chafa`, `timg`) even though the renderer handles them. |
| kitty file transmission (`allow_kitty_file_transmission = true`, `kitty +kitten icat` if available) | Works for `t=f` and `t=t`. | Shared memory (`t=s`) is Linux only. |

### 3.12 Hyperlinks and file links

| Step | Expected | Platform notes |
|---|---|---|
| Hover an OSC 8 link (`links.vt`), hold Ctrl | Underline, link lights up, target pill at the bottom. Ref: [`hyperlink.png`](reference/linux/hyperlink.png) | — |
| Hover a plain URL; Ctrl+click | Underlined (ref: [`url-hover.png`](reference/linux/url-hover.png)); opens in the browser. | Windows: `explorer.exe <url>`; macOS: `open`. |
| `Ctrl+click` on `README.md:46` in a repo, with `editor_open = "system"` | Opens the file with the default app. | Windows: uses `explorer.exe` (fixed in this round — it used to call `xdg-open`). `editor_open = "nvim"` needs `nvim` on PATH. |

### 3.13 Notifications and the notification bar

| Step | Expected | Platform notes |
|---|---|---|
| In a split, `sleep 3; printf '\e]777;notify;Done;Build finished\a'` (pwsh: ``Start-Sleep 3; Write-Host -NoNewline "`e]777;notify;Done;Build finished`a"``), focus the other pane right away | Bar under the tab bar: bell, where it came from, title + message, "just now", **Jump**, "1 unread", ✕. Ref: [`notification-bar.png`](reference/linux/notification-bar.png) | — |
| Same with the erikterm window unfocused | Linux: the first time a bar asks "… wants to send desktop notifications"; Allow → desktop notifications via `notify-send`. | **macOS/Windows: no desktop notifications** (only `notify-send` is supported), so no question bar and no OS popups; the in-window bar works. |
| An agent waiting while the window is unfocused (`agent_urgency`) | The taskbar entry flashes / requests attention. | Windows: taskbar flash; macOS: Dock bounce; check both. |
| Palette › *Notifications…* (or `Ctrl+Shift+B`) | History of every tab, each row naming its tab; Enter jumps, Del dismisses; `T` or the **All / This tab** control narrows it. Ref: [`notification-panel.png`](reference/linux/notification-panel.png) | — |
| Two tabs; in tab 2 `sleep 3; printf '\e]777;notify;Done;Build finished\a'`, switch to tab 1 right away, focus in tab 1 | At 3 s tab 2 shows the unread marker; the bar says *New in other tabs* with **+1 in other tabs** (click: the panel), and leaves after `notification_bar_timeout_ms`. A notification from tab 1 itself takes the bar and keeps the chip. `notification_bar_scope = "all"` shows every window's as before. Ref: [`notification-other-tabs.png`](reference/linux/notification-other-tabs.png) | — |
| With shell integration (§3.21): in a split `sleep 12; false`, focus the other pane right away | After 12 s: *✗ sleep 12; false exited 1 after 12s* on the bar; **Jump** focuses the pane. `notify_command_duration = 0` turns it off. Ref: [`command-finished.png`](reference/linux/command-finished.png) | **Windows: needs OSC 133**, which the PowerShell integration sends (§3.21). |

### 3.14 Coding agents (Claude Code)

| Step | Expected | Platform notes |
|---|---|---|
| Compare [`agent-status.png`](reference/linux/agent-status.png), [`agents-overview.png`](reference/linux/agents-overview.png) | Pills "Claude · needs permission" (red, red rim) and "Claude · working" (yellow, progress hairline); `Ctrl+Shift+A` lists both, waiting first. | Rendering only; same everywhere. |
| Run real Claude Code (2.1+) in a pane, give it a task that asks for permission | Tab dot + pill turn yellow while working, red when it asks, blue when done out of sight; tab named "Claude: <task>". | Agents are known from the foreground process **and** OSC 7501 (and hooks via `erikterm cli set-agent-status`). **Windows:** check both installs — the native `claude.exe` and npm's (`claude.cmd` → `node …\@anthropic-ai\claude-code\cli.js`): each must show as Claude even with MCP servers running (those are Claude's children; the pane reports Claude, not them). `erikterm cli list` shows the pane's foreground name. If the status itself never changes, check whether ConPTY passes OSC 7501 through (`ERIKTERM_LOG_UNHANDLED` won't list it if it never arrives); report it. |
| `Shift+Enter` in Claude Code's prompt | A new line, not submit. | Depends on the agent being identified (see above). |
| Select text, right click › *Send Selection to Agent…*, pick the agent | It lands in the agent's input box and focus jumps there; `Ctrl+Enter` in the picker also submits and stays put. Without a selection, palette › *Send to Agent…* asks for a prompt after you pick. Agents in other windows are listed too. | Depends on the agent being identified (see above). |
| `erikterm cli statusline` as Claude Code's status line ([guide](../../docs/guide/coding-agents.md)) | Cost and context in the pill. | Needs `erikterm cli` (§3.19). |
| tmux emulation: in a pane `echo $TMUX`, `tmux -V`, `tmux split-window -h` | `$TMUX` set, `tmux` is erikterm's shim, the split opens an erikterm pane. | **Not on Windows** (no `$TMUX`, no palette entry *Toggle tmux Emulation*). macOS: should work — check `tmux split-window` really goes to erikterm (the shim relies on `argv[0]` being `tmux`). |
| Agent teams (`CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1`, teammate mode tmux) | Teammates as pills in a strip under the lead pane; click opens/parks. | Linux/macOS only (needs the tmux emulation). |

| `Ctrl+Shift+G` in a git repository; type a prompt, `Tab`, `Enter` | The form lists agents found on PATH, follows the prompt with a branch name and previews the exact command. A new colored tab named after the branch runs the agent in `../REPO.worktrees/BRANCH`, with a status dot. Ref: [`agent-worktree.png`](reference/linux/agent-worktree.png), [`agent-worktree-tab.png`](reference/linux/agent-worktree-tab.png) | Windows: npm `.cmd` agents aren't offered (expected). |
| Same, `Ctrl+Enter` twice with different prompts | Two background tabs appear, you stay in the current tab, and the form stays open for the next. | — |
| Quit the agent in a worktree tab | A dim *agent exited* line, then your shell, in the worktree; the tab stays. | — |
| Launch with a prompt, restart erikterm | The tab comes back as a shell in the worktree: the agent doesn't start again. *Restart Pane* also gives a shell. | — |
| Set `git_worktree_setup = "false"`, launch | `$ false`, then a red *setup failed* line, a shell, and no agent. | Windows runs it through `cmd.exe`. |
| Set `worktree_open_in = "split"`, launch; or `Alt+Enter` with `"tab"` | The agent opens in a split next to the focused pane, in the worktree; the tab's name and color don't change. Quit the agent: your shell, in the worktree. Restart Pane: a shell. Ref: [`agent-worktree-split.png`](reference/linux/agent-worktree-split.png) | — |
| `Ctrl+Shift+H` in a repository with worktrees | One row per worktree with branch, dirty/ahead/behind and agent; `Enter` jumps to an open tab or opens one. Ref: [`worktrees-switcher.png`](reference/linux/worktrees-switcher.png) | — |
| `f` on a linked worktree | Target, commits, files, the merge-tree prediction, the options, the message, the exact commands. With a dirty main worktree it refuses and says why. Ref: [`finish-worktree.png`](reference/linux/finish-worktree.png) | Conflict prediction needs git 2.38; older says so. |
| Confirm a squash merge in a repository with a signing key | A split shows `git merge --squash`, then `git commit`; a refused touch stops with **Retry**, which only re-runs the commit. On success it offers to remove the worktree and branch. | — |
| Finish, then *Git: Clean Up Worktrees…* | The landed worktree is listed with why; a dirty one is listed as kept with the reason. Ref: [`worktrees-cleanup.png`](reference/linux/worktrees-cleanup.png) | — |
| Start a rebase that stops on a conflict | Info bar: "Rebasing 1/2 · 1 conflict" [Next conflict] [Continue] [Abort]; Next conflict opens the file at its marker; Abort asks first. Ref: [`merge-bar.png`](reference/linux/merge-bar.png) | — |
| In a repository stopped on a merge conflict, click **Open Compare** on the merge bar | A new tab (a split with `worktree_open_in = "split"`) runs the resolver. Ref: [`merge-tool.png`](reference/linux/merge-tool.png). `o`, `t`, `b`, `u`, `r`, `n`, `p`, mouse clicks on the buttons and conflicts; `Ctrl+S` saves and stages only that file and the merge is never concluded; `q` quits; `git diff --name-only --diff-filter=U` shows the file is resolved. Narrow the pane below 100 columns: cards instead of columns. `git mergetool` with `mergetool.erikterm.cmd` set (README, *Compare and merge*) opens one file at a time and moves on after `Ctrl+S`. | **Windows, macOS**: raw mode and resize are written per OS but only run on Linux here; check Ctrl+C, resize and that the terminal is restored after quitting. |
| `erikterm compare a b` on two versions of a file; `erikterm compare main:path HEAD:path` in a repository; a binary file; a 100k-line file | Aligned columns with gap rows, word-level highlights, dimmed noise counted separately; `i` opens the rules (`1 2 3` toggle), `d` cycles All / Differences / Context, `]` `[` jump; `h` `l` pan; binary and huge files show a message; a large file opens within a second and keys keep working while it loads. Ref: [`compare.png`](reference/linux/compare.png). | **Windows, macOS**: as for the resolver above. |
| In a linked worktree with commits and uncommitted changes, palette **Git: Compare with main**; `]` until the next file; `u`; `c` then Enter on a commit, Esc back; `b` another base; run it again from another tab; `f` | A tab `compare: <branch>`; files A/M/D/R with +/- counts, dots on uncommitted ones; `]` walks changes and moves to the next file; `u` hides the work tree and the label says so; a commit shows just its files; another base re-diffs; the same tab comes forward; Finish Worktree opens. On `main`: "nothing to compare" and a base offer. No `origin`: no note, `F` says why. Behind origin: the note, and `F` (the fetch is shown while it runs). `git status` and `.git/index` are unchanged after a session without `F`. Ref: [`review.png`](reference/linux/review.png). | **Windows, macOS**: as for the resolver above; `F` needs a non-interactive ssh or https credential (a prompt is refused, not shown). |
| A branch that would conflict with the default branch, `git_conflict_check = true` | A small warning on its tab within a minute; it goes away when the conflict does. | — |
| `Ctrl+Shift+G` outside a repository; or a branch that exists | A toast says it isn't a repository; the form says which name to try and refuses Enter. | — |

### 3.15 Close confirmation

| Step | Expected | Platform notes |
|---|---|---|
| Run `nvim` (or `less`, `top`) in a pane, `Ctrl+Shift+W` | Linux: info bar "Close this pane? nvim is still running" `[Close Pane] [Cancel]`; Enter/`y` closes, Esc/`n` cancels. | macOS and Windows: the same as Linux. **Windows extras:** `notepad` started from pwsh doesn't count (a GUI program isn't attached to the console and outlives the pane), nor does a shell started inside the shell (`cmd` in pwsh: closes at once). An elevated child (erikterm itself not elevated) still counts by name, but its arguments and directory can't be read (`erikterm cli list` still names it). |
| `Ctrl+Shift+W` on a pane running Claude Code | Asks: "Close this pane? Claude is still running". Ref: [`close-confirm.png`](reference/linux/close-confirm.png) | Works on all platforms once Claude Code is identified (by its process or OSC 7501). |
| Close the window with the OS close button while an agent runs | One question for the whole window ("Quit erikterm? …"). | — |

### 3.16 Config live reload and problems

| Step | Expected | Platform notes |
|---|---|---|
| Change `font_size` in the config and save | Applies in every window; toast "Configuration reloaded". `Ctrl+Shift+R` forces a reload. | Windows: editors that save via rename/replace must still trigger it (FileSystemWatcher); try Notepad and VS Code. |
| Add `font_size = "huge"` and `no_such_option = 1` | A *Config problem* toast with the first one; palette › *Show Config Problems* lists both with line numbers. Ref: [`config-toast.png`](reference/linux/config-toast.png), [`config-problems.png`](reference/linux/config-problems.png) (those come from `--set`) | — |
| Save a half-written file (syntax error) | Settings stay as they were; stderr says so. | — |
| Make the config file a symlink (Windows: `mklink`, needs developer mode or admin) and edit the target | Reloads (the target's directory is watched too). | Windows symlinks are optional; skip if not allowed. |

### 3.17 Session restore

| Step | Expected | Platform notes |
|---|---|---|
| Two windows, tabs, splits, a zoomed pane, a renamed tab; quit; start `erikterm` | "Restored N tabs, M panes", same layout, sizes, font zoom. | Session file: Windows `%LOCALAPPDATA%\erikterm\session.json`, others `~/.local/state/erikterm/session.json`. Window **position** is restored on Windows/macOS (not on Wayland). |
| Panes were in different directories | Restored there. | macOS: as Linux (libproc). Windows: OSC 7, else the shell's process directory — right for cmd, but where it started for pwsh (see §3.5). |
| `nvim` was running (in `restore_programs`) | Linux: re-run in its directory. | macOS: as Linux. **Windows: recorded (name and argv in `session.json`) but never re-run** — there is no POSIX shell to hand back to; the pane gets its shell in the saved directory. |
| Claude Code was running with `erikterm cli statusline` set up; quit; start `erikterm` | A *Resume 1 agent* pill at the bottom and nothing started. Click it: `claude --resume ID` runs in the restored pane; quit it and the shell comes back. ✕ instead: no pill on the next start either. | Windows: needs a real `claude.exe` (an npm shim gets a "isn't on PATH" toast). |

### 3.18 Multiple windows and tear-off

| Step | Expected | Platform notes |
|---|---|---|
| `Ctrl+Shift+N`; run `erikterm` again from a pane | A second window in the same process (the second launch forwards over the control socket). | Windows: the socket is an AF_UNIX socket in `%LOCALAPPDATA%\erikterm` (Windows 10 1803+). |
| Drag a tab out of the tab bar | A ghost follows; dropped outside → new window, over another window's tab bar → moves there. | Windows/macOS: the insertion marker shows on the *other* window while dragging, and the new window opens where you drop (on Wayland it doesn't). |
| Tab right-click › *Move Tab to New Window* | Live panes move, nothing restarts. | — |

### 3.19 `erikterm cli`

| Step | Expected | Platform notes |
|---|---|---|
| In a pane: `erikterm cli list`, `erikterm cli split-pane --right -- <program>`, `erikterm cli send-text --no-paste "echo hi\r"`, `erikterm cli get-text` | Table of panes; a split; text typed; screen text printed. | Windows: run from pwsh in an erikterm pane (it has `ERIKTERM_SOCKET`/`ERIKTERM_PANE`). The exe is a GUI app: pipe output (`erikterm cli list \| Out-String`) if it lands after the prompt. The socket directory's permission checks are Linux-only by design. |
| `erikterm ssh HOST`, then `echo $TERM`, `infocmp -x xterm-erikterm \| head -2` on the remote; log out and repeat | First time: a note on stderr and `~/.terminfo` on the remote has the entry; `TERM=xterm-erikterm`. Second time: no note (cached in the state directory). A host without `tic`: `TERM=xterm-256color`. | Windows: needs OpenSSH's `ssh.exe` on PATH; ssh runs as a child instead of replacing the process. |

### 3.20 About, logs, crashes

| Step | Expected | Platform notes |
|---|---|---|
| Palette › *About erikterm* | Version, commit, renderer, display, fonts, session, licenses; `Ctrl+C` copies diagnostics. Ref: [`about.png`](reference/linux/about.png) | Renderer differs by design (Linux OpenGL; Windows likely Direct3D; macOS Metal). **Attach the copied diagnostics to every report.** |
| Crash log | Internal errors: *Internal error (logged)* instead of a crash; the log: | Windows `%LOCALAPPDATA%\erikterm\crash-*.log`; Linux/macOS `~/.local/state/erikterm/crash-*.log`. About shows the latest. |

### 3.21 Shell integration

| Step | Expected | Platform notes |
|---|---|---|
| Default shell zsh/bash/fish, `shell_integration = "auto"` (the default), no dotfile edits: run commands, one failing | `Shift+↑/↓` jump between prompts, failed commands get a red margin bar, new splits open in the same directory. | macOS: same as Linux. Windows: see the next row. |
| **Windows:** default shell pwsh (and once with `default_prog = ["powershell.exe"]`), `auto`: run `Get-Item nope`, `cmd /c exit 3`, `dir`, then `cd C:\Windows` and split | The same as Linux: prompt jumps, red bars on the two failures, *Run again* lists the commands, the split opens in `C:\Windows`. With oh-my-posh or starship the prompt looks as before and still shows the failure. | **The key question is whether ConPTY passes OSC 133 and OSC 7 through.** If nothing is marked, run with `ERIKTERM_LOG_UNHANDLED=%TEMP%\et-unhandled.log`: no 133/7 lines there and no marks means ConPTY dropped them — a finding (try `ERIKTERM_CONPTY=system` too). |
| `shell_integration = "off"`, or source `contrib/shell/erikterm.<shell>` ([guide](../../docs/guide/shell-integration.md)) from the rc as well (PowerShell: `. …\erikterm.ps1` at the end of `$PROFILE`) | `off`: no marks unless the rc sources the script (then as before). `auto` + already sourced: nothing marked twice. | — |

---

## 4. Expected platform differences (summary)

| Area | Linux | macOS | Windows |
|---|---|---|---|
| PTY | `posix_openpt` | `posix_openpt` | ConPTY (inbox `CreatePseudoConsole`): re-renders output; may drop sequences |
| Default shell | `$SHELL` | `$SHELL` | pwsh → powershell → `%COMSPEC%` |
| Foreground program name (tab titles, pane picker, close confirmation, session programs, agent detection by process) | yes (`/proc`) | yes (libproc, `sysctl`) | yes (process tree: newest process under the shell; argv and directory from the PEB) |
| Agent status | process + OSC 7501 + hooks | process + OSC 7501 + hooks | process + OSC 7501 (if ConPTY passes it) + hooks |
| tmux emulation, teammate pills | yes | yes (verify) | no |
| Desktop notifications | `notify-send` | no (bar only) | no (bar only) |
| Font discovery | `fc-match` | `fc-match` (brew `fontconfig`) | registry + Fonts folders |
| Color emoji | CBDT (Noto) | sbix (Apple) | COLR (Segoe UI Emoji) |
| Session: re-run programs | yes | yes | recorded, not re-run |
| New pane directory | OSC 7, `/proc` | OSC 7, libproc | OSC 7, the PEB (pwsh at its prompt: where it started) |
| Kitty shared-memory images | yes | no | no |
| Window icon | desktop entry / X11 | generic (no bundle) | exe resource |
| Tear-off marker / drop position | X11 yes, Wayland no | yes | yes |
| Config / state | `~/.config`, `~/.local/state` | same | `%APPDATA%`, `%LOCALAPPDATA%` |

---

## 5. Reporting

Per platform, one report with:

1. **Machine**: OS version, display scale, GPU; About › *Copy diagnostics* output.
2. **Setup problems**: anything that needed a workaround to start (DLLs, fonts, quarantine, SmartScreen).
3. **Screenshots**: the `reference/<os>/` folder (commit it on a branch, or attach), plus per scene one line:
   *same* / *differs: …* (what, where).
4. **Checklist**: per section, failures and surprises only, each with the step, what happened, and whether the
   *Platform notes* predicted it. Unexpected differences first.
5. **Logs**: crash logs, stderr of the run, and for Windows ConPTY questions an `ERIKTERM_LOG_UNHANDLED` log.

For a Claude Code session doing this: run §2 first and compare the images yourself (`Read` both PNGs), then go
through §3 with the user for anything that needs a real keyboard/mouse; don't change erikterm's code, report.

---

## 6. How the scenes work

- `scenes/scene.toml` — the fixed config (font, size 100×26 cells, scheme, no animations, no git/session/desktop
  notifications). The runners put `default_prog` in front of it: a program that prints nothing and waits
  (`sleep 600`; Windows `powershell … Start-Sleep 600`), so every pane is empty and the same on every OS.
- `scenes/NN-name.txt` — debug scripts for `ERIKTERM_DEBUG_SCRIPT_FILE` (one step per line, every 350 ms; see the
  [*Debugging*](../../docs/development/README.md#debugging) in the developer docs: `action:`, `type:`, `key:`, `feed:`, `vim:`, `mouse:`, `ctrl:`, `menu`, `unfocus`,
  `sleep:`, `shot:` …). The runners add three things: `# set: KEY=VALUE` lines become `--set` overrides,
  `feedfile:NAME.vt` becomes one `feed:` step with that content file, and `{out}` becomes the temporary BMP
  directory. Screenshots are `shot:{out}/NAME.bmp` → `reference/<os>/NAME.png`.
- `scenes/content/*.vt` — the test content, one terminal line per file line, in C# escape notation (`\e` = ESC,
  `\a` = BEL, `\\` = backslash). Generated by `scenes/make-content.py` (the image data too); edit that and rerun it.
- `scenes/print-vt.sh` / `print-vt.ps1` — print a `.vt` file through the PTY (§3.2).
- Screenshots come from `ERIKTERM_SCREENSHOT` / `shot:` — the renderer reads back the frame it just drew
  (`SDL_RenderReadPixels`) and writes a BMP (`SDL_SaveBMP`); no OS screenshot tool, so window decorations and the
  desktop behind a transparent window are not in the picture.
