using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using Xunit;

using static TheCloser.Shared.Constants;

namespace TheCloser.Tests;

public class ActivationHandlerTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);

    private sealed class Harness : IDisposable
    {
        public SharedState State { get; } = new(TestNames.UniqueMapName());
        public string MutexName { get; } = TestNames.UniqueMutexName();
        public TempLogger TempLogger { get; } = new();
        public List<string> Events { get; } = [];
        public long Now = Stopwatch.GetTimestamp();
        public long? Baseline;                    // null lets the handler default to construction time
        public long Tick = 100_000;
        public int TickReadCount;
        public bool CloseResult;
        public bool CloseThrows;
        public bool RestoreResult = true;

        public ActivationHandler Build() => new(
            State,
            TempLogger.Logger,
            MutexName,
            settings: () => new ConfigurationBuilder().Build(),
            runClose: _ =>
            {
                Events.Add("close");

                return CloseThrows ? throw new InvalidOperationException("close failed") : CloseResult;
            },
            dispatchHealer: () => Events.Add("healer"),
            timestamp: () => Now,
            tickCount: () =>
            {
                TickReadCount++;

                return Tick;
            },
            restorePending: _ =>
            {
                Events.Add("restore");

                return RestoreResult;
            },
            initialHandlerExit: Baseline);

        public void AdvancePastThrottle() => Tick += ThrottleThresholdMs + 1;

        // Drains (disposes) the logger, then reads the whole log. Call at most once, always last.
        public async Task<string> ReadLogAsync()
        {
            await TempLogger.DrainAsync();

            return File.ReadAllText(TempLogger.LogPath);
        }

        public void Dispose()
        {
            State.Dispose();
            TempLogger.Dispose();
        }
    }

    [Fact]
    public async Task PlausiblePayload_LogsLatencyAndButton()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.Now += Stopwatch.Frequency / 100; // press 10 ms ago relative to handler entry
        h.State.WriteActivationPayload(h.Now - Stopwatch.Frequency / 100, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        var log = await h.ReadLogAsync();
        Assert.Contains("Activation: latency", log);
        Assert.Contains("XButton2", log);
    }

    [Fact]
    public async Task ZeroPayload_LogsUnavailableAndUnknownButton()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();

        // Act
        handler.HandleActivation();

        // Assert
        var log = await h.ReadLogAsync();
        Assert.Contains("latency unavailable", log);
        Assert.Contains("unknown", log);
    }

    [Fact]
    public async Task StalePayloadOverTenSeconds_LogsUnavailable()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.State.WriteActivationPayload(h.Now - 11 * Stopwatch.Frequency, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains("latency unavailable", await h.ReadLogAsync());
    }

    [Fact]
    public async Task PlausibleQpcPredatingHandlerExit_LogsDeferredMarker()
    {
        // Arrange: first activation stamps the handler exit; the second press's QPC predates it.
        using var h = new Harness();
        var handler = h.Build();
        handler.HandleActivation();
        var pressBeforeExit = h.Now - Stopwatch.Frequency / 1000;
        h.AdvancePastThrottle();
        h.Now += Stopwatch.Frequency / 100;
        h.State.WriteActivationPayload(pressBeforeExit, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains("(deferred)", await h.ReadLogAsync());
    }

    [Fact]
    public async Task PressPredatingThrottleSkipExit_IsMarkedDeferred()
    {
        // Arrange: a throttle-skipped activation must still refresh the handler-exit stamp. The
        // press below lands between the first handling's exit and the skip's later exit, so a
        // correct implementation judges it deferred; one that fails to restamp on the skip path
        // would compare against the first exit, read the press as fresh, and fail this test.
        using var h = new Harness();
        var handler = h.Build();
        handler.HandleActivation();                        // stamps exit at the initial h.Now
        var pressAfterFirstExit = h.Now + Stopwatch.Frequency / 100;
        h.Now += Stopwatch.Frequency / 50;                 // the skip exits 20 ms later
        handler.HandleActivation();                        // throttle skip (Tick unchanged), restamps exit
        h.AdvancePastThrottle();
        h.Now += Stopwatch.Frequency / 100;
        h.State.WriteActivationPayload(pressAfterFirstExit, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains("(deferred)", await h.ReadLogAsync());
    }

    [Fact]
    public async Task PressPredatingConstruction_ButAfterTheSuppliedBaseline_IsNotMarkedDeferred()
    {
        // Arrange: the daemon constructs its handler lazily on the first activation, so that
        // press always predates construction. With the daemon's start time supplied as the
        // baseline the press reads as fresh; seeding from construction time instead would mark
        // every daemon lifetime's first press deferred.
        using var h = new Harness();
        var daemonStart = h.Now;
        var press = h.Now + Stopwatch.Frequency / 100;
        h.Now += Stopwatch.Frequency / 50;        // construction lands 20 ms after the press
        h.Baseline = daemonStart;
        var handler = h.Build();
        h.State.WriteActivationPayload(press, TriggerButtonXButton2);

        // Act
        handler.HandleActivation();

        // Assert
        Assert.DoesNotContain("(deferred)", await h.ReadLogAsync());
    }

    [Fact]
    public async Task WithinThrottle_SkipsCloseAndLogs()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        handler.HandleActivation();

        // Act: Tick unchanged, so the second activation is inside the threshold.
        handler.HandleActivation();

        // Assert
        Assert.Single(h.Events, e => e == "close");
        Assert.Contains("Activation skipped: the previous handling", await h.ReadLogAsync());
    }

    [Fact]
    public async Task BusyGuardMutex_SkipsAndLogs()
    {
        // Arrange: hold the guard mutex on another thread for the duration of the call.
        using var h = new Harness();
        var handler = h.Build();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = new Mutex(true, h.MutexName, out _);
            held.Set();
            release.Wait(WaitBudget);
            m.ReleaseMutex();
        })
        {
            IsBackground = true
        };
        holder.Start();
        Assert.True(held.Wait(WaitBudget), "the holder never took the guard mutex");

        // Act, with the release guaranteed even if the act throws, so a failure fails one test
        // rather than stranding the holder.
        try
        {
            handler.HandleActivation();
        }
        finally
        {
            release.Set();
            Assert.True(holder.Join(WaitBudget), "the holder never released the guard mutex");
        }

        // Assert
        Assert.DoesNotContain(h.Events, e => e == "close");
        Assert.Equal(0, h.TickReadCount);
        Assert.Contains("guard mutex is held", await h.ReadLogAsync());
    }

    [Fact]
    public void AbandonedGuardMutex_CountsAsAcquired()
    {
        // Arrange: a thread takes ownership and dies without releasing.
        using var h = new Harness();
        var handler = h.Build();
        Mutex? abandoned = null;
        var t = new Thread(() => abandoned = new Mutex(true, h.MutexName, out _));
        t.Start();
        t.Join();

        // Act
        handler.HandleActivation();

        // Assert
        Assert.Contains(h.Events, e => e == "close");
        abandoned?.Dispose();
    }

    [Fact]
    public void PendingRepair_RestoredBeforeClose()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.State.SetTimeoutRepair(200);

        // Act
        handler.HandleActivation();

        // Assert: restore observed, and strictly before the close.
        Assert.Equal(["restore", "close"], h.Events);
    }

    [Fact]
    public async Task ThrowingClose_IsLoggedAndSwallowed_AndReleasesMutex()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.CloseThrows = true;

        // Act
        handler.HandleActivation();

        // Assert: the exception is logged, and the handle was released and disposed (a fresh
        // owned create sees a brand-new object).
        Assert.Contains("close failed", await h.ReadLogAsync());
        using var probe = new Mutex(initiallyOwned: true, h.MutexName, out var createdNew);
        Assert.True(createdNew);
        probe.ReleaseMutex();
    }

    [Fact]
    public void AttachClose_DispatchesHealer_NonAttachDoesNot()
    {
        // Arrange
        using var h = new Harness();
        var handler = h.Build();
        h.CloseResult = true;

        // Act
        handler.HandleActivation();
        h.AdvancePastThrottle();
        h.CloseResult = false;
        handler.HandleActivation();

        // Assert
        Assert.Equal(1, h.Events.Count(e => e == "healer"));
    }
}
