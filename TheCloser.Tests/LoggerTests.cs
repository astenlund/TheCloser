using TheCloser.Shared;

namespace TheCloser.Tests;

public sealed class LoggerTests : IDisposable
{
    private const long RotationThresholdBytes = 1024 * 1024;

    private readonly string _appName = TestNames.UniqueLoggerName();
    private readonly string _logPath;

    public LoggerTests()
    {
        _logPath = Path.Combine(Path.GetTempPath(), _appName + ".log");
    }

    // xUnit creates one instance per test and disposes it even when the test fails, so this replaces per-test try/finally cleanup.
    public void Dispose()
    {
        File.Delete(_logPath);
        File.Delete(_logPath + ".old");
    }

    [Fact]
    public void Constructor_FileBelowThreshold_DoesNotRotate()
    {
        // Arrange
        File.WriteAllBytes(_logPath, new byte[16]);

        // Act
        _ = new Logger(_appName);

        // Assert
        Assert.True(File.Exists(_logPath));
        Assert.False(File.Exists(_logPath + ".old"));
    }

    [Fact]
    public void Constructor_FileAboveThreshold_RotatesToOld()
    {
        // Arrange
        File.WriteAllBytes(_logPath, new byte[RotationThresholdBytes + 1]);

        // Act
        _ = new Logger(_appName);

        // Assert
        Assert.False(File.Exists(_logPath));
        Assert.True(File.Exists(_logPath + ".old"));
    }

    [Fact]
    public void Constructor_SecondRotation_OverwritesExistingOldFile()
    {
        // Arrange
        File.WriteAllText(_logPath + ".old", "previous generation");
        File.WriteAllBytes(_logPath, new byte[RotationThresholdBytes + 1]);

        // Act
        _ = new Logger(_appName);

        // Assert
        Assert.Equal(RotationThresholdBytes + 1, new FileInfo(_logPath + ".old").Length);
    }

    [Fact]
    public void Log_TwoLoggerInstancesOnTheSameFile_BothLinesArrive()
    {
        // Arrange
        var first = new Logger(_appName);
        var second = new Logger(_appName);

        // Act
        first.Log("line one");
        second.Log("line two");

        // Assert
        var lines = File.ReadAllLines(_logPath);
        Assert.Contains(lines, line => line.EndsWith("line one"));
        Assert.Contains(lines, line => line.EndsWith("line two"));
    }

    [Fact]
    public void Log_FileLockedExclusively_DoesNotThrow()
    {
        // Arrange
        var logger = new Logger(_appName);
        using var exclusiveLock = new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.None);

        // Act
        var exception = Record.Exception(() => logger.Log("dropped on the floor"));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void Log_NonEmptyMessage_PrefixesUtcTimestamp()
    {
        // Arrange
        var fixedTime = new DateTime(2026, 7, 11, 12, 34, 56, DateTimeKind.Utc);
        var logger = new Logger(_appName, () => fixedTime);

        // Act
        logger.Log("hello");

        // Assert
        var line = Assert.Single(File.ReadAllLines(_logPath));
        Assert.Equal($"{fixedTime:O} hello", line);
    }

    [Fact]
    public void Log_EmptyMessage_WritesBareSeparatorLine()
    {
        // Arrange
        var logger = new Logger(_appName, () => new DateTime(2026, 7, 11, 12, 34, 56, DateTimeKind.Utc));

        // Act
        logger.Log("");

        // Assert
        var line = Assert.Single(File.ReadAllLines(_logPath));
        Assert.Equal(string.Empty, line);
    }
}
