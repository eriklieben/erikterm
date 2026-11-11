#!/usr/bin/env bash
# Re-records every clip: scripts/demos/record-all.sh [ERIKTERM_BIN]
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
for c in hero search quickselect agents palette vim review images; do echo "== $c"; "$here/record.sh" "$c" "$@"; done
