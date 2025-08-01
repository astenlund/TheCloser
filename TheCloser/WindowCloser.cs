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

    private static readonly Logger Logger = Logger.Create(Program.AssemblyName);

    private readonly IConfiguration _config;
    private readonly InputSimulator _inputSimulator;
    private readonly Dictionary<string, Action<IntPtr, TitleBarClickPosition>> _killActions;

    private WindowCloser(IConfiguration config)
    {
        _config = config;
        _inputSimulator = new InputSimulator();
        _killActions = new Dictionary<string, Action<IntPtr, TitleBarClickPosition>>
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

    public static WindowCloser Create(IConfiguration config) => new(config);

    public void CloseWindowUnderCursor()
    {
        var targetWindow = WindowFromPoint(GetMouseCursorPosition());
        var targetProcess = Process.GetProcessById(GetProcessIdFromWindowHandle(targetWindow));
        var settings = GetProcessSettings(targetProcess);
        var killMethod = settings.Method ?? DefaultKillMethod;
        var killAction = GetKillAction(killMethod) ?? _killActions[DefaultKillMethod];

        Logger.Log($"{targetProcess.ProcessName} -> {killMethod}");

        killAction.Invoke(targetWindow, settings.ClickPosition ?? DefaultClickPosition);
    }

    private static bool TrySetForegroundWindow(IntPtr targetWindow, TitleBarClickPosition clickPosition)
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

            return IsForeground(targetWindow);
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
            
            Thread.Sleep(50);

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

            Thread.Sleep(10);
        }

        return false;
    }

    private void TrySendKeyPress(IntPtr targetWindow, TitleBarClickPosition clickPosition, VirtualKeyCode keyCode, params VirtualKeyCode[] modifierKeyCodes)
    {
        if (TrySetForegroundWindow(targetWindow, clickPosition))
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

    private ProcessSettings GetProcessSettings(Process process)
    {
        var section = _config.GetSection(process.ProcessName);
        
        // Check if it's a simple string
        var simpleValue = section.Value;
        if (!string.IsNullOrEmpty(simpleValue))
        {
            return new ProcessSettings { Method = simpleValue.ToUpperInvariant() };
        }
        
        // Otherwise, read values from sections
        var settings = new ProcessSettings
        {
            Method = section["Method"]?.ToUpperInvariant(),
            ClickPosition = Enum.TryParse<TitleBarClickPosition>(section["ClickPosition"], out var clickPos) 
                ? clickPos 
                : Left
        };
        
        return settings;
    }

    private Action<IntPtr, TitleBarClickPosition>? GetKillAction(string killMethod) => _killActions.GetValueOrDefault(killMethod);
}
