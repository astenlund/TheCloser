using System.Diagnostics;
using TheCloser.Shared;

namespace TheCloser.Tests;

public sealed class LoggerTests : IDisposable
{
    private const long RotationThresholdBytes = 1024 * 1024;

    private static readonly DateTime FixedUtcTimestamp = new(2026, 7, 11, 12, 34, 56, DateTimeKind.Utc);

    private readonly string _appName = TestNames.UniqueLoggerName();
    private readonly string _logPath;

    public LoggerTests()
    {
        _logPath = Logger.GetLogPath(_appName);
    }

    // xUnit creates one instance per test and disposes it even when the test fails, so this replaces per-test try/finally cleanup.
    public void Dispose()
    {
        File.Delete(_logPath);
        File.Delete(_logPath + ".old");
    }

    [Fact]
    public async Task DisposeAsync_FileBelowThreshold_DoesNotRotate()
    {
        // Arrange
        File.WriteAllBytes(_logPath, new byte[16]);

        // Act
        var logger = new Logger(_appName);
        await logger.DisposeAsync();

        // Assert
        Assert.True(File.Exists(_logPath));
        Assert.False(File.Exists(_logPath + ".old"));
    }

    [Fact]
    public async Task DisposeAsync_FileAboveThreshold_RotatesToOld()
    {
        // Arrange
        File.WriteAllBytes(_logPath, new byte[RotationThresholdBytes + 1]);

        // Act
        var logger = new Logger(_appName);
        await logger.DisposeAsync();

        // Assert
        Assert.False(File.Exists(_logPath));
        Assert.True(File.Exists(_logPath + ".old"));
    }

    [Fact]
    public async Task DisposeAsync_FileExactlyAtThreshold_DoesNotRotate()
    {
        // Arrange
        File.WriteAllBytes(_logPath, new byte[RotationThresholdBytes]);

        // Act
        var logger = new Logger(_appName);
        await logger.DisposeAsync();

        // Assert
        Assert.True(File.Exists(_logPath));
        Assert.False(File.Exists(_logPath + ".old"));
    }

    [Fact]
    public async Task DisposeAsync_SecondRotation_OverwritesExistingOldFile()
    {
        // Arrange
        File.WriteAllText(_logPath + ".old", "previous generation");
        File.WriteAllBytes(_logPath, new byte[RotationThresholdBytes + 1]);

        // Act
        var logger = new Logger(_appName);
        await logger.DisposeAsync();

        // Assert
        Assert.Equal(RotationThresholdBytes + 1, new FileInfo(_logPath + ".old").Length);
    }

    [Fact]
    public async Task DisposeAsync_TwoLoggerInstancesOnTheSameFile_BothLinesArrive()
    {
        // Arrange
        var first = new Logger(_appName);
        var second = new Logger(_appName);

        // Act
        first.Log("line one");
        second.Log("line two");
        await first.DisposeAsync();
        await second.DisposeAsync();

        // Assert
        var lines = File.ReadAllLines(_logPath);
        Assert.Contains(lines, line => line.EndsWith("line one"));
        Assert.Contains(lines, line => line.EndsWith("line two"));
    }

    [Fact]
    public async Task DisposeAsync_FileLockedExclusively_DoesNotThrow()
    {
        // Arrange
        using var exclusiveLock = new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var logger = new Logger(_appName);

        // Act
        logger.Log("dropped on the floor");
        var exception = await Record.ExceptionAsync(async () => await logger.DisposeAsync());

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposeAsync_NonEmptyMessage_PrefixesUtcTimestamp()
    {
        // Arrange
        var logger = new Logger(_appName, () => FixedUtcTimestamp);

        // Act
        logger.Log("hello");
        await logger.DisposeAsync();

        // Assert
        var line = Assert.Single(File.ReadAllLines(_logPath));
        Assert.Equal($"{FixedUtcTimestamp:O} hello", line);
    }

    [Fact]
    public async Task DisposeAsync_EmptyMessage_WritesBareSeparatorLine()
    {
        // Arrange
        var logger = new Logger(_appName, () => FixedUtcTimestamp);

        // Act
        logger.Log("");
        await logger.DisposeAsync();

        // Assert
        var line = Assert.Single(File.ReadAllLines(_logPath));
        Assert.Equal(string.Empty, line);
    }

    [Fact]
    public async Task Log_WriterIsBlocked_EnqueuesWithoutWaiting()
    {
        // Arrange
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var logger = new Logger(_appName, () => FixedUtcTimestamp, () => { }, _ =>
        {
            writerEntered.Set();
            releaseWriter.Wait();
        });

        try
        {
            logger.Log("first");
            Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)));

            // Act
            var enqueueTask = Task.Run(() => logger.Log("second"));

