dotnet publish --configuration 'Release'

$Source = '.\bin\Release\net7.0-windows\win-x64\publish'
$Destination = 'C:\Sync\Bin\TheCloser\'

if (!(Test-Path $Destination)) {
    New-Item $Destination -ItemType Directory -Force
}

Copy-Item "$Source\*" $Destination -Recurse -Force -Verbose
