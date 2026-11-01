# Measures the engine's idle footprint against the budget (ARCHITECTURE -> Footprint, CLAUDE.md):
# private bytes <= 15 MB, working set <= 35 MB, 0 ms CPU while idle.
param(
    [string]$Exe,
    [string]$Config = (Join-Path $PSScriptRoot '..\samples\spike-config.yaml'),
    [switch]$NoPublish,
    [int]$SettleSeconds = 30,
    [int]$IdleSeconds = 60
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if (-not $Exe) {
    $Exe = Join-Path $root 'artifacts\engine\DazeControl.exe'
    if (-not $NoPublish) {
        & (Join-Path $PSScriptRoot 'stop.ps1')
        dotnet publish (Join-Path $root 'src\DazeControl.Engine') -c Release -r win-x64 -o (Join-Path $root 'artifacts\engine') -nologo
        if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    }
}
if (-not (Test-Path $Exe)) { throw "no engine at $Exe" }

& (Join-Path $PSScriptRoot 'stop.ps1')
$p = Start-Process -FilePath $Exe -ArgumentList "`"$((Resolve-Path $Config).Path)`"" -PassThru
try {
    Start-Sleep -Seconds $SettleSeconds
    $p.Refresh()
    if ($p.HasExited) { throw "engine exited with $($p.ExitCode) during settle" }
    $cpu0 = $p.TotalProcessorTime
    Start-Sleep -Seconds $IdleSeconds
    $p.Refresh()
    $cpuMs = [math]::Round(($p.TotalProcessorTime - $cpu0).TotalMilliseconds)
    $privateMb = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    $wsMb = [math]::Round($p.WorkingSet64 / 1MB, 1)
    # "Memory" in Task Manager = private working set.
    $pwsMb = [math]::Round((Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "IDProcess=$($p.Id)").WorkingSetPrivate / 1MB, 1)
    $threads = $p.Threads.Count
    $exeMb = [math]::Round((Get-Item $Exe).Length / 1MB, 1)
} finally {
    & (Join-Path $PSScriptRoot 'stop.ps1')
}

$ok = $privateMb -le 15 -and $wsMb -le 35 -and $cpuMs -eq 0
Write-Host ("private {0} MB (budget 15) · working set {1} MB (budget 35) · private WS {2} MB · CPU {3} ms over {4} s · {5} threads · exe {6} MB" -f $privateMb, $wsMb, $pwsMb, $cpuMs, $IdleSeconds, $threads, $exeMb)
Write-Host ("| {0} | {1} | | {2} | {3} | {4} ({5} s) |" -f (Get-Date -Format 'yyyy-MM-dd'), (git -C $root rev-parse --short HEAD), $privateMb, $wsMb, $cpuMs, $IdleSeconds)
if ($ok) { Write-Host "within budget" -ForegroundColor Green; exit 0 }
Write-Host "OVER BUDGET" -ForegroundColor Red; exit 1
