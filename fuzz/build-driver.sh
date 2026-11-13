#!/usr/bin/env bash
# Builds libfuzzer-dotnet, the libFuzzer driver fuzz/run.sh needs (LIBFUZZER_DOTNET), from a pinned commit of
# github.com/Metalnem/libfuzzer-dotnet (MIT). The source file is checked against its SHA-256 before it is compiled.
#
#   fuzz/build-driver.sh <output file>      CC (default clang) needs libFuzzer: -fsanitize=fuzzer
#
# CI (.github/workflows/fuzz.yml) builds it the same way. To move the pin: take the commit, download libfuzzer-dotnet.cc
# from it, read the change, and put both the commit and the file's sha256sum here.
set -euo pipefail

commit=bd39d4e88d715ab460a929943645be2a186cde52 # 2025-05-02, "Handle child process failure better (#22)"
sha256=90f019e2e9ad3a0b93c7ecc2c5afb2fbfc8b5aab6aac51c7e0d349ec79354f36

output=${1:?usage: fuzz/build-driver.sh <output file>}
cc=${CC:-clang}

src=$(mktemp -d)
trap 'rm -rf "$src"' EXIT
curl --proto '=https' --tlsv1.2 -fsSL -o "$src/libfuzzer-dotnet.cc" \
  "https://raw.githubusercontent.com/Metalnem/libfuzzer-dotnet/$commit/libfuzzer-dotnet.cc"
echo "$sha256  $src/libfuzzer-dotnet.cc" | sha256sum -c --quiet -

mkdir -p "$(dirname "$output")"
"$cc" -Werror -O2 -fsanitize=fuzzer "$src/libfuzzer-dotnet.cc" -o "$output"
echo "built $output (libfuzzer-dotnet $commit)"
