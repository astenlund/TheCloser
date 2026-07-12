using System.Diagnostics;
using System.Runtime.InteropServices;
using GregsStack.InputSimulatorStandard;
using GregsStack.InputSimulatorStandard.Native;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;

using static TheCloser.NativeMethods;
using static TheCloser.TitleBarClickPosition;

namespace TheCloser;

internal class WindowCloser
{
    private const string DefaultKillMethod = "CTRL-W";
    private const TitleBarClickPosition DefaultClickPosition = Left;

    private readonly IConfiguration _config;
    private readonly Logger _logger;
    private readonly IForegroundActivator _activator;
    private readonly Action<VirtualKeyCode[], VirtualKeyCode> _sendKeystroke;
    private readonly Action<TimeSpan> _sleep;
    private readonly Dictionary<string, Action<IntPtr, TitleBarClickPosition>> _killActions;

    public WindowCloser(
        IConfiguration config,
        SharedState sharedState,
        Logger logger,
        IForegroundActivator? activator = null,
        Action<VirtualKeyCode[], VirtualKeyCode>? sendKeystroke = null,
        Action<TimeSpan>? sleep = null)
    {
        _config = config;
        _logger = logger;
        _activator = activator ?? new ForegroundActivator(sharedState, logger);
        _sendKeystroke = sendKeystroke ?? SendKeystrokeViaInputSimulator;
        _sleep = sleep ?? Thread.Sleep;
        _killActions = new Dictionary<string, Action<IntPtr, TitleBarClickPosition>>(StringComparer.OrdinalIgnoreCase)
        {
            { "ALT-F4", (handle, clickPos) => SendKeyPressIfForeground(handle, clickPos, VirtualKeyCode.F4, VirtualKeyCode.LMENU) },
            { "CTRL-F4", (handle, clickPos) => SendKeyPressIfForeground(handle, clickPos, VirtualKeyCode.F4, VirtualKeyCode.CONTROL) },
            { "CTRL-SHIFT-W", (handle, clickPos) => SendKeyPressIfForeground(handle, clickPos, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL, VirtualKeyCode.SHIFT) },
            { "CTRL-W", (handle, clickPos) => SendKeyPressIfForeground(handle, clickPos, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL) },
            { "ESCAPE", (handle, clickPos) => SendKeyPressIfForeground(handle, clickPos, VirtualKeyCode.ESCAPE) },
            { "WM_CLOSE", (handle, _) => PostMessageLogged(GetRootWindow(handle), WindowNotification.WM_CLOSE) },
            { "WM_DESTROY", (handle, _) => PostMessageLogged(GetRootWindow(handle), WindowNotification.WM_DESTROY) },
            { "WM_QUIT", (handle, _) => PostMessageLogged(GetRootWindow(handle), WindowNotification.WM_QUIT) },
            { "SC_CLOSE", (handle, _) => PostMessageLogged(GetRootWindow(handle), WindowNotification.WM_SYSCOMMAND, SC_CLOSE) }
        };
    }

    public bool PerformedInputAttach => _activator.PerformedInputAttach;

    public void CloseWindowUnderCursor()
    {
        if (!TryGetMouseCursorPosition(out var cursorPosition))
        {
            _logger.Log($"Could not read the mouse cursor position (error {Marshal.GetLastPInvokeError()}). Aborting.");

            return;
        }

        var targetWindow = WindowFromPoint(cursorPosition);
        var processId = GetProcessIdFromWindowHandle(targetWindow);

        if (processId == 0)
        {
            _logger.Log("Could not determine the process for the window under the cursor.");

            return;
        }

        Process targetProcess;

        try
        {
            targetProcess = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // The process under the cursor exited between hover and lookup.
            _logger.Log("The process for the window under the cursor is no longer running.");

            return;
        }

        using var _ = targetProcess;

        var settings = ProcessSettingsParser.Parse(_config, targetProcess.ProcessName, _logger.Log);
        var killMethod = ResolveKillMethodName(settings.Method);
        var killAction = _killActions[killMethod];

        _logger.Log($"{targetProcess.ProcessName} -> {killMethod}");

        killAction.Invoke(targetWindow, settings.ClickPosition ?? DefaultClickPosition);
    }

    internal string ResolveKillMethodName(string? configuredMethod)
    {
        var killMethod = configuredMethod ?? DefaultKillMethod;

        if (_killActions.ContainsKey(killMethod))
        {
            return killMethod;
        }

        _logger.Log($"No kill action configured for method '{killMethod}'. Falling back to {DefaultKillMethod}.");

        return DefaultKillMethod;
    }

    internal void SendKeyPressIfForeground(IntPtr targetWindow, TitleBarClickPosition clickPosition, VirtualKeyCode keyCode, params VirtualKeyCode[] modifierKeyCodes)
    {
        if (_activator.TryActivate(targetWindow, clickPosition))
        {
            // The settle delay is deliberately the activator's: activation and injection pace the same input queue.
            _sleep(ForegroundActivator.InputSettleDelay);
            _sendKeystroke(modifierKeyCodes, keyCode);
        }
        else
        {
            _logger.Log($"Failed to set foreground window for window 0x{targetWindow:X}.");
        }
    }

    private static void SendKeystrokeViaInputSimulator(VirtualKeyCode[] modifierKeyCodes, VirtualKeyCode keyCode)
    {
        var keyboard = new InputSimulator().Keyboard;

        if (modifierKeyCodes.Length != 0)
        {
            keyboard.ModifiedKeyStroke(modifierKeyCodes, keyCode);
        }
        else
        {
            keyboard.KeyPress(keyCode);
        }
    }

    private void PostMessageLogged(IntPtr handle, WindowNotification message, uint? param = null)
    {
        if (!PostMessage(handle, message, param))
        {
            _logger.Log($"PostMessage({message}) failed with Win32 error {Marshal.GetLastPInvokeError()} (5 means access denied; the target may be elevated).");
        }
    }
}
