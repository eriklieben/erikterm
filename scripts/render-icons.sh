#!/usr/bin/env bash
# Renders the app icon from its SVG sources into assets/icon/png/erikterm-<size>.png and assets/icon/erikterm.ico.
# Sizes with a hand-tuned drawing (assets/icon/erikterm-<size>.svg: 16, 24, 32) use it; the others scale the master
# (assets/icon/erikterm.svg). The app embeds png/erikterm-128.png as its window icon (build/AppIcon.props).
#   scripts/render-icons.sh                 render the PNGs and the .ico
#   scripts/render-icons.sh --sheet OUT.png also write a contact sheet: every size on dark and light, small sizes zoomed
# Needs rsvg-convert (librsvg) and ImageMagick 7 (magick).
set -euo pipefail
cd "$(dirname "$0")/.."

sheet=""
case "${1:-}" in
    --sheet) sheet="${2:?--sheet needs an output path}" ;;
    "") ;;
    *) echo "usage: $0 [--sheet OUT.png]" >&2; exit 2 ;;
esac
for tool in rsvg-convert magick; do
    command -v "$tool" >/dev/null || { echo "render-icons: $tool not found" >&2; exit 1; }
done

dir=assets/icon
sizes=(16 24 32 48 64 128 256 512 1024)
ico_sizes=(16 24 32 48 64 256)
mkdir -p "$dir/png"
for size in "${sizes[@]}"; do
    src="$dir/erikterm-$size.svg"
    [[ -f $src ]] || src="$dir/erikterm.svg"
    rsvg-convert -w "$size" -h "$size" "$src" -o "$dir/png/erikterm-$size.png"
done

# Windows: 32-bit BMP entries up to 64 px and a PNG-compressed 256 px entry.
ico_inputs=()
for size in "${ico_sizes[@]}"; do ico_inputs+=("$dir/png/erikterm-$size.png"); done
magick "${ico_inputs[@]}" -define icon:png-compression-size=256 -define png:exclude-chunks=date,time "$dir/erikterm.ico"
echo "render-icons: ${#sizes[@]} PNGs in $dir/png, $dir/erikterm.ico (${ico_sizes[*]})"

[[ -n $sheet ]] || exit 0
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
row() { # $1 background, $2 output: every size up to 256 at 1:1, bottom-aligned
    local args=() size
    for size in 16 24 32 48 64 128 256; do args+=("$dir/png/erikterm-$size.png"); done
    magick "${args[@]}" -background "$1" -bordercolor "$1" -alpha remove -alpha off -border 12 \
        -gravity south +append -border 12 "$2"
}
zoomed() { # $1 background, $2 output: 16, 24, 32 and 48 px blown up to 128 px with hard pixels
    local args=() size
    for size in 16 24 32 48; do args+=("$dir/png/erikterm-$size.png"); done
    magick "${args[@]}" -background "$1" -bordercolor "$1" -alpha remove -alpha off -filter point -resize 128x128 \
        -border 12 -gravity south +append -border 12 "$2"
}
row '#1e2030' "$tmp/dark.png"
row '#eff1f5' "$tmp/light.png"
zoomed '#1e2030' "$tmp/zoom-dark.png"
zoomed '#eff1f5' "$tmp/zoom-light.png"
magick -background '#1e2030' "$tmp/dark.png" "$tmp/light.png" "$tmp/zoom-dark.png" "$tmp/zoom-light.png" \
    -gravity west -append "$sheet"
echo "render-icons: contact sheet $sheet"
