# The Closer

This is a utility that, when executed, closes the window or tab currently under the mouse cursor, even if the window is not active (i.e. does not have focus). Multiple methods of closing a window are supported and can be configured per application via the appsettings.json file. The default behavior is CTRL-W.

## How it works

The normal mouse-button path launches no executable per press. `TheCloser.ahk` writes the press timestamp to shared memory and signals `TheCloser.Daemon.exe`, which samples the cursor and runs the close pipeline in-process. The daemon owns the 200ms throttle, hot-reloads `appsettings.json`, and preserves the crash-repair record for the system foreground lock timeout. If the daemon event is unavailable, the script falls back to launching `TheCloser.exe`; that standalone path remains fully functional and starts the daemon for later presses. The daemon can be stopped with `TheCloser.Daemon.exe --stop`.

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

AutoHotkey must run **elevated** for the binding to work while an elevated window (e.g. Task Manager) is active: UIPI silently drops low-level hook events for unelevated processes whenever the active window has higher integrity, so an unelevated AutoHotkey never sees the button press in that state. Elevation also propagates to the daemon and fallback app, which is what allows them to close elevated windows at all (message posting and input injection across the integrity boundary are otherwise blocked). `deploy.ps1` installs the `TheCloser AutoHotkey (elevated)` logon task on first deploy and restarts it after later deploys. `install-elevated-ahk.ps1` remains available for manual installation or repair; remove any old unelevated autostart.

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
