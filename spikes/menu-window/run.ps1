# Spike S2 runner: one test app mode x one menu script; logs + screenshot into -Out.
# Exclusive mode turns fullscreen optimisations off for the probe exe (per-user AppCompat layer) and removes it after.
param(
    [ValidateSet('borderless', 'fso', 'exclusive')] [string]$Mode = 'borderless',
    [string]$Script = 'keys',
    [string]$Out = "$env:TEMP\s2-menu-window",
    [switch]$Force,
    [int]$GameSeconds = 14
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\MenuProbe.exe'
New-Item -ItemType Directory -Force $Out | Out-Null
$tag = "$Mode-$Script"
$layers = 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
if ($Mode -eq 'exclusive') {
    if (-not (Test-Path $layers)) { New-Item $layers -Force | Out-Null }
    New-ItemProperty $layers -Name $exe -Value '~ DISABLEDXMAXIMIZEDWINDOWEDMODE' -PropertyType String -Force | Out-Null
}
try {
    $game = Start-Process $exe -ArgumentList 'game', $Mode, $GameSeconds, "$Out\$tag.game.log" -PassThru
    Start-Sleep -Milliseconds 2500
    $menuArgs = @('menu', $Script, "$Out\$tag.menu.log", "$Out\$tag.bmp")
    if ($Force) { $menuArgs += '--force' }
    if ($Script -eq 'timeout') { $menuArgs += '--close', '2500' }
    $menu = Start-Process $exe -ArgumentList $menuArgs -PassThru
    if ($Script -eq 'hold') { Start-Sleep -Seconds 2; Stop-Process -Id $menu.Id -Force }
    if (-not $menu.WaitForExit(15000)) { Stop-Process -Id $menu.Id -Force; "menu hung" }
    "menu exit $($menu.ExitCode)"
    $reg = Start-Process $exe -ArgumentList 'regcheck', "$Out\$tag.regcheck.log" -PassThru -Wait
    "regcheck exit $($reg.ExitCode)"
    if (-not $game.WaitForExit(($GameSeconds + 5) * 1000)) { Stop-Process -Id $game.Id -Force; "game killed" }
}
finally {
    if ($Mode -eq 'exclusive') { Remove-ItemProperty $layers -Name $exe -ErrorAction SilentlyContinue }
}
Get-Content "$Out\$tag.menu.log", "$Out\$tag.regcheck.log" -ErrorAction SilentlyContinue
if (Test-Path "$Out\$tag.bmp") {
    Add-Type -AssemblyName System.Drawing
    $b = [System.Drawing.Bitmap]::new("$Out\$tag.bmp"); $b.Save("$Out\$tag.png", [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
    Remove-Item "$Out\$tag.bmp"
}
