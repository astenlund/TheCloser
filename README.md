# The Closer

This is a utility that, when executed, closes the window or tab currently under the mouse cursor, even if the window is not active (i.e. does not have focus). Multiple methods of closing a window are supported and can be configured per application via the appsettings.json file. The default behavior is CTRL-W.

## How it works

The normal mouse-button path launches no executable per press. `TheCloser.ahk` writes the press timestamp to shared memory and signals `TheCloser.Daemon.exe`, which samples the cursor and runs the close pipeline in-process. The daemon owns the 200ms throttle, measured press to press from the timestamp the script wrote, so a mouse that fires twice for one press is debounced even when the previous close was slow to finish. It also hot-reloads `appsettings.json` and preserves the crash-repair record for the system foreground lock timeout. If the daemon event is unavailable, the script falls back to launching `TheCloser.exe`; that standalone path remains fully functional and starts the daemon for later presses. The daemon can be stopped with `TheCloser.Daemon.exe --stop`.

## Supported methods

- Keyboard: ESCAPE
- Keyboard: ALT-F4
- Keyboard: CTRL-F4
- Keyboard: CTRL-W
- Keyboard: CTRL-SHIFT-W
- System Command: SC_CLOSE
- Windows Message: WM_DESTROY (hazardous: posting WM_DESTROY cross-process makes the target run its destruction cleanup while the window handle stays alive; prefer WM_CLOSE or SC_CLOSE)
- Windows Message: WM_CLOSE
- Windows Message: WM_QUIT (hazardous: kills the target's message loop, bypassing any save/confirm-on-close handling)

## Invocation binding

The app is designed to be bound to a mouse button. The reference binding is `TheCloser.ahk` (AutoHotkey v1), which signals the daemon on Mouse5 (XButton2) and uses the standalone executable only as a fallback; `deploy.ps1` copies it next to the binaries.

AutoHotkey must run **elevated** for the binding to work while an elevated window (e.g. Task Manager) is active: UIPI silently drops low-level hook events for unelevated processes whenever the active window has higher integrity, so an unelevated AutoHotkey never sees the button press in that state. Elevation also propagates to the daemon and fallback app, which is what allows them to close elevated windows at all (message posting and input injection across the integrity boundary are otherwise blocked). `deploy.ps1` installs the `TheCloser AutoHotkey (elevated)` logon task on first deploy, restarts it after later deploys, and re-registers it whenever the live task's definition differs from the one in `TheCloserTask.psm1` (which both scripts import). The task runs at normal priority on purpose: Task Scheduler's default is below normal, the daemon inherits it, and a below-normal daemon starves under CPU load, turning one keystroke injection into a multi-second stall. `install-elevated-ahk.ps1` remains available for manual installation or repair: run it elevated from the deploy folder, where `TheCloserTask.psm1` sits next to it, and pass `-AhkExePath` if AutoHotkey is not at `C:\Program Files\AutoHotkey\AutoHotkeyU64.exe`. Remove any old unelevated autostart.

## Building and deploying

`dotnet build` builds the solution; the executables publish as Native AOT. To deploy, copy `deploy.settings.example.psd1` to `deploy.settings.psd1` (git-ignored) and set `Destination` to the folder the binaries should live in, then run:

```powershell
pwsh ./deploy.ps1
```

The script stops the running daemon, publishes in Release, copies `TheCloser.exe`, `TheCloser.Daemon.exe`, `TheCloser.ahk`, `install-elevated-ahk.ps1`, and `TheCloserTask.psm1` to the destination, and then restarts the logon task, or re-registers it when it is missing or its definition has drifted, which needs one elevation prompt (stopping a running daemon, which is elevated, raises another one on every deploy). Keep `appsettings.json` in the destination by hand; the deploy never touches it.

## Configuration

Applications can be configured with either a simple method string or an object with method and click position settings. The configuration is read from an appsettings.json file in the directory of the deployed executable and is maintained by hand there; the repository carries no appsettings.json. The daemon watches the file for changes and keeps the last good snapshot if a reload is malformed or temporarily unreadable.

### Example appsettings.json

```json
{
    "devenv": "CTRL-F4",
    "notepad": "WM_CLOSE",
    "sublime_merge": {
        "Method": "CTRL-W",
        "ClickPosition": "Center"
    }
}
```

### Click Position Options

When using keyboard methods that require window activation, you can specify where to click on the title bar:
- `Left` (default): Click on the left side of the title bar
- `Center`: Click in the center of the title bar

## Logs and troubleshooting

The daemon logs to `%TEMP%\TheCloser.Daemon.log` and the fallback app to `%TEMP%\TheCloser.log`; each rotates to `.log.old` above 1 MB when the process starts. Every non-empty line carries a UTC timestamp. A normal press with a keyboard method produces three daemon lines: the press-to-handler latency with the trigger button, the target process and method, and the foreground outcome. Lines that mean something went wrong:

- `Activation (deferred; queued N ms behind the previous handling)`: the press waited for an earlier close to finish. Followed by `Activation skipped: the press was within 200ms of the previous press` when it was a mouse double activation.
- `Activation took N ms (settings ..., close ...)` and `Keystroke injection took N ms; foreground now: ...`: a handling or a keystroke injection reached 300 ms. A close measured 110 to 133 ms before the task ran at normal priority; with injection now about 1 ms it should take about 65 ms, most of it the deliberate settle sleep.
- `Raised the process priority class to Normal`: the daemon was launched below Normal priority by something other than the registered task, which would have made it starve under CPU load.
- `LowLevelHooksTimeout: ...` at startup: the per-hook timeout Windows applies to unresponsive low-level keyboard hooks, for reading an injection stall against.

`TheCloser.Daemon.exe --stop` stops the daemon gracefully; the next press through the script starts it again.
