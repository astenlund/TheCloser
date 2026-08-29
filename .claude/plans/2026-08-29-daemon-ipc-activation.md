# Daemon IPC Activation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the normal activation path from per-press executable launch to signaling a long-lived daemon, eliminating the 0.7 to 2.5 second Windows process-creation and Defender-scan delays.

**Architecture:** AutoHotkey writes a QPC-plus-button payload into the existing `TheCloserSharedState` MMF and signals a new session-local auto-reset event; the daemon's watchdog loop becomes a `WaitAny` that runs the close pipeline in-process. The close pipeline relocates from `TheCloser` to `TheCloser.Shared` so both the app (fallback path, unchanged behavior) and the daemon can host it. Deployment gains an automatic elevated-task restart so AHK changes need no manual step.

**Tech Stack:** .NET 10 (net10.0-windows, Native AOT), xUnit, AutoHotkey v1, PowerShell 7.

The governing design is the `## Fix design: daemon IPC activation` section of `.claude/bugs/intermittent-slow-invocation.md` (revise-spec graduated 2026-08-29 08:15 at 250d591, content 5dd13c13); the machine-readable contract above this header is authoritative. The plan argues from that section; executors read both. Where this plan and the spec disagree, the spec wins; report the divergence instead of improvising.

**Spec:** [.claude/bugs/intermittent-slow-invocation.md](.claude/bugs/intermittent-slow-invocation.md)

## Governing specs

- Spec JSON: {"kind":"sections","path":".claude/bugs/intermittent-slow-invocation.md","selectors":[{"headingPath":["## Fix design: daemon IPC activation"]}],"workUnit":null}

## Global Constraints

