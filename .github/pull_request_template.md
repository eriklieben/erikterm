## What and why

<!-- What does this change, and why? Link the issue it fixes (e.g. "Fixes #123"). For anything bigger than a small fix,
link the issue where it was agreed on: pull requests without that may be closed (see CONTRIBUTING.md, "What goes into
erikterm"). -->

## How it was tested

<!-- Commands you ran and what you checked by hand: which program, which OS (and compositor on Linux). -->

## Checklist

- [ ] `dotnet test --project tests/ErikTerm.Tests` passes
- [ ] The quality gate passes (`dotnet build src/ErikTerm.App -p:Quality=true` and `scripts/check-complexity.py`, see [CONTRIBUTING.md](../CONTRIBUTING.md#build-and-test))
- [ ] No new NuGet package in `src/` or `bench/`
- [ ] Docs updated (README, `docs/`, `erikterm.example.toml`) when behavior or options changed
- [ ] The PR title follows the [commit message format](../CONTRIBUTING.md#commit-messages) (`fix(pty): ...`, `feat(tabs): ...`; it becomes the changelog entry)

## Screenshots

<!-- For visible changes: before / after. -->
