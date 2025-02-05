﻿using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;
using static TheCloser.Shared.Constants;

namespace TheCloser;

public static class Program
{
    private static readonly TimeSpan StartupIntervalThreshold = TimeSpan.FromMilliseconds(200);
    private static readonly Logger Logger = Logger.Create(AssemblyName);

    private static readonly IConfigurationRoot Config = new ConfigurationBuilder()
        .SetBasePath(Path.GetDirectoryName(Environment.ProcessPath)!)
        .AddJsonFile("appsettings.json", true)
        .Build();

    public static string AssemblyName => typeof(Program).Assembly.GetName().Name!;

    public static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "Global\\TheCloserGuardMutex", out var createdNew);

        if (!createdNew)
        {
            Logger.Log($"Timestamp: {DateTime.UtcNow:O}");
            Logger.Log("The previous instance is still running. Exiting...\r\n");
            return;
        }

        if (DateTime.UtcNow - TimestampHandler.ReadTimestamp() < StartupIntervalThreshold)
        {
            Logger.Log($"Timestamp: {DateTime.UtcNow:O}");
            Logger.Log($"The previous instance was started less than {StartupIntervalThreshold.TotalMilliseconds}ms ago. Exiting...\r\n");
            return;
        }

        StartDaemon();

        TimestampHandler.WriteTimestamp(DateTime.UtcNow);

        WindowCloser.Create(Config).CloseWindowUnderCursor();

        Logger.Log("");
    }

    private static void StartDaemon()
    {
        if (Process.GetProcessesByName(Daemon.Program.AssemblyName).Length != 0)
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
