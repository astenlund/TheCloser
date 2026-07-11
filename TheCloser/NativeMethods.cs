using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Runtime.InteropServices;

using static System.Runtime.InteropServices.CharSet;
using static System.Runtime.InteropServices.UnmanagedType;

namespace TheCloser;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static class NativeMethods
{
    public const int SC_CLOSE = 0xF060;
    public const uint GA_ROOT = 2;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XUP = 0x0100;
    public const uint XBUTTON1 = 0x0001;
    public const uint XBUTTON2 = 0x0002;
    public const uint INPUT_MOUSE = 0;
    public const int VK_MBUTTON = 0x04;
    public const int VK_XBUTTON1 = 0x05;
    public const int VK_XBUTTON2 = 0x06;

    public enum WindowNotification : uint
    {
        WM_DESTROY = 0x0002,
        WM_CLOSE = 0x0010,
        WM_QUIT = 0x0012,
        WM_SYSCOMMAND = 0x0112
    }

    public static bool TryGetMouseCursorPosition(out Point position)
    {
        var success = GetCursorPos(out var lpPoint);
        position = lpPoint;

        return success;
    }

    public static int GetProcessIdFromWindowHandle(IntPtr hWnd)
    {
        _ = GetWindowThreadProcessId(hWnd, out var lpdwProcessId);

        return (int)lpdwProcessId;
    }

    public static bool PostMessage(IntPtr hWnd, WindowNotification message, uint? param = null)
    {
        var wParam = param is not null ? new IntPtr(param.Value) : IntPtr.Zero;

        return PostMessage(hWnd, message, wParam, IntPtr.Zero);
    }

    public static IntPtr GetRootWindow(IntPtr hWnd)
    {
        return GetAncestor(hWnd, GA_ROOT);
    }

    public static bool AttachThreadInput(IntPtr hWnd)
    {
        var currentThreadId = GetCurrentThreadId();
        var targetThreadId = GetWindowThreadProcessId(hWnd, out _);

        return AttachThreadInput(currentThreadId, targetThreadId, true);
    }

    public static bool DetachThreadInput(IntPtr hWnd)
    {
        var currentThreadId = GetCurrentThreadId();
        var targetThreadId = GetWindowThreadProcessId(hWnd, out _);

        return AttachThreadInput(currentThreadId, targetThreadId, false);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>
    ///     Returns a negative value (high bit set) while the key or mouse button is currently down.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(Bool)]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [MarshalAs(LPArray), In] INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
        public static int Size => Marshal.SizeOf<INPUT>();
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr WindowFromPoint(Point p);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [return: MarshalAs(Bool)]
    [DllImport("user32.dll", CharSet = Auto, SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, [MarshalAs(U4)] WindowNotification Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    ///     Activates the window and brings its creating thread to the foreground. The system restricts which processes
    ///     may take the foreground; when the call is denied, Windows flashes the taskbar button instead.
    /// </summary>
    [return: MarshalAs(Bool)]
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    ///     Retrieves a handle to the foreground window; can be zero in transient states such as a window losing activation.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;

        public static implicit operator Point(POINT p)
        {
            return new Point(p.X, p.Y);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
