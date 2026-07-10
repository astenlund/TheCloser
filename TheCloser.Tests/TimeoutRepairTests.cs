using TheCloser.Shared;

namespace TheCloser.Tests;

public class TimeoutRepairTests
{
    // Only the no-pending path is testable: the restore path invokes SystemParametersInfo and would mutate the real system setting.
    [Fact]
    public void TryRestorePending_NothingPending_ReturnsFalse()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());

        // Act
        var restored = TimeoutRepair.TryRestorePending(state);

        // Assert
        Assert.False(restored);
    }
}
