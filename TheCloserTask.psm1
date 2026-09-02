# Single source of truth for the elevated AutoHotkey logon task: the definition the installer
# registers, and a drift test the deploy script runs so any change to that definition triggers a
# re-registration instead of a plain restart. Copied to the deploy target next to the installer.
#
# Only the fields listed in Get-TheCloserTaskSpec are managed; anything else on the live task
# (description, history settings, hand-added triggers) is neither set nor compared.

Set-StrictMode -Version Latest

function Get-TheCloserTaskDefaultAhkExePath {
    return 'C:\Program Files\AutoHotkey\AutoHotkeyU64.exe'
}

function Get-TheCloserTaskDefaultName {
    return 'TheCloser AutoHotkey (elevated)'
}

# The managed fields, as the installer sets them and as the drift test reads them back.
function Get-TheCloserTaskSpec {
    param(
        [Parameter(Mandatory)][string] $AhkExePath,
        [Parameter(Mandatory)][string] $AhkScriptPath
    )

    return [ordered]@{
        Execute                  = $AhkExePath
        Arguments                = ('"{0}"' -f $AhkScriptPath)
        TriggerClass             = 'MSFT_TaskLogonTrigger'
        TriggerUserId            = $env:USERNAME
        PrincipalUserId          = "$env:USERDOMAIN\$env:USERNAME"
        LogonType                = 'Interactive'
        RunLevel                 = 'Highest'
        ExecutionTimeLimit       = 'PT0S'
        DisallowStartIfOnBatteries = $false
        StopIfGoingOnBatteries   = $false
    }
}

# Builds the registration arguments for Register-ScheduledTask from the spec.
function New-TheCloserTaskRegistration {
    param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary] $Spec)

    return @{
        Action    = New-ScheduledTaskAction -Execute $Spec.Execute -Argument $Spec.Arguments
        Trigger   = New-ScheduledTaskTrigger -AtLogOn -User $Spec.TriggerUserId
        Principal = New-ScheduledTaskPrincipal -UserId $Spec.PrincipalUserId -LogonType $Spec.LogonType -RunLevel $Spec.RunLevel
        Settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    }
}

# Reads the managed fields back from a live task in the spec's shape. Missing pieces read as $null
# so they show up as differences rather than throwing.
function Get-TheCloserTaskState {
    param([Parameter(Mandatory)][ciminstance] $Task)

    $action = @($Task.Actions)[0]
    $trigger = @($Task.Triggers)[0]
    $settings = $Task.Settings

    return [ordered]@{
        Execute                  = if ($action) { $action.Execute } else { $null }
        Arguments                = if ($action) { $action.Arguments } else { $null }
        TriggerClass             = if ($trigger) { $trigger.CimClass.CimClassName } else { $null }
        TriggerUserId            = if ($trigger -and $trigger.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger') { $trigger.UserId } else { $null }
        PrincipalUserId          = $Task.Principal.UserId
        LogonType                = [string] $Task.Principal.LogonType
        RunLevel                 = [string] $Task.Principal.RunLevel
        ExecutionTimeLimit       = $settings.ExecutionTimeLimit
        DisallowStartIfOnBatteries = $settings.DisallowStartIfOnBatteries
        StopIfGoingOnBatteries   = $settings.StopIfGoingOnBatteries
    }
}

# Task Scheduler reads registered values back in its own forms: paths keep whichever separator
# they were registered with, and account names come back domain-qualified on the trigger but
# short on the principal regardless of the form given. Normalize both sides to the same shape.
function ConvertTo-ComparableTaskValue {
    param([Parameter(Mandatory)][string] $Field, [AllowNull()][string] $Value)

    if ($null -eq $Value) {
        return ''
    }

    switch ($Field) {
        { $_ -in 'Execute', 'Arguments' } { return $Value.Replace('/', '\').Trim() }
        { $_ -in 'TriggerUserId', 'PrincipalUserId' } { return $Value.Substring($Value.LastIndexOf('\') + 1) }
        default { return $Value }
    }
}

# Returns one line per managed field whose live value differs from the spec; an empty result means
# the task is current. Paths and identities compare case-insensitively; everything else ordinally.
function Compare-TheCloserTaskSpec {
    param(
        [Parameter(Mandatory)][ciminstance] $Task,
        [Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary] $Spec
    )

    $state = Get-TheCloserTaskState -Task $Task
    $caseInsensitive = @('Execute', 'Arguments', 'TriggerUserId', 'PrincipalUserId')
    $differences = foreach ($field in $Spec.Keys) {
        $expected = ConvertTo-ComparableTaskValue -Field $field -Value ([string] $Spec[$field])
        $actual = ConvertTo-ComparableTaskValue -Field $field -Value ([string] $state[$field])
        $comparison = if ($caseInsensitive -contains $field) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not [string]::Equals($expected, $actual, $comparison)) {
            "{0}: expected '{1}', found '{2}'" -f $field, $expected, $actual
        }
    }

    return @($differences)
}

Export-ModuleMember -Function Get-TheCloserTaskDefaultAhkExePath, Get-TheCloserTaskDefaultName, Get-TheCloserTaskSpec, New-TheCloserTaskRegistration, Get-TheCloserTaskState, Compare-TheCloserTaskSpec
