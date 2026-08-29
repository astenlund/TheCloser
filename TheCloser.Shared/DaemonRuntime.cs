using System.Collections.Concurrent;

namespace TheCloser.Shared;

// Daemon lifetime shell: startup ordering, the single-threaded WaitAny loop, healer task
// tracking, the final repair tick, and the drain-before-unwind shutdown ordering. See the fix
// design's Daemon lifecycle section for why each ordering is load-bearing.
internal sealed class DaemonRuntime
{
    private readonly Logger _logger;
    private readonly string _memoryMappedFileName;
    private readonly string _daemonMutexName;
    private readonly string _activationEventName;
    private readonly string _exitEventName;
    private readonly Action<SharedState> _onActivation;
    private readonly Action<SharedState> _watchdogTick;
    private readonly TimeSpan _watchdogInterval;

    // Thread-safe: completions land on thread-pool threads while the loop thread adds.
    private readonly ConcurrentDictionary<Task, byte> _healerTasks = new();

    public DaemonRuntime(
        Logger logger,
        string memoryMappedFileName,
        string daemonMutexName,
        string activationEventName,
        string exitEventName,
        Action<SharedState> onActivation,
        Action<SharedState> watchdogTick,
        TimeSpan watchdogInterval)
    {
        _logger = logger;
        _memoryMappedFileName = memoryMappedFileName;
        _daemonMutexName = daemonMutexName;
        _activationEventName = activationEventName;
        _exitEventName = exitEventName;
        _onActivation = onActivation;
        _watchdogTick = watchdogTick;
        _watchdogInterval = watchdogInterval;
    }

    public void Run()
    {
        // Startup order is load-bearing: MMF pin, both events, then the mutex last, so the mutex
        // proves everything a press or a --stop needs already exists. A losing second instance
        // briefly co-owns both auto-reset events; harmless.
        using var sharedState = new SharedState(_memoryMappedFileName);
        using var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _activationEventName);
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _exitEventName);
        using var mutex = new Mutex(true, _daemonMutexName, out var createdNew);

        if (!createdNew)
        {
            _logger.Log("Daemon is already running. Exiting...");

            return;
        }

        RunLoop(sharedState, exitEvent, activationEvent);

        // Final repair tick: a pending foreground-lock repair record must not die with the MMF
        // pin on a graceful stop. Safe by construction (repairs only under an acquirable guard
        // mutex); a throw is logged and the drain and unwind still run.
        RunIsolated(() => _watchdogTick(sharedState));
        DrainHealers();

        _logger.Log("Daemon STOP signal received. Exiting...");
    }

    public void DispatchHealer(Action heal)
    {
        // Register before running so the drain can never miss a just-dispatched heal, and remove
        // by the registered completion so a fast heal cannot race its own registration.
        var completion = new TaskCompletionSource();
        _healerTasks.TryAdd(completion.Task, 0);
        Task.Run(() =>
        {
            try
            {
                RunIsolated(heal);
            }
            finally
            {
                _healerTasks.TryRemove(completion.Task, out _);
                completion.SetResult();
            }
        });
    }

    private void RunLoop(SharedState sharedState, EventWaitHandle exitEvent, EventWaitHandle activationEvent)
    {
        WaitHandle[] handles = [exitEvent, activationEvent];

        while (true)
        {
            var signaled = WaitHandle.WaitAny(handles, _watchdogInterval);

            if (signaled == 0)
            {
                return;
            }

            if (signaled == 1)
            {
                RunIsolated(() => _onActivation(sharedState));
            }
            else
            {
                RunIsolated(() => _watchdogTick(sharedState));
            }
        }
    }

    private void DrainHealers()
    {
        var outstanding = _healerTasks.Keys.ToArray();

        if (outstanding.Length > 0)
        {
            Task.WaitAll(outstanding);
        }
    }

    private void RunIsolated(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Log(ex.ToString());
        }
    }
}
