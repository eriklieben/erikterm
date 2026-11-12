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

### Integration scenarios

`tests/ErikTerm.IntegrationTests` starts the real erikterm (the JIT build of `src/ErikTerm.App`, its `erikterm`
apphost) once per test, off-screen, and checks what it does:

```sh
scripts/integration-tests.sh                                   # every scenario (builds first)
scripts/integration-tests.sh -- --treenode-filter '/*/*/TabTests/*'   # one class (options after -- go to TUnit)
scripts/integration-tests.sh --coverage coverage/integration.xml      # under dotnet-coverage: a Cobertura report
```

**Off-screen and private.** Each run gets SDL's offscreen video driver and software renderer
(`SDL_VIDEODRIVER=offscreen`, `SDL_RENDER_DRIVER=software`), no `DISPLAY`, `WAYLAND_DISPLAY`, D-Bus session,
`ERIKTERM_*`, `TMUX` or `TMUX_PANE`, and its own `HOME` and XDG runtime, state, config, data and cache directories under
`/tmp/etit-*` (short, for the control socket's path limit; `ERIKTERM_IT_KEEP=1` keeps them; macOS `/private/tmp`,
Windows `%RUNNER_TEMP%` or `%TEMP%`). No window opens, and a
scenario never talks to an erikterm you are using or reads your configuration, so the suite is safe to run from
inside erikterm. The programs erikterm hands things to — `xdg-open`/`open`, `notify-send`, `gh`, `$VISUAL`/`$EDITOR`
and the coding agents `claude`, `codex`, `copilot` — are stand-ins first on `PATH` that only write their arguments to
the run's `calls.log` (the agents also report `working` with OSC 7501 and wait), so a test can check what would have
been opened and nothing reaches the desktop. Every scenario starts from `AppRun.BaseConfig` (fixed size, no animation,
no git or GitHub status, no session, `window_close_confirmation = "NeverPrompt"`); a scenario's own settings replace
those keys.

**Driving and checking.** A scenario is a debug script (`ERIKTERM_DEBUG_SCRIPT_FILE`, the steps in
[Debugging](../docs/development/README.md#debugging)) plus `erikterm cli` calls against the instance's socket
(`send-text`, `get-text`, `list --format json`, splits, tabs, layouts, worktrees, agent states). The script step
`wait-file:NAME` pauses the script until the test creates that file (`run.Release("NAME")`), so the test checks the
window at a known point: the pane text (`WaitForTextAsync`), the panes and tabs (`WaitForPanesAsync`), a screenshot
(`shot:NAME.bmp`: `Checks.RenderedAsync` wants a real frame, not a blank surface; `Checks.DifferAsync` that an
overlay, split or theme changed it), the stand-ins' `calls.log`, git's view of a throwaway repository. Every scenario
ends by closing its window and `Checks.CleanExitAsync`: exit status 0, no crash log in the state directory, no
exception, internal error or failed script step in the output. A scenario that only runs things without such a check
doesn't belong here. A scenario is a walk-through: the script, running in the app on its own, is the Act, and the
test checks it at each `wait-file` stop, so its body has numbered `// Act N` / `// Assert N` steps (or `// Act / Assert`
where a step only observes). At most half the cores' worth of erikterm processes run at once (`ERIKTERM_IT_PARALLEL`), since
the steps are timed and the software renderer is single-threaded.

**Windows and macOS.** The same scenarios run there (CI's `integration (windows)` and `integration (macos)` jobs;
`scripts/integration-tests.sh` from Git Bash on Windows). On Windows the panes run Git for Windows' sh
(`usr\bin\sh.exe`, or `ERIKTERM_IT_SH`), so the scenarios type the same commands; the stand-ins are sh scripts there
too, each with a `NAME.exe` copy of `tests/ErikTerm.IntegrationTests.StandIn` beside it (CreateProcess only starts an
.exe), and the link opener is `explorer.exe`. Each run's `%LOCALAPPDATA%` and `%APPDATA%` are its own `state\` and
`config\` (Windows resolves erikterm's `Environment.GetFolderPath` from them), so control sockets, sessions and crash
logs stay in the run's directory as on Linux. `SDL3.dll`, FreeType,
HarfBuzz and the console host (`conpty.dll`, `OpenConsole.exe`) must be in `src/ErikTerm.App/bin/Debug/net10.0/`,
and JetBrains Mono installed (build.yml's `integration (windows)` job shows how). `Scenarios/PlatformTests.cs` covers
what only those systems do off-screen (the system shell, batch files and cmd, the font lookup);
`Scenarios/DesktopTests.cs` needs a real window (UI Automation, the Win32 hit test, the Cocoa window) and runs only with
`ERIKTERM_IT_DESKTOP=1`, which CI's Windows and macOS jobs set: elsewhere those tests are reported as skipped.

**Coverage.** dotnet-coverage (a local tool, `.config/dotnet-tools.json`) runs the test assembly and measures the
erikterm processes it starts, with [tests/ErikTerm.IntegrationTests/coverage.config](../tests/ErikTerm.IntegrationTests/coverage.config)
(the app and the four libraries, sources under `src/`). CI's integration jobs upload that report as
`coverage-integration-linux`, `-windows` and `-macos`; see [Coverage](../docs/development/code-analysis.md#coverage).

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
