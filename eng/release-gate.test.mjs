// Tests of eng/release-gate.mjs with a mocked fetch (no network):
//   node --test eng/release-gate.test.mjs
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { findRelease, lastReleaseTag } from "./release-gate.mjs";

const REPO = "owner/name";
const HEAD = "head";
const merged = (number, ...labels) => ({
  number,
  merged_at: "2026-11-08T10:00:00Z",
  labels: labels.map((name) => ({ name })),
});

/** A GitHub with the tags `tags` (default: three releases), `commits` (oldest first) since the last one and `pulls` per commit sha. */
function github({ commits = [HEAD], pulls = {}, status = 200, tags = ["v1.0.0", "v1.2.0", "v1.1.3"] } = {}) {
  const requests = [];
  const fetch = async (url) => {
    const path = url.replace("https://api.github.com", "");
    requests.push(path);
    const json = () => {
      if (path.includes("/git/matching-refs/tags/v")) return tags.map((t) => ({ ref: `refs/tags/${t}` }));
      if (path.includes("/compare/")) {
        const page = Number(/[?&]page=(\d+)/.exec(path)[1]);
        return { commits: commits.slice((page - 1) * 100, page * 100).map((sha) => ({ sha })) };
      }
      return pulls[/\/commits\/(.+)\/pulls/.exec(path)[1]] ?? [];
    };
    return { ok: status === 200, status, json: async () => json() };
  };
  return { fetch, requests };
}

const find = (options) => {
  const gh = github(options);
  return findRelease({ repo: REPO, sha: HEAD, token: "t", ...gh }).then((outcome) => ({ ...outcome, requests: gh.requests }));
};

describe("findRelease", () => {
  it("releases when the merged pull request has cut-release", async () => {
    const outcome = await find({ pulls: { [HEAD]: [merged(7, "cut-release")] } });
    assert.equal(outcome.release, true);
    assert.equal(outcome.pullRequest, 7);
  });

  it("compares from the last release tag", async () => {
    const outcome = await find({ pulls: { [HEAD]: [merged(7, "cut-release")] } });
    assert.equal(outcome.tag, "v1.2.0");
    assert.ok(outcome.requests.some((p) => p.startsWith(`/repos/${REPO}/compare/v1.2.0...${HEAD}`)));
  });

  it("doesn't release without the label", async () => {
    const outcome = await find({ pulls: { [HEAD]: [merged(7, "bug", "released")] } });
    assert.equal(outcome.release, false);
    assert.match(outcome.reason, /since v1\.2\.0/);
  });

  it("releases when an earlier merge has the label and the newest doesn't", async () => {
    const outcome = await find({ commits: ["a", HEAD], pulls: { a: [merged(6, "cut-release")], [HEAD]: [merged(7)] } });
    assert.equal(outcome.release, true);
    assert.equal(outcome.pullRequest, 6);
  });

  it("releases a security update without cut-release", async () => {
    const outcome = await find({ pulls: { [HEAD]: [merged(9, "dependencies", "security")] } });
    assert.equal(outcome.release, true);
  });

  it("ignores a labeled pull request that wasn't merged", async () => {
    const outcome = await find({ pulls: { [HEAD]: [{ number: 8, merged_at: null, labels: [{ name: "cut-release" }] }] } });
    assert.equal(outcome.release, false);
  });

  it("doesn't release commits pushed straight to main (no pull request), and says how to release them", async () => {
    const outcome = await find({ commits: ["a", "b", HEAD] });
    assert.equal(outcome.release, false);
    assert.match(outcome.reason, /Actions > Release > Run workflow/);
  });

  it("reads every page of commits since the tag", async () => {
    const commits = Array.from({ length: 150 }, (_, i) => `c${i}`);
    const outcome = await find({ commits, pulls: { c0: [merged(1, "cut-release")] } });
    assert.equal(outcome.release, true);
    assert.equal(outcome.pullRequest, 1);
  });

  it("fails on an API error instead of deciding", async () => {
    await assert.rejects(find({ status: 502 }), /GitHub answered 502/);
  });

  it("doesn't release without a release tag, and says how to make the first release", async () => {
    const outcome = await find({ tags: ["vnext"], pulls: { [HEAD]: [merged(7, "cut-release")] } });
    assert.equal(outcome.release, false);
    assert.match(outcome.reason, /No release tag.*First release/);
    assert.ok(!outcome.requests.some((p) => p.includes("/compare/")));
  });
});

describe("lastReleaseTag", () => {
  it("compares versions as numbers", () => {
    assert.equal(lastReleaseTag(["refs/tags/v1.9.0", "refs/tags/v1.10.0", "refs/tags/v1.2.2"]), "v1.10.0");
  });

  it("compares major, minor and patch in that order", () => {
    assert.equal(lastReleaseTag(["refs/tags/v2.0.0", "refs/tags/v1.99.99", "refs/tags/v2.0.1", "refs/tags/v1.0.0"]), "v2.0.1");
  });

  it("skips prerelease tags, which main never releases", () => {
    assert.equal(lastReleaseTag(["refs/tags/v1.2.0", "refs/tags/v1.3.0-rc.1"]), "v1.2.0");
    assert.equal(lastReleaseTag(["refs/tags/v1.3.0-rc.1"]), undefined);
  });

  it("skips other names", () => {
    assert.equal(lastReleaseTag(["refs/tags/v1.2.0", "refs/tags/vnext", "refs/tags/v1.2", "refs/tags/v9.0.0+build", "refs/tags/erikterm-v9.0.0"]), "v1.2.0");
  });

  it("has no answer without a release tag", () => {
    assert.equal(lastReleaseTag([]), undefined);
  });
});
