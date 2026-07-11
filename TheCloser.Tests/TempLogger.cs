using TheCloser.Shared;

namespace TheCloser.Tests;

// Wraps a GUID-named Logger and deletes its files on dispose. Test classes that write through
// real Logger instances hold one of these (xUnit disposes it even when the test fails), so runs
// leave no stray TheCloser.Tests.<guid>.log files in %TEMP%.
internal sealed class TempLogger : IDisposable
{
    public TempLogger()
    {
        var name = TestNames.UniqueLoggerName();
        Logger = new Logger(name);
        LogPath = Logger.GetLogPath(name);
    }

    public Logger Logger { get; }

    public string LogPath { get; }

    public void Dispose()
    {
        File.Delete(LogPath);
        File.Delete(LogPath + ".old");
    }
}
