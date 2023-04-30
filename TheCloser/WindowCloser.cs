using System.Diagnostics;
using System.Runtime.InteropServices;
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
        var targetHandle = WindowFromPoint(GetMouseCursorPosition());
        var targetProcess = Process.GetProcessById(GetProcessIdFromWindowHandle(targetHandle));
        var killMethod = GetKillMethod(targetProcess) ?? DefaultKillMethod;
        var killAction = GetKillAction(killMethod);

        Logger.Log($"{targetProcess.ProcessName} -> {killMethod}");

        killAction?.Invoke(targetHandle);
    }

    private static bool TrySetForegroundWindow(IntPtr windowHandle)
    {
        if (GetForegroundWindow() == windowHandle)
        {
            return true;
        }

        if (SetForegroundWindow(windowHandle) || GetForegroundWindow() == windowHandle)
        {
            return true;
        }

        Logger.Log($"Window activation failed (error code: {Marshal.GetLastWin32Error()})");

        if (SetForegroundWindow(GetRootWindow(windowHandle)) || GetForegroundWindow() == windowHandle)
        {
            return true;
        }

        Logger.Log($"Root window activation failed (error code: {Marshal.GetLastWin32Error()})");

        return false;
    }

    private void TrySendKeyPress(IntPtr handle, VirtualKeyCode keyCode, params VirtualKeyCode[] modifierKeyCodes)
    {
        if (TrySetForegroundWindow(handle))
        {
            if (modifierKeyCodes.Any())
            {
                _inputSimulator.Keyboard.ModifiedKeyStroke(modifierKeyCodes, keyCode);
            }
            else
            {
                _inputSimulator.Keyboard.KeyPress(keyCode);
            }
        }
    }

    private string? GetKillMethod(Process process) => _killMethods[process.ProcessName]?.ToUpperInvariant();

    private Action<IntPtr>? GetKillAction(string killMethod) => _killActions.TryGetValue(killMethod, out var killAction) ? killAction : null;
}
