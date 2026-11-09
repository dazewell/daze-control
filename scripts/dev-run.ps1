# Try this checkout: stops the running engine (your installed one or another checkout's), builds this
# worktree's engine, CLI and editor, and starts them on a fixed dev config, never your real config.
#   pwsh scripts/dev-run.ps1              build + run this checkout (engine + editor)
#   pwsh scripts/dev-run.ps1 -NoEditor    engine only
#   pwsh scripts/dev-run.ps1 -Restore     stop the dev engine/editor and start the installed app again
#   pwsh scripts/dev-run.ps1 -ResetConfig overwrite the dev config with samples/dev-config.yaml
# The dev config lives outside the repo: %LOCALAPPDATA%\DazeControl\dev\config.yaml. It is copied from
# samples/dev-config.yaml once and then left alone, so edits you make in the editor stay across runs.
# Logs and history are shared with the installed engine (%LOCALAPPDATA%\DazeControl).
param(
    [switch]$NoEditor,
    [switch]$Restore,
    [switch]$ResetConfig,
    [string]$Config = (Join-Path $env:LOCALAPPDATA 'DazeControl\dev\config.yaml')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$installed = Join-Path $env:LOCALAPPDATA 'DazeControlApp\current\DazeControl.exe'
$engineExe = Join-Path $repo 'artifacts\engine\DazeControl.exe'
$dctl = Join-Path $repo 'artifacts\cli\dctl.exe'
$editorExe = Join-Path $repo 'src\DazeControl.Editor\bin\Debug\net10.0-windows10.0.26100.0\win-x64\DazeControl.Editor.exe'

function Stop-Editors {
    # Only editors started on the dev config; other checkouts' editors on other files are left alone.
    Get-CimInstance Win32_Process -Filter "Name = 'DazeControl.Editor.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine -like "*$Config*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Invoke-Step([string]$What, [scriptblock]$Run) {
    Write-Host "dev-run: $What" -ForegroundColor Cyan
    & $Run
    if ($LASTEXITCODE -ne 0) { throw "dev-run: $What failed (exit $LASTEXITCODE)" }
}

Write-Host "dev-run: stopping the running engine"
Stop-Editors
& (Join-Path $PSScriptRoot 'stop.ps1')

if ($Restore) {
    if (Test-Path $installed) {
        Start-Process $installed
        Write-Host "dev-run: installed app started ($installed)" -ForegroundColor Green
    } else {
        Write-Host "dev-run: no installed app at $installed, nothing to restart" -ForegroundColor Yellow
    }
    return
}

# The dev config: created once from the template (or on -ResetConfig).
$template = Join-Path $repo 'samples\dev-config.yaml'
if ($ResetConfig -or -not (Test-Path $Config)) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $Config) | Out-Null
    Copy-Item $template $Config -Force
    Write-Host "dev-run: dev config written to $Config"
}

# ILC's link step needs vswhere on PATH (build skill, 2026-11-01).
$vs = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ($env:PATH -notlike "*$vs*") { $env:PATH += ";$vs" }

Push-Location $repo
try {
    Invoke-Step 'publishing the engine (Native AOT, a few minutes)' { dotnet publish src/DazeControl.Engine -c Release -r win-x64 -o artifacts/engine --nologo -v q }
    Invoke-Step 'publishing dctl' { dotnet publish src/DazeControl.Cli -c Release -r win-x64 -o artifacts/cli --nologo -v q }
    if (-not $NoEditor) {
        Invoke-Step 'building the editor' { dotnet build src/DazeControl.Editor -c Debug --nologo -v q }
    }
} finally { Pop-Location }

Start-Process $engineExe -ArgumentList "`"$Config`""
Start-Sleep -Seconds 3
& $dctl status
if (-not $NoEditor) {
    Start-Process $editorExe -ArgumentList '--config', "`"$Config`""
}
$branch = (git -C $repo branch --show-current)
Write-Host "dev-run: running '$branch' on $Config" -ForegroundColor Green
Write-Host "dev-run: back to your installed app with: pwsh scripts/dev-run.ps1 -Restore"
