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

- All commands below run under **PowerShell 7 (`pwsh -NoProfile`)** unless a step names another shell. Every command is self-contained; no shell state carries between steps. Where a step builds and then tests on one line, the two are joined with `&&`, never `;`: PowerShell's `;` does not short-circuit, and `dotnet test --no-build` would then run the previous task's still-present assembly and print a pass over a failed build.
- Build with `dotnet build C:/Git/TheCloser --no-incremental`; run tests with `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "<filter>"`. Never run the full unfiltered suite.
- No em-dashes, en-dashes, or emoji in any generated text. All new file content is pure ASCII; after editing `TheCloser.ahk` or any file where escape sequences matter, byte-sweep with `rg --crlf -n "[^ -~\t]" <file>` (Git Bash) and expect zero matching lines.
- Kernel object names stay session-local (no `Global\` prefix) and centralized in `TheCloser.Shared/Constants.cs`; the one sanctioned non-C# copy is `TheCloser.ahk`, comment-marked at every duplication site.
- Commit subjects follow Conventional Commits, max 72 chars, subject-only (no body, no Co-Authored-By trailer).
- C# style: blank line before `return`, block braces always, `required` first, pattern matching and collection expressions where natural, Arrange/Act/Assert comments in tests, no `.ToLower()` comparisons.
- The plan file itself is never staged in any implementation commit: a commit step either stages with the plan-excluding pathspec `git add -A -- ':!.claude/plans'` or names its single file explicitly (the three invocation-layer commits stage `TheCloser.ahk`, `install-elevated-ahk.ps1`, and `deploy.ps1` by name).
- Deliberate deferral: the spec's strongly preferred co-requisite, the "Logger rotation only runs at construction" quick win, is NOT part of this plan; unbounded daemon-log growth is the accepted interim per the spec, and that quick win lands as its own change.
- Deliberate deferral: deploy.ps1's existing daemon hard-kill stays as-is in this plan's deploy edits; replacing it with a graceful `--stop` plus `Wait-Process` inside the same elevated child is the deploy-side half of a queued quick win per the spec (its app-side half is superseded by the daemon IPC shape), landing as its own change.
- Tests must never touch the real kernel object names or the real SystemParametersInfo setting: every test uses GUID-suffixed names via the existing `TestNames` helper and injected delegates.

---

## File structure

| File | Responsibility |
|---|---|
| `TheCloser.Shared/WindowCloser.cs` (+9 siblings, moved) | Close pipeline, relocated verbatim except namespace, detach seam, and healer comment |
| `TheCloser.Shared/Constants.cs` | Gains `ActivationEventName`, button codes, shared throttle threshold |
| `TheCloser.Shared/SharedState.cs` | Gains activation payload accessors (offsets 16/24, consume-once) |
| `TheCloser.Shared/LastGoodConfiguration.cs` (new) | Per-activation value-copy snapshot of the live configuration root |
| `TheCloser.Shared/DaemonConfiguration.cs` (new) | Builds the daemon's hot-reloading configuration root with logged, swallowed parse failures |
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

The affected files, enumerated against the pre-move tree (do not rediscover by grep: no test file carries a `using TheCloser;` directive; moved-type references resolve through the enclosing `TheCloser` namespace of `namespace TheCloser.Tests;`, a resolution the move silently breaks). Per-file edits:

- `WindowCloserTests.cs`: no edit (it already has `using TheCloser.Shared;` and no using-static lines).
- `ForegroundActivatorTests.cs`: already has `using TheCloser.Shared;`; rewrite `using static TheCloser.NativeMethods;` to `using static TheCloser.Shared.NativeMethods;` and `using static TheCloser.TitleBarClickPosition;` to `using static TheCloser.Shared.TitleBarClickPosition;`.
- `TriggerButtonHealerTests.cs`: add `using TheCloser.Shared;` above the using-static block, and rewrite `using static TheCloser.NativeMethods;` to `using static TheCloser.Shared.NativeMethods;`.
- `ProcessSettingsParserTests.cs`: add `using TheCloser.Shared;` after `using Microsoft.Extensions.Configuration;`.

`InvocationProbeTests.cs` needs no edit: it tests the app-side `InvocationProbe`, still reached through the enclosing namespace. Then verify the edits landed:

Run (Git Bash): `grep -ln "^using TheCloser.Shared;$" /c/Git/TheCloser/TheCloser.Tests/WindowCloserTests.cs /c/Git/TheCloser/TheCloser.Tests/ForegroundActivatorTests.cs /c/Git/TheCloser/TheCloser.Tests/TriggerButtonHealerTests.cs /c/Git/TheCloser/TheCloser.Tests/ProcessSettingsParserTests.cs`
Expected: all four files listed.

Run (Git Bash): `grep -rnF -e "using static TheCloser.NativeMethods;" -e "using static TheCloser.TitleBarClickPosition;" /c/Git/TheCloser/TheCloser.Tests/`
Expected: no output, exit code 1 (the `.Shared.` forms do not match these fixed strings). Completeness beyond the usings is gated by Step 6's build: any remaining bare reference to a moved type fails compilation, because the enclosing-namespace resolution no longer reaches it.

- [ ] **Step 6: Build and run the relocation regression net**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: Build succeeded, `0 Warning(s)` (the pre-plan tree builds with zero warnings, verified while writing the plan, so zero is the literal bar).

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~WindowCloserTests|FullyQualifiedName~ForegroundActivatorTests|FullyQualifiedName~TriggerButtonHealerTests|FullyQualifiedName~ProcessSettingsParserTests"`
Expected: all listed tests pass, none skipped.

- [ ] **Step 7: Commit**

```bash
git -C C:/Git/TheCloser add -A -- ':!.claude/plans' && git -C C:/Git/TheCloser commit -m "refactor(shared): relocate close pipeline for daemon hosting"
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

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: FAIL with CS1061 (`SharedState` contains no definition for `WriteActivationPayload`, an instance-member call) and CS0117 (`Constants` contains no definition for `TriggerButtonXButton2` / `TriggerButtonUnknown`, static-member accesses); that compile failure is this step's red state. The build runs alone here: chaining a test run after it with `;` would not short-circuit in PowerShell, and `--no-build` would then execute Task 1's still-present assembly, printing a green pass over the red state.

- [ ] **Step 3: Implement**

In `TheCloser.Shared/Constants.cs`, after the line `public const string ProbeLogMutexName = "TheCloserProbeLogMutex";`, insert:

```csharp
    // Duplicated by hand in TheCloser.ahk (AutoHotkey cannot consume this file); keep in sync.
    public const string ActivationEventName = "TheCloserActivationEvent";

    // Shared between the app's fallback path and the daemon's IPC path.
    public const long ThrottleThresholdMs = 200;

    // Trigger button codes for the activation payload. Duplicated by hand in TheCloser.ahk
    // (AutoHotkey cannot consume this file); keep in sync.
    public const int TriggerButtonUnknown = 0;
    public const int TriggerButtonXButton2 = 1;
```

Then mark the two pre-existing constants that Task 8 makes the AutoHotkey script a second holder of, so every duplication site carries the comment the spec's cross-language contract requires. Prefix each of these existing lines with the comment shown:

```csharp
    // Duplicated by hand in TheCloser.ahk (AutoHotkey cannot consume this file); keep in sync.
    public const string DaemonMutexName = "TheCloserDaemonMutex";
```

```csharp
    // Duplicated by hand in TheCloser.ahk (AutoHotkey cannot consume this file); keep in sync.
    public const string MemoryMappedFileName = "TheCloserSharedState";
```

(The lines themselves are unchanged; only the comment above each is new. `DaemonExitEventName` and `GuardMutexName` stay unmarked: the script never names them.)

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

Run: `dotnet build C:/Git/TheCloser --no-incremental && dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~SharedStateTests|FullyQualifiedName~InvocationProbeTests"`
Expected: PASS, all cases.

- [ ] **Step 5: Commit**

```bash
git -C C:/Git/TheCloser add -A -- ':!.claude/plans' && git -C C:/Git/TheCloser commit -m "feat(shared): activation payload region and shared constants"
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

Two comments in this region are deliberately untouched by the block above, which shows method bodies only. Keep the four-line rationale comment that sits between the two methods in the shipped file, beginning `// Foreground rights belong to the thread that received the user's last input (the current` and ending `// the owner shares the target's thread: the target attach already covers that queue.`, exactly as it is: it explains why the owner attach exists, and the signature change does not affect it. Splice the new bodies around it rather than pasting over the span.

- [ ] **Step 2: Update the fake and add the new test**

In `TheCloser.Tests/ForegroundActivatorTests.cs`, update exactly two `FakeNativeApi` members. Under the id-recording detach, any test that leaves `ThreadIdOf` at its default `handle => (uint)handle` keeps its recorded strings (`detach:200`, `detach:100`); exactly one existing case overrides `ThreadIdOf` and is updated below.

```csharp
        public bool DetachSucceeds { get; set; } = true;

        public uint AttachThreadInput(IntPtr hWnd)
        {
            Calls.Add($"attach:{hWnd}");

            return AttachSucceeds ? ThreadIdOf(hWnd) : 0;
        }

        public bool DetachThreadInput(uint threadId)
        {
            Calls.Add($"detach:{threadId}");

            return DetachSucceeds;
        }
```
(The `DetachSucceeds` property is new; the two methods replace the current `bool AttachThreadInput(IntPtr)` / `bool DetachThreadInput(IntPtr)` pair. Do not touch the other fake members.)

One existing case needs its expectation updated: `TryActivate_OwnerSharesTargetThread_SkipsTheOwnerAttach` overrides `ThreadIdOf` to `_ => 7u`, so the target attach returns thread id 7 and the detach records that captured id. Change its expected array from `new[] { "attach:200", "setForeground:200", "detach:200" }` to `new[] { "attach:200", "setForeground:200", "detach:7" }`. The other four sequence-asserting cases use the default `ThreadIdOf` and pass as written.

Then add one new case, using the class's existing `_tempLogger`/`CreateActivator` fixtures:

```csharp
    [Fact]
    public async Task TryActivate_DetachesByCapturedIds_AndLogsFailedDetach()
    {
        // Arrange: distinct owner and target thread ids, and detaches that fail, simulating a
        // target window destroyed mid-close.
        var native = new FakeNativeApi
        {
            ForegroundWindow = OwnerWindow,
            ThreadIdOf = handle => handle == TargetWindow ? 42u : 7u,
            DetachSucceeds = false,
            GetWindowRectSucceeds = false
        };
        var activator = CreateActivator(native);

        // Act
        activator.TryActivate(TargetWindow, Left);
        await _tempLogger.DrainAsync();

        // Assert: both captured ids were detached (never re-resolved from the window), and the
        // failed returns were logged rather than discarded.
        Assert.Contains("detach:42", native.Calls);
        Assert.Contains("detach:7", native.Calls);
        Assert.Contains("DetachThreadInput(42)", File.ReadAllText(_tempLogger.LogPath));
    }
```

- [ ] **Step 3: Build and run**

(Deliberate TDD deviation, this task only: the seam change is a compile-breaking interface signature change, so no test-first red state is runnable; a test written first fails the build on the fake's old signatures, which exercises no behavior. The runtime gate is the updated shared-thread case plus the new case's failed-detach logging assertions; the captured-id-versus-re-resolve distinction is structural, enforced by the seam no longer accepting a window handle at detach, and is verified by reading the Step 1 diff.)

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: Build succeeded.

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~ForegroundActivatorTests"`
Expected: PASS, including the new case and the updated shared-thread case.

- [ ] **Step 4: Commit**

```bash
git -C C:/Git/TheCloser add -A -- ':!.claude/plans' && git -C C:/Git/TheCloser commit -m "fix(activator): detach input queues by captured thread ids"
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

Run: `dotnet build C:/Git/TheCloser --no-incremental && dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~LastGoodConfigurationTests"`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git -C C:/Git/TheCloser add -A -- ':!.claude/plans' && git -C C:/Git/TheCloser commit -m "feat(shared): last-good configuration snapshot"
```

---

### Task 5: ActivationHandler

**Files:**
- Create: `TheCloser.Shared/ActivationHandler.cs`
- Test: `TheCloser.Tests/ActivationHandlerTests.cs` (new)

**Interfaces:**
- Consumes: `SharedState.ConsumeActivationPayload()` and `Constants.TriggerButtonXButton2` (Task 2), `Constants.ThrottleThresholdMs` (Task 2), and `TimeoutRepair.TryRestorePending(SharedState sharedState, Func<uint, bool>? restore = null)` (the shipped two-parameter signature; the handler wraps it in a one-parameter lambda, never a method-group conversion, which C# rejects across the optional parameter).
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
        Func<long>? tickCount = null,           // Environment.TickCount64
        Func<SharedState, bool>? restorePending = null,  // default wraps TimeoutRepair.TryRestorePending
        long? initialHandlerExit = null);        // daemon start QPC; defaults to construction time

    public void HandleActivation();
}
```
Behavior contract (each clause is a test): consume payload at handler entry; latency line with plausibility guard (zero / non-positive / future / over 10 s logs unavailable, button then logs unknown); deferred marker when a plausible QPC predates the stored handler-exit timestamp (initialized to `initialHandlerExit` when the caller supplies one and to construction time otherwise, refreshed at every handler exit including skips). The spec pins this baseline to the daemon's own start time, and the daemon builds its handler lazily on the first activation, so a caller that lets the baseline default would mark every daemon lifetime's first press deferred; Task 7 supplies the daemon start QPC for exactly that reason; throttle skip inside threshold (log, no close); guard mutex created unowned per activation, `WaitOne(0)`, busy skips with log, `AbandonedMutexException` counts as acquired, release and dispose in `finally`; pending repair restored after acquiring; throttle tick written before the close; `runClose` exception logged and swallowed; `dispatchHealer` invoked exactly when `runClose` returned true.

- [ ] **Step 1: Write the failing tests**

Create `TheCloser.Tests/ActivationHandlerTests.cs`. The harness uses the shipped helpers exactly as they exist: `TestNames.UniqueMapName()`, `TestNames.UniqueMutexName()`, and `TempLogger` (whose `DrainAsync()` disposes the wrapped `Logger`, flushing its asynchronous writer, after which the log is read from `LogPath`; log assertions therefore always come last in a test, and each test drains at most once). The handler takes `restorePending` injected so no test touches SystemParametersInfo.

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using Xunit;

using static TheCloser.Shared.Constants;

namespace TheCloser.Tests;

public class ActivationHandlerTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);

    private sealed class Harness : IDisposable
    {
        public SharedState State { get; } = new(TestNames.UniqueMapName());
        public string MutexName { get; } = TestNames.UniqueMutexName();
        public TempLogger TempLogger { get; } = new();
        public List<string> Events { get; } = [];
        public long Now = Stopwatch.GetTimestamp();
        public long? Baseline;                    // null lets the handler default to construction time
        public long Tick = 100_000;
        public bool CloseResult;
        public bool CloseThrows;
        public bool RestoreResult = true;

        public ActivationHandler Build() => new(
            State,
            TempLogger.Logger,
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
            },
            initialHandlerExit: Baseline);

        public void AdvancePastThrottle() => Tick += ThrottleThresholdMs + 1;

        // Drains (disposes) the logger, then reads the whole log. Call at most once, always last.
        public async Task<string> ReadLogAsync()
        {
            await TempLogger.DrainAsync();

            return File.ReadAllText(TempLogger.LogPath);
        }

        public void Dispose()
        {
            State.Dispose();
            TempLogger.Dispose();
        }
    }

    [Fact]
    public async Task PlausiblePayload_LogsLatencyAndButton()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.Now += Stopwatch.Frequency / 100; // press 10 ms ago relative to handler entry
        h.State.WriteActivationPayload(h.Now - Stopwatch.Frequency / 100, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        var log = await h.ReadLogAsync();
        Assert.Contains("Activation: latency", log);
        Assert.Contains("XButton2", log);
    }

    [Fact]
    public async Task ZeroPayload_LogsUnavailableAndUnknownButton()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();

        // Act
        handler.HandleActivation();

        // Assert
        var log = await h.ReadLogAsync();
        Assert.Contains("latency unavailable", log);
        Assert.Contains("unknown", log);
    }

    [Fact]
    public async Task StalePayloadOverTenSeconds_LogsUnavailable()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.State.WriteActivationPayload(h.Now - 11 * Stopwatch.Frequency, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains("latency unavailable", await h.ReadLogAsync());
    }

    [Fact]
    public async Task PlausibleQpcPredatingHandlerExit_LogsDeferredMarker()
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
        Assert.Contains("(deferred)", await h.ReadLogAsync());
    }

    [Fact]
    public async Task PressPredatingThrottleSkipExit_IsMarkedDeferred()
    {
        // Arrange: a throttle-skipped activation must still refresh the handler-exit stamp. The
        // press below lands between the first handling's exit and the skip's later exit, so a
        // correct implementation judges it deferred; one that fails to restamp on the skip path
        // would compare against the first exit, read the press as fresh, and fail this test.
        using var h = new Harness();
        var handler = h.Build();
        handler.HandleActivation();                        // stamps exit at the initial h.Now
        var pressAfterFirstExit = h.Now + Stopwatch.Frequency / 100;
        h.Now += Stopwatch.Frequency / 50;                 // the skip exits 20 ms later
        handler.HandleActivation();                        // throttle skip (Tick unchanged), restamps exit
        h.AdvancePastThrottle();
        h.Now += Stopwatch.Frequency / 100;
        h.State.WriteActivationPayload(pressAfterFirstExit, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains("(deferred)", await h.ReadLogAsync());
    }

    [Fact]
    public async Task PressPredatingConstruction_ButAfterTheSuppliedBaseline_IsNotMarkedDeferred()
    {
        // Arrange: the daemon constructs its handler lazily on the first activation, so that
        // press always predates construction. With the daemon's start time supplied as the
        // baseline the press reads as fresh; seeding from construction time instead would mark
        // every daemon lifetime's first press deferred.
        using var h = new Harness();
        var daemonStart = h.Now;
        var press = h.Now + Stopwatch.Frequency / 100;
        h.Now += Stopwatch.Frequency / 50;        // construction lands 20 ms after the press
        h.Baseline = daemonStart;
        var handler = h.Build();
        h.State.WriteActivationPayload(press, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.DoesNotContain("(deferred)", await h.ReadLogAsync());
    }

    [Fact]
    public async Task WithinThrottle_SkipsCloseAndLogs()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        handler.HandleActivation();

        // Act: Tick unchanged, so the second activation is inside the threshold.
        handler.HandleActivation();

        // Assert
        Assert.Single(h.Events, e => e == "close");
        Assert.Contains("Activation skipped: the previous handling", await h.ReadLogAsync());
    }

    [Fact]
    public async Task BusyGuardMutex_SkipsAndLogs()
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
            release.Wait(WaitBudget);
            m.ReleaseMutex();
        })
        {
            IsBackground = true
        };
        holder.Start();
        Assert.True(held.Wait(WaitBudget), "the holder never took the guard mutex");

        // Act, with the release guaranteed even if the act throws, so a failure fails one test
        // rather than stranding the holder.
        try
        {
            handler.HandleActivation();
        }
        finally
        {
            release.Set();
            Assert.True(holder.Join(WaitBudget), "the holder never released the guard mutex");
        }

        // Assert
        Assert.DoesNotContain(h.Events, e => e == "close");
        Assert.Contains("guard mutex is held", await h.ReadLogAsync());
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
    public async Task ThrowingClose_IsLoggedAndSwallowed_AndReleasesMutex()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.CloseThrows = true;

        // Act
        handler.HandleActivation();

        // Assert: the exception is logged, and the handle was released and disposed (a fresh
        // owned create sees a brand-new object).
        Assert.Contains("close failed", await h.ReadLogAsync());
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
The harness is complete as written; it uses only shipped helpers.

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