- All commands below run under **PowerShell 7 (`pwsh -NoProfile`)** unless a step names another shell. Every command is self-contained; no shell state carries between steps.
- Build with `dotnet build C:/Git/TheCloser --no-incremental`; run tests with `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "<filter>"`. Never run the full unfiltered suite.
- No em-dashes, en-dashes, or emoji in any generated text. All new file content is pure ASCII; after editing `TheCloser.ahk` or any file where escape sequences matter, byte-sweep with `rg --crlf -n "[^ -~\t]" <file>` (Git Bash) and expect zero matching lines.
- Kernel object names stay session-local (no `Global\` prefix) and centralized in `TheCloser.Shared/Constants.cs`; the one sanctioned non-C# copy is `TheCloser.ahk`, comment-marked at every duplication site.
- Commit subjects follow Conventional Commits, max 72 chars, subject-only (no body, no Co-Authored-By trailer).
- C# style: blank line before `return`, block braces always, `required` first, pattern matching and collection expressions where natural, Arrange/Act/Assert comments in tests, no `.ToLower()` comparisons.
- The plan file itself is never staged in any implementation commit.
- Tests must never touch the real kernel object names or the real SystemParametersInfo setting: every test uses GUID-suffixed names via the existing `TestNames` helper and injected delegates.

---

## File structure

| File | Responsibility |
|---|---|
| `TheCloser.Shared/WindowCloser.cs` (+9 siblings, moved) | Close pipeline, relocated verbatim except namespace, detach seam, and healer comment |
| `TheCloser.Shared/Constants.cs` | Gains `ActivationEventName`, button codes, shared throttle threshold |
| `TheCloser.Shared/SharedState.cs` | Gains activation payload accessors (offsets 16/24, consume-once) |
| `TheCloser.Shared/LastGoodConfiguration.cs` (new) | Per-activation value-copy snapshot of the live configuration root |
| `TheCloser.Shared/ActivationHandler.cs` (new) | Per-activation orchestration: payload, latency and deferred marker, throttle, guard mutex, pending repair, close dispatch, healer dispatch decision |
| `TheCloser.Shared/DaemonRuntime.cs` (new) | Daemon lifetime: startup order, WaitAny loop, watchdog tick, final repair tick, healer tracking and drain, unwind |
| `TheCloser.Daemon/Program.cs` | Thin composition root wiring DaemonRuntime with real config, pipeline, healer |
| `TheCloser/Program.cs` | Uses the shared throttle constant; otherwise unchanged control flow |
| `TheCloser.ahk` | Full rewrite: `#SingleInstance Force`, auto-execute stop-poll-start, IPC trigger handler with fallback |
| `install-elevated-ahk.ps1` | Unconditional register-stop-sweep-start with bounded polls |
| `deploy.ps1` | Task stop-poll-start restart plus first-deploy self-elevated install branch |
| `TheCloser.Tests/*` | Existing tests survive the move; new test files per new unit |

---

### Task 1: Relocate the close pipeline to TheCloser.Shared

**Files:**
- Move (git mv, then edit): `TheCloser/WindowCloser.cs`, `TheCloser/ForegroundActivator.cs`, `TheCloser/IForegroundActivator.cs`, `TheCloser/INativeWindowApi.cs`, `TheCloser/NativeWindowApi.cs`, `TheCloser/NativeMethods.cs`, `TheCloser/ProcessSettingsParser.cs`, `TheCloser/ProcessSettings.cs`, `TheCloser/TitleBarClickPosition.cs`, `TheCloser/TriggerButtonHealer.cs` to `TheCloser.Shared/`
- Modify: `TheCloser/TheCloser.csproj`, `TheCloser.Shared/TheCloser.Shared.csproj`
- Modify: every file under `TheCloser.Tests/` whose usings reference the moved types

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: all ten types under namespace `TheCloser.Shared` with unchanged member signatures; `TheCloser.Shared` visible to `TheCloser`, `TheCloser.Daemon`, `TheCloser.Tests` via `InternalsVisibleTo`.

- [ ] **Step 1: Move the files**

Run (Git Bash):
```bash
cd /c/Git/TheCloser && for f in WindowCloser ForegroundActivator IForegroundActivator INativeWindowApi NativeWindowApi NativeMethods ProcessSettingsParser ProcessSettings TitleBarClickPosition TriggerButtonHealer; do git mv "TheCloser/$f.cs" "TheCloser.Shared/$f.cs"; done
```
Expected: silent success; `git status` shows ten renames.

- [ ] **Step 2: Rewrite namespaces and usings in the moved files**

In each of the ten moved files, apply exactly:
1. `namespace TheCloser;` becomes `namespace TheCloser.Shared;`
2. Delete the line `using TheCloser.Shared;` where present (the file now lives in that namespace). Present in: `WindowCloser.cs`, `ForegroundActivator.cs`, `TriggerButtonHealer.cs`.
3. `using static TheCloser.NativeMethods;` becomes `using static TheCloser.Shared.NativeMethods;` (present in `WindowCloser.cs`, `ForegroundActivator.cs`, `TriggerButtonHealer.cs`, `INativeWindowApi.cs`).
4. `using static TheCloser.TitleBarClickPosition;` becomes `using static TheCloser.Shared.TitleBarClickPosition;` (present in `WindowCloser.cs`, `ForegroundActivator.cs`).

- [ ] **Step 3: Rewrite the TriggerButtonHealer header comment**

In `TheCloser.Shared/TriggerButtonHealer.cs`, replace the sentence beginning `Rather than delaying activation until the button is released, the app monitors the trigger` through the end of that comment block (the text `far slower than the monitor does.`) with:

```csharp
// Rather than delaying activation until the button is released, the hosting process monitors the
// trigger buttons after the close operation completes: a genuine hold clears itself on release,
// while a stranded state reads down forever, so anything still down at the deadline gets its
// release injected. Two hosts run this monitor: the daemon dispatches it as a background task
// after an attach-performing close (so the linger never blocks the next activation), and the
// fallback app runs it inline after its close exactly as before. Overlapping monitors are safe:
// injection happens only for a button still observed down at the deadline, and a duplicated
// release for an already-up button is an OS-level no-op.
```

- [ ] **Step 4: Move the package references and add InternalsVisibleTo**

In `TheCloser/TheCloser.csproj`, delete these three lines:
```xml
    <PackageReference Include="GregsStack.InputSimulatorStandard" Version="1.3.1" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.9" />
```
If the enclosing `<ItemGroup>` becomes empty, delete it too.

In `TheCloser.Shared/TheCloser.Shared.csproj`, replace the whole file with:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="GregsStack.InputSimulatorStandard" Version="1.3.1" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.9" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.9" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="TheCloser.Tests" />
    <InternalsVisibleTo Include="TheCloser" />
    <InternalsVisibleTo Include="TheCloser.Daemon" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Fix test-project usings**

Run (Git Bash) to find affected files:
```bash
grep -l "TheCloser\.\(WindowCloser\|ForegroundActivator\|NativeMethods\|TriggerButtonHealer\|ProcessSettingsParser\|TitleBarClickPosition\)" /c/Git/TheCloser/TheCloser.Tests/*.cs; grep -ln "^using TheCloser;$" /c/Git/TheCloser/TheCloser.Tests/*.cs
```
In each listed file, the moved types now resolve through `using TheCloser.Shared;` (add it if absent). Keep `using TheCloser;` only in files that still reference `Program` or `InvocationProbe` (`InvocationProbeTests.cs`); remove it where it becomes unused. `using static TheCloser.NativeMethods;` becomes `using static TheCloser.Shared.NativeMethods;` and `using static TheCloser.TitleBarClickPosition;` becomes `using static TheCloser.Shared.TitleBarClickPosition;` wherever they appear under `TheCloser.Tests/`.

- [ ] **Step 6: Build and run the relocation regression net**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: Build succeeded, 0 warnings attributable to the move.

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~WindowCloserTests|FullyQualifiedName~ForegroundActivatorTests|FullyQualifiedName~TriggerButtonHealerTests|FullyQualifiedName~ProcessSettingsParserTests"`
Expected: all listed tests pass, none skipped.

- [ ] **Step 7: Commit**

```bash
git -C C:/Git/TheCloser add -A && git -C C:/Git/TheCloser commit -m "refactor(shared): relocate close pipeline for daemon hosting"
```

---

### Task 2: Activation constants and SharedState payload accessors

**Files:**
- Modify: `TheCloser.Shared/Constants.cs`
- Modify: `TheCloser.Shared/SharedState.cs`
- Modify: `TheCloser/Program.cs` (consume the shared throttle constant)
- Test: `TheCloser.Tests/SharedStateTests.cs` (append cases)

**Interfaces:**
- Produces: `Constants.ActivationEventName` (string `"TheCloserActivationEvent"`), `Constants.ThrottleThresholdMs` (long, `200`), `Constants.TriggerButtonUnknown` (int `0`), `Constants.TriggerButtonXButton2` (int `1`); `SharedState.WriteActivationPayload(long launchQpc, int buttonCode)`; `SharedState.ConsumeActivationPayload()` returning `(long LaunchQpc, int ButtonCode)` and zeroing both fields.

- [ ] **Step 1: Write the failing tests**

Append to `TheCloser.Tests/SharedStateTests.cs` (match the file's existing style and GUID-name helper usage):

```csharp
[Fact]
public void ActivationPayload_RoundTripsThenZeroesOnConsume()
{
    // Arrange
    using var state = new SharedState(TestNames.UniqueMapName());
    state.WriteActivationPayload(123456789L, Constants.TriggerButtonXButton2);

    // Act
    var first = state.ConsumeActivationPayload();
    var second = state.ConsumeActivationPayload();

    // Assert
    Assert.Equal(123456789L, first.LaunchQpc);
    Assert.Equal(Constants.TriggerButtonXButton2, first.ButtonCode);
    Assert.Equal(0L, second.LaunchQpc);
    Assert.Equal(Constants.TriggerButtonUnknown, second.ButtonCode);
}

[Fact]
public void ActivationPayload_ReadsZeroWhenNeverWritten()
{
    // Arrange
    using var state = new SharedState(TestNames.UniqueMapName());

    // Act
    var payload = state.ConsumeActivationPayload();

    // Assert
    Assert.Equal(0L, payload.LaunchQpc);
    Assert.Equal(Constants.TriggerButtonUnknown, payload.ButtonCode);
}
```

If `TestNames` has no `UniqueMapName()` member, use the file's existing unique-map-name idiom verbatim instead; do not invent a new helper.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build C:/Git/TheCloser --no-incremental; dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~SharedStateTests"`
Expected: build FAILS with CS0117 (`SharedState` contains no definition for `WriteActivationPayload`) before any test runs; that compile failure is this step's red state.

- [ ] **Step 3: Implement**

In `TheCloser.Shared/Constants.cs`, after the line `public const string ProbeLogMutexName = "TheCloserProbeLogMutex";`, insert:

```csharp
    // Duplicated by hand in TheCloser.ahk (AutoHotkey cannot consume this file); keep in sync.
    public const string ActivationEventName = "TheCloserActivationEvent";

    // Shared between the app's fallback path and the daemon's IPC path.
    public const long ThrottleThresholdMs = 200;

    // Trigger button codes for the activation payload. Duplicated by hand in TheCloser.ahk.
    public const int TriggerButtonUnknown = 0;
    public const int TriggerButtonXButton2 = 1;
```

In `TheCloser.Shared/SharedState.cs`, after `private const int RepairPending = 1;`, insert:

```csharp
    // Activation payload offsets. Duplicated by hand in TheCloser.ahk (NumPut sites); keep in sync.
    private const int ActivationQpcOffset = 16;
    private const int ActivationButtonOffset = 24;
```

and after the `TryReadTimeoutRepair` method, insert:

```csharp
    public void WriteActivationPayload(long launchQpc, int buttonCode)
    {
        // Values before the event signal: the activation event acts as the payload-ready flag,
        // mirroring the value-before-flag discipline of the repair record above.
        _accessor.Write(ActivationQpcOffset, launchQpc);
        _accessor.Write(ActivationButtonOffset, buttonCode);
    }

    // Consume-once: zeroing after the read keeps a failed later mapping from replaying this
    // press's values as a fresh latency (see the fix design's payload contract).
    public (long LaunchQpc, int ButtonCode) ConsumeActivationPayload()
    {
        var launchQpc = _accessor.ReadInt64(ActivationQpcOffset);
        var buttonCode = _accessor.ReadInt32(ActivationButtonOffset);

        _accessor.Write(ActivationQpcOffset, 0L);
        _accessor.Write(ActivationButtonOffset, 0);

        return (launchQpc, buttonCode);
    }
```

In `TheCloser/Program.cs`, delete the line `private const long StartupIntervalThresholdMs = 200;` and replace its two uses (`StartupIntervalThresholdMs`) with `ThrottleThresholdMs` (resolved via the existing `using static TheCloser.Shared.Constants;`).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet build C:/Git/TheCloser --no-incremental; dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~SharedStateTests|FullyQualifiedName~InvocationProbeTests"`
Expected: PASS, all cases.

- [ ] **Step 5: Commit**

```bash
git -C C:/Git/TheCloser add -A && git -C C:/Git/TheCloser commit -m "feat(shared): activation payload region and shared constants"
```

---

### Task 3: Detach hardening (captured thread ids)

**Files:**
- Modify: `TheCloser.Shared/INativeWindowApi.cs`, `TheCloser.Shared/NativeWindowApi.cs`, `TheCloser.Shared/NativeMethods.cs`, `TheCloser.Shared/ForegroundActivator.cs`
- Test: `TheCloser.Tests/ForegroundActivatorTests.cs` (fake update + one new case)

**Interfaces:**
- Produces: `INativeWindowApi.AttachThreadInput(IntPtr hWnd)` now returns `uint` (the attached peer thread id, `0` on failure); `INativeWindowApi.DetachThreadInput(uint threadId)` returns `bool`. `ForegroundActivator` behavior otherwise unchanged.

- [ ] **Step 1: Change the seam**

`INativeWindowApi.cs`: replace
```csharp
    bool AttachThreadInput(IntPtr hWnd);

    bool DetachThreadInput(IntPtr hWnd);
```
with
```csharp
    // Returns the peer thread id that was attached, or 0 when the attach failed. The caller
    // detaches by that captured id: re-resolving the thread from the window at detach time is a
    // silent no-op once the window is destroyed, which would leak the attachment on a
    // long-lived thread (see the fix design's detach hardening).
    uint AttachThreadInput(IntPtr hWnd);

    bool DetachThreadInput(uint threadId);
```

`NativeMethods.cs`: replace the two public wrapper methods `AttachThreadInput(IntPtr)` and `DetachThreadInput(IntPtr)` with:
```csharp
    public static uint AttachThreadInputToWindow(IntPtr hWnd)
    {
        var currentThreadId = GetCurrentThreadId();
        var targetThreadId = GetWindowThreadProcessId(hWnd, out _);

        if (targetThreadId == 0 || !AttachThreadInput(currentThreadId, targetThreadId, true))
        {
            return 0;
        }

        return targetThreadId;
    }

    public static bool DetachThreadInputFromThread(uint threadId)
    {
        return AttachThreadInput(GetCurrentThreadId(), threadId, false);
    }
```

`NativeWindowApi.cs`: replace the two corresponding members with:
```csharp
    public uint AttachThreadInput(IntPtr hWnd) => NativeMethods.AttachThreadInputToWindow(hWnd);

    public bool DetachThreadInput(uint threadId) => NativeMethods.DetachThreadInputFromThread(threadId);
```

`ForegroundActivator.cs`: rework `TryActivateNatively` and `TryAttachToForegroundOwner` to capture and detach by id, logging failed detaches. The exact final method bodies:
```csharp
    private bool TryActivateNatively(IntPtr targetWindow)
    {
        using var suppression = _suppressionFactory();

        var foregroundWindow = _native.GetForegroundWindow();
        var foregroundOwnerThreadId = TryAttachToForegroundOwner(foregroundWindow, targetWindow);
        var targetThreadId = 0u;

        try
        {
            targetThreadId = _native.AttachThreadInput(targetWindow);

            if (targetThreadId == 0)
            {
                _logger.Log($"AttachThreadInput failed (error {Marshal.GetLastPInvokeError()}).");
            }

            PerformedInputAttach |= targetThreadId != 0 || foregroundOwnerThreadId != 0;

            if (!_native.SetForegroundWindow(targetWindow))
            {
                _logger.Log("SetForegroundWindow returned false.");
            }
        }
        finally
        {
            // Detach before the settle wait, by the ids captured at attach time: a window
            // destroyed mid-close makes a handle-resolved detach a silent no-op, which on the
            // daemon's long-lived loop thread would accumulate input-queue attachments.
            if (targetThreadId != 0 && !_native.DetachThreadInput(targetThreadId))
            {
                _logger.Log($"DetachThreadInput({targetThreadId}) failed (error {Marshal.GetLastPInvokeError()}).");
            }

            if (foregroundOwnerThreadId != 0 && !_native.DetachThreadInput(foregroundOwnerThreadId))
            {
                _logger.Log($"DetachThreadInput({foregroundOwnerThreadId}) failed (error {Marshal.GetLastPInvokeError()}).");
            }
        }

        _sleep(InputSettleDelay);

        return IsForeground(targetWindow);
    }

    private uint TryAttachToForegroundOwner(IntPtr foregroundWindow, IntPtr targetWindow)
    {
        if (foregroundWindow == IntPtr.Zero)
        {
            _logger.Log("Skipping the foreground owner attach (no foreground window).");

            return 0;
        }

        if (_native.GetWindowThreadId(foregroundWindow) == _native.GetWindowThreadId(targetWindow))
        {
            return 0;
        }

        var ownerThreadId = _native.AttachThreadInput(foregroundWindow);

        if (ownerThreadId == 0)
        {
            _logger.Log($"AttachThreadInput to the foreground owner failed (error {Marshal.GetLastPInvokeError()}).");
        }

        return ownerThreadId;
    }
```
(Note: `TryActivateNatively` still returns `IsForeground(targetWindow)` after the settle sleep; do not change that tail.)

- [ ] **Step 2: Update the fake and add the new test**

In `TheCloser.Tests/ForegroundActivatorTests.cs`, update the `FakeNativeApi` members to the new signatures: attach returns a configurable nonzero thread id per window (for example `AttachResults` dictionary from window to id, default 1), detach records the ids it was called with in a `DetachedThreadIds` list and returns a configurable bool. Then add:

```csharp
[Fact]
public void TryActivate_DetachesByCapturedIds_AndLogsFailedDetach()
{
    // Arrange: native activation path with distinct target and owner thread ids, and a detach
    // that fails, simulating a target window destroyed mid-close.
    var native = new FakeNativeApi { /* configure: target attach id 42, owner attach id 7, DetachReturns = false, foreground transitions so TryActivateNatively runs */ };
    var log = new List<string>();
    var activator = new ForegroundActivator(sharedState, TestLogger(log), native, _ => { }, () => FakeSuppression());

    // Act
    activator.TryActivate(SomeWindow, TitleBarClickPosition.Left);

    // Assert: both captured ids were detached (never re-resolved from the window), and the
    // failed returns were logged rather than discarded.
    Assert.Contains(42u, native.DetachedThreadIds);
    Assert.Contains(7u, native.DetachedThreadIds);
    Assert.Contains(log, line => line.Contains("DetachThreadInput(42)"));
}
```
Adapt constructor arguments and fixture helpers to the file's existing patterns (it already builds `ForegroundActivator` with an injected fake, sleep, and suppression factory); the assertions above are the contract.

- [ ] **Step 3: Run to verify failure, then pass**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected first (before the seam change compiles everywhere): if you wrote the test first the build fails on the fake's old signatures; after implementing, expected: Build succeeded.

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~ForegroundActivatorTests"`
Expected: PASS, including the new case.

- [ ] **Step 4: Commit**

```bash
git -C C:/Git/TheCloser add -A && git -C C:/Git/TheCloser commit -m "fix(activator): detach input queues by captured thread ids"
```

---

### Task 4: LastGoodConfiguration snapshot

**Files:**
- Create: `TheCloser.Shared/LastGoodConfiguration.cs`
- Test: `TheCloser.Tests/LastGoodConfigurationTests.cs` (new)

**Interfaces:**
- Produces: `internal sealed class LastGoodConfiguration` with `IConfiguration Refresh(IConfiguration liveRoot, Action<string> log)`. Returns the retained snapshot; a non-empty live enumeration replaces it (value copy via `AsEnumerable()` into `AddInMemoryCollection`), an empty enumeration after a previously non-empty one logs a warning and keeps the previous snapshot, and a never-populated snapshot is the empty configuration with no warning.

- [ ] **Step 1: Write the failing tests**

Create `TheCloser.Tests/LastGoodConfigurationTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using Xunit;

namespace TheCloser.Tests;

public class LastGoodConfigurationTests
{
    private static IConfiguration Root(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Refresh_EmptyRootAfterNonEmpty_KeepsSnapshotAndWarns()
    {
        // Arrange
        var lastGood = new LastGoodConfiguration();
        var warnings = new List<string>();
        lastGood.Refresh(Root(("notepad", "WM_CLOSE")), warnings.Add);

        // Act
        var snapshot = lastGood.Refresh(Root(), warnings.Add);

        // Assert
        Assert.Equal("WM_CLOSE", snapshot["notepad"]);
        Assert.Single(warnings);
    }

    [Fact]
    public void Refresh_NeverPopulated_ReturnsEmptyWithoutWarning()
    {
        // Arrange
        var lastGood = new LastGoodConfiguration();
        var warnings = new List<string>();

        // Act
        var snapshot = lastGood.Refresh(Root(), warnings.Add);

        // Assert
        Assert.Empty(snapshot.GetChildren());
        Assert.Empty(warnings);
    }

    [Fact]
    public void Refresh_ValueCopiesBothEntryForms()
    {
        // Arrange: flat-string and nested-object forms, as the README documents.
        var lastGood = new LastGoodConfiguration();
        var live = Root(("devenv", "CTRL-F4"), ("sublime_merge:Method", "CTRL-W"), ("sublime_merge:ClickPosition", "Center"));

        // Act
        var snapshot = lastGood.Refresh(live, _ => { });
        var parsedFlat = ProcessSettingsParser.Parse(snapshot, "devenv", _ => { });
        var parsedNested = ProcessSettingsParser.Parse(snapshot, "sublime_merge", _ => { });

        // Assert
        Assert.Equal("CTRL-F4", parsedFlat.Method);
        Assert.Equal("CTRL-W", parsedNested.Method);
        Assert.Equal(TitleBarClickPosition.Center, parsedNested.ClickPosition);
    }
}
```
(Adjust the `ProcessSettingsParser.Parse` signature usage to the actual shipped signature, `Parse(IConfiguration, string, Action<string>)`; verify by reading `TheCloser.Shared/ProcessSettingsParser.cs` before writing.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: FAIL with CS0246 (`LastGoodConfiguration` not found).

- [ ] **Step 3: Implement**

Create `TheCloser.Shared/LastGoodConfiguration.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace TheCloser.Shared;

// Per-activation value-copy snapshot of the live configuration root. The pipeline always
// receives this snapshot, never the live root: under reloadOnChange a failed reload can empty
// the live providers, and retained IConfigurationSection references are live views that would
// go empty with them. The build-vs-reload overlap race is accepted with a one-activation blast
// radius (see the fix design's Configuration section).
internal sealed class LastGoodConfiguration
{
    private IConfiguration _snapshot = new ConfigurationBuilder().Build();
    private bool _populated;

    public IConfiguration Refresh(IConfiguration liveRoot, Action<string> log)
    {
        var pairs = liveRoot.AsEnumerable().ToList();

        if (pairs.Count > 0)
        {
            _snapshot = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
            _populated = true;
        }
        else if (_populated)
        {
            log("Configuration reload produced an empty root; keeping the last good snapshot.");
        }

        return _snapshot;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet build C:/Git/TheCloser --no-incremental; dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~LastGoodConfigurationTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git -C C:/Git/TheCloser add -A && git -C C:/Git/TheCloser commit -m "feat(shared): last-good configuration snapshot"
```

---

### Task 5: ActivationHandler

**Files:**
- Create: `TheCloser.Shared/ActivationHandler.cs`
- Test: `TheCloser.Tests/ActivationHandlerTests.cs` (new)

**Interfaces:**
- Consumes: `SharedState.ConsumeActivationPayload()` (Task 2), `Constants.ThrottleThresholdMs`, `TimeoutRepair.TryRestorePending(SharedState)`.
- Produces:
```csharp
internal sealed class ActivationHandler
{
    public ActivationHandler(
        SharedState sharedState,
        Logger logger,
        string guardMutexName,
        Func<IConfiguration> settings,          // last-good snapshot provider
        Func<IConfiguration, bool> runClose,    // constructs the pipeline fresh, returns attach-performed
        Action dispatchHealer,                  // called when runClose reported an attach
        Func<long>? timestamp = null,           // Stopwatch.GetTimestamp
        Func<long>? tickCount = null);          // Environment.TickCount64

    public void HandleActivation();
}
```
Behavior contract (each clause is a test): consume payload at handler entry; latency line with plausibility guard (zero / non-positive / future / over 10 s logs unavailable, button then logs unknown); deferred marker when a plausible QPC predates the stored handler-exit timestamp (initialized to construction time, refreshed at every handler exit including skips); throttle skip inside threshold (log, no close); guard mutex created unowned per activation, `WaitOne(0)`, busy skips with log, `AbandonedMutexException` counts as acquired, release and dispose in `finally`; pending repair restored after acquiring; throttle tick written before the close; `runClose` exception logged and swallowed; `dispatchHealer` invoked exactly when `runClose` returned true.

- [ ] **Step 1: Write the failing tests**

Create `TheCloser.Tests/ActivationHandlerTests.cs`. Before writing, read `TheCloser.Tests/TestNames.cs`, `TheCloser.Tests/TempLogger.cs`, and `TheCloser.Shared/TimeoutRepair.cs`; reuse their exact helper names (the harness below assumes `TestNames.UniqueMapName()` / `TestNames.UniqueMutexName()` and a `TempLogger`-style capturing logger; adapt those two identifiers to the shipped helpers, changing nothing else). The handler takes `restorePending` injected so no test touches SystemParametersInfo.

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using Xunit;

using static TheCloser.Shared.Constants;

namespace TheCloser.Tests;

public class ActivationHandlerTests
{
    private sealed class Harness : IDisposable
    {
        public SharedState State { get; } = new(TestNames.UniqueMapName());
        public string MutexName { get; } = TestNames.UniqueMutexName();
        public List<string> Log { get; } = [];
        public List<string> Events { get; } = [];
        public long Now = Stopwatch.GetTimestamp();
        public long Tick = 100_000;
        public bool CloseResult;
        public bool CloseThrows;
        public bool RestoreResult = true;

        public ActivationHandler Build() => new(
            State,
            TestLoggers.Capturing(Log),
            MutexName,
            settings: () => new ConfigurationBuilder().Build(),
            runClose: _ =>
            {
                Events.Add("close");

                return CloseThrows ? throw new InvalidOperationException("close failed") : CloseResult;
            },
            dispatchHealer: () => Events.Add("healer"),
            timestamp: () => Now,
            tickCount: () => Tick,
            restorePending: _ =>
            {
                Events.Add("restore");

                return RestoreResult;
            });

        public void AdvancePastThrottle() => Tick += ThrottleThresholdMs + 1;

        public void Dispose() => State.Dispose();
    }

    [Fact]
    public void PlausiblePayload_LogsLatencyAndButton()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.Now += Stopwatch.Frequency / 100; // press 10 ms ago relative to handler entry
        h.State.WriteActivationPayload(h.Now - Stopwatch.Frequency / 100, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains(h.Log, line => line.Contains("Activation: latency") && line.Contains("XButton2"));
    }

    [Fact]
    public void ZeroPayload_LogsUnavailableAndUnknownButton()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains(h.Log, line => line.Contains("latency unavailable") && line.Contains("unknown"));
    }

    [Fact]
    public void StalePayloadOverTenSeconds_LogsUnavailable()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.State.WriteActivationPayload(h.Now - 11 * Stopwatch.Frequency, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains(h.Log, line => line.Contains("latency unavailable"));
    }

    [Fact]
    public void PlausibleQpcPredatingHandlerExit_LogsDeferredMarker()
    {
        // Arrange: first activation stamps the handler exit; the second press's QPC predates it.
        using var h = new Harness();
        var handler = h.Build();
        handler.HandleActivation();
        var pressBeforeExit = h.Now - Stopwatch.Frequency / 1000;
        h.AdvancePastThrottle();
        h.Now += Stopwatch.Frequency / 100;
        h.State.WriteActivationPayload(pressBeforeExit, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains(h.Log, line => line.Contains("(deferred)"));
    }

    [Fact]
    public void FreshPress_AfterThrottleSkip_IsNotMarkedDeferred()
    {
        // Arrange: a throttle-skipped activation must still refresh the handler-exit stamp, so a
        // press issued after the skip judges deferral against the skip's exit, not an older one.
        using var h = new Harness();
        var handler = h.Build();
        handler.HandleActivation();          // stamps exit at h.Now
        handler.HandleActivation();          // throttle skip (Tick unchanged), restamps exit
        h.AdvancePastThrottle();
        h.Now += Stopwatch.Frequency / 10;
        h.State.WriteActivationPayload(h.Now - Stopwatch.Frequency / 100, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.DoesNotContain(h.Log, line => line.Contains("(deferred)"));
    }

    [Fact]
    public void WithinThrottle_SkipsCloseAndLogs()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        handler.HandleActivation();

        // Act: Tick unchanged, so the second activation is inside the threshold.
        handler.HandleActivation();

        // Assert
        Assert.Single(h.Events, e => e == "close");
        Assert.Contains(h.Log, line => line.Contains("Activation skipped: the previous handling"));
    }

    [Fact]
    public void BusyGuardMutex_SkipsAndLogs()
    {
        // Arrange: hold the guard mutex on another thread for the duration of the call.
        using var h = new Harness();
        var handler = h.Build();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = new Mutex(true, h.MutexName, out _);
            held.Set();
            release.Wait();
            m.ReleaseMutex();
        });
        holder.Start();
        held.Wait();

        // Act
        handler.HandleActivation();
        release.Set();
        holder.Join();

        // Assert
        Assert.DoesNotContain(h.Events, e => e == "close");
        Assert.Contains(h.Log, line => line.Contains("guard mutex is held"));
    }

    [Fact]
    public void AbandonedGuardMutex_CountsAsAcquired()
    {
        // Arrange: a thread takes ownership and dies without releasing.
        using var h = new Harness();
        var handler = h.Build();
        Mutex? abandoned = null;
        var t = new Thread(() => abandoned = new Mutex(true, h.MutexName, out _));
        t.Start();
        t.Join();

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains(h.Events, e => e == "close");
        abandoned?.Dispose();
    }

    [Fact]
    public void PendingRepair_RestoredBeforeClose()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.State.SetTimeoutRepair(200);

        // Act
        handler.HandleActivation();

        // Assert: restore observed, and strictly before the close.
        Assert.Equal(["restore", "close"], h.Events);
    }

    [Fact]
    public void ThrowingClose_IsLoggedAndSwallowed_AndReleasesMutex()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.CloseThrows = true;

        // Act
        handler.HandleActivation();

        // Assert: the exception is logged, and the handle was released and disposed (a fresh
        // owned create sees a brand-new object).
        Assert.Contains(h.Log, line => line.Contains("close failed"));
        using var probe = new Mutex(initiallyOwned: true, h.MutexName, out var createdNew);
        Assert.True(createdNew);
        probe.ReleaseMutex();
    }

    [Fact]
    public void AttachClose_DispatchesHealer_NonAttachDoesNot()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.CloseResult = true;

        // Act
        handler.HandleActivation();
        h.AdvancePastThrottle();
        h.CloseResult = false;
        handler.HandleActivation();

        // Assert
        Assert.Equal(1, h.Events.Count(e => e == "healer"));
    }
}
```
`TestLoggers.Capturing(Log)` stands for whatever the existing test suite uses to build a `Logger` whose lines a test can read (`TempLogger` or equivalent); reuse that helper verbatim rather than inventing a new one, and adjust the two harness lines that reference it.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: FAIL with CS0246 (`ActivationHandler` not found).

- [ ] **Step 3: Implement**

Create `TheCloser.Shared/ActivationHandler.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Configuration;

