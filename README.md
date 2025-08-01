# The Closer

This is a utility that, when executed, closes the window or tab currently under the mouse cursor, even if the window is not active (i.e. does not have focus). Multiple methods of closing a window are supported and can be configured per application via the appsettings.json file. The default behavior is CTRL-W.

## Supported methods

- Keyboard: ESCAPE
- Keyboard: ALT-F4
- Keyboard: CTRL-F4
- Keyboard: CTRL-W
- Keyboard: CTRL-SHIFT-W
- System Command: SC_CLOSE
- Windows Message: WM_DESTROY
- Windows Message: WM_CLOSE
- Windows Message: WM_QUIT

## Configuration

Applications can be configured with either a simple method string or an object with method and click position settings.

### Example appsettings.json

```json
{
    "devenv": "CTRL-F4",
    "notepad": "WM_QUIT",
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
