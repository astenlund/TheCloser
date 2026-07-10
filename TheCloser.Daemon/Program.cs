using TheCloser.Shared;

using static TheCloser.Shared.Constants;

namespace TheCloser.Daemon;

public static class Program
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly Logger Logger = new(DaemonAssemblyName);

    public static void Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return;
            }

            switch (args[0])
            {
                case DaemonStartArgument:
                    Logger.Log("Daemon starting...");
                    Run();
                    break;
                case DaemonStopArgument:
                    Logger.Log("Daemon stopping...");
                    SignalExit();
                    break;
                default:
                    Logger.Log($"Daemon could not be started. Unknown argument: '{args[0]}'");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex.ToString());
        }
    }

    private static void Run()
    {
        // The shared memory must be pinned before the mutex is published: the app treats the mutex's existence as proof that the pin is in place.
        using var sharedState = new SharedState(MemoryMappedFileName);
        using var mutex = new Mutex(true, DaemonMutexName, out var createdNew);

        if (!createdNew)
        {
            Logger.Log("Daemon is already running. Exiting...");

            return;
        }

        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, DaemonExitEventName);

        while (!exitEvent.WaitOne(WatchdogInterval))
        {
            try
            {
                RepairIfCrashed(sharedState);
            }
            catch (Exception ex)
            {
                Logger.Log(ex.ToString());
            }
        }

        Logger.Log("Daemon STOP signal received. Exiting...");
    }

    private static void RepairIfCrashed(SharedState sharedState)
    {
        if (TryRepairCrashedState(sharedState, GuardMutexName, Logger))
        {
            Logger.Log("Restored the foreground lock timeout after a detected app crash.");
        }
    }

    // The guard mutex must be ACQUIRED (createdNew), not merely probed: a probe result goes stale
    // before the repair runs, letting the daemon erase a record a freshly started app just published.
    // Pending is checked before acquisition so idle ticks never contend with app startups, and
    // re-checked after, because the app may have healed the record between the pending check and the successful creation.
    internal static bool TryRepairCrashedState(SharedState sharedState, string guardMutexName, Logger logger, Func<uint, bool>? restore = null)
    {
        if (!sharedState.TryReadTimeoutRepair(out _))
        {
            return false;
        }

        using var guardMutex = new Mutex(true, guardMutexName, out var createdNew);

        if (!createdNew)
        {
            return false;
        }

        try
        {
            if (!sharedState.TryReadTimeoutRepair(out var savedTimeout))
            {
                return false;
            }

            if (TimeoutRepair.RestoreAndClear(sharedState, savedTimeout, restore))
            {
                return true;
            }

            logger.Log("Failed to restore the foreground lock timeout; keeping the repair record for the next watchdog tick.");

            return false;
        }
        finally
        {
            guardMutex.ReleaseMutex();
        }
    }

    private static void SignalExit()
    {
        if (EventWaitHandle.TryOpenExisting(DaemonExitEventName, out var exitEvent))
        {
            exitEvent.Set();
            exitEvent.Dispose();
        }
        else
        {
            Logger.Log("Daemon is not running. Exiting...");
        }
    }
}
