using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;

using static TheCloser.Shared.Constants;

namespace TheCloser;

public static class Program
{
    private const int DaemonPinPollAttempts = 20;
    private const int DaemonPinPollIntervalMs = 50;
    private const long StartupIntervalThresholdMs = 200;

    public static string AssemblyName => typeof(Program).Assembly.GetName().Name!;

    public static async Task Main(string[] args)
    {
        Logger? logger = null;

        try
        {
            logger = new Logger(AssemblyName);

            var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;

            using var mutex = new Mutex(initiallyOwned: true, GuardMutexName, out var createdNew);

            if (!createdNew)
            {
                LogEarlyExit(logger, "The previous instance is still running. Exiting...");

                return;
            }

            using var sharedState = new SharedState(MemoryMappedFileName);

            // The pending pre-check keeps the common no-record startup silent; the guard mutex held
            // above means the daemon cannot repair concurrently, so the two reads cannot race.
            var repairPending = sharedState.TryReadTimeoutRepair(out _);

            if (repairPending)
            {
                var restored = TimeoutRepair.TryRestorePending(sharedState);
                logger.Log(restored
                    ? "Restored the foreground lock timeout after a detected crash."
                    : "Failed to restore the foreground lock timeout; keeping the repair record.");
            }

            var elapsedSinceLastRun = Environment.TickCount64 - sharedState.ReadThrottleTick();

            // Negative can only mean a stale-format (pre-tick-count) or foreign value; treat it as not throttled.
            if (elapsedSinceLastRun is >= 0 and < StartupIntervalThresholdMs)
            {
                LogEarlyExit(logger, $"The previous instance was started less than {StartupIntervalThresholdMs}ms ago. Exiting...");

                return;
            }

            if (TryEnsureDaemonProcess(exeDirectory, logger))
            {
                WaitForDaemonPin(logger);
            }

            sharedState.WriteThrottleTick(Environment.TickCount64);

            var configuration = BuildConfiguration(exeDirectory);

            var windowCloser = new WindowCloser(configuration, sharedState, logger);
            windowCloser.CloseWindowUnderCursor();

            if (windowCloser.PerformedInputAttach)
            {
                // Release the single-instance guard before lingering: the monitor may hold the
                // process alive for up to 2s, and follow-up invocations must not be rejected as
                // "previous instance still running" meanwhile. Any pending repair record at this
                // point means a failed restore that the daemon watchdog should pick up anyway.
                mutex.ReleaseMutex();
                new TriggerButtonHealer(logger).HealStuckButtons();
            }

            logger.Log("");
        }
        catch (Exception ex)
        {
            logger?.Log(ex.ToString());
        }
        finally
        {
            if (logger is not null)
            {
                await logger.DisposeAsync();
            }
        }
    }

    private static void LogEarlyExit(Logger logger, string reason)
    {
        logger.Log(reason);
        logger.Log("");
    }

    private static IConfigurationRoot BuildConfiguration(string exeDirectory) => new ConfigurationBuilder()
        .SetBasePath(exeDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();

    private static bool TryEnsureDaemonProcess(string exeDirectory, Logger logger)
    {
        var daemonProcessExists = DaemonProcessExists();

        if (daemonProcessExists)
        {
            return true;
        }

        var daemonExePath = Path.Combine(exeDirectory, $"{DaemonAssemblyName}.exe");

        if (!File.Exists(daemonExePath))
        {
            logger.Log("Could not find Daemon executable.");

            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = daemonExePath,
            Arguments = DaemonStartArgument,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var daemonProcess = Process.Start(startInfo);

        return true;
    }

    private static bool DaemonProcessExists()
    {
        var daemonProcesses = Process.GetProcessesByName(DaemonAssemblyName);

        foreach (var daemonProcess in daemonProcesses)
        {
            daemonProcess.Dispose();
        }

        return daemonProcesses.Length != 0;
    }

    private static void WaitForDaemonPin(Logger logger)
    {
        // The app's own SharedState handle keeps the shared memory alive, so its existence proves nothing; the daemon publishes its mutex only after pinning.
        for (var attempt = 0; attempt < DaemonPinPollAttempts; attempt++)
        {
            if (Mutex.TryOpenExisting(DaemonMutexName, out var daemonMutex))
            {
                daemonMutex.Dispose();

                return;
            }

            Thread.Sleep(DaemonPinPollIntervalMs);
        }

        logger.Log("Timed out waiting for the daemon to pin the shared memory.");
    }
}
