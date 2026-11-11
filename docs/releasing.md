# Releasing

Releases are made by [semantic-release](https://github.com/semantic-release/semantic-release) from the commit messages
on `main`: nobody picks a version, edits the changelog or pushes a tag by hand. How to write commits and what each type
releases: [Commit messages and releases](development/README.md#commit-messages-and-releases).

| File | What |
| --- | --- |
| [`release.config.mjs`](../release.config.mjs) | Release branch `main`, tag format `v<version>`, plugins, release notes. |
| [`.github/workflows/release.yml`](../.github/workflows/release.yml) | The release: next version, build, semantic-release, attestations, packages. |
| [`.github/workflows/build-binaries.yml`](../.github/workflows/build-binaries.yml) | The four downloads and their SBOMs; used by CI (development build) and by releases (with the version). |
| [`.github/workflows/packages.yml`](../.github/workflows/packages.yml) | Publishes a release to [eriklieben/packages](https://github.com/eriklieben/packages) (pacman, Scoop). |
| [`.github/workflows/commit-messages.yml`](../.github/workflows/commit-messages.yml) | Checks PR titles (and, as a warning, PR commits) with [`commitlint.config.mjs`](../commitlint.config.mjs). |
| [`eng/release-gate.mjs`](../eng/release-gate.mjs) | Whether a green push to `main` releases: the `cut-release` label. |
| [`eng/first-release.mjs`](../eng/first-release.mjs) | The Release workflow's *First release* option: releases 1.0.0 while there is no release tag yet. |
| [`eng/release-commit.mjs`](../eng/release-commit.mjs) | The `CHANGELOG.md` commit through the GitHub API (signed, *Verified*). |
| [`eng/release-assets.mjs`](../eng/release-assets.mjs) | Hands the version to the build; checks the assets; writes `SHA256SUMS`. |
| [`eng/sbom.mjs`](../eng/sbom.mjs) | The SPDX SBOM of a download. |
| [`eng/packages.mjs`](../eng/packages.mjs) | The PKGBUILD, Scoop manifest and README edits, and the commit, in eriklieben/packages. |

The scripts in `eng/` have no dependencies and are tested against mocks: `node --test eng/` (CI's *release scripts*
job). The Node tooling in `package.json` (semantic-release, commitlint) is for the repository only; nothing of it ends
up in erikterm.

## How a release works

1. A push to `main` runs CI (`build.yml`). When every job passed (tests on the three systems, code quality, release
   scripts, coverage, build), its last job, `release`, checks for a release label: a pull request merged since the last
   tag must have **`cut-release`** (or `security`). Without one, nothing happens and the commits wait. With one, it
   starts the **Release** workflow for that commit. If `main` has moved on by then, that run releases nothing and the
   newer commit's CI run takes over.
2. **version:** `semantic-release --dry-run` reads the commits since the last tag `v*`. No `feat`, `fix`, `perf`,
   `revert` or breaking change among them: the run stops here. Otherwise it has the next version, e.g. `1.2.1`.
3. **build:** `build-binaries.yml` builds the downloads with that version on Linux, Windows and macOS: `erikterm
   --version` and About show it, so does the installer, and each download gets its SBOM.
4. **semantic-release:** the real run. It first checks that the next version is still the one just built and that
   every asset, its SBOM and `SHA256SUMS` are there (`eng/release-assets.mjs`); anything off stops the release before
   a tag exists. Then:
   - with the release GitHub App (variable `RELEASE_APP_ID`), it writes the notes into `CHANGELOG.md` and commits it as
     `chore(release): <version> [skip ci]` through the GitHub API, so GitHub signs it (*Verified*); `[skip ci]` keeps it
     from starting CI and another release. **Without the App there is no release commit at all** (never an unverified
     push to `main`): `CHANGELOG.md` stays as it is and the notes are only on the GitHub release;
   - it tags `v<version>` (on the release commit, or without the App on the released commit) and creates the GitHub
     release with the downloads, their SBOMs and `SHA256SUMS`; released pull requests and issues get a comment;
   - once the repository is public, the workflow attests the build provenance of every asset and each download's SBOM
     ([Verifying a download](#verifying-a-download)).
5. **packages:** `packages.yml` puts the release into eriklieben/packages ([Packages](#packages)).

The binaries carry the commit they were built from (`erikterm --version`); with the App that's the parent of the
tagged release commit, which only adds `CHANGELOG.md`.

The release commit lands on `main` on GitHub: pull before your next push (`git pull --rebase`), as after any other
commit made there.

## Cutting a release

Pick whichever fits:

- **Commits pushed straight to `main`** (the usual case): **Actions → Release → Run workflow**, branch `main`, untick
  *Dry run*. It releases the head of `main`, which needs a green CI run first. Leave *Dry run* ticked to see the next
  version and its notes without releasing.
- **A pull request:** add the label `cut-release` before merging it. CI on `main` then releases by itself. Forgot the
  label? Add it to the merged pull request and re-run the `release` job of that CI run, or run the workflow by hand.
- **A vulnerability fix:** a pull request with the label `security` releases without `cut-release`.

Only `feat`, `fix`, `perf`, `revert` and breaking changes release. Commits not in this format never
release anything and don't appear in the notes, and neither do `docs`, `chore`, `refactor`, `test`, `ci`, `build` and
`style` commits: a release that has one of the others lists only those.

### The first release

While the repository has no release tag `v*` yet, a green push to `main` releases nothing (there is no tag to count
from). The first release is made by hand: **Actions → Release → Run workflow**, branch `main`, tick *First release*
(and untick *Dry run*, or leave it ticked to see it first). It releases **1.0.0** whatever the commit types are
([`eng/first-release.mjs`](../eng/first-release.mjs), set in both the dry run and the real run), with everything a
normal release has. The option is refused once any `v*` tag exists; after that, leave it unticked.

## Versions

`main` is the release branch, and versions follow [Semantic Versioning](https://semver.org): from the commits since
the last tag, a breaking change releases the next major version (`1.2.1` → `2.0.0`), a `feat` the next minor one
(`1.2.1` → `1.3.0`), a `fix`, `perf` or `revert` the next patch (`1.2.0` → `1.2.1`). The highest one wins: a `feat`
and a `fix` together make a minor release.

There is no version in the source to bump: [`build/BuildInfo.targets`](../build/BuildInfo.targets) has `0.1.0` as the
version of development builds, and releases pass theirs in (`VersionPrefix` and, for a version with a `-` suffix,
`VersionSuffix`, from `build-binaries.yml`). It stays as it is; a release never changes it.

## What a release contains

| Asset | What |
| --- | --- |
| `erikterm-linux-x64.tar.gz` | The binary, docs, desktop entry, icons, shell and editor integration, terminfo. SDL3, FreeType and HarfBuzz come from the system. |
| `erikterm-osx-arm64.tar.gz` | The binary and docs. SDL3, FreeType and HarfBuzz come from Homebrew. |
| `erikterm-win-x64.zip` | The binary with the SDL3, FreeType, HarfBuzz DLLs (and the ones they need: libpng, zlib, bzip2, brotli), Microsoft's console host, and every DLL's license in `licenses/`. |
| `erikterm-win-x64-setup.exe` | A per-user installer of the same files (Inno Setup); needs no administrator rights. |
| `<asset>.spdx.json` | The SBOM of each of the four. |
| `SHA256SUMS` | The SHA-256 of all of the above, for `sha256sum -c`. |

### SBOM

Each download's SBOM is an SPDX 2.3 JSON document written by [`eng/sbom.mjs`](../eng/sbom.mjs) on the runner that
built it. It lists:

- the download itself with its SHA-1 and SHA-256, and every file in it with theirs;
- erikterm, and what Native AOT compiled into the binary (`STATIC_LINK`): the .NET runtime as the NuGet packs restore
  resolved for that runtime (`Microsoft.NETCore.App.Runtime.NativeAOT.<rid>`, `runtime.<rid>.Microsoft.DotNet.ILCompiler`,
  with versions, from `obj/project.assets.json`), plus any other NuGet package the app would reference (none: shipped
  code uses no packages). The compiler packages and the .NET SDK are listed as build tools;
- Windows: every bundled DLL with the vcpkg port that installed it, the port's version and license (from vcpkg's own
  file lists and `vcpkg.spdx.json`), and the console host (`conpty.dll`, `OpenConsole.exe`) with the package version
  `scripts/publish-windows.ps1` pins. A DLL or `.exe` that nothing accounts for fails the build, so the SBOM can't
  silently miss one;
- Linux and macOS: SDL3, FreeType and glibc (Linux) as dependencies and HarfBuzz and fontconfig as optional ones, all
  *not* in the download (no version: whatever the system has);
- the installer: Inno Setup, whose setup and uninstall program are part of the `.exe`.

It's generated by a script rather than by an SBOM tool on purpose: the tools read the NuGet restore graph and the
files on disk, which leaves out the Native AOT runtime packs (they are downloads, not package references) and the
vcpkg DLLs, and lists build-only packages as contents. [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md) has
the licenses.

```bash
jq -r '.packages[] | "\(.name) \(.versionInfo // "-") \(.licenseDeclared)"' erikterm-win-x64.zip.spdx.json
jq -r '.relationships[] | select(.relationshipType == "DEPENDS_ON" or .relationshipType == "OPTIONAL_DEPENDENCY_OF") | .relatedSpdxElement + " " + .relationshipType + " " + .spdxElementId' erikterm-linux-x64.tar.gz.spdx.json
```

## Verifying a download

```bash
tag=v1.2.1
gh release download "$tag" --repo eriklieben/erikterm --dir release
cd release && sha256sum -c SHA256SUMS
```

Every release asset also has a signed SLSA build provenance attestation, and each download an SBOM attestation,
made by the Release workflow ([`actions/attest-build-provenance`](https://github.com/actions/attest-build-provenance),
[`actions/attest`](https://github.com/actions/attest)). They tie each file's SHA-256 to this repository, the
workflow and the commit:

```bash
gh attestation verify erikterm-linux-x64.tar.gz --repo eriklieben/erikterm
gh attestation verify erikterm-linux-x64.tar.gz --repo eriklieben/erikterm --predicate-type https://spdx.dev/Document/v2.3
# the attested SBOM itself, e.g. its package names:
gh attestation verify erikterm-win-x64.zip --repo eriklieben/erikterm --predicate-type https://spdx.dev/Document/v2.3 \
  --format json --jq '.[0].verificationResult.statement.predicate.packages[].name'
```

The attestations are listed under **Actions → Attestations**. While the repository is private these steps are
skipped (artifact attestations need a public repository); `SHA256SUMS` and the SBOMs are there either way.

The provenance is also attached to the release as `erikterm-<tag>.intoto.jsonl` (the Sigstore bundle), so it can be
checked without asking GitHub for it:

```bash
gh attestation verify erikterm-linux-x64.tar.gz --repo eriklieben/erikterm --bundle "erikterm-$tag.intoto.jsonl"
```

## Packages

After the GitHub release, `packages.yml` publishes the same files, downloaded from that release and checked against
its `SHA256SUMS` (and, once public, its attestations), to [eriklieben/packages](https://github.com/eriklieben/packages):

1. the release `erikterm-v<version>` there, with the Linux tar.gz, the Windows zip and the installer (marked as a
   prerelease only for a version with a `-` suffix; the notes are the release's, without links into this repository);
2. `arch/erikterm/PKGBUILD` (`_tag`, `pkgver`, `pkgrel=1`, `sha256sums`), `bucket/erikterm.json` (version, url, hash)
   and the installer link in its README. A `-` suffix, which pacman versions can't hold, is dropped from `pkgver`
   (`1.3.0-rc.1` → `1.3.0rc.1`). For a normal version a beta marker is removed too: "(beta)" from the PKGBUILD's
   `pkgdesc` and the manifest's description, "**Beta**" from erikterm's row in the README's package table;
3. `makepkg` (in an Arch Linux container, as a normal user) and that repository's `scripts/publish-arch.sh`, which
   adds the package to the pacman database on its `arch` release;
4. the commit `Update erikterm to <version>` on its `main`, through the GitHub API with the release App's token, so it
   is *Verified* like the hand-made ones.

It needs the release GitHub App installed on eriklieben/packages too (Contents: read and write); its token there is
limited to that repository. Without the App the job is skipped and the packages are updated by hand as before.

## Dry run

- **GitHub:** Actions → **Release** → *Run workflow* → branch `main`, *Dry run* ticked (the default). The log of
  *Next version* shows the commits it analyzed, the next version and its notes; nothing is built or published.
- **Local**, from a checkout of `main` (not a worktree on another branch: semantic-release force-fetches the
  configured branches it isn't on into your local branches of the same name, and would reset a local `main` that is
  ahead of GitHub):

  ```bash
  pnpm install
  GITHUB_TOKEN=$(gh auth token) pnpm exec semantic-release --dry-run --no-ci
  ```

  It needs push access (it checks with `git push --dry-run`; nothing is pushed). It stops before building anything: a
  real release only runs in the Release workflow. `ERIKTERM_FIRST_RELEASE=true` in front shows what *First release*
  would do.

## When something fails

Look at which job of the Release run failed:

| Job | What to do |
| --- | --- |
| *Check the commit* | No green CI run for the head of `main`: wait for CI, or fix it, then run the workflow again. *First release* ticked while a `v*` tag exists: run it without. |
| *Next version*, *build* | Nothing was released. **Re-run failed jobs**, or run the workflow again (dry run off). |
| *semantic-release*, before the tag | Nothing was released (the log says so). Re-run, or run the workflow again. "The binaries were built as X, but the next version is now Y" means another release happened in between: run the workflow again. |
| *semantic-release*, after the tag | Rare (a GitHub outage while uploading). semantic-release now counts the tag as released, so a re-run finds nothing to do: finish the GitHub release by hand with the run's artifacts (`gh run download <run id> --pattern 'erikterm-*' --dir dist`, then `gh release create` or `gh release upload` with `dist/*` and a `SHA256SUMS` from `node eng/release-assets.mjs checksums dist`), then publish the packages. A failed release also opens an issue *The automated release is failing*, closed by the next successful one. |
| *packages* | The erikterm release is done. **Actions → Publish packages → Run workflow** (branch `main`) with the version, e.g. `1.2.1`. Every step can run twice. |

Don't edit `CHANGELOG.md` or create `v*` tags by hand.
