using System.Runtime.InteropServices;
using TheCloser.Shared;

using static TheCloser.NativeMethods;
using static TheCloser.TitleBarClickPosition;

namespace TheCloser;

// Brings the target window to the foreground via an escalation ladder: already-foreground check,
// native activation of the target and then its root (each under a foreground-lock suppression),
// and finally a synthesized title bar click.
internal class ForegroundActivator
{
    private const int TitleBarClickOffsetX = 10;
    private const int TitleBarClickOffsetY = 20;
    private const int CursorMoveRetries = 5;

    internal static readonly TimeSpan InputSettleDelay = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan CursorPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly SharedState _sharedState;
    private readonly Logger _logger;

    public ForegroundActivator(SharedState sharedState, Logger logger)
    {
        _sharedState = sharedState;
        _logger = logger;
    }

    public bool TryActivate(IntPtr targetWindow, TitleBarClickPosition clickPosition)
    {
        var rootWindow = GetRootWindow(targetWindow);

        if (IsForeground(targetWindow))
        {
            _logger.Log("Foreground: target was already foreground.");

            return true;
        }

        if (TryActivateNatively(targetWindow))
        {
            _logger.Log("Foreground: native activation of the target window succeeded.");

            return true;
        }

        if (rootWindow != targetWindow && TryActivateNatively(rootWindow))
        {
            _logger.Log("Foreground: native activation of the root window succeeded.");

            return true;
        }

        if (TryActivateByClicking(rootWindow, clickPosition))
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

    private bool TryActivateNatively(IntPtr targetWindow)
    {
        using var suppression = new ForegroundLockSuppression(_sharedState, _logger);

        try
        {
            if (!AttachThreadInput(targetWindow))
            {
                _logger.Log($"AttachThreadInput failed (error {Marshal.GetLastPInvokeError()}).");
            }

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
        }
    }

    private bool TryActivateByClicking(IntPtr targetWindow, TitleBarClickPosition clickPosition)
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
            var clickY = rect.Top + TitleBarClickOffsetY;
            var clickX = clickPosition switch
            {
                Left => rect.Left + TitleBarClickOffsetX,
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

        for (var attempts = 0; attempts < CursorMoveRetries; attempts++)
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
}