// Per-activation orchestration for the daemon's IPC path: payload and latency first (lock-free),
// then the throttle (lock-free; the app throttles under its guard mutex instead, which is
// equivalent because the tick is only ever written under the mutex), then the guard-mutex scope
// containing pending repair, the tick write, the close, and the healer decision; see the fix
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
    // (Stopwatch.GetTimestamp == QueryPerformanceCounter). Seeded from the caller's baseline (the
    // daemon passes its own start time, since it constructs this lazily on the first activation)
    // and refreshed on every exit, skips included.
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
        Func<SharedState, bool>? restorePending = null,
        long? initialHandlerExit = null)
    {
        _sharedState = sharedState;
        _logger = logger;
        _guardMutexName = guardMutexName;
        _settings = settings;
        _runClose = runClose;
        _dispatchHealer = dispatchHealer;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _restorePending = restorePending ?? (state => TimeoutRepair.TryRestorePending(state));
        _lastHandlerExit = initialHandlerExit ?? _timestamp();
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
        // Created, acquired, released, and disposed within this one activation, never cached in
        // a field: a live cached handle would make CrashRepair's createdNew liveness check read
        // false on every watchdog tick and silently disable the crash-repair watchdog. The
        // create-unowned-then-WaitOne(0) pair is a genuine acquire, satisfying the
        // acquired-not-probed invariant CrashRepair documents.
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

Spec anti-goal, restated at the point of temptation: the consumed button code feeds logging only. No logic branches on it; in particular the healer dispatch decision keys on `performedAttach` alone, and `TriggerButtonHealer` continues to monitor both trigger buttons regardless of which button signaled.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet build C:/Git/TheCloser --no-incremental && dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~ActivationHandlerTests"`
Expected: PASS, all 12 cases.

- [ ] **Step 5: Commit**

```bash
git -C C:/Git/TheCloser add -A -- ':!.claude/plans' && git -C C:/Git/TheCloser commit -m "feat(shared): activation handler for the daemon IPC path"
```

---

### Task 6: DaemonRuntime (loop, startup order, drain, final repair tick)

**Files:**
- Create: `TheCloser.Shared/DaemonRuntime.cs`
- Modify: `TheCloser.Tests/TestNames.cs` (adds `UniqueEventName()`)
- Test: `TheCloser.Tests/DaemonRuntimeTests.cs` (new)

**Interfaces:**
- Consumes: pre-existing `SharedState` and `Logger` shapes only. The `ActivationHandler` from Task 5 and `CrashRepair.TryRepairCrashedState` reach the runtime solely as the caller-wired `onActivation` / `watchdogTick` delegates; that wiring is the composition-root task's consumption, not this task's.
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
Also produced for later tasks: `TestNames.UniqueEventName()` in `TheCloser.Tests/TestNames.cs`, added by Step 1.
Behavior contract: Run pins the MMF, creates both auto-reset events, publishes the daemon mutex last, exits logging when `createdNew` is false; loops `WaitHandle.WaitAny([exitEvent, activationEvent], watchdogInterval)` where index 0 exits the loop, index 1 invokes `onActivation` under log-and-swallow, and timeout invokes `watchdogTick` under log-and-swallow; after the loop, one final `watchdogTick` under log-and-swallow, then a drain that snapshots the tracked healer tasks and waits for them, all before the using-scope unwind releases the kernel objects.

- [ ] **Step 1: Write the failing tests**

First add one member to `TheCloser.Tests/TestNames.cs`, after `UniqueLoggerName()`:

```csharp
    public static string UniqueEventName() => UniqueName();
```

Then create `TheCloser.Tests/DaemonRuntimeTests.cs`. Every wait is bounded (5 s) so a regression fails rather than hangs; every runtime thread is a background thread, so an assertion failure between start and join can never leave a foreground thread pinning the test process; every kernel object name is GUID-suffixed per test; log assertions use the `TempLogger` drain-then-read idiom (drain only after `Run` has returned).

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

    private static DaemonRuntime Build(Names n, TempLogger logger, Action<SharedState>? onActivation = null, Action<SharedState>? watchdogTick = null) =>
        new(logger.Logger, n.Map, n.Mutex, n.Activation, n.Exit,
            onActivation ?? (_ => { }), watchdogTick ?? (_ => { }), LongInterval);

    private static async Task<string> ReadLogAsync(TempLogger logger)
    {
        await logger.DrainAsync();

        return File.ReadAllText(logger.LogPath);
    }

    private static Thread Start(DaemonRuntime runtime, Names n)
    {
        var thread = new Thread(runtime.Run) { IsBackground = true };
        thread.Start();
        Assert.True(SpinWaitFor(() => Mutex.TryOpenExisting(n.Mutex, out var m) && Dispose(m)), "daemon mutex never appeared");

        return thread;
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
    public async Task SecondInstance_LosesOnMutex_AndEventStaysSignalable()
    {
        // Arrange
        var n = new Names();
        using var logger = new TempLogger();
        var thread = Start(Build(n, logger), n);

        // Act: a second runtime with the same names must return promptly.
        using var secondLogger = new TempLogger();
        var second = Build(n, secondLogger);
        var secondThread = new Thread(second.Run) { IsBackground = true };
        secondThread.Start();
        Assert.True(secondThread.Join(WaitBudget));

        // Assert: the loser logged and the survivor's activation event is still signalable.
        Assert.Contains("already running", await ReadLogAsync(secondLogger));
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
        using var logger = new TempLogger();
        var runtime = Build(n, logger);

        // Act
        var thread = new Thread(runtime.Run) { IsBackground = true };
        thread.Start();
        Assert.True(SpinWaitFor(() => Mutex.TryOpenExisting(n.Mutex, out var m) && Dispose(m)));

        // Assert: the moment the mutex is observable, both events must already exist.
        using (var a = EventWaitHandle.OpenExisting(n.Activation)) { }
        using (var e = EventWaitHandle.OpenExisting(n.Exit)) { }
        StopAndJoin(n, thread);
    }

    [Fact]
    public async Task Activation_InvokesHandler_AndExceptionIsSwallowed()
    {
        // Arrange
        var n = new Names();
        using var logger = new TempLogger();
        var invocations = 0;
        var runtime = Build(n, logger, onActivation: _ =>
        {
            invocations++;
            throw new InvalidOperationException("handler boom");
        });
        var thread = Start(runtime, n);

        // Act: two signals; the loop must survive the first throw to observe the second.
        using (var evt = EventWaitHandle.OpenExisting(n.Activation))
        {
            evt.Set();
            Assert.True(SpinWaitFor(() => invocations == 1));
            evt.Set();
            Assert.True(SpinWaitFor(() => invocations == 2));
        }

        // Assert
        StopAndJoin(n, thread);
        Assert.Contains("handler boom", await ReadLogAsync(logger));
    }

    [Fact]
    public void FinalRepairTick_RunsAfterLoopExit()
    {
        // Arrange: interval far above the test duration, so the only tick is the final one.
        var n = new Names();
        using var logger = new TempLogger();
        var ticks = 0;
        var runtime = Build(n, logger, watchdogTick: _ => ticks++);
        var thread = Start(runtime, n);

        // Act
        StopAndJoin(n, thread);

        // Assert
        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task FinalRepairTick_ThrowIsSwallowed_DrainStillRuns()
    {
        // Arrange
        var n = new Names();
        using var logger = new TempLogger();
        var healRan = false;
        var runtime = Build(n, logger, watchdogTick: _ => throw new InvalidOperationException("tick boom"));
        var thread = Start(runtime, n);
        runtime.DispatchHealer(() =>
        {
            Thread.Sleep(100);
            healRan = true;
        });

        // Act
        StopAndJoin(n, thread);

        // Assert: the throwing final tick was logged and did not skip the drain.
        Assert.True(healRan);
        Assert.Contains("tick boom", await ReadLogAsync(logger));
    }

    [Fact]
    public async Task Drain_WaitsForDispatchedHeal_IncludingThrowingHeal()
    {
        // Arrange
        var n = new Names();
        using var logger = new TempLogger();
        var slowHealDone = false;
        var runtime = Build(n, logger);
        var thread = Start(runtime, n);
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
        Assert.Contains("heal boom", await ReadLogAsync(logger));
    }
}
```
The tests are complete as written; they use only shipped helpers plus the `UniqueEventName()` member this step adds.

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
        // Register before running so the drain can never miss a just-dispatched heal, and remove
        // by the registered completion so a fast heal cannot race its own registration.
        var completion = new TaskCompletionSource();
        _healerTasks.TryAdd(completion.Task, 0);
        Task.Run(() =>
        {
            try
            {
                RunIsolated(heal);
            }
            finally
            {
                _healerTasks.TryRemove(completion.Task, out _);
                completion.SetResult();
            }
        });
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

Run: `dotnet build C:/Git/TheCloser --no-incremental && dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~DaemonRuntimeTests"`
Expected: PASS, all 6 cases.

- [ ] **Step 5: Commit**

```bash
git -C C:/Git/TheCloser add -A -- ':!.claude/plans' && git -C C:/Git/TheCloser commit -m "feat(shared): daemon runtime loop with drain and final repair tick"
```

---

### Task 7: Wire the daemon composition root

**Files:**
- Create: `TheCloser.Shared/DaemonConfiguration.cs`
- Modify: `TheCloser.Daemon/Program.cs`
- Test: `TheCloser.Tests/DaemonConfigurationTests.cs` (new)

**Interfaces:**
- Consumes: everything from Tasks 2 through 6 with the exact signatures above.
- Produces: `internal static class DaemonConfiguration { public static IConfigurationRoot Build(string directory, Action<string> logError); }` in `TheCloser.Shared`.

- [ ] **Step 1: Write the failing configuration tests**

Create `TheCloser.Tests/DaemonConfigurationTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using Xunit;

namespace TheCloser.Tests;

public class DaemonConfigurationTests
{
    [Fact]
    public void MissingFile_YieldsEmptyConfiguration()
    {
        // Arrange
        var directory = CreateTempDirectory();
        IConfigurationRoot? root = null;

        try
        {
            // Act
            root = DaemonConfiguration.Build(directory, _ => { });

            // Assert
            Assert.Empty(root.AsEnumerable());
        }
        finally
        {
            (root as IDisposable)?.Dispose();
            DeleteQuietly(directory);
        }
    }

    [Fact]
    public void MalformedJson_LogsAndYieldsEmptyConfiguration()
    {
        // Arrange
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{ not json");
        var logged = new List<string>();
        IConfigurationRoot? root = null;

        try
        {
            // Act
            root = DaemonConfiguration.Build(directory, logged.Add);

            // Assert
            Assert.Contains(logged, line => line.Contains("Configuration reload failed"));
            Assert.Empty(root.AsEnumerable());
        }
        finally
        {
            (root as IDisposable)?.Dispose();
            DeleteQuietly(directory);
        }
    }

    [Fact]
    public void ValidFile_ExposesValues()
    {
        // Arrange
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{ \"chrome\": \"CTRL-F4\" }");
        IConfigurationRoot? root = null;

        try
        {
            // Act
            root = DaemonConfiguration.Build(directory, _ => { });

            // Assert
            Assert.Equal("CTRL-F4", root["chrome"]);
        }
        finally
        {
            (root as IDisposable)?.Dispose();
            DeleteQuietly(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TheCloserConfigTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        return directory;
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // The config file watcher can hold the directory handle briefly; the GUID-suffixed
            // temp directory is left to OS temp cleanup instead of failing the test.
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: build FAILS with CS0103 (the name 'DaemonConfiguration' does not exist) in DaemonConfigurationTests.cs; that compile failure is this step's red state.

- [ ] **Step 3: Create the configuration builder**

Create `TheCloser.Shared/DaemonConfiguration.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace TheCloser.Shared;

// The daemon's configuration root: optional appsettings.json beside the executable, hot-reloaded,
// with parse failures logged and swallowed so a bad edit degrades to the last good snapshot
// instead of killing the daemon (see the fix design's Configuration section).
internal static class DaemonConfiguration
{
    public static IConfigurationRoot Build(string directory, Action<string> logError) => new ConfigurationBuilder()
        .SetBasePath(directory)
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
                logError($"Configuration reload failed: {context.Exception.Message}");
                context.Ignore = true;
            };
            source.ResolveFileProvider();
        })
        .Build();
}
```

Verify the exact `AddJsonFile(Action<JsonConfigurationSource>)` overload and the `ResolveFileProvider()` requirement against the installed 10.0.9 package source or docs before writing; if the action overload does not honor `SetBasePath` even with `ResolveFileProvider()`, fall back to constructing the `JsonConfigurationSource` explicitly and calling `builder.Add(source)`. Drop the `using Microsoft.Extensions.Configuration.Json;` line if the compiler flags it unnecessary.

Run: `dotnet build C:/Git/TheCloser --no-incremental && dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~DaemonConfigurationTests"`
Expected: Build succeeded; PASS, all 3 cases.

