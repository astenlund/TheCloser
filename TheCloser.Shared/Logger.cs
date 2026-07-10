namespace TheCloser.Shared;

public class Logger
{
    private const long MaxLogSizeBytes = 1024 * 1024;

    private readonly string _logPath;

    public Logger(string appName)
    {
        _logPath = Path.Combine(Path.GetTempPath(), appName + ".log");

        RotateIfTooLarge();
    }

    public void Log(string msg)
    {
        try
        {
            using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);

            writer.WriteLine(msg);
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
