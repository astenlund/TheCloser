using System.Threading.Channels;

namespace TheCloser.Shared;

public class Logger : IAsyncDisposable
{
    private const long MaxLogSizeBytes = 1024 * 1024;

    private static readonly TimeSpan LogMutexWaitTimeout = TimeSpan.FromMilliseconds(250);

    private readonly string _logPath;
    private readonly string _logMutexName;
    private readonly Channel<LogEntry> _messages;
    private readonly Action<string> _writeLine;
    private readonly Action _rotate;
    private readonly Task _workerTask;
    private readonly Func<DateTime> _utcNow;
    private int _disposeState;

    public Logger(string appName, Func<DateTime>? utcNow = null)
        : this(appName, utcNow ?? (() => DateTime.UtcNow), rotate: null, writeLine: null)
    {
    }

    internal Logger(string appName, Func<DateTime> utcNow, Action? rotate, Action<string>? writeLine)
    {
        _logPath = GetLogPath(appName);
        _logMutexName = Constants.GetLogMutexName(appName);
        _utcNow = utcNow;
        _rotate = rotate ?? RotateIfTooLarge;
        _writeLine = writeLine ?? WriteLine;
        _messages = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });
        _workerTask = Task.Run(ProcessMessagesAsync);
    }

    public static string GetLogPath(string appName) => Path.Combine(Path.GetTempPath(), appName + ".log");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _messages.Writer.TryComplete();
        }

        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        catch
        {
            // Logging must never crash the tool; abandon the worker on any unexpected failure.
        }
    }

    public void Log(string msg)
    {
        try
        {
            var timestamp = string.IsNullOrEmpty(msg) ? default : _utcNow();
            _messages.Writer.TryWrite(new LogEntry(timestamp, msg));
        }
        catch
        {
            // Logging must never crash the tool; drop the message if it cannot be queued.
        }
    }

    private async Task ProcessMessagesAsync()
    {
        using var logMutex = new Mutex(initiallyOwned: false, _logMutexName);
        TryRotate(logMutex);

        await foreach (var entry in _messages.Reader.ReadAllAsync())
        {
            TryWriteLine(logMutex, entry);
        }
    }

    private void TryRotate(Mutex logMutex)
    {
        try
        {
            ExecuteWithLogMutex(logMutex, _rotate);
        }
        catch
        {
            // Log rotation must never crash the tool; keep the existing file on any IO failure.
        }
    }

    private void TryWriteLine(Mutex logMutex, LogEntry entry)
    {
        try
        {
            ExecuteWithLogMutex(logMutex, () => _writeLine(string.IsNullOrEmpty(entry.Message) ? entry.Message : $"{entry.Timestamp:O} {entry.Message}"));
        }
        catch
        {
            // Logging must never crash the tool; drop the message on any IO failure.
        }
    }

    private static void ExecuteWithLogMutex(Mutex logMutex, Action action)
    {
        var acquired = false;

        try
        {
            try
            {
                acquired = logMutex.WaitOne(LogMutexWaitTimeout);
            }
            catch (AbandonedMutexException)
            {
                // WaitOne grants ownership when reporting an abandoned mutex, so the protected operation can continue.
                acquired = true;
            }

            if (!acquired)
            {
                return;
            }

            action();
        }
        finally
        {
            if (acquired)
            {
                logMutex.ReleaseMutex();
            }
        }
    }

    private void RotateIfTooLarge()
    {
        var info = new FileInfo(_logPath);

        if (!info.Exists || info.Length <= MaxLogSizeBytes)
        {
            return;
        }

        File.Move(_logPath, _logPath + ".old", overwrite: true);
    }

    private void WriteLine(string line)
    {
        using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(line);
    }

    private readonly record struct LogEntry(DateTime Timestamp, string Message);
}
