#!/usr/bin/env bash
# Fuzzes one target of fuzz/ErikTerm.Fuzz with libFuzzer (SharpFuzz instruments ErikTerm.Core and ErikTerm.Config).
#
#   fuzz/run.sh <terminal|png|gif|jpeg|sixel|toml> [seconds, default 300] [seed files or directories...]
#
# Needs two tools, neither installed by this script:
#   SHARPFUZZ         the sharpfuzz command (dotnet tool install SharpFuzz.CommandLine --tool-path <dir>)
#   LIBFUZZER_DOTNET  the libFuzzer driver for .NET (github.com/Metalnem/libfuzzer-dotnet: a release binary, or
#                     clang -fsanitize=fuzzer libfuzzer-dotnet.cc -o libfuzzer-dotnet)
# FUZZ_WORK (default artifacts/fuzz) holds the instrumented build, the corpus per target (it grows between runs) and
# findings/<target>/ (crash-*, timeout-*, oom-*: replay one with
# dotnet <FUZZ_WORK>/bin/ErikTerm.Fuzz.dll <target> --repro <file>).
set -euo pipefail

target=${1:?usage: fuzz/run.sh <target> [seconds] [seeds...]}
seconds=${2:-300}
shift $(( $# >= 2 ? 2 : 1 ))
root=$(cd "$(dirname "$0")/.." && pwd)
work=${FUZZ_WORK:-$root/artifacts/fuzz}
sharpfuzz=${SHARPFUZZ:?set SHARPFUZZ to the sharpfuzz command}
libfuzzer=${LIBFUZZER_DOTNET:?set LIBFUZZER_DOTNET to the libfuzzer-dotnet driver}

out=$work/bin
rm -rf "$out"
dotnet publish "$root/fuzz/ErikTerm.Fuzz" -c Release -o "$out" -nologo -v q
for dll in ErikTerm.Core.dll ErikTerm.Config.dll; do "$sharpfuzz" "$out/$dll"; done

corpus=$work/corpus/$target
findings=$work/findings/$target
mkdir -p "$corpus" "$findings"
for seed in "$@"; do
  if [ -d "$seed" ]; then cp -n "$seed"/* "$corpus"/ 2>/dev/null || true; else cp -n "$seed" "$corpus"/; fi
done

dict=()
if [ -f "$root/fuzz/$target.dict" ]; then dict=(-dict="$root/fuzz/$target.dict"); fi

# -timeout: one input taking 10 s is a hang. Memory: libFuzzer's -rss_limit_mb only sees the driver, not the .NET
# process it starts, so the GC gets a hard limit instead: one image may legitimately reach ImageLimits.MaxPixels
# (200 MB of RGBA, plus the decoder's own canvas), so needing more than 1 GB means an input got past the limits, and
# the OutOfMemoryException is a crash-* finding.
export DOTNET_GCHeapHardLimit=0x40000000
exec "$libfuzzer" --target_path="$out/ErikTerm.Fuzz" --target_arg="$target" \
  -max_total_time="$seconds" -timeout=10 -print_final_stats=1 \
  -artifact_prefix="$findings/" "${dict[@]}" "$corpus"
