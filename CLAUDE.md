# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

### Build and Publish
```bash
# Build the solution
dotnet build

# Publish for deployment (Release mode, Native AOT)
pwsh ./deploy.ps1
```

The deploy script stops the daemon, builds in Release mode, and copies the executables and invocation-layer files (`TheCloser.ahk`, `install-elevated-ahk.ps1`) to the deploy target configured in `deploy.settings.psd1` (git-ignored, machine-local; see `deploy.settings.example.psd1`).

If Native AOT reports cross-OS compilation from a Codex Windows shell even though the host and target RID are `win-x64`, the filtered shell omitted `OS`; run the deploy with `$env:OS = 'Windows_NT'` for that process.

### Running the Application
```bash
# Run the main application (closes window under cursor)
dotnet run --project TheCloser/TheCloser.csproj

# Start the daemon
dotnet run --project TheCloser.Daemon/TheCloser.Daemon.csproj -- --start

# Stop the daemon
dotnet run --project TheCloser.Daemon/TheCloser.Daemon.csproj -- --stop
```

### Tests
```bash
# Build first, then run the test project (never the full unfiltered suite)
dotnet build --no-incremental
dotnet test TheCloser.Tests --no-build
```

### AutoHotkey syntax validation
```powershell
$script = (Resolve-Path ./TheCloser.ahk).Path
$ahk = Start-Process -FilePath 'C:/Program Files/AutoHotkey/AutoHotkeyU64.exe' -ArgumentList '/iLib', 'NUL', '/ErrorStdOut', ('"{0}"' -f $script) -Wait -PassThru
if ($ahk.ExitCode -ne 0) {
    throw "AutoHotkey syntax validation failed with exit code $($ahk.ExitCode)."
}
```

Run this parse-only AutoHotkey v1 check from PowerShell, not Git Bash: MSYS rewrites slash-prefixed switches, and PowerShell's call operator does not reliably wait for this GUI-subsystem executable or update `$LASTEXITCODE`. Exit code 0 confirms syntax only; runtime `DllCall` type strings remain unchecked.

## Architecture

