# Testing and benchmarking erikterm

How to run erikterm's test suite and benchmarks, and how to test it by hand on Windows and macOS. For the architecture and building, see [Developing erikterm](../docs/development/README.md).

## Tests

`tests/ErikTerm.Tests` uses [TUnit](https://tunit.dev) on Microsoft.Testing.Platform (`global.json` selects that
runner for `dotnet test`); the test project is its own executable.

```sh
dotnet test --project tests/ErikTerm.Tests                                                # everything
dotnet test --project tests/ErikTerm.Tests --treenode-filter '/*/*/TmuxParserTests/*'       # one class
dotnet test --project tests/ErikTerm.Tests --treenode-filter '/*/*/PtyTests/ExitCodeIsPropagated'  # one test
dotnet test --project tests/ErikTerm.Tests --treenode-filter '/*/*/*/*Reflow*'              # by test name
dotnet run --project tests/ErikTerm.Tests -- --treenode-filter '/*/*/ReflowTests/*'         # as a plain exe
```

The filter is `/assembly/namespace/class/test`, with `*` wildcards. Linux-only and Windows-only tests are marked
`[RunOn(OS.Linux)]` / `[RunOn(OS.Windows)]` and show up as skipped elsewhere. CI runs the suite on every push on
Linux, macOS and Windows, and all three gate the builds (the Windows
job puts vcpkg's FreeType/HarfBuzz/SDL3 DLLs next to the test assembly).

**Arrange / Act / Assert.** Every test body is split by `// Arrange`, `// Act` and `// Assert` lines (no Arrange
when there is nothing to set up). Act is the behaviour under test, with its results in named locals; Assert holds only
`await Assert.That(...)` lines. One behaviour per test: cases of the same shape are `[Arguments]` /
`[MethodDataSource]` rows, different behaviours are separate tests. A walk-through where each step depends on the
previous one (a state machine, a command sequence) stays one test: one Act that records every step, then one Assert
block — or numbered `// Act 1` / `// Assert 1` steps when it has to wait between steps, with a comment saying why.

**Parallelism.** TUnit runs every test in parallel, tests of the same class included. A test that uses something
process-wide takes a `[NotInParallel(Isolation.X)]` key from `Support/Isolation.cs` (`Processes`: real processes and
PTYs; `Sockets`: real Unix sockets; `FileWatchers`: inotify waits; `Natives`: FreeType or the DllImport resolver), so
tests sharing a key never overlap. Performance bounds use a bare `[NotInParallel]` (runs alone) and measure CPU time
(`Support/ThreadCpuTime.cs`), not wall-clock time. Wait for other threads or processes with
`await Eventually.True(...)` / `Eventually.Value(...)` (polls, generous deadline) instead of sleeps, give temporary
directories unique names, and pass environment lookups and paths explicitly: no test connects to a real erikterm
(`$ERIKTERM_SOCKET`, `$XDG_RUNTIME_DIR/erikterm`) or touches its session files, so the suite is safe to run from
inside erikterm.

**Converting xUnit tests** (e.g. from a branch started before the switch):

```sh
scripts/xunit-to-tunit.py --build tests/ErikTerm.Tests/NewTests.cs   # convert in place, compiler-assisted
scripts/xunit-to-tunit.py --check tests/ErikTerm.Tests/*.cs          # list what still needs a hand
```

It converts attributes, assertions and signatures, and adds the AAA markers where a test's shape makes them clear.
`--build` lets the compiler name each assertion's types (collections compare with `IsEquivalentTo`, spans become
arrays, numbers widen like xUnit's `Equal<T>`) and then lists the errors and warnings left. `// TODO(AAA)` marks
tests to split by behaviour by hand, `// TODO(TUnit)` what it could not convert (e.g. `await` inside `lock`).
Convert all of a branch's xUnit files in one `--build` run: a file still on xUnit stops the compiler from checking
method bodies anywhere in the project. Then check `[NotInParallel]` for anything process-wide.

## Benchmark

```sh
scripts/bench.sh                                    # in-process parser + screen throughput
scripts/bench.sh --pty                              # also end-to-end through a real PTY
scripts/bench.sh --baseline bench/baseline.json     # compare against the committed baseline
scripts/bench.sh --list                             # workloads
scripts/bench.sh --only ascii --reflow              # also time Resize() reflowing a full 10k-line scrollback
scripts/bench.sh --memory out.txt --scrollback 100000 --cols 140   # heap + RSS of the scrollback for that output
```

Each workload reuses one `Terminal`, so after warm-up the scrollback is full and the numbers are
steady state. **`alloc/MB` must stay at 0** — any allocation in the hot path shows up there. Allocations and the
`gc0/1/2` counts cover only the measured feed (for `--pty` rows: the read + parse loop, not spawning `cat` or the
warm-up that fills the scrollback).

Lines enter the scrollback whole and are compacted later (their blank tails dropped) by the PTY reader once
the program has been quiet for 25 ms; `--memory` shows the memory right after the output and after that compaction.

Note: the JIT can beat AOT on code that relies on dynamic PGO (e.g. devirtualising interface calls).
That's why `VtParser<THandler>` is generic over a struct handler — AOT then emits direct calls.

## Testing on other platforms

[the test plan](testing/README.md) is the test plan for Windows and
macOS: setup, a checklist per feature with what is expected to differ from Linux, and scripted reference scenes —
`scripts/reference-shots.sh` / `scripts/reference-shots.ps1` render them to `bench/testing/reference/<os>/` for a
side-by-side comparison with the Linux screenshots.

## Windows performance run

[testing/windows-performance.md](testing/windows-performance.md) is the procedure for benchmarking erikterm against other terminals on Windows, with the format the results should be reported in.
