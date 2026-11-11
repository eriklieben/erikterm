// Tests of eng/release-assets.mjs on a made-up dist/ folder:
//   node --test eng/release-assets.test.mjs
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, beforeEach, describe, it } from "node:test";
import { fileURLToPath } from "node:url";
import { ASSETS, SUMS, sha256, verifyAssets, writeChecksums } from "./release-assets.mjs";

const script = path.join(path.dirname(fileURLToPath(import.meta.url)), "release-assets.mjs");
const VERSION = "1.2.1";
let dir;
const roots = [];
after(() => roots.forEach((d) => rmSync(d, { recursive: true, force: true })));

function dist(version = VERSION) {
  dir = mkdtempSync(path.join(tmpdir(), "release-assets-"));
  roots.push(dir);
  for (const asset of ASSETS) {
    writeFileSync(path.join(dir, asset), `bytes of ${asset}`);
    const sbom = {
      documentDescribes: ["SPDXRef-Package-x"],
      packages: [{ SPDXID: "SPDXRef-Package-x", name: asset, versionInfo: version, checksums: [{ algorithm: "SHA256", checksumValue: sha256(path.join(dir, asset)) }] }],
    };
    writeFileSync(path.join(dir, `${asset}.spdx.json`), JSON.stringify(sbom));
  }
  writeChecksums(dir);
}

const run = (args, env = {}) => spawnSync(process.execPath, [script, ...args], { encoding: "utf8", env: { PATH: process.env.PATH, ...env } });

describe("verifyAssets", () => {
  beforeEach(() => dist());

  it("accepts a complete set", () => {
    verifyAssets(dir, VERSION);
    const sums = readFileSync(path.join(dir, SUMS), "utf8");
    assert.equal(sums.trim().split("\n").length, ASSETS.length * 2);
    // sha256sum -c understands it (where the tool exists)
    const check = spawnSync("sha256sum", ["-c", SUMS], { cwd: dir, encoding: "utf8" });
    if (!check.error) assert.equal(check.status, 0, check.stdout + check.stderr);
  });

  it("fails on a missing asset", () => {
    rmSync(path.join(dir, "erikterm-win-x64-setup.exe"));
    assert.throws(() => verifyAssets(dir, VERSION), /missing in .*erikterm-win-x64-setup\.exe/);
  });

  it("fails on an SBOM of another version", () => {
    dist("1.2.0");
    assert.throws(() => verifyAssets(dir, VERSION), /describes erikterm-linux-x64\.tar\.gz 1\.2\.0/);
  });

  it("fails when a file changed after SHA256SUMS was written", () => {
    writeFileSync(path.join(dir, "erikterm-win-x64.zip.spdx.json"), readFileSync(path.join(dir, "erikterm-win-x64.zip.spdx.json"), "utf8") + " ");
    assert.throws(() => verifyAssets(dir, VERSION), /SHA256SUMS doesn't match erikterm-win-x64\.zip\.spdx\.json/);
  });

  it("fails when an SBOM has another hash than its asset", () => {
    writeFileSync(path.join(dir, "erikterm-osx-arm64.tar.gz"), "rebuilt");
    assert.throws(() => verifyAssets(dir, VERSION), /erikterm-osx-arm64\.tar\.gz\.spdx\.json has another SHA-256/);
  });
});

describe("verify (the semantic-release step)", () => {
  beforeEach(() => dist());

  it("hands the version to GITHUB_OUTPUT in a dry run", () => {
    const output = path.join(dir, "out.txt");
    const result = run(["verify", VERSION, "--dry-run=true"], { GITHUB_OUTPUT: output });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(readFileSync(output, "utf8"), `version=${VERSION}\n`);
  });

  it("refuses a real release outside the Release workflow", () => {
    const result = run(["verify", VERSION, "--dry-run=false"]);
    assert.equal(result.status, 1);
    assert.match(result.stderr, /ERIKTERM_RELEASE_VERSION isn't set/);
  });

  it("refuses a version other than the one the binaries were built with", () => {
    const result = run(["verify", "1.2.2", "--dry-run=false"], { ERIKTERM_RELEASE_VERSION: VERSION, ERIKTERM_RELEASE_DIR: dir });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /built as 1\.2\.1, but the next version is now 1\.2\.2/);
  });

  it("passes with the built version and complete assets", () => {
    const result = run(["verify", VERSION, "--dry-run=false"], { ERIKTERM_RELEASE_VERSION: VERSION, ERIKTERM_RELEASE_DIR: dir });
    assert.equal(result.status, 0, result.stderr);
  });

  it("writes SHA256SUMS from the command line", () => {
    rmSync(path.join(dir, SUMS));
    execFileSync(process.execPath, [script, "checksums", dir]);
    verifyAssets(dir, VERSION);
  });
});
