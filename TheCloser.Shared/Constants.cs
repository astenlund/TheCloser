namespace TheCloser.Shared;

public static class Constants
{
    // Must match the TheCloser.Daemon assembly name; used to detect the daemon process and locate its executable.
    public const string DaemonAssemblyName = "TheCloser.Daemon";
    public const string GuardMutexName = "TheCloserGuardMutex";

    // Duplicated by hand in TheCloser.ahk (AutoHotkey cannot consume this file); keep in sync.
    public const string DaemonMutexName = "TheCloserDaemonMutex";
    public const string DaemonExitEventName = "TheCloserDaemonExitEvent";

    // Duplicated by hand in TheCloser.ahk (AutoHotkey cannot consume this file); keep in sync.
    public const string MemoryMappedFileName = "TheCloserSharedState";

    // Duplicated by hand in TheCloser.ahk (AutoHotkey cannot consume this file); keep in sync.
    public const string ActivationEventName = "TheCloserActivationEvent";

    // Shared between the app's fallback path and the daemon's IPC path.
    public const long ThrottleThresholdMs = 200;

    // A handling or a keystroke injection at or above this duration is logged with its breakdown
    // so a stall (observed: SendInput blocked 2 s while the daemon starved at Below Normal
    // priority under full-core load) is attributable later. Calibrated above the close as measured
    // before the priority fix, 110 to 133 ms, so a routine press adds no log line.
    public const long StallLogThresholdMs = 300;

    // Trigger button codes for the activation payload. Duplicated by hand in TheCloser.ahk
    // (AutoHotkey cannot consume this file); keep in sync.
    public const int TriggerButtonUnknown = 0;
    public const int TriggerButtonXButton2 = 1;

    public const long MemoryMappedFileSize = 1024;
    public const string DaemonStartArgument = "--start";
    public const string DaemonStopArgument = "--stop";

    public static string GetLogMutexName(string appName) => appName + "LogMutex";
}
