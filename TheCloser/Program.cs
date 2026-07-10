using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;

using static TheCloser.Shared.Constants;

namespace TheCloser;

public static class Program
{
    private const int DaemonPinPollAttempts = 20;
    private const long StartupIntervalThresholdMs = 200;

    private static readonly TimeSpan DaemonPinPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly Logger Logger = new(AssemblyName);
    private static readonly string ExeDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;

    public static string AssemblyName => typeof(Program).Assembly.GetName().Name!;

    public static void Main()
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: true, GuardMutexName, out var createdNew);

            if (!createdNew)
            {
                Logger.Log($"Timestamp: {DateTime.UtcNow:O}");
                Logger.Log("The previous instance is still running. Exiting...");
                Logger.Log("");

                return;
            }

            using var sharedState = new SharedState(MemoryMappedFileName);

            // The pending pre-check keeps the common no-record startup silent; the guard mutex held
            // above means the daemon cannot repair concurrently, so the two reads cannot race.
            if (sharedState.TryReadTimeoutRepair(out _))
            {
                Logger.Log(TimeoutRepair.TryRestorePending(sharedState)
                    ? "Restored the foreground lock timeout after a detected crash."
                    : "Failed to restore the foreground lock timeout; keeping the repair record.");
            }

            var elapsedSinceLastRun = Environment.TickCount64 - sharedState.ReadThrottleTick();

            // Negative can only mean a stale-format (pre-tick-count) or foreign value; treat it as not throttled.
            if (elapsedSinceLastRun >= 0 && elapsedSinceLastRun < StartupIntervalThresholdMs)
            {
                Logger.Log($"Timestamp: {DateTime.UtcNow:O}");
                Logger.Log($"The previous instance was started less than {StartupIntervalThresholdMs}ms ago. Exiting...");
                Logger.Log("");

                return;
            }

            if (TryEnsureDaemonProcess())
            {
                WaitForDaemonPin();
            }

            sharedState.WriteThrottleTick(Environment.TickCount64);

            new WindowCloser(BuildConfiguration(), sharedState, Logger).CloseWindowUnderCursor();

            Logger.Log("");
        }
        catch (Exception ex)
        {
            Logger.Log(ex.ToString());
        }
    }

    private static IConfigurationRoot BuildConfiguration() => new ConfigurationBuilder()
        .SetBasePath(ExeDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .Build();

    private static bool TryEnsureDaemonProcess()
    {
        if (DaemonProcessExists())
        {
            return true;
        }

        var daemonExePath = Path.Combine(ExeDirectory, $"{DaemonAssemblyName}.exe");

        if (!File.Exists(daemonExePath))
        {
            Logger.Log("Could not find Daemon executable.");

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

    private static void WaitForDaemonPin()
    {
        // The app's own SharedState handle keeps the shared memory alive, so its existence proves nothing; the daemon publishes its mutex only after pinning.
        for (var attempt = 0; attempt < DaemonPinPollAttempts; attempt++)
        {
            if (Mutex.TryOpenExisting(DaemonMutexName, out var daemonMutex))
            {
                daemonMutex.Dispose();

                return;
            }

            Thread.Sleep(DaemonPinPollInterval);
        }

        Logger.Log("Timed out waiting for the daemon to pin the shared memory.");
    }
}
