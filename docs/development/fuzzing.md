# Fuzzing

Everything a program prints in erikterm, every image it sends and the settings file is input erikterm has to survive.
Besides the tests (including the property tests for the parser and TOML), a fuzzer feeds that code generated input
and looks for crashes, hangs and runaway memory. It runs in CI every week, and locally with one script.

## What is fuzzed

[fuzz/ErikTerm.Fuzz](../../fuzz/ErikTerm.Fuzz/Program.cs) holds the targets: libFuzzer generates the inputs and
[SharpFuzz](https://github.com/Metalnem/sharpfuzz) instruments `ErikTerm.Core` and `ErikTerm.Config`, so libFuzzer sees
which branches an input reached.

| Target | What it feeds | Seeds |
| --- | --- | --- |
| `terminal` | `Terminal.Feed`: CSI, OSC, DCS, APC, kitty graphics, sixel, iTerm2 images, synchronized output, split over two reads at a point the input picks; then a resize and the screen read back. With [terminal.dict](../../fuzz/terminal.dict), the pieces of the sequences. | SGR and cursor movement, modes and OSC, sixel, kitty, iTerm2 |
| `png` | `Png.Decode` | RGBA, palette with transparency, 16-bit gray Adam7, APNG |
| `gif` | `Gif.Decode` | 1×1, two frames with a transparent color |
| `jpeg` | `Jpeg.Decode` | baseline 4:2:0, progressive gray, restart markers |
| `sixel` | the sixel decoder | color registers, raster attributes, repeats |
| `toml` | `Toml.Parse` on valid UTF-8 | a settings file, the syntax corners erikterm's reader supports |

The decoders are called below their catch-all wrappers (`ImageDecoder.Decode` and `Sixel.Decode` turn any exception
into `ImageDecodeException`), so a bug the wrapper would hide still shows. A finding is:

- **crash**: an exception other than the target's own "bad input" error (`ImageDecodeException`, `TomlException`), a
  decoded image whose size doesn't match its pixels, or the .NET process dying (a stack overflow);
- **out of memory**: the .NET heap is limited to 1 GB. One image may legitimately reach `ImageLimits.MaxPixels`
  (200 MB of RGBA plus the decoder's canvas), so needing more means an input got past the limits;
- **timeout**: one input running for more than 10 seconds.

The seeds in [fuzz/seeds](../../fuzz/seeds) are tiny valid samples (under 3 KB together) made for this: the images are
generated (the JPEGs with ImageMagick, `-strip`, so no metadata), the escape sequences and TOML written by hand.

So far fuzzing found two bugs, both fixed: sixel memory growing across raster declarations, and the TOML `\U` escape.

## In CI

[fuzz.yml](../../.github/workflows/fuzz.yml), early on Saturdays (02:37 UTC), and by hand (Actions › fuzz › Run
workflow, with the targets, the minutes per target and `keep_reproduction`).

- **Time.** Every target is its own job, in parallel, fuzzing for 15 minutes. With the build and the corpus merge a job
  takes about 18 minutes, so a run is about 110 Linux runner minutes, free for a public repository. Like CodeQL, the
  schedule waits for the repository variable `PUBLIC_REPO` = `true`.
- **Tools.** SharpFuzz.CommandLine is pinned in [.config/dotnet-tools.json](../../.config/dotnet-tools.json)
  (`dotnet tool restore`), at the same version as the harness's SharpFuzz package; Dependabot moves them together.
  The libFuzzer driver, [libfuzzer-dotnet](https://github.com/Metalnem/libfuzzer-dotnet), is built with the runner's
  clang from the commit [fuzz/build-driver.sh](../../fuzz/build-driver.sh) pins, after checking the source file's
  SHA-256.
- **Corpus.** Each target's corpus, the inputs that reached new code, is kept in the Actions cache: a new entry per run
  (`fuzz-corpus-<target>-<run>`), restored from the newest one. Before it is saved, `libFuzzer -merge` drops the inputs
  that add no coverage. `fuzz/seeds/<target>` is added every run, so the first run, or one after the cache was lost,
  starts from the seeds. GitHub evicts a cache entry nobody used for 7 days, and a weekly run is just about that late,
  so on Wednesdays a small job only restores each corpus.
- **Findings.** A crash, timeout or out-of-memory fails the job. GitHub emails a failed scheduled run to whoever last
  changed the schedule.

### Findings stay private

A crashing input for a terminal can be a security bug: something a program prints, or a file someone `cat`s, could
crash everyone's terminal, or worse. On a public repository the job log, the job summary and the artifacts are readable
by anyone, and any logged-in GitHub user can download artifacts. libFuzzer's log contains the failing input, as hex,
as text and as Base64, so:

- libFuzzer's output goes to a file on the runner, which is discarded with it. The job log shows only progress lines
  and statistics, which carry no input bytes.
- The job summary ([fuzz/report.py](../../fuzz/report.py)) has, per finding: the target, the kind, the input's size
  and SHA-256, and the top frames of the .NET stack trace (method names and `file:line`, the exception message cut
  to 160 characters). That is enough to know where the bug is, and not a reproduction.
- The input is uploaded only when a manual run sets `keep_reproduction`, minimized and kept for one day. Use it only
  for a finding known not to be a security issue (a hang in TOML parsing, say), download it and delete the artifact.
- The corpus cache never holds a finding: libFuzzer writes failing inputs to `findings/`, not to the corpus. The cache
  isn't secret either (a pull request's workflow can restore the default branch's caches), so treat it as public.

Handle a finding as a [vulnerability report](../../SECURITY.md#reporting-a-vulnerability): open a private security
advisory (Security › Advisories › New draft advisory), not a public issue, until it is understood. Reproduce it
locally (below): the stack trace usually points at the code, and fuzzing that target locally finds the input again in
minutes. The fix gets a unit test with the input (or a smaller one written from it), and is released before the
advisory is published.

## Locally

Linux, with clang (with libFuzzer, `-fsanitize=fuzzer`) and the .NET SDK. Nothing is installed system-wide:

```sh
dotnet tool restore                                  # SharpFuzz.CommandLine (.config/dotnet-tools.json)
fuzz/build-driver.sh ~/.cache/erikterm-fuzz/libfuzzer-dotnet
export LIBFUZZER_DOTNET=~/.cache/erikterm-fuzz/libfuzzer-dotnet

fuzz/run.sh terminal 600                             # one target for 10 minutes; any of the six
```

[fuzz/run.sh](../../fuzz/run.sh) builds and instruments the harness into `artifacts/fuzz/bin`, adds the seeds to
`artifacts/fuzz/corpus/<target>` (which keeps growing between runs, like the CI cache) and uses the dictionary when
there is one. More seed files or directories can follow the seconds; `LIBFUZZER_ARGS` passes libFuzzer flags, e.g.
`LIBFUZZER_ARGS="-jobs=8 -workers=8"` for eight processes. Findings land in `artifacts/fuzz/findings/<target>/`
(`crash-*`, `timeout-*`). Replay one without the fuzzer, with the full exception:

```sh
dotnet artifacts/fuzz/bin/ErikTerm.Fuzz.dll <target> --repro artifacts/fuzz/findings/<target>/crash-...
dotnet run -c Release --project fuzz/ErikTerm.Fuzz -- <target> --repro <file>    # the same, built normally
```

`--repro` prints `no failure` when the input passes, and otherwise fails with the exception and its stack. A timeout or
an out-of-memory input replays the same way (the run is just slow, or needs `DOTNET_GCHeapHardLimit=0x40000000` to
fail the same way).

The harness is compiled on every build (the code quality job in [build.yml](../../.github/workflows/build.yml), and
`scripts/check.sh`), so it can't fall behind the code it calls.
