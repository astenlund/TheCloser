using System.Runtime.InteropServices;
using TheCloser.Shared;

using static TheCloser.NativeMethods;

namespace TheCloser;

// An input attach can swallow the physical release of the mouse button that invoked the app:
// AttachThreadInput resynchronizes key state between the attached threads, and a release in
// flight during the attach window is lost, leaving the button stuck down system-wide (observed
// 2026-07-11 with XBUTTON2 under an AutoHotkey binding, which then swallowed every mouse click).
// Rather than delaying activation until the button is released, the app monitors the trigger
// buttons after the close operation completes: a genuine hold clears itself on release, while a
// stranded state reads down forever, so anything still down at the deadline gets its release
// injected. Deliberately app-hosted with no daemon backstop: a leak requires the sub-millisecond
// attach race AND an app death inside this monitor, and the daemon's 5s watchdog tick would heal
// far slower than the monitor does.
internal sealed class TriggerButtonHealer
{
    private const int MonitorAttempts = 200;

    private static readonly int[] TriggerButtonVirtualKeys = [VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2];
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly Logger _logger;
    private readonly Func<int, bool> _isButtonDown;
    private readonly Action<int> _injectRelease;
    private readonly Action<TimeSpan> _sleep;

    public TriggerButtonHealer(Logger logger, Func<int, bool>? isButtonDown = null, Action<int>? injectRelease = null, Action<TimeSpan>? sleep = null)
    {
        _logger = logger;
        _isButtonDown = isButtonDown ?? (virtualKey => GetAsyncKeyState(virtualKey) < 0);
        _injectRelease = injectRelease ?? InjectRelease;
        _sleep = sleep ?? Thread.Sleep;
    }

    public void HealStuckButtons()
    {
        for (var attempts = 0; attempts < MonitorAttempts; attempts++)
        {
            if (!TriggerButtonVirtualKeys.Any(_isButtonDown))
            {
                return;
            }

            _sleep(PollInterval);
        }

        foreach (var virtualKey in TriggerButtonVirtualKeys.Where(_isButtonDown))
        {
            _injectRelease(virtualKey);
            _logger.Log($"Trigger button 0x{virtualKey:X2} was still down at the monitor deadline after an input attach; injected its release.");
        }
    }

    private void InjectRelease(int virtualKey)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.U.mi.dwFlags = virtualKey == VK_MBUTTON ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_XUP;
        input.U.mi.mouseData = virtualKey switch
        {
            VK_XBUTTON1 => XBUTTON1,
            VK_XBUTTON2 => XBUTTON2,
            _ => 0u
        };

        if (SendInput(1, [input], INPUT.Size) != 1)
        {
            _logger.Log($"SendInput failed to inject the release (error {Marshal.GetLastPInvokeError()}).");
        }
    }
}
