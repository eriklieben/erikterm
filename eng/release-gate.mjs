// Decides whether a green push to main cuts a release: yes when a pull request merged since the last release tag has
// the label `cut-release`, or `security` (a vulnerability fix shouldn't wait for one). semantic-release then works out
// the version from every commit since that tag, so unlabeled commits (pushed straight to main or merged without the
// label) go out together with the labeled one.
//
// The last release tag is the highest vX.Y.Z tag: main is the only release branch and releases normal versions only
// (release.config.mjs). Without any, nothing is released: the first release is made by hand (Actions > Release, with
// "First release" ticked).
//
// Every commit since the tag is looked at, not only the pushed one: when a second push lands while the first one's CI
// runs, only the newest commit's run releases, and that commit may not be the labeled one.
//
// Run by build.yml (job `release`). A failed GitHub API call fails the job rather than skipping or starting a release.
//
// Environment: GH_TOKEN, GITHUB_REPOSITORY, GITHUB_SHA, GITHUB_API_URL (optional), GITHUB_OUTPUT and
// GITHUB_STEP_SUMMARY (optional). Writes release=true or release=false to GITHUB_OUTPUT.
//
// Tests: node --test eng/release-gate.test.mjs
import { appendFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

export const RELEASE_LABELS = ["cut-release", "security"];
const DEFAULT_API_URL = "https://api.github.com";
const PER_PAGE = 100;
// v1.2.3 only: no prerelease, no build metadata, nothing else (vnext, v1.2, v1.3.0-rc.1).
const TAG = /^refs\/tags\/(v(\d+)\.(\d+)\.(\d+))$/;

/** The highest `vX.Y.Z` of git refs (`refs/tags/...`), comparing the numbers as numbers. */
export function lastReleaseTag(refs) {
  const versions = refs
    .map((ref) => TAG.exec(ref))
    .filter((match) => match !== null)
    .map((match) => ({ tag: match[1], parts: [Number(match[2]), Number(match[3]), Number(match[4])] }));
  versions.sort((a, b) => a.parts[0] - b.parts[0] || a.parts[1] - b.parts[1] || a.parts[2] - b.parts[2]);
  return versions.at(-1)?.tag;
}

function client({ apiUrl, token, fetch }) {
  const get = async (path) => {
    const response = await fetch(`${apiUrl}${path}`, {
      headers: { Accept: "application/vnd.github+json", Authorization: `Bearer ${token}` },
    });
    if (!response.ok) throw new Error(`GitHub answered ${response.status} to ${path}`);
    return response.json();
  };
  /** Every page of `path`; `items` picks the list out of a page. */
  const all = async (path, items = (page) => page) => {
    const result = [];
    for (let number = 1; ; number++) {
      const separator = path.includes("?") ? "&" : "?";
      const page = items(await get(`${path}${separator}per_page=${PER_PAGE}&page=${number}`));
      result.push(...page);
      if (page.length < PER_PAGE) return result;
    }
  };
  return { get, all };
}

/** The outcome for the commit `sha`: `{ release, reason, tag, pullRequest? }`. */
export async function findRelease({ repo, sha, token, apiUrl = DEFAULT_API_URL, fetch = globalThis.fetch, labels = RELEASE_LABELS }) {
  const { get, all } = client({ apiUrl, token, fetch });
  const refs = await all(`/repos/${repo}/git/matching-refs/tags/v`);
  const tag = lastReleaseTag(refs.map((r) => r.ref));
  if (!tag) {
    return {
      release: false,
      reason: 'No release tag vX.Y.Z yet (the first release is made by hand: Actions > Release > Run workflow, "First release" ticked)',
    };
  }

  const commits = await all(`/repos/${repo}/compare/${tag}...${sha}`, (page) => page.commits);
  for (const commit of commits.reverse()) {
    for (const pull of await get(`/repos/${repo}/commits/${commit.sha}/pulls`)) {
      const label = pull.merged_at ? pull.labels.find((l) => labels.includes(l.name)) : undefined;
      if (label) return { release: true, tag, pullRequest: pull.number, reason: `#${pull.number} has the label ${label.name}` };
    }
  }
  return {
    release: false,
    tag,
    reason: `No pull request merged since ${tag} has the label ${labels.join(" or ")} (to release anyway: Actions > Release > Run workflow, dry run off)`,
  };
}

async function main() {
  const outcome = await findRelease({
    repo: process.env.GITHUB_REPOSITORY,
    sha: process.env.GITHUB_SHA,
    token: process.env.GH_TOKEN,
    apiUrl: process.env.GITHUB_API_URL ?? DEFAULT_API_URL,
  });
  const line = `${outcome.release ? "Releasing" : "Not releasing"}: ${outcome.reason}.`;
  console.log(line);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${line}\n`);
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `release=${outcome.release}\n`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) await main();
