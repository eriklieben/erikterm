# Windows performance run

Instructions for an agent session on a Windows machine. It has no other context, so everything it needs is here.

**Goal:** measure erikterm against the other Windows terminals in the 11 categories of the Linux comparison, say where
erikterm is outside the top 3, and collect what is needed to explain why. This is a measuring job: no changes to the
project.

Work through the sections in order. If a step can't be done as written, say so in the report and carry on; never fill
in an estimate where a measurement is missing.

---

## 1. Rules

- **Don't change the project.** No commits, pushes, tags, branches or pull requests. Build experiments only in the
  throwaway clone under the workspace.
- **Leave the machine as you found it.** Don't change the default terminal, power plan, Defender settings, registry,
  `PATH`, shell profiles, fonts, or the user's settings of any terminal. Don't run anything as administrator or
  install anything system-wide without asking first. Prefer portable builds.
- **Only stop what you started.** End processes by the PID you launched, never by name. Use a Job Object per run so
  a stuck run can be ended as a whole.
- **Windows take focus.** Terminals open during the run. Tell the user before the first one and wait for their go.
- **Stay out of private files.** Workloads and probes only touch the workspace.
- **Don't test inside yourself.** Each terminal is a new process. Remove `ERIKTERM_SOCKET`, `ERIKTERM_PANE`, `TMUX`,
  `TMUX_PANE`, `WT_SESSION` and `WT_PROFILE_ID` from the environment of what you launch.

## 2. Before starting

Record all of this for the report:

- Windows edition and build, `conhost.exe` file version.
- CPU, core count, RAM, GPU and driver version.
- Display resolution, scale and refresh rate.
- On battery or plugged in, and the active power mode. Plugged in is required; if it isn't, ask the user.
- .NET SDK version, and whether the MSVC linker is available (needed only for section 9).

Then close nothing of the user's, but check the machine is quiet: total CPU below about 5% for 10 seconds. Tell the
user it takes roughly an hour, that windows will take focus, and that they shouldn't use the machine meanwhile. Wait
for their go.

## 3. Workspace

Everything goes in `%USERPROFILE%\erikterm-perf\`: `data`, `cfg`, `tools`, `terminals`, `build`, `raw`, `results`.

If `%LOCALAPPDATA%\erikterm\history.json` exists, copy it to the workspace now and put it back at the end: erikterm
may add the test command to it.

## 4. The terminals

Use portable downloads from each project's own release page, in `terminals\`. Record the version and URL.

| Terminal | What to get | How to start it in its own process |
|---|---|---|
| erikterm | CI artifact, see below | `erikterm.exe --no-restore --new-process --config-file <cfg> -e <cmd>` |
| Windows Terminal | Portable zip from `microsoft/terminal` releases; needs a `.portable` file next to the exe | start `WindowsTerminal.exe` directly, not `wt.exe` |
| WezTerm | Zip from `wez/wezterm` releases | `wezterm-gui.exe --config-file <cfg> start --always-new-process -- <cmd>` |
| Alacritty | Portable exe from `alacritty/alacritty` releases | `alacritty.exe --config-file <cfg> -e <cmd>` |
| Rio | Portable build from `raphamorim/rio` releases, if there is one | see `--help`; skip it and say so if it has none |
| Windows console | The system's `conhost.exe` | `conhost.exe <cmd>` |

Check whether Ghostty, Kitty or Foot has an official Windows build. As far as we know none does; note what you found.
Check every command line against the program's own `--help`, since versions differ.

**erikterm build:** test the newest commit on `main` that has a green CI run:

```
gh run download --repo eriklieben/erikterm <run-id> -n erikterm-win-x64 -D terminals\erikterm
```

Keep the DLLs next to `erikterm.exe`. Record the commit hash.

**Settings, the same for every terminal,** in private config files under `cfg\`:

| Setting | Value |
|---|---|
| Window size | 120 columns, 32 rows |
| Font | `JetBrainsMono Nerd Font` if installed, else `Cascadia Mono`, else `Consolas`. The same one everywhere. Don't install fonts |
| Font size | 11 |
| Scrollback | 100000 lines, or the terminal's maximum (Windows Terminal stops at 32767, conhost at 9999). Report the value used |
| Cursor | Not blinking where that can be set; report it where it can't |
| Background | Opaque, no acrylic or blur |
| When the program exits | Close the window with no prompt |
| Update checks | Off |

erikterm's config:

```toml
font = "Cascadia Mono"
font_size = 11
line_height = 1.0
scrollback_lines = 100000
initial_cols = 120
initial_rows = 32
```

## 5. Workload files

Needs Python 3; if it isn't there, ask the user. Save this as `tools\gen.py` and run `python tools\gen.py data`:

```python
import random,sys
d=sys.argv[1]; R=random.Random(42); M=20<<20
def write(name,lines):
    n=0
    with open(f'{d}/{name}.bin','wb') as f:
        while n<M:
            b=next(lines); f.write(b); n+=len(b)
