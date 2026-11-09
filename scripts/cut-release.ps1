<#
Cuts a Daze Control release end to end with terse output (one line per phase). Run ONLY when the user asked for a release (D-043).
Blocks until done, so run it with run_in_background: waiting costs no model calls.
  pwsh scripts/cut-release.ps1 -Notes notes.md [-Prs 130,131] [-Version v0.51.0] [-Rc] [-TimeoutMinutes 90] [-DryRun] [-SelfTest]
  -Rc cuts a release candidate: the next minor version plus -rc.<n> (n = 1 + the highest existing rc of it), e.g. v0.52.0-rc.1.
  The release is marked pre-release, so installed apps (stable only) do not update to it; install its Setup.exe by hand.
  A later stable release of the same number supersedes it. A tag like v0.52.0-rc.2 can also be passed with -Version.
Exit: 0 ok | 2 PR closed/not merged | 3 CI red | 4 release run red | 5 preflight/other error | 6 timeout.
#>
param([string]$Notes, [int[]]$Prs = @(), [string]$Version, [switch]$Rc, [int]$TimeoutMinutes = 90, [switch]$DryRun, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$Repo = 'dazewell/daze-control'

function Get-NextVersion([string[]]$Tags) {
  $best = $null
  foreach ($t in $Tags) {
    if ($t -match '^v(\d+)\.(\d+)\.(\d+)$') {
      $v = [version]"$($Matches[1]).$($Matches[2]).$($Matches[3])"
      if (-not $best -or $v -gt $best) { $best = $v }
    }
  }
  if (-not $best) { throw 'no v<major>.<minor>.<patch> tag found' }
  "v$($best.Major).$($best.Minor + 1).0"
}
function Get-NextRc([string]$Base, [string[]]$Tags) {
  $max = 0
  foreach ($t in $Tags) { if ($t -match ('^' + [regex]::Escape($Base) + '-rc\.(\d+)$') -and [int]$Matches[1] -gt $max) { $max = [int]$Matches[1] } }
  "$Base-rc.$($max + 1)"
}
function Fail([int]$Code, [string]$Msg) { Write-Output $Msg; exit $Code }

if ($SelfTest) {
  $cases = @(
    @(@('v0.50.0', 'v0.49.0'), 'v0.51.0'), @(@('v0.9.0', 'v0.10.0'), 'v0.11.0'),
    @(@('v0.50.2', 'junk', 'v0.50.0'), 'v0.51.0'), @(@('v1.2.3'), 'v1.3.0'))
  $bad = 0
  foreach ($c in $cases) { $got = Get-NextVersion $c[0]; if ($got -ne $c[1]) { "FAIL $($c[0] -join ',') -> $got (want $($c[1]))"; $bad++ } }
  # Release candidates never count as the latest stable version; the next rc number follows the highest existing one.
  if ((Get-NextVersion @('v0.51.2', 'v0.52.0-rc.1')) -ne 'v0.52.0') { 'FAIL: an rc tag changed the next stable version'; $bad++ }
  if ((Get-NextRc 'v0.52.0' @('v0.51.2')) -ne 'v0.52.0-rc.1') { 'FAIL: first rc'; $bad++ }
  if ((Get-NextRc 'v0.52.0' @('v0.52.0-rc.1', 'v0.52.0-rc.2', 'v0.53.0-rc.9')) -ne 'v0.52.0-rc.3') { 'FAIL: next rc'; $bad++ }
  # Guard: a space after a comma in a --json field list splits it into separate gh arguments.
  $src = Get-Content $PSCommandPath -Raw
  if ($src -match ('--' + 'json \S+, ')) { "FAIL: --json field list contains ', ' (no spaces after commas)"; $bad++ }
  if ($bad) { exit 1 }
  "selftest ok ($($cases.Count) cases)"; exit 0
}

# Preflight
foreach ($c in 'gh', 'git') { if (-not (Get-Command $c -ErrorAction SilentlyContinue)) { Fail 5 "preflight: $c not found" } }
gh auth status *> $null; if ($LASTEXITCODE) { Fail 5 'preflight: gh not authenticated' }
if (-not $DryRun) {
  if (-not $Notes) { Fail 5 'preflight: -Notes <file> required' }
  if (-not (Test-Path $Notes)) { Fail 5 "preflight: notes file not found: $Notes" }
  if (-not (Get-Content $Notes -Raw).Trim()) { Fail 5 'preflight: notes file is empty' }
}
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)

# 1. PRs
if ($Prs.Count) {
  $open = [System.Collections.Generic.HashSet[int]]::new([int[]]$Prs)
  while ($open.Count -gt 0) {
    foreach ($n in @($open)) {
      $s = gh pr view $n --repo $Repo --json state -q .state 2>$null
      if ($s -eq 'MERGED') { [void]$open.Remove($n) }
      elseif ($s -eq 'CLOSED') { Fail 2 "PR #$n closed without merging" }
      elseif ($DryRun) { Write-Output "dry-run: PR #$n is $s (would wait)"; [void]$open.Remove($n) }
    }
    if ($open.Count -eq 0) { break }
    if ((Get-Date) -gt $deadline) { Fail 6 "timeout: PR(s) $($open -join ',') still open" }
    Start-Sleep -Seconds 60
  }
  if (-not $DryRun) { Write-Output "PRs merged: $($Prs -join ',')" }
}

