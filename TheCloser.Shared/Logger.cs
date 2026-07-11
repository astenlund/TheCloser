namespace TheCloser.Shared;

public class Logger
{
    private const long MaxLogSizeBytes = 1024 * 1024;

    private readonly string _logPath;
    private readonly Func<DateTime> _utcNow;

    public Logger(string appName, Func<DateTime>? utcNow = null)
    {
        _logPath = GetLogPath(appName);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);

        RotateIfTooLarge();
    }

    public static string GetLogPath(string appName) => Path.Combine(Path.GetTempPath(), appName + ".log");

    public void Log(string msg)
    {
        try
        {
            using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);

            // Empty messages are visual separators between invocations; a timestamp prefix would defeat that.
            writer.WriteLine(string.IsNullOrEmpty(msg) ? msg : $"{_utcNow():O} {msg}");
        }
        catch
        {
            // Logging must never crash the tool; drop the message on any IO failure.
        }
    }

    private void RotateIfTooLarge()
    {
        try
        {
            var info = new FileInfo(_logPath);

            if (!info.Exists || info.Length <= MaxLogSizeBytes)
            {
                return;
            }

            File.Move(_logPath, _logPath + ".old", overwrite: true);
        }
        catch
        {
            // Log rotation must never crash the tool; keep the existing file on any IO failure.
        }
    }
}
