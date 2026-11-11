#!/usr/bin/env bash
# Publish the benchmark with Native AOT and run it. Extra args are passed through, e.g.:
#   scripts/bench.sh --pty                       # also measure end-to-end through a real PTY
#   scripts/bench.sh --json bench-results/now.json --baseline bench/baseline.json
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet publish bench/ErikTerm.Bench -c Release -o artifacts/bench -v q -nologo >/dev/null
exec ./artifacts/bench/erikterm-bench "$@"
