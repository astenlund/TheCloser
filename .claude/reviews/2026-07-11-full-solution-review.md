# Full Solution Review, 2026-07-11

Scope: entire solution at HEAD `527b6d3` (clean working tree). Three fresh-context reviewers (correctness/concurrency, architecture/quality, test quality); all findings below were verified against the source before inclusion.

## Verdict

No critical defects. The crash-repair protocol, the riskiest part of the codebase, held up under exhaustive kill-point and interleaving tracing: value-before-flag writes, flag-before-value reads, restore-before-clear, and the daemon's acquire-not-probe mutex discipline in `CrashRepair` are all correct. The findings are hardening, diagnosability, and hygiene items, plus one strong hypothesis for the open Chrome bug.

## Headline finding: likely root cause of the open Chrome-activation bug

`ForegroundActivator.TryActivateNatively` (`ForegroundActivator.cs:81`) attaches the calling thread to the **target** window's thread (`NativeMethods.cs:54-60`). But `SetForegroundWindow` permission is inherited from the **foreground** queue. When a third process holds the foreground, the target window's thread has no foreground rights to share, so the attach grants nothing, which is exactly the scenario in the open BUGS.md entry ("Chrome window activation fails when a non-Chrome process holds the foreground"). The lock-timeout suppression covers the timeout-based denial rule but not the others (foreground queue locked by recent input, active menu).

Fix: additionally attach to the foreground window's thread (`GetWindowThreadProcessId(GetForegroundWindow(), _)`) for the duration, detaching in reverse order, while keeping the existing target attach. This is a hypothesis grounded in Win32 semantics, not executed by the reviewers; it fits the interactive re-verification already queued.

Relatedly, the attach result is discarded silently (`ForegroundActivator.cs:81,94`), so the per-rung logging added to diagnose that bug leaves no trace of the most failure-prone step. Worth adding before the re-verification session.

## Important

1. **Log lines have no timestamps except on early-exit paths.** `Program.cs:71-76` writes a manual `Timestamp:` line in `LogEarlyExit`, but the success path (the `chrome -> CTRL-W` line, all per-rung activation lines) and the entire daemon log are undated. This directly undermines the Chrome-bug investigation: undated rung entries cannot be correlated with interactive attempts. Fix centrally in `Logger.Log` and delete the manual line; that also removes the app/daemon inconsistency in one move.

2. **Three tests leak one GUID-named log file each to %TEMP% per run.** This is the exact mechanism behind the tracked test-hygiene item; each write path verified:
   - `CrashRepairTests.cs:7` static `Logger`, written by `TryRepairCrashedState_RestoreFails_KeepsRecordForNextTick` (production logs at `CrashRepair.cs:35`)
   - `ForegroundLockSuppressionTests.cs:7` static `Logger`, written by `Dispose_RestoreFails_KeepsRecordPending` (production logs at `ForegroundLockSuppression.cs:63`)
   - `WindowCloserTests.cs:17` per-test logger, written by the `NO-SUCH-METHOD` fallback case (`WindowCloser.cs:97`)

   Fix: a small `TempLogger : IDisposable` helper next to `TestNames`, following the cleanup pattern `LoggerTests` already uses (delete `<name>.log` and `.log.old` in `Dispose`).

3. **The publish-record-before-disable ordering in `ForegroundLockSuppression.cs:42-43` is untested.** The crash-repair design depends on the repair record existing *before* the system value is mutated, but swapping `SetTimeoutRepair` and `disable()` passes the current suite, because every test asserts only end state (identical under either order). Fix: capture `state.TryReadTimeoutRepair(...)` inside the injected `disable` delegate and assert the record was already pending with the right value at disable time. For contrast, the restore-before-clear ordering in `TimeoutRepair` *is* pinned indirectly by `RestoreAndClear_RestoreFails_KeepsRecordPending`.

4. **`Logger` rotation only runs at construction** (`RotateIfTooLarge` is called only from the constructor, `Logger.cs:13`). The daemon constructs its logger once and can run for months, so once its log passes 1 MB nothing rotates it until a daemon restart; growth is unbounded during a run. Move the size check into `Log` (e.g. check `stream.Length` after opening, rotate on the next call), keeping the swallow-on-failure semantics.

5. **`WindowCloser` hard-wires `ForegroundActivator` and `InputSimulator`** (`WindowCloser.cs:28-29`). The shared library went to deliberate lengths to make every side-effecting piece injectable, and then the one class orchestrating them news up concrete dependencies; that is why `WindowCloserTests` can only reach `ResolveKillMethodName` and none of the dispatch/activation flow. Fix: constructor parameters with real-implementation defaults, mirroring the `ForegroundLockSuppression` pattern.

## Minor

6. **App launch during a daemon repair tick is misclassified.** Interleaving: app died mid-suppression leaving a pending record; the watchdog acquires `TheCloserGuardMutex` to repair; the user presses the hotkey inside that window; the app sees `createdNew == false` and exits logging "The previous instance is still running" (`Program.cs:24-31` vs `CrashRepair.cs:16`). One silently dropped hotkey press and a misleading log line. The window is microseconds wide and only exists while a record is pending. If worth fixing: retry once after a short delay on `!createdNew`, or mention the daemon-repair possibility in the log line.

7. **`DaemonProcessExists` is a name probe** (`Program.cs:112-122`). Any process named `TheCloser.Daemon` counts, including a `--stop` stopper (as deploy.ps1 spawns) or a losing second daemon in its exit path. The app then skips spawning, burns the full 1s pin wait, and proceeds unpinned, silently entering the documented daemon-dead residual-risk state for that run even though spawning was possible. Self-heals next run. More truthful: check `Mutex.TryOpenExisting(DaemonMutexName)` first (the actual pin signal) and use the name probe only to avoid double-spawning.

