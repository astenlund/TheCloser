namespace TheCloser.Shared;

public static class TimeoutRepair
{
    // Restore must precede the clear: clearing first would drop the record while the system value is still wrong.
    public static void RestoreAndClear(SharedState sharedState, uint timeout)
    {
        ForegroundLockTimeout.Restore(timeout);
        sharedState.ClearTimeoutRepair();
    }

    public static bool TryRestorePending(SharedState sharedState)
    {
        if (!sharedState.TryReadTimeoutRepair(out var savedTimeout))
        {
            return false;
        }

        RestoreAndClear(sharedState, savedTimeout);

        return true;
    }
}
