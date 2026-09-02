$ErrorActionPreference = 'Stop'

# Machine-local paths live in deploy.settings.psd1 (git-ignored); the example file documents the shape.
$SettingsPath = Join-Path $PSScriptRoot 'deploy.settings.psd1'

if (!(Test-Path $SettingsPath)) {
    [Console]::Error.WriteLine("Missing '$SettingsPath'. Copy deploy.settings.example.psd1 to deploy.settings.psd1 and fill in your paths.")
    exit 1
}

$Settings = Import-PowerShellDataFile $SettingsPath

# Native AOT's ilcompiler locates the VC++ toolchain via vswhere.exe, which is not on PATH in shells without the VS developer environment.
$VsInstallerDir = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if ($env:PATH -notlike "*$VsInstallerDir*") {
    $env:PATH = "$VsInstallerDir;$env:PATH"
}

# The daemon inherits elevation from the AHK invocation layer, so an unelevated shell cannot
# stop it directly; retry the stop once through an elevated child before giving up.
$Daemon = Get-Process 'TheCloser.Daemon' -ErrorAction Ignore
if ($Daemon) {
    try {
        $Daemon | Stop-Process -Verbose -ErrorAction Stop
    }
    catch {
        Start-Process pwsh -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', "Get-Process 'TheCloser.Daemon' -ErrorAction Ignore | Stop-Process"

        if (Get-Process 'TheCloser.Daemon' -ErrorAction Ignore) {
            [Console]::Error.WriteLine('Could not stop TheCloser.Daemon even via the elevated retry. Nothing was deployed.')
            exit 1
        }
    }
}

dotnet publish $PSScriptRoot --configuration 'Release'

if ($LASTEXITCODE -ne 0) {
    # Not Write-Error: under ErrorActionPreference Stop it would terminate here and exit 1 instead of propagating the code.
    [Console]::Error.WriteLine("dotnet publish failed with exit code $LASTEXITCODE. Nothing was deployed.")
    exit $LASTEXITCODE
}

$Destination = $Settings.Destination

if (!(Test-Path $Destination)) {
    New-Item $Destination -ItemType Directory -Force | Out-Null
}

# The TFM lives in Directory.Build.props; deriving it here keeps a future TFM bump from silently copying stale binaries.
$Tfm = ([xml](Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props'))).Project.PropertyGroup.TargetFramework

Copy-Item (Join-Path $PSScriptRoot "TheCloser\bin\Release\$Tfm\win-x64\publish\TheCloser.exe") $Destination -Force -Verbose
Copy-Item (Join-Path $PSScriptRoot "TheCloser.Daemon\bin\Release\$Tfm\win-x64\publish\TheCloser.Daemon.exe") $Destination -Force -Verbose

# The invocation layer ships alongside the binaries: the AHK trigger, the per-machine elevated-task
# installer, and the task-definition module both scripts share travel to other machines through
# the synced Bin folder.
Copy-Item (Join-Path $PSScriptRoot 'TheCloser.ahk') $Destination -Force -Verbose
Copy-Item (Join-Path $PSScriptRoot 'install-elevated-ahk.ps1') $Destination -Force -Verbose
Copy-Item (Join-Path $PSScriptRoot 'TheCloserTask.psm1') $Destination -Force -Verbose

# Restart the elevated AutoHotkey task so the just-copied script (and, via its auto-execute,
# the just-copied daemon) take over without a manual step, or re-register it through the
# installer when it is missing or any managed field of its definition (paths, trigger, principal,
# run limits) differs from what the installer would register. Task-state polls sidestep
# process inspection: an unelevated shell cannot read an elevated process's command line. Unlike
# the installer, the restart branch deliberately runs no stray-instance sweep: on a machine already
# running the deployed chain the only instance to manage is the task-hosted one the stop-poll just
# ended. This sequence is not an atomic task reservation: an external start after the stop poll can
# make IgnoreNew discard this start, while the post-start Running state cannot distinguish that
# race. The personal single-user deployment workflow accepts that residual external-concurrency
# boundary.
Import-Module (Join-Path $Destination 'TheCloserTask.psm1') -Force
$TaskName = Get-TheCloserTaskDefaultName

function Get-TheCloserTask {
    param([string] $Name)
    $tasks = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object {
            [string]::Equals($_.TaskName, $Name, [StringComparison]::OrdinalIgnoreCase)
        })
    if ($tasks.Count -gt 1) {
        throw "Expected one root task named '$Name', but found $($tasks.Count)."
    }

    if ($tasks.Count -eq 0) {
        return $null
    }

    return $tasks[0]
}

function Wait-TaskState {
    param([string] $Name, [bool] $Running, [int] $TimeoutSeconds)
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $task = Get-TheCloserTask -Name $Name
        if ($null -eq $task) {
            throw "Task '$Name' disappeared while waiting for its state."
        }

        $state = $task.State
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

$Task = Get-TheCloserTask -Name $TaskName
$Drift = @()

if ($Task) {
    $Spec = Get-TheCloserTaskSpec -AhkExePath (Get-TheCloserTaskDefaultAhkExePath) -AhkScriptPath (Join-Path $Destination 'TheCloser.ahk')
    $Drift = @(Compare-TheCloserTaskSpec -Task $Task -Spec $Spec)
    if ($Drift.Count -gt 0) {
        Write-Output "Task '$TaskName' differs from the installer's definition; re-registering. $($Drift -join '; ')"
    }
}

if ($Task -and $Drift.Count -eq 0) {
    Stop-ScheduledTask -TaskName $TaskName
    if (-not (Wait-TaskState -Name $TaskName -Running $false -TimeoutSeconds 10)) {
        [Console]::Error.WriteLine("Task '$TaskName' did not leave Running within 10 seconds. Nothing restarted.")
        exit 1
    }

    Start-ScheduledTask -TaskName $TaskName
    if (-not (Wait-TaskState -Name $TaskName -Running $true -TimeoutSeconds 5)) {
        [Console]::Error.WriteLine("Task '$TaskName' did not return to Running within 5 seconds of the start.")
        exit 1
    }

    Write-Output "Restarted task '$TaskName'."
}
else {
    # First deploy on this machine, or a drifted definition: register through the deployed
    # installer copy so the task binds to the deploy target's script, not this working tree. One
    # UAC prompt per registration. ArgumentList entries are joined with spaces and never quoted,
    # and the configured Destination contains a space, so the -File path is quoted explicitly.
    $Installer = Start-Process pwsh -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-File', ('"{0}"' -f (Join-Path $Destination 'install-elevated-ahk.ps1'))
    if ($Installer.ExitCode -ne 0) {
        [Console]::Error.WriteLine("Installer exited with code $($Installer.ExitCode). Nothing verified.")
        exit $Installer.ExitCode
    }

    $Task = Get-TheCloserTask -Name $TaskName
    if ($null -eq $Task -or -not (Wait-TaskState -Name $TaskName -Running $true -TimeoutSeconds 5)) {
        [Console]::Error.WriteLine("Install did not leave task '$TaskName' running. Nothing verified.")
        exit 1
    }

    Write-Output "Installed and started task '$TaskName'."
}
