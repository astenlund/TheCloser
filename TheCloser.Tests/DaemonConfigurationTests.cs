using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using Xunit;

namespace TheCloser.Tests;

public class DaemonConfigurationTests
{
    [Fact]
    public void MissingFile_YieldsEmptyConfiguration()
    {
        // Arrange
        var directory = CreateTempDirectory();
        IConfigurationRoot? root = null;

        try
        {
            // Act
            root = DaemonConfiguration.Build(directory, _ => { });

            // Assert
            Assert.Empty(root.AsEnumerable());
        }
        finally
        {
            (root as IDisposable)?.Dispose();
            DeleteQuietly(directory);
        }
    }

    [Fact]
    public void MalformedJson_LogsAndYieldsEmptyConfiguration()
    {
        // Arrange
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{ not json");
        var logged = new List<string>();
        IConfigurationRoot? root = null;

        try
        {
            // Act
            root = DaemonConfiguration.Build(directory, logged.Add);

            // Assert
            Assert.Contains(logged, line => line.Contains("Configuration reload failed"));
            Assert.Empty(root.AsEnumerable());
        }
        finally
        {
            (root as IDisposable)?.Dispose();
            DeleteQuietly(directory);
        }
    }

    [Fact]
    public void ValidFile_ExposesValues()
    {
        // Arrange
        var directory = CreateTempDirectory();
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{ \"chrome\": \"CTRL-F4\" }");
        IConfigurationRoot? root = null;

        try
        {
            // Act
            root = DaemonConfiguration.Build(directory, _ => { });

            // Assert
            Assert.Equal("CTRL-F4", root["chrome"]);
        }
        finally
        {
            (root as IDisposable)?.Dispose();
            DeleteQuietly(directory);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TheCloserConfigTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        return directory;
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // The config file watcher can hold the directory handle briefly; the GUID-suffixed
            // temp directory is left to OS temp cleanup instead of failing the test.
        }
    }
}
