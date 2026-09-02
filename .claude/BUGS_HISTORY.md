# Bugs (history)

Fixed bugs, archived from `BUGS.md` so the active list stays scannable
on session start. **Archaeological**: read only when consulted, not at
session start. When a bug is fixed, append its entry here rather than
to the active file.

The bug breakout file at `bugs/<slug>.md` (when present) stays in place
as the historical diagnosis record; the entry here is a brief
description of the fix and the commit it landed in.

## Cross-reference resolution

`/nightshift:ready` does **not** scan this file. When a bug is fixed, every other
`**Requires:**` line in `FEATURES.md` / `BUGS.md` that referenced it is
edited at the same time to drop the now-satisfied reference (mirror of
the `FEATURES.md` convention). The active `Requires:` lines therefore
describe what is *currently* blocking; this file is purely
archaeological.

## Entries

### Mouse double activation closed two Chrome tabs when the first close stalled

Reported: 2026-09-02. Fixed: 2026-09-02 in the commit titled "fix(shared): date the activation throttle from the press".

**Symptom:** one physical XButton2 press closed two Chrome tabs, both landing about two seconds after the press, a few tens of milliseconds apart.

**Root cause:** two independent things coincided. The mouse emitted a double activation (second press 92 ms after the first; the daemon log shows the same ~100 ms signature routinely, normally skipped). Separately, the first close's SendInput blocked the daemon loop thread for about 2.1 s (the cursor kept moving, so the raw input path was alive; the keyboard hook chain was the stalled path, and PowerToys Keyboard Manager installs a WH_KEYBOARD_LL hook). The throttle in `ActivationHandler` compared the handling time against the previous throttle tick, so when the queued second press was finally handled 2136 ms after the first tick write it passed, even though its payload QPC placed it 92 ms after the first press. The daemon IPC design (`bugs/intermittent-slow-invocation.md`) had accepted exactly this outcome, reasoning that a press collapsed behind a longer close and surviving the throttle honors a real double press; this fix supersedes that decision, since the payload timestamp can tell a double activation from a double press.

**Fix:** the throttle now subtracts the plausible press-to-handler latency from the current tick before comparing against the stored tick, so a press is judged by when the user pressed, not by when the loop reached it. The window is symmetric: a press landing between the previous handler's entry and its tick write yields a small negative elapsed value and is skipped too, while a stale-format or foreign tick still reads as a huge magnitude and stays unthrottled. When the payload is implausible the latency counts as zero and the throttle is equivalent to before: the only new skip case is a tick in the 200ms ahead of now, unreachable with the monotonic tick count both writers share. The standalone fallback app's throttle is unchanged: it is its own press.

**Not fixed, by design:** the first tab still closes late when SendInput stalls; Windows runs every low-level keyboard hook synchronously before delivering injected input, and the daemon cannot shorten that. Stall instrumentation (injection duration, handler phase breakdown, hook timeout at startup) landed in the commit titled "feat(shared): log stalled activation phases" so a recurrence is attributable.

### Intermittent slow invocation after idle

Reported: 2026-08-27. Fixed: 2026-08-29 by the daemon IPC implementation series and verified after all temporary diagnostics were removed.

**Symptom:** the first invocation after an idle interval sometimes spent hundreds of milliseconds or several seconds before managed `Main`; warm invocations were usually fast.

**Root causes:** retained WPR traces proved two process-launch delay modes. One launch spent 702.310 ms in a synchronous Microsoft Defender scan before process creation. Another spent 1784.354 ms inside Windows process creation before the executable was opened, followed by a 655.392 ms Defender scan. Native AOT application work after process creation took only single-digit milliseconds.

**Fix:** `TheCloser.ahk` now writes a QPC payload to shared memory and signals a long-lived daemon for the normal press path, so no executable launches per activation. The daemon hosts the close pipeline, hot-reloads last-good configuration, shares throttle and repair state with the standalone fallback, logs press-to-handler latency, drains stuck-button healers on shutdown, and keeps the fallback path available when its event is absent. Deployment and installer flows restart the elevated task with bounded state polls.

**Verified:** deployed IPC closes passed background and foreground activation, configuration reload and last-good retention, same-path script replacement, missing-binary degraded mode, outside-copy installer cleanup, held-mutex retry expiry, and fallback self-healing. After one pre-instrumentation 215.5 ms activation reset the gate, ten consecutive real Mouse5 activations passed at 0.0 to 1.7 ms with no deferred marker; three were first presses after more than 30 minutes idle. The temporary WPR task, monitor, fallback `InvocationProbe`, and AHK boundary probe were then retired. The full evidence remains in [the historical investigation report](bugs/intermittent-slow-invocation.md).

