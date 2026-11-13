#!/usr/bin/env bash
# Runs the checks of CI's test, code quality, release scripts and integration jobs (.github/workflows/build.yml)
# on this machine, before you push:
#
#   1. the tests:            dotnet test --project tests/ErikTerm.Tests
#   2. the analyzers:        dotnet build src/ErikTerm.App -p:Quality=true (build/quality.globalconfig)
#   3. method complexity:    scripts/check-complexity.py against build/complexity-baseline.txt
#   4. the fuzz harness:     dotnet build fuzz/ErikTerm.Fuzz (it compiles; fuzzing is fuzz/run.sh)
#   5. the release tooling:  node --test eng/ (skipped when node isn't installed)
#   6. the integration scenarios (Linux): scripts/integration-tests.sh, the real app off-screen (no window opens)
#
#   scripts/check.sh
#
# Every check runs, also after one fails. Exits 1 when any of them failed. Coverage and the other platforms are left
# to CI.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1

# Run inside erikterm, a shell has these. The tests and the tmux shim follow them to the running erikterm; CI has none.
unset ERIKTERM_SOCKET ERIKTERM_PANE TMUX TMUX_PANE
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

log=$(mktemp "${TMPDIR:-/tmp}/erikterm-quality.XXXXXX")
trap 'rm -f "$log"' EXIT

passed=()
failed=()
skipped=()

step() {
    local name=$1
    shift
    printf '\n==> %s\n' "$name"
    if "$@"; then passed+=("$name"); else failed+=("$name"); fi
}

quality_build() {
    dotnet build src/ErikTerm.App -p:Quality=true --no-incremental -nologo -v q -clp:NoSummary | tee "$log"
}

step "tests" dotnet test --project tests/ErikTerm.Tests
step "analyzers" quality_build
step "method complexity" python3 scripts/check-complexity.py "$log" build/complexity-baseline.txt
step "fuzz harness" dotnet build fuzz/ErikTerm.Fuzz -nologo -v q -clp:NoSummary
if command -v node >/dev/null; then
    step "release scripts" node --test eng/
else
    printf '\n==> release scripts\nnode not found: skipped.\n'
    skipped+=("release scripts")
fi

if [[ $(uname -s) == Linux ]]; then
    step "integration scenarios" scripts/integration-tests.sh
else
    printf '\n==> integration scenarios\nLinux only here: skipped.\n'
    skipped+=("integration scenarios")
fi

printf '\n'
for name in ${passed[@]+"${passed[@]}"}; do printf 'ok       %s\n' "$name"; done
for name in ${skipped[@]+"${skipped[@]}"}; do printf 'skipped  %s\n' "$name"; done
for name in ${failed[@]+"${failed[@]}"}; do printf 'FAILED   %s\n' "$name"; done
[[ ${#failed[@]} -eq 0 ]]
