# The Windows console host, and what it taught us about performance

On Windows, a terminal does not talk to the program in its pane directly. The program talks to a console host
(ConPTY), and the host talks to the terminal. Replacing the host that comes with Windows by the newer one Microsoft
ships separately made erikterm **2 to 14 times faster**, with no change to erikterm's parser or renderer.

This page records what we measured, what we changed, and how to avoid the mistakes we nearly made.

Full results are in the [benchmark review](https://claude.ai/artifact/2KPhb7W6QTKUzRGhasC1nX) (private). The test
procedure is in [testing/windows-performance.md](../../bench/testing/windows-performance.md).

## The result

Same binary, same machine (Ryzen 9 9950X, 120×32 window, 20 MiB workloads), median of nine repeats. Only the host
differs.

| MB/s | Host in Windows | Bundled host | Gain | Windows Terminal |
|---|---:|---:|---:|---:|
| Plain text | 30.1 | 430.8 | 14× | 429.9 |
| 256-colour | 24.9 | 142.1 | 5.7× | 123.3 |
| Truecolor | 31.3 | 169.5 | 5.4× | 147.7 |
| Unicode | 27.6 | 155.9 | 5.6× | 133.7 |
| Cursor movement | 41.9 | 129.8 | 3.1× | 125.1 |
| Scroll regions | 44.2 | 103.0 | 2.3× | 98.1 |

The bundled host also removed a rendering bug: a stray copy of the last character at the right edge, where a wide
character wraps. Startup, memory and idle CPU did not change.

## Why it matters so much

The host in Windows parses the program's output, updates its own copy of the screen, and generates new escape
sequences for the terminal. The newer host largely passes output through.

We saw this directly. The same 361 MB written by the program arrived as **362 MB through the bundled host** and
**248 MB through the Windows host**, which had re-rendered it.

Reading the output and throwing it away gave the same speed as parsing and drawing it (32.6 and 467 MB/s for plain
text). So the host set the speed in both cases. Without a pty at all, erikterm's parser does 295 to 808 MB/s, so the
host is still the limit on Windows today.

## What we changed

- `src/ErikTerm.Pty/ConPty.cs` uses `conpty.dll` when it and `OpenConsole.exe` sit next to `erikterm.exe`. Otherwise it
  uses the host in Windows. If the bundled one fails to start, it falls back too.
- `ERIKTERM_CONPTY=system` forces the host in Windows, for comparing the two.
- `scripts/publish-windows.ps1` downloads a pinned release of `Microsoft.Windows.Console.ConPTY` and checks its
  SHA-256. Linux and macOS never touch it.

## Advice

### Finding a bottleneck like this

- **Remove one layer and see if the number moves.** Skip the parser and discard the output. If the speed doesn't
  change, the time is being spent somewhere else, and tuning that code is wasted work.
- **Be suspicious when different programs score the same.** erikterm, WezTerm and Rio all landed at about 25 to 50 MB/s
  on three different renderers. That pointed at something they shared.
- **Change one thing at a time.** The clearest result came from running one binary twice with a single environment
  variable changed. Comparing a local build with a CI build would have left room for doubt.
- **Don't assume a fix carries across platforms.** Linux and macOS have no ConPTY, so this speedup doesn't exist
  there. On those platforms the program writes straight to the kernel's pty.

### Measuring on Windows

- **Set the console to UTF-8 in any test program.** Call `SetConsoleOutputCP(65001)`. A new console starts on the
  system's OEM code page (437 here), and every multi-byte character turns into several wrong ones. Our first pass was
  invalid because of this, and it looked like missing emoji in the terminal.
- **Look at the screen.** Take a screenshot of each terminal before trusting a Unicode number. The speed figures looked
  fine while the output was garbage.
- **Measure the terminal finishing as well as the write ending.** The program's write can return before the terminal
  is done, so wait for the process tree's CPU to go quiet as well.
- **Record which host each terminal really used.** A terminal can load a different `conpty.dll` than you expect:
  Alacritty and Rio picked up the one from a WezTerm installed on `PATH`.
- **Read the console size after a few seconds.** Rio reported 284×74 at start and 120×32 once its window had settled.

### Shipping the bundled host

- **Keep the fallback.** The host in Windows is always there, so a missing or blocked file should never leave a pane
  unable to start.
- **Close and resize with the library that created the console.** An `HPCON` from `conpty.dll` must go back to
  `conpty.dll`, which is why `ConPty.cs` remembers which ones it made.
- **Keep the version pinned and hash-checked,** and update it deliberately. When you do, rerun the Windows benchmark and
  screenshots, since this one file now decides most of erikterm's throughput on Windows.
- **Include the license** when packaging. The package is MIT licensed but holds no license file, so ours is in
  `packaging/windows/conpty-LICENSE.txt` and ends up in the download's `licenses/` folder.

## What this didn't fix

These are in erikterm itself. They were measured at `ce02b89`, before the slower idle tick landed, so re-measure first.

- **Idle CPU, about 220 ms per 15 s against 3 to 12 for other terminals.** Two process snapshots a second (Toolhelp) at
  about 7.4 ms each account for nearly all of it. Linux uses `/proc`, so this is Windows only.
- **Scrollback compaction doesn't run once output stops.** The reader blocks in `ReadFile`, so its idle step never
  runs. After a 100k-line flood and 40 s of quiet, memory had not gone down. Fixing it should help the RAM figures.
- **Startup, about 154 ms to the first frame.** Fonts take 40 ms and the renderer 37 ms.

The first two are the next things to do on Windows.