8. **`SharedState` ordering relies on unfenced program order** (`SharedState.cs:28-47`). The value-then-flag store order is enforced only by source order of plain accessor writes; the .NET memory model formally permits reordering of non-volatile stores. Safe today (win-x64 RID, TSO hardware, opaque SafeBuffer calls), but the documented invariant is not language-guaranteed. Cheap hardening: `Thread.MemoryBarrier()` between the two stores in `SetTimeoutRepair` and the two loads in `TryReadTimeoutRepair`; also survives a future ARM64 RID.

9. **`TryMoveCursor` ignores both P/Invoke returns** (`ForegroundActivator.cs:154-158`): a failed `SetCursorPos` just burns 5 retries, and an ignored `GetCursorPos` failure compares against a zeroed struct. The same file already uses the checked `TryGetMouseCursorPosition` wrapper at line 105; use it here and bail early on a failed set.

10. **Test coverage gaps** (most valuable first):
    - `SharedState` offset independence: no test writes both the throttle tick and the repair record, so an off-by-4 in `RepairFlagOffset` overlapping the 8-byte tick would pass silently. Set a record, `WriteThrottleTick(long.MaxValue)`, assert the record intact; and the reverse.
    - Logger rotation at exactly 1 MiB: production rotates only on strictly-greater (`Logger.cs:37`); tests cover 16 bytes and threshold+1 but not the boundary, which must NOT rotate.
    - `ResolveKillMethodName("")`: empty string takes a different path than `null` (`?? DefaultKillMethod` does not apply; it falls to `ContainsKey("")`). Also six of the nine documented method names are untested; a theory over the full documented list pins the dictionary against typos and README drift.
    - `ProcessSettingsParser`: numeric ClickPosition `"1"` silently parses to `Center` (the `Enum.IsDefined` guard only rejects out-of-range numerics); precedence when both simple value and `Method` key exist is unpinned; invalid ClickPosition with null `logWarning` (cheap NRE guard).

11. **Project and code hygiene:**
    - `deploy.ps1:5` publishes the whole solution including the test project; `<IsPublishable>false</IsPublishable>` in the test csproj or publishing the two exe projects explicitly fixes it.
    - Dead x64/x86 solution platforms all mapping to AnyCPU (`TheCloser.sln:27-31`); noise, trivial cleanup.
    - Daemon exits silently when launched with no arguments (`TheCloser.Daemon\Program.cs:16-19`) while an unknown argument gets a log line; double-clicking the exe gives no feedback. Fold into the same log path.
    - `SendKeyPressIfForeground` (`WindowCloser.cs:110`) is misnamed: it actively drives the full activation ladder and then injects; something like `ActivateAndSendKeyPress` matches what it does.
    - `SignalExit` manually calls `Set()` then `Dispose()` (`TheCloser.Daemon\Program.cs:82-86`); a `using var` on the `TryOpenExisting` out variable is the idiomatic, leak-proof shape.
    - Inconsistent sealing: `SharedState` and `ForegroundLockSuppression` are `sealed`; `Logger`, `WindowCloser`, `ForegroundActivator` are not, and none is designed for inheritance.
    - `Program.AssemblyName` (`TheCloser\Program.cs:18`) is a public reflection-based one-off used exactly once, asymmetric with the daemon's plain constant in `Constants.cs:6`; replace with a constant.
    - `NativeMethods`: `GetCurrentThreadId`, `GetAncestor`, `GetWindowThreadProcessId` are `public` but consumed only inside the class; `INPUT.Size` recomputes `Marshal.SizeOf<INPUT>()` on every access (make it `static readonly`).
    - `LoggerTests.cs:7` re-declares the `1024 * 1024` threshold because `Logger.MaxLogSizeBytes` is private; making it `internal` (plus `InternalsVisibleTo` on TheCloser.Shared) removes the duplication.
    - No analyzer enforcement anywhere: no StyleCop.Analyzers package, no `TreatWarningsAsErrors`/`EnforceCodeStyleInBuild` in `Directory.Build.props`, and the `.editorconfig` carries only two C# rules. The code complies with conventions by discipline only; adding enforcement locks it in.
    - The app-to-daemon `ProjectReference` (`TheCloser.csproj:14-15`, kept for the exe copy) is a full assembly reference, so `TheCloser.Daemon.Program` (public) is callable from app code; making the daemon's `Program` internal closes that door at zero cost.

## Strengths

The reviewers independently converged on the same picture: the protocol decomposition (`TimeoutRepair`/`CrashRepair`/`ForegroundLockSuppression`) with injectable delegate seams is exemplary for a utility this size; comments document *why* (invariants) rather than *what*; the test suite asserts real observable state (MMF contents, files on disk) with airtight GUID-based isolation and zero mock-echo tests, and the tricky suppression state machine is thoroughly pinned including the subtle "pending record's saved value wins" case; P/Invoke details are correct including `SPI_SETFOREGROUNDLOCKTIMEOUT`'s pvParam-as-value convention and the INPUT union layout; `PostMessage`-not-`SendMessage` avoids hanging on unresponsive targets; the daemon's pin-before-mutex-publish ordering paired with the app polling the daemon *mutex* rather than the MMF (the app's own handle would make an MMF probe self-satisfying) is exactly right; and README/CLAUDE.md match the code.

## Suggested disposition

- Bundle the AttachThreadInput fix + attach logging + Logger timestamps ahead of the Chrome-bug re-verification (headline finding + items 1 and part of 9's logging spirit).
- Use the test-hygiene fixes (items 2 and 10) to close the tracked %TEMP% backlog entry.
- Track the rest (items 3-9, 11) as quick wins.
