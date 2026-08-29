using System.Diagnostics;

using TheCloser.Shared;
using Xunit;

namespace TheCloser.Tests;

public class DaemonRuntimeTests
{
    private static readonly TimeSpan LongInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReturnProbeBudget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);

    private sealed class Names
    {
        public string Map { get; } = TestNames.UniqueMapName();
        public string Mutex { get; } = TestNames.UniqueMutexName();
        public string Activation { get; } = TestNames.UniqueEventName();
        public string Exit { get; } = TestNames.UniqueEventName();
    }

    private static DaemonRuntime Build(Names n, TempLogger logger, Action<SharedState>? onActivation = null, Action<SharedState>? watchdogTick = null) =>
        new(logger.Logger, n.Map, n.Mutex, n.Activation, n.Exit,
            onActivation ?? (_ => { }), watchdogTick ?? (_ => { }), LongInterval);

    private static async Task<string> ReadLogAsync(TempLogger logger)
    {
        await logger.DrainAsync();

        return File.ReadAllText(logger.LogPath);
    }

    private static Thread Start(DaemonRuntime runtime, Names n, ManualResetEventSlim? runtimeReturned = null)
    {
        var thread = new Thread(() =>
        {
            runtime.Run();
            runtimeReturned?.Set();
        }) { IsBackground = true };
        thread.Start();
        Assert.True(SpinWaitFor(() => Mutex.TryOpenExisting(n.Mutex, out var m) && Dispose(m)), "daemon mutex never appeared");

        return thread;
    }

    private static bool Dispose(Mutex m)
    {
        m.Dispose();

        return true;
    }

    private static bool SpinWaitFor(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < WaitBudget)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(25);
        }

        return false;
    }

    private static void SignalStop(Names n)
    {
        using var exit = EventWaitHandle.OpenExisting(n.Exit);
        exit.Set();
    }

    private static void StopAndJoin(Names n, Thread thread)
    {
        SignalStop(n);
        Assert.True(thread.Join(WaitBudget), "Run did not return after the exit signal");
    }

    private static void WaitForRelease(ManualResetEventSlim release, string message)
    {
        if (!release.Wait(WaitBudget))
        {
            throw new TimeoutException(message);
        }
    }

    [Fact]
    public async Task SecondInstance_LosesOnMutex_AndEventStaysSignalable()
    {
        // Arrange
        var n = new Names();
        using var logger = new TempLogger();
        var thread = Start(Build(n, logger), n);

        // Act: a second runtime with the same names must return promptly.
        using var secondLogger = new TempLogger();
        var second = Build(n, secondLogger);
        var secondThread = new Thread(second.Run) { IsBackground = true };
        secondThread.Start();
        Assert.True(secondThread.Join(WaitBudget));

        // Assert: the loser logged and the survivor's activation event is still signalable.
        Assert.Contains("already running", await ReadLogAsync(secondLogger));
        using (var evt = EventWaitHandle.OpenExisting(n.Activation))
        {
            evt.Set();
        }
        StopAndJoin(n, thread);
    }

    [Fact]
    public void StartupOrder_MutexObservableImpliesEventsOpenable()
    {
        // Arrange
        var n = new Names();
        using var logger = new TempLogger();
        var runtime = Build(n, logger);

        // Act
        var thread = new Thread(runtime.Run) { IsBackground = true };
        thread.Start();
        Assert.True(SpinWaitFor(() => Mutex.TryOpenExisting(n.Mutex, out var m) && Dispose(m)));

        // Assert: the moment the mutex is observable, both events must already exist.
        using (var a = EventWaitHandle.OpenExisting(n.Activation)) { }
        using (var e = EventWaitHandle.OpenExisting(n.Exit)) { }
        StopAndJoin(n, thread);
    }

    [Fact]
    public async Task Activation_InvokesHandler_AndExceptionIsSwallowed()
    {
        // Arrange
        var n = new Names();
        using var logger = new TempLogger();
        var invocations = 0;
        var runtime = Build(n, logger, onActivation: _ =>
        {
            Interlocked.Increment(ref invocations);
            throw new InvalidOperationException("handler boom");
        });
        var thread = Start(runtime, n);

        // Act: two signals; the loop must survive the first throw to observe the second.
        using (var evt = EventWaitHandle.OpenExisting(n.Activation))
        {
            evt.Set();
            Assert.True(SpinWaitFor(() => Volatile.Read(ref invocations) == 1));
            evt.Set();
            Assert.True(SpinWaitFor(() => Volatile.Read(ref invocations) == 2));
        }

        // Assert
        StopAndJoin(n, thread);
        Assert.Contains("handler boom", await ReadLogAsync(logger));
    }

    [Fact]
    public void FinalRepairTick_RunsAfterLoopExit()
    {
        // Arrange: activation requests exit, so the next wait-loop iteration must exit before the final tick.
        var n = new Names();
        using var logger = new TempLogger();
        var sequence = 0;
        var activationStep = 0;
        var finalTickStep = 0;
        var runtime = Build(
            n,
            logger,
            onActivation: _ =>
            {
                Volatile.Write(ref activationStep, Interlocked.Increment(ref sequence));
                SignalStop(n);
            },
            watchdogTick: _ => Volatile.Write(ref finalTickStep, Interlocked.Increment(ref sequence)));
        var thread = Start(runtime, n);

        // Act
        using (var activation = EventWaitHandle.OpenExisting(n.Activation))
        {
            activation.Set();
        }
        Assert.True(thread.Join(WaitBudget), "Run did not return after activation requested exit");

        // Assert: moving the final tick before RunLoop reverses these steps and fails this test.
        Assert.Equal(1, Volatile.Read(ref activationStep));
        Assert.Equal(2, Volatile.Read(ref finalTickStep));
    }

    [Fact]
    public async Task FinalRepairTick_ThrowIsSwallowed_DrainStillRuns()
    {
        // Arrange
        var n = new Names();
        using var logger = new TempLogger();
        using var healerStarted = new ManualResetEventSlim();
        using var releaseHealer = new ManualResetEventSlim();
        using var finalTickRan = new ManualResetEventSlim();
        using var runtimeReturned = new ManualResetEventSlim();
        var healRan = 0;
        var runtime = Build(n, logger, watchdogTick: _ =>
        {
            finalTickRan.Set();
            throw new InvalidOperationException("tick boom");
        });
        var thread = Start(runtime, n, runtimeReturned);
        runtime.DispatchHealer(() =>
        {
            healerStarted.Set();
            WaitForRelease(releaseHealer, "healer release was not signaled");
            Interlocked.Exchange(ref healRan, 1);
        });
        Assert.True(healerStarted.Wait(WaitBudget), "healer did not reach its gate");

        // Act
        SignalStop(n);
        Assert.True(finalTickRan.Wait(WaitBudget), "final repair tick did not run");
        var returnedBeforeRelease = runtimeReturned.Wait(ReturnProbeBudget);
        releaseHealer.Set();
        Assert.True(thread.Join(WaitBudget), "Run did not return after the healer was released");

        // Assert: the throwing final tick was logged and did not skip the drain.
        Assert.False(returnedBeforeRelease, "Run returned while a healer remained gated");
        Assert.Equal(1, Volatile.Read(ref healRan));
        Assert.Contains("tick boom", await ReadLogAsync(logger));
    }

    [Fact]
    public async Task Drain_WaitsForDispatchedHeal_IncludingThrowingHeal()
    {
        // Arrange
        var n = new Names();
        using var logger = new TempLogger();
        using var healersStarted = new CountdownEvent(2);
        using var releaseHealers = new ManualResetEventSlim();
        using var runtimeReturned = new ManualResetEventSlim();
        var slowHealDone = 0;
        var runtime = Build(n, logger);
        var thread = Start(runtime, n, runtimeReturned);
        runtime.DispatchHealer(() =>
        {
            healersStarted.Signal();
            WaitForRelease(releaseHealers, "slow healer release was not signaled");
            Interlocked.Exchange(ref slowHealDone, 1);
        });
        runtime.DispatchHealer(() =>
        {
            healersStarted.Signal();
            WaitForRelease(releaseHealers, "throwing healer release was not signaled");
            throw new InvalidOperationException("heal boom");
        });
        Assert.True(healersStarted.Wait(WaitBudget), "healers did not reach their gates");

        // Act
        SignalStop(n);
        var returnedBeforeRelease = runtimeReturned.Wait(ReturnProbeBudget);
        releaseHealers.Set();
        Assert.True(thread.Join(WaitBudget), "Run did not return after the healers were released");

        // Assert
        Assert.False(returnedBeforeRelease, "Run returned while healers remained gated");
        Assert.Equal(1, Volatile.Read(ref slowHealDone));
        Assert.Contains("heal boom", await ReadLogAsync(logger));
    }
}
