# Contributing to erikterm

Thanks for helping out. Bug reports, ideas, docs fixes and code are all welcome. By participating you agree to the
[Code of Conduct](CODE_OF_CONDUCT.md). Security problems: see [SECURITY.md](SECURITY.md), not the issue tracker.

## What goes into erikterm

erikterm is built for its maintainer, [Erik Lieben](https://github.com/eriklieben), who decides what goes in. Every
idea is read, but not every one fits, and a well-made pull request can still be declined. That is a decision about
scope and maintenance, not about your work.

- **Small fixes** (typos, docs, an obvious bug with a clear fix): a pull request is fine.
- **Anything bigger** (a feature, an option, a change in behavior, a new dependency): open an issue first and wait
  for a go-ahead before you write code. A pull request without that may be closed.
- **No new packages in the shipped binary.** `src/` and `bench/` take no NuGet packages; native libraries are loaded
  from the system. The test project may use packages.

## Build and test

You need the .NET 10 SDK (`global.json` pins the version), and SDL3, FreeType and HarfBuzz from your system's package
manager. [Developing erikterm](docs/development/README.md) has the architecture and debugging tips,
[Testing and benchmarking](bench/README.md) the test conventions.

New functionality comes with tests, and a bug fix with a test that fails without it.

```sh
scripts/run.sh                                 # publish with Native AOT and start it
dotnet test --project tests/ErikTerm.Tests     # the tests
scripts/check.sh                               # before you push: the tests and the quality gate, as CI runs them
```

`scripts/check.sh` (on Windows `scripts\check.ps1`) runs the tests, the quality gate and the release scripts' tests
(`node --test eng/`, skipped without Node.js), and ends with a list of what passed and what failed.

CI runs the tests on Linux, macOS and Windows. Each pull request also has to pass:

- **The quality gate:** the SDK analyzers in [build/quality.globalconfig](build/quality.globalconfig), and no method
  more complex than before ([build/complexity-baseline.txt](build/complexity-baseline.txt)). Run it locally:

  ```sh
  dotnet build src/ErikTerm.App -p:Quality=true --no-incremental | tee quality.log
  python3 scripts/check-complexity.py quality.log build/complexity-baseline.txt
  ```

  Made a listed method simpler? Add `--write` to lower its bar.
- **Coverage:** at least 80% of the lines of ErikTerm.Core, ErikTerm.Config, ErikTerm.Ui and ErikTerm.Pty, merged over
  the three platforms (`scripts/merge-coverage.py`).

Tests follow Arrange / Act / Assert; see [Tests](bench/README.md#tests).

## Commit messages

Commits use the Angular format that semantic-release reads: `type(scope): subject`, for example
`fix(pty): keep the exit code of a killed shell` or `docs: explain the tmux shim on Windows`. `feat`, `fix` and
`perf` make a release; `docs`, `test`, `refactor`, `build`, `ci` and `chore` don't. A `!` after the type, or a
`BREAKING CHANGE:` footer, marks a breaking change. The pull request title becomes the commit message on main.
[Commit messages and releases](docs/development/README.md#commit-messages-and-releases) has the types, the scopes
and how a release is made.

Please sign your commits (SSH or GPG, see
[GitHub's guide](https://docs.github.com/en/authentication/managing-commit-signature-verification/signing-commits)).

## Pull requests

Keep a pull request to one change, and update the docs (README, `docs/`, `erikterm.example.toml`) when behavior or
options change. The template lists the rest.
