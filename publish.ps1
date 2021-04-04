dotnet publish --configuration 'Release'

$Source = '.\bin\Release\net5.0\win-x64\publish'
$Destination = 'C:\Sync\Bin\TheCloser\'

if (!(Test-Path $Destination)) {
    New-Item $Destination -ItemType Directory -Force
}

Copy-Item "$Source\*" $Destination -Force -Verbose
