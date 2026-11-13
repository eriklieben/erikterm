# Code analysis and dependency updates

What checks the code besides the tests, and what keeps the dependencies current. The build itself (tests on three
platforms, the quality gate, coverage) is [build.yml](../../.github/workflows/build.yml); see
[CONTRIBUTING.md](../../CONTRIBUTING.md#build-and-test).

| Check | Workflow | When | Needs |
| --- | --- | --- | --- |
| CodeQL | [codeql.yml](../../.github/workflows/codeql.yml) | pushes to main, pull requests, Mondays | a public repository (or GitHub Advanced Security) |
| OpenSSF Scorecard | [scorecard.yml](../../.github/workflows/scorecard.yml) | pushes to main, branch protection changes, Mondays | a public repository |
| SonarQube Cloud | the `sonar` job in [build.yml](../../.github/workflows/build.yml) | pushes to main, after the tests | the secret `SONAR_TOKEN` |
| Fuzzing | [fuzz.yml](../../.github/workflows/fuzz.yml) | Saturdays, by hand | a public repository (the schedule) |
| Dependabot | [dependabot.yml](../../.github/dependabot.yml) | Mondays | nothing |

Fuzzing (libFuzzer on the VT parser, the image decoders and the TOML reader, and how its findings are kept private)
has its own page: [Fuzzing](fuzzing.md).

## CodeQL

GitHub code scanning for the C# and for the workflows in `.github/workflows`, with the `security-extended` queries.
Results are under Security → Code scanning, and a pull request shows the new ones.

The C# is analysed with build mode `none`: CodeQL reads the sources without compiling them. A real build would need
SDL3, FreeType and HarfBuzz on the runner and gains little, because the shipped code has no package dependencies for
CodeQL to resolve. `setup-dotnet` still installs the SDK from `global.json`, so the restore of the test project
(TUnit) works.

Code scanning is free for public repositories; a private one needs GitHub Advanced Security. While the repository is
private, the job skips itself on pushes and pull requests. Scheduled runs carry no repository in their event, so they
can't tell; they wait for the repository variable `PUBLIC_REPO` = `true` (Settings › Secrets and variables › Actions ›
Variables), set when the repository is public. The same goes for Scorecard.

## OpenSSF Scorecard

