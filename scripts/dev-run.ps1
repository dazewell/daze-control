# Try this checkout: stops the running engine (your installed one or another checkout's), builds this
# worktree's engine, CLI and editor, and starts them on a fixed dev config, never your real config.
#   pwsh scripts/dev-run.ps1              build + run this checkout (engine + editor)
#   pwsh scripts/dev-run.ps1 -NoEditor    engine only
#   pwsh scripts/dev-run.ps1 -Restore     stop the dev engine/editor and start the installed app again
#   pwsh scripts/dev-run.ps1 -ResetConfig overwrite the dev config with samples/dev-config.yaml
#   pwsh scripts/dev-run.ps1 -Sample showcase   run on a larger sample instead: samples/dev-<name>.yaml, kept as
#                                               %LOCALAPPDATA%\CtrlAltDaze\dev\<name>.yaml (showcase: branchy flows,
#                                               cards, failure lines, prompts; big: 40/60/100-step flows). Safe: they
#                                               only check, show messages, wait or ask. -ResetConfig works with it too.
# The dev config lives at a fixed place outside the repo: %LOCALAPPDATA%\CtrlAltDaze\dev\config.yaml. It is
# copied from samples/dev-config.yaml once and then left alone, so edits you make in the editor stay across runs.
# Logs and history are shared with the installed engine (%LOCALAPPDATA%\CtrlAltDaze).
# The editor is started here on the dev config. The dev engine's own tray "Open editor" stays off on purpose:
# it would open your real config.
# If the build fails after the installed app was stopped, the installed app is started again.
param(
    [switch]$NoEditor,
    [switch]$Restore,
    [switch]$ResetConfig,
    [string]$Sample
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$devFolder = Join-Path $env:LOCALAPPDATA 'CtrlAltDaze\dev'
$config = if ($Sample) { Join-Path $devFolder "$Sample.yaml" } else { Join-Path $devFolder 'config.yaml' }
$installed = Join-Path $env:LOCALAPPDATA 'CtrlAltDazeApp\current\CtrlAltDaze.exe'
$engineExe = Join-Path $repo 'artifacts\engine\CtrlAltDaze.exe'
$cad = Join-Path $repo 'artifacts\cli\cad.exe'
$editorExe = Join-Path $repo 'src\CtrlAltDaze.Editor\bin\Debug\net10.0-windows10.0.26100.0\win-x64\CtrlAltDaze.Editor.exe'

function Stop-Editors {
    # Editors on any dev config (%LOCALAPPDATA%\CtrlAltDaze\dev\*.yaml, so a previous -Sample's editor goes too, F-026);
    # editors on the user's real config are left alone.
    # Closed through the window first so the editor flushes pending edits; killed only if it doesn't exit.
    $ids = @(Get-CimInstance Win32_Process -Filter "Name = 'CtrlAltDaze.Editor.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($devFolder, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.ProcessId })
    foreach ($id in $ids) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        [void]$p.CloseMainWindow()
        if (-not $p.WaitForExit(8000)) {
            Write-Host "dev-run: editor pid $id didn't close, killing it" -ForegroundColor Yellow
            Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Start-Installed {
    if (Test-Path $installed) {
        Start-Process $installed
        Write-Host "dev-run: installed app started ($installed)" -ForegroundColor Green
    } else {
        Write-Host "dev-run: no installed app at $installed, nothing to restart" -ForegroundColor Yellow
    }
}

function Invoke-Step([string]$What, [scriptblock]$Run) {
    Write-Host "dev-run: $What" -ForegroundColor Cyan
    & $Run
    if ($LASTEXITCODE -ne 0) { throw "dev-run: $What failed (exit $LASTEXITCODE)" }
}

# Checked before anything is stopped: a mistyped sample leaves the running app alone.
$template = if ($Sample) { Join-Path $repo "samples\dev-$Sample.yaml" } else { Join-Path $repo 'samples\dev-config.yaml' }
if (($Sample -and $Sample -notmatch '^[A-Za-z0-9-]+$') -or -not (Test-Path -LiteralPath $template)) {
    $names = (Get-ChildItem (Join-Path $repo 'samples') -Filter 'dev-*.yaml' | Where-Object Name -ne 'dev-config.yaml' |
        ForEach-Object { $_.BaseName -replace '^dev-', '' }) -join ', '
    throw "dev-run: no sample '$Sample' (samples: $names)"
}
Write-Host "dev-run: stopping the running engine"
Stop-Editors
& (Join-Path $PSScriptRoot 'stop.ps1')

if ($Restore) {
    Start-Installed
    return
}

try {
    # The dev config: created once from the template (or on -ResetConfig).
    if ($ResetConfig -or -not (Test-Path -LiteralPath $config)) {
        New-Item -ItemType Directory -Force (Split-Path -Parent $config) | Out-Null
        Copy-Item -LiteralPath $template -Destination $config -Force
        Write-Host "dev-run: dev config written to $config"
    }

    # ILC's link step needs vswhere on PATH (build skill, 2026-11-01).
    $vs = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if ($env:PATH -notlike "*$vs*") { $env:PATH += ";$vs" }

    Push-Location $repo
    try {
        Invoke-Step 'publishing the engine (Native AOT, a few minutes)' { dotnet publish src/CtrlAltDaze.Engine -c Release -r win-x64 -o artifacts/engine --nologo -v q }
        Invoke-Step 'publishing cad' { dotnet publish src/CtrlAltDaze.Cli -c Release -r win-x64 -o artifacts/cli --nologo -v q }
        if (-not $NoEditor) {
            Invoke-Step 'building the editor' { dotnet build src/CtrlAltDaze.Editor -c Debug --nologo -v q }
        }
    } finally { Pop-Location }

    Start-Process $engineExe -ArgumentList "`"$config`""
    # The engine needs a moment to start: ask it for up to ~15 s.
    $up = $false
    for ($i = 0; $i -lt 15 -and -not $up; $i++) {
        Start-Sleep -Seconds 1
        & $cad status | Out-Null
        $up = ($LASTEXITCODE -eq 0)
    }
    if (-not $up) { throw 'dev-run: the dev engine did not answer cad status' }
    & $cad status
    if (-not $NoEditor) {
        $ed = Start-Process $editorExe -ArgumentList '--config', "`"$config`"" -PassThru
        # A XAML crash at start is native and leaves no editor.log line: say so instead of showing nothing.
        if ($ed.WaitForExit(6000)) {
            Write-Host "dev-run: the editor exited at once (exit $($ed.ExitCode)), no window. See Windows Event Viewer > Application (CtrlAltDaze.Editor.exe) and $env:LOCALAPPDATA\CtrlAltDaze\logs\editor.log" -ForegroundColor Red
        }
    }
} catch {
    Write-Host "dev-run: failed: $($_.Exception.Message)" -ForegroundColor Red
    Stop-Editors
    & (Join-Path $PSScriptRoot 'stop.ps1')
    Start-Installed
    throw
}

$branch = (git -C $repo branch --show-current)
Write-Host "dev-run: running '$branch' on $config" -ForegroundColor Green
Write-Host "dev-run: back to your installed app with: pwsh scripts/dev-run.ps1 -Restore"
