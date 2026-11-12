<#
The ONE way sessions merge: runs the ready check, then `gh pr merge <n> --merge` (never --auto, --squash or --admin), prints the merge commit.
Allow-list this single command in permission settings: `pwsh scripts/pr-merge.ps1` (D-122).
Usage: pwsh scripts/pr-merge.ps1 <pr> [-DryRun]
Exit: 0 merged (or dry-run READY), 1 blocked or merge failed.
#>
param([Parameter(Mandatory, Position = 0)][int]$Pr, [switch]$DryRun)
. "$PSScriptRoot/pr-lib.ps1"
$ErrorActionPreference = 'Stop'
$d = Get-PrData $Pr
$v = Get-Verdict $d { Test-LocalConflict $Pr $d.Head }
Write-Verdict $v $d.Head
if (-not $v.Ready) { exit 1 }
if ($DryRun) { "dry-run: would run gh pr merge $Pr --merge"; exit 0 }
$out = gh pr merge $Pr --merge 2>&1
if ($LASTEXITCODE -ne 0) { "merge FAILED: $(Format-Text ($out -join ' ') 250)"; exit 1 }
$sha = $null
for ($i = 0; $i -lt 6 -and -not $sha; $i++) { $sha = gh pr view $Pr --json mergeCommit -q .mergeCommit.oid 2>$null; if (-not $sha) { Start-Sleep -Seconds 2 } }
"merged #$Pr as $(if ($sha) { $sha.Substring(0,7) } else { '(commit not reported yet)' })"
exit 0
