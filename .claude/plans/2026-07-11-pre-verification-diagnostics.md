# Pre-Verification Diagnostics Bundle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the two diagnosability fixes the Chrome-activation bug re-verification depends on (central log timestamps, AttachThreadInput failure logging), then redeploy so the next failing occurrence produces a correlatable, complete rung trace.

**Architecture:** `Logger.Log` gains a UTC ISO-8601 timestamp prefix on every non-empty line, with an injectable clock delegate for testability (mirroring the repo's injectable-delegate pattern in `TimeoutRepair`/`ForegroundLockSuppression`). The manual `Timestamp:` line in `Program.LogEarlyExit` is deleted; the daemon log picks up timestamps automatically since it writes through the same `Logger`. Separately, `ForegroundActivator.TryActivateNatively` stops discarding the `AttachThreadInput` result and logs failures with the Win32 error code, in the same style as the existing `SetForegroundWindow returned false.` line.

**Tech Stack:** C# / .NET (Native AOT), xUnit, PowerShell deploy script.

## Global Constraints

- Build with `dotnet build C:/Git/TheCloser --no-incremental`; test with `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build` (never the full unfiltered solution suite).
- Test methods use Arrange/Act/Assert comments.
- Blank line before every `return` statement.
- Commit subjects: Conventional Commits, max 72 chars, subject-only, no `Co-Authored-By` trailer.
- `.claude/*` changes are committed separately with the literal message `CLAUDE`, never mixed with code changes.
- No em-dashes, en-dashes, or emoji in any generated text.
- Every file ends with a newline.

## Design decisions locked in

- **Timestamp format:** `DateTime.UtcNow` formatted with `"O"` (round-trip, e.g. `2026-07-11T12:34:56.0000000Z`). UTC with `O` matches what `LogEarlyExit` historically wrote, so old and new log segments stay comparable, and lexical order equals chronological order.
- **Empty messages stay unprefixed.** `Logger.Log("")` is used as a visual separator between invocations (`Program.Main` and `LogEarlyExit` both end with it); a timestamp prefix on a separator would defeat its purpose.
- **Clock injection via optional constructor parameter** `Func<DateTime>? utcNow = null`, defaulting to `() => DateTime.UtcNow`. All production call sites keep compiling unchanged (matches the default-parameter rollout convention).
- **Attach logging is failure-only,** matching the ladder's existing style where success is implied by the subsequent rung-success line. `DetachThreadInput` in the `finally` stays unlogged: its failure is inconsequential (the process exits moments later) and logging inside `finally` would add noise to every activation.
- **No unit test for the `ForegroundActivator` change.** The class hard-wires `NativeMethods` statics; making it testable is the separately tracked "WindowCloser hard-wires ForegroundActivator and InputSimulator" quick win, deliberately out of scope here. Verification is by build plus the deployed rung trace.

---

### Task 1: Timestamps in Logger.Log

**Files:**
- Modify: `TheCloser.Shared/Logger.cs`
- Modify: `TheCloser/Program.cs` (delete the manual `Timestamp:` line in `LogEarlyExit`)
- Test: `TheCloser.Tests/LoggerTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `Logger(string appName, Func<DateTime>? utcNow = null)`; `Log(string msg)` writes `{utcNow():O} {msg}` for non-empty `msg`, a bare empty line for empty `msg`. Task 2 relies only on the unchanged `Log(string)` signature.

- [ ] **Step 1: Write the failing tests**

Add to `TheCloser.Tests/LoggerTests.cs`, inside the existing `LoggerTests` class:

```csharp
[Fact]
public void Log_NonEmptyMessage_PrefixesUtcTimestamp()
{
    // Arrange
    var fixedTime = new DateTime(2026, 7, 11, 12, 34, 56, DateTimeKind.Utc);
    var logger = new Logger(_appName, () => fixedTime);

    // Act
    logger.Log("hello");

    // Assert
    var line = Assert.Single(File.ReadAllLines(_logPath));
    Assert.Equal($"{fixedTime:O} hello", line);
}

[Fact]
public void Log_EmptyMessage_WritesBareSeparatorLine()
{
    // Arrange
    var logger = new Logger(_appName, () => new DateTime(2026, 7, 11, 12, 34, 56, DateTimeKind.Utc));

    // Act
    logger.Log("");

    // Assert
    var line = Assert.Single(File.ReadAllLines(_logPath));
    Assert.Equal(string.Empty, line);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet build C:/Git/TheCloser --no-incremental`

Expected: FAIL to compile with CS1729 ("'Logger' does not contain a constructor that takes 2 arguments") in `LoggerTests.cs`. A compile error in the test project is this red phase's expected failure mode.

- [ ] **Step 3: Implement the Logger change**

Replace the field block, constructor, and `Log` in `TheCloser.Shared/Logger.cs` (leave `RotateIfTooLarge` untouched):

```csharp
public class Logger
{
    private const long MaxLogSizeBytes = 1024 * 1024;

    private readonly string _logPath;
    private readonly Func<DateTime> _utcNow;

    public Logger(string appName, Func<DateTime>? utcNow = null)
    {
        _logPath = Path.Combine(Path.GetTempPath(), appName + ".log");
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        RotateIfTooLarge();
    }

    public void Log(string msg)
    {
        try
        {
            using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);

            // Empty messages are visual separators between invocations; a timestamp prefix would defeat that.
            writer.WriteLine(string.IsNullOrEmpty(msg) ? msg : $"{_utcNow():O} {msg}");
        }
        catch
        {
            // Logging must never crash the tool; drop the message on any IO failure.
        }
    }
```

- [ ] **Step 4: Update the two call-site casualties**

(a) `TheCloser.Tests/LoggerTests.cs`, test `Log_TwoLoggerInstancesOnTheSameFile_BothLinesArrive`: the exact-match assertions no longer hold under the prefix. Replace the Assert block:

```csharp
    // Assert
    var lines = File.ReadAllLines(_logPath);
    Assert.Contains(lines, line => line.EndsWith("line one"));
    Assert.Contains(lines, line => line.EndsWith("line two"));
```

(b) `TheCloser/Program.cs`, `LogEarlyExit`: delete the manual timestamp line, keeping the helper (it still bundles reason + separator for its two callers):

```csharp
    private static void LogEarlyExit(string reason)
    {
        Logger.Log(reason);
        Logger.Log("");
    }
```

- [ ] **Step 5: Build and run the Logger tests**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: Build succeeded, 0 warnings.

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~LoggerTests"`
Expected: all LoggerTests pass, including the two new ones.

- [ ] **Step 6: Run the whole test project**

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build`
Expected: PASS. If any non-Logger test fails on log content, it is asserting on a log line format and must be updated the same `EndsWith` way as Step 4a (none are known to; the review verified the other test classes never read log contents back).

- [ ] **Step 7: Commit**

```bash
git -C C:/Git/TheCloser add TheCloser.Shared/Logger.cs TheCloser/Program.cs TheCloser.Tests/LoggerTests.cs
git -C C:/Git/TheCloser commit -m "feat(shared): timestamp log lines centrally"
```

---

### Task 2: Log AttachThreadInput failures in the activation ladder

**Files:**
- Modify: `TheCloser/ForegroundActivator.cs` (method `TryActivateNatively`)

**Interfaces:**
- Consumes: `Logger.Log(string)` (signature unchanged by Task 1).
- Produces: nothing later tasks rely on programmatically; the deployed binary now emits `AttachThreadInput failed (error N).` inside the rung trace.

- [ ] **Step 1: Capture and log the attach result**

In `TheCloser/ForegroundActivator.cs`, `TryActivateNatively`, replace the bare call `AttachThreadInput(targetWindow);` at the top of the `try` block:

```csharp
        try
        {
            if (!AttachThreadInput(targetWindow))
            {
                _logger.Log($"AttachThreadInput failed (error {Marshal.GetLastPInvokeError()}).");
            }

            if (!SetForegroundWindow(targetWindow))
            {
                _logger.Log("SetForegroundWindow returned false.");
            }
```

`System.Runtime.InteropServices` is already imported at the top of the file (the `SendInput` failure line uses the same `Marshal.GetLastPInvokeError()` pattern), and the `AttachThreadInput` P/Invoke declares `SetLastError = true`. The `finally` block's `DetachThreadInput` stays as is per the locked-in design decision.

- [ ] **Step 2: Build clean**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: Build succeeded, 0 warnings. No unit test exists for this class (see design decisions); the build gate plus Task 3's deployed trace is the verification.

- [ ] **Step 3: Commit**

```bash
git -C C:/Git/TheCloser add TheCloser/ForegroundActivator.cs
git -C C:/Git/TheCloser commit -m "feat(app): log AttachThreadInput failures"
```

---

### Task 3: Redeploy

**Files:**
- None modified; runs `deploy.ps1` (stops the daemon, publishes Release/AOT, copies both exes to `C:\Sync\Personal\3. Resources\Bin\TheCloser\`). The script uses `$PSScriptRoot` throughout, so it can be invoked from any working directory.

- [ ] **Step 1: Deploy**

Run: `pwsh -NoProfile C:/Git/TheCloser/deploy.ps1`
Expected: `dotnet publish` succeeds, two `Copy-Item` verbose lines confirm `TheCloser.exe` and `TheCloser.Daemon.exe` landed in the destination. A `Stop-Process` verbose line appears only if the daemon was running.

- [ ] **Step 2: Verify the copied binaries are fresh**

Run: `pwsh -NoProfile -Command "Get-ChildItem 'C:\Sync\Personal\3. Resources\Bin\TheCloser\*.exe' | Select-Object Name, LastWriteTime"`
Expected: both exes carry today's date and a just-now time.

Do NOT smoke-run the deployed `TheCloser.exe`: it closes the window under the cursor. The daemon restarts automatically on the next real invocation. Interactive re-verification of the Chrome bug needs the user at the mouse and stays out of this plan.

---

### Task 4: Update the Chrome bug entry and clean up the plan

**Files:**
- Modify: `.claude/BUGS.md` (Chrome entry, "Next steps" item 2)
- Delete: `.claude/plans/2026-07-11-pre-verification-diagnostics.md` (plans are ephemeral; delete once the work lands)

- [ ] **Step 1: Mark the prep done in BUGS.md**

In `.claude/BUGS.md`, replace next-steps item 2 of the Chrome entry:

```markdown
2. Diagnostics deployed 2026-07-11: per-rung ladder logging (ForegroundActivator.TryActivate), timestamps on every log line (Logger.Log), and AttachThreadInput failure logging (ForegroundActivator.TryActivateNatively). After the next failing occurrence, the log will state exactly which rung claimed success, whether the attach failed, and when, correlatable with the interactive attempt.
```

- [ ] **Step 2: Delete this plan file**

```bash
git -C C:/Git/TheCloser rm .claude/plans/2026-07-11-pre-verification-diagnostics.md
```

(If the plan file was never committed, plain `rm` instead.)

- [ ] **Step 3: Commit the .claude changes separately**

```bash
git -C C:/Git/TheCloser add .claude/BUGS.md
git -C C:/Git/TheCloser commit -m "CLAUDE"
```

---

## Self-review notes

- Spec coverage: BUGS.md next-step 2 names exactly three parts (timestamps centrally in `Logger.Log` deleting the manual `Timestamp:` line, attach-result logging in `TryActivateNatively`, redeploy); Tasks 1, 2, 3 map one-to-one, Task 4 closes the tracking loop.
- The `Log_FileLockedExclusively_DoesNotThrow`, rotation, and multi-instance tests are unaffected by the prefix except `Log_TwoLoggerInstancesOnTheSameFile_BothLinesArrive`, handled in Task 1 Step 4a.
- Type consistency: Task 2 consumes `Log(string)` which Task 1 does not change; the new `Logger` constructor parameter is optional, so `Program.cs` (app and daemon) and all test constructions compile unchanged.
