# Intermittent slow invocation investigation

## Status

Open, with two pre-`Main` delay components identified. One retained Windows Performance Recorder trace proves that Microsoft Defender synchronously scanned the unsigned deployed executable for 702.310 ms before Windows created the process. A second retained trace captured a 2455.128 ms launch composed of 1784.354 ms inside Windows process creation before Defender opened the executable, a 655.392 ms Defender scan, 7.079 ms from the scan completing to process creation, and 8.303 ms from process creation to managed `Main`.

An exact contextual Defender exclusion remains a useful partial mitigation, but it would have removed only 655.392 ms of the second occurrence. It cannot make invocation reliable by itself. The temporary scheduled task `TheCloser Launch Trace (Temporary)` remains registered. After removing minifilter stack capture to retain more early scheduling history, the task armed `.tmp/TheCloser-auto-20260828-204631.etl` with monitor PID 32076. The earlier report of an approximately 10 second launch has not been captured and may be an extreme instance of the pre-image process-creation mode or another mode.

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

### 2026-08-28 retained Defender trace

The automatic detector stopped on deployed process PID 26848 after measuring 715.924 ms from the AutoHotkey launch timestamp to managed `Main`. The retained trace is `.tmp/TheCloser-auto-20260828-182847.etl`; it covers 29 minutes and 18 seconds with no lost events.

The trace establishes this sequence:

- AutoHotkey opened the deployed `TheCloser.exe` image.
- Microsoft Defender started a real-time stream scan requested by AutoHotkey.
- Defender performed exhaustive scanning, low-fidelity signature checks, hash calculation, and trust validation.
- Defender recorded that `TheCloser.exe` was not trusted and that signed-file validation failed.
- The Defender stream scan stopped 702.310 ms after it started.
- Defender's process-create event for TheCloser followed 3.713 ms later, 706.023 ms after the scan began.
- Managed `Main` followed about 10 ms after process creation, matching the probe's total 715.924 ms pre-`Main` measurement.

The hard-fault and disk-I/O reports contain no events for the trigger interval. The minifilter-delay report contains no separate delay record. For this sample, Defender's synchronous scan accounts for nearly the entire observed pause and is the confirmed root cause.

After the trace had already stopped, deployed process PID 18440 recorded another 711.074 ms before `Main`, followed by launches at 14.221 ms, 11.741 ms, and 10.570 ms. Its duration and before-`Main` shape strongly match the captured Defender mode, but it has no retained kernel trace and is therefore corroborating evidence rather than independent proof.

### 2026-08-28 retained mixed process-creation and Defender trace

The automatic detector stopped on deployed process PID 14092 after measuring 2455.128 ms from the AutoHotkey launch timestamp to managed `Main`. The retained trace is `.tmp/TheCloser-auto-20260828-201856.etl`; it covers 18 minutes and 9.744 seconds with no lost buffers or events.

The AutoHotkey launch timestamp was mapped into ETW time through a minifilter event carrying a raw `QueryPerformanceCounter` value. The trace establishes this sequence:

- 1784.354 ms elapsed after AutoHotkey called `Run` and before Microsoft Defender received its first open of the deployed executable.
- Defender's real-time stream scan then took 655.392 ms. This was 26.694 percent of the total pre-`Main` time.
- Windows created the process 7.079 ms after the scan stopped.
- Managed `Main` began 8.303 ms after process creation.

The four intervals sum to the probe's complete 2455.128 ms measurement. There was no target executable file I/O before Defender's first open, so the first 1784.354 ms was not loader or storage work on `TheCloser.exe`. The hard-fault, disk-I/O, and minifilter-delay reports contain no independent delay in the trigger interval. A CPU export covering the interval shows the system approximately 90 percent idle.

The exact AutoHotkey v1.1.36.02 source was inspected at its official tag. `Script::ActionExec` calls `CreateProcess` first and uses `ShellExecuteEx` only if `CreateProcess` fails. The fully qualified executable invocation therefore does not normally pass through shell resolution. The unexplained first interval is inside the Windows `CreateProcess` path before the target image is opened.

The system collector retained context switches only from approximately 39 ms after the AutoHotkey launch timestamp because minifilter stack capture consumed the circular history. It therefore missed the launch thread's initial switch-out and cannot identify its precise wait reason. The next trace drops those already-unhelpful minifilter stacks while retaining the minifilter events.

## Trace history

The first 128 MB WPR profile captured process, loader, file, minifilter, hard-fault, sampled-profile, and Defender providers. It ran for several hours. Stopping it triggered a large runtime rundown and about 42 seconds of trace-save activity, which overwrote the useful circular buffers before the slow interval. The ETL had no dropped events but could not retain the target window.

The replacement profile uses two 64 MB memory collectors:

- System: process, thread scheduling, loader, hard faults, disk and file I/O, minifilter I/O, and sampled profile events.
- Event: only Microsoft Defender service, engine, real-time protection, and filter providers.

An elevated monitor tails `%TEMP%\TheCloser.Probe.log` from its starting offset. When a new `main-enter` exceeds 150 ms, it stops WPR immediately and saves a timestamped ETL under `.tmp/`.

The original heavy trace is `.tmp/TheCloser-slow-launch.etl`. The lean trace armed after the 2026-08-28 reboot saved `.tmp/TheCloser-auto-20260828-182847.etl` when PID 26848 crossed the trigger threshold. The automatic stop exited successfully. Its replacement saved `.tmp/TheCloser-auto-20260828-201856.etl` when PID 14092 crossed the threshold, and its automatic stop also exited successfully. The next trace, `.tmp/TheCloser-auto-20260828-204631.etl`, was armed at 20:46 local time with monitor PID 32076 and the minifilter stack capture removed. ETLs are machine-local and ignored by Git; durable conclusions extracted from them belong in this report.

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
- Microsoft Defender's synchronous scan of the unsigned and untrusted executable caused the retained approximately 716 ms slow launch.
- A second retained launch contained both a 655.392 ms Defender scan and a separate 1784.354 ms delay inside Windows process creation before the executable was opened.
- AutoHotkey v1.1.36.02 normally implements `Run` with `CreateProcess`, so shell resolution is not responsible for the second trace's pre-image interval.

### Refuted or narrowed

- Synchronous logger writes are not required for the delay; they were queued off the invocation path and the symptom recurred.
- A Sync-only path explanation is not sufficient for all occurrences.
- The asymmetric 2026-08-27 pair cannot be treated as proof of a Sync-specific delay because its peer launch was issued after most of the real delay had elapsed.
- Windows did not create the captured process until the Defender scan completed, so application scheduling, loader work, daemon discovery, configuration, and window-closing logic cannot explain that sample's pause.
- Hard faults and storage I/O did not contribute measurable work in the captured trigger interval.
- A contextual Defender exclusion cannot eliminate every slow occurrence. It would have removed only 655.392 ms of the captured 2455.128 ms launch.

### Open

- Why Defender repeats the scan after an idle interval despite the executable bytes remaining unchanged.
- Whether trusted code signing prevents or materially shortens the scan without requiring a Defender exclusion.
- What blocks AutoHotkey's `CreateProcess` call for 1784.354 ms before any open of the target executable.
- Whether the approximately 10 second report is an extreme instance of the same pre-image process-creation path or a distinct slow-launch mode.
- Whether moving invocation over IPC to the already-running daemon is preferable to continuing to depend on synchronous process creation for every activation.

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