            // Assert
            await enqueueTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseWriter.Set();
            await logger.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_QueuedMessages_DrainsInOrderBeforeReturning()
    {
        // Arrange
        var writtenLines = new List<string>();
        var logger = new Logger(_appName, () => FixedUtcTimestamp, () => { }, writtenLines.Add);
        logger.Log("first");
        logger.Log("");
        logger.Log("third");

        // Act
        await logger.DisposeAsync();

        // Assert
        Assert.Equal(
            [
                $"{FixedUtcTimestamp:O} first",
                string.Empty,
                $"{FixedUtcTimestamp:O} third"
            ],
            writtenLines);
    }

    [Fact]
    public async Task Log_QueuedWriteIsDelayed_CapturesTimestampWhenEnqueued()
    {
        // Arrange
        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var currentTicks = FixedUtcTimestamp.Ticks;
        var writtenLines = new List<string>();
        var writeCount = 0;
        var logger = new Logger(_appName, () => new DateTime(Interlocked.Read(ref currentTicks), DateTimeKind.Utc), () => { }, line =>
        {
            writtenLines.Add(line);

            if (Interlocked.Increment(ref writeCount) == 1)
            {
                writerEntered.Set();
                releaseWriter.Wait();
            }
        });

        try
        {
            logger.Log("first");
            Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)));
            Interlocked.Exchange(ref currentTicks, FixedUtcTimestamp.AddMinutes(1).Ticks);
            logger.Log("second");
            Interlocked.Exchange(ref currentTicks, FixedUtcTimestamp.AddMinutes(2).Ticks);

            // Act
            releaseWriter.Set();
            await logger.DisposeAsync();

            // Assert
            Assert.Equal(
                [
                    $"{FixedUtcTimestamp:O} first",
                    $"{FixedUtcTimestamp.AddMinutes(1):O} second"
                ],
                writtenLines);
        }
        finally
        {
            releaseWriter.Set();
            await logger.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_AnotherProcessOwnsLogMutex_PreservesBothMessages()
    {
        // Arrange
        var readyPath = _logPath + ".ready";
        var releasePath = _logPath + ".release";
        using var childProcess = StartLogMutexHolder(_appName, _logPath, readyPath, releasePath);
        Task? drainTask = null;

        try
        {
            await WaitForFileAsync(readyPath, childProcess);
            var logger = new Logger(_appName, () => FixedUtcTimestamp);
            logger.Log("parent");
            drainTask = logger.DisposeAsync().AsTask();

            // Act
            File.WriteAllText(releasePath, "");
            await childProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await drainTask.WaitAsync(TimeSpan.FromSeconds(5));

            // Assert
            Assert.Equal(0, childProcess.ExitCode);
            var lines = File.ReadAllLines(_logPath);
            Assert.Contains("child", lines);
            Assert.Contains($"{FixedUtcTimestamp:O} parent", lines);
        }
        finally
        {
            await StopLogMutexHolderAsync(childProcess, releasePath);

            if (drainTask is not null)
            {
                await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
            }

            File.Delete(readyPath);
            File.Delete(releasePath);
        }
    }

    [Fact]
    public async Task DisposeAsync_AnotherProcessRetainsLogMutex_CompletesWithoutRelease()
    {
        // Arrange
        var readyPath = _logPath + ".ready";
        var releasePath = _logPath + ".release";
        using var childProcess = StartLogMutexHolder(_appName, _logPath, readyPath, releasePath);
        Task? drainTask = null;

        try
        {
            await WaitForFileAsync(readyPath, childProcess);
            var logger = new Logger(_appName, () => FixedUtcTimestamp);
            logger.Log("dropped while mutex is retained");
            drainTask = logger.DisposeAsync().AsTask();

            // Act
            await drainTask.WaitAsync(TimeSpan.FromSeconds(2));

            // Assert
            Assert.False(childProcess.HasExited);
        }
        finally
        {
            await StopLogMutexHolderAsync(childProcess, releasePath);

            if (drainTask is not null)
            {
                await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
            }

            File.Delete(readyPath);
            File.Delete(releasePath);
        }
    }

    private static Process StartLogMutexHolder(string appName, string logPath, string readyPath, string releasePath)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HoldLogMutex.ps1");
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-MutexName");
        startInfo.ArgumentList.Add(Constants.GetLogMutexName(appName));
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(logPath);
        startInfo.ArgumentList.Add("-ReadyPath");
        startInfo.ArgumentList.Add(readyPath);
        startInfo.ArgumentList.Add("-ReleasePath");
        startInfo.ArgumentList.Add(releasePath);

        return Process.Start(startInfo)!;
    }

    private static async Task StopLogMutexHolderAsync(Process process, string releasePath)
    {
        TrySignalRelease(releasePath);

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The child exited between the timeout and the kill request.
            }

            await process.WaitForExitAsync();
        }
    }

    private static async Task WaitForFileAsync(string path, Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!File.Exists(path))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The log-lock child exited with code {process.ExitCode} before becoming ready.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static void TrySignalRelease(string path)
    {
        try
        {
            File.WriteAllText(path, "");
        }
        catch
        {
            // Cleanup continues by terminating the child if the release sentinel cannot be written.
        }
    }
}
