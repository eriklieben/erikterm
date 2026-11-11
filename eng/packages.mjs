// Publishing a release to eriklieben/packages (the pacman repository and the Scoop bucket): the file edits and the
// commit that used to be made by hand. Run by .github/workflows/packages.yml in an Arch Linux container; the GitHub
// release erikterm-v<version> there, makepkg and scripts/publish-arch.sh (of that repository) are workflow steps.
//
//   node eng/packages.mjs update <packages checkout> <version> <dist>
//       arch/erikterm/PKGBUILD: _tag, pkgver (1.3.0-rc.1 -> 1.3.0rc.1: pacman versions can't hold "-"), pkgrel=1,
//       sha256sums of dist/erikterm-linux-x64.tar.gz; bucket/erikterm.json: version, url and hash of
//       dist/erikterm-win-x64.zip; README.md: the link to the installer of this version. For a normal version (no
//       "-") also drops a beta marker: "(beta)" from the PKGBUILD's pkgdesc and the manifest's description, "**Beta**"
//       from the erikterm row of the README's package table. Prints what changed.
//   node eng/packages.mjs commit <packages checkout> <version>
//       Commits the changed files as "Update erikterm to <version>" on main of eriklieben/packages through the GitHub
//       API (createCommitOnBranch, eng/release-commit.mjs): made with the release App's token, GitHub signs it
//       ("Verified"). Nothing to commit (a rerun after a commit went through): says so and succeeds. When main of
//       eriklieben/packages moved on since the checkout, GitHub refuses it and nothing is committed: run it again.
//   node eng/packages.mjs notes <version> < release-notes.md
//       The release notes for erikterm-v<version> on eriklieben/packages: the generated notes without links (they
//       point into the erikterm repository, which may be private) and commit hashes.
//
// Environment (commit): PACKAGES_TOKEN (the App token for eriklieben/packages; never logged), PACKAGES_REPOSITORY
// (default eriklieben/packages), GITHUB_GRAPHQL_URL (optional).
// Tests: node --test eng/packages.test.mjs
import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { sha256 } from "./release-assets.mjs";
import { createReleaseCommit } from "./release-commit.mjs";

export const PACKAGES_REPOSITORY = "eriklieben/packages";
const SEMVER = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;
const FILES = { pkgbuild: "arch/erikterm/PKGBUILD", scoop: "bucket/erikterm.json", readme: "README.md" };

export class PackagesError extends Error {}

/** The pacman pkgver of a version: no "-" allowed; 1.3.0-rc.1 -> 1.3.0rc.1 (sorts below 1.3.0, as pacman expects). */
export const pkgver = (version) => version.replace("-", "");
export const isPrerelease = (version) => version.includes("-");
// The beta markers of the descriptions ("... status (beta)") and of the erikterm row of the packages README
// ("... status. **Beta**"): dropped for a normal version, left alone for a prerelease. Plain string searches, not
// regular expressions: a "\s*" in front of the marker backtracks over every run of white space it meets.
const withoutBetaMarker = (text) =>
  text
    .split(/\(beta\)/i)
    .map((part, i, parts) => (i < parts.length - 1 ? part.trimEnd() : part))
    .join("");
const README_ROW = "| `erikterm` |";
const README_BETA = "**beta**";
function withoutReadmeRowBeta(text) {
  return text
    .split("\n")
    .map((line) => {
      const lower = line.toLowerCase();
      if (!lower.startsWith(README_ROW)) return line;
      const at = lower.indexOf(README_BETA, README_ROW.length);
      return at < 0 ? line : line.slice(0, at).trimEnd() + line.slice(at + README_BETA.length);
    })
    .join("\n");
}
export const releaseTag = (version) => `erikterm-v${version}`;
const downloadUrl = (version, file) => `https://github.com/${PACKAGES_REPOSITORY}/releases/download/${releaseTag(version)}/${file}`;

function replaceLine(text, pattern, line, file) {
  if (!pattern.test(text)) throw new PackagesError(`${file}: no line matching ${pattern}`);
  return text.replace(pattern, line);
}

export function updatePkgbuild(text, version, linuxSha256) {
  let out = replaceLine(text, /^_tag=.*$/m, `_tag=${version}`, FILES.pkgbuild);
  out = replaceLine(out, /^pkgver=.*$/m, `pkgver=${pkgver(version)}`, FILES.pkgbuild);
  out = replaceLine(out, /^pkgrel=.*$/m, "pkgrel=1", FILES.pkgbuild);
  if (!isPrerelease(version)) out = out.replace(/^pkgdesc=.*$/m, withoutBetaMarker);
  return replaceLine(out, /^sha256sums=\('[0-9a-f]*'\)$/m, `sha256sums=('${linuxSha256}')`, FILES.pkgbuild);
}

