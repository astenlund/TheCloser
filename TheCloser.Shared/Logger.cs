namespace TheCloser.Shared;

public class Logger
{
    private readonly string _logPath;

    private Logger(string appName)
    {
        _logPath = Path.Combine(Path.GetTempPath(), appName + ".log");
    }

    public static Logger Create(string appName) => new(appName);

    public void Log(string msg)
    {
        File.AppendAllText(_logPath, msg + Environment.NewLine);
    }
}
