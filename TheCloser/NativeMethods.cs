using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace TheCloser
{
    public static class NativeMethods
    {
        public static Process GetProcessFromMouseCursorPosition()
        {
            var lpPoint = GetMouseCursorPosition();
            var hWnd = WindowFromPoint(lpPoint);
            var lpdwProcessId = GetProcessIdFromWindowHandle(hWnd);
            return Process.GetProcessById((int)lpdwProcessId);
        }

        public static Point GetMouseCursorPosition()
        {
            POINT lpPoint;
            GetCursorPos(out lpPoint);
            return lpPoint;
        }

        public static uint GetProcessIdFromWindowHandle(IntPtr hWnd)
        {
            uint lpdwProcessId;
            GetWindowThreadProcessId(hWnd, out lpdwProcessId);
            return lpdwProcessId;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr WindowFromPoint(Point p);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
    }
}