- [ ] **Step 4: Rewrite TheCloser.Daemon/Program.cs**

Replace the `Run()` method and supporting members with a composition root (keep `Main`, `SignalExit`, and the argument dispatch exactly as they are). The `onActivation` and `watchdogTick` delegates receive the `SharedState` that `DaemonRuntime.Run` created, so the handler is built lazily on the first activation. Because of that laziness the daemon's start QPC is captured here and passed as `initialHandlerExit`: the spec pins the deferred-marker baseline to the daemon's own start time, and seeding it at construction instead would mark every daemon lifetime's first press deferred, excluding exactly the after-idle presses the spec's latency gate samples. The exact final shape:

```csharp
    private static void Run()
    {
        var daemonStart = Stopwatch.GetTimestamp();
        var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        var liveRoot = DaemonConfiguration.Build(exeDirectory, Logger.Log);
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
                    // The WindowCloser (and the ForegroundActivator it owns) is constructed
                    // inside this lambda, never hoisted to a field: PerformedInputAttach is
                    // OR-assigned and never reset, so it is sticky for a pipeline instance's
                    // life. A daemon-lifetime instance would therefore report an attach on every
                    // close forever after the first real one, dispatching the healer after every
                    // close for the daemon's whole life. Per-activation construction is a spec
                    // requirement, not an allocation the daemon can optimize away.
                    runClose: snapshot =>
                    {
                        var closer = new WindowCloser(snapshot, sharedState, Logger);
                        closer.CloseWindowUnderCursor();

                        return closer.PerformedInputAttach;
                    },
                    dispatchHealer: () => runtime!.DispatchHealer(() => new TriggerButtonHealer(Logger).HealStuckButtons()),
                    initialHandlerExit: daemonStart);
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

    private static void RepairIfCrashed(SharedState sharedState)
    {
        if (CrashRepair.TryRepairCrashedState(sharedState, GuardMutexName, Logger))
        {
            Logger.Log("Restored the foreground lock timeout after a detected app crash.");
        }
    }
```
Add `using System.Diagnostics;`, `using TheCloser.Shared;`, and `using Microsoft.Extensions.Configuration;` beside the existing usings as needed. Note the config-root disposal after `Run()` returns and before `Main`'s `finally` disposes the Logger, matching the spec's unwind ordering. The composition wiring itself (lazy handler construction, disposal ordering) is gated by compile plus the pipeline's end-to-end verification; its parts are unit-covered piecewise by Tasks 4 through 6 and the configuration tests above.

