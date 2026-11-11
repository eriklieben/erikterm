#!/usr/bin/env bash
# Contact sheet of N evenly spaced frames (in time, when assemble.py left its seq/ directory) of a clip (for checking): sheet.sh WORK_FRAMES_DIR OUT.png [N]
set -euo pipefail
d=$1; out=$2; n=${3:-12}
[[ -d $d/seq ]] && d=$d/seq
mapfile -t f < <(cd "$d" && /bin/ls [0-9]*.bmp)
sel=()
for ((i = 0; i < n; i++)); do sel+=("$d/${f[$(( i * (${#f[@]} - 1) / (n - 1) ))]}"); done
magick montage "${sel[@]}" -resize 640x -tile 3x -geometry +4+4 -background '#000' "$out"
