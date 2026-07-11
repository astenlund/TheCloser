using Microsoft.Extensions.Configuration;
using TheCloser.Shared;

namespace TheCloser.Tests;

public sealed class WindowCloserTests : IDisposable
{
    private readonly TempLogger _tempLogger = new();
    private readonly SharedState _sharedState = new(TestNames.UniqueMapName());

    public void Dispose()
    {
        _sharedState.Dispose();
        _tempLogger.Dispose();
    }

    [Theory]
    [InlineData(null, "CTRL-W")]
    [InlineData("NO-SUCH-METHOD", "CTRL-W")]
    [InlineData("ctrl-shift-w", "ctrl-shift-w")]
    [InlineData("WM_CLOSE", "WM_CLOSE")]
    public void ResolveKillMethodName_ResolvesKnownMethodsCaseInsensitivelyAndFallsBackOtherwise(string? configured, string expected)
    {
        // Arrange
        var closer = CreateCloser();

        // Act
        var resolved = closer.ResolveKillMethodName(configured);

        // Assert
        Assert.Equal(expected, resolved);
    }

    private WindowCloser CreateCloser() => new(new ConfigurationBuilder().Build(), _sharedState, _tempLogger.Logger);
}