- [ ] **Step 5: Verify the per-activation lifetimes structurally**

Neither hoisting hazard has a unit test (both are about object lifetime in the composition root, not observable behavior of one call), so gate them by reading the two files.

Run (Git Bash):
```bash
grep -n "new WindowCloser\|new TriggerButtonHealer\|new Mutex" /c/Git/TheCloser/TheCloser.Daemon/Program.cs /c/Git/TheCloser/TheCloser.Shared/ActivationHandler.cs
```
Expected: exactly three hits, each inside a lambda or method body and none at field scope. In `Program.cs`: one `new WindowCloser` inside the `runClose` lambda and one `new TriggerButtonHealer` inside the `dispatchHealer` lambda, and no `new Mutex` at all (the daemon mutex the pre-rewrite `Run()` created now lives in `DaemonRuntime`). In `ActivationHandler.cs`: one `new Mutex` inside `RunThrottledActivation`. A hit at field scope, or a surviving `new Mutex` in `Program.cs`, means a pipeline, healer, or guard-mutex handle was hoisted to daemon lifetime; revert that hoist before continuing.

- [ ] **Step 6: Build, run the touching test suites**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: Build succeeded.

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build --filter "FullyQualifiedName~DaemonRuntimeTests|FullyQualifiedName~ActivationHandlerTests|FullyQualifiedName~DaemonConfigurationTests|FullyQualifiedName~CrashRepairTests|FullyQualifiedName~TimeoutRepairTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git -C C:/Git/TheCloser add -A -- ':!.claude/plans' && git -C C:/Git/TheCloser commit -m "feat(daemon): host the IPC activation pipeline"
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
; ==== This replacement is also the whole answer to downward mixed elevation (an elevated
; script signalling an unelevated daemon, which OpenEvent cannot distinguish and which would
; silently fail to close elevated windows). Detecting that per press is an explicit spec
; anti-goal: it arises only from manually starting a daemon in a deployed chain, and the
; stop-then-start above replaces such a daemon at the next script start. Add no per-press
; elevation probe. Upward (unelevated script, elevated daemon) needs nothing either: OpenEvent
; fails with access denied and the press takes the fallback.
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
    ; No UseErrorLevel here, deliberately: the auto-execute suppresses its launch-error dialog
    ; because a modal at logon would block a headless sequence, and the spec states that
    ; rationale does not transfer to a press an interactive user is present for. Keeping today's
    ; modal surface means the fallback's failure exposure is never worse than shipped.
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
        ; The payload carries a timestamp and a button code and nothing else. Press-time cursor
        ; position transmission is an explicit spec non-goal: the daemon samples the cursor when
        ; it handles the press, which is the user's latest intent, where a press-time position
        ; would aim a deferred close at a spot the user has already left.
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

