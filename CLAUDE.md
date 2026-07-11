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

The deploy script stops the daemon, builds in Release mode, and copies executables to `C:\Sync\Personal\3. Resources\Bin\TheCloser\`.

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

## Architecture

TheCloser is a Windows utility that closes windows/tabs under the mouse cursor. It consists of three projects plus a test project. All kernel object names (mutexes, event, memory-mapped file) are session-local (no `Global\` prefix) and centralized in `TheCloser.Shared/Constants.cs`.

### TheCloser (Main Application)
- Entry point that executes window closing operations
- Uses mutex `TheCloserGuardMutex` to ensure single instance; holds it for the whole run
- Implements 200ms throttling via a monotonic tick count in the shared memory-mapped file
- On startup, restores a pending foreground-lock-timeout repair record before doing anything else
- Automatically starts daemon if not running, then waits (50ms x 20 attempts) for the daemon to pin the memory-mapped file
- Key files: `Program.cs`, `WindowCloser.cs` (kill-method resolution and dispatch), `ForegroundActivator.cs` (activation ladder), `ProcessSettingsParser.cs`, `NativeMethods.cs`

### TheCloser.Daemon (Background Service)
- Runs continuously in background, pinning the memory-mapped file `TheCloserSharedState` (named MMFs vanish when the last handle closes)
- Uses mutex `TheCloserDaemonMutex` for single instance; exit is signaled via event `TheCloserDaemonExitEvent`
- Watchdog: every 5s, if a foreground-lock-timeout repair record is pending and `TheCloserGuardMutex` can be acquired (the app died mid-operation), restores the saved timeout while holding the mutex; each iteration is exception-isolated so transient failures never kill the daemon

### TheCloser.Shared (Common Library)
- `Constants.cs`: kernel object names and IPC constants
- `SharedState.cs`: memory-mapped file accessor (throttle tick at offset 0; repair flag at offset 8; saved timeout at offset 12). Write discipline: the saved value is committed before the flag, the reader checks the flag before the value, and clearing touches only the flag
- `ForegroundLockTimeout.cs`: the SystemParametersInfo get/disable/restore wrapper
- `TimeoutRepair.cs` / `CrashRepair.cs` / `ForegroundLockSuppression.cs`: the crash-repair protocol pieces (restore-then-clear with clear-only-on-success; the daemon's acquire-and-repair; the app's disable/restore scope around SetForegroundWindow), each unit-testable via injectable restore/tryGet/disable delegates
- `Logger.cs`: writes to `%TEMP%\TheCloser*.log`; every non-empty line gets a UTC round-trip timestamp prefix (empty lines are unprefixed separators; clock injectable via optional constructor delegate); contention-tolerant, rotates to `.log.old` above 1 MB, never throws

### TheCloser.Tests
- xUnit tests for `ProcessSettingsParser`, `SharedState` (including cross-handle visibility), `TimeoutRepair`, `CrashRepair`, `ForegroundLockSuppression`, `WindowCloser` kill-method resolution, and `Logger` rotation/append/timestamping
- Kernel objects and log files use unique GUID-suffixed names per test (via the `TestNames` helper), so tests never collide with a live daemon or each other; the repair-protocol tests inject tryGet/disable/restore delegates and never touch the real SystemParametersInfo setting

## Window Closing Methods

The application supports multiple methods configured per-process in `appsettings.json`, which is read from the deployed executable's directory and maintained by hand there (the repository carries no appsettings.json; see the README for examples). Method and ClickPosition values are parsed case-insensitively; unknown values are logged and fall back to defaults:
- **WM_DESTROY, WM_CLOSE, WM_QUIT**: Windows messages
- **SC_CLOSE**: System command (WM_SYSCOMMAND with SC_CLOSE)
- **ESCAPE, ALT-F4, CTRL-F4, CTRL-W, CTRL-SHIFT-W**: Keyboard shortcuts
- Default method: CTRL-W

## Tracking

Known bugs, quick wins, feature ideas, and design patterns are tracked in the `.claude/` indexes; see `## Backlogs and indexes` below.

## Key Implementation Details

1. **Window Detection**: Uses Windows API to get window under cursor position
2. **Foreground Window Handling**: Multiple strategies including SetForegroundWindow, AttachThreadInput, and clicking on title bar as fallback, implemented as the escalation ladder in `ForegroundActivator`. The system-wide foreground lock timeout is disabled around SetForegroundWindow and restored afterwards (the `ForegroundLockSuppression` scope); a repair record in the memory-mapped file plus the daemon watchdog and the app's startup repair heal the setting if the process is killed mid-operation
3. **Inter-Process Communication**: A single memory-mapped file (pinned by the daemon) shares the throttle tick and the timeout repair record between main app and daemon
4. **Native AOT**: Compiled with Native AOT for faster startup and smaller memory footprint
5. **Logging**: Timestamped debug logs written to temp directory for troubleshooting
6. **Accepted residual risk**: the timeout repair record only survives an app crash while a live daemon pins the memory-mapped file. If the daemon is dead (or on a first run where the 1s daemon wait timed out), a kill inside the sub-second disable window strands the foreground lock timeout at 0 until reboot. Gating the SPI manipulation on a confirmed daemon pin is an explicit anti-goal

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
