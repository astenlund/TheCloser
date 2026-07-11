#Requires -RunAsAdministrator

# Registers a logon scheduled task that runs the TheCloser AutoHotkey script elevated.
# Rationale: UIPI silently drops low-level input hook events for unelevated processes while an
# elevated window is active, so an unelevated AutoHotkey never sees the trigger button when e.g.
# Task Manager has focus. An elevated AutoHotkey receives them, and everything it launches
# (TheCloser included) inherits the elevation, which also lets TheCloser close elevated windows.
# Run once per machine from an elevated shell; remove any old unelevated autostart by hand.
# The script defaults to the TheCloser.ahk sitting next to it (deploy.ps1 copies both to the
# deploy target), so no paths need passing when run from there.

[CmdletBinding()]
param(
    [string] $AhkScriptPath = (Join-Path $PSScriptRoot 'TheCloser.ahk'),
    [string] $AhkExePath = 'C:\Program Files\AutoHotkey\AutoHotkeyU64.exe',
    [string] $TaskName = 'TheCloser AutoHotkey (elevated)',
    [switch] $StartNow
)

$ErrorActionPreference = 'Stop'

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

if ($StartNow) {
    # Replace any currently running instance of this script (typically the old unelevated autostart)
    # so the elevated one takes over without a logoff.
    $scriptLeaf = Split-Path $AhkScriptPath -Leaf
    Get-CimInstance Win32_Process -Filter "Name like 'AutoHotkey%'" |
        Where-Object { $_.CommandLine -like "*$scriptLeaf*" } |
        ForEach-Object {
            Write-Output "Stopping AutoHotkey PID $($_.ProcessId) running $scriptLeaf."
            Stop-Process -Id $_.ProcessId -Force
        }

    Start-ScheduledTask -TaskName $TaskName
    Write-Output "Started task '$TaskName'."
}
