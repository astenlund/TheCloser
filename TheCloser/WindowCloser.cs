using System.Diagnostics;
using GregsStack.InputSimulatorStandard;
using GregsStack.InputSimulatorStandard.Native;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using static TheCloser.NativeMethods;

namespace TheCloser;

internal class WindowCloser
{
    private const string DefaultKillMethod = "CTRL-W";

    private static readonly Logger Logger = Logger.Create(Program.AssemblyName);

    private readonly IConfiguration _killMethods;
    private readonly InputSimulator _inputSimulator;
    private readonly Dictionary<string, Action<IntPtr>> _killActions;

    private WindowCloser(IConfiguration killMethods)
    {
        _killMethods = killMethods;
        _inputSimulator = new InputSimulator();
        _killActions = new Dictionary<string, Action<IntPtr>>
        {
            { "ALT-F4", handle => TrySendKeyPress(handle, VirtualKeyCode.F4, VirtualKeyCode.LMENU) },
            { "CTRL-F4", handle => TrySendKeyPress(handle, VirtualKeyCode.F4, VirtualKeyCode.CONTROL) },
            { "CTRL-SHIFT-W", handle => TrySendKeyPress(handle, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL, VirtualKeyCode.SHIFT) },
            { "CTRL-W", handle => TrySendKeyPress(handle, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL) },
            { "ESCAPE", handle => TrySendKeyPress(handle, VirtualKeyCode.ESCAPE) },
            { "WM_CLOSE", handle => PostMessage(handle, WindowNotification.WM_CLOSE) },
            { "WM_DESTROY", handle => PostMessage(handle, WindowNotification.WM_DESTROY) },
            { "WM_QUIT", handle => PostMessage(handle, WindowNotification.WM_QUIT) }
        };
    }

    public static WindowCloser Create(IConfiguration config) => new(config);

    public void CloseWindowUnderCursor()
    {
        var targetWindow = WindowFromPoint(GetMouseCursorPosition());
        var targetProcess = Process.GetProcessById(GetProcessIdFromWindowHandle(targetWindow));
        var killMethod = GetKillMethod(targetProcess) ?? DefaultKillMethod;
        var killAction = GetKillAction(killMethod);

        Logger.Log($"{targetProcess.ProcessName} -> {killMethod}");

        killAction?.Invoke(targetWindow);
    }

    private static bool TrySetForegroundWindow(IntPtr targetWindow)
    {
        var rootWindow = GetRootWindow(targetWindow);

        if (IsForeground(targetWindow, rootWindow))
        {
            return true;
        }

        return TrySetForegroundWindowNative(targetWindow)   || IsForeground(targetWindow, rootWindow) ||
               TrySetForegroundWindowNative(rootWindow)     || IsForeground(targetWindow, rootWindow) ||
               TrySetForegroundWindowByClicking(rootWindow) && IsForeground(targetWindow, rootWindow);
    }

    private static bool IsForeground(IntPtr targetWindow, IntPtr rootWindow)
    {
        var foregroundWindow = GetForegroundWindow();

        return foregroundWindow == targetWindow ||
               foregroundWindow == rootWindow;
    }

    private static bool TrySetForegroundWindowNative(IntPtr targetWindow)
    {
        try
        {
            AllowSetForegroundWindow(GetProcessIdFromWindowHandle(targetWindow));
            AttachThreadInput(targetWindow);

            return SetForegroundWindow(targetWindow) || GetForegroundWindow() == targetWindow;
        }
        finally
        {
            DetachThreadInput(targetWindow);
        }
    }

    private static bool TrySetForegroundWindowByClicking(IntPtr targetWindow)
    {
        if (GetWindowRect(targetWindow, out var rect))
        {
            GetCursorPos(out var oldPos);

            try
            {
                var width = rect.Right - rect.Left;
                var titleBarX = rect.Left + width / 2;
                var titleBarY = rect.Top + 2;

                if (!TryMoveCursor(titleBarX, titleBarY))
                {
                    return false;
                }

                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

                TryMoveCursor(oldPos.X, oldPos.Y);

                return GetForegroundWindow() == targetWindow;
            }
            finally
            {
                TryMoveCursor(oldPos.X, oldPos.Y);
            }
        }

        return false;
    }

    private static bool TryMoveCursor(int x, int y)
    {
        SetCursorPos(x, y);

        for (var attempts = 0; attempts < 5; attempts++)
        {
            GetCursorPos(out var currentPos);

            if (currentPos.X == x && currentPos.Y == y)
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    private void TrySendKeyPress(IntPtr targetWindow, VirtualKeyCode keyCode, params VirtualKeyCode[] modifierKeyCodes)
    {
        if (TrySetForegroundWindow(targetWindow))
        {
            Thread.Sleep(50);

            if (modifierKeyCodes.Length != 0)
            {
                _inputSimulator.Keyboard.ModifiedKeyStroke(modifierKeyCodes, keyCode);
            }
            else
            {
                _inputSimulator.Keyboard.KeyPress(keyCode);
            }
        }
        else
        {
            Logger.Log($"Failed to set foreground window for {targetWindow}");
        }
    }

    private string? GetKillMethod(Process process) => _killMethods[process.ProcessName]?.ToUpperInvariant();

    private Action<IntPtr>? GetKillAction(string killMethod) => _killActions.GetValueOrDefault(killMethod);
}
