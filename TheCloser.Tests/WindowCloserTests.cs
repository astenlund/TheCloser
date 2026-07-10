using Microsoft.Extensions.Configuration;
using TheCloser.Shared;

namespace TheCloser.Tests;

public class WindowCloserTests
{
    [Theory]
    [InlineData(null, "CTRL-W")]
    [InlineData("NO-SUCH-METHOD", "CTRL-W")]
    [InlineData("ctrl-shift-w", "ctrl-shift-w")]
    [InlineData("WM_CLOSE", "WM_CLOSE")]
    public void ResolveKillMethodName_ResolvesKnownMethodsCaseInsensitivelyAndFallsBackOtherwise(string? configured, string expected)
    {
        // Arrange
        using var sharedState = new SharedState(TestNames.UniqueMapName());
        var closer = new WindowCloser(new ConfigurationBuilder().Build(), sharedState, new Logger($"TheCloser.Tests.{Guid.NewGuid():N}"));

        // Act
        var resolved = closer.ResolveKillMethodName(configured);

        // Assert
        Assert.Equal(expected, resolved);
    }
}
