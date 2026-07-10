using System.Diagnostics;
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
            { "WM_CLOSE", (handle, _) => PostMessage(handle, WindowNotification.WM_CLOSE) },
            { "WM_DESTROY", (handle, _) => PostMessage(handle, WindowNotification.WM_DESTROY) },
            { "WM_QUIT", (handle, _) => PostMessage(handle, WindowNotification.WM_QUIT) },
            { "SC_CLOSE", (handle, _) => PostMessage(handle, WindowNotification.WM_SYSCOMMAND, SC_CLOSE) }
        };
    }

    public void CloseWindowUnderCursor()
    {
        var targetWindow = WindowFromPoint(GetMouseCursorPosition());
        var processId = GetProcessIdFromWindowHandle(targetWindow);

        if (processId == 0)
        {
            _logger.Log("Could not determine the process for the window under the cursor.");

            return;
        }

        var targetProcess = Process.GetProcessById(processId);
        var settings = ProcessSettingsParser.Parse(_config, targetProcess.ProcessName, _logger.Log);
        var killMethod = settings.Method ?? DefaultKillMethod;
        var killAction = GetKillAction(killMethod);

        if (killAction is null)
        {
            _logger.Log($"No kill action configured for method '{killMethod}'. Falling back to {DefaultKillMethod}.");
            killAction = _killActions[DefaultKillMethod];
        }

        _logger.Log($"{targetProcess.ProcessName} -> {killMethod}");

        killAction.Invoke(targetWindow, settings.ClickPosition ?? DefaultClickPosition);
    }

    private bool TrySetForegroundWindow(IntPtr targetWindow, TitleBarClickPosition clickPosition)
    {
        var rootWindow = GetRootWindow(targetWindow);

        return IsForeground(targetWindow) ||
               TrySetForegroundWindowNative(targetWindow) ||
               TrySetForegroundWindowNative(rootWindow) ||
               TrySetForegroundWindowByClicking(rootWindow, clickPosition);
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
            if (ForegroundLockTimeout.TryGet(out originalTimeout))
            {
                _sharedState.SetTimeoutRepair(originalTimeout);
                ForegroundLockTimeout.Disable();
                timeoutDisabled = true;
            }

            AllowSetForegroundWindow(GetProcessIdFromWindowHandle(targetWindow));
            AttachThreadInput(targetWindow);
            SetForegroundWindow(targetWindow);

            Thread.Sleep(InputSettleDelay);

            return IsForeground(targetWindow);
        }
        finally
        {
            DetachThreadInput(targetWindow);

            if (timeoutDisabled)
            {
                TimeoutRepair.RestoreAndClear(_sharedState, originalTimeout);
            }
        }
    }

    private static bool TrySetForegroundWindowByClicking(IntPtr targetWindow, TitleBarClickPosition clickPosition)
    {
        if (!GetWindowRect(targetWindow, out var rect))
        {
            return false;
        }

        GetCursorPos(out var oldPos);

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

            _ = SendInput(2, inputs, INPUT.Size);

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

    private Action<IntPtr, TitleBarClickPosition>? GetKillAction(string killMethod) => _killActions.GetValueOrDefault(killMethod);
}