using static TheCloser.Shared.Constants;

namespace TheCloser.Shared;

// Per-activation orchestration for the daemon's IPC path. Mirrors the app Main's responsibility
// order (payload, throttle, guard mutex, pending repair, close, healer decision); see the fix
// design's responsibility table. Instances live for the daemon's lifetime; everything per-press
// is created inside HandleActivation.
internal sealed class ActivationHandler
{
    private static readonly TimeSpan MaxPlausibleLatency = TimeSpan.FromSeconds(10);

    private readonly SharedState _sharedState;
    private readonly Logger _logger;
    private readonly string _guardMutexName;
    private readonly Func<IConfiguration> _settings;
    private readonly Func<IConfiguration, bool> _runClose;
    private readonly Action _dispatchHealer;
    private readonly Func<long> _timestamp;
    private readonly Func<long> _tickCount;
    private readonly Func<SharedState, bool> _restorePending;

    // Deferred-press attribution state: a plausible payload QPC older than the previous handler
    // exit was collapsed behind that handling. Same clock as the payload QPC
    // (Stopwatch.GetTimestamp == QueryPerformanceCounter). Refreshed on every exit, skips included.
    private long _lastHandlerExit;

    public ActivationHandler(
        SharedState sharedState,
        Logger logger,
        string guardMutexName,
        Func<IConfiguration> settings,
        Func<IConfiguration, bool> runClose,
        Action dispatchHealer,
        Func<long>? timestamp = null,
        Func<long>? tickCount = null,
        Func<SharedState, bool>? restorePending = null)
    {
        _sharedState = sharedState;
        _logger = logger;
        _guardMutexName = guardMutexName;
        _settings = settings;
        _runClose = runClose;
        _dispatchHealer = dispatchHealer;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _restorePending = restorePending ?? TimeoutRepair.TryRestorePending;
        _lastHandlerExit = _timestamp();
    }

