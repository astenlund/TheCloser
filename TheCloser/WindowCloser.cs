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

    private static readonly TimeSpan InputSettleDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CursorPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly IConfiguration _config;
    private readonly SharedState _sharedState;
    private readonly Logger _logger;
    private readonly InputSimulator _inputSimulator;
    private readonly Dictionary<string, Action<IntPtr, TitleBarClickPosition>> _killActions;

    public WindowCloser(IConfiguration config, SharedState sharedState, Logger logger)
    {
        _config = config;
        _sharedState = sharedState;
        _logger = logger;
        _inputSimulator = new InputSimulator();
        _killActions = new Dictionary<string, Action<IntPtr, TitleBarClickPosition>>(StringComparer.OrdinalIgnoreCase)
        {
            { "ALT-F4", (handle, clickPos) => TrySendKeyPress(handle, clickPos, VirtualKeyCode.F4, VirtualKeyCode.LMENU) },
            { "CTRL-F4", (handle, clickPos) => TrySendKeyPress(handle, clickPos, VirtualKeyCode.F4, VirtualKeyCode.CONTROL) },
            { "CTRL-SHIFT-W", (handle, clickPos) => TrySendKeyPress(handle, clickPos, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL, VirtualKeyCode.SHIFT) },
            { "CTRL-W", (handle, clickPos) => TrySendKeyPress(handle, clickPos, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL) },
            { "ESCAPE", (handle, clickPos) => TrySendKeyPress(handle, clickPos, VirtualKeyCode.ESCAPE) },
            { "WM_CLOSE", (handle, _) => PostMessageLogged(GetRootWindow(handle), WindowNotification.WM_CLOSE) },
            { "WM_DESTROY", (handle, _) => PostMessageLogged(GetRootWindow(handle), WindowNotification.WM_DESTROY) },
            { "WM_QUIT", (handle, _) => PostMessageLogged(GetRootWindow(handle), WindowNotification.WM_QUIT) },
            { "SC_CLOSE", (handle, _) => PostMessageLogged(GetRootWindow(handle), WindowNotification.WM_SYSCOMMAND, SC_CLOSE) }
        };
    }

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

    private void PostMessageLogged(IntPtr handle, WindowNotification message, uint? param = null)
    {
        if (!PostMessage(handle, message, param))
        {
            _logger.Log($"PostMessage({message}) failed with Win32 error {Marshal.GetLastPInvokeError()} (5 means access denied; the target may be elevated).");
        }
    }

    private bool TrySetForegroundWindow(IntPtr targetWindow, TitleBarClickPosition clickPosition)
    {
        var rootWindow = GetRootWindow(targetWindow);

        if (IsForeground(targetWindow))
        {
            _logger.Log("Foreground: target was already foreground.");

            return true;
        }

        if (TrySetForegroundWindowNative(targetWindow))
        {
            _logger.Log("Foreground: native activation of the target window succeeded.");

            return true;
        }

        if (rootWindow != targetWindow && TrySetForegroundWindowNative(rootWindow))
        {
            _logger.Log("Foreground: native activation of the root window succeeded.");

            return true;
        }

        if (TrySetForegroundWindowByClicking(rootWindow, clickPosition))
        {
            _logger.Log("Foreground: title bar click fallback succeeded.");

            return true;
        }

        return false;
    }

    private static bool IsForeground(IntPtr targetWindow)
    {
        var rootWindow = GetRootWindow(targetWindow);
        var foregroundWindow = GetForegroundWindow();

        return foregroundWindow == targetWindow ||
               foregroundWindow == rootWindow;
    }

    private bool TrySetForegroundWindowNative(IntPtr targetWindow)
    {
        uint originalTimeout = 0;
        var timeoutDisabled = false;

        try
        {
            if (ForegroundLockTimeout.TryGet(out var currentTimeout))
            {
                if (_sharedState.TryReadTimeoutRepair(out var pendingTimeout))
                {
                    // An earlier restore failed; the pending record's saved value, not the current
                    // (possibly still disabled) system value, is the true original. Never overwrite it.
                    originalTimeout = pendingTimeout;
                    timeoutDisabled = ForegroundLockTimeout.Disable();
                }
                else
                {
                    originalTimeout = currentTimeout;
                    _sharedState.SetTimeoutRepair(originalTimeout);
                    timeoutDisabled = ForegroundLockTimeout.Disable();

                    if (!timeoutDisabled)
                    {
                        _sharedState.ClearTimeoutRepair();
                    }
                }
            }

            AttachThreadInput(targetWindow);

            if (!SetForegroundWindow(targetWindow))
            {
                _logger.Log("SetForegroundWindow returned false.");
            }

            Thread.Sleep(InputSettleDelay);

            return IsForeground(targetWindow);
        }
        finally
        {
            DetachThreadInput(targetWindow);

            if (timeoutDisabled && !TimeoutRepair.RestoreAndClear(_sharedState, originalTimeout))
            {
                _logger.Log("Failed to restore the foreground lock timeout; keeping the repair record for the daemon watchdog.");
            }
        }
    }

    private bool TrySetForegroundWindowByClicking(IntPtr targetWindow, TitleBarClickPosition clickPosition)
    {
        if (!GetWindowRect(targetWindow, out var rect))
        {
            return false;
        }

        if (!TryGetMouseCursorPosition(out var oldPos))
        {
            _logger.Log("Could not save the cursor position; skipping the click fallback.");

            return false;
        }

        try
        {
            var clickY = rect.Top + 20;
            var clickX = clickPosition switch
            {
                Left => rect.Left + 10,
                Center => rect.Left + (rect.Right - rect.Left) / 2,
                _ => throw new ArgumentOutOfRangeException(nameof(clickPosition), clickPosition, null)
            };

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

            if (SendInput((uint)inputs.Length, inputs, INPUT.Size) != inputs.Length)
            {
                _logger.Log($"SendInput injected fewer events than requested (error {Marshal.GetLastPInvokeError()}).");
            }

            Thread.Sleep(InputSettleDelay);

            return IsForeground(targetWindow);
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

            Thread.Sleep(CursorPollInterval);
        }

        return false;
    }

    private void TrySendKeyPress(IntPtr targetWindow, TitleBarClickPosition clickPosition, VirtualKeyCode keyCode, params VirtualKeyCode[] modifierKeyCodes)
    {
        if (TrySetForegroundWindow(targetWindow, clickPosition))
        {
            Thread.Sleep(InputSettleDelay);

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
            _logger.Log($"Failed to set foreground window for {targetWindow}");
        }
    }
}
