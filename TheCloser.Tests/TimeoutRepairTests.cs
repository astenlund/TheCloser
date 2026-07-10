using TheCloser.Shared;

namespace TheCloser.Tests;

public class TimeoutRepairTests
{
    [Fact]
    public void TryRestorePending_NothingPending_ReturnsFalseWithoutRestoring()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        var restoreCalled = false;

        // Act
        var restored = TimeoutRepair.TryRestorePending(state, _ => restoreCalled = true);

        // Assert
        Assert.False(restored);
        Assert.False(restoreCalled);
    }

    [Fact]
    public void TryRestorePending_PendingRecord_RestoresSavedValueAndClearsFlag()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        state.SetTimeoutRepair(200000u);
        uint restoredValue = 0;

        // Act
        var restored = TimeoutRepair.TryRestorePending(state, value =>
        {
            restoredValue = value;

            return true;
        });

        // Assert
        Assert.True(restored);
        Assert.Equal(200000u, restoredValue);
        Assert.False(state.TryReadTimeoutRepair(out _));
    }

    [Fact]
    public void RestoreAndClear_RestoreFails_KeepsRecordPending()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        state.SetTimeoutRepair(200000u);

        // Act
        var cleared = TimeoutRepair.RestoreAndClear(state, 200000u, _ => false);

        // Assert
        Assert.False(cleared);
        Assert.True(state.TryReadTimeoutRepair(out var stillSaved));
        Assert.Equal(200000u, stillSaved);
    }
}