    public void HandleActivation()
    {
        try
        {
            var handlerEntry = _timestamp();
            var (launchQpc, buttonCode) = _sharedState.ConsumeActivationPayload();
            LogLatency(handlerEntry, launchQpc, buttonCode);

            var elapsedSinceLastRun = _tickCount() - _sharedState.ReadThrottleTick();

            if (elapsedSinceLastRun is >= 0 and < ThrottleThresholdMs)
            {
                _logger.Log($"Activation skipped: the previous handling was less than {ThrottleThresholdMs}ms ago.");

                return;
            }

            RunThrottledActivation();
        }
        finally
        {
            _lastHandlerExit = _timestamp();
        }
    }

    private void RunThrottledActivation()
    {
        using var guardMutex = new Mutex(initiallyOwned: false, _guardMutexName);
        var acquired = false;

        try
        {
            try
            {
                acquired = guardMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // WaitOne grants ownership when reporting an abandoned mutex.
                acquired = true;
            }

            if (!acquired)
            {
                _logger.Log("Activation skipped: the guard mutex is held by another instance.");

                return;
            }

            if (_sharedState.TryReadTimeoutRepair(out _) && _restorePending(_sharedState))
            {
                _logger.Log("Restored the foreground lock timeout before closing.");
            }

            _sharedState.WriteThrottleTick(_tickCount());

            var performedAttach = false;

            try
            {
                performedAttach = _runClose(_settings());
            }
            catch (Exception ex)
            {
                _logger.Log(ex.ToString());
            }

            if (performedAttach)
            {
                _dispatchHealer();
            }
        }
        finally
        {
            if (acquired)
            {
                guardMutex.ReleaseMutex();
            }
        }
    }

