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
            { "WM_QUIT", handle => PostMessage(handle, WindowNotification.WM_QUIT) },
            { "SC_CLOSE", handle => PostMessage(handle, WindowNotification.WM_SYSCOMMAND, SC_CLOSE) }
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
        uint originalTimeout = 0;
        var timeoutDisabled = false;
        
        try
        {
            if (SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref originalTimeout, 0))
            {
                SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);
                timeoutDisabled = true;
            }
            
            AllowSetForegroundWindow(GetProcessIdFromWindowHandle(targetWindow));
            AttachThreadInput(targetWindow);
            SetForegroundWindow(targetWindow);
            SwitchToThisWindow(targetWindow, false);

            Thread.Sleep(50);
            
            return GetForegroundWindow() == targetWindow;
        }
        finally
        {
            DetachThreadInput(targetWindow);
            
            if (timeoutDisabled)
            {
                SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, originalTimeout, IntPtr.Zero, SPIF_SENDCHANGE);
            }
        }
    }

    private static bool TrySetForegroundWindowByClicking(IntPtr targetWindow)
    {
        if (!GetWindowRect(targetWindow, out var rect))
        {
            return false;
        }

        GetCursorPos(out var oldPos);

        try
        {
            // Single click at top-left corner
            var clickX = rect.Left + 10;
            var clickY = rect.Top + 10;

            if (!TryMoveCursor(clickX, clickY))
            {
                return false;
            }

            var inputs = new INPUT[2];
            
            // Mouse down
            inputs[0].type = INPUT_MOUSE;
            inputs[0].U.mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
            
            // Mouse up
            inputs[1].type = INPUT_MOUSE;
            inputs[1].U.mi.dwFlags = MOUSEEVENTF_LEFTUP;
            
            var result = SendInput(2, inputs, INPUT.Size);
            
            Thread.Sleep(50);
            
            return result == 2 && GetForegroundWindow() == targetWindow;
        }
        finally
        {
            TryMoveCursor(oldPos.X, oldPos.Y);
        }
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
