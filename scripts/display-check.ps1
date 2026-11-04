# Live display matrix (D-077, PLAN F5): runs display flows through a dev engine and prints the screens after each.
# CHANGES THE SCREENS: run only with the user's OK at that time. It saves the current config first and restores it
# in `finally`, checks the restore, and restarts the installed engine.
#   pwsh scripts/display-check.ps1 -Config <scratch config.yaml> -Flows u-both,u-dell,l-dual -Go
#   pwsh scripts/display-check.ps1            # read-only: prints the screens now and exits
param(
    [string]$Config = "",
    [string[]]$Flows = @(),
    [string]$Out = "$env:TEMP\daze-display-check",
    [switch]$Go
)
$ErrorActionPreference = 'Stop' # until the snapshot is saved; 'Continue' while the matrix runs, so cleanup always runs
$Flows = @($Flows | ForEach-Object { $_ -split ',' } | Where-Object { $_ })  # pwsh -File passes "a,b" as one string
$root = Split-Path $PSScriptRoot -Parent
$bin = "net10.0-windows10.0.26100.0\win-x64"
$engine = "$root\src\DazeControl.Engine\bin\Debug\$bin\DazeControl.exe"
$dctl = "$root\src\DazeControl.Cli\bin\Debug\$bin\dctl.exe"
$installed = "$env:LOCALAPPDATA\DazeControlApp\current\DazeControl.exe"

Add-Type -Namespace DazeControl -Name Ccd -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint f, out uint p, out uint m);
[DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint f, ref uint pc, [Out] byte[] p, ref uint mc, [Out] byte[] m, out int t);
[DllImport("user32.dll")] public static extern int QueryDisplayConfig(uint f, ref uint pc, [Out] byte[] p, ref uint mc, [Out] byte[] m, IntPtr t);
[DllImport("user32.dll")] public static extern int SetDisplayConfig(uint pc, byte[] p, uint mc, byte[] m, uint f);
'@

# Paths and modes for a QDC_* query, retried when a screen comes or goes between the two calls; throws on any
# other error, so a snapshot is complete or the script stops before changing anything.
function Query-Ccd([uint32]$which, [ref]$topology) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $pc = 0; $mc = 0
        $e = [DazeControl.Ccd]::GetDisplayConfigBufferSizes($which, [ref]$pc, [ref]$mc)
        if ($e -ne 0) { throw "GetDisplayConfigBufferSizes failed ($e)" }
        $p = New-Object byte[] ($pc * 72); $m = New-Object byte[] ($mc * 64); $t = 0
        $e = if ($which -eq 4) { [DazeControl.Ccd]::QueryDisplayConfig($which, [ref]$pc, $p, [ref]$mc, $m, [ref]$t) }
             else { [DazeControl.Ccd]::QueryDisplayConfig($which, [ref]$pc, $p, [ref]$mc, $m, [IntPtr]::Zero) }
        if ($e -eq 0) { if ($topology) { $topology.Value = $t }; return [pscustomobject]@{ Paths = $pc; Modes = $mc; P = $p; M = $m } }
        if ($e -ne 122) { throw "QueryDisplayConfig failed ($e)" } # 122 = ERROR_INSUFFICIENT_BUFFER: size again
    }
    throw 'QueryDisplayConfig: the screens kept changing'
}

# Active paths (QDC_ONLY_ACTIVE_PATHS) as raw bytes: [pathCount][modeCount][paths][modes], x64 struct sizes 72 / 64.
function Get-Raw {
    $q = Query-Ccd 2 $null; $pc = $q.Paths; $mc = $q.Modes; $p = $q.P; $m = $q.M
    if ($pc -lt 1) { throw 'no active screen' }
    [byte[]]([BitConverter]::GetBytes([int]$pc) + [BitConverter]::GetBytes([int]$mc) + $p[0..($pc * 72 - 1)] + $m[0..($mc * 64 - 1)])
}