### Test hygiene: stray GUID-named log files in %TEMP%

Reported: 2026-07-10. Fixed: 2026-07-12 (test-only sweep across the commits titled "test: add TempLogger and stop leaking log files in %TEMP%" through "test(parser): pin numeric position, precedence, null warn sink").

**Fix:** `TempLogger : IDisposable` next to `TestNames` wraps a GUID-named `Logger` and deletes `<name>.log` / `.log.old` on dispose; adopted in CrashRepairTests, ForegroundLockSuppressionTests, WindowCloserTests, and TriggerButtonHealerTests (which had hand-rolled the same cleanup). Verified by a clean `%TEMP%` after a full test-project run. A post-landing review pass single-sourced the temp-log-path formula as `Logger.GetLogPath`, consumed by Logger's constructor, TempLogger, and LoggerTests, so the cleanup can no longer silently desync from Logger's naming scheme.

**Coverage gaps from the 2026-07-11 review closed in the same sweep:** SharedState offset independence (tick vs repair record, both directions); Logger's no-rotation-at-exactly-1-MiB boundary; `ResolveKillMethodName` empty-string fallback, a theory over all nine documented method names, and the fallback warning log assertion; ProcessSettingsParser in-range numeric ClickPosition leniency, simple-value-over-object-form precedence, and null-warning-sink safety; LoggerTests' fixed-UTC-timestamp duplication retired into a shared field.

### Invocation dead while an elevated window is active (UIPI filters the AHK hook)

Reported: 2026-07-11. Fixed: 2026-07-12 (environment change, codified in the repo).

**Symptom:** pressing the bound mouse button while an elevated app (tested: Task Manager) held focus did nothing, regardless of what was hovered; the log recorded zero invocations, so TheCloser never ran.

