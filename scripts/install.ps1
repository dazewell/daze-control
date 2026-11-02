# Daily-driver install from this checkout: the same Velopack package a GitHub Release ships (D-043), installed with its
# Setup.exe, so a local build and a release share one folder (%LOCALAPPDATA%\DazeControlApp), one Apps entry and one
# uninstall. Start with Windows is the editor's Settings toggle (or `dctl autostart on`).
# Example: pwsh scripts/install.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$packages = Join-Path $root 'artifacts/install'
$exe = Join-Path $env:LOCALAPPDATA 'DazeControlApp/current/DazeControl.exe'

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
if ($process.ExitCode -ne 0) { throw "Setup failed ($($process.ExitCode)); see $env:LOCALAPPDATA\velopack\velopack_DazeControlApp.log." }

if (-not (Get-Process DazeControl -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) | Out-Null
}
$run = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name DazeControl -ErrorAction SilentlyContinue).DazeControl
Write-Host "Installed and started: $exe"
Write-Host "Start with Windows: $(if ($run) { $run } else { 'off - turn it on in the editor''s Settings or with dctl autostart on' })"
