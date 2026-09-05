$ErrorActionPreference = 'Stop'

# Parse without executing: the installer requires elevation and changes scheduled tasks.
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$failures = @(foreach ($name in 'deploy.ps1', 'install-elevated-ahk.ps1', 'TheCloserTask.psm1') {
    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repositoryRoot $name), [ref]$tokens, [ref]$parseErrors)
    foreach ($parseError in $parseErrors) {
        '{0}:{1}: {2}' -f $name, $parseError.Extent.StartLineNumber, $parseError.Message
    }
})

if ($failures.Count -gt 0) {
    throw ($failures -join [Environment]::NewLine)
}

Write-Output 'All three deployment scripts parse successfully.'
