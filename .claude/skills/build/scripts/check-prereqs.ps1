# Checks the local machine can build Ctrl Alt Daze. Read-only: prints problems and fixes, installs nothing.
$ok = $true

$sdks = @(dotnet --list-sdks 2>$null)
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    Write-Host "MISSING  .NET 10 SDK (found: $($sdks -join ', '))" -ForegroundColor Red
    Write-Host "         fix: winget install Microsoft.DotNet.SDK.10"
    $ok = $false
} else {
    Write-Host "OK       .NET 10 SDK"
}

# Native AOT links with MSVC link.exe + the Windows SDK libs; ILC finds them through vswhere.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vc = if (Test-Path $vswhere) {
    & $vswhere -products * -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $vc) {
    Write-Host "MISSING  MSVC C++ tools (needed only for Native AOT publish of the engine and cad)" -ForegroundColor Yellow
    Write-Host '         fix: winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"'
    $ok = $false
} else {
    Write-Host "OK       MSVC C++ tools ($vc)"
}

$build = [System.Environment]::OSVersion.Version.Build
if ($build -lt 26100) {
    Write-Host "UNSUPPORTED  Windows build $build; minimum is 26100 (Win11 24H2)" -ForegroundColor Red
    $ok = $false
} else {
    Write-Host "OK       Windows build $build"
}

$arch = $env:PROCESSOR_ARCHITECTURE
Write-Host "INFO     CPU architecture $arch (publish RID: $(if ($arch -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }))"

if ($ok) { exit 0 } else { exit 1 }
