using System.Runtime.InteropServices;
using TheCloser.Shared;

using static TheCloser.NativeMethods;
using static TheCloser.TitleBarClickPosition;

namespace TheCloser;

// Brings the target window to the foreground via an escalation ladder: already-foreground check,
// native activation of the root window (under a foreground-lock suppression, with the input
// queues of the foreground owner and the root attached), and finally a synthesized title bar
// click. The root is activated directly because SetForegroundWindow rejects child HWNDs even
// with foreground permission, and a child can only become "foreground" via its root anyway.
internal class ForegroundActivator : IForegroundActivator
{
    private const int TitleBarClickOffsetX = 10;
    private const int TitleBarClickOffsetY = 20;
    private const int CursorMoveRetries = 5;

    internal static readonly TimeSpan InputSettleDelay = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan CursorPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly Logger _logger;
    private readonly INativeWindowApi _native;
    private readonly Action<TimeSpan> _sleep;
    private readonly Func<IDisposable> _suppressionFactory;

    public ForegroundActivator(
        SharedState sharedState,
        Logger logger,
        INativeWindowApi? native = null,
        Action<TimeSpan>? sleep = null,
        Func<IDisposable>? suppressionFactory = null)
    {
        _logger = logger;
        _native = native ?? new NativeWindowApi();
        _sleep = sleep ?? Thread.Sleep;
        _suppressionFactory = suppressionFactory ?? (() => new ForegroundLockSuppression(sharedState, logger));
    }

    // True once any AttachThreadInput succeeded during this run. The attach's key-state resync
    // can swallow the in-flight release of the mouse button that invoked the app; the caller uses
    // this to decide whether the post-close stuck-button monitor is needed (TriggerButtonHealer).
    public bool PerformedInputAttach { get; private set; }

    public bool TryActivate(IntPtr targetWindow, TitleBarClickPosition clickPosition)
    {
        var rootWindow = _native.GetRootWindow(targetWindow);

        if (rootWindow == IntPtr.Zero)
        {
            rootWindow = targetWindow;
        }

        if (IsForeground(targetWindow))
        {
            _logger.Log("Foreground: target was already foreground.");

            return true;
        }

        if (TryActivateNatively(rootWindow))
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

    private bool IsForeground(IntPtr targetWindow)
    {
        var rootWindow = _native.GetRootWindow(targetWindow);
        var foregroundWindow = _native.GetForegroundWindow();

        return foregroundWindow == targetWindow ||
               foregroundWindow == rootWindow;
    }

    private bool TryActivateNatively(IntPtr targetWindow)
    {
        using var suppression = _suppressionFactory();

        var foregroundWindow = _native.GetForegroundWindow();
        var attachedToForegroundOwner = TryAttachToForegroundOwner(foregroundWindow, targetWindow);

        try
        {
            var attachedToTarget = _native.AttachThreadInput(targetWindow);

            if (!attachedToTarget)
            {
                _logger.Log($"AttachThreadInput failed (error {Marshal.GetLastPInvokeError()}).");
            }

            PerformedInputAttach |= attachedToTarget || attachedToForegroundOwner;

            if (!_native.SetForegroundWindow(targetWindow))
            {
                _logger.Log("SetForegroundWindow returned false.");
            }
        }
        finally
        {
            // Detach before the settle wait: the attaches are only needed around the
            // SetForegroundWindow call, and every attached millisecond widens the window in which
            // the key-state resync can swallow in-flight input (see TriggerButtonHealer).
            _native.DetachThreadInput(targetWindow);

            if (attachedToForegroundOwner)
            {
                _native.DetachThreadInput(foregroundWindow);
            }
        }

        _sleep(InputSettleDelay);

        return IsForeground(targetWindow);
    }

    // Foreground rights belong to the thread that received the user's last input (the current
    // foreground owner), and the lock-timeout suppression does not lift that rule. Sharing the
    // owner's input queue borrows its permission so SetForegroundWindow can succeed. Skipped when
    // the owner shares the target's thread: the target attach already covers that queue.
    private bool TryAttachToForegroundOwner(IntPtr foregroundWindow, IntPtr targetWindow)
    {
        if (foregroundWindow == IntPtr.Zero)
        {
            _logger.Log("Skipping the foreground owner attach (no foreground window).");

            return false;
        }

        if (_native.GetWindowThreadId(foregroundWindow) == _native.GetWindowThreadId(targetWindow))
        {
            return false;
        }

        if (!_native.AttachThreadInput(foregroundWindow))
        {
            _logger.Log($"AttachThreadInput to the foreground owner failed (error {Marshal.GetLastPInvokeError()}).");

            return false;
        }

        return true;
    }

    private bool TryActivateByClicking(IntPtr targetWindow, TitleBarClickPosition clickPosition)
    {
        if (!_native.TryGetWindowRect(targetWindow, out var rect))
        {
            return false;
        }

        if (!_native.TryGetCursorPosition(out var oldPos))
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

            if (_native.SendInput(inputs) != inputs.Length)
            {
                _logger.Log($"SendInput injected fewer events than requested (error {Marshal.GetLastPInvokeError()}).");
            }

            _sleep(InputSettleDelay);

            return IsForeground(targetWindow);
        }
        finally
        {
            TryMoveCursor(oldPos.X, oldPos.Y);
        }
    }

    // Deliberately preserves the ignored P/Invoke returns; fixing them is the separate
    // "TryMoveCursor ignores both P/Invoke returns" quick win.
    private bool TryMoveCursor(int x, int y)
    {
        _native.SetCursorPosition(x, y);

        for (var attempts = 0; attempts < CursorMoveRetries; attempts++)
        {
            _native.TryGetCursorPosition(out var currentPos);

            if (currentPos.X == x && currentPos.Y == y)
            {
                return true;
            }

            _sleep(CursorPollInterval);
        }

        return false;
    }
}
