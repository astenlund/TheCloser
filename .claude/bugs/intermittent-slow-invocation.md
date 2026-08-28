# Intermittent slow invocation investigation

## Status

Open. The delay is confirmed to occur before managed `Main`. A lean Windows Performance Recorder trace is armed in memory and stops automatically when the invocation probe records more than 150 ms before `Main`. The temporary scheduled task `TheCloser Launch Trace (Temporary)` is registered and ready to re-arm the trace at the current user's next logon.

The current goal is root-cause identification, not mitigation. Do not add exclusions, retries, preloaders, or daemon-side workarounds until a retained trace identifies the blocking component.

## Symptom

- The first TheCloser activation after an idle interval can feel delayed. Reported cases range from noticeable sub-second pauses to about 10 seconds.
- Subsequent invocations usually feel instant.
- The behavior was not noticed at commit `04cbeb7` and appeared after later changes. No commit in that range has been isolated by bisect.
- The machine normally remains running continuously, but a reboot on 2026-08-28 exposed that an in-memory trace must be re-armed at logon.

## Confirmed observations

### Application work is not the dominant delay

The invocation probe receives a `QueryPerformanceCounter` value immediately before AutoHotkey calls `Run`, then records the first timestamp inside managed `Main`. Slow samples spend nearly all extra time between those boundaries. Once `Main` begins, daemon discovery, configuration, and window closing remain near their normal durations.

The daemon was alive in the captured slow samples. Moving logger file writes to a queue in `f668ff4` removed synchronous file logging from the invocation path, but slow launches still occurred.

### 2026-08-27 asymmetric sample

- Real Sync executable, PID 50320: `main-enter` at 625.501 ms; close returned at 706.695 ms.
- Local no-op executable, PID 16064: `main-enter` at 24.785 ms.
- The peer launch timestamp was 654.209 ms after the real launch timestamp, so the launches were not concurrent enough to prove a path-specific cause.

This sample established that the real invocation delay was before `Main`, but it did not establish that the Sync path caused the delay.

### 2026-08-28 near-concurrent two-path sample

- Real Sync executable, PID 8412: `main-enter` at 692.508 ms; main exited at 764.136 ms.
- Local no-op executable, PID 18092: `main-enter` at 684.043 ms.
- The launch timestamps were 51.133 ms apart.
- A preceding paired activation about 10 minutes earlier reached `Main` at 137.270 ms and 139.906 ms.

This sample proves that at least one near-concurrent slow occurrence affected both executable locations before `Main`. It does not prove that the same underlying cause delayed both processes. A Sync-only explanation cannot account for every occurrence. The paired copy no longer added enough diagnostic value and was retired on 2026-08-28; the kernel trace can identify path-specific waits directly.

## Trace history

The first 128 MB WPR profile captured process, loader, file, minifilter, hard-fault, sampled-profile, and Defender providers. It ran for several hours. Stopping it triggered a large runtime rundown and about 42 seconds of trace-save activity, which overwrote the useful circular buffers before the slow interval. The ETL had no dropped events but could not retain the target window.

The replacement profile uses two 64 MB memory collectors:

- System: process, thread scheduling, loader, hard faults, disk and file I/O, minifilter I/O, and sampled profile events.
- Event: only Microsoft Defender service, engine, real-time protection, and filter providers.

An elevated monitor tails `%TEMP%\TheCloser.Probe.log` from its starting offset. When a new `main-enter` exceeds 150 ms, it stops WPR immediately and saves a timestamped ETL under `.tmp/`.

The original heavy trace is `.tmp/TheCloser-slow-launch.etl`. The lean trace armed after the 2026-08-28 reboot targets `.tmp/TheCloser-auto-20260828-182847.etl`. After scheduled-task registration, WPR remained active with zero dropped events. ETLs are machine-local and ignored by Git; durable conclusions extracted from them belong in this report.

The temporary scheduled task has these verified properties:

- Current-user logon trigger.
- Highest run level with an interactive token.
- Hidden PowerShell 7 action that calls `.tmp/manage-launch-trace-task.ps1 -Action Arm`.
- `IgnoreNew` multiple-instance policy, so an existing arm is not duplicated.
- Ready state after registration. It was not started manually because the current trace was already armed.

## Current deployed state

On 2026-08-28, the single-launch AHK binding was deployed to `C:\Sync\Personal\3. Resources\Bin\TheCloser`. The obsolete `TheCloser.ProbeWorker.ahk`, `TheCloser.Probe.ini`, and dedicated local peer-copy directory were deleted. The newly deployed daemon was restarted.

A guarded end-to-end check against sacrificial WinForms windows passed both keyboard activation paths. The log recorded native background activation and the already-foreground path, and both forms closed through the configured `ALT-F4` dispatch.

## Hypothesis state

### Confirmed

- The intermittent delay can occur before any managed application logic runs.
- Window closing and configuration work are not the source of the long pause in the measured samples.
- The daemon can remain alive throughout a slow occurrence.
- One near-concurrent sample showed pre-`Main` delays in both the Sync executable and an identical local copy; whether they shared one cause remains open.

### Refuted or narrowed

- Synchronous logger writes are not required for the delay; they were queued off the invocation path and the symptom recurred.
- A Sync-only path explanation is not sufficient for all occurrences.
- The asymmetric 2026-08-27 pair cannot be treated as proof of a Sync-specific delay because its peer launch was issued after most of the real delay had elapsed.

### Open

- Windows process creation or scheduling delay between AutoHotkey `Run` and the new process receiving CPU time.
- Loader, code-integrity, App Control, or signature verification work.
- Defender or another minifilter scan.
- Hard faults, storage latency, or image-page reactivation after idle.
- More than one slow-launch mode, including a possible path-specific mode distinct from the near-concurrent two-path sample.

## Diagnostic code and artifacts

- `7a791b5`: startup checkpoint probe and `%TEMP%\TheCloser.Probe.log`.
- `f668ff4`: asynchronous queued logger writes.
- `451825d` and `9556455`: temporary paired-launch comparison; `41f7dd4` retired it after one near-concurrent activation showed both locations were slow.
- `.tmp/Minifilter.wprp`: lean 128 MB memory trace profile.
- `.tmp/start-launch-trace-elevated.ps1`: elevated WPR start, log monitor, and automatic stop.
- `.tmp/manage-launch-trace-task.ps1`: temporary task install, logon arm, and cleanup entry point.
- `.tmp/wpr-start-output.txt`: current arm status, trace path, and monitor PID.
- `.tmp/wpr-auto-stop-output.txt`: trigger identity, measured duration, saved ETL path, and WPR stop result.

## Resume procedure

When the user reports another slow activation:

1. Read `.tmp/wpr-auto-stop-output.txt`. If present, preserve the named ETL and note the trigger PID and duration here.
2. Read the final probe records from `%TEMP%\TheCloser.Probe.log` and correlate the PID with the ETL.
3. Analyze process lifetime, loader and hard-fault events, thread scheduling, file and minifilter delays, and Defender events for the interval from the AHK launch timestamp through `main-enter`.
4. Record evidence and update the hypothesis sections before proposing a fix.
5. If no ETL was saved, check the scheduled task, WPR status, reboot time, and monitor status before changing the detector.

## Cleanup after investigation

Run the following from a normal PowerShell 7 shell; it self-elevates:

```powershell
pwsh -NoProfile -File .tmp/manage-launch-trace-task.ps1 -Action Remove
```

This stops and unregisters `TheCloser Launch Trace (Temporary)`, stops a recognized trace monitor, and cancels WPR. It does not delete ETLs or this historical report.
