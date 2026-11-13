#!/usr/bin/env bash
# Fuzzes one target of fuzz/ErikTerm.Fuzz with libFuzzer (SharpFuzz instruments ErikTerm.Core and ErikTerm.Config).
# docs/development/fuzzing.md.
#
#   fuzz/run.sh <terminal|png|gif|jpeg|sixel|toml> [seconds, default 300] [more seed files or directories...]
#
# Tools, neither installed by this script:
#   LIBFUZZER_DOTNET  the libFuzzer driver for .NET: fuzz/build-driver.sh <file> builds the pinned one
#   SHARPFUZZ         the sharpfuzz command; default: the one in .config/dotnet-tools.json (dotnet tool restore)
# FUZZ_WORK (default artifacts/fuzz) holds the instrumented build, the corpus per target (it grows between runs) and
# findings/<target>/ (crash-*, timeout-*, oom-*: replay one with
# dotnet <FUZZ_WORK>/bin/ErikTerm.Fuzz.dll <target> --repro <file>). fuzz/seeds/<target>/ is always added to the
# corpus, and fuzz/<target>.dict is the dictionary when there is one. LIBFUZZER_ARGS adds libFuzzer flags (split on
# spaces), e.g. LIBFUZZER_ARGS="-jobs=4 -workers=4".
set -euo pipefail

target=${1:?usage: fuzz/run.sh <target> [seconds] [seeds...]}
seconds=${2:-300}
shift $(( $# >= 2 ? 2 : 1 ))
root=$(cd "$(dirname "$0")/.." && pwd)
work=${FUZZ_WORK:-$root/artifacts/fuzz}
libfuzzer=${LIBFUZZER_DOTNET:?set LIBFUZZER_DOTNET to the libfuzzer-dotnet driver (fuzz/build-driver.sh builds it)}
if [[ -n "${SHARPFUZZ:-}" ]]; then sharpfuzz=("$SHARPFUZZ"); else sharpfuzz=(dotnet tool run sharpfuzz); fi

out=$work/bin
rm -rf "$out"
dotnet publish "$root/fuzz/ErikTerm.Fuzz" -c Release -o "$out" -nologo -v q
for dll in ErikTerm.Core.dll ErikTerm.Config.dll; do (cd "$root" && "${sharpfuzz[@]}" "$out/$dll"); done

corpus=$work/corpus/$target
findings=$work/findings/$target
mkdir -p "$corpus" "$findings"
seeds=("$@")
if [[ -d "$root/fuzz/seeds/$target" ]]; then seeds+=("$root/fuzz/seeds/$target"); fi
for seed in "${seeds[@]}"; do
  if [[ -d "$seed" ]]; then cp -n "$seed"/* "$corpus"/ 2>/dev/null || true; else cp -n "$seed" "$corpus"/; fi
done

dict=()
if [[ -f "$root/fuzz/$target.dict" ]]; then dict=(-dict="$root/fuzz/$target.dict"); fi
read -r -a extra <<< "${LIBFUZZER_ARGS:-}"

# -timeout: one input taking 10 s is a hang. Memory: libFuzzer's -rss_limit_mb only sees the driver, not the .NET
# process it starts, so the GC gets a hard limit instead: one image may legitimately reach ImageLimits.MaxPixels
# (200 MB of RGBA, plus the decoder's own canvas), so needing more than 1 GB means an input got past the limits, and
# the OutOfMemoryException is a crash-* finding.
export DOTNET_GCHeapHardLimit=0x40000000 # NOSONAR: the .NET runtime setting is spelled this way
exec "$libfuzzer" --target_path="$out/ErikTerm.Fuzz" --target_arg="$target" \
  -max_total_time="$seconds" -timeout=10 -print_final_stats=1 \
  -artifact_prefix="$findings/" "${dict[@]}" "${extra[@]}" "$corpus"