    private void LogLatency(long handlerEntry, long launchQpc, int buttonCode)
    {
        var plausible = launchQpc > 0 && launchQpc <= handlerEntry
            && Stopwatch.GetElapsedTime(launchQpc, handlerEntry) <= MaxPlausibleLatency;

        if (!plausible)
        {
            _logger.Log("Activation: latency unavailable (button unknown).");

            return;
        }

        var latency = Stopwatch.GetElapsedTime(launchQpc, handlerEntry);
        var deferred = launchQpc < _lastHandlerExit ? " (deferred)" : string.Empty;
        var button = buttonCode == TriggerButtonXButton2 ? "XButton2" : $"code {buttonCode}";
        _logger.Log($"Activation{deferred}: latency {latency.TotalMilliseconds:F1} ms (button {button}).");
    }
}
```
Note the healer-dispatch ordering divergence from the app: the app releases its guard mutex before lingering because the healer runs inline there; the daemon's healer runs on a background task, so dispatching inside the mutex scope is equivalent (the dispatch itself is instantaneous) and keeps the method simple. The heal itself never needs the guard mutex. If the spec's revise-plan pass disagrees, move `_dispatchHealer()` after the `finally`; behavior is identical either way because dispatch does not block.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet build C:/Git/TheCloser --no-incremental; dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~ActivationHandlerTests"`
Expected: PASS, all 11 cases.

- [ ] **Step 5: Commit**

```bash
git -C C:/Git/TheCloser add -A && git -C C:/Git/TheCloser commit -m "feat(shared): activation handler for the daemon IPC path"
```

---

### Task 6: DaemonRuntime (loop, startup order, drain, final repair tick)

**Files:**
- Create: `TheCloser.Shared/DaemonRuntime.cs`
- Test: `TheCloser.Tests/DaemonRuntimeTests.cs` (new)

**Interfaces:**
- Consumes: `CrashRepair.TryRepairCrashedState(SharedState, string, Logger)` (read its exact signature in `TheCloser.Shared/CrashRepair.cs` first), `ActivationHandler` shape from Task 5 (wired by the caller as a delegate).
- Produces:
```csharp
internal sealed class DaemonRuntime
{
    public DaemonRuntime(
        Logger logger,
        string memoryMappedFileName,
        string daemonMutexName,
        string activationEventName,
        string exitEventName,
        Action<SharedState> onActivation,
        Action<SharedState> watchdogTick,     // also runs once as the final repair tick
        TimeSpan watchdogInterval);

    public void Run();                        // returns when the exit event fires or a second instance loses
    public void DispatchHealer(Action heal);  // tracked background task, log-and-swallow
}
```
Behavior contract: Run pins the MMF, creates both auto-reset events, publishes the daemon mutex last, exits logging when `createdNew` is false; loops `WaitHandle.WaitAny([exitEvent, activationEvent], watchdogInterval)` where index 0 exits the loop, index 1 invokes `onActivation` under log-and-swallow, and timeout invokes `watchdogTick` under log-and-swallow; after the loop, one final `watchdogTick` under log-and-swallow, then a drain that snapshots the tracked healer tasks and waits for them, all before the using-scope unwind releases the kernel objects.

- [ ] **Step 1: Write the failing tests**

Create `TheCloser.Tests/DaemonRuntimeTests.cs`. Every wait is bounded (5 s) so a regression fails rather than hangs; every kernel object name is GUID-suffixed per test via the shipped `TestNames` helpers (reuse the exact member names found there).

