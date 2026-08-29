using System.Drawing;

using static TheCloser.Shared.NativeMethods;

namespace TheCloser.Shared;

// Injectable seam over the NativeMethods statics that ForegroundActivator drives, so the
// escalation ladder is testable without touching real windows, input queues, or the cursor.
internal interface INativeWindowApi
{
    IntPtr GetRootWindow(IntPtr hWnd);

    IntPtr GetForegroundWindow();

    uint GetWindowThreadId(IntPtr hWnd);

    // Returns the peer thread id that was attached, or 0 when the attach failed. The caller
    // detaches by that captured id: re-resolving the thread from the window at detach time is a
    // silent no-op once the window is destroyed, which would leak the attachment on a
    // long-lived thread (see the fix design's detach hardening).
    uint AttachThreadInput(IntPtr hWnd);

    bool DetachThreadInput(uint threadId);

    bool SetForegroundWindow(IntPtr hWnd);

    bool TryGetWindowRect(IntPtr hWnd, out RECT rect);

    bool TryGetCursorPosition(out Point position);

    bool SetCursorPosition(int x, int y);

    uint SendInput(INPUT[] inputs);
}
