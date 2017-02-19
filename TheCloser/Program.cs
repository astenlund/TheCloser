using System.Configuration;
using System.Diagnostics;
using WindowsInput;
using WindowsInput.Native;

namespace TheCloser
{
    public class Program
    {
        private static readonly InputSimulator InputSimulator = new InputSimulator();

        public static void Main()
        {
            var windowHandle = NativeMethods.WindowFromPoint(NativeMethods.GetMouseCursorPosition());

            NativeMethods.SetForegroundWindow(windowHandle);

            var process = Process.GetProcessById(NativeMethods.GetProcessIdFromWindowHandle(windowHandle));
            var killMethod = ConfigurationManager.AppSettings[process.ProcessName];

            switch (killMethod?.ToUpperInvariant())
            {
                case "ESCAPE":
                    InputSimulator.Keyboard.KeyPress(VirtualKeyCode.ESCAPE);
                    break;
                case "CTRL-W":
                    InputSimulator.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_W);
                    break;
                case "CTRL-F4":
                    InputSimulator.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.F4);
                    break;
                case "WM_DESTROY":
                    NativeMethods.PostMessage(windowHandle, NativeMethods.WindowNotification.WM_DESTROY);
                    break;
                case "WM_CLOSE":
                    NativeMethods.PostMessage(windowHandle, NativeMethods.WindowNotification.WM_CLOSE);
                    break;
                case "WM_QUIT":
                    NativeMethods.PostMessage(windowHandle, NativeMethods.WindowNotification.WM_QUIT);
                    break;
                default:
                    InputSimulator.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_W);
                    break;
            }
        }
    }
}
