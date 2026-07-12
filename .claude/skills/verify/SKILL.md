---
name: verify
description: Drive the real TheCloser.exe end to end on the live desktop against sacrificial windows, without touching the user's windows, daemon, or config.
---

# Verifying TheCloser end to end

The runtime surface is the whole flow: config resolution, activation ladder, keystroke or message dispatch, window closes. Unit tests cover the seams; this recipe observes the real thing.

## Build and locate

```
dotnet build C:/Git/TheCloser --no-incremental
```

Debug exe: `TheCloser/bin/Debug/net10.0-windows/win-x64/TheCloser.exe` (the daemon exe sits alongside via the ProjectReference copy). Debug is JIT; Native AOT only applies to published output.

## Drive recipe

1. **Sacrificial targets**: host your own WinForms windows from `pwsh -NoProfile` helper processes (a `[System.Windows.Forms.Application]::Run($form)` script taking title/position params). Do NOT use `Start-Process notepad`/`mspaint`: Store app-execution aliases fail with "cannot find all the information required". Position matters: two windows spawned at CenterScreen stack, so offset the foreground-stealer.
2. **Transient config**: write `appsettings.json` into the Debug exe directory mapping `pwsh` to a method; `ALT-F4` closes a plain WinForms form AND exercises the full `SendKeyPressIfForeground` path (activation ladder + injected keystroke). Delete the file in a `finally`.
3. **Safety guard (mandatory)**: before each invocation, `WindowFromPoint` + `GetAncestor(GA_ROOT)` + `GetWindowThreadProcessId` and abort unless the PID under the cursor is your sacrificial window's. A miss closes a real user window.
4. **Two ladder paths**: run once with the target in the background while another window you own holds foreground (log line: `Foreground: native activation of the root window succeeded.`) and once with the target already foreground (`Foreground: target was already foreground.`).
5. **Evidence**: snapshot `%TEMP%\TheCloser.log` length before, read the delta after (open with FileShare.ReadWrite; the logger holds no lock but the daemon may append). Expect `<process> -> <METHOD>` lines, the ladder line, and a blank separator per invocation.
6. **Courtesy**: save and restore the cursor position; kill leftover helper processes in a `finally`.

## Cautions

- A deployed daemon is usually running; the debug app will use its MMF pin and skip spawning. NEVER stop a daemon you did not start. If no daemon was running before and your run spawned the debug one, stop it afterwards with `TheCloser.Daemon.exe --stop` from the Debug directory.
- The run mutates and restores the real foreground lock timeout (production behavior with crash repair); that is expected, not a defect.
- Invocations under 200ms apart hit the throttle; leave 500ms+ between runs.
- A run that performed an input attach lingers up to 2s (TriggerButtonHealer); wait for exit with a 10s timeout rather than assuming instant return.

Working reference implementation from 2026-07-12: a drive script following exactly this shape verified the injectability-seam landing (two runs, both ladder paths, forms closed, log evidence captured).
