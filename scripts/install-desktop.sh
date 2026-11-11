#!/usr/bin/env bash
# Installs erikterm for the current user: bin/erikterm (a symlink to the published binary), the hicolor icons (PNGs,
# scalable SVG, symbolic SVG) and share/applications/erikterm.desktop, all under ~/.local unless --prefix says otherwise.
#   scripts/install-desktop.sh                  publish to artifacts/app if needed, install into ~/.local
#   scripts/install-desktop.sh --prefix DIR     install into DIR/bin and DIR/share
#   scripts/install-desktop.sh --bin PATH       link PATH instead of artifacts/app/erikterm
#   scripts/install-desktop.sh --uninstall      remove what an install put there (same --prefix)
set -euo pipefail
cd "$(dirname "$0")/.."
repo=$(pwd)

prefix="$HOME/.local"
bin="$repo/artifacts/app/erikterm"
uninstall=false
while (($#)); do
    case "$1" in
        --prefix) prefix="${2:?--prefix needs a directory}"; shift 2 ;;
        --bin) bin="${2:?--bin needs a path}"; shift 2 ;;
        --uninstall) uninstall=true; shift ;;
        -h | --help) sed -n '2,7p' "$0" | cut -c3-; exit 0 ;;
        *) echo "install-desktop: unknown argument '$1' (see --help)" >&2; exit 2 ;;
    esac
done

prefix=$(realpath -m "$prefix")
apps="$prefix/share/applications"
icons="$prefix/share/icons/hicolor"
link="$prefix/bin/erikterm"
sizes=(16 24 32 48 64 128 256 512)

refresh() {
    # Only refresh caches that exist: a stale icon-theme.cache would hide new icons, a missing one is just slower.
    if [[ -f $icons/icon-theme.cache ]] && command -v gtk-update-icon-cache >/dev/null; then
        gtk-update-icon-cache -q -f -t "$icons" || true
    fi
    if [[ -f $apps/mimeinfo.cache ]] && command -v update-desktop-database >/dev/null; then
        update-desktop-database -q "$apps" || true
    fi
}

if $uninstall; then
    rm -f "$apps/erikterm.desktop" "$icons/scalable/apps/erikterm.svg" "$icons/symbolic/apps/erikterm-symbolic.svg"
    for size in "${sizes[@]}"; do rm -f "$icons/${size}x${size}/apps/erikterm.png"; done
    if [[ -L $link ]]; then rm -f "$link"; fi # only the symlink an install made, never a real file
    refresh
    echo "install-desktop: removed erikterm from $prefix"
    exit 0
fi

if [[ ! -x $bin ]]; then
    if [[ $bin != "$repo/artifacts/app/erikterm" ]]; then
        echo "install-desktop: $bin is not an executable" >&2
        exit 1
    fi
    echo "install-desktop: publishing erikterm (Native AOT) to artifacts/app"
    dotnet publish src/ErikTerm.App -c Release -o artifacts/app -v q -nologo >/dev/null
fi
bin=$(realpath "$bin")

install -d "$prefix/bin" "$apps" "$icons/scalable/apps" "$icons/symbolic/apps"
ln -sfn "$bin" "$link"
for size in "${sizes[@]}"; do
    install -D -m 644 "assets/icon/png/erikterm-$size.png" "$icons/${size}x${size}/apps/erikterm.png"
done
install -m 644 assets/icon/erikterm.svg "$icons/scalable/apps/erikterm.svg"
install -m 644 assets/icon/erikterm-symbolic.svg "$icons/symbolic/apps/erikterm-symbolic.svg"

# Absolute Exec/TryExec: launchers started by the compositor often don't have ~/.local/bin on PATH.
exec_path="$link"
if [[ $link == *[[:space:]]* ]]; then exec_path="\"$link\""; fi
sed -e "s|^Exec=erikterm|Exec=$exec_path|" -e "s|^TryExec=erikterm|TryExec=$link|" \
    contrib/linux/erikterm.desktop >"$apps/erikterm.desktop.tmp"
chmod 644 "$apps/erikterm.desktop.tmp"
mv -f "$apps/erikterm.desktop.tmp" "$apps/erikterm.desktop"
if command -v desktop-file-validate >/dev/null; then desktop-file-validate "$apps/erikterm.desktop"; fi
refresh

echo "install-desktop: $link -> $bin"
echo "install-desktop: icons in $icons, launcher entry $apps/erikterm.desktop"
case ":$PATH:" in *":$prefix/bin:"*) ;; *) echo "install-desktop: note: $prefix/bin is not on PATH" ;; esac
# TERM=xterm-erikterm (styled underlines, true color, OSC 52 for Neovim and tmux) needs its terminfo entry; erikterm
# falls back to xterm-256color without it. Not installed from here: it goes into ~/.terminfo, outside --prefix.
if command -v infocmp >/dev/null && ! infocmp -x xterm-erikterm >/dev/null 2>&1; then
    echo "install-desktop: tip: scripts/install-terminfo.sh installs the xterm-erikterm terminfo entry (see README)"
fi
