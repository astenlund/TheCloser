using System.Drawing;

using static TheCloser.NativeMethods;

namespace TheCloser;

// Injectable seam over the NativeMethods statics that ForegroundActivator drives, so the
// escalation ladder is testable without touching real windows, input queues, or the cursor.
internal interface INativeWindowApi
{
    IntPtr GetRootWindow(IntPtr hWnd);

    IntPtr GetForegroundWindow();

    uint GetWindowThreadId(IntPtr hWnd);

    bool AttachThreadInput(IntPtr hWnd);

    bool DetachThreadInput(IntPtr hWnd);

    bool SetForegroundWindow(IntPtr hWnd);

    bool TryGetWindowRect(IntPtr hWnd, out RECT rect);

    bool TryGetCursorPosition(out Point position);

    bool SetCursorPosition(int x, int y);

    uint SendInput(INPUT[] inputs);
}