- [ ] **Step 3: Syntax gate**

AutoHotkey v1 does have a headless syntax check: `/iLib NUL` loads and parses the script for library auto-inclusion and exits without executing it, and `/ErrorStdOut` sends any parse error to stderr instead of a modal dialog. This was probed while writing the plan against this very script: as prescribed above it exits 0 silently, while a dropped closing brace exits 2 with `(52) : ==> Functions cannot contain functions.` and an unrecognized command exits 2 with `==> This line does not contain a recognized action.`

Two constraints on how it is invoked, both established by that probe:

- Run it from **PowerShell, never Git Bash**. MSYS path conversion rewrites the `/iLib` and `/ErrorStdOut` switches into `C:/Program Files/Git/iLib` and `.../ErrorStdOut`, so AutoHotkey sees no switches, **runs** the script instead of parsing it, and the gate hangs with a resident AutoHotkey process.
- `AutoHotkeyU64.exe` is a GUI-subsystem binary, so `&` does not wait for it and leaves `$LASTEXITCODE` unset. Use `Start-Process -Wait -PassThru` and read `.ExitCode`.

Run:
```powershell
$p = Start-Process -FilePath 'C:\Program Files\AutoHotkey\AutoHotkeyU64.exe' -ArgumentList '/iLib', 'NUL', '/ErrorStdOut', 'C:/Git/TheCloser/TheCloser.ahk' -Wait -PassThru -WindowStyle Hidden -RedirectStandardError 'C:/Git/TheCloser/.tmp/ahk-parse.err'; "EXIT=$($p.ExitCode)"; Get-Content 'C:/Git/TheCloser/.tmp/ahk-parse.err' -Raw
```
Expected: `EXIT=0` and an empty error file. A nonzero exit prints the offending line number and message; fix the script before committing.

