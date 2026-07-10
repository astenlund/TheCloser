namespace TheCloser.Shared;

public static class Constants
{
    public const string GuardMutexName = "TheCloserGuardMutex";
    public const string DaemonMutexName = "TheCloserDaemonMutex";
    public const string DaemonExitEventName = "TheCloserDaemonExitEvent";
    public const string MemoryMappedFileName = "TheCloserStartupTimestamp";
    public const long MemoryMappedFileSize = 1024;
    public const string DaemonStartArgument = "--start";
    public const string DaemonStopArgument = "--stop";
}
