namespace TheCloser.Shared;

// Best-effort: disables the system foreground lock timeout for the scope's lifetime, guarded by the
// crash-repair record so a kill inside the window is healed by the daemon watchdog or the next app
// startup. A failed timeout read or disable yields a no-op scope. Scopes must not overlap within a
// process: an inner dispose lifts the suppression and consumes the repair record early.
public sealed class ForegroundLockSuppression : IDisposable
{
    public delegate bool TryGetTimeoutHandler(out uint timeout);

    private readonly SharedState _sharedState;
    private readonly Logger _logger;
    private readonly Func<uint, bool>? _restore;
    private readonly uint _originalTimeout;
    private readonly bool _timeoutDisabled;

    private bool _disposed;

    public ForegroundLockSuppression(SharedState sharedState, Logger logger, TryGetTimeoutHandler? tryGet = null, Func<bool>? disable = null, Func<uint, bool>? restore = null)
    {
        _sharedState = sharedState;
        _logger = logger;
        _restore = restore;
        tryGet ??= ForegroundLockTimeout.TryGet;
        disable ??= ForegroundLockTimeout.Disable;

        if (!tryGet(out var currentTimeout))
        {
            return;
        }

        if (sharedState.TryReadTimeoutRepair(out var pendingTimeout))
        {
            // An earlier restore failed; the pending record's saved value, not the current
            // (possibly still disabled) system value, is the true original. Never overwrite it.
            _originalTimeout = pendingTimeout;
            _timeoutDisabled = disable();
        }
        else
        {
            _originalTimeout = currentTimeout;
            sharedState.SetTimeoutRepair(_originalTimeout);
            _timeoutDisabled = disable();

            if (!_timeoutDisabled)
            {
                sharedState.ClearTimeoutRepair();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_timeoutDisabled && !TimeoutRepair.RestoreAndClear(_sharedState, _originalTimeout, _restore))
        {
            _logger.Log("Failed to restore the foreground lock timeout; keeping the repair record for the daemon watchdog.");
        }
    }
}
