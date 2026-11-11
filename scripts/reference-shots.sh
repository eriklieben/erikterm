#!/usr/bin/env bash
# Renders the reference scenes (bench/testing/scenes/*.txt) with an erikterm binary and saves one PNG per
# screenshot in bench/testing/reference/<os>/, for side-by-side comparison between platforms.
#
#   scripts/reference-shots.sh path/to/erikterm               # every scene
#   scripts/reference-shots.sh path/to/erikterm 03-rendering  # just these scenes (file names without .txt)
#
# Environment:
#   REFSHOTS_OUT=DIR            where the PNGs go (default: bench/testing/reference/linux or .../macos)
#   ERIKTERM_SHOT_CLASS=NAME    window class / app id of the scene windows (default: erikterm-refshots);
#                               on a tiling window manager give it a floating rule so the window keeps
#                               the size scene.toml asks for
#   REFSHOTS_KEEP=1             keep the temporary directory (generated scripts, BMPs, logs)
#
# Each scene runs in its own erikterm process (--new-process --no-restore) with a private
# XDG_RUNTIME_DIR and XDG_STATE_HOME, so it never talks to, or saves over, an erikterm you are using.
# Windows: scripts/reference-shots.ps1.
set -euo pipefail

if [[ $# -lt 1 || $1 == -h || $1 == --help ]]; then
    sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
fi
bin=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
shift
[[ -x $bin ]] || { echo "reference-shots: $bin is not an executable" >&2; exit 2; }

root=$(cd "$(dirname "$0")/.." && pwd)
scenes_dir=$root/bench/testing/scenes
case "$(uname -s)" in
    Darwin) os=macos ;;
    Linux) os=linux ;;
    *) os=$(uname -s | tr '[:upper:]' '[:lower:]') ;;
esac
out=${REFSHOTS_OUT:-$root/bench/testing/reference/$os}
class=${ERIKTERM_SHOT_CLASS:-erikterm-refshots}
mkdir -p "$out"

# /tmp, not $TMPDIR: the control socket path must stay short (macOS allows 104 bytes).
tmp=$(mktemp -d /tmp/etshots.XXXXXX)
cleanup() { if [[ ${REFSHOTS_KEEP:-0} == 1 ]]; then echo "kept $tmp"; else rm -rf "$tmp"; fi; }
trap cleanup EXIT
mkdir -m 700 "$tmp/rt"
mkdir -p "$tmp/state" "$tmp/bmp" "$tmp/cwd"