The gate parses, so it catches malformed control flow, unbalanced braces, unrecognized commands, and bad hotkey labels. It does not evaluate `DllCall` type strings: a bogus type such as `"NotAType*"` still exits 0, because that failure is a runtime one. The token gate below therefore stays, covering the load-bearing API names the parser cannot vouch for, and real behavior is exercised by the handover's e2e pass.

- [ ] **Step 4: Structural token gate**

The parse gate cannot tell whether the rewrite kept the elements the design depends on, so check their presence literally:

Run (Git Bash):
```bash
for token in "#SingleInstance Force" "XButton2::" "OpenMutex" "OpenEvent" "OpenFileMapping" "MapViewOfFile" "SetEvent" "QueryPerformanceCounter" "RunWait" "--probe-launch-qpc"; do grep -qF -e "$token" /c/Git/TheCloser/TheCloser.ahk || echo "MISSING $token"; done
```
Expected: no output. (The `-e` is load-bearing: `--probe-launch-qpc` passed as the first non-option word is parsed as an unknown long option, and grep exits 2 before searching, which the `||` branch would report as a false MISSING line.) Any `MISSING` line means the rewrite dropped a load-bearing element; fix before committing.

- [ ] **Step 5: Commit**

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
Update the header comment (the block at lines 3-10, above the `[CmdletBinding()]` line; it contains no `-StartNow` sentence, so nothing is removed from it). Replace only the block's line-8 sentence, `# Run once per machine from an elevated shell; remove any old unelevated autostart by hand.` (the two lines after it, documenting the `$AhkScriptPath` default, stay exactly as they are), with:

