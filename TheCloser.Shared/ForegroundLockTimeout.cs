using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

using static System.Runtime.InteropServices.UnmanagedType;

namespace TheCloser.Shared;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ForegroundLockTimeout
{
    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;
    private const uint SPIF_SENDCHANGE = 0x02;

    public static bool TryGet(out uint timeout)
    {
        timeout = 0;

        return SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref timeout, 0);
    }

    public static bool Disable() => SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE);

    // SPI_SETFOREGROUNDLOCKTIMEOUT takes its value through pvParam (as the value itself, not a pointer); uiParam is ignored.
    public static bool Restore(uint timeout) => SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, new IntPtr(timeout), SPIF_SENDCHANGE);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
}