# 2. Target
git fetch origin main --tags --quiet; if ($LASTEXITCODE) { Fail 5 'git fetch failed' }
$sha = (git rev-parse origin/main).Trim()
if ($Rc -and $Version) { Fail 5 'preflight: use either -Rc or -Version, not both' }
if (-not $Version) {
  $Version = Get-NextVersion @(git tag --list 'v*')
  if ($Rc) { $Version = Get-NextRc $Version @(git tag --list 'v*') }
}
if ($Version -notmatch '^v\d+\.\d+\.\d+(-rc\.\d+)?$') { Fail 5 "bad version '$Version' (want vX.Y.Z or vX.Y.Z-rc.N)" }
if (git tag --list $Version) { Fail 5 "tag $Version already exists locally" }
git ls-remote --exit-code --tags origin "refs/tags/$Version" *> $null
if ($LASTEXITCODE -eq 0) { Fail 5 "tag $Version already exists on origin" }
if ($DryRun) {
  Write-Output "dry-run: $Version -> $($sha.Substring(0, 10)) (origin/main); would: wait CI (ci.yml) on that sha, tag+push, wait release.yml, set notes from '$Notes', check assets. Nothing changed."
  exit 0
}

# 3. CI on the target sha
$ci = $null
while (-not $ci) {
  $runs = gh run list --repo $Repo --workflow ci.yml --branch main --limit 30 --json databaseId,headSha,status,conclusion,url 2>&1 | Out-String
  if ($LASTEXITCODE) { $ghErr = $runs.Trim(); $runs = @() } else { $ghErr = ''; $runs = $runs | ConvertFrom-Json }
  $ci = $runs | Where-Object { $_.headSha -eq $sha } | Select-Object -First 1
  if ($ci) { break }
  if ((Get-Date) -gt $deadline) { Fail 6 "timeout: no CI run for $($sha.Substring(0, 10))$(if ($ghErr) { "; gh said: $ghErr" })" }
  Start-Sleep -Seconds 15
}
if ($ci.status -ne 'completed') {
  gh run watch $ci.databaseId --repo $Repo --exit-status *> $null
  $ci = gh run view $ci.databaseId --repo $Repo --json databaseId,conclusion,url | ConvertFrom-Json
}
if ($ci.conclusion -ne 'success') { Fail 3 "CI red ($($ci.conclusion)): $($ci.url)" }
Write-Output "CI green on $($sha.Substring(0, 10)): run $($ci.databaseId)"

# 4. Tag the exact sha (D-043: only reached because the user asked for this release)
git tag $Version $sha; if ($LASTEXITCODE) { Fail 5 'git tag failed' }
git push origin $Version --quiet; if ($LASTEXITCODE) { Fail 5 "git push $Version failed" }

# 5. Release run
$rel = $null
while (-not $rel) {
  $rel = gh run list --repo $Repo --workflow release.yml --branch $Version --limit 5 --json databaseId,status,conclusion,url | ConvertFrom-Json | Select-Object -First 1
  if ($rel) { break }
  if ((Get-Date) -gt $deadline) { Fail 6 "timeout: no release run for $Version (tag is pushed)" }
  Start-Sleep -Seconds 10
}
if ($rel.status -ne 'completed') {
  gh run watch $rel.databaseId --repo $Repo --exit-status *> $null
  $rel = gh run view $rel.databaseId --repo $Repo --json databaseId,conclusion,url | ConvertFrom-Json
}
if ($rel.conclusion -ne 'success') { Fail 4 "release run red ($($rel.conclusion)): $($rel.url)" }
Write-Output "release run green: $($rel.databaseId)"

# 6. Notes + assets
gh release edit $Version --repo $Repo --notes-file $Notes *> $null; if ($LASTEXITCODE) { Fail 5 "gh release edit $Version failed (tag and release exist)" }
$info = gh release view $Version --repo $Repo --json url,assets | ConvertFrom-Json
$names = @($info.assets | ForEach-Object name)
$want = @{ 'Setup.exe' = '*Setup.exe'; 'Portable.zip' = '*Portable.zip'; 'full nupkg' = '*-full.nupkg'; 'delta nupkg' = '*-delta.nupkg'; 'RELEASES' = 'RELEASES*' }
$missing = @($want.Keys | Where-Object { $p = $want[$_]; -not ($names | Where-Object { $_ -like $p }) })
$assets = if ($missing) { "MISSING: $($missing -join ', ')" } else { 'ok' }
Write-Output "---"
Write-Output "version: $Version"
Write-Output "sha: $sha"
Write-Output "ci: $($ci.conclusion) ($($ci.databaseId)); release: $($rel.conclusion) ($($rel.databaseId))"
Write-Output "link: $($info.url)"
Write-Output "assets: $assets ($($names.Count) files)"
exit 0
