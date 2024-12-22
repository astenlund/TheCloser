Get-Process 'TheCloser.Daemon' -ErrorAction Ignore | Stop-Process -Verbose

dotnet publish --configuration 'Release'

$Destination = 'C:\Sync\Bin\TheCloser\'

if (!(Test-Path $Destination)) {
    New-Item $Destination -ItemType Directory -Force
}

Copy-Item '.\TheCloser\bin\Release\net9.0-windows\win-x64\publish\TheCloser.exe' $Destination -Force -Verbose
Copy-Item '.\TheCloser.Daemon\bin\Release\net9.0-windows\win-x64\publish\TheCloser.Daemon.exe' $Destination -Force -Verbose