export function updateScoop(text, version, zipSha256) {
  const manifest = JSON.parse(text);
  const x64 = manifest.architecture?.["64bit"];
  if (!x64) throw new PackagesError(`${FILES.scoop}: no architecture.64bit`);
  manifest.version = version;
  if (!isPrerelease(version) && typeof manifest.description === "string") manifest.description = withoutBetaMarker(manifest.description);
  x64.url = downloadUrl(version, "erikterm-win-x64.zip");
  x64.hash = zipSha256;
  return `${JSON.stringify(manifest, null, 4)}\n`;
}

export function updateReadme(text, version) {
  const out = isPrerelease(version) ? text : withoutReadmeRowBeta(text);
  return replaceLine(
    out,
    /https:\/\/github\.com\/eriklieben\/packages\/releases\/download\/erikterm-v[^/\s)]+\/erikterm-win-x64-setup\.exe/g,
    downloadUrl(version, "erikterm-win-x64-setup.exe"),
    FILES.readme,
  );
}

/** Markdown release notes without links and commit hashes. */
export function plainNotes(markdown) {
  return markdown
    .replace(/^(#+) \[([^\]]+)\]\([^)]*\)/gm, "$1 $2") // "## [1.2.1](compare link) (date)"
    .replace(/ \(\[[0-9a-f]{7,40}\]\([^)]*\)\)/g, "") // " ([abc1234](commit link))"
    .replace(/ \(\[#\d+\]\([^)]*\)\)/g, "") // " ([#12](pull link))"
    .replace(/, closes \[[^\]]*\]\([^)]*\)(?:, \[[^\]]*\]\([^)]*\))*/g, "")
    .replace(/\[([^[\]]+)\]\([^)]*\)/g, "$1") // the text holds no "[": a run of "[" doesn't make each one scan the line
    .trim();
}

export function update(dir, version, dist) {
  if (!SEMVER.test(version)) throw new PackagesError(`not a version: ${version}`);
  const edits = {
    [FILES.pkgbuild]: (t) => updatePkgbuild(t, version, sha256(path.join(dist, "erikterm-linux-x64.tar.gz"))),
    [FILES.scoop]: (t) => updateScoop(t, version, sha256(path.join(dist, "erikterm-win-x64.zip"))),
    [FILES.readme]: (t) => updateReadme(t, version),
  };
  const changed = [];
  for (const [file, edit] of Object.entries(edits)) {
    const before = readFileSync(path.join(dir, file), "utf8");
    const after = edit(before);
    if (after !== before) {
      writeFileSync(path.join(dir, file), after);
      changed.push(file);
    }
  }
  return changed;
}

// The Arch Linux container's git, by its full path: nothing found through PATH runs with the packages token around.
const GIT = "/usr/bin/git";
const git = (dir, ...args) => execFileSync(GIT, ["-C", dir, ...args], { encoding: "utf8" }).trim();

export async function commit(dir, version, env, { fetch = globalThis.fetch, run = git } = {}) {
  const files = run(dir, "diff", "--name-only", "HEAD").split("\n").filter(Boolean);
  if (files.length === 0) return { committed: false };
  const contents = Object.fromEntries(files.map((f) => [f, readFileSync(path.join(dir, f))]));
  const result = await createReleaseCommit({
    token: env.PACKAGES_TOKEN,
    repository: env.PACKAGES_REPOSITORY || PACKAGES_REPOSITORY,
    branch: "main",
    expectedHeadOid: run(dir, "rev-parse", "HEAD"),
    message: `Update erikterm to ${version}`,
    files: contents,
    graphqlUrl: env.GITHUB_GRAPHQL_URL || undefined,
    fetch,
  });
  return { committed: true, files, ...result };
}

async function main([command, ...args]) {
  if (command === "update" && args.length === 3) {
    const changed = update(args[0], args[1], args[2]);
    console.log(changed.length ? `[packages] updated ${changed.join(", ")}` : "[packages] already up to date");
  } else if (command === "commit" && args.length === 2) {
    const result = await commit(args[0], args[1], process.env);
    console.log(result.committed ? `[packages] committed ${result.files.join(", ")}: ${result.url ?? result.oid}` : "[packages] nothing to commit");
  } else if (command === "notes" && args.length === 1) {
    const notes = plainNotes(readFileSync(0, "utf8"));
    const text = notes || `erikterm ${args[0]}.`;
    process.stdout.write(`${text}\n`);
  } else {
    console.error("usage: node eng/packages.mjs update <dir> <version> <dist> | commit <dir> <version> | notes <version> < notes.md");
    process.exit(2);
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    await main(process.argv.slice(2));
  } catch (error) {
    if (!(error instanceof PackagesError) && error?.name !== "ReleaseCommitError") throw error;
    const details = error.details ? "\n" + error.details : "";
    console.error(`[packages] error: ${error.message}${details}`);
    process.exit(1);
  }
}
