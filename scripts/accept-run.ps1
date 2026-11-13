# The acceptance kit (docs/ACCEPTANCE.md): runs this checkout on the kit config, never on your real config.
#   pwsh scripts/accept-run.ps1             back up the dev config, copy samples/acceptance/config.yaml over it,
#                                           start the dev engine + editor on it (scripts/dev-run.ps1)
#   pwsh scripts/accept-run.ps1 -Restore    stop the dev engine/editor, put your previous dev config back and
#                                           start the installed app again (dev-run.ps1 -Restore)
#   -NoEditor / -KeepEdits                  engine only / don't overwrite the dev config with a fresh kit (keep what
#                                           you edited during the sitting)
# Only %LOCALAPPDATA%\DazeControl\dev\config.yaml is touched (backup: config.yaml.accept-backup next to it). A second
# run keeps the first backup, so your original dev config survives repeated runs. The installed config is never read or written.
param(
    [switch]$Restore,
    [switch]$NoEditor,
    [switch]$KeepEdits
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$devFolder = Join-Path $env:LOCALAPPDATA 'DazeControl\dev'
$config = Join-Path $devFolder 'config.yaml'
$backup = Join-Path $devFolder 'config.yaml.accept-backup'
$noPrevious = Join-Path $devFolder 'config.yaml.accept-none'   # marker: there was no dev config before the kit
$kit = Join-Path $repo 'samples\acceptance\config.yaml'
$devRun = Join-Path $PSScriptRoot 'dev-run.ps1'

if ($Restore) {
    & $devRun -Restore            # closes the editor first so it flushes, then starts the installed app
    if (Test-Path -LiteralPath $backup) {
        Copy-Item -LiteralPath $backup -Destination $config -Force
        Remove-Item -LiteralPath $backup -Force
        Write-Host 'accept-run: your previous dev config is back' -ForegroundColor Green
    } elseif (Test-Path -LiteralPath $noPrevious) {
        Remove-Item -LiteralPath $config -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $noPrevious -Force
        Write-Host 'accept-run: the kit config is removed (there was no dev config before)' -ForegroundColor Green
    } else {
        Write-Host 'accept-run: nothing to restore (no kit run found)' -ForegroundColor Yellow
    }
    return
}

if (-not (Test-Path -LiteralPath $kit)) { throw "accept-run: kit not found at $kit" }
New-Item -ItemType Directory -Force $devFolder | Out-Null
$already = (Test-Path -LiteralPath $backup) -or (Test-Path -LiteralPath $noPrevious)
if (-not $already) {
    if (Test-Path -LiteralPath $config) {
        Copy-Item -LiteralPath $config -Destination $backup
        Write-Host "accept-run: dev config backed up to $backup"
    } else {
        Set-Content -LiteralPath $noPrevious -Value 'no dev config before the acceptance kit'
    }
}
if (-not $KeepEdits -or -not (Test-Path -LiteralPath $config)) {
    Copy-Item -LiteralPath $kit -Destination $config -Force
    Write-Host 'accept-run: kit config copied to the dev config'
}
$devArgs = @()
if ($NoEditor) { $devArgs += '-NoEditor' }
& $devRun @devArgs
Write-Host 'accept-run: when you are done: pwsh scripts/accept-run.ps1 -Restore' -ForegroundColor Cyan