```csharp
using TheCloser.Shared;
using Xunit;

namespace TheCloser.Tests;

public class DaemonRuntimeTests
{
    private static readonly TimeSpan LongInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);

    private sealed class Names
    {
        public string Map { get; } = TestNames.UniqueMapName();
        public string Mutex { get; } = TestNames.UniqueMutexName();
        public string Activation { get; } = TestNames.UniqueEventName();
        public string Exit { get; } = TestNames.UniqueEventName();
    }

    private static DaemonRuntime Build(Names n, List<string> log, Action<SharedState>? onActivation = null, Action<SharedState>? watchdogTick = null) =>
        new(TestLoggers.Capturing(log), n.Map, n.Mutex, n.Activation, n.Exit,
            onActivation ?? (_ => { }), watchdogTick ?? (_ => { }), LongInterval);

    private static (Thread Thread, Names Names, List<string> Log) Start(DaemonRuntime runtime, Names n, List<string> log)
    {
        var thread = new Thread(runtime.Run);
        thread.Start();
        Assert.True(SpinWaitFor(() => Mutex.TryOpenExisting(n.Mutex, out var m) && Dispose(m)), "daemon mutex never appeared");

        return (thread, n, log);
    }

    private static bool Dispose(Mutex m)
    {
        m.Dispose();

        return true;
    }

    private static bool SpinWaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitBudget;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(25);
        }

        return false;
    }

    private static void StopAndJoin(Names n, Thread thread)
    {
        using var exit = EventWaitHandle.OpenExisting(n.Exit);
        exit.Set();
        Assert.True(thread.Join(WaitBudget), "Run did not return after the exit signal");
    }

    [Fact]
    public void SecondInstance_LosesOnMutex_AndEventStaysSignalable()
    {
        // Arrange
        var n = new Names();
        var log = new List<string>();
        var (thread, _, _) = Start(Build(n, log), n, log);

        // Act: a second runtime with the same names must return promptly.
        var secondLog = new List<string>();
        var second = Build(n, secondLog);
        var secondThread = new Thread(second.Run);
        secondThread.Start();
        Assert.True(secondThread.Join(WaitBudget));

        // Assert: the loser logged and the survivor's activation event is still signalable.
        Assert.Contains(secondLog, line => line.Contains("already running"));
        using (var evt = EventWaitHandle.OpenExisting(n.Activation))
        {
            evt.Set();
        }
        StopAndJoin(n, thread);
    }

    [Fact]
    public void StartupOrder_MutexObservableImpliesEventsOpenable()
    {
        // Arrange
        var n = new Names();
        var log = new List<string>();
        var runtime = Build(n, log);

        // Act
        var thread = new Thread(runtime.Run);
        thread.Start();
        Assert.True(SpinWaitFor(() => Mutex.TryOpenExisting(n.Mutex, out var m) && Dispose(m)));

        // Assert: the moment the mutex is observable, both events must already exist.
        using (var a = EventWaitHandle.OpenExisting(n.Activation)) { }
        using (var e = EventWaitHandle.OpenExisting(n.Exit)) { }
        StopAndJoin(n, thread);
    }

    [Fact]
    public void Activation_InvokesHandler_AndExceptionIsSwallowed()
    {
        // Arrange
        var n = new Names();
        var log = new List<string>();
        var invocations = 0;
        var runtime = Build(n, log, onActivation: _ =>
        {
            invocations++;
            throw new InvalidOperationException("handler boom");
        });
        var (thread, _, _) = Start(runtime, n, log);

        // Act: two signals; the loop must survive the first throw to observe the second.
        using (var evt = EventWaitHandle.OpenExisting(n.Activation))
        {
            evt.Set();
            Assert.True(SpinWaitFor(() => invocations == 1));
            evt.Set();
            Assert.True(SpinWaitFor(() => invocations == 2));
        }

        // Assert
        Assert.Contains(log, line => line.Contains("handler boom"));
        StopAndJoin(n, thread);
    }

    [Fact]
    public void FinalRepairTick_RunsAfterLoopExit()
    {
        // Arrange: interval far above the test duration, so the only tick is the final one.
        var n = new Names();
        var log = new List<string>();
        var ticks = 0;
        var runtime = Build(n, log, watchdogTick: _ => ticks++);
        var (thread, _, _) = Start(runtime, n, log);

        // Act
        StopAndJoin(n, thread);

        // Assert
        Assert.Equal(1, ticks);
    }

    [Fact]
    public void FinalRepairTick_ThrowIsSwallowed_DrainStillRuns()
    {
        // Arrange
        var n = new Names();
        var log = new List<string>();
        var healRan = false;
        var runtime = Build(n, log, watchdogTick: _ => throw new InvalidOperationException("tick boom"));
        var (thread, _, _) = Start(runtime, n, log);
        runtime.DispatchHealer(() =>
        {
            Thread.Sleep(100);
            healRan = true;
        });

        // Act
        StopAndJoin(n, thread);

        // Assert: the throwing final tick was logged and did not skip the drain.
        Assert.Contains(log, line => line.Contains("tick boom"));
        Assert.True(healRan);
    }

    [Fact]
    public void Drain_WaitsForDispatchedHeal_IncludingThrowingHeal()
    {
        // Arrange
        var n = new Names();
        var log = new List<string>();
        var slowHealDone = false;
        var runtime = Build(n, log);
        var (thread, _, _) = Start(runtime, n, log);
        runtime.DispatchHealer(() =>
        {
            Thread.Sleep(100);
            slowHealDone = true;
        });
        runtime.DispatchHealer(() => throw new InvalidOperationException("heal boom"));

        // Act
        StopAndJoin(n, thread);

        // Assert
        Assert.True(slowHealDone);
        Assert.Contains(log, line => line.Contains("heal boom"));
    }
}
```
As in Task 5, `TestLoggers.Capturing` and the `TestNames.Unique*` members stand for the shipped helpers; read `TestNames.cs` and `TempLogger.cs` first and substitute the real names, adding a `UniqueEventName()` member to `TestNames` in this task if none exists (same GUID-suffix pattern as its siblings).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: FAIL with CS0246 (`DaemonRuntime` not found).

- [ ] **Step 3: Implement**

Create `TheCloser.Shared/DaemonRuntime.cs`:

```csharp
using System.Collections.Concurrent;

namespace TheCloser.Shared;

// Daemon lifetime shell: startup ordering, the single-threaded WaitAny loop, healer task
// tracking, the final repair tick, and the drain-before-unwind shutdown ordering. See the fix
// design's Daemon lifecycle section for why each ordering is load-bearing.
internal sealed class DaemonRuntime
{
    private readonly Logger _logger;
    private readonly string _memoryMappedFileName;
    private readonly string _daemonMutexName;
    private readonly string _activationEventName;
    private readonly string _exitEventName;
    private readonly Action<SharedState> _onActivation;
    private readonly Action<SharedState> _watchdogTick;
    private readonly TimeSpan _watchdogInterval;

    // Thread-safe: completions land on thread-pool threads while the loop thread adds.
    private readonly ConcurrentDictionary<Task, byte> _healerTasks = new();

    public DaemonRuntime(
        Logger logger,
        string memoryMappedFileName,
        string daemonMutexName,
        string activationEventName,
        string exitEventName,
        Action<SharedState> onActivation,
        Action<SharedState> watchdogTick,
        TimeSpan watchdogInterval)
    {
        _logger = logger;
        _memoryMappedFileName = memoryMappedFileName;
        _daemonMutexName = daemonMutexName;
        _activationEventName = activationEventName;
        _exitEventName = exitEventName;
        _onActivation = onActivation;
        _watchdogTick = watchdogTick;
        _watchdogInterval = watchdogInterval;
    }

    public void Run()
    {
        // Startup order is load-bearing: MMF pin, both events, then the mutex last, so the mutex
        // proves everything a press or a --stop needs already exists. A losing second instance
        // briefly co-owns both auto-reset events; harmless.
        using var sharedState = new SharedState(_memoryMappedFileName);
        using var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _activationEventName);
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _exitEventName);
        using var mutex = new Mutex(true, _daemonMutexName, out var createdNew);

        if (!createdNew)
        {
            _logger.Log("Daemon is already running. Exiting...");

            return;
        }

        RunLoop(sharedState, exitEvent, activationEvent);

        // Final repair tick: a pending foreground-lock repair record must not die with the MMF
        // pin on a graceful stop. Safe by construction (repairs only under an acquirable guard
        // mutex); a throw is logged and the drain and unwind still run.
        RunIsolated(() => _watchdogTick(sharedState));
        DrainHealers();

        _logger.Log("Daemon STOP signal received. Exiting...");
    }

    public void DispatchHealer(Action heal)
    {
        Task? task = null;
        task = Task.Run(() =>
        {
            RunIsolated(heal);

            if (task is not null)
            {
                _healerTasks.TryRemove(task, out _);
            }
        });
        _healerTasks.TryAdd(task, 0);
    }

    private void RunLoop(SharedState sharedState, EventWaitHandle exitEvent, EventWaitHandle activationEvent)
    {
        WaitHandle[] handles = [exitEvent, activationEvent];

        while (true)
        {
            var signaled = WaitHandle.WaitAny(handles, _watchdogInterval);

            if (signaled == 0)
            {
                return;
            }

            if (signaled == 1)
            {
                RunIsolated(() => _onActivation(sharedState));
            }
            else
            {
                RunIsolated(() => _watchdogTick(sharedState));
            }
        }
    }

    private void DrainHealers()
    {
        var outstanding = _healerTasks.Keys.ToArray();

        if (outstanding.Length > 0)
        {
            Task.WaitAll(outstanding);
        }
    }

    private void RunIsolated(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Log(ex.ToString());
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet build C:/Git/TheCloser --no-incremental; dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~DaemonRuntimeTests"`
Expected: PASS, all 6 cases.

