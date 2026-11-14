# Daily-driver install from this checkout: the same Velopack package a GitHub Release ships (D-043), installed with its
# Setup.exe, so a local build and a release share one folder (%LOCALAPPDATA%\CtrlAltDazeApp), one Apps entry and one
# uninstall. Start with Windows is the editor's Settings toggle (or `cad autostart on`).
# Example: pwsh scripts/install.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packages = Join-Path $root 'artifacts/install'
$exe = Join-Path $env:LOCALAPPDATA 'CtrlAltDazeApp/current/CtrlAltDaze.exe'

# Pack while the installed copy keeps running: a failed build changes nothing. A fresh folder each time, so vpk never
# sees an earlier local package of the same version.
if (Test-Path $packages) { Remove-Item $packages -Recurse -Force }
& (Join-Path $PSScriptRoot 'pack.ps1') -Output $packages
$setup = Get-ChildItem $packages -Filter '*-Setup.exe' | Select-Object -First 1
if (-not $setup) { throw "No Setup.exe in $packages." }

# Stop gracefully (tray icon removed, history written); Setup would force-kill the engine instead.
& (Join-Path $PSScriptRoot 'stop.ps1')

# Not Start-Process -Wait: in PowerShell 7 it also waits for the engine Setup starts, which never exits.
$process = Start-Process $setup.FullName -ArgumentList '--silent' -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Setup failed ($($process.ExitCode)); see $env:LOCALAPPDATA\velopack\velopack_CtrlAltDazeApp.log." }

if (-not (Get-Process CtrlAltDaze -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) | Out-Null
}
# The rename (T29.2, D-152): the new engine stops the old one, copies its folders, takes over Start with Windows and
# re-points the old desktop shortcuts. Only then is the old install removed: its uninstall deletes shortcuts that still
# start it. A shortcut of a flow that no longer exists is never re-pointed: it is listed and the old app stays.
$oldUpdate = Join-Path $env:LOCALAPPDATA 'DazeControlApp\Update.exe'
if (Test-Path $oldUpdate) {
    $shell = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $left = @(Get-ChildItem $desktop -Filter *.lnk | Where-Object { $shell.CreateShortcut($_.FullName).TargetPath -like '*\DazeControlApp\*' })
        if ($left.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    if ($left.Count) {
        Write-Warning "Old app kept: these desktop shortcuts still start it: $($left.Name -join ', ')"
    } else {
        $process = Start-Process $oldUpdate -ArgumentList 'uninstall', '--silent' -PassThru
        $process.WaitForExit()
        Write-Host "Removed the old Daze Control install (exit $($process.ExitCode))."
    }
}

$run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name CtrlAltDaze -ErrorAction SilentlyContinue).CtrlAltDaze
Write-Host "Installed and started: $exe"
Write-Host "Start with Windows: $(if ($run) { $run } else { 'off - turn it on in the editor''s Settings or with cad autostart on' })"
