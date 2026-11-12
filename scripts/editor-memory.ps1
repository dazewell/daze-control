# Editor memory probe (F-036): builds the editor, opens the dev-big sample's Probe 100 (95 steps) on a scratch
# copy, runs select all / copy / paste / undo three times through the view model and prints the process's memory
# after each step. The editor window shows for about a minute; nothing touches the user's config or devices.
#   pwsh scripts/editor-memory.ps1                 Release build
#   pwsh scripts/editor-memory.ps1 -Configuration Debug -NoBuild
param(
    [string]$Configuration = 'Release',
    [string]$Flow = 'probe-100',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'src/DazeControl.Editor') -c $Configuration --nologo -v q | Out-Host
    if ($LASTEXITCODE) { throw "editor build failed ($LASTEXITCODE)" }
}
$exe = Join-Path $repo "src\DazeControl.Editor\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\DazeControl.Editor.exe"
$work = Join-Path ([IO.Path]::GetTempPath()) ('dc-memprobe-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
Copy-Item (Join-Path $repo 'samples\dev-big.yaml') (Join-Path $work 'config.yaml')
$log = Join-Path $work 'memory.log'
$p = Start-Process $exe -ArgumentList '--config', (Join-Path $work 'config.yaml'), '--memory-probe', $Flow, $log -PassThru
if (-not $p.WaitForExit(180000)) { $p.Kill($true); throw 'the probe did not finish in 3 minutes' }
Get-Content $log
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
