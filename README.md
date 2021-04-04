# The Closer

This is a utility that, when executed, closes the window or tab currently under the mouse cursor, even if the window is not active (i.e. does not have focus). Multiple methods of closing a window are supported and can be configured per application via the appsettings.json file. The default behavior is CTRL-W.

## Supported methods

- Windows Message: WM_DESTROY
- Windows Message: WM_CLOSE
- Windows Message: WM_QUIT
- Keyboard: ESCAPE
- Keyboard: ALT-F4
- Keyboard: CTRL-W
- Keyboard: CTRL-F4

## Example appsettings.json

```javascript
{
    "Calculator": "WM_CLOSE",
    "devenv": "CTRL-F4",
    "MicrosoftEdge": "CTRL-W",
    "mpc-hc64": "ALT-F4",
    "notepad": "WM_QUIT",
    "nwc2": "CTRL-F4",
    "pageant": "ESCAPE",
    "PicasaPhotoViewer": "WM_CLOSE",
    "PicoViewer": "WM_CLOSE",
    "Rambox": "WM_CLOSE",
    "Resilio Sync": "ALT-F4",
    "rider64": "CTRL-F4",
    "SystemSettings": "WM_CLOSE",
    "TARGETGUI": "WM_CLOSE",
    "TeamViewer": "CTRL-F4",
    "WinStore.App": "WM_CLOSE"
}
```
