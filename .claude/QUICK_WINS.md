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

`RotateIfTooLarge` is called only from the `Logger` constructor. The daemon constructs its logger once and can run for months, so once its log passes 1 MB nothing rotates it until a daemon restart; growth is unbounded during a run. Daemon IPC also moved the permanent per-press latency instrument into this log, increasing the growth rate and making the item a strongly preferred follow-up without blocking activation correctness. Move the size check into the queued write path (e.g. check `stream.Length` after opening, rotate on the next write), keeping the swallow-on-failure semantics.

### SharedState ordering relies on unfenced program order

The value-then-flag discipline in `SetTimeoutRepair` / `TryReadTimeoutRepair` is enforced only by source order of plain accessor calls; the activation payload region added the same language-level assumption across its QPC and button fields before the event signal. The .NET memory model formally permits reordering of non-volatile stores. Safe today (win-x64 RID, TSO hardware, opaque SafeBuffer calls plus the kernel event boundary), but the documented invariants are not language-guaranteed. Cheap hardening: add explicit memory barriers to both the repair-record and activation-payload accessors; this also survives a future ARM64 RID.

### DaemonProcessExists is a name probe

Any process named `TheCloser.Daemon` counts, including a manually launched `--stop` process or a losing second daemon in its exit path. The app then skips spawning, burns the full 1s pin wait, and proceeds unpinned, silently entering the documented daemon-dead residual-risk state for that run even though spawning was possible (self-heals next run). More truthful: check `Mutex.TryOpenExisting(Constants.DaemonMutexName)` first (the actual pin signal) and use the name probe only to avoid double-spawning. The AHK auto-execute path now signals the exit event in-process, so it no longer creates this false-positive shape itself.

### TryMoveCursor ignores both P/Invoke returns

In `ForegroundActivator.TryMoveCursor`, both `_native.SetCursorPosition` and `_native.TryGetCursorPosition` returns are ignored: a failed set just burns 5 retries, and a failed position read compares against a zeroed struct. The comment above the method marks the deferral. Fix: bail early on a failed set and treat a failed read as a failed attempt. The `INativeWindowApi` seam makes both paths unit-testable (`FakeNativeApi` in `ForegroundActivatorTests` already has a `CursorPositionAvailable` toggle).

### Deploy still stops the daemon with a hard kill

The daemon now runs a final repair tick on graceful shutdown, so the in-process half of this item landed with daemon IPC activation. `deploy.ps1` still uses `Stop-Process`, including its elevated retry, instead of the graceful path. Finish the remaining half by invoking the deploy target's `TheCloser.Daemon.exe --stop`, waiting a short bounded interval for the daemon to exit, and falling back to `Stop-Process` only if it does not. The graceful signal must run through the elevated child because an unelevated process cannot open the elevated daemon's exit event. The final repair tick is safe when a close is active: it proceeds only when `TheCloserGuardMutex` is acquirable, otherwise the close owns restoration.

### App launch during a daemon repair tick is misclassified

Interleaving: a standalone or fallback app starts while the daemon owns `TheCloserGuardMutex`; the app sees `createdNew == false` and exits logging "The previous instance is still running". The window was originally limited to a microsecond-scale repair tick, but daemon IPC now holds the guard for the complete in-process close, so mixed-elevation fallback and manual app launches can hit the wider close interval. Normal elevated AHK presses signal the daemon and do not launch the app. If worth fixing: retry once after a short delay on `!createdNew`, or make the log name both a running close and repair as possible owners.

## Testability

### Dangerous real-implementation defaults in constructor seams

The injectable seams ship with real defaults: `ForegroundActivator` defaults `suppressionFactory` to constructing a real `ForegroundLockSuppression` (whose constructor mutates the system-wide foreground lock timeout), `WindowCloser` defaults to a real activator and keystroke sender, and `TriggerButtonHealer` defaults to real input injection. A test that forgets one injection silently touches real system state instead of failing to compile. Preferred shape: make the dependencies required (no defaults) and compose imperatively in both production roots, `TheCloser.Program.Main` for fallback and `TheCloser.Daemon.Program` for daemon-hosted closes; test helpers already build full fakes. Anti-goal: an IoC container. These collaborators do not justify one, and Native AOT penalizes reflection-based containers. When this lands, rewrite the CLAUDE.md test-safety bullet (the suppression-factory warning becomes moot once injection is required).

