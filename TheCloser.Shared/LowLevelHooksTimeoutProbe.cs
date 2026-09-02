using Microsoft.Win32;

namespace TheCloser.Shared;

// Startup diagnostic for stalled keystroke injection. Windows runs every WH_KEYBOARD_LL hook
// synchronously before delivering injected input and waits up to this per-hook timeout on an
// unresponsive hook owner, so a logged SendInput stall (see StallLogThresholdMs) can be read
// against the value in force. Read-only; the daemon never changes the setting.
internal static class LowLevelHooksTimeoutProbe
{
    private const string DesktopKeyPath = @"Control Panel\Desktop";
    private const string ValueName = "LowLevelHooksTimeout";

    public static string Describe(Func<object?>? readValue = null)
    {
        try
        {
            // REG_DWORD arrives as a signed int; values at or above 0x80000000 must not print negative.
            return (readValue ?? ReadFromRegistry)() switch
            {
                null => $"{ValueName}: not set (Windows default applies).",
                int dword => $"{ValueName}: {(uint)dword} ms.",
                var other => $"{ValueName}: {other} (unexpected value kind)."
            };
        }
        catch (Exception ex)
        {
            return $"{ValueName}: unreadable ({ex.GetType().Name}).";
        }
    }

    private static object? ReadFromRegistry()
    {
        using var desktop = Registry.CurrentUser.OpenSubKey(DesktopKeyPath);

        return desktop?.GetValue(ValueName);
    }
}
