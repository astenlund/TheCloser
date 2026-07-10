using TheCloser.Shared;

namespace TheCloser.Tests;

public class SharedStateTests
{
    [Fact]
    public void WriteTimestamp_ThenReadTimestamp_RoundTrips()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        var expected = new DateTime(2026, 7, 10, 8, 30, 0, DateTimeKind.Utc);

        // Act
        state.WriteTimestamp(expected);
        var actual = state.ReadTimestamp();

        // Assert
        Assert.Equal(expected.Ticks, actual.Ticks);
    }

    [Fact]
    public void SetTimeoutRepair_MarksPending_AndExposesSavedValue()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        const uint saved = 200000u;

        // Act
        state.SetTimeoutRepair(saved);
        var pending = state.TryReadTimeoutRepair(out var readValue);

        // Assert
        Assert.True(pending);
        Assert.Equal(saved, readValue);
    }

    [Fact]
    public void ClearTimeoutRepair_ClearsFlag_ButLeavesSavedValueReadable()
    {
        // Arrange
        using var state = new SharedState(TestNames.UniqueMapName());
        const uint saved = 200000u;
        state.SetTimeoutRepair(saved);

        // Act
        state.ClearTimeoutRepair();
        var pending = state.TryReadTimeoutRepair(out var readValue);

        // Assert
        Assert.False(pending);
        Assert.Equal(saved, readValue);
    }

    [Fact]
    public void SetTimeoutRepair_IsVisibleThroughASecondHandleOnTheSameMap()
    {
        // Arrange
        var mapName = TestNames.UniqueMapName();
        using var writer = new SharedState(mapName);
        using var reader = new SharedState(mapName);
        const uint saved = 200000u;

        // Act
        writer.SetTimeoutRepair(saved);
        var pending = reader.TryReadTimeoutRepair(out var readValue);

        // Assert
        Assert.True(pending);
        Assert.Equal(saved, readValue);
    }

    [Fact]
    public void ClearTimeoutRepair_ByOneHandle_IsVisibleThroughAnother()
    {
        // Arrange
        var mapName = TestNames.UniqueMapName();
        using var writer = new SharedState(mapName);
        using var reader = new SharedState(mapName);
        writer.SetTimeoutRepair(200000u);

        // Act
        reader.ClearTimeoutRepair();
        var pending = writer.TryReadTimeoutRepair(out _);

        // Assert
        Assert.False(pending);
    }
}