```powershell
# Run from an elevated shell, or let deploy.ps1 run it on a machine with no task registered yet.
# Every run registers, stops the running instance, sweeps stray AutoHotkey copies of the script,
# starts the task, and exits nonzero if either state poll expires; remove any old unelevated
# autostart by hand.
```

The `-StartNow` removal is a param-block edit, already covered by "keep the param block (minus `-StartNow`)" above; starting is now unconditional.

- [ ] **Step 2: Syntax check**

An inline `-Command` string cannot carry this check: the calling shell expands `$t`/`$e`/`$null` inside double quotes before the child pwsh sees them. Write the check to a script file instead (the Write tool; overwriting an identical existing copy is fine) at `C:/Git/TheCloser/.tmp/parse-check.ps1` with exactly:

```powershell
param([Parameter(Mandatory)][string] $Path)
$tokens = $null
$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
if ($errors.Count) {
    $errors | ForEach-Object Message
    exit 1
}
'parse-ok'
```

Run: `pwsh -NoProfile -File C:/Git/TheCloser/.tmp/parse-check.ps1 C:/Git/TheCloser/install-elevated-ahk.ps1`
Expected: `parse-ok` and exit code 0; any parse error prints its messages and exits 1.

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
# stop-poll proves none does before the start is issued. Unlike the installer, this branch
# deliberately runs no stray-instance sweep: on a machine already running the deployed chain the
# only instance to manage is the task-hosted one the stop-poll just ended.
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
    # ArgumentList entries are joined with spaces and never quoted, and the configured
    # Destination contains a space, so the -File path is quoted explicitly.
    Start-Process pwsh -Verb RunAs -Wait -ArgumentList '-NoProfile', '-File', ('"{0}"' -f (Join-Path $Destination 'install-elevated-ahk.ps1'))
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

