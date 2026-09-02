using System.Diagnostics;
using Microsoft.Extensions.Configuration;

using static TheCloser.Shared.Constants;

namespace TheCloser.Shared;

// Per-activation orchestration for the daemon's IPC path: payload and latency first (lock-free),
// then the guard-mutex scope containing the shared throttle check, pending repair, tick write,
// close, and healer decision; see the fix design's responsibility table. Instances live for the
// daemon's lifetime; everything per-press is created inside HandleActivation.
internal sealed class ActivationHandler
{
    private static readonly TimeSpan MaxPlausibleLatency = TimeSpan.FromSeconds(10);

    private readonly SharedState _sharedState;
    private readonly Logger _logger;
    private readonly string _guardMutexName;
    private readonly Func<IConfiguration> _settings;
    private readonly Func<IConfiguration, bool> _runClose;
    private readonly Action _dispatchHealer;
    private readonly Func<long> _timestamp;
    private readonly Func<long> _tickCount;
    private readonly Func<SharedState, bool> _restorePending;

    // Deferred-press attribution state: a plausible payload QPC older than the previous handler
    // exit was collapsed behind that handling. Same clock as the payload QPC
    // (Stopwatch.GetTimestamp == QueryPerformanceCounter). Seeded from the caller's baseline (the
    // daemon passes its own start time, since it constructs this lazily on the first activation)
    // and refreshed on every exit, skips included.
    private long _lastHandlerExit;

    public ActivationHandler(
        SharedState sharedState,
        Logger logger,
        string guardMutexName,
        Func<IConfiguration> settings,
        Func<IConfiguration, bool> runClose,
        Action dispatchHealer,
        Func<long>? timestamp = null,
        Func<long>? tickCount = null,
        Func<SharedState, bool>? restorePending = null,
        long? initialHandlerExit = null)
    {
        _sharedState = sharedState;
        _logger = logger;
        _guardMutexName = guardMutexName;
        _settings = settings;
        _runClose = runClose;
        _dispatchHealer = dispatchHealer;
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _restorePending = restorePending ?? (state => TimeoutRepair.TryRestorePending(state));
        _lastHandlerExit = initialHandlerExit ?? _timestamp();
    }

    public void HandleActivation()
    {
        try
        {
            var handlerEntry = _timestamp();
            var (launchQpc, buttonCode) = _sharedState.ConsumeActivationPayload();
            var pressLatency = LogAndMeasureLatency(handlerEntry, launchQpc, buttonCode);

            var phases = RunThrottledActivation(pressLatency ?? TimeSpan.Zero);
            LogIfStalled(handlerEntry, phases);
        }
        finally
        {
            _lastHandlerExit = _timestamp();
        }
    }

    // Returns the durations of the two phases that can stall; both are zero on every skip path.
    private ActivationPhases RunThrottledActivation(TimeSpan pressLatency)
    {
        // Created, acquired, released, and disposed within this one activation, never cached in
        // a field: a live cached handle would make CrashRepair's createdNew liveness check read
        // false on every watchdog tick and silently disable the crash-repair watchdog. The
        // create-unowned-then-WaitOne(0) pair is a genuine acquire, satisfying the
        // acquired-not-probed invariant CrashRepair documents.
        using var guardMutex = new Mutex(initiallyOwned: false, _guardMutexName);
        var acquired = false;
        var performedAttach = false;
        var phases = default(ActivationPhases);

        try
        {
            try
            {
                acquired = guardMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // WaitOne grants ownership when reporting an abandoned mutex.
                acquired = true;
            }

            if (!acquired)
            {
                _logger.Log("Activation skipped: the guard mutex is held by another instance.");

                return phases;
            }

            // Dated from the press, not from this handling: a double activation queued behind a
            // stalled close (observed: SendInput blocked 2 s on a slow low-level hook) is still a
            // double activation when the loop finally reaches it. The window is symmetric because
            // a press can land between the previous handler's entry and its tick write; a
            // stale-format or foreign tick reads as a huge magnitude and stays unthrottled.
            var pressTick = _tickCount() - (long)pressLatency.TotalMilliseconds;
            var elapsedSinceLastRun = pressTick - _sharedState.ReadThrottleTick();

            if (elapsedSinceLastRun is > -ThrottleThresholdMs and < ThrottleThresholdMs)
            {
                _logger.Log($"Activation skipped: the press was within {ThrottleThresholdMs}ms of the previous handling.");

                return phases;
            }

            if (_sharedState.TryReadTimeoutRepair(out _) && _restorePending(_sharedState))
            {
                _logger.Log("Restored the foreground lock timeout before closing.");
            }

            _sharedState.WriteThrottleTick(_tickCount());

            var settingsStart = _timestamp();
            long? closeStart = null;

            try
            {
                var snapshot = _settings();
                closeStart = _timestamp();
                performedAttach = _runClose(snapshot);
            }
            catch (Exception ex)
            {
                _logger.Log(ex.ToString());
            }
            finally
            {
                // Measured in finally so a close that stalls and then throws still reports its
                // duration; a throwing settings snapshot attributes the whole span to settings.
                var end = _timestamp();
                var boundary = closeStart ?? end;
                phases = new ActivationPhases(Stopwatch.GetElapsedTime(settingsStart, boundary), Stopwatch.GetElapsedTime(boundary, end));
            }
        }
        finally
        {
            if (acquired)
            {
                guardMutex.ReleaseMutex();
            }
        }

        if (performedAttach)
        {
            _dispatchHealer();
        }

        return phases;
    }

    private void LogIfStalled(long handlerEntry, ActivationPhases phases)
    {
        var total = Stopwatch.GetElapsedTime(handlerEntry, _timestamp());

        if (total.TotalMilliseconds >= StallLogThresholdMs)
        {
            _logger.Log($"Activation took {total.TotalMilliseconds:F0} ms (settings {phases.Settings.TotalMilliseconds:F0} ms, close {phases.Close.TotalMilliseconds:F0} ms).");
        }
    }

    private TimeSpan? LogAndMeasureLatency(long handlerEntry, long launchQpc, int buttonCode)
    {
        var latency = Stopwatch.GetElapsedTime(launchQpc, handlerEntry);
        var plausible = launchQpc > 0 && launchQpc <= handlerEntry && latency <= MaxPlausibleLatency;

        if (!plausible)
        {
            _logger.Log("Activation: latency unavailable (button unknown).");

            return null;
        }

        var deferred = launchQpc < _lastHandlerExit
            ? $" (deferred; queued {Stopwatch.GetElapsedTime(launchQpc, _lastHandlerExit).TotalMilliseconds:F0} ms behind the previous handling)"
            : string.Empty;
        var button = buttonCode == TriggerButtonXButton2 ? "XButton2" : $"code {buttonCode}";
        _logger.Log($"Activation{deferred}: latency {latency.TotalMilliseconds:F1} ms (button {button}).");

        return latency;
    }

    private readonly record struct ActivationPhases(TimeSpan Settings, TimeSpan Close);
}
