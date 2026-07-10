using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using static TheCloser.Shared.Constants;

namespace TheCloser;

public static class Program
{
    private const int DaemonPinPollAttempts = 20;

    private static readonly TimeSpan StartupIntervalThreshold = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DaemonPinPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly Logger Logger = Logger.Create(AssemblyName);

    private static readonly IConfigurationRoot Config = new ConfigurationBuilder()
        .SetBasePath(Path.GetDirectoryName(Environment.ProcessPath)!)
        .AddJsonFile("appsettings.json", true)
        .Build();

    public static string AssemblyName => typeof(Program).Assembly.GetName().Name!;

    public static void Main()
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: true, GuardMutexName, out var createdNew);

            if (!createdNew)
            {
                Logger.Log($"Timestamp: {DateTime.UtcNow:O}");
                Logger.Log("The previous instance is still running. Exiting...\r\n");

                return;
            }

            using var sharedState = new SharedState(MemoryMappedFileName);

            TimeoutRepair.TryRestorePending(sharedState);

            if (DateTime.UtcNow - sharedState.ReadTimestamp() < StartupIntervalThreshold)
            {
                Logger.Log($"Timestamp: {DateTime.UtcNow:O}");
                Logger.Log($"The previous instance was started less than {StartupIntervalThreshold.TotalMilliseconds}ms ago. Exiting...\r\n");

                return;
            }

            if (StartDaemon())
            {
                WaitForDaemonPin();
            }

            sharedState.WriteTimestamp(DateTime.UtcNow);

            WindowCloser.Create(Config, sharedState).CloseWindowUnderCursor();

            Logger.Log("");
        }
        catch (Exception ex)
        {
            Logger.Log(ex.ToString());
        }
    }

    private static bool StartDaemon()
    {
        if (Process.GetProcessesByName(Daemon.Program.AssemblyName).Length != 0)
        {
            return false;
        }

        var daemonExePath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, $"{Daemon.Program.AssemblyName}.exe");

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

        Process.Start(startInfo);

        return true;
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
