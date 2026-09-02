using System.Diagnostics;
using TheCloser.Shared;

using static TheCloser.Shared.Constants;

namespace TheCloser.Daemon;

public static class Program
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly Logger Logger = new(DaemonAssemblyName);

    public static async Task Main(string[] args)
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
                    ProcessPriority.EnsureCurrentAtLeastNormal(Logger.Log);
                    Logger.Log(LowLevelHooksTimeoutProbe.Describe());
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
        finally
        {
            await Logger.DisposeAsync();
        }
    }

    private static void Run()
    {
        var daemonStart = Stopwatch.GetTimestamp();
        var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        var liveRoot = DaemonConfiguration.Build(exeDirectory, Logger.Log);
        try
        {
            var lastGood = new LastGoodConfiguration();
            ActivationHandler? handler = null;
            DaemonRuntime? runtime = null;

            runtime = new DaemonRuntime(
                Logger,
                MemoryMappedFileName,
                DaemonMutexName,
                ActivationEventName,
                DaemonExitEventName,
                onActivation: sharedState =>
                {
                    handler ??= new ActivationHandler(
                        sharedState,
                        Logger,
                        GuardMutexName,
                        settings: () => lastGood.Refresh(liveRoot, Logger.Log),
                        // The WindowCloser (and the ForegroundActivator it owns) is constructed
                        // inside this lambda, never hoisted to a field: PerformedInputAttach is
                        // OR-assigned and never reset, so it is sticky for a pipeline instance's
                        // life. A daemon-lifetime instance would therefore report an attach on every
                        // close forever after the first real one, dispatching the healer after every
                        // close for the daemon's whole life. Per-activation construction is a spec
                        // requirement, not an allocation the daemon can optimize away.
                        runClose: snapshot =>
                        {
                            var closer = new WindowCloser(snapshot, sharedState, Logger);
                            closer.CloseWindowUnderCursor();

                            return closer.PerformedInputAttach;
                        },
                        dispatchHealer: () => runtime!.DispatchHealer(() => new TriggerButtonHealer(Logger).HealStuckButtons()),
                        initialHandlerExit: daemonStart);
                    handler.HandleActivation();
                },
                watchdogTick: RepairIfCrashed,
                WatchdogInterval);

            runtime.Run();
        }
        finally
        {
            if (liveRoot is IDisposable disposableRoot)
            {
                disposableRoot.Dispose();
            }
        }
    }

    private static void RepairIfCrashed(SharedState sharedState)
    {
        if (CrashRepair.TryRepairCrashedState(sharedState, GuardMutexName, Logger))
        {
            Logger.Log("Restored the foreground lock timeout after a detected app crash.");
        }
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
