param(
    [Parameter(Mandatory)]
    [string] $MutexName,

    [Parameter(Mandatory)]
    [string] $LogPath,

    [Parameter(Mandatory)]
    [string] $ReadyPath,

    [Parameter(Mandatory)]
    [string] $ReleasePath
)

$ErrorActionPreference = 'Stop'
$mutex = [Threading.Mutex]::new($false, $MutexName)
$mutexAcquired = $false
$stream = $null

try {
    try {
        $mutexAcquired = $mutex.WaitOne()
    }
    catch [Threading.AbandonedMutexException] {
        $mutexAcquired = $true
    }

    $stream = [IO.FileStream]::new($LogPath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    [IO.File]::WriteAllText($ReadyPath, '')

    while (![IO.File]::Exists($ReleasePath)) {
        Start-Sleep -Milliseconds 10
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes('child' + [Environment]::NewLine)
    $stream.Write($bytes)
    $stream.Flush()
}
finally {
    if ($null -ne $stream) {
        $stream.Dispose()
    }

    if ($mutexAcquired) {
        $mutex.ReleaseMutex()
    }

    $mutex.Dispose()
}
