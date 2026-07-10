using TheCloser.Shared;

namespace TheCloser.Tests;

public class ForegroundLockSuppressionTests
{
    private static readonly Logger Logger = new(TestNames.UniqueLoggerName());

    [Fact]
    public void Constructor_NoPendingRecord_PublishesRecordAndDisables()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        var disabled = false;

        // Act
        using var suppression = new ForegroundLockSuppression(state, Logger, TryGetReturning(200000u), () => disabled = true, _ => true);

        // Assert
        Assert.True(disabled);
        Assert.True(state.TryReadTimeoutRepair(out var saved));
        Assert.Equal(200000u, saved);
    }

    [Fact]
    public void Constructor_TimeoutUnreadable_LeavesNoRecord()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        var disableCalled = false;

        // Act
        using var suppression = new ForegroundLockSuppression(state, Logger, FailingTryGet, () => disableCalled = true, _ => true);

        // Assert
        Assert.False(disableCalled);
        Assert.False(state.TryReadTimeoutRepair(out _));
    }

    [Fact]
    public void Dispose_DisableFailedWithPendingRecord_NeverInvokesRestore()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        state.SetTimeoutRepair(200000u);
        var restoreCalled = false;
        var suppression = new ForegroundLockSuppression(state, Logger, TryGetReturning(0u), () => false, _ => restoreCalled = true);

        // Act
        suppression.Dispose();

        // Assert
        Assert.False(restoreCalled);
        Assert.True(state.TryReadTimeoutRepair(out var saved));
        Assert.Equal(200000u, saved);
    }

    [Fact]
    public void Dispose_TimeoutUnreadableWithPendingRecord_LeavesRecordIntact()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        state.SetTimeoutRepair(200000u);
        var restoreCalled = false;
        var suppression = new ForegroundLockSuppression(state, Logger, FailingTryGet, () => true, _ => restoreCalled = true);

        // Act
        suppression.Dispose();

        // Assert
        Assert.False(restoreCalled);
        Assert.True(state.TryReadTimeoutRepair(out var saved));
        Assert.Equal(200000u, saved);
    }

    [Fact]
    public void Constructor_PendingRecord_NeverOverwritesSavedValue()
    {
        // Arrange: an earlier failed restore left the record pending while the system value reads as disabled.
        using var state = new SharedState(TestNames.UniqueMapName());
        state.SetTimeoutRepair(200000u);

        // Act
        using var suppression = new ForegroundLockSuppression(state, Logger, TryGetReturning(0u), () => true, _ => true);

        // Assert
        Assert.True(state.TryReadTimeoutRepair(out var saved));
        Assert.Equal(200000u, saved);
    }

    [Fact]
    public void Constructor_DisableFails_ClearsTheFreshRecord()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());

        // Act
        using var suppression = new ForegroundLockSuppression(state, Logger, TryGetReturning(200000u), () => false, _ => true);

        // Assert
        Assert.False(state.TryReadTimeoutRepair(out _));
    }

    [Fact]
    public void Constructor_DisableFailsWithPendingRecord_KeepsTheRecord()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        state.SetTimeoutRepair(200000u);

        // Act
        using var suppression = new ForegroundLockSuppression(state, Logger, TryGetReturning(0u), () => false, _ => true);

        // Assert
        Assert.True(state.TryReadTimeoutRepair(out var saved));
        Assert.Equal(200000u, saved);
    }

    [Fact]
    public void Dispose_RestoreSucceeds_RestoresOriginalAndClears()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        uint restoredValue = 0;
        var suppression = new ForegroundLockSuppression(state, Logger, TryGetReturning(200000u), () => true, value =>
        {
            restoredValue = value;

            return true;
        });

        // Act
        suppression.Dispose();

        // Assert
        Assert.Equal(200000u, restoredValue);
        Assert.False(state.TryReadTimeoutRepair(out _));
    }

    [Fact]
    public void Dispose_RestoreFails_KeepsRecordPending()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        var suppression = new ForegroundLockSuppression(state, Logger, TryGetReturning(200000u), () => true, _ => false);

        // Act
        suppression.Dispose();

        // Assert
        Assert.True(state.TryReadTimeoutRepair(out var saved));
        Assert.Equal(200000u, saved);
    }

    [Fact]
    public void Dispose_PendingRecordFromEarlierFailure_RestoresTheSavedValueNotTheCurrentOne()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        state.SetTimeoutRepair(200000u);
        var restoredValue = 0u;
        var suppression = new ForegroundLockSuppression(state, Logger, TryGetReturning(0u), () => true, value =>
        {
            restoredValue = value;

            return true;
        });

        // Act
        suppression.Dispose();

        // Assert
        Assert.Equal(200000u, restoredValue);
        Assert.False(state.TryReadTimeoutRepair(out _));
    }

    [Fact]
    public void Dispose_DisableFailed_NeverInvokesRestore()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        var restoreCalled = false;
        var suppression = new ForegroundLockSuppression(state, Logger, TryGetReturning(200000u), () => false, _ => restoreCalled = true);

        // Act
        suppression.Dispose();

        // Assert
        Assert.False(restoreCalled);
    }

    [Fact]
    public void Dispose_TimeoutUnreadable_NeverInvokesRestore()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        var restoreCalled = false;
        var suppression = new ForegroundLockSuppression(state, Logger, FailingTryGet, () => true, _ => restoreCalled = true);

        // Act
        suppression.Dispose();

        // Assert
        Assert.False(restoreCalled);
    }

    private static ForegroundLockSuppression.TryGetTimeoutHandler TryGetReturning(uint timeout) =>
        (out uint value) =>
        {
            value = timeout;

            return true;
        };

    private static bool FailingTryGet(out uint value)
    {
        value = 0;

        return false;
    }
}
