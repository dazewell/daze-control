<#
Requests the Copilot review on a PR. Idempotent: does nothing if a review is pending or already covers the head.
Budget: 2 Copilot reviews per PR (repo-workflow); a third needs -Force (it costs the user).
Usage: pwsh scripts/pr-request-review.ps1 <pr> [-DryRun] [-Force]
Exit: 0 requested or already there, 1 refused (budget) or failed.
#>
param([Parameter(Mandatory, Position = 0)][int]$Pr, [switch]$DryRun, [switch]$Force)
. "$PSScriptRoot/pr-lib.ps1"
$ErrorActionPreference = 'Stop'
$repo = Get-RepoSlug; $d = Get-PrData $Pr $repo
if ($d.State -ne 'OPEN') { "#$Pr is $($d.State)"; exit 1 }
if ($d.CopilotPending) { "#$Pr copilot review already requested, pending"; exit 0 }
$n = $d.CopilotReviews.Count
if ($n -gt 0 -and $d.CopilotReviews[-1].Commit -eq $d.Head) { "#$Pr copilot already reviewed head $($d.Head.Substring(0,7)) ($n review(s))"; exit 0 }
if ($n -ge 2 -and -not $Force) { "#$Pr has $n copilot reviews: budget is 2, not requesting (-Force to override)"; exit 1 }
if ($DryRun) { "#$Pr dry-run: would request copilot review (reviews so far: $n)"; exit 0 }
gh pr edit $Pr --add-reviewer '@copilot' 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
  gh api -X POST "repos/$repo/pulls/$Pr/requested_reviewers" -f 'reviewers[]=copilot-pull-request-reviewer[bot]' 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 0) { "#$Pr request failed (both gh pr edit and the REST fallback)"; exit 1 }
}
$d = Get-PrData $Pr $repo
if ($d.CopilotPending) { "#$Pr copilot review requested (review $($n + 1) of 2)"; exit 0 }
"#$Pr request sent but not visible in the timeline yet: check pr-status.ps1 in a minute"; exit 0
