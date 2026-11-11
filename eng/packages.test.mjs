// Tests of eng/packages.mjs on a made-up eriklieben/packages checkout (no network, no git repository changes):
//   node --test eng/packages.test.mjs
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { after, describe, it } from "node:test";
import { commit, pkgver, plainNotes, update, updatePkgbuild, updateReadme, updateScoop } from "./packages.mjs";

const OLD = "1.2.0";
const NEW = "1.2.1";
const PRE = "1.3.0-rc.1";
const PKGBUILD = `pkgname=erikterm
_tag=${OLD}
pkgver=1.2.0
pkgrel=2
pkgdesc="GPU terminal (beta)"
source=("erikterm-linux-x64-$_tag.tar.gz::https://github.com/eriklieben/packages/releases/download/erikterm-v$_tag/erikterm-linux-x64.tar.gz")
sha256sums=('20c6e3f3e367d515bac2b4f257db8b5320f8ec220031392775ef95dd8605bef2')

package() {
    cd erikterm
}
`;
const SCOOP = `${JSON.stringify({
  version: OLD,
  description: "GPU terminal (beta)",
  architecture: { "64bit": { url: `https://github.com/eriklieben/packages/releases/download/erikterm-v${OLD}/erikterm-win-x64.zip`, hash: "d7ff" } },
  extract_dir: "erikterm",
  bin: "erikterm.exe",
  shortcuts: [["erikterm.exe", "Erik's Terminal"]],
}, null, 4)}\n`;
const README = `| Package | What | pacman | Scoop |
| --- | --- | --- | --- |
| \`erikterm\` | GPU terminal with tabs. **Beta** | \`sudo pacman -S erikterm\` | \`scoop install eriklieben/erikterm\` |
| \`other\` | Something else. **Beta** | \`sudo pacman -S other\` | |

Corporate laptop? The [installer](https://github.com/eriklieben/packages/releases/download/erikterm-v${OLD}/erikterm-win-x64-setup.exe) installs erikterm.
`;

const roots = [];
after(() => roots.forEach((d) => rmSync(d, { recursive: true, force: true })));
function checkout() {
  const dir = mkdtempSync(path.join(tmpdir(), "packages-"));
  roots.push(dir);
  mkdirSync(path.join(dir, "arch", "erikterm"), { recursive: true });
  mkdirSync(path.join(dir, "bucket"));
  mkdirSync(path.join(dir, "dist"));
  writeFileSync(path.join(dir, "arch/erikterm/PKGBUILD"), PKGBUILD);
  writeFileSync(path.join(dir, "bucket/erikterm.json"), SCOOP);
  writeFileSync(path.join(dir, "README.md"), README);
  writeFileSync(path.join(dir, "dist/erikterm-linux-x64.tar.gz"), "linux");
  writeFileSync(path.join(dir, "dist/erikterm-win-x64.zip"), "zip");
  return dir;
}
const sha = (text) => createHash("sha256").update(text).digest("hex");