# One line: the database topology plus each active screen's target, source, resolution and position.
function Get-State {
    $b = Get-Raw; $pc = [BitConverter]::ToInt32($b, 0); $mc = [BitConverter]::ToInt32($b, 4); $mo = 8 + $pc * 72
    $t = 0; [void](Query-Ccd 4 ([ref]$t))
    $topology = @{ 1 = 'internal'; 2 = 'clone'; 4 = 'extend'; 8 = 'external' }[$t]
    $screens = for ($i = 0; $i -lt $pc; $i++) {
        $o = 8 + $i * 72
        $src = [BitConverter]::ToUInt32($b, $o + 8); $smi = [BitConverter]::ToUInt32($b, $o + 12); $tid = [BitConverter]::ToUInt32($b, $o + 28)
        $s = $mo + $smi * 64
        '0x{0:x} src{1} {2}x{3} @({4},{5})' -f $tid, $src, [BitConverter]::ToUInt32($b, $s + 16), [BitConverter]::ToUInt32($b, $s + 20),
            [BitConverter]::ToInt32($b, $s + 28), [BitConverter]::ToInt32($b, $s + 32)
    }
    "db=$topology  " + ($screens -join ' | ')
}

function Restore-Raw([byte[]]$b) {
    $pc = [BitConverter]::ToInt32($b, 0); $mc = [BitConverter]::ToInt32($b, 4)
    $p = $b[8..(8 + $pc * 72 - 1)]; $m = $b[(8 + $pc * 72)..($b.Length - 1)]
    # SDC_APPLY 0x80 | USE_SUPPLIED 0x20 | SAVE_TO_DATABASE 0x200 | ALLOW_CHANGES 0x400
    [DazeControl.Ccd]::SetDisplayConfig($pc, $p, $mc, $m, 0x80 -bor 0x20 -bor 0x200 -bor 0x400)
}

$start = Get-State
"{0,-12} {1}" -f 'now', $start
if (-not $Go) { 'read-only (pass -Config, -Flows and -Go, with the user''s OK, to run the matrix)'; return }
if (-not (Test-Path $Config) -or -not (Test-Path $engine) -or -not (Test-Path $dctl)) { "missing config or Debug build"; exit 1 }

New-Item -ItemType Directory -Force $Out | Out-Null
$before = Get-Raw
[IO.File]::WriteAllBytes("$Out\before.bin", $before)
$start | Set-Content "$Out\before.txt"
"saved: $Out\before.bin"

$ErrorActionPreference = 'Continue'
& pwsh -NoProfile -File "$PSScriptRoot\stop.ps1"
$dev = Start-Process $engine -ArgumentList "`"$Config`"" -PassThru
try {
    Start-Sleep 4
    foreach ($f in $Flows) {
        $result = & $dctl run $f --wait 2>&1 | Select-Object -Last 1
        Start-Sleep 3
        "{0,-12} {1}" -f $f, (Get-State)
        "             -> $result"
    }
}
finally {
    $restored = $false
    foreach ($attempt in 1..2) {
        $r = Restore-Raw $before; Start-Sleep 3
        $now = try { Get-State } catch { "unreadable: $_" }
        if ($now -eq $start) { $restored = $true; break }
        "restore attempt $attempt (error $r): $now"
    }
    "{0,-12} {1}" -f 'restored', $now
    if (-not $restored) { Write-Host "SCREENS NOT RESTORED. Saved: $Out\before.bin / before.txt" -ForegroundColor Red }
    Stop-Process -Id $dev.Id -Force -ErrorAction SilentlyContinue
    if (Test-Path $installed) { Start-Process $installed | Out-Null; Start-Sleep 2 }
    "engines: " + ((Get-CimInstance Win32_Process -Filter "Name='DazeControl.exe'").ExecutablePath -join ', ')
}
if (-not $restored) { exit 2 }