W='lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua'.split()
def txt(n=14): return ' '.join(R.choice(W) for _ in range(n))
def ascii_():
    while True: yield (txt()+'\n').encode()
def c256():
    while True: yield (''.join(f'\x1b[38;5;{R.randrange(256)}m{R.choice(W)} ' for _ in range(12))+'\x1b[0m\n').encode()
def tc():
    while True: yield (''.join(f'\x1b[38;2;{R.randrange(256)};{R.randrange(256)};{R.randrange(256)}m{R.choice(W)} ' for _ in range(12))+'\x1b[0m\n').encode()
U='日本語の文章 한국어 текст ελληνικά é̃ 🙂 🚀 👩‍💻 ñandú'.split()
def uni():
    while True: yield (' '.join(R.choice(U) for _ in range(18))+'\n').encode()
def cur():
    while True: yield (''.join(f'\x1b[{R.randrange(1,30)};{R.randrange(1,90)}H{R.choice(W)}' for _ in range(20))).encode()
def reg():
    yield b'\x1b[3;25r\x1b[25;1H'
    while True: yield (txt()+'\n').encode()
for n,g in [('ascii',ascii_),('color256',c256),('truecolor',tc),('unicode',uni),('cursor',cur),('scrollregion',reg)]: write(n,g())
with open(f'{d}/lines100k.bin','wb') as f:
    for i in range(100000): f.write(f'{i:06d} {txt(10)}\n'.encode())
