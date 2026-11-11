// The release assets: what a GitHub release of erikterm carries, and the checks around them. No dependencies.
//
//   node eng/release-assets.mjs verify <version> --dry-run=<true|false>
//       semantic-release's verifyRelease step (@semantic-release/exec, release.config.mjs), once the next version is known.
//       Dry run (the Release workflow's `version` job, local dry runs): writes version=<version> to GITHUB_OUTPUT, so the
//       build jobs build exactly that version. Real run (the `release` job): the version must be ERIKTERM_RELEASE_VERSION,
//       the one the binaries in dist/ were built with (another release in between would change it), and dist/ must hold
//       every asset, its SBOM (for that version) and a SHA256SUMS that matches them. Anything else stops the release
//       before a tag or a GitHub release exists.
//   node eng/release-assets.mjs checksums [dir]
//       Writes <dir>/SHA256SUMS (default dir: dist) for every other file in it, in `sha256sum -c` format.
//
// Environment: ERIKTERM_RELEASE_VERSION, ERIKTERM_RELEASE_DIR (default dist), GITHUB_OUTPUT, GITHUB_STEP_SUMMARY.
// Tests: node --test eng/release-assets.test.mjs
import { createHash } from "node:crypto";
import { appendFileSync, existsSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/** The downloads of a release, one per platform (plus the Windows installer). Each has <asset>.spdx.json next to it. */
export const ASSETS = [
  "erikterm-linux-x64.tar.gz",
  "erikterm-osx-arm64.tar.gz",
  "erikterm-win-x64.zip",
  "erikterm-win-x64-setup.exe",
];
export const SUMS = "SHA256SUMS";
const SEMVER = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

export class AssetError extends Error {}

export const sha256 = (file) => createHash("sha256").update(readFileSync(file)).digest("hex");

/** Orders file names by UTF-16 code units, as sort() does without a compare function: the same on every locale. */
const byCodeUnits = (a, b) => {
  if (a === b) return 0;
  return a < b ? -1 : 1;
};

/** The text of SHA256SUMS for `files` (names in `dir`), sorted by name. */
export function checksums(dir, files) {
  return [...files].sort(byCodeUnits).map((name) => `${sha256(path.join(dir, name))}  ${name}\n`).join("");
}

/** Writes dir/SHA256SUMS for every regular file in `dir` except SHA256SUMS itself. Returns the file names. */
export function writeChecksums(dir) {
  const files = readdirSync(dir).filter((name) => name !== SUMS && statSync(path.join(dir, name)).isFile());
  writeFileSync(path.join(dir, SUMS), checksums(dir, files));
  return files;
}

/** Throws AssetError unless `dir` holds a complete, consistent set of release assets for `version`. */
export function verifyAssets(dir, version) {
  const expected = ASSETS.flatMap((asset) => [asset, `${asset}.spdx.json`]);
  const missing = [...expected, SUMS].filter((name) => !existsSync(path.join(dir, name)));
  if (missing.length) throw new AssetError(`missing in ${dir}/: ${missing.join(", ")}`);

  for (const asset of ASSETS) {
    const sbom = JSON.parse(readFileSync(path.join(dir, `${asset}.spdx.json`), "utf8"));
    const described = sbom.packages?.find((p) => sbom.documentDescribes?.includes(p.SPDXID));
    if (described?.name !== asset || described?.versionInfo !== version) {
      throw new AssetError(`${asset}.spdx.json describes ${described?.name} ${described?.versionInfo}, not ${asset} ${version}`);
    }
    const hash = described.checksums?.find((c) => c.algorithm === "SHA256")?.checksumValue;
    if (hash !== sha256(path.join(dir, asset))) throw new AssetError(`${asset}.spdx.json has another SHA-256 than ${asset}`);
  }

  const listed = new Map(
    readFileSync(path.join(dir, SUMS), "utf8")
      .split("\n")
      .filter(Boolean)
      .map((line) => {
        const m = /^([0-9a-f]{64}) [ *](.+)$/.exec(line);
        if (!m) throw new AssetError(`${SUMS}: bad line "${line}"`);
        return [m[2], m[1]];
      }),
  );
  for (const name of expected) {
    if (listed.get(name) !== sha256(path.join(dir, name))) throw new AssetError(`${SUMS} doesn't match ${name}`);
  }
}

function verify(version, dryRun, env) {
  if (!SEMVER.test(version ?? "")) throw new AssetError(`not a version: ${version}`);
  if (dryRun) {
    console.log(`[release-assets] next version: ${version}`);
    if (env.GITHUB_OUTPUT) appendFileSync(env.GITHUB_OUTPUT, `version=${version}\n`);
    return;
  }
  const built = env.ERIKTERM_RELEASE_VERSION;
  if (!built) {
    throw new AssetError("ERIKTERM_RELEASE_VERSION isn't set: a real release only runs in the Release workflow, which builds the binaries first (docs/releasing.md).");
  }
  if (built !== version) {
    throw new AssetError(`the binaries were built as ${built}, but the next version is now ${version} (another release happened in between?): start the Release workflow again.`);
  }
  const dir = env.ERIKTERM_RELEASE_DIR || "dist";
  verifyAssets(dir, version);
  console.log(`[release-assets] ${dir}/ has every asset of ${version}, with SBOMs and ${SUMS}`);
}

function main(argv, env) {
  const [command, ...rest] = argv;
  if (command === "verify") {
    const dryRun = rest.includes("--dry-run") || rest.includes("--dry-run=true");
    verify(rest.find((a) => !a.startsWith("--")), dryRun, env);
  } else if (command === "checksums") {
    const dir = rest[0] ?? "dist";
    const files = writeChecksums(dir);
    console.log(`[release-assets] ${dir}/${SUMS}: ${files.length} file(s)`);
  } else {
    console.error("usage: node eng/release-assets.mjs verify <version> --dry-run=<true|false> | checksums [dir]");
    process.exit(2);
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    main(process.argv.slice(2), process.env);
  } catch (error) {
    if (!(error instanceof AssetError)) throw error;
    console.error(`[release-assets] error: ${error.message}`);
    process.exit(1);
  }
}
