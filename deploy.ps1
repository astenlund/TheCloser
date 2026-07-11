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

Get-Process 'TheCloser.Daemon' -ErrorAction Ignore | Stop-Process -Verbose

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

# The invocation layer ships alongside the binaries: the AHK trigger script and the per-machine
# elevated-task installer travel to other machines through the synced Bin folder.
Copy-Item (Join-Path $PSScriptRoot 'TheCloser.ahk') $Destination -Force -Verbose
Copy-Item (Join-Path $PSScriptRoot 'install-elevated-ahk.ps1') $Destination -Force -Verbose
