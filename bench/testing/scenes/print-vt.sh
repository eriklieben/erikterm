#!/usr/bin/env bash
# Prints a content/*.vt file through the terminal you run it in (real escape bytes, through the PTY),
# e.g. to compare with the scripted reference scene, which feeds the same text straight into the parser:
#   bench/testing/scenes/print-vt.sh bench/testing/scenes/content/text.vt
set -euo pipefail
[[ $# -eq 1 && -f $1 ]] || { echo "usage: $0 FILE.vt" >&2; exit 2; }
first=1
while IFS= read -r line || [[ -n $line ]]; do
    line=${line%$'\r'}
    [[ $first == 1 ]] || printf '\r\n'
    first=0
    printf '%b' "$line" # \e, \a, \\ as in the debug script's feed: step
done < "$1"
printf '\r\n'
