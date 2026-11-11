# Testing screen readers on Windows

erikterm exposes its panes to Windows UI Automation (`src/ErikTerm.App/Accessibility/`). That code is compiled on
Linux and its text model is unit-tested there (`AccessibleTextTests`, `OutputAnnouncerTests`), but **nothing in the
COM layer has run yet**: the provider tree, the events and the notifications have only been compiled. This plan is
the first run. Follow it on a Windows 11 machine with **NVDA** (latest) and **Narrator**, and report what you hear
next to what is expected.

For the rest of the Windows checks see [README.md](README.md).

## 1. Setup

- A build of the branch (`scripts/publish.ps1` or the CI artifact), started with `erikterm --no-restore`.
- NVDA: install it, and in *NVDA menu › Tools › Event Tracker* (or with the *Developer Toolkit* add-on), if you can,
  watch the UI Automation events while you test. Turn on *Speech viewer* (NVDA menu › Tools) so you can copy what was
  said into the report.
- Narrator: `Win+Ctrl+Enter` starts and stops it.
- **Accessibility Insights for Windows** (free, Microsoft) shows the UI Automation tree, properties and events. It is
  the quickest way to see whether something is missing or only spoken wrongly. Its *Live Inspect* mode on the
  erikterm window is step 0 of every finding.
- Use PowerShell (pwsh) in the pane, with the default prompt.

## 2. The tree (Accessibility Insights, no screen reader needed)

| Step | Expected |
|---|---|
| Inspect the erikterm window | A window element named `erikterm — <tab title>` (for example `erikterm — pwsh`). Its children: one element per visible pane. |
| Inspect a pane | Control type *Edit*, localized type "terminal", name = the pane's program or folder, class `ErikTermPane`, automation id `pane-<n>`, *IsKeyboardFocusable* true, *HasKeyboardFocus* true on the focused pane only. Patterns: **Text** and **Text2**. |
| Split the pane (`Ctrl+Shift+\`) | Two pane children within about a quarter of a second; their bounding rectangles match the panes on screen. |
| Move the window to a monitor with another scale (125 %, 150 %) | The pane rectangles still line up with the panes (screen pixels, DPI-aware). |
| Text pattern › *DocumentRange* › GetText | The scrollback and the screen down to the prompt, lines ending in CR LF, no screenful of blank lines at the end. |
| Text pattern › *GetVisibleRanges* | The rows on screen (scroll back with the mouse wheel and ask again: the range follows). |
| Text pattern › *GetSelection* with nothing selected | A degenerate range at the cursor. Select some text with the mouse: the selected text. |
| Text pattern › *RangeFromPoint* on a word | A degenerate range there; expanding it to Word gives that word. |
| Range › *Select* | erikterm's selection appears on that text. |
| Range › *ScrollIntoView* on an old line | The pane scrolls back to show it. |

## 3. NVDA

| Step | Expected | Watch for |
|---|---|---|
| Start NVDA, then focus the erikterm window | NVDA says the window name and "terminal" and the pane name. | Silence (the root provider isn't handed out: WM_GETOBJECT), or "unknown". |
| Type `echo hello` slowly | Each key is spoken **once** (NVDA's own key echo); the echoed characters are not spoken a second time. | Every character spoken twice: the echo filter isn't working. |
| Press Enter | "hello" and then the new prompt are spoken, once, about a tenth of a second after the output. | Nothing (notifications not raised or not spoken); the whole screen spoken. |
| `Get-ChildItem C:\Windows` (long output) | Only the end of the output (at most the last 20 lines) is spoken, then it stops. Running it again replaces rather than queues. | Minutes of speech you can't stop. |
| `1..30 \| % { Write-Progress -Activity x -PercentComplete ($_*3); sleep -m 100 }` | Progress is read at most once a second, the latest value. | A queue of every percentage. |
| Review cursor: `NVDA+Numpad 7/8/9` (previous / current / next line), `NVDA+Numpad 4/5/6` (words), `1/2/3` (characters) | Each row of the screen and the scrollback is read as a line. Words and characters move as expected. A wide character (`echo 日本`) is one character; `é` typed as e + combining accent is one character. | Characters skipped or doubled around wide characters; the review cursor stuck at the end. |
| A long command that wraps onto a second row | Line review reads each row separately (a *line* is a row, like Windows Terminal). | — |
| Left / Right arrow in the PowerShell prompt after typing a word | NVDA reads the character the cursor moved onto (caret events, `GetCaretRange`). | Nothing: TextSelectionChanged not raised or caret range wrong. |
| `NVDA+Tab` (report focus) after switching panes with `Ctrl+Shift+arrow` | The newly focused pane's name. Switching panes says the new pane's name. | The old pane, or nothing. |
| Select text with the mouse, then `NVDA+Shift+Up` (read selection) | The selected text. | — |
| Start `vim` or `less` (alternate screen) and move around | **No** output announcements while it runs (full-screen redraws stay quiet); the review cursor still reads the screen. After quitting, the shell prompt is not re-announced. | Constant chatter from redraws. |
| Open a second tab and switch with `Ctrl+Tab` | The window name changes to the new tab's title; the focused pane is announced. | — |
| Close a pane while NVDA's review cursor is in it | No crash; NVDA moves on. | A crash or a hang (provider not disconnected). |

## 4. Narrator

| Step | Expected |
|---|---|
| Focus erikterm with Narrator on | Narrator reads the window and the pane ("terminal"). |
| Type, press Enter on `echo hello` | Typed characters echoed once; "hello" and the prompt read once. |
| `Caps Lock+Up` (read current line), `Caps Lock+Ctrl+Right` (next word) in scan mode | Lines and words of the terminal read as with NVDA. |
| `Caps Lock+R` (read from here) | Reads down to the prompt and stops — no trailing blank lines. |

## 5. Without a screen reader

| Step | Expected |
|---|---|
| Normal use with no screen reader or inspection tool running | No change in speed or CPU use: the providers are created on the first WM_GETOBJECT, and with no listening client a tick only checks every half second whether one started. Compare `ERIKTERM_FRAME_STATS=1` output with and without Narrator running if you can. |
| Start and quit Narrator while output flows | No crash; no announcements after it has quit. |

## 6. Reporting

For each finding: the step, what was heard (Speech viewer text), what Accessibility Insights showed for the element
or event, NVDA's version and the Windows build. A crash leaves a log (see [README.md §3.20](README.md#320-about-logs-crashes)).
