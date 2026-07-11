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

    [Fact]
    public void ResolveKillMethodName_EmptyString_FallsBackToTheDefault()
    {
        // Arrange
        var closer = CreateCloser();

        // Act
        var resolved = closer.ResolveKillMethodName("");

        // Assert
        Assert.Equal("CTRL-W", resolved);
    }

    [Theory]
    [InlineData("WM_DESTROY")]
    [InlineData("WM_CLOSE")]
    [InlineData("WM_QUIT")]
    [InlineData("SC_CLOSE")]
    [InlineData("ESCAPE")]
    [InlineData("ALT-F4")]
    [InlineData("CTRL-F4")]
    [InlineData("CTRL-W")]
    [InlineData("CTRL-SHIFT-W")]
    public void ResolveKillMethodName_EveryDocumentedMethod_ResolvesVerbatim(string documented)
    {
        // Arrange
        var closer = CreateCloser();

        // Act
        var resolved = closer.ResolveKillMethodName(documented);

        // Assert
        Assert.Equal(documented, resolved);
    }

    [Fact]
    public void ResolveKillMethodName_UnknownMethod_LogsTheFallbackWarning()
    {
        // Arrange
        var closer = CreateCloser();

        // Act
        closer.ResolveKillMethodName("NO-SUCH-METHOD");

        // Assert
        Assert.Contains("No kill action configured for method 'NO-SUCH-METHOD'", File.ReadAllText(_tempLogger.LogPath));
    }

    private WindowCloser CreateCloser() => new(new ConfigurationBuilder().Build(), _sharedState, _tempLogger.Logger);
}
