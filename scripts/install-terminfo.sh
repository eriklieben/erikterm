#!/usr/bin/env bash
# Installs the xterm-erikterm terminfo entry (contrib/terminfo/erikterm.terminfo) for the current user, so
# erikterm's default `term = "auto"` sets TERM=xterm-erikterm and programs (Neovim, tmux) use styled and colored
# underlines, true color, OSC 52 and the rest. Panes opened after this get it; no restart needed.
#
#   scripts/install-terminfo.sh          # into ~/.terminfo
#   scripts/install-terminfo.sh DIR      # into DIR instead (then add DIR to TERMINFO_DIRS, or use it for testing)
#
# The entry is plain xterm-256color plus extensions, so nothing breaks where it's installed. Where it isn't (another
# machine over ssh), programs complain about an unknown terminal: copy it there with
#   infocmp -x xterm-erikterm | ssh HOST 'mkdir -p ~/.terminfo && tic -x -o ~/.terminfo /dev/stdin'
# or set `term = "xterm-256color"` in erikterm.toml.
set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
src=$root/contrib/terminfo/erikterm.terminfo
out=${1:-$HOME/.terminfo}

command -v tic >/dev/null || { echo "install-terminfo: tic not found (install ncurses)" >&2; exit 1; }
[[ -f $src ]] || { echo "install-terminfo: $src is missing" >&2; exit 1; }

mkdir -p "$out"
tic -x -o "$out" "$src"
# Check it compiled where we said, the way programs will look it up.
TERMINFO=$out infocmp -x xterm-erikterm >/dev/null
echo "installed xterm-erikterm into $out"
