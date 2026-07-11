# Bugs (history)

Fixed bugs, archived from `BUGS.md` so the active list stays scannable
on session start. **Archaeological**: read only when consulted, not at
session start. When a bug is fixed, append its entry here rather than
to the active file.

The bug breakout file at `bugs/<slug>.md` (when present) stays in place
as the historical diagnosis record; the entry here is a brief
description of the fix and the commit it landed in.

## Cross-reference resolution

`/nightshift:ready` does **not** scan this file. When a bug is fixed, every other
`**Requires:**` line in `FEATURES.md` / `BUGS.md` that referenced it is
edited at the same time to drop the now-satisfied reference (mirror of
the `FEATURES.md` convention). The active `Requires:` lines therefore
describe what is *currently* blocking; this file is purely
archaeological.

## Entries

### Chrome window activation fails when a non-Chrome process holds the foreground

Reported: 2026-07-10. Fixed: 2026-07-11 in 6fbbbc3 (deployed same day).

**Symptom:** invoking TheCloser over a background Chrome window while another process held the foreground failed to activate Chrome, so CTRL-W never reached it or landed elsewhere.

**Root cause:** SetForegroundWindow was denied by a foreground rule the lock-timeout suppression cannot lift (the input lock: the user's last physical input went to the foreground process), and attaching to the *target's* thread confers no rights because the target does not hold them either. All pre-2026-07-10 observations were additionally poisoned by the SystemParametersInfo uiParam/pvParam bug (fixed in 4861ca2) that stranded the system-wide lock timeout at 0.

**Fix:** ForegroundActivator.TryActivateNatively additionally attaches the calling thread to the *current foreground owner's* thread around SetForegroundWindow, borrowing the input-queue state that holds foreground rights; both attaches detach in the rung's finally.

**Verified interactively 2026-07-11** (explorer focused, viewport hover, per-rung trace): base case at 16:27, minimize-then-restore at 16:37, two-windows-hover-the-stale-one at 16:39. All three: child-HWND rung fails (expected; activating child windows directly is unreliable), root rung succeeds natively in ~140ms, click fallback never engages.

**Durable learnings** (full diagnosis in this entry's git history in `BUGS.md`):

- The lock-timeout suppression (ForegroundLockSuppression) lifts only the timeout-based denial rule; the input-lock rule requires sharing the foreground owner's input queue via AttachThreadInput.
- Chrome's HWND hierarchy under the cursor varies by invocation: WindowFromPoint sometimes returns the Chrome_WidgetWin_1 root, sometimes a child; the ladder's target-then-root rung pair covers both.
- The title bar click fallback works for Chrome and its default Left click point (left+10, top+20) lands above the tab click area (no accidental tab switch), at the cost of ~200ms and a cursor round-trip; it remains the last rung.
- Closing a specific background tab (tab-strip hover) is deliberately unsupported: the app is bound to a mouse button, so Chrome's native middle-click-to-close is already an equivalent gesture away.
- Prior art: a SwitchToThisWindow() fallback was added and reverted (04cbeb7 reverted b971d68) with no recorded motivation; treat as untried rather than rejected if the ladder ever needs another rung.
