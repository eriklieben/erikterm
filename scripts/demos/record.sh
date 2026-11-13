#!/usr/bin/env bash
# Records one README demo clip off-screen and puts it in docs/media.
#
#   scripts/demos/record.sh CLIP [ERIKTERM_BIN]        e.g. scripts/demos/record.sh hero
#
# CLIP is clips/CLIP.steps (debug-script steps; tool/Steps.cs lists the extras). clips/CLIP.set, if present, holds
# KEY=VALUE config overrides, one per line. Without ERIKTERM_BIN, erikterm is published to artifacts/demos/bin first.
# The demo tool (tool/, a small .NET console project) is built into artifacts/demos/tool on every run (incremental).
# The clip goes to docs/media/CLIP.webp; a shot:{FRAMES}/STILL.bmp step also writes docs/media/CLIP.png. The GIF and
# first-frame PNG stay in artifacts/demos/CLIP/ for checking.
# Environment: KEEP_FRAMES=1 keeps the BMP frames; FPS (default 20), SCALE (default 1.0), TRIM=START_MS:END_MS,
# MIN_MS (shortest gap between recorded frames, default 30), TIMEOUT (seconds, default 120).
# Needs bash, git, the .NET 11 SDK and ffmpeg (with libwebp). Runs off-screen: no window opens.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
clip=$1
demos=$root/artifacts/demos
work=$demos/$clip
world=$demos/world
media=$root/docs/media

bin=${2:-}
if [[ -z $bin ]]; then
    bin=$demos/bin/erikterm
    if [[ ! -x $bin ]]; then
        echo "record: publishing erikterm to $demos/bin"
        dotnet publish "$root/src/ErikTerm.App" -c Release -o "$demos/bin" -v q -nologo > /dev/null
    fi
fi
bin=$(cd "$(dirname "$bin")" && pwd)/$(basename "$bin")
tool=$demos/tool/demo
dotnet build "$here/tool" -c Release --artifacts-path "$demos/tool-build" -o "$demos/tool" -v q -nologo > /dev/null

rm -rf "$work"
mkdir -p "$work/frames" "$work/state" "$media"
rt=$(mktemp -d)
chmod 700 "$rt"
trap 'rm -rf "$rt"' EXIT

"$tool" world "$world" "$(dirname "$bin")" > /dev/null
demo_home=$world/home
[[ -x $here/clips/$clip.pre ]] && HOME=$demo_home "$here/clips/$clip.pre" "$demo_home"
sets=()
if [[ -f $here/clips/$clip.set ]]; then while IFS= read -r l; do [[ -n $l && $l != '#'* ]] && sets+=(--set "$l"); done < "$here/clips/$clip.set"; fi
"$tool" steps "$here/clips/$clip.steps" "$work/frames" > "$work/script.txt"

# A clean environment: nothing of the real user reaches erikterm or the shells in its panes.
env -i \
    HOME="$demo_home" USER=demo LOGNAME=demo HOSTNAME=devbox SHELL=/bin/bash \
    PATH="$demo_home/.local/bin:$(dirname "$bin"):/usr/local/bin:/usr/bin:/bin" \
    LANG=C.UTF-8 LC_ALL=C.UTF-8 TZ=UTC \
    XDG_RUNTIME_DIR="$rt" XDG_STATE_HOME="$work/state" XDG_CONFIG_HOME="$demo_home/.config" \
    XDG_CACHE_HOME="$world/cache" XDG_DATA_HOME="$demo_home/.local/share" \
    SDL_VIDEODRIVER=offscreen SDL_RENDER_DRIVER=software \
    ERIKTERM_RECORD_DIR="$work/frames" ERIKTERM_RECORD_MIN_MS="${MIN_MS:-30}" ERIKTERM_SCRIPT_STEP_MS=40 \
    ERIKTERM_DEBUG_SCRIPT_FILE="$work/script.txt" \
    bash -c 'cd "$HOME/tidewatch" && exec "$0" --config-file "$1" --no-restore --new-process "${@:2}"' \
    "$bin" "$here/erikterm.toml" ${sets[@]+"${sets[@]}"} > "$work/erikterm.log" 2>&1 &
pid=$!

deadline=$((SECONDS + ${TIMEOUT:-120}))
until [[ -e $work/frames/END.bmp ]] || ! kill -0 "$pid" 2>/dev/null || (( SECONDS >= deadline )); do sleep 0.3; done
sleep 0.5
kill "$pid" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
[[ -e $work/frames/END.bmp ]] || { echo "record: $clip did not finish; log:" >&2; cat "$work/erikterm.log" >&2; exit 1; }

"$tool" assemble "$work/frames" "$work/$clip" --fps "${FPS:-20}" --scale "${SCALE:-1.0}" ${TRIM:+--trim "$TRIM"}
cp "$work/$clip.webp" "$media/$clip.webp"
if [[ -e $work/frames/STILL.bmp ]]; then
    ffmpeg -loglevel error -y -i "$work/frames/STILL.bmp" "$media/$clip.png"
fi
[[ ${KEEP_FRAMES:-0} == 1 ]] || rm -rf "$work/frames"
echo "record: $clip -> docs/media/$clip.webp"
