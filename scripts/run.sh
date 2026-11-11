#!/usr/bin/env bash
# Publish erikterm with Native AOT and launch it. Args pass through (e.g. `-e btop`).
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet publish src/ErikTerm.App -c Release -o artifacts/app -v q -nologo >/dev/null
exec ./artifacts/app/erikterm "$@"
