using TheCloser.Shared;

namespace TheCloser.Tests;

public class LoggerTests
{
    private const long RotationThresholdBytes = 1024 * 1024;

    [Fact]
    public void Constructor_FileBelowThreshold_DoesNotRotate()
    {
        // Arrange
        var (appName, logPath) = UniqueLogTarget();
        File.WriteAllBytes(logPath, new byte[16]);

        try
        {
            // Act
            _ = new Logger(appName);

            // Assert
            Assert.True(File.Exists(logPath));
            Assert.False(File.Exists(logPath + ".old"));
        }
        finally
        {
            CleanUp(logPath);
        }
    }

    [Fact]
    public void Constructor_FileAboveThreshold_RotatesToOld()
    {
        // Arrange
        var (appName, logPath) = UniqueLogTarget();
        File.WriteAllBytes(logPath, new byte[RotationThresholdBytes + 1]);

        try
        {
            // Act
            _ = new Logger(appName);

            // Assert
            Assert.False(File.Exists(logPath));
            Assert.True(File.Exists(logPath + ".old"));
        }
        finally
        {
            CleanUp(logPath);
        }
    }

    [Fact]
    public void Constructor_SecondRotation_OverwritesExistingOldFile()
    {
        // Arrange
        var (appName, logPath) = UniqueLogTarget();
        File.WriteAllText(logPath + ".old", "previous generation");
        File.WriteAllBytes(logPath, new byte[RotationThresholdBytes + 1]);

        try
        {
            // Act
            _ = new Logger(appName);

            // Assert
            Assert.Equal(RotationThresholdBytes + 1, new FileInfo(logPath + ".old").Length);
        }
        finally
        {
            CleanUp(logPath);
        }
    }

    [Fact]
    public void Log_TwoLoggerInstancesOnTheSameFile_BothLinesArrive()
    {
        // Arrange
        var (appName, logPath) = UniqueLogTarget();
        var first = new Logger(appName);
        var second = new Logger(appName);

        try
        {
            // Act
            first.Log("line one");
            second.Log("line two");

            // Assert
            var lines = File.ReadAllLines(logPath);
            Assert.Contains("line one", lines);
            Assert.Contains("line two", lines);
        }
        finally
        {
            CleanUp(logPath);
        }
    }

    [Fact]
    public void Log_FileLockedExclusively_DoesNotThrow()
    {
        // Arrange
        var (appName, logPath) = UniqueLogTarget();
        var logger = new Logger(appName);
        using var exclusiveLock = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.None);

        try
        {
            // Act
            var exception = Record.Exception(() => logger.Log("dropped on the floor"));

            // Assert
            Assert.Null(exception);
        }
        finally
        {
            exclusiveLock.Dispose();
            CleanUp(logPath);
        }
    }

    private static (string AppName, string LogPath) UniqueLogTarget()
    {
        var appName = $"TheCloser.Tests.{Guid.NewGuid():N}";

        return (appName, Path.Combine(Path.GetTempPath(), appName + ".log"));
    }

    private static void CleanUp(string logPath)
    {
        File.Delete(logPath);
        File.Delete(logPath + ".old");
    }
}
