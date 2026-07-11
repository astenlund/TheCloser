# The Closer

This is a utility that, when executed, closes the window or tab currently under the mouse cursor, even if the window is not active (i.e. does not have focus). Multiple methods of closing a window are supported and can be configured per application via the appsettings.json file. The default behavior is CTRL-W.

## How it works

Invoking the executable closes the window or tab under the cursor and exits. A small background daemon (`TheCloser.Daemon.exe`, auto-started on first invocation) keeps shared state alive between invocations: a 200ms throttle that absorbs accidental double-triggers, and a crash-repair record that restores the system foreground lock timeout if the app is killed mid-operation. The daemon can be stopped with `TheCloser.Daemon.exe --stop`.

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

The app is designed to be bound to a mouse button. The reference binding is `TheCloser.ahk` (AutoHotkey v1), which launches the executable on Mouse5 (XButton2); `deploy.ps1` copies it next to the binaries.

AutoHotkey must run **elevated** for the binding to work while an elevated window (e.g. Task Manager) is active: UIPI silently drops low-level hook events for unelevated processes whenever the active window has higher integrity, so an unelevated AutoHotkey never sees the button press in that state. Elevation also propagates to TheCloser, which is what allows it to close elevated windows at all (message posting and input injection across the integrity boundary are otherwise blocked). Run `install-elevated-ahk.ps1` once per machine from an elevated shell to register a logon scheduled task that starts the script elevated, and remove any old unelevated autostart.

## Configuration

Applications can be configured with either a simple method string or an object with method and click position settings. The configuration is read from an appsettings.json file in the directory of the deployed executable and is maintained by hand there; the repository carries no appsettings.json.

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
