using TheCloser.Shared;
using static TheCloser.Shared.Constants;

namespace TheCloser.Daemon;

public class Program
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly Logger Logger = Logger.Create("TheCloser.Daemon");

    public static string AssemblyName => typeof(Program).Assembly.GetName().Name!;

    public static void Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return;
            }

            switch (args[0])
            {
                case DaemonStartArgument:
                    Logger.Log("Daemon starting...");
                    Run();
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
        catch (Exception ex)
        {
            Logger.Log(ex.ToString());
        }
    }

    private static void Run()
    {
        // The shared memory must be pinned before the mutex is published: the app treats the mutex's existence as proof that the pin is in place.
        using var sharedState = new SharedState(MemoryMappedFileName);
        using var mutex = new Mutex(true, DaemonMutexName, out var createdNew);

        if (!createdNew)
        {
            Logger.Log("Daemon is already running. Exiting...");

            return;
        }

        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, DaemonExitEventName);

        while (!exitEvent.WaitOne(WatchdogInterval))
        {
            try
            {
                RepairIfCrashed(sharedState);
            }
            catch (Exception ex)
            {
                Logger.Log(ex.ToString());
            }
        }

        Logger.Log("Daemon STOP signal received. Exiting...");
    }

    private static void RepairIfCrashed(SharedState sharedState)
    {
        if (!sharedState.TryReadTimeoutRepair(out var savedTimeout))
        {
            return;
        }

        if (Mutex.TryOpenExisting(GuardMutexName, out var guardMutex))
        {
            guardMutex.Dispose();

            return;
        }

        TimeoutRepair.RestoreAndClear(sharedState, savedTimeout);
    }

    private static void SignalExit()
    {
        if (EventWaitHandle.TryOpenExisting(DaemonExitEventName, out var exitEvent))
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
