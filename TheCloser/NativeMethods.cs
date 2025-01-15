using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Runtime.InteropServices;
using static System.Runtime.InteropServices.CallingConvention;
using static System.Runtime.InteropServices.CharSet;

namespace TheCloser;

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal static class NativeMethods
{
    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_RESTORE = 0xF120;
    public const uint GA_ROOT = 2;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    public enum WindowNotification : uint
    {
        WM_DESTROY = 0x0002,
        WM_CLOSE = 0x0010,
        WM_QUIT = 0x0012
    }

    public static Point GetMouseCursorPosition()
    {
        GetCursorPos(out var lpPoint);
        return lpPoint;
    }

    public static int GetProcessIdFromWindowHandle(IntPtr hWnd)
    {
        _ = GetWindowThreadProcessId(hWnd, out var lpdwProcessId);
        return (int)lpdwProcessId;
    }

    public static void PostMessage(IntPtr hWnd, WindowNotification message)
    {
        PostMessage(hWnd, (uint)message, IntPtr.Zero, IntPtr.Zero);
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

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll", SetLastError=true)]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);
    
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int X, int Y);
    
    [DllImport("user32.dll", CharSet = Auto, CallingConvention = StdCall)]
    public static extern void mouse_event(
        uint dwFlags,
        uint dx,
        uint dy,
        uint dwData,
        UIntPtr dwExtraInfo
    );

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr WindowFromPoint(Point p);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("user32.dll", CharSet = Auto, SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    ///     Brings the thread that created the specified window into the foreground and activates the window. Keyboard input is
    ///     directed to the window, and various visual cues are changed for the user. The system assigns a slightly higher
    ///     priority to the thread that created the foreground window than it does to other threads.
    ///     <para>See for https://msdn.microsoft.com/en-us/library/windows/desktop/ms633539%28v=vs.85%29.aspx more information.</para>
    /// </summary>
    /// <param name="hWnd">
    ///     C++ ( hWnd [in]. Type: HWND )<br />A handle to the window that should be activated and brought to the foreground.
    /// </param>
    /// <returns>
    ///     <c>true</c> or nonzero if the window was brought to the foreground, <c>false</c> or zero If the window was not
    ///     brought to the foreground.
    /// </returns>
    /// <remarks>
    ///     The system restricts which processes can set the foreground window. A process can set the foreground window only if
    ///     one of the following conditions is true:
    ///     <list type="bullet">
    ///     <listheader>
    ///         <term>Conditions</term><description></description>
    ///     </listheader>
    ///     <item>The process is the foreground process.</item>
    ///     <item>The process was started by the foreground process.</item>
    ///     <item>The process received the last input event.</item>
    ///     <item>There is no foreground process.</item>
    ///     <item>The process is being debugged.</item>
    ///     <item>The foreground process is not a Modern Application or the Start Screen.</item>
    ///     <item>The foreground is not locked (see LockSetForegroundWindow).</item>
    ///     <item>The foreground lock time-out has expired (see SPI_GETFOREGROUNDLOCKTIMEOUT in SystemParametersInfo).</item>
    ///     <item>No menus are active.</item>
    ///     </list>
    ///     <para>
    ///     An application cannot force a window to the foreground while the user is working with another window.
    ///     Instead, Windows flashes the taskbar button of the window to notify the user.
    ///     </para>
    ///     <para>
    ///     A process that can set the foreground window can enable another process to set the foreground window by
    ///     calling the AllowSetForegroundWindow function. The process specified by dwProcessId loses the ability to set
    ///     the foreground window the next time the user generates input, unless the input is directed at that process, or
    ///     the next time a process calls AllowSetForegroundWindow, unless that process is specified.
    ///     </para>
    ///     <para>
    ///     The foreground process can disable calls to SetForegroundWindow by calling the LockSetForegroundWindow
    ///     function.
    ///     </para>
    /// </remarks>
    // For Windows Mobile, replace user32.dll with coredll.dll
    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    ///     Retrieves a handle to the foreground window (the window with which the user is currently working). The system
    ///     assigns a slightly higher priority to the thread that creates the foreground window than it does to other threads.
    ///     <para>See https://msdn.microsoft.com/en-us/library/windows/desktop/ms633505%28v=vs.85%29.aspx for more information.</para>
    /// </summary>
    /// <returns>
    ///     C++ ( Type: Type: HWND )<br /> The return value is a handle to the foreground window. The foreground window
    ///     can be NULL in certain circumstances, such as when a window is losing activation.
    /// </returns>
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }

        public POINT(Point pt) : this(pt.X, pt.Y) { }

        public static implicit operator Point(POINT p)
        {
            return new Point(p.X, p.Y);
        }

        public static implicit operator POINT(Point p)
        {
            return new POINT(p.X, p.Y);
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
