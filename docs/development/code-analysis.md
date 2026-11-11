# Code analysis and dependency updates

What checks the code besides the tests, and what keeps the dependencies current. The build itself (tests on three
platforms, the quality gate, coverage) is [build.yml](../../.github/workflows/build.yml); see
[CONTRIBUTING.md](../../CONTRIBUTING.md#build-and-test).

| Check | Workflow | When | Needs |
| --- | --- | --- | --- |
| CodeQL | [codeql.yml](../../.github/workflows/codeql.yml) | pushes to main, pull requests, Mondays | a public repository (or GitHub Advanced Security) |
| OpenSSF Scorecard | [scorecard.yml](../../.github/workflows/scorecard.yml) | pushes to main, branch protection changes, Mondays | a public repository |
| SonarQube Cloud | [sonar.yml](../../.github/workflows/sonar.yml) | after a green build of main | the secret `SONAR_TOKEN` |
| Dependabot | [dependabot.yml](../../.github/dependabot.yml) | Mondays | nothing |

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

[sonar.yml](../../.github/workflows/sonar.yml) runs after each build of main that passed (`workflow_run`). It
downloads that run's coverage artifacts (`coverage-linux`, `coverage-macos`, `coverage-windows`), points their file
paths at its own checkout ([scripts/sonar-coverage.py](../../scripts/sonar-coverage.py)), and wraps one Release
build of `src/ErikTerm.App` with SonarScanner for .NET. It runs no tests.

Why a separate workflow and not a job in build.yml or a test run of its own:

- **No second test run.** The suite runs on three platforms already, and a Linux-only run would lose the coverage of
  the Windows and macOS code. CI minutes are limited.
- **build.yml stays as it is.** The coverage it uploads is enough.
- **Secrets stay on main.** `workflow_run` gets the repository's secrets whatever triggered the build, so the job only
  follows pushes to main of this repository. Pull requests and forks are not analysed; the analysis of main after the
  merge picks the change up.

`workflow_run` matches the build workflow by its name, `build`, and the artifacts by their names. Renaming either
needs the same change in sonar.yml.

The settings are in [build/SonarQube.Analysis.xml](../../build/SonarQube.Analysis.xml): the benchmark, scripts,
packaging, `contrib/` and generated code are left out, compiler warnings aren't imported (the quality job gates the
SDK analyzers), and the coverage comes from the three Cobertura reports. The scanner is a CI tool in
[.config/dotnet-tools.json](../../.config/dotnet-tools.json); it isn't part of erikterm. Only `src/ErikTerm.App` is
built, which brings in the libraries; the test project compiles some app files a second time, and building it too
would report those files twice.

Without the secret `SONAR_TOKEN` the job stops after its first step with a notice, so the workflow can be merged
before the project exists. The organization and project key are `eriklieben` and `eriklieben_erikterm`; the
repository variables `SONAR_ORGANIZATION` and `SONAR_PROJECT_KEY` override them. Quality profile: *Sonar way*.

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
| license, platforms, .NET 10, semantic-release | always (static) |
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
4. Create a token (My Account → Security) and add it to the repository as the secret `SONAR_TOKEN`.
5. Push to main, or re-run the last *SonarQube Cloud* run under Actions.

On [OpenSSF Best Practices](https://www.bestpractices.dev): log in with GitHub, add the project, fill in the criteria
and add the badge with its id as above.
