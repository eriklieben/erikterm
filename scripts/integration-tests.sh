#!/usr/bin/env bash
# Runs the off-screen integration scenarios (tests/ErikTerm.IntegrationTests): each test starts the real erikterm with
# SDL's offscreen video driver and software renderer, in private directories, drives it and checks what it shows.
# No window opens and nothing talks to an erikterm you are using. bench/README.md (Integration scenarios).
#
#   scripts/integration-tests.sh                        # the scenarios
#   scripts/integration-tests.sh --coverage out.xml     # the same under dotnet-coverage: a Cobertura report
#   scripts/integration-tests.sh -- --treenode-filter '/*/*/ShellTests/*'   # options after -- go to the test runner
#
# Environment: ERIKTERM_IT_PARALLEL=N (erikterm processes at once; default half the cores, at most 4),
# ERIKTERM_IT_KEEP=1 (keep each run's directory under /tmp: script, config, screenshots), ERIKTERM_IT_DESKTOP=1
# (Windows, macOS: also the scenarios that open a real window), ERIKTERM_IT_SH (Windows: the sh for the panes;
# default Git for Windows' usr\bin\sh.exe). Runs on Linux, macOS, and Windows from Git Bash.
set -euo pipefail
cd "$(dirname "$0")/.."

coverage=""
while [[ $# -gt 0 ]]; do
    case $1 in
        # Relative to the checkout (no realpath -m: macOS's realpath has no -m).
        --coverage) coverage=$2; [[ $coverage == /* ]] || coverage="$PWD/$coverage"; shift 2 ;;
        --) shift; break ;;
        -h|--help) sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "integration-tests: unknown option $1" >&2; exit 2 ;;
    esac
done

# Nothing that points at a display or at a running erikterm or tmux (the tests clear these for erikterm as well).
unset DISPLAY WAYLAND_DISPLAY WAYLAND_SOCKET ERIKTERM_SOCKET ERIKTERM_PANE ERIKTERM_SCREENSHOT ERIKTERM_DEBUG_SCRIPT \
    ERIKTERM_DEBUG_SCRIPT_FILE TMUX TMUX_PANE
export SDL_VIDEODRIVER=offscreen SDL_RENDER_DRIVER=software
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

dotnet build tests/ErikTerm.IntegrationTests -nologo -v q -clp:NoSummary
runner=tests/ErikTerm.IntegrationTests/bin/Debug/net11.0/ErikTerm.IntegrationTests
# Windows (Git Bash): the .exe by its full name. MSYS2's -x test would also accept the bare name, which
# dotnet-coverage can't start.
[[ -f $runner.exe ]] && runner="$runner.exe"

if [[ -z $coverage ]]; then
    exec "$runner" "$@"
fi
dotnet tool restore >/dev/null
mkdir -p "$(dirname "$coverage")"
dotnet tool run dotnet-coverage collect --settings tests/ErikTerm.IntegrationTests/coverage.config \
    -f cobertura -o "$coverage" "$runner" "$@"