### Publish-record-before-disable ordering in ForegroundLockSuppression is untested

The crash-repair design depends on the repair record existing *before* the system value is mutated, but swapping `SetTimeoutRepair` and `disable()` passes the current suite, because every test asserts only end state (identical under either order). Fix: capture `state.TryReadTimeoutRepair(...)` inside the injected `disable` delegate and assert the record was already pending with the right value at disable time. For contrast, the restore-before-clear ordering in `TimeoutRepair` *is* pinned indirectly by `RestoreAndClear_RestoreFails_KeepsRecordPending`.

## Hygiene

### Unify project guidance behind one canonical file

The tracked `CLAUDE.md` and ignored root `AGENTS.md` adapter duplicate project commands, architecture, backlog conventions, and implementation rules, so one can drift while the other remains stale. Merge the shared content into one tracked canonical project-guidance file and reduce each host-specific file to the smallest adapter its host requires. Preserve host discovery behavior and verify both Claude Code and Codex load the canonical rules before removing duplicated prose.

### Scrub personal information from all git history

The current tips are clean (machine-local paths moved to the git-ignored `deploy.settings.psd1` in the commit titled "refactor(setup): move personal paths to git-ignored psd1 settings"), but the *history* still carries personal information in old blobs: the personal deploy target path lived hardcoded in `deploy.ps1` from its inception until that refactor, appeared in `CLAUDE.md` until the follow-up docs commit, briefly in `TheCloser.ahk` and `install-elevated-ahk.ps1` between their introduction and the same refactor, and in the (since-deleted) implementation plan for the target-rung removal. Do not trust this inventory as complete: start by grepping every revision, e.g. `git grep -I <needle> $(git rev-list --all)`, using the deploy target string from the local `deploy.settings.psd1` as the needle, then broaden to other candidate needles (user name fragments, machine-specific folders) and inventory what surfaces.

Preferred shape: `git filter-repo --replace-text` mapping each personal string to the placeholder already used in `deploy.settings.example.psd1` (`C:\Path\To\Bin\TheCloser`), plus `--replace-message` if any commit message turns out to carry a needle. The stated requirement is that **each rewritten commit must still make sense in isolation**: after replacement, historical `deploy.ps1` reads as a placeholder hardcode that the psd1 refactor then externalizes, which is coherent; spot-check the refactor commit and the ahk/installer introduction commit afterwards to confirm.

Decision points to settle with the user before running: whether commit author/committer identity is in scope (currently assumed NOT; it is deliberate); the exact needle list. Operational notes: filter-repo rewrites every SHA, so this is gated on user coordination: a user-driven force-push plus re-clone/reset on every machine with a checkout, at a moment when no other work is in flight. Re-verify afterwards with the same all-revisions grep returning empty. Side effect to flag when done: every commit SHA referenced in the `.claude` docs (BUGS_HISTORY entries, this file's history) goes stale; sweep and update them as part of the same change, as was done once before for the same reason (see the "update rebased commit SHAs" commit).

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
- `NativeMethods`: `GetCurrentThreadId` and `GetAncestor` are `public` but consumed only inside the class (`GetWindowThreadProcessId` gained an external consumer in `NativeWindowApi`); `INPUT.Size` recomputes `Marshal.SizeOf<INPUT>()` on every access (make it `static readonly`).
- `LoggerTests` re-declares the `1024 * 1024` threshold because `Logger.MaxLogSizeBytes` is private; making it `internal` (plus `InternalsVisibleTo` on TheCloser.Shared) removes the duplication.
- The app-to-daemon `ProjectReference` (kept for the exe copy) is a full assembly reference, so the daemon's `Program` (public) is callable from app code; making it internal closes that door at zero cost.

## History

Implemented quick wins are archived in
[`QUICK_WINS_HISTORY.md`](QUICK_WINS_HISTORY.md), read only when
consulted (not at session start) so the active backlog above stays
scannable. When a quick win lands, append its entry there rather
than to this file.
