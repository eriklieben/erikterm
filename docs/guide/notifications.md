# Notifications

erikterm tells you when something needs you in a tab you aren't looking at: an agent waiting or finished, a long
command ending, or a program's own notification (`OSC 9`, `OSC 777`, `OSC 99`). It shows these in the window first,
and only on your desktop once you allow it.

## Allowing desktop notifications

**Nothing reaches your desktop until you say so.** The first time something would send a desktop notification, a bar
under the tab bar asks instead: *Claude wants to send desktop notifications*, with **Allow**, **Only in erikterm** and
**Settings…**. ✕ decides later, and it asks again after a restart. Your answer is stored in
`~/.local/state/erikterm/preferences.json`; your config file is never edited.

Desktop notifications are only sent while no erikterm window has focus, and never for the pane you're looking at.
They are rate-limited, so a pane that floods is cut off. Clicking one focuses its pane.

```toml
# os_notifications = "on"   # "on" / "off" override your stored answer; "ask" or unset: use it, or ask
```

The config setting wins, then your stored answer, then the question. `agent_notifications = false` also keeps
agents off the desktop (see [Coding agents](coding-agents.md)).

## The notification bar

The bar under the tab bar shows the latest unread notification of the tab you're on: a status dot or bell, the
message, how long ago, **Jump** (goes to the pane, switching tab and window) and ✕. The bar takes room in the layout,
so it never covers anything.

- Other tabs keep their status dot and unread marker, and the bar says **+N in other tabs**. When the bar has nothing
  of its own to show, it shows news from other tabs for `notification_bar_timeout_ms`.
- Most items leave after `notification_bar_timeout_ms`. *Needs permission* and *has a question* stay until you look
  at that pane, answer it or dismiss it.
- A notification belongs to the tab its pane is in now. A teammate's belongs to its lead's tab.

```toml
notification_bar = true              # false: toasts instead
notification_bar_position = "top"    # or "bottom"
notification_bar_scope = "tab"       # "all": notifications of every window, labelled with their window and tab
notification_bar_timeout_ms = 10000  # 0: until dismissed
```

## The history

Click **N unread** or **+N in other tabs**, press `Ctrl+Shift+B` or choose *Notifications…* in the palette to see the
last 100 notifications across windows, unread first.

| Key | |
|---|---|
| `Enter` | jump to the pane |
| `Del` | dismiss one |
| `Shift+Del` | clear all (also *Clear all*) |
| `T` | switch between all tabs and this tab (also the **All / This tab** control) |

Focusing a pane marks its notifications read, and closing the history marks what it showed read.

## Long-running commands

With [shell integration](shell-integration.md), a command that ran at least `notify_command_duration` seconds and
finishes in a pane you aren't looking at is announced: *✓ cargo build finished in 1m 12s* or *✗ make exited 2 after
15s*. It shows on the bar, in the history and, when allowed, on the desktop.

```toml
notify_command_duration = 10   # seconds; 0 turns it off
```

## Palette and keys

*Notifications…* (`Ctrl+Shift+B`), *Clear Notifications*, *Toggle Desktop Notifications*, and *Desktop Notifications:
Allow / Only in erikterm / Ask Me Next Time*. For `[keys]`: `NotificationsPanel`, `ClearNotifications`,
`ToggleDesktopNotifications`.

Notification text comes from whatever a program printed, so control characters are removed, lengths are capped, and
it is shown as plain text.
