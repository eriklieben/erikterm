// Commit message rules: the Angular format, `type(scope): subject`, checked on pull request titles (the squash-merge
// commit message) by .github/workflows/commit-messages.yml and, after `pnpm install` in the repository root, by a local
// commit-msg hook. Details and examples: docs/development/README.md, "Commit messages and releases".
//
//   echo "feat(render): draw curly underlines" | pnpm exec commitlint
//
// config-conventional = the Angular types (build, ci, docs, feat, fix, perf, refactor, style, test) plus chore and
// revert, and also accepts the `!` breaking-change marker (`feat(config)!: ...`), which release.config.mjs understands.
// Commits from before this format (plain sentences) stay as they are: nothing checks old history.
export default {
  extends: ["@commitlint/config-conventional"],
  rules: {
    // Suggested scopes: a warning, not an error, and the scope stays optional. Keep in sync with
    // docs/development/README.md.
    "scope-enum": [
      1,
      "always",
      [
        // The terminal itself
        "vt", "render", "font", "input", "pty", "shell", "images",
        // The window around it
        "tabs", "panes", "search", "vim", "settings", "config", "themes", "session", "notifications",
        // Developer tools
        "agents", "git", "github", "review", "worktrees", "tmux", "ssh", "cli", "ipc",
        // Platforms
        "linux", "macos", "windows",
        // The repository
        "docs", "ci", "deps", "deps-dev", "release", "packaging", "bench", "tests",
      ],
    ],
    // Bodies and footers hold URLs, stack traces and generated release notes (semantic-release): no line length
    // limit there. The header (the line that ends up in the changelog) stays at 100 characters.
    "body-max-line-length": [0],
    "footer-max-line-length": [0],
  },
};