- [ ] **Step 5: Commit**

```bash
git -C C:/Git/TheCloser add -A && git -C C:/Git/TheCloser commit -m "feat(shared): daemon runtime loop with drain and final repair tick"
```

---

### Task 7: Wire the daemon and app composition roots

**Files:**
- Modify: `TheCloser.Daemon/Program.cs`
- Modify: `TheCloser/Program.cs` (only if Task 2's constant edit left anything; otherwise untouched here)

**Interfaces:**
- Consumes: everything from Tasks 2 through 6 with the exact signatures above.

- [ ] **Step 1: Rewrite TheCloser.Daemon/Program.cs**

Replace the `Run()` method and supporting members with a composition root (keep `Main`, `SignalExit`, and the argument dispatch exactly as they are). The `onActivation` and `watchdogTick` delegates receive the `SharedState` that `DaemonRuntime.Run` created, so the handler is built lazily on the first activation. The exact final shape:

```csharp
    private static void Run()
    {
        var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        var liveRoot = BuildConfiguration(exeDirectory);
        var lastGood = new LastGoodConfiguration();
        ActivationHandler? handler = null;
        DaemonRuntime? runtime = null;

        runtime = new DaemonRuntime(
            Logger,
            MemoryMappedFileName,
            DaemonMutexName,
            ActivationEventName,
            DaemonExitEventName,
            onActivation: sharedState =>
            {
                handler ??= new ActivationHandler(
                    sharedState,
                    Logger,
                    GuardMutexName,
                    settings: () => lastGood.Refresh(liveRoot, Logger.Log),
                    runClose: snapshot =>
                    {
                        var closer = new WindowCloser(snapshot, sharedState, Logger);
                        closer.CloseWindowUnderCursor();

                        return closer.PerformedInputAttach;
                    },
                    dispatchHealer: () => runtime!.DispatchHealer(() => new TriggerButtonHealer(Logger).HealStuckButtons()));
                handler.HandleActivation();
            },
            watchdogTick: RepairIfCrashed,
            WatchdogInterval);

        runtime.Run();

        if (liveRoot is IDisposable disposableRoot)
        {
            disposableRoot.Dispose();
        }
    }

    private static IConfigurationRoot BuildConfiguration(string exeDirectory) => new ConfigurationBuilder()
        .SetBasePath(exeDirectory)
        .AddJsonFile(source =>
        {
            source.Path = "appsettings.json";
            source.Optional = true;
            source.ReloadOnChange = true;
            // Parse failures only: the provider opens the file outside this handler's reach, so
            // an open failure faults the framework's discarded watcher task instead (accepted;
            // see the fix design's Configuration section).
            source.OnLoadException = context =>
            {
                Logger.Log($"Configuration reload failed: {context.Exception.Message}");
                context.Ignore = true;
            };
            source.ResolveFileProvider();
        })
        .Build();

    private static void RepairIfCrashed(SharedState sharedState)
    {
        if (CrashRepair.TryRepairCrashedState(sharedState, GuardMutexName, Logger))
        {
            Logger.Log("Restored the foreground lock timeout after a detected app crash.");
        }
    }
```
Add `using Microsoft.Extensions.Configuration;` and `using Microsoft.Extensions.Configuration.Json;` as needed. Note the config-root disposal after `Run()` returns and before `Main`'s `finally` disposes the Logger, matching the spec's unwind ordering. Verify the exact `AddJsonFile(Action<JsonConfigurationSource>)` overload and the `ResolveFileProvider()` requirement against the installed 10.0.9 package source or docs before writing; if the action overload does not honor `SetBasePath` even with `ResolveFileProvider()`, fall back to constructing the `JsonConfigurationSource` explicitly and calling `builder.Add(source)`.

- [ ] **Step 2: Build, run the touching test suites**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: Build succeeded.

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~DaemonRuntimeTests|FullyQualifiedName~ActivationHandlerTests|FullyQualifiedName~CrashRepairTests|FullyQualifiedName~TimeoutRepairTests"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git -C C:/Git/TheCloser add -A && git -C C:/Git/TheCloser commit -m "feat(daemon): host the IPC activation pipeline"
```

---

### Task 8: TheCloser.ahk rewrite

**Files:**
- Replace: `TheCloser.ahk`

- [ ] **Step 1: Replace the script**

One load-bearing constraint first: `TheCloser.Daemon.exe --start` runs the daemon loop and does not exit until the daemon stops, so `--start` must be launched with `Run` (fire and forget), while `--stop` is a short-lived stopper and is awaited with `RunWait`. Write `TheCloser.ahk` with exactly this content (AHK v1; ASCII only; CRLF endings preserved by Git as before):

```autohotkey
#SingleInstance Force

; ==== Hand-synchronized copies of TheCloser.Shared/Constants.cs values (AutoHotkey cannot
; ==== consume C#): kernel object names, payload MMF offsets (SharedState.cs), button code.
global DaemonMutexName := "TheCloserDaemonMutex"
global ActivationEventName := "TheCloserActivationEvent"
global SharedStateName := "TheCloserSharedState"
global ActivationQpcOffset := 16
global ActivationButtonOffset := 24
global TriggerButtonXButton2 := 1

; ==== Auto-execute: replace any running daemon with the freshly deployed binary (upgrade
; transition, and any unelevated daemon left by manual testing). The --stop is awaited so a
; late stopper can never kill the daemon started below; both mutex polls are bounded at
; 5000 ms (above the measured 0.7-2.5 s process-creation window and the ~2 s drain). Every
; branch ends with no handle held; a launch failure is silent (headless at logon) and leaves
; the degraded mode, which self-heals through per-press fallback.
Loop, 2 {
    RunWait, "%A_ScriptDir%\TheCloser.Daemon.exe" --stop, %A_ScriptDir%, UseErrorLevel
    if (ErrorLevel = "ERROR")
        Break
    if (DaemonMutexGone(5000)) {
        Run, "%A_ScriptDir%\TheCloser.Daemon.exe" --start, %A_ScriptDir%, UseErrorLevel
        DaemonMutexAppeared(5000)  ; Non-diagnostic: expiry just means degraded until it publishes.
        Break
    }
}
Return

; Polls until the daemon mutex name no longer exists. Opens the mutex only to test existence
; and closes the handle before sleeping (a held handle would pin the name past daemon exit).
DaemonMutexGone(timeoutMs) {
    global DaemonMutexName
    deadline := A_TickCount + timeoutMs
    Loop {
        handle := DllCall("OpenMutex", "UInt", 0x00100000, "Int", 0, "Str", DaemonMutexName, "Ptr") ; SYNCHRONIZE
        if (!handle)
            Return 1
        DllCall("CloseHandle", "Ptr", handle)
        if (A_TickCount > deadline)
            Return 0
        Sleep, 100
    }
}

DaemonMutexAppeared(timeoutMs) {
    global DaemonMutexName
    deadline := A_TickCount + timeoutMs
    Loop {
        handle := DllCall("OpenMutex", "UInt", 0x00100000, "Int", 0, "Str", DaemonMutexName, "Ptr")
        if (handle) {
            DllCall("CloseHandle", "Ptr", handle)
            Return 1
        }
        if (A_TickCount > deadline)
            Return 0
        Sleep, 100
    }
}

XButton2:: ; Mouse5 is XButton2 in AHK
DllCall("QueryPerformanceCounter", "Int64*", LaunchQpc)
; EVENT_MODIFY_STATE = 0x0002. Open per press, never cached: the name existing is the exact
; daemon-alive signal (see the fix design's daemon-down detection).
hEvent := DllCall("OpenEvent", "UInt", 0x0002, "Int", 0, "Str", ActivationEventName, "Ptr")
if (!hEvent) {
    ; Fallback: today's slow-but-working path; also starts a daemon for the next press.
    Run, "%A_ScriptDir%\TheCloser.exe" --probe-launch-qpc %LaunchQpc%, %A_ScriptDir%
    Return
}
; FILE_MAP_WRITE = 0x0002. Payload before signal (value-before-flag); offsets mirror
; SharedState.cs. On mapping failure, still signal: consume-once zeroing makes the daemon log
; latency unavailable rather than replaying a stale payload.
hMap := DllCall("OpenFileMapping", "UInt", 0x0002, "Int", 0, "Str", SharedStateName, "Ptr")
if (hMap) {
    pView := DllCall("MapViewOfFile", "Ptr", hMap, "UInt", 0x0002, "UInt", 0, "UInt", 0, "UPtr", 0, "Ptr")
    if (pView) {
        NumPut(LaunchQpc, pView + ActivationQpcOffset, 0, "Int64")
        NumPut(TriggerButtonXButton2, pView + ActivationButtonOffset, 0, "Int")
        DllCall("UnmapViewOfFile", "Ptr", pView)
    }
    DllCall("CloseHandle", "Ptr", hMap)
}
DllCall("SetEvent", "Ptr", hEvent)
DllCall("CloseHandle", "Ptr", hEvent)
Return
```
- [ ] **Step 2: Byte sweep**

Run (Git Bash): `rg --crlf -n "[^ -~\t]" /c/Git/TheCloser/TheCloser.ahk`
Expected: no output (exit 1 is the zero-match success signal).

- [ ] **Step 3: Commit**

```bash
git -C C:/Git/TheCloser add TheCloser.ahk && git -C C:/Git/TheCloser commit -m "feat(ahk): IPC trigger with daemon stop-start auto-execute"
```

---

### Task 9: install-elevated-ahk.ps1 rewrite

**Files:**
- Modify: `install-elevated-ahk.ps1`

- [ ] **Step 1: Rewrite**

Keep the param block (minus `-StartNow`), the `#Requires -RunAsAdministrator` line, the rationale comment (updated), and the existence checks. Replace everything from the `$action = ...` line down with:

```powershell
$action = New-ScheduledTaskAction -Execute $AhkExePath -Argument "`"$AhkScriptPath`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Output "Registered scheduled task '$TaskName' (at logon, elevated, user $env:USERNAME)."

function Wait-TaskState {
    param([string] $Name, [bool] $Running, [int] $TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $state = (Get-ScheduledTask -TaskName $Name).State
        if (($state -eq 'Running') -eq $Running) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

# Stop the task first: its logon instance would make the later start a silent no-op under the
# default IgnoreNew multiple-instances policy.
if ((Get-ScheduledTask -TaskName $TaskName).State -eq 'Running') {
    Stop-ScheduledTask -TaskName $TaskName
    if (-not (Wait-TaskState -Name $TaskName -Running $false -TimeoutSeconds 10)) {
        [Console]::Error.WriteLine("Task '$TaskName' did not leave the Running state within 10 seconds.")
        exit 1
    }
}

# Sweep every AutoHotkey instance running the script, task-hosted or not, matched by command
# line leaf: this covers instances launched outside the task or from a differently pathed copy,
# which #SingleInstance Force's same-path replacement would miss, and preserves the old
# replace-without-logoff behavior.
$scriptLeaf = Split-Path $AhkScriptPath -Leaf
Get-CimInstance Win32_Process -Filter "Name like 'AutoHotkey%'" |
    Where-Object { $_.CommandLine -like "*$scriptLeaf*" } |
    ForEach-Object {
        Write-Output "Stopping AutoHotkey PID $($_.ProcessId) running $scriptLeaf."
        Stop-Process -Id $_.ProcessId -Force
    }

Start-ScheduledTask -TaskName $TaskName
if (-not (Wait-TaskState -Name $TaskName -Running $true -TimeoutSeconds 5)) {
    [Console]::Error.WriteLine("Task '$TaskName' did not reach the Running state within 5 seconds of the start.")
    exit 1
}
Write-Output "Started task '$TaskName'."
```
Update the header comment: remove the sentence about `-StartNow`, state that the script now always registers, stops, sweeps, starts, and fails with a nonzero exit on a failed poll.

- [ ] **Step 2: Syntax check**

Run: `pwsh -NoProfile -Command "[void][System.Management.Automation.Language.Parser]::ParseFile('C:/Git/TheCloser/install-elevated-ahk.ps1', [ref]$null, [ref]$err); $err ? ($err | ForEach-Object Message) : 'parse-ok'"`
Expected: `parse-ok`.

- [ ] **Step 3: Commit**

```bash
git -C C:/Git/TheCloser add install-elevated-ahk.ps1 && git -C C:/Git/TheCloser commit -m "feat(setup): unconditional install with bounded task polls"
```

---

### Task 10: deploy.ps1 task restart

**Files:**
- Modify: `deploy.ps1`

- [ ] **Step 1: Append the invocation-layer restart**

After the final `Copy-Item ... 'install-elevated-ahk.ps1' ...` line in `deploy.ps1`, append:

```powershell

# Restart the elevated AutoHotkey task so the just-copied script (and, via its auto-execute,
# the just-copied daemon) take over without a manual step. Task-state polls sidestep process
# inspection: an unelevated shell cannot read an elevated process's command line. A discarded
# start is excluded structurally: IgnoreNew only discards while an instance runs, and the
# stop-poll proves none does before the start is issued.
$TaskName = 'TheCloser AutoHotkey (elevated)'

function Wait-TaskState {
    param([string] $Name, [bool] $Running, [int] $TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $state = (Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue).State
        if (($state -eq 'Running') -eq $Running) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

$Task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

if ($Task) {
    if ($Task.State -eq 'Running') {
        Stop-ScheduledTask -TaskName $TaskName
        if (-not (Wait-TaskState -Name $TaskName -Running $false -TimeoutSeconds 10)) {
            [Console]::Error.WriteLine("Task '$TaskName' did not leave Running within 10 seconds. Nothing restarted.")
            exit 1
        }
    }
    Start-ScheduledTask -TaskName $TaskName
    if (-not (Wait-TaskState -Name $TaskName -Running $true -TimeoutSeconds 5)) {
        [Console]::Error.WriteLine("Task '$TaskName' did not return to Running within 5 seconds of the start.")
        exit 1
    }
    Write-Output "Restarted task '$TaskName'."
}
else {
    # First deploy on this machine: register through the deployed installer copy so the task
    # binds to the deploy target's script, not this working tree. One UAC prompt, once per machine.
    Start-Process pwsh -Verb RunAs -Wait -ArgumentList '-NoProfile', '-File', (Join-Path $Destination 'install-elevated-ahk.ps1')
    $Task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if (-not $Task -or -not (Wait-TaskState -Name $TaskName -Running $true -TimeoutSeconds 5)) {
        [Console]::Error.WriteLine("First-deploy install did not leave task '$TaskName' running. Nothing verified.")
        exit 1
    }
    Write-Output "Installed and started task '$TaskName'."
}
```
Note the stop-side elevation live-claim: if `Stop-ScheduledTask` fails with access denied from the unelevated shell, the recorded fallback is folding the stop and start into the same `Start-Process pwsh -Verb RunAs -Wait` child pattern already used above; implement the fallback only if the first real deploy hits it, and log which path ran.

- [ ] **Step 2: Syntax check**

Run: `pwsh -NoProfile -Command "[void][System.Management.Automation.Language.Parser]::ParseFile('C:/Git/TheCloser/deploy.ps1', [ref]$null, [ref]$err); $err ? ($err | ForEach-Object Message) : 'parse-ok'"`
Expected: `parse-ok`.

- [ ] **Step 3: Commit**

```bash
git -C C:/Git/TheCloser add deploy.ps1 && git -C C:/Git/TheCloser commit -m "feat(deploy): automatic elevated task restart with bounded polls"
```

---

### Task 11: Full project test pass and hygiene sweep

- [ ] **Step 1: Build clean and run the whole test project**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: Build succeeded, no new warnings versus the pre-task-1 baseline.

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build`
(The test project IS the filter; this is not the forbidden solution-wide unfiltered run.)
Expected: all tests pass.

- [ ] **Step 2: Cross-language sync sweep**

Run (Git Bash):
```bash
grep -n "TheCloserDaemonMutex\|TheCloserActivationEvent\|TheCloserSharedState\|ActivationQpcOffset\|ActivationButtonOffset" /c/Git/TheCloser/TheCloser.ahk /c/Git/TheCloser/TheCloser.Shared/Constants.cs /c/Git/TheCloser/TheCloser.Shared/SharedState.cs
```
Expected: names and offset values 16/24 agree across all three files, each site carrying its keep-in-sync comment.

- [ ] **Step 3: Commit any stragglers**

```bash
git -C C:/Git/TheCloser status --short
```
Expected: clean apart from the plan file and `.tmp/`; commit nothing here if clean.

---

## Post-implementation (owned by the handover pipeline, not this plan)

Code review (`/nightshift:revise-code`), end-to-end verification per the spec's Testing and Verification criteria sections (deploy first; elevated e2e drive; latency gate including after-idle samples; live-claim probes), docs pass (CLAUDE.md architecture and centralization note), backlog bookkeeping, and the morning report.