# A private runtime dir hides the Wayland socket from SDL (it would fall back to XWayland at scale 1):
# point WAYLAND_DISPLAY at it by its absolute path first.
if [[ -n ${WAYLAND_DISPLAY:-} && $WAYLAND_DISPLAY != /* && -n ${XDG_RUNTIME_DIR:-} ]]; then
    export WAYLAND_DISPLAY=$XDG_RUNTIME_DIR/$WAYLAND_DISPLAY
fi
export XDG_RUNTIME_DIR=$tmp/rt XDG_STATE_HOME=$tmp/state
unset ERIKTERM_SOCKET ERIKTERM_PANE ERIKTERM_SCREENSHOT ERIKTERM_DEBUG_SCRIPT TMUX TMUX_PANE

# The scene config, behind a program that prints nothing and waits (each pane runs it).
config=$tmp/scene.toml
{ printf 'default_prog = ["sleep", "600"]\n'; cat "$scenes_dir/scene.toml"; } > "$config"

if [[ $# -gt 0 ]]; then
    scenes=(); for s in "$@"; do scenes+=("$scenes_dir/${s%.txt}.txt"); done
else
    scenes=("$scenes_dir"/[0-9]*.txt)
fi

convert_png() { # BMP → PNG with whatever is installed; else keep the BMP
    local bmp=$1 png=$2
    if command -v magick >/dev/null; then magick "$bmp" -strip -define png:compression-level=9 "$png"
    elif command -v convert >/dev/null; then convert "$bmp" -strip -define png:compression-level=9 "$png"
    elif command -v sips >/dev/null; then sips -s format png "$bmp" --out "$png" >/dev/null
    else cp "$bmp" "${png%.png}.bmp"; echo "  (no ImageMagick or sips: kept ${png%.png}.bmp)"; return
    fi
    if command -v pngquant >/dev/null && [[ ${REFSHOTS_QUANTIZE:-1} == 1 ]]; then
        pngquant --force --skip-if-larger --quality 85-100 --speed 1 --output "$png" -- "$png" 2>/dev/null || true
    fi
}

# 12-worktree: a throwaway repository to launch from (the scene directory) and fake agents, never real ones.
git -C "$tmp/cwd" init -q -b main
git -C "$tmp/cwd" -c user.name=refshots -c user.email=refshots@example.invalid -c commit.gpgsign=false commit -q --allow-empty -m first
mkdir -p "$tmp/agents"
for agent in claude copilot codex; do
    cat > "$tmp/agents/$agent" <<'FAKE'
#!/bin/sh
printf '\033]7501;state=working:app=claude-code\033\\'
printf ' Fake agent (reference scene)\r\n\r\n args:'
for a in "$@"; do printf ' [%s]' "$a"; done
printf '\r\n cwd:  %s\r\n' "$PWD"
exec sleep 600
FAKE
    chmod +x "$tmp/agents/$agent"
done
export PATH=$tmp/agents:$PATH

# 13-worktrees, 14-merge-bar: temp repositories with worktrees in the states the dialogs show.
fx() { git -C "$1" -c user.name=refshots -c user.email=refshots@example.invalid -c commit.gpgsign=false "${@:2}"; }
fx_commit() { fx "$1" add -A && fx "$1" commit -q -m "$2"; }
wr=$tmp/wtrepo; mkdir -p "$wr"
fx "$wr" init -q -b main
fx "$wr" config user.name refshots; fx "$wr" config user.email refshots@example.invalid; fx "$wr" config commit.gpgsign false
printf 'one\ntwo\nthree\n' > "$wr/README.md"; fx_commit "$wr" "first"
fx "$wr" worktree add -q -b fix-login-redirect -- "$tmp/wtrepo.worktrees/fix-login-redirect" main
printf 'fix\n' > "$tmp/wtrepo.worktrees/fix-login-redirect/login.txt"; fx_commit "$tmp/wtrepo.worktrees/fix-login-redirect" "Fix the login redirect loop"
printf 'test\n' > "$tmp/wtrepo.worktrees/fix-login-redirect/login.test.txt"; fx_commit "$tmp/wtrepo.worktrees/fix-login-redirect" "Add a regression test"
fx "$wr" worktree add -q -b landed-cleanup -- "$tmp/wtrepo.worktrees/landed-cleanup" main
printf 'gone\n' > "$tmp/wtrepo.worktrees/landed-cleanup/old.txt"; fx_commit "$tmp/wtrepo.worktrees/landed-cleanup" "Remove the old flow"
fx "$wr" merge -q --squash --end-of-options landed-cleanup >/dev/null; fx "$wr" commit -q -m "Remove the old flow (squashed)"
printf 'more\n' >> "$wr/README.md"; fx_commit "$wr" "Later work on main"
fx "$wr" worktree add -q -b dirty-experiment -- "$tmp/wtrepo.worktrees/dirty-experiment" main
printf 'scratch\n' > "$tmp/wtrepo.worktrees/dirty-experiment/scratch.txt"
rb=$tmp/rebasing; mkdir -p "$rb"
fx "$rb" init -q -b main
fx "$rb" config user.name refshots; fx "$rb" config user.email refshots@example.invalid; fx "$rb" config commit.gpgsign false
printf 'one\ntwo\nthree\n' > "$rb/a.txt"; fx_commit "$rb" "first"
fx "$rb" switch -q -c feat
printf 'one\nbranch\nthree\n' > "$rb/a.txt"; fx_commit "$rb" "edit on the branch"
printf 'x\n' > "$rb/b.txt"; fx_commit "$rb" "a second commit"
fx "$rb" switch -q main
printf 'one\nmain\nthree\n' > "$rb/a.txt"; fx_commit "$rb" "edit on main"
fx "$rb" switch -q feat
fx "$rb" rebase main >/dev/null 2>&1 || true

# 15-merge-tool, 16-merge-compact: a repository stopped on a merge with conflicts in two files and an automatic merge in a third.
mr=$tmp/mergerepo; mkdir -p "$mr/src"
fx "$mr" init -q -b main
fx "$mr" config user.name refshots; fx "$mr" config user.email refshots@example.invalid; fx "$mr" config commit.gpgsign false
cat > "$mr/src/auth.ts" <<'TS'
import { redirect } from "./nav";

export const login = (u) => {
  if (!u) return "/login";
  return "/home";
};

export const logout = () => "/login";
TS
cat > "$mr/src/cart.ts" <<'TS'
// cart helpers
import { money } from "./money";

export const total = (items) =>
  items.reduce((a, i) => a + i.price, 0);

export const count = (items) =>
  items.length;
TS
printf '# Shop\n\nThe shop front end.\n' > "$mr/README.md"
fx_commit "$mr" "base"
fx "$mr" switch -q -c feature/signin
sed -i.bak 's|if (!u) return "/login";|if (!u) return "/signin";|; s|logout = () => "/login"|logout = () => "/signin"|' "$mr/src/auth.ts"; rm "$mr/src/auth.ts.bak"
sed -i.bak 's|a + i.price, 0|a + i.price * i.qty, 0|' "$mr/src/cart.ts"; rm "$mr/src/cart.ts.bak"
printf '# Shop\n\nThe shop front end.\n\nSign in with your account.\n' > "$mr/README.md"
fx_commit "$mr" "sign in"
fx "$mr" switch -q main
sed -i.bak 's/if (!u) return "\/login";/if (!u || !u.token) return "\/login";/' "$mr/src/auth.ts"; rm "$mr/src/auth.ts.bak"
sed -i.bak 's|a + i.price, 0|a + i.price + i.tax, 0|' "$mr/src/cart.ts"; rm "$mr/src/cart.ts.bak"
fx_commit "$mr" "token and tax"
fx "$mr" merge -q --no-edit feature/signin >/dev/null 2>&1 || true

# 17-compare: two versions of a file with one real change and two noise changes.
cf=$tmp/comparefiles; mkdir -p "$cf/main" "$cf/branch"
cat > "$cf/main/cart.ts" <<'TS'
// cart helpers
import { money } from "./money";

export const total = (items) =>
  items.reduce((a, i) => a + i.price, 0);

export const count = (items) =>
  items.length;

export const format = (n) =>
  money(n, "EUR");

export const empty = (items) =>
  items.length === 0;
TS
cat > "$cf/branch/cart.ts" <<'TS'
// helpers for the shopping cart
import { money } from "./money";

export const total = (items) =>
  items.reduce((a, i) => a + i.price * i.qty, 0);
  // qty defaults to 1 in the cart store
  // see store.ts

export const count = (items) =>
    items.length;

export const format = (n) =>
  money(n, "EUR");

export const empty = (items) =>
  items.length === 0;
TS

# 18-review, 19-review-narrow: a worktree with commits, uncommitted work and a base that moved; main is behind origin.
rv=$tmp/reviewrepo; rw=$tmp/reviewrepo.worktrees/fix-cart-total; mkdir -p "$rv/src" "$rv/tests"
fx "$rv" init -q -b main
fx "$rv" config user.name refshots; fx "$rv" config user.email refshots@example.invalid; fx "$rv" config commit.gpgsign false
cp "$cf/main/cart.ts" "$rv/src/cart.ts"
printf 'export const login = (u) => {\n  if (!u) return "/login";\n  return "/home";\n};\n\nexport const logout = () => "/login";\n' > "$rv/src/auth.ts"
printf 'test("old flow", () => {});\n' > "$rv/tests/old.test.ts"
printf '# Shop\n\nThe shop front end.\n' > "$rv/README.md"
fx_commit "$rv" "base"
fx "$rv" remote add origin https://example.invalid/shop.git
fx "$rv" worktree add -q -b fix-cart-total -- "$rw" main
cp "$cf/branch/cart.ts" "$rw/src/cart.ts"; fx_commit "$rw" "Fix the cart total for quantities"
fx "$rw" mv src/auth.ts src/signin.ts
sed -i.bak 's|logout = () => "/login"|logout = () => "/signin"|' "$rw/src/signin.ts"; rm "$rw/src/signin.ts.bak"
printf 'test("total", () => {});\n' > "$rw/tests/cart.test.ts"
fx "$rw" rm -q tests/old.test.ts
fx_commit "$rw" "Rename login to sign in and replace the old test"
printf '# Shop\n\nThe shop front end.\n\nSign in with your account.\n' > "$rw/README.md"
printf 'notes while reviewing\n' > "$rw/notes.txt"
sed -i.bak 's|a + i.price, 0|a + i.price + i.tax, 0|' "$rv/src/cart.ts"; rm "$rv/src/cart.ts.bak"
fx_commit "$rv" "Add tax to the total"
for n in 1 2 3; do printf '%s\n' "$n" >> "$rv/README.md"; fx_commit "$rv" "later $n"; done
fx "$rv" update-ref refs/remotes/origin/main HEAD
fx "$rv" reset -q --hard HEAD~3

failed=0
for scene in "${scenes[@]}"; do
    [[ -f $scene ]] || { echo "reference-shots: no scene $scene" >&2; failed=1; continue; }
    name=$(basename "$scene" .txt)
    script=$tmp/$name.script
    sets=() shots=() scene_cwd=$tmp/cwd
    : > "$script"
    while IFS= read -r line || [[ -n $line ]]; do
        line=${line%$'\r'}
        case $line in
            '# set: '*) set_value=${line#'# set: '}; sets+=(--set "${set_value//\{bin\}/$bin}") ;;
            '# cwd: '*) scene_cwd=$tmp/${line#'# cwd: '} ;;
            feedfile:*)
                # One feed: step with the file's lines joined by \r\n (C# escapes, as the debug script expects).
                printf 'feed:' >> "$script"
                awk '{ sub(/\r$/, "") } NR > 1 { printf "\\r\\n" } { printf "%s", $0 }' "$scenes_dir/content/${line#feedfile:}" >> "$script"
                printf '\n' >> "$script" ;;
            *)
                line=${line//\{out\}/$tmp/bmp}
                [[ $line == shot:* ]] && shots+=("${line#shot:}")
                printf '%s\n' "$line" >> "$script" ;;
        esac
    done < "$scene"

    echo "$name: ${#shots[@]} screenshot(s)"
    (cd "$scene_cwd" && ERIKTERM_DEBUG_SCRIPT_FILE=$script exec "$bin" --config-file "$config" --no-restore --new-process \
        --class "$class" ${sets[@]+"${sets[@]}"}) > "$tmp/$name.log" 2>&1 &
    pid=$!

    # Wait for every screenshot (or the process to die, or 60 s), then end the process.
    deadline=$((SECONDS + 60))
    while :; do
        done_all=1
        for s in "${shots[@]}"; do [[ -s $s ]] || done_all=0; done
        [[ $done_all == 1 ]] && break
        kill -0 "$pid" 2>/dev/null || break
        (( SECONDS < deadline )) || break
        sleep 0.2
    done
    sleep 0.5 # the last BMP finishes writing
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true

    for s in "${shots[@]}"; do
        if [[ -s $s ]]; then
            png=$out/$(basename "$s" .bmp).png
            convert_png "$s" "$png"
            echo "  $png"
        else
            echo "  MISSING $(basename "$s" .bmp) — see the log:" >&2
            sed 's/^/    /' "$tmp/$name.log" >&2
            failed=1
        fi
    done
done
exit $failed
