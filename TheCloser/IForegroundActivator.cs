namespace TheCloser;

// Seam between WindowCloser and the activation ladder so dispatch tests can script activation
// outcomes without touching real windows.
internal interface IForegroundActivator
{
    bool PerformedInputAttach { get; }

    bool TryActivate(IntPtr targetWindow, TitleBarClickPosition clickPosition);
}
