using TheCloser.Shared;

using DaemonProgram = TheCloser.Daemon.Program;

namespace TheCloser.Tests;

public class CrashRepairTests
{
    private static readonly Logger Logger = new($"TheCloser.Tests.{Guid.NewGuid():N}");

    [Fact]
    public void TryRepairCrashedState_NothingPending_DoesNothing()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        var restoreCalled = false;

        // Act
        var repaired = DaemonProgram.TryRepairCrashedState(state, TestNames.UniqueMutexName(), Logger, _ => restoreCalled = true);

        // Assert
        Assert.False(repaired);
        Assert.False(restoreCalled);
    }

    [Fact]
    public void TryRepairCrashedState_PendingAndAppDead_RestoresAndClears()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        state.SetTimeoutRepair(200000u);
        uint restoredValue = 0;

        // Act
        var repaired = DaemonProgram.TryRepairCrashedState(state, TestNames.UniqueMutexName(), Logger, value =>
        {
            restoredValue = value;

            return true;
        });

        // Assert
        Assert.True(repaired);
        Assert.Equal(200000u, restoredValue);
        Assert.False(state.TryReadTimeoutRepair(out _));
    }

    [Fact]
    public void TryRepairCrashedState_PendingButAppAlive_LeavesRecordUntouched()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        state.SetTimeoutRepair(200000u);
        var mutexName = TestNames.UniqueMutexName();
        using var liveAppMutex = new Mutex(true, mutexName, out _);
        var restoreCalled = false;

        // Act
        var repaired = DaemonProgram.TryRepairCrashedState(state, mutexName, Logger, _ => restoreCalled = true);

        // Assert
        Assert.False(repaired);
        Assert.False(restoreCalled);
        Assert.True(state.TryReadTimeoutRepair(out _));
    }

    [Fact]
    public void TryRepairCrashedState_RestoreFails_KeepsRecordForNextTick()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        state.SetTimeoutRepair(200000u);

        // Act
        var repaired = DaemonProgram.TryRepairCrashedState(state, TestNames.UniqueMutexName(), Logger, _ => false);

        // Assert
        Assert.False(repaired);
        Assert.True(state.TryReadTimeoutRepair(out _));
    }
}