```

The files must be byte-identical to the Linux ones. Check SHA-256 before going on:

| File | SHA-256 |
|---|---|
| `ascii.bin` | `f3436cbcdca92b826557534fce0ac274671bb7e4a31d31157fefd8c2f2469b18` |
| `color256.bin` | `5ae9a024e178088f7aa72a4d83dcc8d22c67ba589fc073e1f28d17112b6a81f2` |
| `truecolor.bin` | `8d3670aa8f763e70daa2285d0ee08e91080ba0db65b33f0dea7a2544fc0d8759` |
| `unicode.bin` | `607e279f4b8fa1b30ca6946aa41381cf9ab314c1096fda074423836e241217de` |
| `cursor.bin` | `d19ee16d736d0853ba84637798e470c9441738f250a371fb891c71d6c1cb7591` |
| `scrollregion.bin` | `1cab811a2a9865d1c96c5f360b5675185babea2a6aa11a3c0c9639cd6d86343a` |
| `lines100k.bin` | `6328a3b13d30c6ac3a7af3f59fc57c2ec31eebb6c140527d3ee9356976dc3b0b` |

## 6. The two tools to write

Put the source under `tools\`. Write both in C# on .NET 11. The child must be published with Native AOT if the MSVC
linker is present, because its own startup counts in the startup figure. If it can't be, use ReadyToRun and say so.

### `perfchild.exe`: runs inside the terminal under test

1. **First statement of `Main`:** read the time with `GetSystemTimePreciseAsFileTime`, and append it to the run's log
   as the start. Every later event also gets a precise timestamp in that log (JSON lines in `raw\`).
2. Log the console size from `GetConsoleScreenBufferInfo`, and the program's parent PID.
3. Enable `ENABLE_PROCESSED_OUTPUT | ENABLE_VIRTUAL_TERMINAL_PROCESSING` on stdout, and leave
   `DISABLE_NEWLINE_AUTO_RETURN` off, so that LF moves to the next line start like a Unix tty.
4. Run the phases below, then exit with code 0 so the terminal closes.

Output is always `WriteFile` to the stdout handle, in 64 KiB chunks. `Reset` means writing `ESC[r ESC[3J ESC[2J ESC[H`.

**Pass A, the same sequence as on Linux, with nothing in between:**

| Step | What happens | Logged |
|---|---|---|
| 1 | Wait 4 s | |
| 2 | Wait 15 s | `idle_start`, `idle_end` |
| 3 | For each of `ascii color256 truecolor unicode cursor scrollregion`, three times: Reset, write the file | start and end of each write |
| 4 | Reset, wait 1 s, write `lines100k.bin`, wait 3 s | `final` |

**Pass B, how long the terminal takes to really finish:** for each of the six workloads once: wait 3 s, Reset, write
the file, then wait until the driver's file `raw\<run>.settled` appears (at most 120 s).

### `perfdriver.exe`: starts the runs and samples

For each run:

1. Write the time with `GetSystemTimePreciseAsFileTime` immediately before `CreateProcess`, as `T0`.
2. Start the terminal in a Job Object, with `perfchild.exe` as its command.
3. Every 20 ms, for every process in the terminal's tree except `perfchild.exe` (the terminal, plus any
   `conhost.exe` or `OpenConsole.exe` under it; refresh the tree with Toolhelp once a second), sample:
   `QueryProcessCycleTime`, `GetProcessTimes`, and `GetProcessMemoryInfo` (`WorkingSetSize`, `PeakWorkingSetSize`,
   `PrivateUsage`). Keep the process image path and its file version: it shows which console host each terminal
   actually uses.
4. Pass B: after each write ends, the run is settled at the first moment the tree's CPU over the last 100 ms is
   below 2 ms. Then create the `.settled` file for the child.
5. Limit each run to 10 minutes. On a timeout end that run's Job Object and record `timeout`.

`QueryProcessCycleTime` is the accurate one for idle CPU. Convert cycles to milliseconds with the TSC rate measured
once (cycles of a spinning thread, `QueryThreadCycleTime`, against `QueryPerformanceCounter`).

### Order of runs

Three rounds, each terminal once per round, rotating which goes first. Run erikterm one extra time at the very end,
to see if the machine drifted. Wait 3 s between runs, and check total CPU has gone back below about 5%.

## 7. What to compute

Use the sum over the terminal's tree. Give the terminal alone as well.

| Category | Definition | Better |
|---|---|---|
| Plain text, 256-colour, Truecolor, Unicode, Cursor, Scroll regions | Pass A: 20 MiB ÷ duration of the write, MiB/s (median of the 9 repeats) | Higher |
| Startup | Child's first timestamp − `T0`, ms (median of the rounds) | Lower |
| Idle CPU | Pass A: CPU between `idle_start` and `idle_end`, ms per 15 s | Lower |
| Idle RAM | Working set at `idle_end`, MB | Lower |
| RAM with 100k lines | Working set at `final`, MB | Lower |
| Peak RAM | Highest summed working set sampled in the run, MB | Lower |

Also report for each workload `done_mb_s` from Pass B: 20 MiB ÷ (write start → settled, minus the 100 ms window).

**Why both.** On Windows the program talks to the console host (ConPTY), which reads the output, and then sends the
terminal a redrawn version. So the write can end long before the terminal has finished, and the write time partly
measures the console host rather than the terminal. Pass B catches that. Show which host each terminal used.

Treat results within 3% as tied. Mark a figure **unstable** if `(max − min) / median` is over 20%, or if the extra
erikterm run differs from the others by over 10%.

## 8. Report

Write `results\report.md` and `results\results.json`, then show the report in your final message along with where the
files are, so that it can be carried back to the Linux session.

**`report.md`** in this order:

1. **Verdict:** in which categories erikterm is in the top 3 and in which not, in a few lines.
2. **Results table:** one row per category, one column per terminal, erikterm's rank in the last column.
3. **`done_mb_s` table** for Pass B.
4. **Which console host each terminal used**, with version.
5. **Machine and versions** from section 2, terminal versions and URLs, the erikterm commit.
6. **Deviations:** every setting not as specified (actual window sizes, scrollback limits, font, blinking), unstable
   figures, skipped terminals, timeouts.
7. **Diagnostics** from section 9, if run.

**`results.json`:**

```json
{
  "commit": "…",
  "machine": { "os": "…", "cpu": "…", "ram_gb": 0, "gpu": "…", "display": "…", "power": "…" },
  "terminals": {
    "erikterm": {
      "version": "…",
      "console_host": { "path": "…", "version": "…" },
      "settings": { "cols": 120, "rows": 32, "font": "…", "font_size": 11, "scrollback": 100000, "blinking": false },
      "metrics": {
        "mb_s":          { "ascii": { "median": 0, "min": 0, "max": 0, "runs": [] } },
        "done_mb_s":     { "ascii": { "median": 0, "runs": [] } },
        "startup_ms":    { "median": 0, "runs": [] },
        "idle_cpu_ms":   { "median": 0, "runs": [] },
        "idle_mb":       { "median": 0, "terminal_only": 0, "runs": [] },
        "mb_100k":       { "median": 0, "terminal_only": 0, "runs": [] },
        "peak_mb":       { "median": 0, "terminal_only": 0, "runs": [] }
      },
      "flags": []
    }
  },
  "deviations": []
}
```

Use `null` with an entry in `deviations` for anything not measured. Keep `raw\`.

## 9. erikterm diagnostics

After the report exists, and only if the MSVC linker is present. Use a throwaway clone in `build\` at the same commit.
Nothing from here goes back into the project. Each point says what to learn:

1. **Startup timeline.** Add `Stopwatch` output at: start of `Main`, around `Sdl.Init` (`App.cs`), around
   `SDL_CreateWindow` and `SDL_CreateRenderer` (`TermWindow.Open`), font load, pty spawn and first present. On Linux
   SDL's setup was the largest part. Which is it here, and which renderer does SDL pick (`SDL_GetRendererName`)?
2. **Speed without ConPTY.** Publish `bench\ErikTerm.Bench` for `win-x64`, run it plain and with `--pty --json`. How
   much is lost going through ConPTY?
3. **ConPTY ceiling.** In `Pane.StartReader`, skip `Term.Feed` for one run so the output is only read and dropped.
   Report MB/s and the average read size per workload.
4. **GC share.** Log `GC.GetTotalPauseDuration()` around one flood in the app, for plain text and Unicode. On Linux
   the app paused far longer than the same data in the bench.
5. **Idle work.** `WindowsPty.Read` blocks in `ReadFile`, so the reader's idle step may not run once output stops.
   After a 100k-line flood and 30 s of quiet, does `Terminal.NeedsCompaction` still hold, and did memory go down?
6. **Newer console host, optional.** If other terminals use a bundled `OpenConsole.exe` and do better, point the clone
   at the `conpty.dll` and `OpenConsole.exe` from the Windows Terminal download and repeat Pass A for erikterm. This
   is an experiment only. Shipping a binary like that is a separate decision.

Report per point: what you changed in the clone, the numbers, what you conclude, and what you couldn't determine.

## 10. Clean up

1. Check that no process you started is still running.
2. Delete `terminals`, `cfg`, `tools`, `build`, and `data` from the workspace. Keep `raw` and `results`.
3. Put `history.json` back if you saved one.
4. Say what is left on the machine: only the workspace's `raw` and `results` should be.
