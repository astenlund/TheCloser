using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GregsStack.InputSimulatorStandard;
using GregsStack.InputSimulatorStandard.Native;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace TheCloser
{
    public static class Program
    {
        private const string DefaultKillMethod = "CTRL-W";
        private const string ToolTip = "The Closer";

        private static readonly IConfigurationRoot Config = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName))
            .AddJsonFile("appsettings.json", true)
            .Build();

        private static readonly InputSimulator InputSimulator = new();

        private static readonly Dictionary<string, Action<IntPtr>> KillActions = new()
        {
            { "WM_DESTROY", handle => NativeMethods.PostMessage(handle, NativeMethods.WindowNotification.WM_DESTROY) },
            { "WM_CLOSE", handle => NativeMethods.PostMessage(handle, NativeMethods.WindowNotification.WM_CLOSE) },
            { "WM_QUIT", handle => NativeMethods.PostMessage(handle, NativeMethods.WindowNotification.WM_QUIT) },
            { "ESCAPE", handle => TrySendKeyPress(handle, VirtualKeyCode.ESCAPE) },
            { "ALT-F4", handle => TrySendKeyPress(handle, VirtualKeyCode.F4, VirtualKeyCode.LMENU) },
            { "CTRL-W", handle => TrySendKeyPress(handle, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL) },
            { "CTRL-F4", handle => TrySendKeyPress(handle, VirtualKeyCode.F4, VirtualKeyCode.CONTROL) }
        };

        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "TheCloser.txt");
        private static NotifyIcon _notifyIcon;

        [STAThread]
        public static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args).Build();

            InitializeContext();

            var arg = args.SingleOrDefault()?.ToLowerInvariant();
            if (arg == "-e" || arg == "--execute")
            {
                Execute();
            }

            await host.StartAsync();
        }

        private static void Execute()
        {
            var targetHandle = NativeMethods.WindowFromPoint(NativeMethods.GetMouseCursorPosition());
            var targetProcess = Process.GetProcessById(NativeMethods.GetProcessIdFromWindowHandle(targetHandle));
            var killMethod = GetKillMethod(targetProcess);
            var killAction = GetKillAction(killMethod);

            Log($"{targetProcess.ProcessName} -> {killMethod}");

            killAction?.Invoke(targetHandle);
        }

        private static Action<IntPtr>? GetKillAction(string killMethod) =>
            KillActions.TryGetValue(killMethod, out var killAction) ? killAction : null;

        private static string GetKillMethod(Process process) =>
            Config[process.ProcessName]?.ToUpperInvariant() ?? DefaultKillMethod;

        private static void InitializeContext()
        {
            _notifyIcon = new NotifyIcon(new Container())
            {
                ContextMenuStrip = new ContextMenuStrip(),
                Icon = Properties.Resources.TrayIcon,
                Text = ToolTip,
                Visible = true
            };

            // notifyIcon.ContextMenuStrip.Opening += ContextMenuStrip_Opening;
            // notifyIcon.DoubleClick += notifyIcon_DoubleClick;
        }

        private static void Log(string msg) => File.AppendAllText(LogPath, msg + Environment.NewLine);

        private static void TrySendKeyPress(IntPtr handle, VirtualKeyCode keyCode, VirtualKeyCode modifierKeyCode = default)
        {
            var (success, error) = TrySetForegroundWindow(handle);

            if (!success)
            {
                Log($"Window activation failed (error code: {error})");
                return;
            }

            if (modifierKeyCode != default)
            {
                InputSimulator.Keyboard.ModifiedKeyStroke(modifierKeyCode, keyCode);
            }
            else
            {
                InputSimulator.Keyboard.KeyPress(keyCode);
            }
        }

        private static (bool success, int error) TrySetForegroundWindow(IntPtr windowHandle)
        {
            var success = NativeMethods.GetForegroundWindow() == windowHandle ||
                          NativeMethods.SetForegroundWindow(windowHandle);

            var error = success
                ? 0
                : Marshal.GetLastWin32Error();

            return (success, error);
        }
    }
}
