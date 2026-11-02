# Release package (D-015, D-043): the editor (self-contained WinUI), the Native AOT engine and dctl published into one
# folder, then Velopack's per-user Setup.exe, portable zip and update feed in artifacts/releases. release.yml and
# install.ps1 run this; vpk comes from the repo's dotnet-tools.json (keep its version equal to the Velopack package).
# Example: pwsh scripts/pack.ps1
# -Version overrides <Version> (testing an update against a local feed, build skill).
param([string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/releases'), [string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts/publish'
# Not "DazeControl": Velopack installs to %LOCALAPPDATA%\<packId> and deletes it on uninstall, and
# %LOCALAPPDATA%\DazeControl holds the logs and run history (OSD D-029).
$packId = 'DazeControlApp'

# ILC's link step runs vswhere from PATH (build skill).
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) { $env:PATH += ";$installer" }

dotnet tool restore --tool-manifest (Join-Path $root 'dotnet-tools.json') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed ($LASTEXITCODE)." }

$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
$version = if ($Version) { $Version } else { $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# Editor first: the AOT exes are single files and never collide with its runtime files.
foreach ($project in 'DazeControl.Editor', 'DazeControl.Engine', 'DazeControl.Cli') {
    dotnet publish (Join-Path $root "src/$project") -c Release -r win-x64 -o $publish -v q -nologo -p:Version=$version
    if ($LASTEXITCODE -ne 0) { throw "Publish of $project failed ($LASTEXITCODE)." }
}
# Without the .pri the editor dies loading XAML (OSD X-004).
if (-not (Test-Path (Join-Path $publish 'DazeControl.Editor.pri'))) { throw 'Publish has no DazeControl.Editor.pri (OSD X-004).' }
foreach ($exe in 'DazeControl.exe', 'DazeControl.Editor.exe', 'dctl.exe') {
    if (-not (Test-Path (Join-Path $publish $exe))) { throw "Publish has no $exe." }
}
# Debug symbols stay out of the package (the AOT .pdb files are tens of MB).
Get-ChildItem $publish -Filter '*.pdb' | Remove-Item

dotnet vpk pack --packId $packId --packVersion $version --packDir $publish --mainExe DazeControl.exe `
    --packTitle 'Daze Control' --packAuthors dazewell --icon (Join-Path $root 'assets/app.ico') --shortcuts StartMenuRoot --outputDir $Output
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)." }
Write-Host "Packed $version into $Output"
