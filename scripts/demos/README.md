# README demos

The animated clips in the [README](../../README.md) are recorded with these scripts, off-screen and in a throwaway demo
world: a fresh home directory with a short bash prompt (directory, git branch and a `.NET 10.0` segment), a small
invented C# repository (`tidewatch`, a tide forecast CLI with TUnit tests), a neutral fake coding agent (`helper`), a
stand-in `dotnet` and a fixed config. Nothing from your own setup appears, and no window opens.

```sh
scripts/demos/record.sh hero       # one clip -> docs/media/hero.webp
scripts/demos/record-all.sh        # every clip
```

Re-record a clip when the part of the UI it shows changes. The first run publishes erikterm to `artifacts/demos/bin`;
pass a binary as the second argument to use another build. Every run builds the demo tool (incremental, a second or
two) into `artifacts/demos/tool`. You need bash, git, the .NET 10 SDK, ffmpeg with libwebp, and ImageMagick for
`sheet.sh`.

| File | What it is |
|---|---|
| `clips/NAME.steps` | The clip's script: debug-script steps, one per line (see `tool/Steps.cs` for `typeslow:`, `run:` and `end`) |
| `clips/NAME.set` | Optional `KEY=VALUE` config overrides for that clip |
| `tool/` | `demo`, a small console project (no packages, not in `ErikTerm.slnx`) with a command per job, below |
| `tool/World.cs` | `demo world`: builds the demo home, repository, agent, `dotnet` and config in `artifacts/demos/world` |
| `tool/Tidewatch/` | The demo repository's source: compiled into `demo` and copied into the world as the repository's files |
| `tool/WorldTests.cs` | The demo repository's tests |
| `tool/Agent.cs` | `helper`: the fake agent; reports its state with OSC 7501, never runs a real agent |
| `tool/FakeDotnet.cs` | The world's `dotnet`: steady `dotnet build` / `dotnet test` output, `dotnet run -- chart` draws the tide chart, `dotnet run -- serve` prints a web host's request log, and `dotnet nuget list source` |
| `tool/Steps.cs` | `demo steps`: turns a `.steps` file into the debug script |
| `tool/Assemble.cs` | `demo assemble`: turns the recorded frames into a steady-rate WebP (and a GIF and PNG for checking) |
| `sheet.sh` | A contact sheet of a clip's frames, for checking it (`KEEP_FRAMES=1` keeps them) |

The world's `dotnet` and `helper` are links to `demo` in its `~/.local/bin`; `demo` takes its command from the name
it was started as. The real SDK is never run inside a recording: a restore and build would be slow, need NuGet and
vary from run to run. Its output follows the .NET 10 terminal logger and Microsoft.Testing.Platform, and the real
project does build and its tests pass (`dotnet test` in `artifacts/demos/world/home/tidewatch` with the real SDK).

Recording uses three debug settings of erikterm: `ERIKTERM_RECORD_DIR` saves every drawn frame between the steps
`record:on` and `record:off`, `ERIKTERM_RECORD_MIN_MS` sets the shortest gap between saved frames, and
`ERIKTERM_SCRIPT_STEP_MS` runs debug-script steps faster than the usual 350 ms. The step `typex:TEXT` types text with
C# escapes, so it can type a space (`\x20`).

Keep clips under about 2.5 MB each and check them before committing: look at a contact sheet, and make sure no text
is cut off and nothing personal shows. Start and end each clip on a busy screen, not an empty prompt: GitHub shows the
first frame before the animation plays, and the hero's last frame is also `hero.png`. If the screen layout or the
prompt changes, check that `quickselect.steps` still picks a
commit hash, since quick select labels depend on position. `Moon.cs` keeps its lines short because the hero and
palette clips show it in half a window.