[Scorecard](https://scorecard.dev) scores supply-chain practices: actions pinned to a commit, least-privilege tokens,
branch protection, code review, signed releases, a dependency update tool, a security policy, and so on. Failed checks
show under Security → Code scanning with what to change. `publish_results` sends the result to scorecard.dev, which
the README badge reads.

It only works on a public repository. scorecard.dev also checks the workflow file before it accepts a result: no
top-level `env` or `defaults`, no write permissions at the top, a GitHub-hosted Ubuntu runner, and only the actions it
lists. Keep it that way when changing [scorecard.yml](../../.github/workflows/scorecard.yml).

## SonarQube Cloud

The `sonar` job in [build.yml](../../.github/workflows/build.yml) runs on pushes to main, after the three test jobs,
the integration scenarios and the release scripts. It downloads their coverage artifacts (`coverage-linux`,
`coverage-macos`, `coverage-windows`, `coverage-integration-linux`, `coverage-integration-windows`,
`coverage-integration-macos`, and `coverage-eng` for `eng/`) from the same run,
points their file paths at its own checkout ([scripts/sonar-coverage.py](../../scripts/sonar-coverage.py)), and wraps
one Release build of `src/ErikTerm.App` with SonarScanner for .NET. It runs no tests.

Why this shape:

- **No second test run.** The suite runs on three platforms already, and a Linux-only run would lose the coverage of
  the Windows and macOS code.
- **The token stays on main.** Only pushes to main run the job, and it checks out the commit its own run is for.
  Pull requests and forks are not analysed; the analysis of main after the merge picks the change up. A separate
  `workflow_run` workflow would also work, but it checks out a commit from another run while it holds the token,
  which code scanning (CodeQL, Scorecard's Dangerous-Workflow) flags.
- **Documentation-only pushes** skip the macOS and Windows tests (the `scope` job), so the analysis is skipped too.

The settings are in [build/SonarQube.Analysis.xml](../../build/SonarQube.Analysis.xml): the benchmark, scripts,
packaging, `contrib/` and generated code are left out, compiler warnings aren't imported (the quality job gates the
SDK analyzers), and the coverage comes from the four Cobertura reports and the lcov of `eng/` (see [Coverage](#coverage)). The scanner is a CI tool in
[.config/dotnet-tools.json](../../.config/dotnet-tools.json); it isn't part of erikterm. Only `src/ErikTerm.App` is
built, which brings in the libraries; the test project compiles some app files a second time, and building it too
would report those files twice.

Without the secret `SONAR_TOKEN` the job stops after its first step with a notice, so the workflow can be merged
before the project exists. The organization and project key are `eriklieben` and `eriklieben_erikterm`; the
repository variables `SONAR_ORGANIZATION` and `SONAR_PROJECT_KEY` override them. Quality profile: *Sonar way*.

## Coverage

Each test job runs the suite with Microsoft's code coverage extension (`--coverage`, Cobertura output) and the
settings in [tests/ErikTerm.Tests/coverage.config](../../tests/ErikTerm.Tests/coverage.config). Locally, from the
repository root (the settings path is resolved against the working directory):

```sh
dotnet test --project tests/ErikTerm.Tests --coverage --coverage-output-format cobertura --coverage-output coverage.xml --coverage-settings tests/ErikTerm.Tests/coverage.config --results-directory coverage
python3 scripts/merge-coverage.py coverage/coverage.xml
```

**What is counted.** Code under `src/` that a test can run, wherever it was compiled:

- The four libraries (`ErikTerm.Core`, `Config`, `Ui`, `Pty`), which the test project references.
- The part of `ErikTerm.App` the test project compiles in itself. The app is an AOT executable with native
  dependencies, so the test project doesn't reference it; it links the app's testable files instead (the
  `<Compile Include="../../src/ErikTerm.App/...">` items in ErikTerm.Tests.csproj). That code ends up in the test
  assembly, which the coverage tool skips by default. `IncludeTestAssembly` takes it in, and the `Sources` filter
  keeps only files under `src/`, so the tests' own code is never counted.
- Not counted: the tests, generated code (anything under `obj/`, `*.g.cs`), and app files that aren't compiled into
  the tests. Those last ones aren't in the reports at all.

`IncludeTestAssembly` is a top-level setting, a sibling of `CodeCoverage`; nested inside `CodeCoverage` it is
ignored without a warning. An XML comment in the file must not contain two hyphens in a row: the tool then rejects the
whole file with "Provided settings file doesn't exist or is invalid".

**The gate.** The `coverage` job merges the reports (the three test jobs' and the three integration jobs') ([scripts/merge-coverage.py](../../scripts/merge-coverage.py)):
a line counts as covered when any platform ran it. Lines are grouped by the project their file lives in
(`src/<project>/...`), not by the assembly that ran them, so the app's linked files count as `ErikTerm.App` and never
inflate the libraries. The four libraries must each reach 80%; the total is over them alone. `ErikTerm.App` follows
without a gate, with how many of its source files are in the reports: its percentage covers only the linked files.

**The integration scenarios** ([Integration scenarios](../../bench/README.md#integration-scenarios)) are the other
half of the app's coverage: much of `ErikTerm.App` (the window, its event loop, rendering, overlays, the window
features) only runs with a real window. The `integration (linux)` job runs them in an Arch Linux container (Ubuntu
24.04 has no SDL3) under dotnet-coverage, with
[tests/ErikTerm.IntegrationTests/coverage.config](../../tests/ErikTerm.IntegrationTests/coverage.config): the
`erikterm` processes the tests start are measured (the app and the four libraries, sources under `src/`), and the
report is uploaded as `coverage-integration-linux`. `integration (windows)` and `integration (macos)` run the same
scenarios on GitHub's Windows and macOS runners (`coverage-integration-windows`, `coverage-integration-macos`), for
the code only those systems run: ConPTY and the Windows process tree, `WindowsFontLocator` and `MacFontLocator`, cmd,
PowerShell and the macOS login shell, the macOS spawn helper and socket directory; and, with a real window on the
runner's desktop (`ERIKTERM_IT_DESKTOP=1`, `Scenarios/DesktopTests.cs`), the UI Automation provider in
`Accessibility/`, the Win32 hit test and caption double click in `Chrome/` and the DWM backdrop. The `coverage` and
`sonar` jobs pick these up with the test jobs'
reports (`coverage-*`), and a line counts as covered when any report ran it, so the per-project totals in the
`coverage` job and SonarQube Cloud's include what the scenarios ran. On macOS dotnet-coverage collects nothing so far
(not even for the test process; the cause isn't known yet), so `coverage-integration-macos` is empty: the scenarios still run and
check the behaviour, and the macOS-only code is measured by the unit tests alone. `scripts/sonar-coverage.py` warns
about an empty report and fails on one that has data but no file under `src/` (a path it couldn't map). Locally:

```sh
scripts/integration-tests.sh --coverage coverage/integration/coverage.xml
python3 scripts/merge-coverage.py coverage/coverage.xml coverage/integration/coverage.xml
```

What stays uncovered is mostly what no scenario can drive: system dark-mode and accessibility settings changing,
the compositor's drag-and-drop between processes, real tmux handoff, and key events as the platform delivers them
(the debug script's keys skip SDL's event translation, so `MacKeys` is left to the unit tests).

**JavaScript.** The release scripts job runs `node --test eng/` with node's own coverage and writes
`coverage/lcov.info` (the test files left out, paths relative to the checkout), uploaded as `coverage-eng`; the sonar
job passes it to SonarQube Cloud as `sonar.javascript.lcov.reportPaths` (build/SonarQube.Analysis.xml).

**SonarQube Cloud** imports the same reports. It knows every app file from its own build of `src/ErikTerm.App`, so app
files missing from the reports count as uncovered there, and its `src/ErikTerm.App` percentage is over the whole app.
That is the honest figure for the app; the one in the `coverage` job says how well the linked part is tested.

## Dependency updates

[Dependabot](../../.github/dependabot.yml) opens the update pull requests, on Mondays, one group per ecosystem:

| What | Commit type | Releases? |
| --- | --- | --- |
| GitHub Actions (pinned to a commit SHA, version in a comment) | `ci(deps)` | no |
| The .NET SDK in `global.json` | `build(deps)` | no |
| The test project's packages (TUnit) | `build(deps)` | no |
| The SonarQube Cloud scanner (`.config/dotnet-tools.json`) | `ci(deps)` | no |
| Release and commit tooling in `package.json` | `build(deps-dev)` | no |

None of them is `fix(deps)`, because nothing that ships has a package dependency. Updates wait until a release is
three days old (`cooldown`), as compromised releases are usually pulled by then. Security updates arrive as soon as
an alert is raised, outside the schedule.

Dependabot and not Renovate (which labcoat uses): erikterm has a handful of dependencies, none of them shipped, so
labcoat's rules for which package gets `fix` and which gets `build` aren't needed. Dependabot needs no app installed,
works the same while the repository is private, and handles SHA-pinned actions and security updates by itself.
Renovate is the better choice once there are many packages to group or rules per file.

## README badges

| Badge | Shows once |
| --- | --- |
| build, CodeQL | the repository is public (workflow badges of a private repository don't load for others) and, for CodeQL, the workflow has run on main |
| release | the repository is public and has a release (shields.io can't see private repositories) |
| license, platforms, .NET 11, semantic-release | always (static) |
| SonarQube Cloud (quality gate, coverage, ratings) | the project is public on SonarQube Cloud and main has been analysed |
| OpenSSF Scorecard | the repository is public and scorecard.yml has published a result |

The OpenSSF Best Practices badge isn't there yet: it needs the project id from
[bestpractices.dev](https://www.bestpractices.dev) after registering. Then add, next to the Scorecard badge:

```html
<a href="https://www.bestpractices.dev/projects/ID"><img src="https://www.bestpractices.dev/projects/ID/badge" alt="OpenSSF Best Practices"></a>
```

## Setting it up

On GitHub (Settings of the repository):

1. Make the repository public. CodeQL, Scorecard, the badges and SonarQube Cloud's free plan depend on it.
2. **Advanced Security:** turn on *Private vulnerability reporting* (SECURITY.md links to it), *Dependency graph*,
   *Dependabot alerts* and *Dependabot security updates*. Code scanning needs no setup: codeql.yml is an advanced
   setup, so leave the *default setup* off.
3. **Rules:** protect main (pull requests, required checks, signed commits); the Scorecard Branch-Protection check
   reads this.

On [SonarQube Cloud](https://sonarcloud.io):

1. Log in with GitHub, and import the repository into the `eriklieben` organization (key `eriklieben_erikterm`).
2. Under Administration → Analysis Method, turn **Automatic Analysis off**: the CI analysis replaces it.
3. Quality profile *Sonar way* for C#; new code relative to the previous version.
   The `sonar` job passes the last release's version (its `v*` tag), so new code is what changed since that release.
4. Create a token (My Account → Security) and add it to the repository as the secret `SONAR_TOKEN`.
5. Push to main, or re-run the last *SonarQube Cloud* run under Actions.

On [OpenSSF Best Practices](https://www.bestpractices.dev): log in with GitHub, add the project, fill in the criteria
and add the badge with its id as above.
