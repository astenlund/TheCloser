using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using GregsStack.InputSimulatorStandard;
using GregsStack.InputSimulatorStandard.Native;
using Microsoft.Extensions.Configuration;

namespace TheCloser;

public static class Program
{
    private const string DefaultKillMethod = "CTRL-W";
    private const string KillMessage = "KILL";
    private const string PipeName = "TheCloserNamedPipe";

    private static readonly IConfigurationRoot Config = new ConfigurationBuilder()
        .SetBasePath(Path.GetDirectoryName(Environment.ProcessPath))
        .AddJsonFile("appsettings.json", true)
        .Build();

    private static readonly InputSimulator InputSimulator = new();

    private static readonly Dictionary<string, Action<IntPtr>> KillActions = new()
    {
        { "ALT-F4", handle => TrySendKeyPress(handle, VirtualKeyCode.F4, VirtualKeyCode.LMENU) },
        { "CTRL-F4", handle => TrySendKeyPress(handle, VirtualKeyCode.F4, VirtualKeyCode.CONTROL) },
        { "CTRL-SHIFT-W", handle => TrySendKeyPress(handle, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL, VirtualKeyCode.SHIFT) },
        { "CTRL-W", handle => TrySendKeyPress(handle, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL) },
        { "ESCAPE", handle => TrySendKeyPress(handle, VirtualKeyCode.ESCAPE) },
        { "WM_CLOSE", handle => NativeMethods.PostMessage(handle, NativeMethods.WindowNotification.WM_CLOSE) },
        { "WM_DESTROY", handle => NativeMethods.PostMessage(handle, NativeMethods.WindowNotification.WM_DESTROY) },
        { "WM_QUIT", handle => NativeMethods.PostMessage(handle, NativeMethods.WindowNotification.WM_QUIT) }
    };

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "TheCloser.txt");

    [STAThread]
    public static async Task Main(string[] args)
    {
        using var guard = SingleInstanceGuard.Create();

        if (guard == null)
        {
            if (args.FirstOrDefault() is "--execute" or "-e")
            {
                await SendKillMessage();
            }

            return;
        }

        using var trayIcon = new NotifyIcon();

        trayIcon.Icon = new Icon("TheCloser.ico");
        trayIcon.Visible = true;
        trayIcon.Text = "The Closer";

        var contextMenu = new ContextMenuStrip();
        var exitMenuItem = new ToolStripMenuItem("Exit", null, (_, _) => Application.Exit());

        contextMenu.Items.Add(exitMenuItem);
        trayIcon.ContextMenuStrip = contextMenu;

        var cts = new CancellationTokenSource();
        _ = Task.Run(() => StartNamedPipeServerAsync(cts.Token), cts.Token);

        Application.ApplicationExit += (_, _) => cts.Cancel();
        Application.Run();
    }

    private static async Task StartNamedPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipeSecurity = new PipeSecurity();

            pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

            await using var serverStream = NamedPipeServerFactory.Create(PipeName, pipeSecurity);
            await serverStream.WaitForConnectionAsync(ct).ConfigureAwait(false);

            try
            {
                using var reader = new StreamReader(serverStream);

                while (await reader.ReadLineAsync(ct) is { } message)
                {
                    if (message == KillMessage)
                    {
                        Kill();
                    }
                }
            }
            catch (IOException)
            {
                // Ignore IOException (error code 232) caused by client closing the connection
            }
        }
    }

    private static async Task SendKillMessage()
    {
        await using var clientStream = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await clientStream.ConnectAsync();
        await using var writer = new StreamWriter(clientStream);
        await writer.WriteLineAsync(KillMessage);
        await writer.FlushAsync();
    }

    private static void Kill()
    {
        var targetHandle = NativeMethods.WindowFromPoint(NativeMethods.GetMouseCursorPosition());
        var targetProcess = Process.GetProcessById(NativeMethods.GetProcessIdFromWindowHandle(targetHandle));
        var killMethod = GetKillMethod(targetProcess);
        var killAction = GetKillAction(killMethod);

        Log($"{targetProcess.ProcessName} -> {killMethod}");

        killAction?.Invoke(targetHandle);
    }

    private static Action<IntPtr>? GetKillAction(string killMethod) => KillActions.TryGetValue(killMethod, out var killAction) ? killAction : null;

    private static string GetKillMethod(Process process) => Config[process.ProcessName]?.ToUpperInvariant() ?? DefaultKillMethod;

    private static void Log(string msg) => File.AppendAllText(LogPath, msg + Environment.NewLine);

    private static void TrySendKeyPress(IntPtr handle, VirtualKeyCode keyCode, params VirtualKeyCode[] modifierKeyCodes)
    {
        if (TrySetForegroundWindow(handle))
        {
            if (modifierKeyCodes.Any())
            {
                InputSimulator.Keyboard.ModifiedKeyStroke(modifierKeyCodes, keyCode);
            }
            else
            {
                InputSimulator.Keyboard.KeyPress(keyCode);
            }
        }
    }

    private static bool TrySetForegroundWindow(IntPtr windowHandle)
    {
        var success = NativeMethods.GetForegroundWindow() == windowHandle ||
                      NativeMethods.SetForegroundWindow(NativeMethods.GetRootWindow(windowHandle));

        if (!success)
        {
            Log($"Window activation failed (error code: {Marshal.GetLastWin32Error()})");
        }

        return success;
    }
}
