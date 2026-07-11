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
- While in LoggerTests: retire the duplicated fixed-UTC-timestamp construction (a named variable in `Log_NonEmptyMessage_PrefixesUtcTimestamp`, an inline literal in `Log_EmptyMessage_WritesBareSeparatorLine`) into a shared fixture field.

**Requires:** none.

## History

Fixed bugs are archived in [`BUGS_HISTORY.md`](BUGS_HISTORY.md), loaded
on demand only (not at session start) so the active list above stays
scannable. When a bug is fixed, append its entry there rather than to
this file, AND walk every other `**Requires:**` line in `FEATURES.md`
/ `BUGS.md`: remove the now-satisfied reference (if it was the only
one, set the line to `Requires: none.`). The active `Requires:` lines
describe what is *currently* blocking, so `/nightshift:ready` never has to consult
the history file — the dependency graph settles as bugs are fixed.
