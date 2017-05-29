using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using WindowsInput;
using WindowsInput.Native;

namespace TheCloser
{
    public class Program
    {
        private const string DefaultKillMethod = "CTRL-W";

        private static readonly IReadOnlyDictionary<string, Action<IntPtr>> KillActions = new Dictionary<string, Action<IntPtr>>
            {
                {"WM_DESTROY", handle => NativeMethods.PostMessage(handle, NativeMethods.WindowNotification.WM_DESTROY)},
                {"WM_CLOSE", handle => NativeMethods.PostMessage(handle, NativeMethods.WindowNotification.WM_CLOSE)},
                {"WM_QUIT", handle => NativeMethods.PostMessage(handle, NativeMethods.WindowNotification.WM_QUIT)},
                {"ESCAPE", handle => TrySendKeyPress(handle, VirtualKeyCode.ESCAPE)},
                {"ALT-F4", handle => TrySendKeyPress(handle, VirtualKeyCode.F4, VirtualKeyCode.LMENU)},
                {"CTRL-W", handle => TrySendKeyPress(handle, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL)},
                {"CTRL-F4", handle => TrySendKeyPress(handle, VirtualKeyCode.F4, VirtualKeyCode.CONTROL)}
            };

        private static readonly InputSimulator InputSimulator = new InputSimulator();

        public static void Main()
        {
            var targetHandle = NativeMethods.WindowFromPoint(NativeMethods.GetMouseCursorPosition());
            var targetProcess = Process.GetProcessById(NativeMethods.GetProcessIdFromWindowHandle(targetHandle));

            GetKillAction(GetKillMethod(targetProcess))?.Invoke(targetHandle);
        }

        private static string GetKillMethod(Process process)
        {
            return ConfigurationManager.AppSettings[process.ProcessName]?.ToUpperInvariant() ?? DefaultKillMethod;
        }

        private static Action<IntPtr> GetKillAction(string killMethod)
        {
            return KillActions.TryGetValue(killMethod, out Action<IntPtr> killAction) ? killAction : null;
        }

        private static void TrySendKeyPress(IntPtr handle, VirtualKeyCode keyCode, VirtualKeyCode modifierKeyCode = default(VirtualKeyCode))
        {
            if (!TrySetForegroundWindow(handle))
            {
                return;
            }

            if (modifierKeyCode != default(VirtualKeyCode))
            {
                InputSimulator.Keyboard.ModifiedKeyStroke(modifierKeyCode, keyCode);
            }
            else
            {
                InputSimulator.Keyboard.KeyPress(keyCode);
            }
        }

        private static bool TrySetForegroundWindow(IntPtr windowHandle)
        {
            return NativeMethods.GetForegroundWindow() == windowHandle ||
                   NativeMethods.SetForegroundWindow(windowHandle);
        }
    }
}
