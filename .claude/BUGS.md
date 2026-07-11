# Bugs

Known bugs awaiting attention. Short entries live here; bugs that need
more than a few lines of description graduate to a dedicated file under
`.claude/bugs/<slug>.md`.

This file is **one of four repo-local indexes** Claude reads on every
session start (alongside `QUICK_WINS.md`, `FEATURES.md`, `PATTERNS.md`).
When a bug is fixed, append its entry to
[`BUGS_HISTORY.md`](BUGS_HISTORY.md); do not keep a `## Fixed` section
inline.

## Requires lines

**Every open bug entry carries a `**Requires:**` line** declaring what
must be in place before the fix can land. Comma-separated, same shape
as `FEATURES.md` (long lines may wrap; `/nightshift:ready` joins them before
parsing):

- A markdown link to a feature, quick win, or bug. The reference is a
  current blocker; under the walk-and-remove convention below, a
  satisfied dependency is edited out of the line at the moment it
  ships or is fixed.
- Bare text. An external primitive (driver release, vendor support,
  user decision) the user confirms case by case.
- The literal word `none.` if the fix is unblocked.

A missing `Requires:` line is a structural error. `/nightshift:ready` parses these
lines. History entries don't carry `Requires:` lines.

**When a bug is fixed**, move its entry to
[`BUGS_HISTORY.md`](BUGS_HISTORY.md) with a brief note on the fix and
the commit it landed in; drop its `Requires:` line in the move. If the
bug had its own file, keep the file in place as a historical record of
the diagnosis.

**Then walk every other `**Requires:**` line in `FEATURES.md` and
`BUGS.md`** and remove references to the just-fixed bug: if it was the
only item on the line, set the line to `Requires: none.`. Mirror of the
`FEATURES.md` walk-and-remove convention — `/nightshift:ready` never has to
consult `BUGS_HISTORY.md`.

## Open

### Test hygiene: stray GUID-named log files in %TEMP%

Reported: 2026-07-10. Status: open (minor).

WindowCloserTests' unknown-method fallback case, CrashRepairTests' failure-path tests, and ForegroundLockSuppressionTests' restore-failure paths write through real Logger instances with GUID-suffixed names, leaving stray `TheCloser.Tests.<guid>.log` files in %TEMP% on each run (LoggerTests clean up after themselves; these three classes do not). While sweeping, also consider asserting the fallback warning log line in WindowCloserTests, which currently exercises the warning path but never verifies the message.

The 2026-07-11 full-solution review verified the three write paths: CrashRepairTests' static Logger is written by `TryRepairCrashedState_RestoreFails_KeepsRecordForNextTick` (production logs in `CrashRepair`), ForegroundLockSuppressionTests' static Logger by `Dispose_RestoreFails_KeepsRecordPending` (production logs in `ForegroundLockSuppression.Dispose`), and WindowCloserTests' per-test logger by the `NO-SUCH-METHOD` fallback case. Fix shape: a small `TempLogger : IDisposable` helper next to `TestNames`, following the cleanup pattern LoggerTests already uses (delete `<name>.log` and `.log.old` in `Dispose`).

Close the review's test coverage gaps in the same sweep (most valuable first):

- `SharedState` offset independence: no test writes both the throttle tick and the repair record, so an off-by-4 in `RepairFlagOffset` overlapping the 8-byte tick would pass silently. Set a record, `WriteThrottleTick(long.MaxValue)`, assert the record intact; and the reverse.
- Logger rotation at exactly 1 MiB: production rotates only on strictly-greater; tests cover 16 bytes and threshold+1 but not the boundary, which must NOT rotate.
- `ResolveKillMethodName("")`: empty string takes a different path than `null` (`?? DefaultKillMethod` does not apply; it falls to `ContainsKey("")`). Also six of the nine documented method names are untested; a theory over the full documented list pins the dictionary against typos and README drift.
- `ProcessSettingsParser`: numeric ClickPosition `"1"` silently parses to `Center` (the `Enum.IsDefined` guard only rejects out-of-range numerics); precedence when both simple value and `Method` key exist is unpinned; invalid ClickPosition with null `logWarning` (cheap NRE guard).

**Requires:** none.

### Chrome window activation fails when a non-Chrome process holds the foreground

Reported: 2026-07-10. Status: open.

**Symptom:** invoking TheCloser over a Chrome window while some non-Chrome process is in the foreground fails to activate the Chrome window, so the CTRL-W keypress never reaches it (or lands elsewhere). Activation works when Chrome itself already holds the foreground.

