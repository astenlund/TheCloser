#Requires -RunAsAdministrator

# Registers a logon scheduled task that runs the TheCloser AutoHotkey script elevated.
# Rationale: UIPI silently drops low-level input hook events for unelevated processes while an
# elevated window is active, so an unelevated AutoHotkey never sees the trigger button when e.g.
# Task Manager has focus. An elevated AutoHotkey receives them, and everything it launches
# (TheCloser included) inherits the elevation, which also lets TheCloser close elevated windows.
# Run from an elevated shell, or let deploy.ps1 run it on a machine with no task registered yet.
# Every run registers, stops the running instance, sweeps stray AutoHotkey copies of the script,
# starts the task, and exits nonzero if either state poll expires; remove any old unelevated
# autostart by hand.
# The script defaults to the TheCloser.ahk sitting next to it (deploy.ps1 copies both to the
# deploy target), so no paths need passing when run from there.

[CmdletBinding()]
param(
    [string] $AhkScriptPath = (Join-Path $PSScriptRoot 'TheCloser.ahk'),
    [string] $AhkExePath = 'C:\Program Files\AutoHotkey\AutoHotkeyU64.exe',
    [string] $TaskName = 'TheCloser AutoHotkey (elevated)'
)

$ErrorActionPreference = 'Stop'

if ($null -eq ('TheCloser.NativeCommandLine' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace TheCloser
{
    public static class NativeCommandLine
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static string[] Parse(string commandLine)
        {
            if (String.IsNullOrWhiteSpace(commandLine))
            {
                return null;
            }

            IntPtr argumentsPointer = CommandLineToArgvW(commandLine, out int argumentCount);
            if (argumentsPointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (argumentCount < 1)
                {
                    return null;
                }

                string[] arguments = new string[argumentCount];
                for (int index = 0; index < argumentCount; index++)
                {
                    string argument = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argumentsPointer, index * IntPtr.Size));
                    if (argument == null)
                    {
                        return null;
                    }

                    arguments[index] = argument;
                }

                return arguments;
            }
            finally
            {
                LocalFree(argumentsPointer);
            }
        }
    }
}
'@
}

function Test-AutoHotkeyScriptCommandLine {
    param([AllowNull()][object] $CommandLine, [Parameter(Mandatory)][string] $ScriptLeaf)

    if ($CommandLine -isnot [string]) {
        return $false
    }

    $arguments = [TheCloser.NativeCommandLine]::Parse($CommandLine)
    if ($null -eq $arguments -or $arguments.Length -lt 2) {
        return $false
    }

    $scriptArgument = $null
    for ($index = 1; $index -lt $arguments.Length; $index++) {
        $argument = $arguments[$index]
        if ($argument.StartsWith('/', [StringComparison]::Ordinal)) {
            if ([string]::Equals($argument, '/iLib', [StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($argument, '/include', [StringComparison]::OrdinalIgnoreCase)) {
                $index++
                if ($index -ge $arguments.Length) {
                    return $false
                }
            }

            continue
        }

        $scriptArgument = $argument
        break
    }

    if ([string]::IsNullOrWhiteSpace($scriptArgument)) {
        return $false
    }

    try {
        $candidateLeaf = [IO.Path]::GetFileName($scriptArgument)
    }
    catch {
        # Unparseable paths must not select a process for termination.
        return $false
    }

    return [string]::Equals($candidateLeaf, $ScriptLeaf, [StringComparison]::OrdinalIgnoreCase)
}

if (!(Test-Path $AhkExePath)) {
    throw "AutoHotkey executable not found at '$AhkExePath'. Pass -AhkExePath if it is installed elsewhere."
}

if (!(Test-Path $AhkScriptPath)) {
    throw "AutoHotkey script not found at '$AhkScriptPath'. Pass -AhkScriptPath if it lives elsewhere."
}

$action = New-ScheduledTaskAction -Execute $AhkExePath -Argument "`"$AhkScriptPath`""
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Output "Registered scheduled task '$TaskName' (at logon, elevated, user $env:USERNAME)."

function Wait-TaskState {
    param([string] $Name, [bool] $Running, [int] $TimeoutSeconds)
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $state = (Get-ScheduledTask -TaskName $Name).State
        if (($state -eq 'Running') -eq $Running) {
            return $true
        }

        $remainingMilliseconds = ($TimeoutSeconds * 1000) - $stopwatch.Elapsed.TotalMilliseconds
        if ($remainingMilliseconds -le 0) {
            break
        }

        Start-Sleep -Milliseconds ([int][Math]::Min(250, [Math]::Ceiling($remainingMilliseconds)))
    }

    return $false
}

# Stop the task first: its logon instance would make the later start a silent no-op under the
# default IgnoreNew multiple-instances policy.
if ((Get-ScheduledTask -TaskName $TaskName).State -eq 'Running') {
    Stop-ScheduledTask -TaskName $TaskName
    if (-not (Wait-TaskState -Name $TaskName -Running $false -TimeoutSeconds 10)) {
        [Console]::Error.WriteLine("Task '$TaskName' did not leave the Running state within 10 seconds.")
        exit 1
    }
}

# Sweep every AutoHotkey instance running the script, task-hosted or not, matched by parsed script
# argument leaf: this covers instances launched outside the task or from a differently pathed copy,
# which #SingleInstance Force's same-path replacement would miss, and preserves the old
# replace-without-logoff behavior.
$scriptLeaf = Split-Path $AhkScriptPath -Leaf
Get-CimInstance Win32_Process -Filter "Name like 'AutoHotkey%'" |
    Where-Object { Test-AutoHotkeyScriptCommandLine -CommandLine $_.CommandLine -ScriptLeaf $scriptLeaf } |
    ForEach-Object {
        Write-Output "Stopping AutoHotkey PID $($_.ProcessId) running $scriptLeaf."
        Stop-Process -Id $_.ProcessId -Force
    }

Start-ScheduledTask -TaskName $TaskName
if (-not (Wait-TaskState -Name $TaskName -Running $true -TimeoutSeconds 5)) {
    [Console]::Error.WriteLine("Task '$TaskName' did not reach the Running state within 5 seconds of the start.")
    exit 1
}
Write-Output "Started task '$TaskName'."