TheCloser is a Windows utility that closes windows/tabs under the mouse cursor. It consists of three projects plus a test project. All kernel object names are session-local (no `Global\` prefix) and centralized in `TheCloser.Shared/Constants.cs`; `TheCloser.ahk` carries the one deliberate hand-synchronized copy because AutoHotkey cannot consume the C# constants.

### TheCloser (Standalone Fallback Application)
- Fully functional fallback entry point used when AutoHotkey cannot open the daemon activation event
- Uses mutex `TheCloserGuardMutex` to ensure single instance; holds it for the whole run
- Implements 200ms throttling via a monotonic tick count in the shared memory-mapped file
- On startup, restores a pending foreground-lock-timeout repair record before doing anything else
- Automatically starts daemon if not running, then waits (50ms x 20 attempts) for the daemon to pin the memory-mapped file
- Key file: `Program.cs`; the close pipeline lives in `TheCloser.Shared`

### TheCloser.Daemon (Background Service)
- Runs continuously in background, pins `TheCloserSharedState`, and hosts the normal close pipeline in-process
- Waits on `TheCloserActivationEvent` and `TheCloserDaemonExitEvent` under single-instance mutex `TheCloserDaemonMutex`; it publishes both events before the mutex so an observable daemon is ready for activation and stop signals
- On activation, consumes the press payload, logs press-to-handler latency, applies the shared 200ms throttle (dated from the press timestamp on both sides, so a double activation queued behind a stalled close is still skipped and a genuine press shortly after a late handling is not) and guard mutex, snapshots the current configuration, closes the window under the cursor, and dispatches the stuck-button healer when required. Stall attribution: a handling at or above `StallLogThresholdMs` (300ms; a normal close measures 110 to 133 ms) logs its settings/close phase breakdown, a keystroke injection at or above it logs its duration plus the current foreground process, a deferred press logs how long it queued behind the previous handling, and startup logs the `LowLevelHooksTimeout` registry value (SendInput waits on every low-level keyboard hook, so an unresponsive hook owner such as PowerToys Keyboard Manager stalls injection)
- Hot-reloads `appsettings.json`; malformed, exclusively locked, or delete-and-replace transitions keep dispatching through the last good value snapshot
- Every 5s, if a foreground-lock-timeout repair record is pending and `TheCloserGuardMutex` can be acquired, restores the saved timeout while holding the mutex; activation and watchdog exceptions are isolated
- Graceful shutdown runs a final repair tick and drains healer tasks before releasing its kernel objects and disposing configuration and logging

### TheCloser.Shared (Common Library)
- `Constants.cs`: kernel object names and IPC constants
- `SharedState.cs`: memory-mapped file accessor (throttle tick at offset 0; repair flag at offset 8; saved timeout at offset 12; activation QPC at offset 16; trigger button at offset 24). Repair writes commit the saved value before the flag; activation writes commit the payload before signaling the event. Both activation fields are consumed and zeroed together
- `DaemonRuntime.cs` / `ActivationHandler.cs`: daemon lifetime and per-activation orchestration, including startup publication order, watchdog and activation dispatch, final repair, healer drain, latency attribution, throttle, guard mutex, and exception isolation
- `DaemonConfiguration.cs` / `LastGoodConfiguration.cs`: watched configuration root and per-activation value snapshot retained across transient reload failures
- `WindowCloser.cs` / `ForegroundActivator.cs` / `TriggerButtonHealer.cs`: close dispatch, foreground activation ladder, and stuck-button recovery shared by the daemon and standalone fallback
- `ForegroundLockTimeout.cs`: the SystemParametersInfo get/disable/restore wrapper
- `TimeoutRepair.cs` / `CrashRepair.cs` / `ForegroundLockSuppression.cs`: the crash-repair protocol pieces (restore-then-clear with clear-only-on-success; the daemon's acquire-and-repair; the close pipeline's disable/restore scope around SetForegroundWindow), each unit-testable via injectable restore/tryGet/disable delegates
- `LowLevelHooksTimeoutProbe.cs`: read-only startup diagnostic that describes the `LowLevelHooksTimeout` registry value (or its absence) and never throws; injectable reader for tests
- `Logger.cs`: writes to `%TEMP%\TheCloser*.log`; every non-empty line gets a UTC round-trip timestamp prefix (empty lines are unprefixed separators; clock injectable via optional constructor delegate); contention-tolerant and never throws. Rotation to `.log.old` above 1 MB is checked only at logger construction; moving that check into the long-lived write path remains tracked in `.claude/QUICK_WINS.md`

### TheCloser.Tests
- xUnit tests cover the shared-memory protocol, activation handler, daemon runtime, watched and last-good configuration, repair protocol, close dispatch, foreground activation ladder, stuck-button healer, hook-timeout probe, and logging
- Kernel objects and log files use unique GUID-suffixed names per test (via the `TestNames` helper), so tests never collide with a live daemon or each other; the repair-protocol tests inject tryGet/disable/restore delegates, activator tests inject the suppression factory (constructing a real `ForegroundLockSuppression` mutates the system-wide foreground lock timeout), and no test ever touches the real SystemParametersInfo setting

## Window Closing Methods

The application supports multiple methods configured per-process in `appsettings.json`, which is read from the deployed executable's directory and maintained by hand there (the repository carries no appsettings.json; see the README for examples). Method and ClickPosition values are parsed case-insensitively; unknown values are logged and fall back to defaults (see `ProcessSettingsParser` for the full method set; default method: CTRL-W).

## Tracking

Known bugs, quick wins, feature ideas, and design patterns are tracked in the `.claude/` indexes; see `## Backlogs and indexes` below.

## Key Implementation Details

1. **Foreground Window Handling**: Multiple strategies including SetForegroundWindow, AttachThreadInput, and clicking on the title bar as fallback are implemented by `ForegroundActivator`. The system-wide foreground lock timeout is disabled around SetForegroundWindow and restored afterwards. The repair record plus the daemon watchdog, final repair tick, and fallback app startup repair heal interrupted operations. Because AttachThreadInput can strand the mouse button's in-flight release, any close that performed an attach releases the guard mutex before dispatching `TriggerButtonHealer`; the standalone app awaits it directly, while the daemon tracks it as a bounded task and drains it during shutdown. See the stuck-XBUTTON2 entry in `.claude/BUGS_HISTORY.md` for the incident and manual recovery command
2. **Invocation IPC**: `TheCloser.ahk` opens and closes the activation event and shared-memory handles on every press. Never cache them: an AutoHotkey-held handle would pin a stale kernel object after daemon death and break fallback detection or daemon restart. The QPC and button-code constants in the script must stay synchronized with `Constants.cs` and `SharedState.cs`
3. **Accepted residual risk**: if the process that owns the last shared-memory handle is killed during foreground-lock suppression, the repair record disappears and the timeout can remain 0 until reboot. This includes a fallback-app crash while no daemon pins the map and a daemon kill during an in-process close. Killing the daemon can also cut off the healer. Gating suppression on a confirmed daemon pin and adding a separate healer backstop are explicit anti-goals

## Backlogs and indexes

Four repo-local indexes live under `.claude/`. A `SessionStart` hook in `.claude/settings.json` injects a directive so Claude reads them on the first turn of every session; any task the user raises may already be queued, designed, diagnosed, or covered by an existing pattern:

- `.claude/QUICK_WINS.md`: refactors ready to land when time allows. Shipped entries are appended to `.claude/QUICK_WINS_HISTORY.md` (described below).
- `.claude/FEATURES.md`: product-level feature ideas, with one file per feature under `.claude/features/`. Shipped entries are appended to `.claude/FEATURES_HISTORY.md` (described below). When sibling feature files start duplicating shared concerns (machinery, patterns, conventions), promote an umbrella file that hosts the shared content and trim the siblings to deltas; cross-references through an umbrella scale better than pairwise cross-references.
- `.claude/BUGS.md`: known bugs awaiting fix, with one file per bug under `.claude/bugs/` when more than a few lines of description is needed. Fixed entries are appended to `.claude/BUGS_HISTORY.md` (described below).
- `.claude/PATTERNS.md`: cross-cutting design patterns that span multiple features, with one file per pattern under `.claude/patterns/`. Complementary to the umbrella-promotion heuristic above: umbrellas cluster children of one family; patterns cluster concerns that span families. A pattern graduates here when the same structure would otherwise be re-described in two or more feature files.

Four locations sit alongside the indexes that are not read at session start; consult them when relevant work is in flight:

- `.claude/plans/<date>-<slug>.md`: implementation plans produced by the writing-plans workflow. **Ephemeral**: a plan exists while the implementation is in flight and is deleted once the work lands. The code, tests, and commits are the durable record. Plans are purely mechanical step-by-step instructions for the agent doing the work. There is no "implemented plans" archive.
- `.claude/QUICK_WINS_HISTORY.md`: archive of shipped quick wins, split out from `QUICK_WINS.md` so the active backlog stays scannable on session start. Append entries here as soon as the quick win lands; the file itself is consulted only when something pulls it in (a pattern-doc cross-reference, an archaeological lookup, a negative-knowledge sweep). Negative-knowledge entries (approaches attempted and reverted) are first-class promotion candidates into the relevant `.claude/patterns/<slug>.md` Cautionary tales sections.
- `.claude/FEATURES_HISTORY.md`: archive of shipped features and shipped slices, split out from `FEATURES.md` so the active backlog stays scannable on session start. Append entries here as soon as a feature or slice lands.
- `.claude/BUGS_HISTORY.md`: archive of fixed bugs, split out from `BUGS.md`. Append entries here as soon as a bug is fixed.

**Walk-and-remove convention.** When a feature, slice, quick win, or bug-fix ships, the same change set that appends its entry to the relevant history archive ALSO walks every other `**Requires:**` line in `FEATURES.md` / `BUGS.md` and drops references to the just-shipped item; if the dropped reference was the only one on the line, the line becomes `Requires: none.`. Active `Requires:` lines therefore describe what is *currently* blocking, and `/nightshift:ready` never has to consult the history archives to resolve dependencies — the dependency graph settles as work ships.

Brainstorming output lives in feature files (or in patterns when cross-cutting / in bugs when diagnostic) rather than as separate dated specs. Pre-feature exploratory brainstorms land as draft features with `status: exploring` frontmatter and an entry in `FEATURES.md`'s `## Exploring` section; `/nightshift:ready` skips them. They graduate to a themed `##` section with a `**Requires:**` line once the design firms up.

The `/nightshift:ready` command parses each entry's `**Requires:**` line in `FEATURES.md` and `BUGS.md` and reports the unblocked work set. Run it when picking what to work on next.