**Evidence gathered 2026-07-10 (adversarial review):**

- The deployed log (June 20 to July 10, ~200+ `chrome -> CTRL-W` invocations) contains ZERO "Failed to set foreground" lines for any process. That line only fires when all ladder rungs fail, so on every failing invocation some rung returned true and the click fallback was never attempted.
- All historical observations are poisoned: the June-era deployed binary re-set the system foreground lock timeout to 0 on every "restore" (the SystemParametersInfo uiParam/pvParam bug fixed in 4861ca2), so from the first invocation after each logon the system-wide timeout sat at 0, letting any app (including the old foreground app) freely self-foreground. The fixed binary was deployed 2026-07-10 15:46; SPI_GETFOREGROUNDLOCKTIMEOUT verified back at 200000.
- REFUTED lead: AttachThreadInput attaching to the target thread (instead of the foreground owner's) is real but cannot be the mechanism; the code disables the lock timeout around every SetForegroundWindow, which grants permission regardless of the attach. The failure is downstream of permission: focus/input delivery.
- Counterpoint from the 2026-07-11 full-solution review (independent, unaware of the refutation): the lock-timeout suppression lifts only the timeout-based denial rule, not the other foreground-denial rules (foreground queue locked by recent input, active menu), so attaching to the foreground owner's thread can still matter in those cases. Does not overturn the refutation for the common case, but keeps the foreground-thread attach on the candidate list (already step 3 below).
- Supported mechanism: SetForegroundWindow succeeds and IsForeground honestly reports true, but keyboard focus in the target thread is never established (no SetFocus anywhere in the codebase), or the previous foreground app reasserted itself (freely, under the then-broken timeout=0) in the ~100ms between the check and the injection. The chord then lands in a queue whose focus is not the Chrome tab.
- Structural note: on this machine Chrome exposes NO input-visible child HWNDs (only a disabled+transparent "Intermediate D3D Window"), so WindowFromPoint returns the top-level Chrome_WidgetWin_1 and target == root in the ladder; any fix plan assuming a Chrome_RenderWidgetHostHWND child is built on a hierarchy that does not exist here.
- Latent secondary hazard: if the click fallback ever does engage for Chrome, the default click point (TitleBarClickOffsetX/Y in ForegroundActivator: left+10, top+20) is inside the tab strip and can switch tabs before CTRL-W fires, closing the wrong tab.
- Code map (post-refactor): the activation ladder and its rungs live in `TheCloser\ForegroundActivator.cs` (TryActivate, TryActivateNatively, TryActivateByClicking); the foreground-lock disable/restore scope is `TheCloser.Shared\ForegroundLockSuppression.cs` (unit-tested via injected delegates); WindowCloser now only resolves and dispatches kill methods and logs the all-rungs-failed line in SendKeyPressIfForeground.

**Next steps (require mouse; deferred):**

1. Re-verify the bug under the fixed binary (deployed 2026-07-10). It may have been entirely a symptom of the stranded timeout=0.
2. Diagnostics deployed 2026-07-11: per-rung ladder logging (ForegroundActivator.TryActivate), timestamps on every log line (Logger.Log), and AttachThreadInput failure logging (ForegroundActivator.TryActivateNatively). After the next failing occurrence, the log will state exactly which rung claimed success, whether the attach failed, and when, correlatable with the interactive attempt.
3. If the bug survives: try SetFocus on the target while AttachThreadInput is active (the classic remedy absent from this codebase; the place is ForegroundActivator.TryActivateNatively), and only then consider attaching to the foreground owner's thread instead of the target's.
4. Prior art: a SwitchToThisWindow() fallback was added and reverted (04cbeb7 reverted b971d68); re-check the revert motivation before reintroducing.

**Verification once fixed:** with Notepad (or any non-Chrome app) focused, invoke TheCloser over a background Chrome window; the hovered Chrome tab should close. Repeat with Chrome minimized-then-restored and with multiple Chrome profiles/windows.

**Requires:** interactive re-verification session with mouse access.

## History

Fixed bugs are archived in [`BUGS_HISTORY.md`](BUGS_HISTORY.md), loaded
on demand only (not at session start) so the active list above stays
scannable. When a bug is fixed, append its entry there rather than to
this file, AND walk every other `**Requires:**` line in `FEATURES.md`
/ `BUGS.md`: remove the now-satisfied reference (if it was the only
one, set the line to `Requires: none.`). The active `Requires:` lines
describe what is *currently* blocking, so `/nightshift:ready` never has to consult
the history file — the dependency graph settles as bugs are fixed.
