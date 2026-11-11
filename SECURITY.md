# Security

## Supported versions

Security fixes go into the next release only: update to the latest release to get them.

| Version | Supported |
| --- | --- |
| latest release | yes |
| older releases | no |

## Reporting a vulnerability

Please **don't open a public issue** for a security problem. Report it privately through GitHub:
[Report a vulnerability](https://github.com/eriklieben/erikterm/security/advisories/new) (Security tab →
"Report a vulnerability").

Include what you can of:

- the erikterm version (`erikterm --version`), or the diagnostics from *Help › About erikterm* (`Ctrl+C` copies them);
- the operating system, and on Linux the compositor or window manager;
- steps to reproduce or a proof of concept, and the impact you see. For erikterm that's usually something printed in
  a terminal that does more than show text: reads a file, sets or reads the clipboard, types a command, or reaches
  the control socket.

You'll get an acknowledgement within a week. Once a fix is ready, it is released and a GitHub security advisory is
published, crediting you unless you prefer otherwise.

## What erikterm protects against

erikterm runs the programs you start, as your user. The text it shows comes from anywhere: a remote host, a file you
`cat`, a log, a web page you `curl`. So the rule is that output can't act for you. The design notes behind that:

- **Clipboard (OSC 52).** A program can ask to set the clipboard; erikterm asks you first by default. Programs can
  never read it. [Clipboard from programs](docs/guide/shell-integration.md#clipboard-from-programs).
- **Pasting.** Escape sequences and control characters other than Tab and Enter are removed from every paste, drag
  and drop, quick select and `erikterm cli send-text`, so pasted text can't press keys.
  [Pasting](docs/guide/shell-integration.md#pasting).
- **Kitty images from files.** Images sent as a file path, temp file or shared memory are off by default, or anything
  printed could make erikterm read your local files. When on, only regular, non-empty files are read.
  [Supported protocols](docs/guide/images.md#supported-protocols).
- **Control socket.** `erikterm cli` and the tmux emulation talk to a socket that is mode 0600, in a 0700 directory
  that must be owned by you and must not be a symlink. Anything that reaches it can type into and read every pane.
  [erikterm cli](docs/guide/scripting.md#erikterm-cli),
  [Control socket](docs/development/README.md#how-things-work-inside).
- **Notifications.** Text from programs is escaped and length-capped, and desktop notifications are rate-limited.
  [How things work inside](docs/development/README.md#how-things-work-inside).
- **Theme catalogue.** Downloaded with a shallow HTTPS clone with hooks, redirects and symlinks off, size caps, and a
  check of every file. [How things work inside](docs/development/README.md#how-things-work-inside).
- **Limits.** Decoded images are capped at 320 MB per terminal and 50 megapixels per image, and input waiting for a
  program that stops reading at 16 MB, so output can't exhaust memory.

## Privacy

erikterm sends no telemetry: no analytics, no crash reports, no update checks. It has no HTTP client. Crash logs and
the saved session stay on your machine. It only uses the network through programs you installed, for a feature that
needs them: `gh` for the GitHub status of your branch (`github_status = false` turns it off), `git` when you download
color schemes, fetch in the review screen or push a worktree, `ssh` for `erikterm ssh`, and docker or podman for
`erikterm devcontainer`. [FAQ](docs/faq.md#does-erikterm-collect-telemetry) has the details.

## What is not a vulnerability

- **Code you run.** A program you start in erikterm can do what your user can do, including driving erikterm through
  its control socket. A program in one pane can control the others.
- **Settings you turn on.** `osc52 = "allow"`, `paste_sanitize = false` and `allow_kitty_file_transmission = true`
  switch a protection off on purpose.

Found a way around one of the protections above? Please report it as described above.
