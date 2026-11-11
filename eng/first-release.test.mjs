// Tests of eng/first-release.mjs:
//   node --test eng/first-release.test.mjs
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { analyzeCommits, FirstReleaseError } from "./first-release.mjs";

const logger = { log: () => {} };

describe("analyzeCommits", () => {
  it("asks for a release when there is no last release", () => {
    // Arrange
    const context = { lastRelease: {}, commits: [{ message: "initial commit" }], logger };

    // Act
    const type = analyzeCommits({}, context);

    // Assert
    assert.equal(type, "major");
  });

  it("asks for a release without a lastRelease at all", () => {
    // Arrange
    const context = { logger };

    // Act
    const type = analyzeCommits({}, context);

    // Assert
    assert.equal(type, "major");
  });

  it("refuses once a release exists, naming its tag", () => {
    // Arrange
    const context = { lastRelease: { version: "1.2.0", gitTag: "v1.2.0" }, logger };

    // Act
    const act = () => analyzeCommits({}, context);

    // Assert
    assert.throws(act, (error) => error instanceof FirstReleaseError && error.semanticRelease && /v1\.2\.0 is already released/.test(error.message));
  });
});

describe("release.config.mjs", () => {
  const load = async (env) => {
    const saved = process.env.ERIKTERM_FIRST_RELEASE;
    if (env === undefined) delete process.env.ERIKTERM_FIRST_RELEASE;
    else process.env.ERIKTERM_FIRST_RELEASE = env;
    try {
      // A fresh copy each time: the config reads the environment when it's imported.
      const { default: config } = await import(`../release.config.mjs?first=${env}-${Math.random()}`);
      return config.plugins.map((p) => (Array.isArray(p) ? p[0] : p));
    } finally {
      if (saved === undefined) delete process.env.ERIKTERM_FIRST_RELEASE;
      else process.env.ERIKTERM_FIRST_RELEASE = saved;
    }
  };

  it("lists the plugin right after the commit analyzer with ERIKTERM_FIRST_RELEASE=true", async () => {
    // Act
    const plugins = await load("true");

    // Assert
    assert.equal(plugins.indexOf("./eng/first-release.mjs"), plugins.indexOf("@semantic-release/commit-analyzer") + 1);
  });

  it("leaves it out otherwise", async () => {
    // Act
    const plugins = [...(await load(undefined)), ...(await load("false"))];

    // Assert
    assert.ok(!plugins.includes("./eng/first-release.mjs"));
  });

  it("releases from main only", async () => {
    // Act
    const { default: config } = await import("../release.config.mjs");

    // Assert
    assert.deepEqual(config.branches, ["main"]);
  });
});