describe("file updates", () => {
  it("turns the version into a pacman pkgver", () => {
    assert.equal(pkgver("1.3.0-rc.1"), "1.3.0rc.1");
    assert.equal(pkgver("1.3.0-rc.10"), "1.3.0rc.10");
    assert.equal(pkgver("1.2.0"), "1.2.0");
  });

  it("bumps the PKGBUILD and resets pkgrel", () => {
    const out = updatePkgbuild(PKGBUILD, PRE, "a".repeat(64));
    assert.match(out, /^_tag=1\.3\.0-rc\.1$/m);
    assert.match(out, /^pkgver=1\.3\.0rc\.1$/m);
    assert.match(out, /^pkgrel=1$/m);
    assert.match(out, new RegExp(`^sha256sums=\\('${"a".repeat(64)}'\\)$`, "m"));
    // the rest is left alone, the beta marker of a prerelease included
    assert.match(out, /^pkgdesc="GPU terminal \(beta\)"$/m);
    assert.match(out, /erikterm-v\$_tag\/erikterm-linux-x64\.tar\.gz/);
    assert.match(out, /package\(\) \{\n    cd erikterm\n\}/);
  });

  it("drops the PKGBUILD's beta marker for a normal version", () => {
    const out = updatePkgbuild(PKGBUILD, NEW, "a".repeat(64));
    assert.match(out, /^pkgver=1\.2\.1$/m);
    assert.match(out, /^pkgdesc="GPU terminal"$/m);
  });

  it("bumps the Scoop manifest, keeping its layout", () => {
    const out = updateScoop(SCOOP, PRE, "b".repeat(64));
    const manifest = JSON.parse(out);
    assert.equal(manifest.version, PRE);
    assert.equal(manifest.architecture["64bit"].url, "https://github.com/eriklieben/packages/releases/download/erikterm-v1.3.0-rc.1/erikterm-win-x64.zip");
    assert.equal(manifest.architecture["64bit"].hash, "b".repeat(64));
    assert.equal(out, SCOOP.replaceAll(OLD, PRE).replace('"d7ff"', `"${"b".repeat(64)}"`));
  });

  it("drops the Scoop manifest's beta marker for a normal version", () => {
    const out = updateScoop(SCOOP, NEW, "b".repeat(64));
    assert.equal(JSON.parse(out).description, "GPU terminal");
    assert.equal(out, SCOOP.replaceAll(OLD, NEW).replace('"d7ff"', `"${"b".repeat(64)}"`).replace(" (beta)", ""));
  });

  it("points the README at the new installer", () => {
    assert.equal(updateReadme(README, PRE), README.replace(OLD, PRE));
  });

  it("drops the beta marker of erikterm's README row for a normal version, and only that one", () => {
    const out = updateReadme(README, NEW);
    assert.equal(out, README.replace(OLD, NEW).replace("GPU terminal with tabs. **Beta**", "GPU terminal with tabs."));
    assert.match(out, /Something else\. \*\*Beta\*\*/);
  });

  it("fails on a file it doesn't recognise instead of guessing", () => {
    assert.throws(() => updatePkgbuild("pkgname=x\n", "1.2.0", "c"), /no line matching/);
    assert.throws(() => updateReadme("no link", "1.2.0"), /README\.md: no line matching/);
  });

  it("updates a checkout from the release assets, and is a no-op the second time", () => {
    const dir = checkout();
    const changed = update(dir, NEW, path.join(dir, "dist"));
    assert.deepEqual(changed, ["arch/erikterm/PKGBUILD", "bucket/erikterm.json", "README.md"]);
    assert.match(readFileSync(path.join(dir, "arch/erikterm/PKGBUILD"), "utf8"), new RegExp(sha("linux")));
    assert.equal(JSON.parse(readFileSync(path.join(dir, "bucket/erikterm.json"), "utf8")).architecture["64bit"].hash, sha("zip"));
    assert.deepEqual(update(dir, NEW, path.join(dir, "dist")), []);
  });
});

describe("commit", () => {
  const TOKEN = "not-a-real-token-for-the-tests";
  const HEAD = "1".repeat(40);

  it("commits the changed files through the API on main of eriklieben/packages", async () => {
    const dir = checkout();
    update(dir, NEW, path.join(dir, "dist"));
    const calls = [];
    const fetch = async (url, init) => {
      calls.push(JSON.parse(init.body));
      return { ok: true, status: 200, text: async () => JSON.stringify({ data: { createCommitOnBranch: { commit: { oid: "2".repeat(40), url: "u" } } } }) };
    };
    const run = (_dir, ...args) => (args[0] === "diff" ? "arch/erikterm/PKGBUILD\nbucket/erikterm.json\nREADME.md\n" : HEAD);
    const result = await commit(dir, NEW, { PACKAGES_TOKEN: TOKEN }, { fetch, run });
    assert.equal(result.committed, true);
    const { input } = calls[0].variables;
    assert.deepEqual(input.branch, { repositoryNameWithOwner: "eriklieben/packages", branchName: "main" });
    assert.equal(input.expectedHeadOid, HEAD);
    assert.deepEqual(input.message, { headline: "Update erikterm to 1.2.1" });
    assert.deepEqual(input.fileChanges.additions.map((a) => a.path), ["arch/erikterm/PKGBUILD", "bucket/erikterm.json", "README.md"]);
  });

  it("does nothing when nothing changed", async () => {
    const result = await commit(checkout(), NEW, { PACKAGES_TOKEN: TOKEN }, { fetch: () => assert.fail("no API call"), run: () => "" });
    assert.equal(result.committed, false);
  });
});

describe("plainNotes", () => {
  it("drops the links into the erikterm repository", () => {
    const notes = `## [1.2.1](https://github.com/eriklieben/erikterm/compare/v1.2.0...v1.2.1) (2026-11-11)

### Features

* **settings:** add a settings page ([#12](https://github.com/eriklieben/erikterm/issues/12)) ([2c74f0e](https://github.com/eriklieben/erikterm/commit/2c74f0ecde1c5e08aa54c49891a7ffa5bdb9808e)), closes [#11](https://github.com/eriklieben/erikterm/issues/11)
* see [the guide](https://github.com/eriklieben/erikterm/blob/main/docs/README.md)`;
    assert.equal(plainNotes(notes), `## 1.2.1 (2026-11-11)

### Features

* **settings:** add a settings page
* see the guide`);
  });

  it("keeps the heading of a first release, which has no compare link", () => {
    assert.equal(plainNotes("## 1.0.0 (2026-11-11)"), "## 1.0.0 (2026-11-11)");
  });
});
