# Copy to deploy.settings.psd1 (git-ignored) and fill in the machine-local paths.
@{
    # Where deploy.ps1 copies the published executables and the invocation-layer files
    # (TheCloser.ahk, install-elevated-ahk.ps1). Typically a folder synced across machines.
    Destination = 'C:\Path\To\Bin\TheCloser'
}
