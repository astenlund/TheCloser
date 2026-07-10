namespace TheCloser.Shared;

public static class TimeoutRepair
{
    // Restore must precede the clear, and a failed restore must keep the record: clearing on failure
    // would drop the record while the system value is still wrong, silencing every future retry.
    public static bool RestoreAndClear(SharedState sharedState, uint timeout, Func<uint, bool>? restore = null)
    {
        restore ??= ForegroundLockTimeout.Restore;

        if (!restore(timeout))
        {
            return false;
        }

        sharedState.ClearTimeoutRepair();

        return true;
    }

    public static bool TryRestorePending(SharedState sharedState, Func<uint, bool>? restore = null)
    {
        if (!sharedState.TryReadTimeoutRepair(out var savedTimeout))
        {
            return false;
        }

        return RestoreAndClear(sharedState, savedTimeout, restore);
    }
}