**Root cause:** UIPI. Low-level input hooks of unelevated processes receive no input while an elevated window is active (AutoHotkey's documented administrative-window limitation), and the user's AutoHotkey ran unelevated. The discrimination test pinned the rule to the *active* window's integrity, not the hovered target's: with Task Manager focused, hovering Task Manager AND hovering unelevated Chrome both left the log silent.

**Fix:** run the invocation layer elevated. `install-elevated-ahk.ps1` (repo root, deployed to the Bin folder by `deploy.ps1`) registers a logon scheduled task running `TheCloser.ahk` with highest privileges, once per machine; TheCloser inherits the elevation, which is also what allows closing elevated windows at all (UIPI blocks every kill method across the integrity boundary). `Taskmgr -> SC_CLOSE` added to the deployed appsettings.json. Both invocation-layer files are repo-tracked and self-locating; machine-local paths live in the git-ignored `deploy.settings.psd1`.

**Verified 2026-07-12:** M5 over Task Manager closes it (`Taskmgr -> SC_CLOSE`, single log line, no ladder: message posting needs no activation at same integrity). With Task Manager focused, M5 over a background Chrome viewport closes its active tab via native root activation, and the predicted `AttachThreadInput to the foreground owner failed (error 5)` never appeared: elevation removed the integrity barrier, so the elevated-foreground-owner case degenerated to the ordinary one. The stuck-button healer stayed silent (its success mode).

**Durable learning:** UIPI failures in this tool can present at two layers with identical symptoms; the log discriminates. Zero log lines means the invocation layer never fired (hook filtering; fix is environmental). A logged ladder failure means integrity blocking inside activation or delivery (fix is elevation or method choice, e.g. message-based `SC_CLOSE` needs no activation at all).

### System-wide stuck XBUTTON2 after input attach; all mouse clicks dead

Reported: 2026-07-11. Mitigated: 2026-07-11 in fbd21fb.

**Symptom:** immediately after an `explorer -> CTRL-W` invocation (native activation, 18:11:21), mouse clicks stopped registering system-wide (taskbar included) while the keyboard kept working. Novel behavior, same session as the owner-attach deployment.

**Diagnosis:** `GetAsyncKeyState(VK_XBUTTON2)` read -32768: the OS held mouse button 5, the app's AutoHotkey invocation binding, as pressed. The physical release was lost, most plausibly because AttachThreadInput resynchronizes key state between attached threads and the invocation had just attached to explorer's own input thread (as target and likely foreground owner simultaneously) inside the ~70ms window where the release was in flight. AutoHotkey's mouse hook, believing XBUTTON2 was still held, then poisoned every subsequent click. Earlier same-day owner-attach invocations (Chrome targets) were harmless; the shell-as-target case is what lined up the race.

**Immediate remedy (works if this ever recurs):** inject the missing release, then re-probe:
`mouse_event(0x0100, 0, 0, 2, 0)` (MOUSEEVENTF_XUP, mouseData = XBUTTON2), e.g. via Add-Type P/Invoke in PowerShell; `GetAsyncKeyState(0x06)` should return 0 afterwards.

**Mitigations:** TryActivateNatively detaches both threads immediately after SetForegroundWindow instead of holding the attaches through the 50ms settle wait (fbd21fb, exposure ~70ms to ~1ms). A preventive pre-attach release wait (also fbd21fb) was replaced in 5cd2667 by the reactive `TriggerButtonHealer`: the wait cost ~50-100ms of activation latency on every background-target invocation (AutoHotkey fires on M5-*down*, so the finger is still on the button when activation starts), whereas the healer costs nothing up front. After a close that performed an input attach, the app releases the single-instance guard mutex and lingers up to 2s polling the middle/X buttons; a genuine hold clears itself on release, while a stranded state reads down forever, so anything still down at the deadline gets its release injected (and logged). Unit-tested via injected probe/inject/sleep delegates.

**Residual risk, accepted:** the underlying race is not reproducible on demand; the narrow attach window plus the self-heal bound the damage (~2s of dead mouse, auto-recovered) rather than provably eliminate it. Deliberate anti-goal: no daemon backstop for the healer; a leak requires the sub-millisecond attach race AND an app death inside the 2s monitor, and the daemon's 5s watchdog tick would heal far slower than the in-app monitor does.

**Contingency (decided 2026-07-11):** if the symptom recurs and the healer fails to clear it, either debug the escape route further (the healer's log line plus the manual remedy above are the entry points) or fall back to the preventive pre-attach release wait exactly as it existed in fbd21fb, accepting its ~50-100ms activation latency on background-target invocations.

### Chrome window activation fails when a non-Chrome process holds the foreground

Reported: 2026-07-10. Fixed: 2026-07-11 in 6fbbbc3 (deployed same day).

**Symptom:** invoking TheCloser over a background Chrome window while another process held the foreground failed to activate Chrome, so CTRL-W never reached it or landed elsewhere.

**Root cause:** SetForegroundWindow was denied by a foreground rule the lock-timeout suppression cannot lift (the input lock: the user's last physical input went to the foreground process), and attaching to the *target's* thread confers no rights because the target does not hold them either. All pre-2026-07-10 observations were additionally poisoned by the SystemParametersInfo uiParam/pvParam bug (fixed in 4861ca2) that stranded the system-wide lock timeout at 0.

**Fix:** ForegroundActivator.TryActivateNatively additionally attaches the calling thread to the *current foreground owner's* thread around SetForegroundWindow, borrowing the input-queue state that holds foreground rights; both attaches detach in the rung's finally.

**Verified interactively 2026-07-11** (explorer focused, viewport hover, per-rung trace): base case at 16:27, minimize-then-restore at 16:37, two-windows-hover-the-stale-one at 16:39. All three: child-HWND rung fails (expected; activating child windows directly is unreliable), root rung succeeds natively in ~140ms, click fallback never engages.

**Durable learnings** (full diagnosis in this entry's git history in `BUGS.md`):

- The lock-timeout suppression (ForegroundLockSuppression) lifts only the timeout-based denial rule; the input-lock rule requires sharing the foreground owner's input queue via AttachThreadInput.
- Chrome's HWND hierarchy under the cursor varies by invocation: WindowFromPoint sometimes returns the Chrome_WidgetWin_1 root, sometimes a child. The ladder originally covered both with a target-then-root rung pair; the target rung was later dropped because SetForegroundWindow rejects child HWNDs even with foreground permission (verified in the 2026-07-11 traces: the child rung failed in the same permission context where the root rung succeeded), so the ladder activates the root directly.
- The title bar click fallback works for Chrome and its default Left click point (left+10, top+20) lands above the tab click area (no accidental tab switch), at the cost of ~200ms and a cursor round-trip; it remains the last rung.
- Closing a specific background tab (tab-strip hover) is deliberately unsupported: the app is bound to a mouse button, so Chrome's native middle-click-to-close is already an equivalent gesture away.
- Prior art: a SwitchToThisWindow() fallback was added and reverted (04cbeb7 reverted b971d68) with no recorded motivation; treat as untried rather than rejected if the ladder ever needs another rung.
- An `AttachThreadInput ... failed (error 0)` log line can be benign: same-thread attaches and already-attached pairs return failure without setting an OS error, so error 0 means no diagnosable OS failure rather than a mysterious one.
