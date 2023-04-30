using System.IO.MemoryMappedFiles;
using TheCloser.Shared;
using static TheCloser.Shared.Constants;

namespace TheCloser.Daemon;

public class Program
{
    private const string ExitEventName = "TheCloserDaemonExitEvent";
    private const string MutexName = "TheCloserDaemonMutex";

    private static readonly Logger Logger = Logger.Create("TheCloser.Daemon");

    public static string AssemblyName => typeof(Program).Assembly.GetName().Name!;

    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            return;
        }

        switch (args[0])
        {
            case DaemonStartArgument:
                Logger.Log("Daemon starting...");
                EnsureInstance();
                break;
            case DaemonStopArgument:
                Logger.Log("Daemon stopping...");
                SignalExit();
                break;
            default:
                Logger.Log($"Daemon could not be started. Unknown argument: '{args[0]}'");
                break;
        }
    }

    private static void EnsureInstance()
    {
        if (Mutex.TryOpenExisting(MutexName, out var existingMutex))
        {
            Logger.Log("Daemon is already running. Exiting...");
            existingMutex.Dispose();
        }
        else
        {
            Run();
        }
    }

    private static void Run()
    {
        using var mmf = MemoryMappedFile.CreateOrOpen(MemoryMappedFileName, MemoryMappedFileSize);
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        using var mutex = new Mutex(true, MutexName);

        exitEvent.WaitOne();

        Logger.Log("Daemon STOP signal received. Exiting...");
    }

    private static void SignalExit()
    {
        if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent))
        {
            exitEvent.Set();
            exitEvent.Dispose();
        }
        else
        {
            Logger.Log("Daemon is not running. Exiting...");
        }
    }
}
