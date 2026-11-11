// semantic-release plugin (a local one: release.config.mjs lists it by path, and only when ERIKTERM_FIRST_RELEASE=true)
// for the very first release of the repository, while there is no release tag yet. semantic-release releases only when
// a commit since the last release is a feat / fix / perf / revert or a breaking change; this plugin's analyzeCommits
// asks for a release anyway, so the first one is made whatever the commits are. semantic-release's first release on a
// release branch is 1.0.0, whatever the release type, and the highest type any analyzer returns wins.
//
// Set by release.yml when it's run by hand with "First release" ticked (input first_release), in both the `version`
// job (the dry run that picks the version the binaries are built with) and the `release` job, so both get 1.0.0. The
// workflow refuses that input once a v* tag exists; this plugin refuses it too (an error, so nothing is released) when
// semantic-release found a last release.
//
// Tests: node --test eng/first-release.test.mjs

export class FirstReleaseError extends Error {
  constructor(message) {
    super(message);
    this.name = "FirstReleaseError";
    this.code = "EFIRSTRELEASE";
    this.semanticRelease = true;
  }
}

/** "major" (any release type would do: the first release is 1.0.0) when there is no last release; an error otherwise. */
export function analyzeCommits(pluginConfig, { lastRelease, logger }) {
  if (lastRelease?.version) {
    throw new FirstReleaseError(
      `First release requested, but ${lastRelease.gitTag ?? `v${lastRelease.version}`} is already released: run the Release workflow without "First release".`,
    );
  }
  logger?.log("First release: no release tag yet, releasing whatever the commit types are.");
  return "major";
}
