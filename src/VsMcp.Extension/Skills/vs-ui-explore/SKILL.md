---
name: vs-ui-explore
description: Operate the UI of a Visual Studio debuggee with the Extended vs-mcp tools (ui_window_*, ui_menu_*, standard_dialog_*, standard_file_dialog_*, diagnostics_*) with the right tool priority, safety rules and Observe→Act→Verify workflow, and autonomously crawl the UI to produce a bug/coverage report. Use when asked to operate, test, crawl or debug a debuggee's windows, dialogs, file dialogs, menus or keyboard input.
---

# vs-ui-explore

Revision: Phase 11 (+Phase 12 notes)

## Purpose

Drive a running, debugged desktop application from Visual Studio through its UI
with the `vs-mcp` tools — either for a single targeted task ("click Save, then
answer the dialog") or for an autonomous crawl that reports bugs.

The Extended tools (`ui_window_*`, `ui_menu_*`, `standard_dialog_*`,
`standard_file_dialog_*`, `diagnostics_*`) take an explicit window handle, so
they reach modal dialogs, owned windows and popup menus. The upstream generic
tools (`ui_click`, `ui_find_elements`, `ui_send_keys`, `ui_snapshot`, …) are
fixed to the debuggee's main window and are only a fallback when no Extended
tool covers the case.

Before acting, confirm via `get_status` that a solution is open and
`debuggerMode` is `Running`; if it is `Design`, ask the user whether to start
debugging instead of starting it silently. If `ui_list_windows` lists no debuggee window, stop and
ask rather than guessing.

## Quick decision tree

```text
MessageBox / TaskDialog?
  → standard_dialog_*

Open / Save / Folder dialog?
  → standard_file_dialog_*

Popup / ContextMenu / Win32 Menu?
  → ui_menu_*

Otherwise a debuggee Window?
  → ui_window_*

Only when no Extended tool exists → upstream generic ui_*
```

## Core safety rules

Dedicated tools win. They carry semantics, safety checks and diagnostic events
that a generic click does not have:

```text
standard_dialog_*      > generic click
standard_file_dialog_* > generic click
ui_menu_*              > generic click
ui_window_*            > upstream generic ui_*
ui_window_send_keys    > upstream ui_send_keys
```

**Observe → Act → Verify.** Never act on a window you have not observed in this
step, and never report success without verifying it: find → click → check the
app result, the window state or the dialog state.

**Modal safety.** When an error says `blocked by modal window`, `ownerDisabled`
or `siblingDisabled`, do not click the owner again. Find the blocking dialog
(`standard_dialog_detect`, `standard_file_dialog_detect`, `ui_list_windows`),
handle it, and only then return to the owner. `ui_window_get_info` reports
`modalState.isBlockedByModal` with the blocking window — read it first.

**Stale HWND.** A window, dialog or menu that may have closed must be
re-detected, never reused from an earlier response:

```text
ui_list_windows / ui_wait_for_window
standard_dialog_detect
standard_file_dialog_detect
ui_menu_detect
```

**Selector policy.** Prefer `AutomationId`, then `ClassName` / `ControlType`,
then `Name`. If several candidates match, narrow the selector. Do not pick an
ambiguous candidate with `index=0`; use `index` only when you have listed the
candidates and can point at one deliberately. When two elements share a `Name`
(a dialog's own "Close" button and the window frame's close button, for
example), the one with the application-specific `AutomationId` is the target;
frame buttons carry generic ids such as `Close` / `Minimize` / `Maximize`.

**detect vs wait.** `*_detect` / `ui_list_windows` list what is open right now;
`*_wait` / `ui_wait_for_window` poll until it appears. Use `wait` right after
the action that opens something, `detect` / `list` when re-checking the state.

**Localized text.** Select by `AutomationId`, `ControlType`, `ClassName` or
Win32 control ID. `Name` / `Title` / button captions change between Japanese and
English Windows and are informational only.

**Screenshots are secondary.** Decide from HWNDs, structured info and the UIA
tree; use an image only to confirm or to describe something to the user. Never
derive click coordinates from a screenshot.

## Normal Window

```text
ui_list_windows / ui_get_active_window
↓
ui_window_get_info          (read modalState first)
↓
ui_window_find_elements
↓
ui_window_click / double_click / right_click / drag / mouse_wheel / send_keys
↓
ui_window_wait_idle + state verification
```

`ui_window_get_info` also returns geometry (DPI, monitor, bounds in physical
pixels) — use those values instead of assuming 96 DPI.

## Standard Dialog

```text
standard_dialog_detect / standard_dialog_wait
↓
standard_dialog_get_info
↓
standard_dialog_capture        (only if you need the picture)
↓
standard_dialog_execute
↓
standard_dialog_wait_closed
```

Press buttons with the logical `action` (ok, cancel, yes, no, retry, abort,
ignore, tryAgain, continue, close, help) or with `buttonId` for TaskDialog
custom buttons. Never select a button by its caption. Prefer this over a
generic `ui_window_click` on a MessageBox or TaskDialog button.

## File Dialog

```text
standard_file_dialog_detect / standard_file_dialog_wait
↓
standard_file_dialog_get_info
↓
standard_file_dialog_set_filename   or   standard_file_dialog_select
↓
standard_file_dialog_confirm / standard_file_dialog_cancel
↓
standard_file_dialog_wait_closed
```

- A save overwrite confirmation is a separate dialog: re-detect it with
  `standard_dialog_detect` and answer it with `standard_dialog_execute`.
- Never reuse the HWND of a dialog that has closed.
- Do not treat the displayed text as the source of truth; read `fileName`,
  `currentFolderPath` and `selectedItems` from `standard_file_dialog_get_info`.

## Popup / Menu

```text
ui_window_right_click       (use its 'menuHandles')
↓
ui_menu_wait
↓
ui_menu_get_info
↓
ui_menu_select
```

Nested menus: `ui_menu_select_path` with the path segments. Just dismissing a
menu: `ui_menu_close`.

Do not send `ui_window_send_keys escape` to close a menu:

- A WPF ContextMenu can swallow the first ESC right after it opens.
- `ui_menu_close` retries ESC and falls back to an outside click.
- `ui_window_send_keys` on a popup HWND is refused on purpose.

Windows reuses the HWND of a context menu that was just closed, and WPF reuses
the popup HWND of a submenu that was opened once. Therefore `ui_menu_detect` /
`ui_menu_wait` / `ui_menu_wait_closed` decide by content, not by HWND identity —
trust `ui_menu_detect`'s `menus[]`, `ui_menu_wait`'s `menu` / `found` and
`ui_menu_wait_closed`'s `reason`, not a handle you saw earlier.

## Keyboard

```text
For keyboard input to a debuggee window, prefer ui_window_send_keys.
Do not rely on upstream ui_send_keys for x64 Extended UI automation.
```

(Phase 9 measured `SendInput` reporting `inserted=0` there.)
`ui_window_send_keys` takes the target HWND and a `mode`: `auto` (default,
foreground only when the debuggee's active window is that window),
`noForeground` (fails unless it already is the foreground window) and
`foreground` (brings it to the front, fails if that did not work). A handle
that is an open popup menu is refused — use `ui_menu_close` instead. Check
`insertedEvents` in the response before assuming the keys arrived.

## Wait / Capture

Wait instead of sleeping:

```text
ui_wait_for_window / ui_wait_for_window_closed
standard_dialog_wait / standard_dialog_wait_closed
standard_file_dialog_wait / standard_file_dialog_wait_closed
ui_menu_wait / ui_menu_wait_closed
ui_window_wait_idle          (mode: single / active / all)
```

Capture with the dedicated tool for that kind of window:

```text
Window        ui_capture_window_by_handle / by_title / ui_capture_active_window
              ui_window_capture_region / ui_window_snapshot
Menu          ui_menu_capture
MessageBox    standard_dialog_capture
File dialog   standard_file_dialog_capture
```

These work while Visual Studio itself has the foreground; the upstream
`ui_capture_window` only ever captures the main window.

## RawView / Search performance

Keep `view=control` (default). Use `view=raw` only when an element is missing
from the control view (for example the `TaskDialog` Pane of a TaskDialog), and
narrow the walk with `maxVisited`, `maxResults` and a real selector.

Do not start with a search that is too wide (a bare `controlType` plus a huge
`maxVisited`). Search order:

```text
AutomationId / ClassName
↓
ControlType if needed
↓
RawView last
```

`truncated=true` in the response means the walk was stopped by `maxVisited` or
by the timeout, so the result list is incomplete — narrow it and search again
instead of concluding the element does not exist.

## Diagnostics

`diagnostics_get_status` reports `enabled`, `level`, `writerHealthy`,
`droppedEventCount` and `currentLogFile`. Call it before exporting: with
`writerHealthy=false` the JSONL may be missing, so an empty export is **not**
proof that nothing happened.

Put `diagnostics_mark` **before** a reproduction ("before login dialog
reproduction") and again after it ("issue reproduced"), then export:

```text
diagnostics_export  sessionId   — the whole session, the usual choice
                    ↓ minutes   — when the session is long
                    ↓ correlationId — one tool call whose id you already know
```

A marker has a correlationId of its own, so exporting by the marker's
correlationId returns only the marker. The failing tool's correlationId is not
part of its error text; it is in `events.jsonl` of a sessionId export, next to
your markers. `eventCount` / `errorCount` in the export result count the whole
session: to confirm that one reproduction is inside, read `events.jsonl`
between the `sequence` values returned by your two markers (`tool.end` with
`result: error` there is the failure, and its correlationId links its events).

Log level: `info` for normal work, `verbose` while reproducing a problem,
`trace` only for a short deep dive — then set it back to `info` with
`diagnostics_set_level`.

Sensitive data is never written: the `text` of send_keys, file names and paths,
and any value whose key looks like password, passphrase, secret, token, apiKey,
authorization, cookie, credential or privateKey. `includeUiText=true` enables
UIA names and titles but still does **not** write the send_keys text. Never put
a secret into a `diagnostics_mark` message.

## Failure recovery

Do not repeat the same action immediately after it failed:

```text
1. Read the error
2. Check whether the HWND is still valid
3. Check the modal / popup / menu state
4. Re-find the element if needed
5. Check diagnostics
6. Fix the selector / target
7. Retry
```

Read the human-readable reason in the error text first; the embedded JSON (candidate
lists, counts) is structured supplementary data, not the primary message. The `reason`
values returned by `*_wait_closed` are listed in `docs/extended/11-DIAGNOSTICS.md`.

## Autonomous crawl mode

When the user asks to "test all the screens", "crawl the UI" or "find UI bugs
autonomously", run this loop (cap: 40 steps or 10 distinct screens; stop after
5 iterations without anything new):

1. **Observe.** `ui_window_snapshot` on the active debuggee window
   (`ui_get_active_window`), `depth: 8`.
2. **Fingerprint.** `window title + focused role + focused automationId + top
   level child roles`; skip screens already visited.
3. **Catalogue.** Collect nodes whose actions contain invoke / toggle / select
   / expand; prefer elements with an `automationId`.
4. **Decide.** Pick one unexercised target, navigation before destructive
   verbs, toggles and selects last.
5. **Act.** `ui_window_click` (or `ui_menu_select` for menu items). Never raw
   coordinates.
6. **Settle.** `ui_window_wait_idle` with `quietMs: 600`, `timeoutMs: 4000`
   (never above ~10 s).
7. **Verify.** Snapshot again; a new window or dialog is a new screen. No
   change after a non-trivial action is a suspicious result to record.
8. **Escape dialogs.** An unexpected MessageBox / TaskDialog is closed with
   `standard_dialog_execute` (cancel / close / no), a file dialog with
   `standard_file_dialog_cancel`, an open menu with `ui_menu_close`.

Hard stops — ask the user first: `Delete`, `Remove`, `Uninstall`, `Reset`,
`Restore Defaults`, `Format`, `Sign out`, `Clear history`, `Overwrite`,
`Discard changes`, `Revert`, anything that looks like a purchase, and `OK` on a
dialog whose message you have not read.

Bug signals to record: error dialogs (title or message contains Error /
Exception / Failed / Unhandled), an unexpected `Break` from `debug_get_mode`
(then `debug_get_callstack`), UIA binding errors (`diagnostics_binding_errors`
before and after), controls disabled where enabled was expected, dead screens,
dialogs that cannot be closed by their own buttons, errors in `output_read`.

Report in Markdown: Summary (screens visited, elements exercised, issues by
severity, duration) → Screens visited → Issues (Critical / Warning / Info, each
with reproduction path and evidence) → Coverage gaps → Recommended next steps.
Cite element ids and window handles, and attach one capture per unique screen.

## Do not

```text
Do not pick the first ambiguous element automatically.
Do not interact with a blocked modal owner.
Do not reuse a stale HWND after a window closes.
Do not use upstream ui_send_keys for Extended x64 keyboard automation.
Do not prefer screen coordinates over structured selectors.
Do not leave diagnostics at trace during normal long-running work.
Do not intentionally place secrets in diagnostics_mark.
```

## Examples

```text
Button        ui_list_windows → ui_window_find_elements (automationId)
              → ui_window_click → verify the changed text / state

MessageBox    standard_dialog_wait → standard_dialog_get_info
              → standard_dialog_execute (action) → standard_dialog_wait_closed

Open file     standard_file_dialog_wait → standard_file_dialog_get_info
              → standard_file_dialog_set_filename or standard_file_dialog_select
              → standard_file_dialog_confirm → standard_file_dialog_wait_closed

ContextMenu   ui_window_right_click → ui_menu_wait
              → ui_menu_get_info → ui_menu_select

Nested        ui_menu_select_path (["Submenu", "Sub Item 2"])

Keyboard      ui_window_send_keys (windowHandle, keys / text, mode)
              → verify insertedEvents and the resulting state

Problem       diagnostics_get_status → diagnostics_mark ("before …")
              → reproduce → diagnostics_mark ("issue reproduced")
              → diagnostics_export (sessionId)
```
