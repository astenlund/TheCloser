# Quick wins

Refactors ready to land when time allows; not blocking any feature, but
would improve the codebase meaningfully.

This file is **one of four repo-local indexes** Claude reads on every
session start (alongside `FEATURES.md`, `BUGS.md`, `PATTERNS.md`). Active
entries are kept inline, organized under thematic `##` sections you
invent as work emerges. When a quick win lands, append a shipped-note
entry to [`QUICK_WINS_HISTORY.md`](QUICK_WINS_HISTORY.md); do not move
it within this file. Negative-knowledge findings (approaches attempted
and reverted) are first-class promotion candidates from the history
into the relevant `.claude/patterns/<slug>.md` Cautionary tales sections.

Capture shorthand: name the refactor, describe the current smell in a
sentence or two, sketch the preferred shape. A reader should be able to
start work from the entry alone. Anchor entries on identifiers that
survive refactors -- symbol names, entry titles, commit hashes, config
keys -- never on line numbers, plan-phase ordinals, bullet positions,
or temporal qualifiers ("new", "recent"): a precise locator that rots
misleads harder than a coarse one that holds.

All entries below come from the 2026-07-11 full-solution review
(`reviews/2026-07-11-full-solution-review.md`); each was verified
against the source by the reviewers before inclusion.

## Robustness and hardening

### Logger rotation only runs at construction

`RotateIfTooLarge` is called only from the `Logger` constructor. The daemon constructs its logger once and can run for months, so once its log passes 1 MB nothing rotates it until a daemon restart; growth is unbounded during a run. Move the size check into `Log` (e.g. check `stream.Length` after opening, rotate on the next call), keeping the swallow-on-failure semantics.

### SharedState ordering relies on unfenced program order

The value-then-flag discipline in `SetTimeoutRepair` / `TryReadTimeoutRepair` is enforced only by source order of plain accessor calls; the .NET memory model formally permits reordering of non-volatile stores. Safe today (win-x64 RID, TSO hardware, opaque SafeBuffer calls), but the documented invariant is not language-guaranteed. Cheap hardening: `Thread.MemoryBarrier()` between the two stores in `SetTimeoutRepair` and the two loads in `TryReadTimeoutRepair`; also survives a future ARM64 RID.

### DaemonProcessExists is a name probe

Any process named `TheCloser.Daemon` counts, including a `--stop` stopper (as deploy.ps1 spawns) or a losing second daemon in its exit path. The app then skips spawning, burns the full 1s pin wait, and proceeds unpinned, silently entering the documented daemon-dead residual-risk state for that run even though spawning was possible (self-heals next run). More truthful: check `Mutex.TryOpenExisting(Constants.DaemonMutexName)` first (the actual pin signal) and use the name probe only to avoid double-spawning.

### TryMoveCursor ignores both P/Invoke returns

In `ForegroundActivator.TryMoveCursor`, a failed `SetCursorPos` just burns 5 retries, and an ignored `GetCursorPos` failure compares against a zeroed struct. The same file already uses the checked `TryGetMouseCursorPosition` wrapper elsewhere; use it here and bail early on a failed set.

### App launch during a daemon repair tick is misclassified

Interleaving: app died mid-suppression leaving a pending record; the watchdog acquires `TheCloserGuardMutex` to repair; the user presses the hotkey inside that window; the app sees `createdNew == false` and exits logging "The previous instance is still running". One silently dropped hotkey press and a misleading log line. The window is microseconds wide and only exists while a record is pending. If worth fixing: retry once after a short delay on `!createdNew`, or mention the daemon-repair possibility in the log line.

## Testability

### Publish-record-before-disable ordering in ForegroundLockSuppression is untested

The crash-repair design depends on the repair record existing *before* the system value is mutated, but swapping `SetTimeoutRepair` and `disable()` passes the current suite, because every test asserts only end state (identical under either order). Fix: capture `state.TryReadTimeoutRepair(...)` inside the injected `disable` delegate and assert the record was already pending with the right value at disable time. For contrast, the restore-before-clear ordering in `TimeoutRepair` *is* pinned indirectly by `RestoreAndClear_RestoreFails_KeepsRecordPending`.

### WindowCloser hard-wires ForegroundActivator and InputSimulator

The shared library went to deliberate lengths to make every side-effecting piece injectable, and then the one class orchestrating them news up concrete dependencies in its constructor; that is why `WindowCloserTests` can only reach `ResolveKillMethodName` and none of the dispatch/activation flow. Fix: constructor parameters with real-implementation defaults, mirroring the `ForegroundLockSuppression` pattern.

## Hygiene

### Analyzer enforcement

No StyleCop.Analyzers package, no `TreatWarningsAsErrors`/`EnforceCodeStyleInBuild` in a `Directory.Build.props`, and the `.editorconfig` carries only two C# rules. The code complies with conventions by discipline only; adding enforcement locks it in.

### Small-item hygiene sweep

One-line items, each independently landable:

- deploy.ps1 publishes the whole solution including the test project; `<IsPublishable>false</IsPublishable>` in the test csproj, or publish the two exe projects explicitly.
- Dead x64/x86 solution platforms in `TheCloser.sln`, all mapping to AnyCPU; noise, trivial cleanup.
- Daemon exits silently when launched with no arguments while an unknown argument gets a log line; double-clicking the exe gives no feedback. Fold into the same log path.
- `WindowCloser.SendKeyPressIfForeground` is misnamed: it actively drives the full activation ladder and then injects; something like `ActivateAndSendKeyPress` matches what it does.
- The daemon's `SignalExit` manually calls `Set()` then `Dispose()`; a `using var` on the `TryOpenExisting` out variable is the idiomatic, leak-proof shape.
- Inconsistent sealing: `SharedState` and `ForegroundLockSuppression` are `sealed`; `Logger`, `WindowCloser`, `ForegroundActivator` are not, and none is designed for inheritance.
- `Program.AssemblyName` (main app) is a public reflection-based one-off used exactly once, asymmetric with the daemon's plain constant in `Constants.cs`; replace with a constant.
- `NativeMethods`: `GetCurrentThreadId`, `GetAncestor`, `GetWindowThreadProcessId` are `public` but consumed only inside the class; `INPUT.Size` recomputes `Marshal.SizeOf<INPUT>()` on every access (make it `static readonly`).
- `LoggerTests` re-declares the `1024 * 1024` threshold because `Logger.MaxLogSizeBytes` is private; making it `internal` (plus `InternalsVisibleTo` on TheCloser.Shared) removes the duplication.
- The app-to-daemon `ProjectReference` (kept for the exe copy) is a full assembly reference, so the daemon's `Program` (public) is callable from app code; making it internal closes that door at zero cost.

## History

Implemented quick wins are archived in
[`QUICK_WINS_HISTORY.md`](QUICK_WINS_HISTORY.md), read only when
consulted (not at session start) so the active backlog above stays
scannable. When a quick win lands, append its entry there rather
than to this file.
