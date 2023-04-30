using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using static TheCloser.Shared.Constants;

namespace TheCloser;

public static class Program
{
    private static readonly TimeSpan StartupIntervalThreshold = TimeSpan.FromMilliseconds(125);
    private static readonly Logger Logger = Logger.Create(AssemblyName);

    private static readonly IConfigurationRoot Config = new ConfigurationBuilder()
        .SetBasePath(Path.GetDirectoryName(Environment.ProcessPath))
        .AddJsonFile("appsettings.json", true)
        .Build();

    public static string AssemblyName => typeof(Program).Assembly.GetName().Name!;

    public static void Main()
    {
        if (DateTime.UtcNow - TimestampHandler.ReadTimestamp() < StartupIntervalThreshold)
        {
            Logger.Log($"The previous instance was started less than {StartupIntervalThreshold.TotalMilliseconds}ms ago. Exiting...");
            return;
        }

        StartDaemon();

        TimestampHandler.WriteTimestamp(DateTime.UtcNow);

        WindowCloser.Create(Config).CloseWindowUnderCursor();

        Logger.Log("");
    }

    private static void StartDaemon()
    {
        if (Process.GetProcessesByName(Daemon.Program.AssemblyName).Any())
        {
            return;
        }

        var daemonExePath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, $"{Daemon.Program.AssemblyName}.exe");

        if (!File.Exists(daemonExePath))
        {
            Logger.Log("Could not find Daemon executable.");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = daemonExePath,
            Arguments = DaemonStartArgument,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process.Start(startInfo);
    }
}
