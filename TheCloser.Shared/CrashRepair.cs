namespace TheCloser.Shared;

public static class CrashRepair
{
    // The guard mutex must be ACQUIRED (createdNew), not merely probed: a probe result goes stale
    // before the repair runs, letting the daemon erase a record a freshly started app just published.
    // Pending is checked before acquisition so idle ticks never contend with app startups, and
    // re-checked after, because the app may have healed the record between the pending check and the successful creation.
    public static bool TryRepairCrashedState(SharedState sharedState, string guardMutexName, Logger logger, Func<uint, bool>? restore = null)
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
}
