$ErrorActionPreference = 'Stop'

Get-Process 'TheCloser.Daemon' -ErrorAction Ignore | Stop-Process -Verbose

dotnet publish $PSScriptRoot --configuration 'Release'

if ($LASTEXITCODE -ne 0) {
    # Not Write-Error: under ErrorActionPreference Stop it would terminate here and exit 1 instead of propagating the code.
    [Console]::Error.WriteLine("dotnet publish failed with exit code $LASTEXITCODE. Nothing was deployed.")
    exit $LASTEXITCODE
}

$Destination = 'C:\Sync\Personal\3. Resources\Bin\TheCloser\'

if (!(Test-Path $Destination)) {
    New-Item $Destination -ItemType Directory -Force | Out-Null
}

Copy-Item (Join-Path $PSScriptRoot 'TheCloser\bin\Release\net10.0-windows\win-x64\publish\TheCloser.exe') $Destination -Force -Verbose
Copy-Item (Join-Path $PSScriptRoot 'TheCloser.Daemon\bin\Release\net10.0-windows\win-x64\publish\TheCloser.Daemon.exe') $Destination -Force -Verbose
