using System.Drawing;

namespace TheCloser.Shared;

internal sealed class NativeWindowApi : INativeWindowApi
{
    public IntPtr GetRootWindow(IntPtr hWnd) => NativeMethods.GetRootWindow(hWnd);

    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public uint GetWindowThreadId(IntPtr hWnd) => NativeMethods.GetWindowThreadProcessId(hWnd, out _);

    public uint AttachThreadInput(IntPtr hWnd) => NativeMethods.AttachThreadInputToWindow(hWnd);

    public bool DetachThreadInput(uint threadId) => NativeMethods.DetachThreadInputFromThread(threadId);

    public bool SetForegroundWindow(IntPtr hWnd) => NativeMethods.SetForegroundWindow(hWnd);

    public bool TryGetWindowRect(IntPtr hWnd, out NativeMethods.RECT rect) => NativeMethods.GetWindowRect(hWnd, out rect);

    public bool TryGetCursorPosition(out Point position) => NativeMethods.TryGetMouseCursorPosition(out position);

    public bool SetCursorPosition(int x, int y) => NativeMethods.SetCursorPos(x, y);

    public uint SendInput(NativeMethods.INPUT[] inputs) => NativeMethods.SendInput((uint)inputs.Length, inputs, NativeMethods.INPUT.Size);
}