An inline `-Command` string cannot carry this check: the calling shell expands `$t`/`$e`/`$null` inside double quotes before the child pwsh sees them. Write the check to a script file instead (the Write tool; overwriting an identical existing copy is fine) at `C:/Git/TheCloser/.tmp/parse-check.ps1` with exactly:

```powershell
param([Parameter(Mandatory)][string] $Path)
$tokens = $null
$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
if ($errors.Count) {
    $errors | ForEach-Object Message
    exit 1
}
'parse-ok'
```

Run: `pwsh -NoProfile -File C:/Git/TheCloser/.tmp/parse-check.ps1 C:/Git/TheCloser/deploy.ps1`
Expected: `parse-ok` and exit code 0; any parse error prints its messages and exits 1.

- [ ] **Step 3: Commit**

```bash
git -C C:/Git/TheCloser add deploy.ps1 && git -C C:/Git/TheCloser commit -m "feat(deploy): automatic elevated task restart with bounded polls"
```

---

### Task 11: Full project test pass and hygiene sweep

- [ ] **Step 1: Build clean and run the whole test project**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: Build succeeded, `0 Warning(s)` (the pre-plan tree builds with zero warnings, verified while writing the plan, so zero is the literal bar).

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build`
(The test project IS the filter; this is not the forbidden solution-wide unfiltered run.)
Expected: all tests pass.

- [ ] **Step 2: Cross-language sync sweep**

Run (Git Bash):
```bash
grep -n "TheCloserDaemonMutex\|TheCloserActivationEvent\|TheCloserSharedState\|ActivationQpcOffset\|ActivationButtonOffset\|TriggerButtonXButton2" /c/Git/TheCloser/TheCloser.ahk /c/Git/TheCloser/TheCloser.Shared/Constants.cs /c/Git/TheCloser/TheCloser.Shared/SharedState.cs
```
Expected: names and offset values 16/24 agree across all three files (this grep prints only the value-carrying lines; it cannot see comments).

Run (Git Bash):
```bash
grep -c "keep in sync\|Hand-synchronized" /c/Git/TheCloser/TheCloser.ahk /c/Git/TheCloser/TheCloser.Shared/Constants.cs /c/Git/TheCloser/TheCloser.Shared/SharedState.cs
```
Expected: a nonzero per-file count printed for each of the three files; judge by the printed numbers, never by grep's exit status.

- [ ] **Step 3: Commit any stragglers**

```bash
git -C C:/Git/TheCloser status --short
```
Expected: clean apart from the plan file and `.tmp/`; commit nothing here if clean.

---

## Post-implementation (owned by the handover pipeline, not this plan)

Code review (`/nightshift:revise-code`), end-to-end verification per the spec's Testing and Verification criteria sections (deploy first; elevated e2e drive; latency gate including after-idle samples; live-claim probes), docs pass (CLAUDE.md architecture and centralization note), backlog bookkeeping, and the morning report.
## Hardening

- revise-plan graduated 2026-08-29 20:35 at d5bd1f2, scope: whole file, content: p-df3e0414a432
